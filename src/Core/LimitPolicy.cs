using System;
using System.Globalization;

namespace TaskManagerPro
{
    /// <summary>The lowest limits allowed for one app (0 = no minimum), and where they come from.</summary>
    internal struct LimitFloors
    {
        public int CpuPercent;
        public int MemoryMB;
        public int DiskMBps;
        public int NetKBps;
        /// <summary>"current" (what the app uses right now), "learned" (what it usually uses) or "none".</summary>
        public string Basis;
    }

    /// <summary>
    /// The one rule for how low a limit may be set: a limit under 20% of what the app uses as a whole
    /// is not saved. "Uses" is the app's total across all its processes at the moment Enter is pressed
    /// (or, while the app is not running or is already held by a limit, what it usually uses, learned
    /// while it ran without a limit, see <see cref="AppProfileStore"/>). The same check, against the
    /// learned value, is applied to a limits.cfg that was changed from outside. Nothing else sets a
    /// minimum: no fixed floors and nothing per process.
    /// </summary>
    internal static class LimitPolicy
    {
        /// <summary>A limit may not be lower than this share of the app's use.</summary>
        public const double ShareOfUse = 0.20;

        public const int MaxMemoryMB = 4 * 1024 * 1024;  // sanity ceilings
        public const int MaxDiskMBps = 100000;
        public const int MaxNetKBps = 2000000;
        public const int MaxRules = 256;
        public const int MaxNameLength = 128;
        private const int MinProfileSamples = 3;

        /// <summary>Half of one logical processor, as a share of the whole CPU (at least 1%): the smallest CPU cap the governor sets.</summary>
        public static int BasicCpuPercent
        {
            get { return (int)Math.Max(1, Math.Ceiling(50.0 / Math.Max(1, Environment.ProcessorCount))); }
        }

        /// <summary>
        /// The lowest allowed limits for one app. <paramref name="current"/> (optional) is the running app:
        /// while it runs without a limit, its total use right now is the base. Otherwise the learned profile is.
        /// </summary>
        public static LimitFloors FloorsFor(AppProfile profile, ProcessGroup current, bool currentlyLimited)
        {
            var f = new LimitFloors { Basis = "none" };
            double mem, cpu, disk, net;
            if (current != null && !currentlyLimited)
            {
                mem = current.PrivateBytes / (1024.0 * 1024.0);
                cpu = current.CpuPercent;
                disk = current.DiskBytesPerSec / (1024.0 * 1024.0);
                net = current.NetBytesPerSec / 1024.0;
                f.Basis = "current";
            }
            else if (profile != null && profile.Samples >= MinProfileSamples)
            {
                mem = profile.MemoryMB;
                cpu = profile.CpuPercent;
                disk = profile.DiskMBps;
                net = profile.NetKBps;
                f.Basis = "learned";
            }
            else
            {
                return f;
            }

            f.MemoryMB = Math.Min(MaxMemoryMB, (int)Math.Ceiling(mem * ShareOfUse));
            f.CpuPercent = Math.Min(100, (int)Math.Ceiling(cpu * ShareOfUse));
            f.DiskMBps = (int)Math.Ceiling(disk * ShareOfUse);
            f.NetKBps = (int)Math.Ceiling(net * ShareOfUse);
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

        // ---------------- Message for a value that can't be saved (null = acceptable) ----------------

        public static string CheckCpu(int percent, LimitFloors f, string app)
        {
            if (percent == 0) return null;
            if (percent > 100) return "A CPU limit can't be more than 100%.";
            if (percent < f.CpuPercent) return TooLow("CPU", f.CpuPercent + "%", f, app);
            return null;
        }

        public static string CheckMemory(int mb, LimitFloors f, string app)
        {
            if (mb == 0) return null;
            if (mb > MaxMemoryMB) return "That memory limit is too large.";
            if (mb < f.MemoryMB) return TooLow("memory", f.MemoryMB.ToString("#,0", CultureInfo.InvariantCulture) + " MB", f, app);
            return null;
        }

        public static string CheckDisk(int mbps, LimitFloors f, string app)
        {
            if (mbps == 0) return null;
            if (mbps > MaxDiskMBps) return "That disk limit is too large.";
            if (mbps < f.DiskMBps) return TooLow("disk", f.DiskMBps + " MB/s", f, app);
            return null;
        }

        public static string CheckNetwork(int kbps, LimitFloors f, string app)
        {
            if (kbps == 0) return null;
            if (kbps > MaxNetKBps) return "That network limit is too large.";
            if (kbps < f.NetKBps) return TooLow("network", Mbps(f.NetKBps) + " Mbps", f, app);
            return null;
        }

        private static string TooLow(string what, string lowest, LimitFloors f, string app)
        {
            return "The lowest " + what + " limit for " + app + " is " + lowest + " (20% of what it "
                   + (f.Basis == "learned" ? "usually uses" : "uses now") + ").";
        }

        public static string Mbps(int kbps)
        {
            return (NetworkQos.ToBitsPerSecond(kbps) / 1e6).ToString("0.##", CultureInfo.InvariantCulture);
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
