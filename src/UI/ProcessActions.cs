using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TaskManagerPro
{
    /// <summary>End task, Open file location, Properties and Run new task.</summary>
    internal static class ProcessActions
    {
        /// <summary>
        /// Ends every process in the list. Processes that already exited count as ended; only
        /// "access denied" counts as failed.
        /// </summary>
        public static int Terminate(IList<ProcInfo> processes, out int failed)
        {
            int ended = 0;
            failed = 0;
            int self = NativeMethods.GetCurrentProcessId();

            foreach (ProcInfo p in processes)
            {
                if (p.Pid == self || ProtectedProcesses.IsCritical(p.Name)) continue;
                IntPtr h = NativeMethods.OpenProcessVerified(p.Pid, p.CreateTime, NativeMethods.PROCESS_TERMINATE);
                if (h == IntPtr.Zero)
                {
                    if (Marshal.GetLastWin32Error() == NativeMethods.ERROR_ACCESS_DENIED) failed++;
                    continue;
                }
                try
                {
                    if (NativeMethods.TerminateProcess(h, 1)) ended++;
                    else failed++;
                }
                finally
                {
                    NativeMethods.CloseHandle(h);
                }
            }
            return ended;
        }

        public static void OpenFileLocation(string path)
        {
            Process.Start("explorer.exe", "/select,\"" + path + "\"");
        }

        public static void ShowProperties(string path)
        {
            var info = new NativeMethods.SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf(typeof(NativeMethods.SHELLEXECUTEINFO)),
                fMask = 0x0000000C, // SEE_MASK_INVOKEIDLIST
                lpVerb = "properties",
                lpFile = path,
                nShow = 1
            };
            NativeMethods.ShellExecuteEx(ref info);
        }

        /// <summary>The standard Windows "Run" dialog (Task Manager's "Run new task").</summary>
        public static void RunNewTask(IntPtr owner)
        {
            RunFileDlg(owner, IntPtr.Zero, null, "Create new task",
                "Type the name of a program, folder, document, or Internet resource, and Windows will open it for you. " +
                "It will be created with administrative privileges.", 0);
        }

        [DllImport("shell32.dll", EntryPoint = "#61", CharSet = CharSet.Unicode)]
        private static extern int RunFileDlg(IntPtr owner, IntPtr icon, string directory, string title, string prompt, uint flags);
    }
}
