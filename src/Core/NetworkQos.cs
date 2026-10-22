using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace TaskManagerPro
{
    /// <summary>
    /// Network limits through Windows' built-in Policy-based QoS (New-NetQosPolicy). Policies are
    /// persistent OS settings named "TaskManagerPro_&lt;app&gt;": they keep working with no process of
    /// ours running, even after a restart. Windows QoS throttles outbound (upload) traffic only.
    /// Commands run one at a time on a background thread so callers never wait for PowerShell.
    /// </summary>
    internal static class NetworkQos
    {
        private const string Prefix = "TaskManagerPro_";
        private const string LegacyPrefix = "SmartLimit_"; // Smart Task Manager v1
        private static readonly object queueLock = new object();
        private static int pending;

        /// <summary>Converts the stored limit (KB/s) to bits per second.</summary>
        public static long ToBitsPerSecond(int kiloBytesPerSecond)
        {
            return (long)kiloBytesPerSecond * 1024L * 8L;
        }

        public static void Apply(string appName, int maxKBps)
        {
            var script = new StringBuilder();
            script.AppendFormat("Remove-NetQosPolicy -Name {0},{1} -Confirm:$false -ErrorAction SilentlyContinue;",
                Quote(Prefix + appName), Quote(LegacyPrefix + appName));
            if (maxKBps > 0) AppendNew(script, appName, maxKBps);
            Run(script.ToString());
        }

        /// <summary>Makes the QoS policies match the rules exactly (null = remove all of ours).</summary>
        public static void Sync(IDictionary<string, ProcessRule> rules)
        {
            var script = new StringBuilder("Get-NetQosPolicy -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '"
                + Prefix + "*' -or $_.Name -like '" + LegacyPrefix + "*' } | Remove-NetQosPolicy -Confirm:$false -ErrorAction SilentlyContinue;");
            if (rules != null)
            {
                foreach (ProcessRule r in rules.Values)
                {
                    if (r.MaxNetKBps > 0 && !ProtectedProcesses.CannotLimit(r.Name)) AppendNew(script, r.Name, r.MaxNetKBps);
                }
            }
            Run(script.ToString());
        }

        private static void AppendNew(StringBuilder script, string appName, int maxKBps)
        {
            script.AppendFormat(" New-NetQosPolicy -Name {0} -AppPathNameMatchCondition {1} -ThrottleRateActionBitsPerSecond {2} -ErrorAction SilentlyContinue | Out-Null;",
                Quote(Prefix + appName), Quote(appName + ".exe"), ToBitsPerSecond(maxKBps));
        }

        private static string Quote(string value)
        {
            return "'" + value.Replace("'", "''") + "'";
        }

        private static void Run(string script)
        {
            // -EncodedCommand avoids every quoting problem with unusual process names.
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            string powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            Interlocked.Increment(ref pending);
            ThreadPool.QueueUserWorkItem(delegate
            {
                lock (queueLock)
                {
                    IntPtr h;
                    int pid;
                    int code = NativeMethods.RunProcess(powershell,
                        "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + encoded,
                        true, 60000, out h, out pid);
                    if (code != 0) Log.Write("Network QoS command failed (exit code " + code + ").");
                }
                Interlocked.Decrement(ref pending);
            });
        }

        /// <summary>Waits until queued commands have finished (before a short-lived process exits).</summary>
        public static void Flush(int timeoutMs)
        {
            for (int waited = 0; Volatile.Read(ref pending) > 0 && waited < timeoutMs; waited += 50) Thread.Sleep(50);
        }
    }
}
