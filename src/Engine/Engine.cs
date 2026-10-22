using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace TaskManagerPro
{
    /// <summary>
    /// The optional background engine ("Run in background" setting). It keeps the exact CPU and memory
    /// caps on apps that are started after the window was closed. It is event-driven: Windows tells it
    /// (via ETW) when a process starts, it applies the limits in a few milliseconds and goes back to
    /// sleep. No polling, no extra work when no limits are set. It uses only mscorlib + raw Win32
    /// (no WinForms, no System.dll) and opens the window as a separate "--ui" process on demand.
    /// </summary>
    internal sealed class Engine
    {
        private const uint WM_APP_TRAY = 0x8001;  // WM_APP + 1
        private const int TimerApply = 1, TimerTrim = 2;
        private const int CmdOpen = 1, CmdPause = 2, CmdExit = 3;
        private const int ApplyDelayMs = 150;      // let a burst of process starts settle
        private const int FallbackPollMs = 3000;   // only if ETW is unavailable
        private const int TrimEveryMs = 5 * 60 * 1000;

        private readonly string exePath = Assembly.GetEntryAssembly().Location;
        private readonly LimitController limits = new LimitController();
        private readonly ProcessSampler sampler = new ProcessSampler();
        private StatusChannel status;
        private EtwSession processWatcher;
        private bool polling;

        private IntPtr showEvent, exitEvent, configEvent, pauseEvent, resumeEvent, uiActivateEvent, uiCloseEvent, processStarted;
        private IntPtr uiProcess;
        private int uiPid;
        private IntPtr[] waitHandles;

        private Native.WndProc wndProc; // kept in a field so the GC never collects the callback
        private IntPtr hwnd, hIcon;
        private uint taskbarCreatedMsg;
        private bool iconAdded, running, applyPending;
        private Dictionary<string, GovernorState> lastStates = new Dictionary<string, GovernorState>();

        /// <summary>Runs the engine until Exit. Returns immediately if another engine is running.</summary>
        public static int Run(bool showUi)
        {
            bool createdNew;
            using (var mutex = new Mutex(true, Ipc.EngineMutex, out createdNew))
            {
                if (!createdNew)
                {
                    if (showUi) Ipc.Signal(Ipc.ShowEvent);
                    return 0;
                }

                var engine = new Engine();
                try
                {
                    engine.Start(showUi);
                    engine.Loop();
                }
                catch (Exception ex)
                {
                    Log.Error("Engine", ex);
                }
                finally
                {
                    engine.Stop();
                }
                mutex.ReleaseMutex();
            }
            return 0;
        }

        /// <summary>True if the background engine is running in this session.</summary>
        public static bool IsRunning()
        {
            try
            {
                Mutex m;
                if (!Mutex.TryOpenExisting(Ipc.EngineMutex, out m)) return false;
                m.Dispose();
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true; // exists, owned by an elevated engine
            }
        }

        // =====================================================================
        // Lifecycle
        // =====================================================================

        private void Start(bool showUi)
        {
            NativeMethods.EnableDebugPrivilege();
            try
            {
                AppPaths.EnsureDataDir(); // lock the data folder right away (admin-only)
            }
            catch (Exception ex)
            {
                Log.Error("Secure data folder", ex);
            }
            StartupManager.Maintain(exePath);

            showEvent = Ipc.CreateEvent(Ipc.ShowEvent, true);
            exitEvent = Ipc.CreateEvent(Ipc.ExitEvent, false);
            configEvent = Ipc.CreateEvent(Ipc.ConfigEvent, false);
            pauseEvent = Ipc.CreateEvent(Ipc.PauseEvent, false);
            resumeEvent = Ipc.CreateEvent(Ipc.ResumeEvent, false);
            uiActivateEvent = Ipc.CreateEvent(Ipc.UiActivateEvent, false);
            uiCloseEvent = Ipc.CreateEvent(Ipc.UiCloseEvent, false);
            processStarted = NativeMethods.CreateEvent(IntPtr.Zero, false, false, null);
            status = StatusChannel.Create();
            RebuildWaitHandles();

            CreateMessageWindow();
            AddTrayIcon();
            Apply();
            UpdateWatcher();
            Native.SetTimer(hwnd, (IntPtr)TimerTrim, 5000, IntPtr.Zero); // first trim once startup is done

            // Undo any outside change to the registry / QoS limits, then warn about a tampered file.
            limits.SyncPersistent();
            if (limits.Rules.NeedsReview)
            {
                ShowBalloon("Limits need your review",
                    "The limits file was changed outside Task Manager Pro, so its limits are not applied. Open Task Manager Pro to review them.");
            }

            if (showUi) ShowUi();
        }

        private void Loop()
        {
            running = true;
            while (running)
            {
                uint r = Native.MsgWaitForMultipleObjects((uint)waitHandles.Length, waitHandles, false, NativeMethods.INFINITE, Native.QS_ALLINPUT);
                if (r == waitHandles.Length)
                {
                    PumpMessages();
                    continue;
                }
                if (r >= waitHandles.Length) // WAIT_FAILED: avoid a busy loop
                {
                    Thread.Sleep(100);
                    continue;
                }

                IntPtr signaled = waitHandles[r];
                if (signaled == exitEvent) running = false;
                else if (signaled == showEvent) ShowUi();
                else if (signaled == processStarted) ScheduleApply();
                else if (signaled == configEvent) ReloadConfig();
                else if (signaled == pauseEvent) SetPaused(true);
                else if (signaled == resumeEvent) SetPaused(false);
                else if (signaled == uiProcess) OnUiExited();
            }
        }

        private void Stop()
        {
            if (hwnd != IntPtr.Zero)
            {
                Native.KillTimer(hwnd, (IntPtr)TimerApply);
                Native.KillTimer(hwnd, (IntPtr)TimerTrim);
            }
            if (processWatcher != null) processWatcher.Dispose();
            // Limits stay on the running apps: only Pause / removing a limit / uninstall undo them.
            limits.Detach();
            if (uiCloseEvent != IntPtr.Zero && uiProcess != IntPtr.Zero) NativeMethods.SetEvent(uiCloseEvent);
            RemoveTrayIcon();
            if (hwnd != IntPtr.Zero) Native.DestroyWindow(hwnd);
            if (status != null) status.Dispose();
            sampler.Dispose();
            foreach (IntPtr h in new[] { showEvent, exitEvent, configEvent, pauseEvent, resumeEvent, uiActivateEvent, uiCloseEvent, processStarted, uiProcess })
            {
                if (h != IntPtr.Zero) NativeMethods.CloseHandle(h);
            }
        }

        private void RebuildWaitHandles()
        {
            var list = new List<IntPtr>();
            // Order matters: Exit wins if several are signaled together.
            foreach (IntPtr h in new[] { exitEvent, showEvent, configEvent, pauseEvent, resumeEvent, processStarted, uiProcess })
            {
                if (h != IntPtr.Zero) list.Add(h);
            }
            waitHandles = list.ToArray();
        }

        // =====================================================================
        // Limits
        // =====================================================================

        /// <summary>Watches process starts only while there is something to enforce.</summary>
        private void UpdateWatcher()
        {
            bool needed = !limits.Paused && limits.Rules.Count > 0;
            if (needed && processWatcher == null && !polling)
            {
                processWatcher = EtwSession.Start("TaskManagerPro-ProcessStart", EtwSession.KernelProcessProvider,
                    0x10 /* WINEVENT_KEYWORD_PROCESS */, 4, 8, 2, 200, OnEtwEvent);
                if (processWatcher == null)
                {
                    polling = true;
                    Native.SetTimer(hwnd, (IntPtr)TimerApply, FallbackPollMs, IntPtr.Zero);
                }
            }
            else if (!needed)
            {
                if (processWatcher != null) processWatcher.Dispose();
                processWatcher = null;
                if (polling) Native.KillTimer(hwnd, (IntPtr)TimerApply);
                polling = false;
            }
            UpdateTrayTip();
        }

        /// <summary>ETW thread: just wake the engine thread.</summary>
        private void OnEtwEvent(ushort eventId, IntPtr data, int length)
        {
            if (eventId == 1 /* ProcessStart */) NativeMethods.SetEvent(processStarted);
        }

        private void ScheduleApply()
        {
            if (polling || applyPending) return;
            applyPending = true;
            Native.SetTimer(hwnd, (IntPtr)TimerApply, ApplyDelayMs, IntPtr.Zero);
        }

        private List<ProcessGroup> CurrentGroups()
        {
            SampleResult sample = sampler.Sample();
            return sample == null ? new List<ProcessGroup>() : ProcessTree.Group(sample.Processes);
        }

        private void Apply()
        {
            if (!polling)
            {
                Native.KillTimer(hwnd, (IntPtr)TimerApply);
                applyPending = false;
            }
            if (limits.Paused || limits.Rules.Count == 0) return;
            try
            {
                lastStates = limits.Enforce(CurrentGroups(), false);
                PublishStatus();
            }
            catch (Exception ex)
            {
                Log.Error("Apply limits", ex);
            }
        }

        private void ReloadConfig()
        {
            IDictionary<string, ProcessRule> before = limits.Rules.Snapshot();
            limits.Reload();
            IDictionary<string, ProcessRule> after = limits.Rules.Snapshot();

            // Restore every app whose limits changed; Apply then sets the new limits.
            List<ProcessGroup> groups = null;
            foreach (string name in new List<string>(before.Keys))
            {
                ProcessRule now;
                if (after.TryGetValue(name, out now) && now.SameLimits(before[name])) continue;
                if (groups == null) groups = CurrentGroups();
                limits.Governor.ReleaseGroup(name, groups.Find(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)));
            }
            lastStates = new Dictionary<string, GovernorState>();
            Apply();
            UpdateWatcher();
            PublishStatus();
        }

        private void SetPaused(bool paused)
        {
            limits.SetPaused(paused, CurrentGroups());
            lastStates = new Dictionary<string, GovernorState>();
            Apply();
            UpdateWatcher();
            PublishStatus();
        }

        private void PublishStatus()
        {
            if (status != null && uiProcess != IntPtr.Zero) status.Write(limits.Paused, lastStates);
        }

        // =====================================================================
        // Window (UI process)
        // =====================================================================

        private void ShowUi()
        {
            if (uiProcess != IntPtr.Zero && NativeMethods.WaitForSingleObject(uiProcess, 0) == NativeMethods.WAIT_TIMEOUT)
            {
                NativeMethods.AllowSetForegroundWindow(uiPid);
                NativeMethods.SetEvent(uiActivateEvent);
                return;
            }

            IntPtr h;
            int pid;
            if (NativeMethods.RunProcess(exePath, "--ui", false, 0, out h, out pid) != 0)
            {
                Log.Write("Could not start the window process.");
                return;
            }
            uiProcess = h;
            uiPid = pid;
            NativeMethods.AllowSetForegroundWindow(pid);
            RebuildWaitHandles();
            PublishStatus();
        }

        private void OnUiExited()
        {
            NativeMethods.CloseHandle(uiProcess);
            uiProcess = IntPtr.Zero;
            uiPid = 0;
            RebuildWaitHandles();

            if (!limits.TrayHintShown)
            {
                limits.TrayHintShown = true;
                ShowBalloon("Task Manager Pro is running in the background",
                    "Your limits are also applied to apps you open later. Click the icon to open Task Manager Pro.");
            }
            NativeMethods.TrimWorkingSet();
        }

        // =====================================================================
        // Message window + tray icon
        // =====================================================================

        private void CreateMessageWindow()
        {
            wndProc = WindowProc;
            IntPtr instance = Native.GetModuleHandle(null);
            var wc = new Native.WNDCLASSEX
            {
                cbSize = Marshal.SizeOf(typeof(Native.WNDCLASSEX)),
                lpfnWndProc = wndProc,
                hInstance = instance,
                lpszClassName = "TaskManagerPro.Engine"
            };
            Native.RegisterClassEx(ref wc);
            // A hidden top-level window (not message-only) so it receives the "TaskbarCreated" broadcast.
            hwnd = Native.CreateWindowEx(0, wc.lpszClassName, "Task Manager Pro Engine", 0, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");
        }

        private void PumpMessages()
        {
            Native.MSG msg;
            while (Native.PeekMessage(out msg, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
            {
                if (msg.message == 0x0012 /* WM_QUIT */)
                {
                    running = false;
                    return;
                }
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
        }

        private IntPtr WindowProc(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                switch (msg)
                {
                    case WM_APP_TRAY:
                        switch (lParam.ToInt32() & 0xFFFF)
                        {
                            case 0x0202: // WM_LBUTTONUP
                            case 0x0405: // NIN_BALLOONUSERCLICK
                                ShowUi();
                                break;
                            case 0x0205: // WM_RBUTTONUP
                                ShowTrayMenu();
                                break;
                        }
                        return IntPtr.Zero;

                    case 0x0113: // WM_TIMER
                        if (wParam.ToInt32() == TimerApply)
                        {
                            Apply();
                        }
                        else if (wParam.ToInt32() == TimerTrim)
                        {
                            NativeMethods.TrimWorkingSet();
                            Native.SetTimer(hwnd, (IntPtr)TimerTrim, TrimEveryMs, IntPtr.Zero);
                        }
                        return IntPtr.Zero;

                    case 0x0011: // WM_QUERYENDSESSION
                        return (IntPtr)1;

                    case 0x0016: // WM_ENDSESSION
                        if (wParam != IntPtr.Zero) running = false;
                        return IntPtr.Zero;
                }

                if (msg == taskbarCreatedMsg && taskbarCreatedMsg != 0)
                {
                    iconAdded = false; // Explorer restarted: the icon must be added again
                    AddTrayIcon();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Engine window", ex);
            }
            return Native.DefWindowProc(h, msg, wParam, lParam);
        }

        private void ShowTrayMenu()
        {
            IntPtr menu = Native.CreatePopupMenu();
            try
            {
                Native.AppendMenu(menu, 0, (IntPtr)CmdOpen, "Open Task Manager Pro");
                Native.AppendMenu(menu, limits.Paused ? 0x8u : 0u, (IntPtr)CmdPause, "Pause all limits");
                Native.AppendMenu(menu, 0x800 /* MF_SEPARATOR */, IntPtr.Zero, null);
                Native.AppendMenu(menu, 0, (IntPtr)CmdExit, "Exit");
                Native.SetMenuDefaultItem(menu, CmdOpen, false);

                Native.POINT pt;
                Native.GetCursorPos(out pt);
                Native.SetForegroundWindow(hwnd); // required so the menu closes when clicking elsewhere
                int cmd = Native.TrackPopupMenu(menu, 0x0100 | 0x0002 | 0x0080 /* RETURNCMD | RIGHTBUTTON | NONOTIFY */,
                    pt.X, pt.Y, 0, hwnd, IntPtr.Zero);
                Native.PostMessage(hwnd, 0, IntPtr.Zero, IntPtr.Zero);

                switch (cmd)
                {
                    case CmdOpen: ShowUi(); break;
                    case CmdPause: SetPaused(!limits.Paused); break;
                    case CmdExit: running = false; break;
                }
            }
            finally
            {
                Native.DestroyMenu(menu);
            }
        }

        private Native.NOTIFYICONDATA NewIconData(int flags)
        {
            return new Native.NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf(typeof(Native.NOTIFYICONDATA)),
                hWnd = hwnd,
                uID = 1,
                uFlags = flags,
                uCallbackMessage = (int)WM_APP_TRAY,
                hIcon = hIcon,
                szTip = TrayTip(),
                szInfo = "",
                szInfoTitle = ""
            };
        }

        private void AddTrayIcon()
        {
            if (iconAdded || hwnd == IntPtr.Zero) return;
            if (hIcon == IntPtr.Zero)
            {
                var small = new IntPtr[1];
                if (Native.ExtractIconEx(exePath, 0, null, small, 1) > 0) hIcon = small[0];
                if (hIcon == IntPtr.Zero) hIcon = Native.LoadIcon(IntPtr.Zero, (IntPtr)32512 /* IDI_APPLICATION */);
            }
            var data = NewIconData(Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP);
            iconAdded = Native.Shell_NotifyIcon(Native.NIM_ADD, ref data);
        }

        private void RemoveTrayIcon()
        {
            if (!iconAdded) return;
            var data = NewIconData(0);
            Native.Shell_NotifyIcon(Native.NIM_DELETE, ref data);
            iconAdded = false;
            if (hIcon != IntPtr.Zero) Native.DestroyIcon(hIcon);
            hIcon = IntPtr.Zero;
        }

        private string TrayTip()
        {
            int count = limits.Rules.Count;
            string detail = limits.Rules.NeedsReview ? "Limits need review"
                : limits.Paused ? "Limits paused"
                : count == 0 ? "No limits set"
                : count == 1 ? "1 app limited" : count + " apps limited";
            return "Task Manager Pro " + AppVersion.Text + "\n" + detail;
        }

        private void UpdateTrayTip()
        {
            if (!iconAdded) return;
            var data = NewIconData(Native.NIF_TIP);
            Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data);
        }

        private void ShowBalloon(string title, string text)
        {
            if (!iconAdded) return;
            var data = NewIconData(Native.NIF_INFO);
            data.szInfoTitle = title;
            data.szInfo = text;
            data.dwInfoFlags = 1; // NIIF_INFO
            Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data);
        }

        /// <summary>user32 / shell32 declarations needed only by the engine.</summary>
        private static class Native
        {
            public const uint QS_ALLINPUT = 0x04FF;
            public const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
            public const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10;

            public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct WNDCLASSEX
            {
                public int cbSize;
                public uint style;
                public WndProc lpfnWndProc;
                public int cbClsExtra, cbWndExtra;
                public IntPtr hInstance, hIcon, hCursor, hbrBackground;
                public string lpszMenuName, lpszClassName;
                public IntPtr hIconSm;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct POINT
            {
                public int X, Y;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct MSG
            {
                public IntPtr hwnd;
                public uint message;
                public IntPtr wParam, lParam;
                public uint time;
                public POINT pt;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct NOTIFYICONDATA
            {
                public int cbSize;
                public IntPtr hWnd;
                public int uID, uFlags, uCallbackMessage;
                public IntPtr hIcon;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
                public int dwState, dwStateMask;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
                public int uTimeoutOrVersion;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
                public int dwInfoFlags;
                public Guid guidItem;
                public IntPtr hBalloonIcon;
            }

            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
                int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

            [DllImport("user32.dll")]
            public static extern bool DestroyWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern uint RegisterWindowMessage(string name);

            [DllImport("user32.dll")]
            public static extern uint MsgWaitForMultipleObjects(uint count, IntPtr[] handles, bool waitAll, uint milliseconds, uint wakeMask);

            [DllImport("user32.dll")]
            public static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax, uint remove);

            [DllImport("user32.dll")]
            public static extern bool TranslateMessage(ref MSG msg);

            [DllImport("user32.dll")]
            public static extern IntPtr DispatchMessage(ref MSG msg);

            [DllImport("user32.dll")]
            public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll")]
            public static extern IntPtr SetTimer(IntPtr hWnd, IntPtr id, uint elapse, IntPtr func);

            [DllImport("user32.dll")]
            public static extern bool KillTimer(IntPtr hWnd, IntPtr id);

            [DllImport("user32.dll")]
            public static extern IntPtr CreatePopupMenu();

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr id, string text);

            [DllImport("user32.dll")]
            public static extern bool SetMenuDefaultItem(IntPtr menu, int item, bool byPosition);

            [DllImport("user32.dll")]
            public static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);

            [DllImport("user32.dll")]
            public static extern bool DestroyMenu(IntPtr menu);

            [DllImport("user32.dll")]
            public static extern bool GetCursorPos(out POINT pt);

            [DllImport("user32.dll")]
            public static extern bool SetForegroundWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

            [DllImport("user32.dll")]
            public static extern bool DestroyIcon(IntPtr icon);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            public static extern IntPtr GetModuleHandle(string name);

            [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
            public static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);

            [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
            public static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);
        }
    }
}
