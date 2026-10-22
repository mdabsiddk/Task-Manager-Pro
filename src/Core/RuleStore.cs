using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace TaskManagerPro
{
    /// <summary>
    /// Persistent rule list (limits.cfg). Copy-on-write: Snapshot() returns an immutable dictionary
    /// that the governor can read without locking.
    ///
    /// Tamper protection: every save ends with a "#seal:" line, an HMAC-SHA256 of the rules made
    /// with a secret key that only administrators can read. A file that was written by anything
    /// else (another program, malware, a hand edit, an old version) is loaded as "needs review":
    /// none of its limits are applied until the user confirms them in the window. Every rule is also
    /// raised to at least the app's own minimum (<see cref="LimitPolicy"/>), so even a confirmed file can't
    /// starve an app or limit a Windows component.
    ///
    /// Format, one rule per line (compatible with v1): app|maxCpu%|maxMemoryMB|maxDiskMBps|maxNetworkKBps
    /// </summary>
    internal sealed class RuleStore
    {
        private const string SealPrefix = "#seal:";
        private readonly object writeLock = new object();
        private volatile Dictionary<string, ProcessRule> rules = NewMap();
        private List<ProcessRule> unverified;

        /// <summary>
        /// Lowest allowed limits per app (from the app's learned profile). Every rule that enters the
        /// store, from the window or from the file, is raised to at least these values.
        /// </summary>
        public Func<string, LimitFloors> Floors = name => LimitPolicy.FloorsFor(null, null, false);

        public int Count
        {
            get { return rules.Count; }
        }

        /// <summary>True if limits.cfg was not written by Task Manager Pro; nothing is applied until reviewed.</summary>
        public bool NeedsReview
        {
            get { return unverified != null; }
        }

        /// <summary>The (already sanitized) limits waiting for the user's confirmation.</summary>
        public IList<ProcessRule> PendingReview
        {
            get { return unverified != null ? unverified.AsReadOnly() : null; }
        }

        public IDictionary<string, ProcessRule> Snapshot()
        {
            return rules;
        }

        public ProcessRule Get(string name)
        {
            ProcessRule rule;
            return rules.TryGetValue(name, out rule) ? rule : null;
        }

        public void Set(ProcessRule rule)
        {
            if (rule == null) return;
            rule = LimitPolicy.Sanitize(rule, Floors(rule.Name));
            if (rule == null) return;
            lock (writeLock)
            {
                if (!rules.ContainsKey(rule.Name) && rules.Count >= LimitPolicy.MaxRules) return;
                var copy = new Dictionary<string, ProcessRule>(rules, StringComparer.OrdinalIgnoreCase);
                copy[rule.Name] = rule;
                rules = copy;
            }
        }

        public void Remove(string name)
        {
            lock (writeLock)
            {
                if (!rules.ContainsKey(name)) return;
                var copy = new Dictionary<string, ProcessRule>(rules, StringComparer.OrdinalIgnoreCase);
                copy.Remove(name);
                rules = copy;
            }
        }

        public void Clear()
        {
            lock (writeLock)
            {
                rules = NewMap();
                unverified = null;
            }
        }

        public void Load()
        {
            var map = NewMap();
            List<ProcessRule> pending = null;
            try
            {
                // One-time migration of limits saved by Smart Task Manager. Unsealed, so it is reviewed.
                if (!File.Exists(AppPaths.RulesFile))
                {
                    foreach (string legacy in AppPaths.LegacyRulesFiles)
                    {
                        if (!File.Exists(legacy)) continue;
                        AppPaths.EnsureDataDir();
                        File.Copy(legacy, AppPaths.RulesFile);
                        break;
                    }
                }

                if (File.Exists(AppPaths.RulesFile))
                {
                    var body = new List<string>();
                    string seal = null;
                    foreach (string raw in File.ReadAllLines(AppPaths.RulesFile))
                    {
                        string line = raw.Trim();
                        if (line.StartsWith(SealPrefix, StringComparison.Ordinal)) seal = line.Substring(SealPrefix.Length).Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        body.Add(line);
                        ProcessRule parsed = ParseLine(line);
                        ProcessRule rule = parsed != null && LimitPolicy.IsValidName(parsed.Name)
                            ? LimitPolicy.Sanitize(parsed, Floors(parsed.Name.Trim()))
                            : null;
                        if (rule != null && (map.ContainsKey(rule.Name) || map.Count < LimitPolicy.MaxRules)) map[rule.Name] = rule;
                    }

                    if (body.Count > 0 && !Seal.Matches(body, seal))
                    {
                        Log.Write("limits.cfg was changed outside Task Manager Pro: its limits are not applied until reviewed.");
                        pending = new List<ProcessRule>(map.Values);
                        map = NewMap();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Load rules", ex);
            }
            lock (writeLock)
            {
                rules = map;
                unverified = pending;
            }
        }

        /// <summary>The user confirmed the reviewed limits: apply them and seal the file.</summary>
        public bool AcceptReview()
        {
            lock (writeLock)
            {
                if (unverified == null) return true;
                var map = NewMap();
                foreach (ProcessRule r in unverified) map[r.Name] = r;
                rules = map;
                unverified = null;
            }
            return Save();
        }

        /// <summary>The user rejected the reviewed limits: keep a copy for reference and start empty.</summary>
        public bool RejectReview()
        {
            try
            {
                if (File.Exists(AppPaths.RulesFile))
                {
                    string copy = Path.Combine(AppPaths.DataDir, "limits.rejected-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".cfg");
                    File.Copy(AppPaths.RulesFile, copy, true);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Keep rejected limits", ex);
            }
            Clear();
            return Save();
        }

        public bool Save()
        {
            try
            {
                var body = new List<string>();
                foreach (ProcessRule r in rules.Values)
                {
                    body.Add(string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3}|{4}",
                        r.Name, r.MaxCpuPercent, r.MaxRamMB, r.MaxDiskMBps, r.MaxNetKBps));
                }
                var lines = new List<string>
                {
                    "# Task Manager Pro limits - app|maxCpu%|maxMemoryMB|maxDiskMBps|maxNetworkKBps (0 = no limit)",
                    "# Edit limits in Task Manager Pro. Changes made here by hand must be confirmed in the app."
                };
                lines.AddRange(body);
                lines.Add(SealPrefix + Seal.Compute(body));
                AppPaths.WriteAllLinesAtomic(AppPaths.RulesFile, lines);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Save rules", ex);
                return false;
            }
        }

        // ---------------- Parsing ----------------

        private static ProcessRule ParseLine(string line)
        {
            string[] parts = line.Split('|');
            if (parts.Length != 5) return null;
            return new ProcessRule
            {
                Name = parts[0].Trim(),
                MaxCpuPercent = ParseInt(parts[1]),
                MaxRamMB = ParseInt(parts[2]),
                MaxDiskMBps = ParseInt(parts[3]),
                MaxNetKBps = ParseInt(parts[4])
            };
        }

        private static int ParseInt(string text)
        {
            int value;
            return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? Math.Max(0, value) : 0;
        }

        private static Dictionary<string, ProcessRule> NewMap()
        {
            return new Dictionary<string, ProcessRule>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
