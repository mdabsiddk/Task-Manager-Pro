using System;
using System.Globalization;

namespace TaskManagerPro
{
    /// <summary>The lowest limits allowed for one app, and where they come from.</summary>
    internal struct LimitFloors
    {
        public int CpuPercent;
        public int MemoryMB;
        public int DiskMBps;
        public int NetKBps;
        /// <summary>Typical memory the floor was derived from (0 = no app data yet).</summary>
        public int TypicalMemoryMB;
        /// <summary>"learned" (app profile), "current" (what it uses right now) or "basic" (no app data).</summary>
        public string Basis;
    }

    /// <summary>
    /// Safety floors for limits. Every app needs some CPU, memory, disk and network to start and stay
    /// responsive, and that amount is different for every app, so the floor is computed from the app's
    /// own data rather than from a fixed list: a limit can never be lower than HALF of what the app
    /// normally uses (learned while it runs without a limit, see <see cref="AppProfileStore"/>). Before
    /// there is any data, only Windows-level floors apply (half a logical processor, 32 MB per process).
    /// The floors are enforced for limits typed in the window, for limits.cfg on every load (so a
    /// tampered file can't starve an app) and inside the governor itself.
    /// </summary>
    internal static class LimitPolicy
    {
        /// <summary>Fraction of an app's normal use that a limit may never go below.</summary>
        public const double ShareOfTypicalUse = 0.5;

        // Windows-level floors: what any process needs, independent of the app.
        public const int MinMemoryPerProcessMB = 32;
        public const int BasicMemoryMB = 64;
        public const int BasicDiskMBps = 1;
        public const int BasicNetKBps = 30;              // ≈ 0.25 Mbps: enough to stay connected

        public const int MaxMemoryMB = 4 * 1024 * 1024;  // sanity ceilings
        public const int MaxDiskMBps = 100000;
        public const int MaxNetKBps = 2000000;
        public const int MaxRules = 256;
        public const int MaxNameLength = 128;
        private const int MinProfileSamples = 3;

        /// <summary>Half of one logical processor, as a share of the whole CPU (at least 1%).</summary>
        public static int BasicCpuPercent
        {
            get { return (int)Math.Max(1, Math.Ceiling(50.0 / Math.Max(1, Environment.ProcessorCount))); }
        }

        /// <summary>
        /// Floors for one app. <paramref name="current"/> (optional) is the running app: its process count
        /// raises the memory floor, and if there is no learned profile yet and the app is running without
        /// a limit, what it uses right now stands in for its typical use.
        /// </summary>
        public static LimitFloors FloorsFor(AppProfile profile, ProcessGroup current, bool currentlyLimited)
        {
            int processes = current != null ? Math.Max(1, current.Processes.Count) : 1;
            var f = new LimitFloors
            {
                CpuPercent = BasicCpuPercent,
                MemoryMB = Math.Max(BasicMemoryMB, MinMemoryPerProcessMB * processes),
                DiskMBps = BasicDiskMBps,
                NetKBps = BasicNetKBps,
                Basis = "basic"
            };

            double mem, cpu, disk, net;
            if (profile != null && profile.Samples >= MinProfileSamples)
            {
                mem = profile.MemoryMB;
                cpu = profile.CpuPercent;
                disk = profile.DiskMBps;
                net = profile.NetKBps;
                f.Basis = "learned";
            }
            else if (current != null && !currentlyLimited)
            {
                mem = current.PrivateBytes / (1024.0 * 1024.0);
                cpu = current.CpuPercent;
                disk = current.DiskBytesPerSec / (1024.0 * 1024.0);
                net = current.NetBytesPerSec / 1024.0;
                f.Basis = "current";
            }
            else
            {
                return f;
            }

            f.TypicalMemoryMB = (int)Math.Round(mem);
            f.MemoryMB = Math.Max(f.MemoryMB, RoundUp(mem * ShareOfTypicalUse, 8));
            f.CpuPercent = Math.Min(100, Math.Max(f.CpuPercent, (int)Math.Ceiling(cpu * ShareOfTypicalUse)));
            f.DiskMBps = Math.Max(f.DiskMBps, (int)Math.Ceiling(disk * ShareOfTypicalUse));
            f.NetKBps = Math.Max(f.NetKBps, (int)Math.Ceiling(net * ShareOfTypicalUse));
            f.MemoryMB = Math.Min(MaxMemoryMB, f.MemoryMB);
            return f;
        }

        /// <summary>
        /// Returns a safe copy of the rule with every limit raised to at least the app's floor, or null if
        /// the rule must not exist (invalid or protected app name, or nothing left to limit).
        /// </summary>
        public static ProcessRule Sanitize(ProcessRule rule, LimitFloors floors)
        {
            if (rule == null || !IsValidName(rule.Name) || ProtectedProcesses.CannotLimit(rule.Name)) return null;
            var safe = new ProcessRule
            {
                Name = rule.Name.Trim(),
                MaxCpuPercent = Clamp(rule.MaxCpuPercent, floors.CpuPercent, 100),
                MaxRamMB = Clamp(rule.MaxRamMB, floors.MemoryMB, MaxMemoryMB),
                MaxDiskMBps = Clamp(rule.MaxDiskMBps, floors.DiskMBps, MaxDiskMBps),
                MaxNetKBps = Clamp(rule.MaxNetKBps, floors.NetKBps, MaxNetKBps)
            };
            return safe.IsEmpty ? null : safe;
        }

        // ---------------- Messages for values typed in the window (null = acceptable) ----------------

        public static string CheckCpu(int percent, LimitFloors f, string app)
        {
            if (percent == 0) return null;
            if (percent > 100) return "CPU limit can't be more than 100%.";
            if (percent < f.CpuPercent)
                return "The lowest CPU limit for " + app + " is " + f.CpuPercent + "% (" + Reason(f) + "), or leave it empty for no limit.";
            return null;
        }

        public static string CheckMemory(int mb, LimitFloors f, string app)
        {
            if (mb == 0) return null;
            if (mb > MaxMemoryMB) return "That memory limit is too large.";
            if (mb < f.MemoryMB)
                return "The lowest memory limit for " + app + " is " + f.MemoryMB.ToString("#,0", CultureInfo.InvariantCulture) + " MB (" + Reason(f)
                       + "). Less could stop it from starting. Leave it empty for no limit.";
            return null;
        }

        public static string CheckDisk(int mbps, LimitFloors f, string app)
        {
            if (mbps == 0) return null;
            if (mbps > MaxDiskMBps) return "That disk limit is too large.";
            if (mbps < f.DiskMBps) return "The lowest disk limit for " + app + " is " + f.DiskMBps + " MB/s (" + Reason(f) + "), or leave it empty for no limit.";
            return null;
        }

        public static string CheckNetwork(int kbps, LimitFloors f, string app)
        {
            if (kbps == 0) return null;
            if (kbps > MaxNetKBps) return "That network limit is too large.";
            if (kbps < f.NetKBps)
                return "The lowest network limit for " + app + " is " + Mbps(f.NetKBps) + " Mbps (" + Reason(f) + "). Less could cut its connection. Leave it empty for no limit.";
            return null;
        }

        /// <summary>Short text for the hint shown when a limit box is opened.</summary>
        public static string Describe(LimitFloors f, string column)
        {
            switch (column)
            {
                case "cpu": return "Lowest allowed: " + f.CpuPercent + "%  (" + Reason(f) + ")";
                case "memory": return "Lowest allowed: " + f.MemoryMB.ToString("#,0", CultureInfo.InvariantCulture) + " MB  (" + Reason(f) + ")";
                case "disk": return "Lowest allowed: " + f.DiskMBps + " MB/s  (" + Reason(f) + ")";
                default: return "Lowest allowed: " + Mbps(f.NetKBps) + " Mbps  (" + Reason(f) + ")";
            }
        }

        public static string Mbps(int kbps)
        {
            return (NetworkQos.ToBitsPerSecond(kbps) / 1e6).ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string Reason(LimitFloors f)
        {
            switch (f.Basis)
            {
                case "learned":
                    return "half of what it normally uses" + (f.TypicalMemoryMB > 0 ? ", about " + f.TypicalMemoryMB.ToString("#,0", CultureInfo.InvariantCulture) + " MB of memory" : "");
                case "current":
                    return "half of what it uses right now" + (f.TypicalMemoryMB > 0 ? ", " + f.TypicalMemoryMB.ToString("#,0", CultureInfo.InvariantCulture) + " MB of memory" : "");
                default:
                    return "no usage data for this app yet, so only the Windows minimum applies";
            }
        }

        public static bool IsValidName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.Trim();
            return n.Length > 0 && n.Length <= MaxNameLength && n.IndexOfAny(new[] { '|', '\\', '/', ':', '*', '?', '"', '<', '>', '\r', '\n', '\t' }) < 0;
        }

        private static int Clamp(int value, int floor, int ceiling)
        {
            if (value <= 0) return 0;
            return Math.Min(ceiling, Math.Max(floor, value));
        }

        private static int RoundUp(double value, int step)
        {
            return (int)(Math.Ceiling(value / step) * step);
        }
    }
}
