using System;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace TaskManagerPro
{
    /// <summary>
    /// "Start with Windows" through a Task Scheduler logon task with highest privileges, so the
    /// administrator engine starts at sign-in without a UAC prompt. The task XML also removes Task
    /// Scheduler defaults that would break "run all year": the 72-hour execution limit and
    /// "don't start / stop on battery". No System.Diagnostics: the engine stays lightweight.
    /// </summary>
    internal static class StartupManager
    {
        public const string TaskName = "TaskManagerPro";
        public const string AutostartArg = "--autostart";
        private const string LegacyTaskName = "SmartTaskManager";
        private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        private static string TaskFile(string name)
        {
            return Path.Combine(Path.Combine(Environment.SystemDirectory, "Tasks"), name);
        }

        public static bool IsEnabled()
        {
            return File.Exists(TaskFile(TaskName));
        }

        public static bool Enable(string exePath)
        {
            string xmlPath = Path.Combine(Path.GetTempPath(), "TaskManagerPro_task_" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(xmlPath, BuildTaskXml(exePath), Encoding.Unicode);
                return Schtasks(string.Format("/Create /TN \"{0}\" /XML \"{1}\" /F", TaskName, xmlPath)) == 0;
            }
            catch (Exception ex)
            {
                Log.Error("Enable autostart", ex);
                return false;
            }
            finally
            {
                try { File.Delete(xmlPath); } catch { }
            }
        }

        public static bool Disable()
        {
            return !IsEnabled() || Schtasks("/Delete /TN \"" + TaskName + "\" /F") == 0;
        }

        /// <summary>
        /// Run once when the engine starts: converts Smart Task Manager's autostart (Run key or task)
        /// into this app's task, and re-points the task if the program was moved.
        /// </summary>
        public static void Maintain(string exePath)
        {
            try
            {
                bool hadLegacy = RemoveLegacyRunEntry();
                if (File.Exists(TaskFile(LegacyTaskName)))
                {
                    Schtasks("/Delete /TN \"" + LegacyTaskName + "\" /F");
                    hadLegacy = true;
                }

                if (!IsEnabled())
                {
                    if (hadLegacy) Enable(exePath);
                    return;
                }

                string xml = File.ReadAllText(TaskFile(TaskName));
                bool pointsHere = xml.IndexOf(exePath, StringComparison.OrdinalIgnoreCase) >= 0
                               || xml.IndexOf(SecurityElement.Escape(exePath), StringComparison.OrdinalIgnoreCase) >= 0;
                if (!pointsHere) Enable(exePath);
            }
            catch (Exception ex)
            {
                Log.Error("Maintain autostart", ex);
            }
        }

        private static bool RemoveLegacyRunEntry()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (key == null || key.GetValue(LegacyTaskName) == null) return false;
                key.DeleteValue(LegacyTaskName, false);
                return true;
            }
        }

        private static int Schtasks(string arguments)
        {
            IntPtr handle;
            int pid;
            string exe = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
            return NativeMethods.RunProcess(exe, arguments, true, 15000, out handle, out pid);
        }

        private static string BuildTaskXml(string exePath)
        {
            string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
            string exe = SecurityElement.Escape(exePath);
            string dir = SecurityElement.Escape(Path.GetDirectoryName(exePath));

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
            sb.AppendLine("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
            sb.AppendLine("  <RegistrationInfo><Description>Starts Task Manager Pro (resource limits) at sign-in.</Description></RegistrationInfo>");
            sb.AppendLine("  <Triggers>");
            sb.AppendLine("    <LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId><Delay>PT10S</Delay></LogonTrigger>");
            sb.AppendLine("  </Triggers>");
            sb.AppendLine("  <Principals>");
            sb.AppendLine("    <Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>");
            sb.AppendLine("  </Principals>");
            sb.AppendLine("  <Settings>");
            sb.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
            sb.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
            sb.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
            sb.AppendLine("    <AllowHardTerminate>true</AllowHardTerminate>");
            sb.AppendLine("    <StartWhenAvailable>false</StartWhenAvailable>");
            sb.AppendLine("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>");
            sb.AppendLine("    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>");
            sb.AppendLine("    <AllowStartOnDemand>true</AllowStartOnDemand>");
            sb.AppendLine("    <Enabled>true</Enabled>");
            sb.AppendLine("    <Hidden>false</Hidden>");
            sb.AppendLine("    <RunOnlyIfIdle>false</RunOnlyIfIdle>");
            sb.AppendLine("    <WakeToRun>false</WakeToRun>");
            sb.AppendLine("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>");
            sb.AppendLine("    <Priority>5</Priority>");
            sb.AppendLine("  </Settings>");
            sb.AppendLine("  <Actions Context=\"Author\">");
            sb.AppendLine("    <Exec><Command>\"" + exe + "\"</Command><Arguments>" + AutostartArg + "</Arguments><WorkingDirectory>" + dir + "</WorkingDirectory></Exec>");
            sb.AppendLine("  </Actions>");
            sb.AppendLine("</Task>");
            return sb.ToString();
        }
    }
}
