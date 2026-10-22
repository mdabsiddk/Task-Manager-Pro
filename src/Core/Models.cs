using System.Collections.Generic;

namespace TaskManagerPro
{
    /// <summary>
    /// Resource limits for an app (a process tree, keyed by the root process name, e.g. "chrome").
    /// 0 means "no limit". Instances are treated as immutable once stored in the RuleStore.
    /// </summary>
    public sealed class ProcessRule
    {
        public string Name;
        public int MaxCpuPercent;
        public int MaxRamMB;
        public int MaxDiskMBps;
        public int MaxNetKBps;

        public bool IsEmpty
        {
            get { return MaxCpuPercent <= 0 && MaxRamMB <= 0 && MaxDiskMBps <= 0 && MaxNetKBps <= 0; }
        }

        public bool SameLimits(ProcessRule other)
        {
            return other != null
                && MaxCpuPercent == other.MaxCpuPercent
                && MaxRamMB == other.MaxRamMB
                && MaxDiskMBps == other.MaxDiskMBps
                && MaxNetKBps == other.MaxNetKBps;
        }

        public ProcessRule Clone()
        {
            return (ProcessRule)MemberwiseClone();
        }
    }

    /// <summary>One running process and its usage during the last sampling interval.</summary>
    public sealed class ProcInfo
    {
        public int Pid;
        public int ParentPid;
        public long CreateTime;
        public uint SessionId;
        public string Name;
        public double CpuPercent;
        public long PrivateBytes;      // private working set: what Task Manager calls "Memory"
        public long WorkingSet;        // total working set: what the RAM limit caps
        public double DiskBytesPerSec;

        // Filled in by the UI only
        public double NetBytesPerSec;
        public bool HasWindow;
    }

    /// <summary>
    /// An "app" the way Task Manager groups it: a root process plus all of its descendants, merged
    /// with other trees that have the same root name. Limits apply to the whole group.
    /// </summary>
    public sealed class ProcessGroup
    {
        public string Name;
        public ProcInfo Root;
        public readonly List<ProcInfo> Processes = new List<ProcInfo>();
        public double CpuPercent;
        public long PrivateBytes;
        public long WorkingSet;
        public double DiskBytesPerSec;
        public double NetBytesPerSec;
        public bool HasWindow;
    }

    /// <summary>What the governor is currently doing to an app.</summary>
    public enum GovernorState
    {
        None = 0,
        Active = 1,
        Throttling = 2,
        NoAccess = 3
    }

    /// <summary>One sampling pass: every process plus system totals.</summary>
    public sealed class SampleResult
    {
        public List<ProcInfo> Processes;
        public double CpuPercent;
        public uint MemoryLoad;
        public ulong TotalPhysBytes;
        public ulong AvailPhysBytes;
    }
}
