using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TaskManagerPro
{
    /// <summary>What an app normally uses, learned from the app itself while it runs without limits.</summary>
    internal sealed class AppProfile
    {
        public string Name;
        public double MemoryMB;      // typical private memory
        public double CpuPercent;    // typical CPU share while active
        public double DiskMBps;      // typical disk rate while active
        public double NetKBps;       // typical network rate while active
        public int Samples;
        public DateTime LastSampleUtc;
    }

    /// <summary>
    /// Learns every app's normal resource use, so the minimum allowed limit can come from the app's
    /// own data instead of fixed numbers (we can't know in advance which apps a user runs).
    ///  - Only learns while the app is NOT limited and limits aren't paused-by-tamper, so a limit can
    ///    never drag its own minimum down.
    ///  - Exponential moving averages, at most one sample per app every 15 s.
    ///  - Stored in profiles.cfg in the admin-only data folder and sealed with the same HMAC key as
    ///    limits.cfg: a changed file is ignored (only the Windows-level floors apply then), so nobody
    ///    can lower an app's minimum from outside.
    /// </summary>
    internal sealed class AppProfileStore
    {
        private const string SealPrefix = "#seal:";
        private const double Alpha = 0.1;                 // weight of a new sample
        private static readonly TimeSpan SampleEvery = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(5);
        private const int MaxProfiles = 2000;

        private readonly object sync = new object();
        private Dictionary<string, AppProfile> profiles = new Dictionary<string, AppProfile>(StringComparer.OrdinalIgnoreCase);
        private bool dirty;
        private DateTime lastSaveUtc = DateTime.UtcNow;

        public AppProfile Get(string name)
        {
            lock (sync)
            {
                AppProfile p;
                return name != null && profiles.TryGetValue(name, out p) ? p : null;
            }
        }

        /// <summary>Records the current use of every app that has no limit (window process only).</summary>
        public void Observe(IList<ProcessGroup> groups, IDictionary<string, ProcessRule> rules, bool limitsActive)
        {
            DateTime now = DateTime.UtcNow;
            lock (sync)
            {
                foreach (ProcessGroup g in groups)
                {
                    // A limited app shows its limited use, not its needs: never learn from it.
                    if (limitsActive && rules.ContainsKey(g.Name)) continue;
                    if (ProtectedProcesses.IsWindowsComponent(g.Name) || !LimitPolicy.IsValidName(g.Name)) continue;

                    AppProfile p;
                    if (!profiles.TryGetValue(g.Name, out p))
                    {
                        if (profiles.Count >= MaxProfiles) continue;
                        p = new AppProfile { Name = g.Name };
                        profiles[g.Name] = p;
                    }
                    if (now - p.LastSampleUtc < SampleEvery) continue;

                    double mem = g.PrivateBytes / (1024.0 * 1024.0);
                    double disk = g.DiskBytesPerSec / (1024.0 * 1024.0);
                    double net = g.NetBytesPerSec / 1024.0;
                    if (p.Samples == 0)
                    {
                        p.MemoryMB = mem;
                        p.CpuPercent = g.CpuPercent;
                        p.DiskMBps = disk;
                        p.NetKBps = net;
                    }
                    else
                    {
                        p.MemoryMB += Alpha * (mem - p.MemoryMB);
                        p.CpuPercent += Alpha * (g.CpuPercent - p.CpuPercent);
                        p.DiskMBps += Alpha * (disk - p.DiskMBps);
                        p.NetKBps += Alpha * (net - p.NetKBps);
                    }
                    p.Samples = Math.Min(int.MaxValue - 1, p.Samples + 1);
                    p.LastSampleUtc = now;
                    dirty = true;
                }
            }
            if (dirty && now - lastSaveUtc > SaveEvery) Save();
        }

        public void Load()
        {
            var map = new Dictionary<string, AppProfile>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(AppPaths.ProfilesFile))
                {
                    var body = new List<string>();
                    string seal = null;
                    foreach (string raw in File.ReadAllLines(AppPaths.ProfilesFile))
                    {
                        string line = raw.Trim();
                        if (line.StartsWith(SealPrefix, StringComparison.Ordinal)) seal = line.Substring(SealPrefix.Length).Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        body.Add(line);
                        AppProfile p = Parse(line);
                        if (p != null && map.Count < MaxProfiles) map[p.Name] = p;
                    }
                    if (body.Count > 0 && !Seal.Matches(body, seal))
                    {
                        Log.Write("profiles.cfg was changed outside Task Manager Pro: learned app minimums are ignored and relearned.");
                        map.Clear();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Load app profiles", ex);
                map.Clear();
            }
            lock (sync)
            {
                profiles = map;
                dirty = false;
            }
        }

        public void Save()
        {
            List<string> body;
            lock (sync)
            {
                if (!dirty) return;
                body = new List<string>(profiles.Count);
                foreach (AppProfile p in profiles.Values)
                {
                    body.Add(string.Format(CultureInfo.InvariantCulture, "{0}|{1:0.#}|{2:0.##}|{3:0.###}|{4:0.#}|{5}",
                        p.Name, p.MemoryMB, p.CpuPercent, p.DiskMBps, p.NetKBps, p.Samples));
                }
                dirty = false;
                lastSaveUtc = DateTime.UtcNow;
            }
            try
            {
                var lines = new List<string> { "# Task Manager Pro - what each app normally uses (learned). app|memoryMB|cpu%|diskMBps|netKBps|samples" };
                lines.AddRange(body);
                lines.Add(SealPrefix + Seal.Compute(body));
                AppPaths.WriteAllLinesAtomic(AppPaths.ProfilesFile, lines);
            }
            catch (Exception ex)
            {
                Log.Error("Save app profiles", ex);
            }
        }

        private static AppProfile Parse(string line)
        {
            string[] parts = line.Split('|');
            if (parts.Length != 6 || !LimitPolicy.IsValidName(parts[0])) return null;
            var p = new AppProfile { Name = parts[0].Trim() };
            p.MemoryMB = Number(parts[1]);
            p.CpuPercent = Math.Min(100, Number(parts[2]));
            p.DiskMBps = Number(parts[3]);
            p.NetKBps = Number(parts[4]);
            int samples;
            int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out samples);
            p.Samples = Math.Max(0, samples);
            return p;
        }

        private static double Number(string text)
        {
            double v;
            return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0 && !double.IsInfinity(v) ? v : 0;
        }
    }

    /// <summary>HMAC-SHA256 seal shared by limits.cfg and profiles.cfg.</summary>
    internal static class Seal
    {
        public static string Compute(List<string> body)
        {
            using (var hmac = new HMACSHA256(AppPaths.GetSealKey()))
            {
                return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", body))));
            }
        }

        public static bool Matches(List<string> body, string seal)
        {
            if (string.IsNullOrEmpty(seal)) return false;
            string expected;
            try
            {
                expected = Compute(body);
            }
            catch (Exception ex)
            {
                Log.Error("Verify seal", ex);
                return false;
            }
            if (expected.Length != seal.Length) return false;
            int diff = 0;
            for (int i = 0; i < expected.Length; i++) diff |= expected[i] ^ seal[i]; // constant time
            return diff == 0;
        }
    }
}
