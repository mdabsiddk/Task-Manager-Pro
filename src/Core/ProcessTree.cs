using System;
using System.Collections.Generic;

namespace TaskManagerPro
{
    /// <summary>
    /// Groups processes into apps the way Task Manager does: each process belongs to the tree of its
    /// top-most ancestor that is not a Windows shell/service host. So Chrome's renderers, or the
    /// PowerShell and Console Window Host started by an app, appear under that app. Trees whose roots
    /// have the same name are merged (e.g. two chrome.exe roots = one "chrome" group).
    /// </summary>
    internal static class ProcessTree
    {
        // Parents that start unrelated programs: grouping never climbs above them.
        private static readonly Dictionary<string, bool> Boundaries = ProtectedProcesses.ToSet(
            "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "svchost", "explorer",
            "sihost", "userinit", "taskhostw", "RuntimeBroker", "dllhost", "WmiPrvSE", "ShellExperienceHost",
            "StartMenuExperienceHost", "ApplicationFrameHost", "Memory Compression", "Secure System", "fontdrvhost",
            "dwm", "LogonUI", "SearchHost", "ctfmon", "lsass", "Unknown");

        public static List<ProcessGroup> Group(List<ProcInfo> processes)
        {
            var byPid = new Dictionary<int, ProcInfo>(processes.Count);
            foreach (ProcInfo p in processes) byPid[p.Pid] = p;

            var groups = new Dictionary<string, ProcessGroup>(StringComparer.OrdinalIgnoreCase);
            var result = new List<ProcessGroup>();

            foreach (ProcInfo p in processes)
            {
                ProcInfo root = FindRoot(p, byPid);
                ProcessGroup g;
                if (!groups.TryGetValue(root.Name, out g))
                {
                    g = new ProcessGroup { Name = root.Name, Root = root };
                    groups.Add(root.Name, g);
                    result.Add(g);
                }
                else if (root != g.Root && root.CreateTime < g.Root.CreateTime)
                {
                    g.Root = root; // the oldest root represents the app
                }

                g.Processes.Add(p);
                g.CpuPercent += p.CpuPercent;
                g.PrivateBytes += p.PrivateBytes;
                g.WorkingSet += p.WorkingSet;
                g.DiskBytesPerSec += p.DiskBytesPerSec;
                g.NetBytesPerSec += p.NetBytesPerSec;
                if (p.HasWindow) g.HasWindow = true;
            }

            foreach (ProcessGroup g in result)
            {
                if (g.CpuPercent > 100) g.CpuPercent = 100;
            }
            return result;
        }

        private static ProcInfo FindRoot(ProcInfo p, Dictionary<int, ProcInfo> byPid)
        {
            ProcInfo current = p;
            for (int depth = 0; depth < 64; depth++)
            {
                if (Boundaries.ContainsKey(current.Name)) return current;
                ProcInfo parent;
                // A parent must be older than its child, otherwise the parent PID was reused.
                if (!byPid.TryGetValue(current.ParentPid, out parent) || parent == current
                    || parent.CreateTime > current.CreateTime || parent.SessionId != current.SessionId
                    || Boundaries.ContainsKey(parent.Name))
                {
                    return current;
                }
                current = parent;
            }
            return current;
        }
    }
}
