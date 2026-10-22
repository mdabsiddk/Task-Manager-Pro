using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace TaskManagerPro
{
    /// <summary>
    /// Entry point. One exe, several roles:
    ///   (no arguments)   open the window (asks for administrator rights once if needed)
    ///   --ui             the window process (started by the line above or by the background engine)
    ///   --engine         the optional background engine (tray icon, applies limits to apps started later)
    ///   --autostart      used by the sign-in task: starts the engine if "Run in background" is on
    ///   --exit           closes Task Manager Pro: the window and the engine (limits stay in place)
    ///   --release-all    restores every limited app and removes all persistent limits (uninstaller)
    ///   --update-check   weekly scheduled task: installs a newer GitHub release if there is one
    ///   --register-updates  (installer) creates or removes the weekly update task per the settings
    ///   --restore-all    emergency: closes the window, restores every app and pauses all limits
    ///                    (Start menu: "Task Manager Pro - Restore all apps")
    /// This class must not touch WinForms types, so the engine never loads WinForms.
    /// </summary>
    internal static class Program
    {
        public const string AppName = "Task Manager Pro";

        [STAThread]
        private static int Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("Unhandled", e.ExceptionObject as Exception);
            try
            {
                if (HasArg(args, "--ui")) return RunUi();
                if (HasArg(args, "--engine")) return Engine.Run(HasArg(args, "--show"));
                if (HasArg(args, StartupManager.AutostartArg))
                    return new AppSettings().BackgroundEngine ? Engine.Run(false) : 0;
                if (HasArg(args, "--exit")) return CloseAll();
                if (HasArg(args, "--release-all")) return ReleaseAll();
                if (HasArg(args, "--restore-all")) return RestoreAll(HasArg(args, "--quiet"));
                if (HasArg(args, StartupManager.UpdateCheckArg)) return RunUpdateCheck();
                if (HasArg(args, "--register-updates")) return RegisterUpdates();
                return Launch();
            }
            catch (Exception ex)
            {
                Log.Error("Main", ex);
                return 1;
            }
        }

        /// <summary>Normal start (shortcut / Start menu): show the window.</summary>
        private static int Launch()
        {
            // Already running (engine or window)? Bring the window up; works without elevation.
            if (Ipc.Signal(Ipc.ShowEvent)) return 0;

            // Limits need administrator rights: relaunch elevated (one UAC prompt).
            if (!NativeMethods.IsElevated()) return Elevate("--ui");
            return RunUi();
        }

        private static int Elevate(string arguments)
        {
            var info = new NativeMethods.SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf(typeof(NativeMethods.SHELLEXECUTEINFO)),
                lpVerb = "runas",
                lpFile = Assembly.GetEntryAssembly().Location,
                lpParameters = arguments,
                nShow = 1
            };
            NativeMethods.ShellExecuteEx(ref info);
            return 0;
        }

        /// <summary>
        /// Emergency switch: if limits ever make apps unusable (a mistake, or something tampering with
        /// the settings), this puts every app back to normal in one step and pauses all limits.
        /// </summary>
        private static int RestoreAll(bool quiet)
        {
            if (!NativeMethods.IsElevated()) return Elevate("--restore-all" + (quiet ? " --quiet" : ""));

            // An open window would keep applying limits: close it first (it lets go without undoing).
            Ipc.Signal(Ipc.UiCloseEvent);
            WaitForMutex(@"Local\TaskManagerPro.Window", 10000);
            StopEngine();

            var limits = new LimitController();
            using (var sampler = new ProcessSampler())
            {
                SampleResult sample = sampler.Sample();
                List<ProcessGroup> groups = sample == null ? null : ProcessTree.Group(sample.Processes);
                limits.SetPaused(true, groups);
                limits.ReleaseEverything(groups);
            }
            NetworkQos.Flush(60000);
            if (quiet) return 0;
            MessageBox(IntPtr.Zero,
                "All apps are back to normal and every limit is paused.\n\n" +
                "Your limits are kept. To use them again, open Task Manager Pro and click \"Resume limits\".",
                AppName, 0x40 /* MB_ICONINFORMATION */);
            return 0;
        }

        /// <summary>Closes the window and the engine gracefully (installer / updates). Limits stay.</summary>
        private static int CloseAll()
        {
            Ipc.Signal(Ipc.UiCloseEvent);
            WaitForMutex(@"Local\TaskManagerPro.Window", 10000);
            return StopEngine();
        }

        private static void WaitForMutex(string name, int timeoutMs)
        {
            try
            {
                Mutex m;
                if (!Mutex.TryOpenExisting(name, out m)) return;
                using (m)
                {
                    try
                    {
                        if (m.WaitOne(timeoutMs)) m.ReleaseMutex();
                    }
                    catch (AbandonedMutexException) { }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Wait for " + name, ex);
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);

        [MethodImpl(MethodImplOptions.NoInlining)] // keeps WinForms out of Main's JIT
        private static int RunUi()
        {
            return UiApp.Run();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int RunUpdateCheck()
        {
            return Updater.RunScheduled();
        }

        private static int RegisterUpdates()
        {
            string exe = Assembly.GetEntryAssembly().Location;
            bool ok = new AppSettings().AutoCheckUpdates ? StartupManager.EnableUpdateCheck(exe) : StartupManager.DisableUpdateCheck();
            return ok ? 0 : 1;
        }

        private static int StopEngine()
        {
            Ipc.Signal(Ipc.ExitEvent);
            try
            {
                Mutex engine;
                if (Mutex.TryOpenExisting(Ipc.EngineMutex, out engine))
                {
                    using (engine)
                    {
                        try
                        {
                            if (engine.WaitOne(10000)) engine.ReleaseMutex();
                        }
                        catch (AbandonedMutexException) { }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Stop engine", ex);
            }
            return 0;
        }

        private static int ReleaseAll()
        {
            StopEngine();
            var limits = new LimitController();
            using (var sampler = new ProcessSampler())
            {
                SampleResult sample = sampler.Sample();
                List<ProcessGroup> groups = sample == null ? null : ProcessTree.Group(sample.Processes);
                limits.ReleaseEverything(groups);
            }
            NetworkQos.Flush(60000); // the QoS cleanup runs in the background: wait for it
            return 0;
        }

        private static bool HasArg(string[] args, string name)
        {
            foreach (string a in args)
            {
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
