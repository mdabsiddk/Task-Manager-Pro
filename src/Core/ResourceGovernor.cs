using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TaskManagerPro
{
    /// <summary>
    /// Applies limits to running processes with documented, reversible Windows mechanisms. Every
    /// one of them is a kernel setting on the process itself, so it keeps working after Task Manager
    /// Pro is closed (<see cref="Detach"/>), until that process exits:
    ///  - CPU : a named Job Object per app with a CPU-rate hard cap (Windows 8+). Child processes the app
    ///          starts later join the job automatically. Falls back to core affinity where a job can't be used.
    ///          Plus BelowNormal priority (Idle while over the limit, when usage is being watched).
    ///  - RAM : one limit for the whole app, checked as a total (see <see cref="TrimToLimit"/>). No process
    ///          gets a cap of its own: Windows manages them as usual, and one process may use the whole limit.
    ///          Only when the app's total is over the limit are the processes' working sets trimmed back
    ///          (the pages go to the standby list / page file; nothing crashes).
    ///  - Disk: I/O priority Low (Very low while over the limit)
    /// Original values are restored when a limit is removed or paused (<see cref="ReleaseGroup"/>,
    /// <see cref="ReleaseAll"/>). Processes limited during an earlier session are recognised through
    /// job membership and restored to Windows defaults. Thread-safe.
    /// </summary>
    internal sealed class ResourceGovernor
    {
        // PROCESS_TERMINATE is required by AssignProcessToJobObject; the governor never terminates anything.
        private const uint AccessRights = NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_SET_INFORMATION
                                        | NativeMethods.PROCESS_SET_QUOTA | NativeMethods.PROCESS_TERMINATE
                                        | NativeMethods.PROCESS_DUP_HANDLE;
        private static readonly TimeSpan TrimEvery = TimeSpan.FromSeconds(4); // at most one trim per app every few seconds
        private const double TrimTarget = 0.95;                  // trim to 95% of the limit, so it isn't over again at once
        private const long MinTrimBytes = 16L * 1024 * 1024;     // processes using less than this are left alone
        private const double Hysteresis = 0.8;                    // stop throttling below 80% of the limit
        private const int IoVeryLow = 0, IoLow = 1, IoNormal = 2;

        private sealed class ProcState
        {
            public int Pid;
            public long CreateTime;
            public string Name;
            public string Group;
            public bool NoAccess;
            public bool InJob;
            public bool JobFailed;

            public bool PriorityChanged;
            public uint OriginalPriority;
            public uint AppliedPriority;

            public bool AffinityChanged;
            public UIntPtr OriginalAffinity;
            public int AppliedCores;

            public bool IoChanged;
            public int OriginalIo;
            public int AppliedIo = -1;

            public bool WorkingSetChanged;
            public UIntPtr OriginalMinWs, OriginalMaxWs;
            public uint OriginalWsFlags;
            public long AppliedWsMax;

            public bool HasChanges
            {
                get { return PriorityChanged || AffinityChanged || IoChanged || WorkingSetChanged; }
            }
        }

        private sealed class GroupState
        {
            public bool CpuHot;
            public bool DiskHot;
            public bool MemoryOver;   // over its memory limit at the last check
            public DateTime LastTrimUtc;
            public IntPtr Job;      // named job object "TaskManagerPro.Job.<app>"
            public int JobPercent;  // cap currently set on the job, 0 = none
            public bool JobUsable = true;
        }

        private readonly object sync = new object();
        private readonly Dictionary<int, ProcState> procs = new Dictionary<int, ProcState>();
        private readonly Dictionary<string, GroupState> groups = new Dictionary<string, GroupState>(StringComparer.OrdinalIgnoreCase);
        private readonly int selfPid = NativeMethods.GetCurrentProcessId();
        private readonly int coreCount = Math.Max(1, Environment.ProcessorCount);

        /// <summary>
        /// Enforces all rules on the current process groups and returns the state of each limited app.
        /// <paramref name="watchUsage"/> = false is for callers that only sample when a process starts
        /// (the background engine): limits are then applied statically, without usage-based throttling.
        /// </summary>
        public Dictionary<string, GovernorState> Enforce(IList<ProcessGroup> snapshot, IDictionary<string, ProcessRule> rules, bool watchUsage)
        {
            var result = new Dictionary<string, GovernorState>(StringComparer.OrdinalIgnoreCase);
            lock (sync)
            {
                var touched = new Dictionary<int, bool>();
                foreach (ProcessGroup g in snapshot)
                {
                    ProcessRule rule;
                    if (!rules.TryGetValue(g.Name, out rule) || ProtectedProcesses.CannotLimit(g.Name)) continue;
                    result[g.Name] = EnforceGroup(g, rule, touched, watchUsage);
                }

                // Processes we changed that are no longer governed (exited or rule removed).
                var stale = new List<int>();
                foreach (var kv in procs)
                {
                    if (!touched.ContainsKey(kv.Key)) stale.Add(kv.Key);
                }
                foreach (int pid in stale)
                {
                    Restore(procs[pid]);
                    procs.Remove(pid);
                }

                var unused = new List<string>();
                foreach (string name in groups.Keys)
                {
                    if (!rules.ContainsKey(name)) unused.Add(name);
                }
                foreach (string name in unused)
                {
                    CloseJob(groups[name], true);
                    groups.Remove(name);
                }
            }
            return result;
        }

        /// <summary>
        /// Restores every process of one app to normal and removes its CPU cap. Pass the app's current
        /// processes so ones limited in an earlier session (unknown to this instance) are restored too.
        /// </summary>
        public void ReleaseGroup(string name, ProcessGroup current)
        {
            lock (sync)
            {
                GroupState gs;
                if (!groups.TryGetValue(name, out gs))
                {
                    gs = new GroupState();
                    groups[name] = gs;
                }
                if (current != null)
                {
                    OpenJob(gs, name);
                    foreach (ProcInfo p in current.Processes)
                    {
                        if (p.Pid != selfPid && !ProtectedProcesses.IsCritical(p.Name)) Track(p, name, gs);
                    }
                }

                var pids = new List<int>();
                foreach (var kv in procs)
                {
                    if (string.Equals(kv.Value.Group, name, StringComparison.OrdinalIgnoreCase)) pids.Add(kv.Key);
                }
                foreach (int pid in pids)
                {
                    Restore(procs[pid]);
                    procs.Remove(pid);
                }
                CloseJob(gs, true);
                groups.Remove(name);
            }
        }

        /// <summary>Restores every process this governor knows about.</summary>
        public void ReleaseAll()
        {
            lock (sync)
            {
                foreach (ProcState st in procs.Values) Restore(st);
                foreach (GroupState gs in groups.Values) CloseJob(gs, true);
                procs.Clear();
                groups.Clear();
            }
        }

        /// <summary>
        /// Lets go without undoing anything: limits stay on the running processes (jobs live on while
        /// they contain processes). Used when Task Manager Pro closes.
        /// </summary>
        public void Detach()
        {
            lock (sync)
            {
                foreach (GroupState gs in groups.Values) CloseJob(gs, false);
                procs.Clear();
                groups.Clear();
            }
        }

        private GovernorState EnforceGroup(ProcessGroup g, ProcessRule rule, Dictionary<int, bool> touched, bool watchUsage)
        {
            GroupState gs;
            if (!groups.TryGetValue(g.Name, out gs))
            {
                gs = new GroupState();
                groups[g.Name] = gs;
            }
            gs.CpuHot = watchUsage && UpdateHot(gs.CpuHot, g.CpuPercent, rule.MaxCpuPercent);
            gs.DiskHot = watchUsage && UpdateHot(gs.DiskHot, g.DiskBytesPerSec / (1024.0 * 1024.0), rule.MaxDiskMBps);

            uint wantPriority = 0;
            if (gs.CpuHot || gs.DiskHot) wantPriority = NativeMethods.IDLE_PRIORITY_CLASS;
            else if (rule.MaxCpuPercent > 0 || rule.MaxDiskMBps > 0) wantPriority = NativeMethods.BELOW_NORMAL_PRIORITY_CLASS;

            // Every limited process joins the app's job: it carries the CPU cap and also marks the
            // process as "ours", so a later session can find and restore it.
            bool haveJob = OpenJob(gs, g.Name);
            bool capOn = haveJob && SetJobCap(gs, rule.MaxCpuPercent);

            // Affinity is only the fallback where the job cap can't work. Rounded down so the share of
            // cores never exceeds the limit (minimum one core).
            int fallbackCores = rule.MaxCpuPercent > 0
                ? Math.Max(1, Math.Min(coreCount, (int)Math.Floor(rule.MaxCpuPercent / 100.0 * coreCount)))
                : 0;
            int wantIo = rule.MaxDiskMBps > 0 ? (gs.DiskHot ? IoVeryLow : IoLow) : -1;

            // Memory: measured like the Memory column (private working set of all the app's processes).
            // No process gets a cap; a cap left by an earlier version is lifted (wsMax = 0 below).
            long ramLimit = (long)rule.MaxRamMB * 1024L * 1024L;
            gs.MemoryOver = rule.MaxRamMB > 0 && g.PrivateBytes > ramLimit;
            const long wsMax = 0;

            int considered = 0, denied = 0;
            foreach (ProcInfo p in g.Processes)
            {
                // Never touch ourselves or a critical Windows process that ended up inside an app tree.
                if (p.Pid == selfPid || ProtectedProcesses.IsCritical(p.Name)) continue;
                considered++;
                touched[p.Pid] = true;

                ProcState st = Track(p, g.Name, gs);
                if (st.NoAccess)
                {
                    denied++;
                    continue;
                }

                bool joinJob = haveJob && !st.InJob && !st.JobFailed;
                int wantCores = capOn && !st.JobFailed ? 0 : fallbackCores;
                if (!joinJob && !NeedsWork(st, wantPriority, wantCores, wantIo, wsMax)) continue;

                IntPtr h = NativeMethods.OpenProcess(AccessRights, false, p.Pid);
                if (h == IntPtr.Zero)
                {
                    st.NoAccess = true; // protected process: remember and stop retrying
                    denied++;
                    continue;
                }
                try
                {
                    if (joinJob)
                    {
                        st.InJob = NativeMethods.AssignProcessToJobObject(gs.Job, h);
                        st.JobFailed = !st.InJob;
                        if (st.JobFailed) wantCores = fallbackCores;
                        else KeepJobNameAlive(gs.Job, h);
                    }
                    ApplyPriority(h, st, wantPriority);
                    ApplyAffinity(h, st, wantCores);
                    ApplyIoPriority(h, st, wantIo);
                    ApplyWorkingSet(h, st, wsMax);
                }
                finally
                {
                    NativeMethods.CloseHandle(h);
                }
            }

            if (gs.MemoryOver && DateTime.UtcNow - gs.LastTrimUtc >= TrimEvery)
            {
                gs.LastTrimUtc = DateTime.UtcNow;
                TrimToLimit(g, ramLimit);
            }

            if (considered > 0 && denied == considered) return GovernorState.NoAccess;
            if (gs.CpuHot || gs.DiskHot || (watchUsage && gs.MemoryOver)) return GovernorState.Throttling;
            return GovernorState.Active;
        }

        /// <summary>
        /// Returns the state for a process, creating it on first sight. A process that is already in
        /// this app's job (limited by an earlier session) or that Windows started with our PerfOptions
        /// priorities is "adopted": its current values are ours, so Windows defaults are what to restore.
        /// </summary>
        private ProcState Track(ProcInfo p, string group, GroupState gs)
        {
            ProcState st;
            if (procs.TryGetValue(p.Pid, out st) && st.CreateTime == p.CreateTime) return st;

            st = new ProcState { Pid = p.Pid, CreateTime = p.CreateTime, Name = p.Name, Group = group };
            procs[p.Pid] = st;
            if (p.Pid == selfPid) return st;

            IntPtr h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_INFORMATION, false, p.Pid);
            if (h == IntPtr.Zero) return st;
            try
            {
                bool inJob = false;
                if (gs.Job != IntPtr.Zero) NativeMethods.IsProcessInJob(h, gs.Job, out inJob);
                st.InJob = inJob;
                if (inJob || PerfOptions.IsManaged(p.Name)) Adopt(h, st);
            }
            finally
            {
                NativeMethods.CloseHandle(h);
            }
            return st;
        }

        private static void Adopt(IntPtr h, ProcState st)
        {
            uint priority = NativeMethods.GetPriorityClass(h);
            if (priority == NativeMethods.IDLE_PRIORITY_CLASS || priority == NativeMethods.BELOW_NORMAL_PRIORITY_CLASS)
            {
                st.PriorityChanged = true;
                st.OriginalPriority = NativeMethods.NORMAL_PRIORITY_CLASS;
            }

            int io, length;
            if (NativeMethods.NtQueryInformationProcess(h, NativeMethods.ProcessIoPriority, out io, sizeof(int), out length) == 0 && io < IoNormal)
            {
                st.IoChanged = true;
                st.OriginalIo = IoNormal;
            }

            UIntPtr min, max, processMask, systemMask;
            uint flags;
            if (NativeMethods.GetProcessWorkingSetSizeEx(h, out min, out max, out flags) && (flags & NativeMethods.QUOTA_LIMITS_HARDWS_MAX_ENABLE) != 0)
            {
                st.WorkingSetChanged = true;
                st.OriginalMinWs = min;
                st.OriginalMaxWs = max;
                st.OriginalWsFlags = 0; // restore = no hard limits
            }

            if (NativeMethods.GetProcessAffinityMask(h, out processMask, out systemMask) && processMask != systemMask && systemMask != UIntPtr.Zero)
            {
                st.AffinityChanged = true;
                st.OriginalAffinity = systemMask;
            }
        }

        private static bool UpdateHot(bool hot, double value, int limit)
        {
            if (limit <= 0) return false;
            if (value > limit) return true;
            if (value < limit * Hysteresis) return false;
            return hot;
        }

        /// <summary>
        /// The memory limit is a limit on the app's total, not on its processes. Nothing is capped, so
        /// Windows manages every process as usual and a single process may use the whole limit. This
        /// only checks the total; when it is over the limit, each process's working set is trimmed by
        /// the same share, so that the app as a whole comes back to just under its limit (95%). The
        /// trim is a one-time reduction and nothing stays capped: pages the process still needs come
        /// back by themselves.
        /// </summary>
        private void TrimToLimit(ProcessGroup g, long ramLimit)
        {
            double share = ramLimit * TrimTarget / Math.Max(1, g.PrivateBytes);
            foreach (ProcInfo p in g.Processes)
            {
                if (p.Pid == selfPid || ProtectedProcesses.IsCritical(p.Name) || p.WorkingSet < MinTrimBytes) continue;
                ProcState st;
                if (procs.TryGetValue(p.Pid, out st) && st.NoAccess) continue;
                IntPtr h = NativeMethods.OpenProcess(AccessRights, false, p.Pid);
                if (h == IntPtr.Zero) continue;
                try
                {
                    UIntPtr min, max;
                    uint flags;
                    if (!NativeMethods.GetProcessWorkingSetSizeEx(h, out min, out max, out flags)) continue;
                    if (min.ToUInt64() > 4UL * 1024 * 1024) min = new UIntPtr(204800);
                    ulong target = (ulong)Math.Max(1L * 1024 * 1024, (long)(p.WorkingSet * share));
                    if (NativeMethods.SetProcessWorkingSetSizeEx(h, new UIntPtr(204800), new UIntPtr(target),
                            NativeMethods.QUOTA_LIMITS_HARDWS_MIN_DISABLE | NativeMethods.QUOTA_LIMITS_HARDWS_MAX_ENABLE))
                    {
                        // Take the cap off again right away. The old default maximum can't be put back: it is
                        // tiny, and setting it trims the process far below the target. A huge soft maximum
                        // trims nothing and limits nothing.
                        NativeMethods.SetProcessWorkingSetSizeEx(h, min, new UIntPtr(1UL << 36),
                            NativeMethods.QUOTA_LIMITS_HARDWS_MIN_DISABLE | NativeMethods.QUOTA_LIMITS_HARDWS_MAX_DISABLE);
                    }
                }
                finally
                {
                    NativeMethods.CloseHandle(h);
                }
            }
        }

        private static bool NeedsWork(ProcState st, uint wantPriority, int wantCores, int wantIo, long wsMax)
        {
            return (wantPriority == 0 ? st.PriorityChanged : st.AppliedPriority != wantPriority)
                || (wantCores == 0 ? st.AffinityChanged : st.AppliedCores != wantCores)
                || (wantIo < 0 ? st.IoChanged : st.AppliedIo != wantIo)
                || (wsMax <= 0 ? st.WorkingSetChanged : st.AppliedWsMax != wsMax);
        }

        // ---------------- CPU hard cap (named job object) ----------------

        private static string JobName(string app)
        {
            return @"Local\TaskManagerPro.Job." + app.Replace('\\', '_');
        }

        /// <summary>
        /// Windows forgets a job's name as soon as the last handle to it closes, even while processes
        /// are still inside it. Giving the limited process itself a query-only handle to its own job
        /// keeps the name alive exactly as long as the app runs, so a later session (after Task Manager
        /// Pro was closed) can reopen the job and lift or change the cap. The handle grants nothing but
        /// read access to the job and closes automatically when the process exits.
        /// </summary>
        private static void KeepJobNameAlive(IntPtr job, IntPtr process)
        {
            IntPtr remote;
            NativeMethods.DuplicateHandle(NativeMethods.GetCurrentProcess(), job, process, out remote,
                NativeMethods.JOB_OBJECT_QUERY, false, 0);
        }

        /// <summary>Creates the app's job, or opens it if an earlier session left it alive.</summary>
        private static bool OpenJob(GroupState gs, string app)
        {
            if (gs.Job != IntPtr.Zero) return true;
            if (!gs.JobUsable) return false;
            gs.Job = NativeMethods.CreateJobObject(IntPtr.Zero, JobName(app));
            gs.JobPercent = -1; // unknown: always (re)set the cap once
            if (gs.Job == IntPtr.Zero) gs.JobUsable = false;
            return gs.Job != IntPtr.Zero;
        }

        private static bool SetJobCap(GroupState gs, int percent)
        {
            if (gs.Job == IntPtr.Zero) return false;
            if (gs.JobPercent == percent) return percent > 0;

            var info = new NativeMethods.JOBOBJECT_CPU_RATE_CONTROL_INFORMATION();
            if (percent > 0)
            {
                info.ControlFlags = NativeMethods.JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | NativeMethods.JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP;
                info.CpuRate = (uint)(Math.Max(LimitPolicy.BasicCpuPercent, Math.Min(100, percent)) * 100);
            }
            if (!SetJobCpuRate(gs.Job, ref info) && percent > 0)
            {
                // e.g. Windows 7: no CPU rate control. Keep the job as a marker, use the affinity fallback.
                gs.JobPercent = 0;
                return false;
            }
            gs.JobPercent = percent;
            return percent > 0;
        }

        /// <summary>
        /// Closes our handle. With <paramref name="removeCap"/> the cap is lifted first; processes stay in
        /// the job (they can't leave it), but a job without limits has no effect.
        /// </summary>
        private static void CloseJob(GroupState gs, bool removeCap)
        {
            if (gs.Job == IntPtr.Zero) return;
            if (removeCap)
            {
                var off = new NativeMethods.JOBOBJECT_CPU_RATE_CONTROL_INFORMATION();
                SetJobCpuRate(gs.Job, ref off);
            }
            NativeMethods.CloseHandle(gs.Job);
            gs.Job = IntPtr.Zero;
            gs.JobPercent = 0;
        }

        private static bool SetJobCpuRate(IntPtr job, ref NativeMethods.JOBOBJECT_CPU_RATE_CONTROL_INFORMATION info)
        {
            return NativeMethods.SetInformationJobObject(job, NativeMethods.JobObjectCpuRateControlInformation, ref info,
                Marshal.SizeOf(typeof(NativeMethods.JOBOBJECT_CPU_RATE_CONTROL_INFORMATION)));
        }

        // ---------------- Priority ----------------

        private static int PriorityRank(uint priorityClass)
        {
            switch (priorityClass)
            {
                case NativeMethods.IDLE_PRIORITY_CLASS: return 0;
                case NativeMethods.BELOW_NORMAL_PRIORITY_CLASS: return 1;
                case NativeMethods.NORMAL_PRIORITY_CLASS: return 2;
                case NativeMethods.ABOVE_NORMAL_PRIORITY_CLASS: return 3;
                case NativeMethods.HIGH_PRIORITY_CLASS: return 4;
                case NativeMethods.REALTIME_PRIORITY_CLASS: return 5;
                default: return 2;
            }
        }

        private static void ApplyPriority(IntPtr h, ProcState st, uint want)
        {
            if (want == 0)
            {
                if (st.PriorityChanged) NativeMethods.SetPriorityClass(h, st.OriginalPriority);
                st.PriorityChanged = false;
                st.AppliedPriority = 0;
                return;
            }
            if (st.AppliedPriority == want) return;
            st.AppliedPriority = want;

            if (!st.PriorityChanged)
            {
                uint current = NativeMethods.GetPriorityClass(h);
                if (current == 0 || PriorityRank(current) <= PriorityRank(want)) return; // already as low: never raise
                st.OriginalPriority = current;
            }
            if (NativeMethods.SetPriorityClass(h, want)) st.PriorityChanged = true;
        }

        // ---------------- CPU affinity (fallback) ----------------

        private static ulong KeepLowestBits(ulong mask, int count)
        {
            ulong result = 0;
            for (int bit = 0; bit < 64 && count > 0; bit++)
            {
                ulong b = 1UL << bit;
                if ((mask & b) != 0)
                {
                    result |= b;
                    count--;
                }
            }
            return result;
        }

        private static void ApplyAffinity(IntPtr h, ProcState st, int wantCores)
        {
            if (wantCores == 0)
            {
                if (st.AffinityChanged) NativeMethods.SetProcessAffinityMask(h, st.OriginalAffinity);
                st.AffinityChanged = false;
                st.AppliedCores = 0;
                return;
            }
            if (st.AppliedCores == wantCores) return;
            st.AppliedCores = wantCores;

            if (!st.AffinityChanged)
            {
                UIntPtr processMask, systemMask;
                if (!NativeMethods.GetProcessAffinityMask(h, out processMask, out systemMask) || processMask == UIntPtr.Zero) return;
                st.OriginalAffinity = processMask;
            }

            ulong original = st.OriginalAffinity.ToUInt64();
            ulong mask = KeepLowestBits(original, wantCores); // a subset of the cores it was already allowed
            if (mask == 0) return;
            if (mask == original)
            {
                if (st.AffinityChanged) NativeMethods.SetProcessAffinityMask(h, st.OriginalAffinity);
                st.AffinityChanged = false;
                return;
            }
            if (NativeMethods.SetProcessAffinityMask(h, new UIntPtr(mask))) st.AffinityChanged = true;
        }

        // ---------------- Disk I/O priority ----------------

        private static void ApplyIoPriority(IntPtr h, ProcState st, int want)
        {
            if (want < 0)
            {
                if (st.IoChanged)
                {
                    int original = st.OriginalIo;
                    NativeMethods.NtSetInformationProcess(h, NativeMethods.ProcessIoPriority, ref original, sizeof(int));
                }
                st.IoChanged = false;
                st.AppliedIo = -1;
                return;
            }
            if (st.AppliedIo == want) return;
            st.AppliedIo = want;

            if (!st.IoChanged)
            {
                int current, length;
                if (NativeMethods.NtQueryInformationProcess(h, NativeMethods.ProcessIoPriority, out current, sizeof(int), out length) != 0) return;
                if (current <= want) return; // already as low: never raise
                st.OriginalIo = current;
            }
            int value = want;
            if (NativeMethods.NtSetInformationProcess(h, NativeMethods.ProcessIoPriority, ref value, sizeof(int)) == 0) st.IoChanged = true;
        }

        // ---------------- Working set (RAM) ----------------

        /// <summary>Lifts a hard working-set cap that an earlier version put on the process (nothing sets one now).</summary>
        private static void ApplyWorkingSet(IntPtr h, ProcState st, long wsMax)
        {
            if (st.WorkingSetChanged) RestoreWorkingSet(h, st);
            st.WorkingSetChanged = false;
            st.AppliedWsMax = 0;
        }

        private static void RestoreWorkingSet(IntPtr h, ProcState st)
        {
            uint flags = ((st.OriginalWsFlags & NativeMethods.QUOTA_LIMITS_HARDWS_MIN_ENABLE) != 0
                             ? NativeMethods.QUOTA_LIMITS_HARDWS_MIN_ENABLE : NativeMethods.QUOTA_LIMITS_HARDWS_MIN_DISABLE)
                       | ((st.OriginalWsFlags & NativeMethods.QUOTA_LIMITS_HARDWS_MAX_ENABLE) != 0
                             ? NativeMethods.QUOTA_LIMITS_HARDWS_MAX_ENABLE : NativeMethods.QUOTA_LIMITS_HARDWS_MAX_DISABLE);
            NativeMethods.SetProcessWorkingSetSizeEx(h, st.OriginalMinWs, st.OriginalMaxWs, flags);
        }

        // ---------------- Restore ----------------

        private static void Restore(ProcState st)
        {
            if (!st.HasChanges) return;
            IntPtr h = NativeMethods.OpenProcessVerified(st.Pid, st.CreateTime, AccessRights);
            if (h == IntPtr.Zero) return; // exited (or PID reused): nothing to restore
            try
            {
                if (st.PriorityChanged) NativeMethods.SetPriorityClass(h, st.OriginalPriority);
                if (st.AffinityChanged) NativeMethods.SetProcessAffinityMask(h, st.OriginalAffinity);
                if (st.IoChanged)
                {
                    int original = st.OriginalIo;
                    NativeMethods.NtSetInformationProcess(h, NativeMethods.ProcessIoPriority, ref original, sizeof(int));
                }
                if (st.WorkingSetChanged) RestoreWorkingSet(h, st);
            }
            finally
            {
                NativeMethods.CloseHandle(h);
            }
        }
    }
}
