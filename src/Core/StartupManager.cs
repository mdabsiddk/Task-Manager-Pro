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
        public const string UpdateTaskName = "TaskManagerPro Update";
        public const string UpdateCheckArg = "--update-check";
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
            string trigger = "<LogonTrigger><Enabled>true</Enabled><UserId>" + CurrentUser() + "</UserId><Delay>PT10S</Delay></LogonTrigger>";
            return Register(TaskName, BuildTaskXml(exePath, AutostartArg, "Starts Task Manager Pro (resource limits) at sign-in.",
                trigger, false, false, "PT0S"));
        }

        // ---------------- Weekly update check ----------------

        public static bool IsUpdateCheckEnabled()
        {
            return File.Exists(TaskFile(UpdateTaskName));
        }

        /// <summary>
        /// Weekly task that runs "--update-check": it only starts when a network is available, and if the
        /// PC was off at the scheduled time it runs at the next opportunity (StartWhenAvailable).
        /// </summary>
        public static bool EnableUpdateCheck(string exePath)
        {
            DateTime start = DateTime.Today.AddDays(1).AddHours(12);
            string trigger = "<CalendarTrigger><StartBoundary>" + start.ToString("yyyy-MM-ddTHH:mm:ss") + "</StartBoundary>"
                           + "<Enabled>true</Enabled><RandomDelay>PT2H</RandomDelay>"
                           + "<ScheduleByWeek><DaysOfWeek><" + start.DayOfWeek + " /></DaysOfWeek><WeeksInterval>1</WeeksInterval></ScheduleByWeek>"
                           + "</CalendarTrigger>";
            return Register(UpdateTaskName, BuildTaskXml(exePath, UpdateCheckArg,
                "Checks GitHub once a week for a new Task Manager Pro version and installs it.", trigger, true, true, "PT1H"));
        }

        public static bool DisableUpdateCheck()
        {
            return !IsUpdateCheckEnabled() || Schtasks("/Delete /TN \"" + UpdateTaskName + "\" /F") == 0;
        }

        private static bool Register(string name, string xml)
        {
            string xmlPath = Path.Combine(Path.GetTempPath(), "TaskManagerPro_task_" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(xmlPath, xml, Encoding.Unicode);
                return Schtasks(string.Format("/Create /TN \"{0}\" /XML \"{1}\" /F", name, xmlPath)) == 0;
            }
            catch (Exception ex)
            {
                Log.Error("Register task " + name, ex);
                return false;
            }
            finally
            {
                try { File.Delete(xmlPath); } catch { }
            }
        }

        private static string CurrentUser()
        {
            return SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
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

        private static string BuildTaskXml(string exePath, string arguments, string description, string trigger,
            bool startWhenAvailable, bool needsNetwork, string timeLimit)
        {
            string user = CurrentUser();
            string exe = SecurityElement.Escape(exePath);
            string dir = SecurityElement.Escape(Path.GetDirectoryName(exePath));

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
            sb.AppendLine("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
            sb.AppendLine("  <RegistrationInfo><Description>" + SecurityElement.Escape(description) + "</Description></RegistrationInfo>");
            sb.AppendLine("  <Triggers>");
            sb.AppendLine("    " + trigger);
            sb.AppendLine("  </Triggers>");
            sb.AppendLine("  <Principals>");
            sb.AppendLine("    <Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>");
            sb.AppendLine("  </Principals>");
            sb.AppendLine("  <Settings>");
            sb.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
            sb.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
            sb.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
            sb.AppendLine("    <AllowHardTerminate>true</AllowHardTerminate>");
            sb.AppendLine("    <StartWhenAvailable>" + (startWhenAvailable ? "true" : "false") + "</StartWhenAvailable>");
            sb.AppendLine("    <RunOnlyIfNetworkAvailable>" + (needsNetwork ? "true" : "false") + "</RunOnlyIfNetworkAvailable>");
            sb.AppendLine("    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>");
            sb.AppendLine("    <AllowStartOnDemand>true</AllowStartOnDemand>");
            sb.AppendLine("    <Enabled>true</Enabled>");
            sb.AppendLine("    <Hidden>false</Hidden>");
            sb.AppendLine("    <RunOnlyIfIdle>false</RunOnlyIfIdle>");
            sb.AppendLine("    <WakeToRun>false</WakeToRun>");
            sb.AppendLine("    <ExecutionTimeLimit>" + timeLimit + "</ExecutionTimeLimit>");
            sb.AppendLine("    <Priority>5</Priority>");
            sb.AppendLine("  </Settings>");
            sb.AppendLine("  <Actions Context=\"Author\">");
            sb.AppendLine("    <Exec><Command>\"" + exe + "\"</Command><Arguments>" + arguments + "</Arguments><WorkingDirectory>" + dir + "</WorkingDirectory></Exec>");
            sb.AppendLine("  </Actions>");
            sb.AppendLine("</Task>");
            return sb.ToString();
        }
    }
}
