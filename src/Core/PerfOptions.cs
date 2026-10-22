using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace TaskManagerPro
{
    /// <summary>
    /// Persistent limits for apps started later, with no background process: Windows' own
    /// "Image File Execution Options\&lt;exe&gt;\PerfOptions" registry values. The kernel reads them
    /// whenever the program starts and gives it a lower CPU priority, I/O priority and memory page
    /// priority. Only values this app wrote (marked with "TaskManagerPro"=1) are ever removed.
    /// </summary>
    internal static class PerfOptions
    {
        private const string IfeoPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
        private const string Marker = "TaskManagerPro";
        private const int CpuBelowNormal = 5;   // CpuPriorityClass: 1 Idle, 2 Normal, 5 BelowNormal
        private const int IoLow = 1;            // IoPriority: 0 Very low, 1 Low, 2 Normal
        private const int PageLow = 2;          // PagePriority: 1 (lowest) .. 5 (normal)

        /// <summary>Makes the registry match the given rules exactly (empty = remove everything we wrote).</summary>
        public static void Sync(IDictionary<string, ProcessRule> rules)
        {
            try
            {
                using (RegistryKey ifeo = OpenIfeo(true))
                {
                    if (ifeo == null) return;
                    foreach (string exe in ifeo.GetSubKeyNames())
                    {
                        string name = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe.Substring(0, exe.Length - 4) : exe;
                        if (IsManaged(ifeo, exe) && (rules == null || !rules.ContainsKey(name))) RemoveValues(ifeo, exe);
                    }
                    if (rules == null) return;
                    foreach (ProcessRule rule in rules.Values) Write(ifeo, rule);
                }
            }
            catch (Exception ex)
            {
                Log.Error("PerfOptions", ex);
            }
        }

        /// <summary>True if this exe got its startup priorities from us (so its current priority is ours, not its own).</summary>
        public static bool IsManaged(string processName)
        {
            try
            {
                using (RegistryKey ifeo = OpenIfeo(false))
                {
                    return ifeo != null && IsManaged(ifeo, processName + ".exe");
                }
            }
            catch
            {
                return false;
            }
        }

        private static RegistryKey OpenIfeo(bool writable)
        {
            // Always the native (64-bit) view: that is the one the kernel reads.
            RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            return hklm.OpenSubKey(IfeoPath, writable);
        }

        private static bool IsManaged(RegistryKey ifeo, string exe)
        {
            using (RegistryKey perf = ifeo.OpenSubKey(exe + @"\PerfOptions", false))
            {
                return perf != null && perf.GetValue(Marker) != null;
            }
        }

        private static void Write(RegistryKey ifeo, ProcessRule rule)
        {
            if (ProtectedProcesses.CannotLimit(rule.Name)) return;
            string exe = rule.Name + ".exe";
            using (RegistryKey perf = ifeo.CreateSubKey(exe + @"\PerfOptions"))
            {
                // Never take over values someone else configured.
                if (perf.GetValue(Marker) == null && perf.GetValueNames().Length > 0) return;
                perf.SetValue(Marker, 1, RegistryValueKind.DWord);
                SetOrDelete(perf, "CpuPriorityClass", rule.MaxCpuPercent > 0 ? CpuBelowNormal : 0);
                SetOrDelete(perf, "IoPriority", rule.MaxDiskMBps > 0 ? IoLow : 0);
                SetOrDelete(perf, "PagePriority", rule.MaxRamMB > 0 ? PageLow : 0);
            }
        }

        /// <summary>0 = "not limited": remove the value so Windows uses its default.</summary>
        private static void SetOrDelete(RegistryKey key, string name, int value)
        {
            if (value > 0) key.SetValue(name, value, RegistryValueKind.DWord);
            else key.DeleteValue(name, false);
        }

        private static void RemoveValues(RegistryKey ifeo, string exe)
        {
            using (RegistryKey perf = ifeo.OpenSubKey(exe + @"\PerfOptions", true))
            {
                if (perf == null) return;
                foreach (string v in new[] { Marker, "CpuPriorityClass", "IoPriority", "PagePriority" }) perf.DeleteValue(v, false);
                if (perf.GetValueNames().Length > 0 || perf.SubKeyCount > 0) return;
            }
            ifeo.DeleteSubKey(exe + @"\PerfOptions", false);
            using (RegistryKey app = ifeo.OpenSubKey(exe, false))
            {
                if (app == null || app.GetValueNames().Length > 0 || app.SubKeyCount > 0) return;
            }
            ifeo.DeleteSubKey(exe, false);
        }
    }
}
