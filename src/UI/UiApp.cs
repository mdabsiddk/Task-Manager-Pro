using System;
using System.Threading;
using System.Windows.Forms;

namespace TaskManagerPro
{
    /// <summary>The window process ("--ui"). Closing it ends this process completely.</summary>
    internal static class UiApp
    {
        private const string WindowMutex = @"Local\TaskManagerPro.Window";

        public static int Run()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, WindowMutex, out createdNew))
            {
                if (!createdNew)
                {
                    Ipc.Signal(Ipc.UiActivateEvent); // already open: bring it to the front
                    return 0;
                }

                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => Log.Error("UI thread", e.Exception);
                NativeMethods.EnableDebugPrivilege();
                SecureDataFolder();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                var settings = new AppSettings();
                Theme.Initialize(settings.Theme);
                using (var form = new MainForm(settings))
                {
                    Application.Run(form);
                }
                NetworkQos.Flush(20000); // finish a queued network-limit change before exiting
                mutex.ReleaseMutex();
            }
            return 0;
        }

        /// <summary>Lock the data folder right away, not only when something is first written to it.</summary>
        internal static void SecureDataFolder()
        {
            try
            {
                AppPaths.EnsureDataDir();
            }
            catch (Exception ex)
            {
                Log.Error("Secure data folder", ex);
            }
        }
    }
}
