using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace TaskManagerPro
{
    /// <summary>
    /// Safety lists. Critical processes can never be limited or ended (doing so crashes or logs off
    /// Windows). Core components can never be limited; End task is allowed after a warning.
    /// </summary>
    internal static class ProtectedProcesses
    {
        private static readonly Dictionary<string, bool> Critical = ToSet(
            "Idle", "System", "Secure System", "Registry", "Memory Compression", "smss", "csrss", "wininit",
            "services", "lsass", "LsaIso", "winlogon", "svchost");

        private static readonly Dictionary<string, bool> CoreComponents = ToSet(
            "explorer", "dwm", "fontdrvhost", "sihost", "ctfmon", "conhost", "audiodg", "spoolsv", "taskhostw",
            "dllhost", "RuntimeBroker", "StartMenuExperienceHost", "ShellExperienceHost", "SearchHost", "SearchIndexer",
            "TextInputHost", "WUDFHost", "dasHost", "smartscreen", "MsMpEng", "NisSrv", "SecurityHealthService",
            "SecurityHealthSystray", "SgrmBroker", "Taskmgr", "LogonUI", "userinit", "WmiPrvSE", "ApplicationFrameHost",
            "SystemSettings", "LockApp", "Widgets", "WidgetService", "PhoneExperienceHost", "CompPkgSrv", "AggregatorHost");

        private static readonly string SelfName = GetSelfName();

        public static bool IsSelf(string name)
        {
            return string.Equals(name, SelfName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Ending this process would crash Windows or end the session.</summary>
        public static bool IsCritical(string name)
        {
            return name != null && Critical.ContainsKey(name);
        }

        /// <summary>Part of Windows itself (shown under "Windows processes", never limited).</summary>
        public static bool IsWindowsComponent(string name)
        {
            return name != null && (Critical.ContainsKey(name) || CoreComponents.ContainsKey(name));
        }

        /// <summary>Limits are never applied to these processes.</summary>
        public static bool CannotLimit(string name)
        {
            return IsWindowsComponent(name) || IsSelf(name);
        }

        private static string GetSelfName()
        {
            try
            {
                Assembly entry = Assembly.GetEntryAssembly();
                return entry != null ? Path.GetFileNameWithoutExtension(entry.Location) : "TaskManagerPro";
            }
            catch
            {
                return "TaskManagerPro";
            }
        }

        internal static Dictionary<string, bool> ToSet(params string[] names)
        {
            var set = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (string n in names) set[n] = true;
            return set;
        }
    }
}
