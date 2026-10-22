using System;
using System.Collections.Generic;

namespace TaskManagerPro
{
    /// <summary>
    /// Everything that happens when limits change, in one place, for both the window and the
    /// background engine:
    ///  - limits.cfg (the rules)                        → RuleStore
    ///  - running processes                             → ResourceGovernor (only when enforcing locally)
    ///  - apps started later, with no process running   → PerfOptions (registry) + NetworkQos (Windows policy)
    /// When the background engine runs, the window is a client: it saves rules and persistent limits,
    /// then tells the engine, which owns the running-process side.
    /// </summary>
    internal sealed class LimitController
    {
        public readonly RuleStore Rules = new RuleStore();
        public readonly AppProfileStore Profiles = new AppProfileStore();
        public readonly ResourceGovernor Governor = new ResourceGovernor();
        private LimitState state = new LimitState();

        public LimitController()
        {
            Profiles.Load();
            Rules.Floors = name => LimitPolicy.FloorsFor(Profiles.Get(name), null, false);
            Rules.Load();
        }

        /// <summary>The lowest limits allowed for an app, from its own usage data.</summary>
        public LimitFloors FloorsFor(string name, ProcessGroup current)
        {
            bool limited = !state.Paused && Rules.Get(name) != null;
            return LimitPolicy.FloorsFor(Profiles.Get(name), current, limited);
        }

        public bool Paused
        {
            get { return state.Paused; }
        }

        public void Reload()
        {
            Profiles.Load();
            Rules.Load();
            state = new LimitState();
        }

        /// <summary>Applies the limits to the current processes (nothing while paused).</summary>
        public Dictionary<string, GovernorState> Enforce(IList<ProcessGroup> groups, bool watchUsage)
        {
            if (state.Paused) return new Dictionary<string, GovernorState>(StringComparer.OrdinalIgnoreCase);
            return Governor.Enforce(groups, Rules.Snapshot(), watchUsage);
        }

        /// <summary>
        /// Saves new limits for one app (null or all zero = remove) and updates the persistent OS limits.
        /// With <paramref name="governLocally"/> the app's running processes are restored right away; the
        /// next Enforce applies the new limits. Returns false if limits.cfg could not be written.
        /// </summary>
        public bool SetRule(string name, ProcessRule rule, ProcessGroup current, bool governLocally)
        {
            if (Rules.NeedsReview) return false; // never overwrite limits the user hasn't reviewed yet
            // The app's own minimums; null for protected apps or when nothing is limited any more.
            if (rule != null) rule = LimitPolicy.Sanitize(rule, FloorsFor(name, current));
            ProcessRule old = Rules.Get(name);
            if (old == null && rule == null) return true;
            if (old != null && rule != null && old.SameLimits(rule)) return true;

            if (governLocally) Governor.ReleaseGroup(name, current);
            if (rule == null) Rules.Remove(name);
            else Rules.Set(rule);
            bool saved = Rules.Save();

            if (!state.Paused)
            {
                PerfOptions.Sync(Rules.Snapshot());
                int oldNet = old == null ? 0 : old.MaxNetKBps;
                int newNet = rule == null ? 0 : rule.MaxNetKBps;
                if (oldNet != newNet) NetworkQos.Apply(name, newNet);
            }
            return saved;
        }

        /// <summary>The user confirmed a limits file that was changed outside the app: apply it.</summary>
        public bool AcceptReview()
        {
            bool saved = Rules.AcceptReview();
            if (!state.Paused) SyncPersistent();
            return saved;
        }

        /// <summary>The user rejected a limits file that was changed outside the app: start with no limits.</summary>
        public bool RejectReview()
        {
            bool saved = Rules.RejectReview();
            PerfOptions.Sync(null);
            NetworkQos.Sync(null);
            return saved;
        }

        /// <summary>
        /// Makes the persistent OS limits (registry PerfOptions, QoS policies) match the sealed rules
        /// again, undoing any change made to them from outside. Run when the enforcing process starts.
        /// </summary>
        public void SyncPersistent()
        {
            if (Rules.NeedsReview) return;
            IDictionary<string, ProcessRule> rules = state.Paused ? null : Rules.Snapshot();
            PerfOptions.Sync(rules);
            NetworkQos.Sync(rules);
        }

        /// <summary>Pause = every app back to normal and no persistent limits, but the rules are kept.</summary>
        public void SetPaused(bool paused, IList<ProcessGroup> current)
        {
            if (state.Paused == paused) return;
            state.Paused = paused;
            state.Save();
            if (paused)
            {
                ReleaseRunning(current);
                PerfOptions.Sync(null);
                NetworkQos.Sync(null);
            }
            else
            {
                PerfOptions.Sync(Rules.Snapshot());
                NetworkQos.Sync(Rules.Snapshot());
            }
        }

        /// <summary>Deletes every rule and undoes every limit.</summary>
        public void ClearAll(IList<ProcessGroup> current, bool governLocally)
        {
            if (governLocally) ReleaseRunning(current);
            Rules.Clear();
            Rules.Save();
            PerfOptions.Sync(null);
            NetworkQos.Sync(null);
        }

        /// <summary>Undoes every limit but keeps the rules (used by the uninstaller).</summary>
        public void ReleaseEverything(IList<ProcessGroup> current)
        {
            ReleaseRunning(current);
            PerfOptions.Sync(null);
            NetworkQos.Sync(null);
        }

        /// <summary>Keeps every limit in place and lets go (window closing / engine exiting).</summary>
        public void Detach()
        {
            Governor.Detach();
        }

        public bool TrayHintShown
        {
            get { return state.TrayHintShown; }
            set
            {
                state.TrayHintShown = value;
                state.Save();
            }
        }

        private void ReleaseRunning(IList<ProcessGroup> current)
        {
            IDictionary<string, ProcessRule> rules = Rules.Snapshot();
            if (current != null)
            {
                foreach (ProcessGroup g in current)
                {
                    if (rules.ContainsKey(g.Name)) Governor.ReleaseGroup(g.Name, g);
                }
            }
            Governor.ReleaseAll();
        }
    }
}
