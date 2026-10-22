using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TaskManagerPro
{
    /// <summary>
    /// Collects CPU, memory and disk usage for every process with a single NtQuerySystemInformation
    /// call (the technique used by System Informer). No per-process handles are opened, so sampling
    /// costs a few milliseconds and also works for protected processes. Use from one thread at a time.
    /// </summary>
    internal sealed class ProcessSampler : IDisposable
    {
        private struct PrevSample
        {
            public long CreateTime;
            public long CpuTime;
            public long IoBytes;
        }

        private static readonly Type EntryType = typeof(NativeMethods.SYSTEM_PROCESS_INFORMATION);

        private readonly int coreCount = Math.Max(1, Environment.ProcessorCount);
        private readonly long qpcFrequency;
        private Dictionary<int, PrevSample> prev = new Dictionary<int, PrevSample>();
        private IntPtr buffer = IntPtr.Zero;
        private int bufferSize;
        private long prevQpc = -1;
        private long prevSysIdle, prevSysBusy;

        public ProcessSampler()
        {
            NativeMethods.QueryPerformanceFrequency(out qpcFrequency);
            if (qpcFrequency <= 0) qpcFrequency = 1;
        }

        public SampleResult Sample()
        {
            if (!QueryProcesses()) return null;

            long now;
            NativeMethods.QueryPerformanceCounter(out now);
            double elapsedSec = prevQpc < 0 ? 0 : (now - prevQpc) / (double)qpcFrequency;
            prevQpc = now;
            // CPU times are in 100 ns units; capacity = all logical processors for the elapsed time.
            double cpuCapacity = elapsedSec * 1e7 * coreCount;

            var list = new List<ProcInfo>(prev.Count + 16);
            var next = new Dictionary<int, PrevSample>(prev.Count + 16);
            long offset = 0;

            while (true)
            {
                var info = (NativeMethods.SYSTEM_PROCESS_INFORMATION)Marshal.PtrToStructure(new IntPtr(buffer.ToInt64() + offset), EntryType);
                int pid = info.UniqueProcessId.ToInt32();

                if (pid != 0) // skip the System Idle Process
                {
                    long cpuTime = info.KernelTime + info.UserTime;
                    long ioBytes = info.ReadTransferCount + info.WriteTransferCount;
                    next[pid] = new PrevSample { CreateTime = info.CreateTime, CpuTime = cpuTime, IoBytes = ioBytes };

                    var p = new ProcInfo
                    {
                        Pid = pid,
                        ParentPid = info.InheritedFromUniqueProcessId.ToInt32(),
                        CreateTime = info.CreateTime,
                        SessionId = info.SessionId,
                        Name = GetName(ref info, pid),
                        PrivateBytes = info.WorkingSetPrivateSize,
                        WorkingSet = (long)info.WorkingSetSize.ToUInt64()
                    };

                    PrevSample last;
                    if (cpuCapacity > 0 && prev.TryGetValue(pid, out last) && last.CreateTime == info.CreateTime)
                    {
                        long cpuDelta = cpuTime - last.CpuTime;
                        if (cpuDelta > 0) p.CpuPercent = Math.Min(100.0, cpuDelta * 100.0 / cpuCapacity);
                        long ioDelta = ioBytes - last.IoBytes;
                        if (ioDelta > 0) p.DiskBytesPerSec = ioDelta / elapsedSec;
                    }
                    list.Add(p);
                }

                if (info.NextEntryOffset == 0) break;
                offset += info.NextEntryOffset;
            }
            prev = next;

            var result = new SampleResult { Processes = list, CpuPercent = SampleSystemCpu() };
            var mem = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(NativeMethods.MEMORYSTATUSEX)) };
            if (NativeMethods.GlobalMemoryStatusEx(ref mem))
            {
                result.MemoryLoad = mem.dwMemoryLoad;
                result.TotalPhysBytes = mem.ullTotalPhys;
                result.AvailPhysBytes = mem.ullAvailPhys;
            }
            return result;
        }

        private static string GetName(ref NativeMethods.SYSTEM_PROCESS_INFORMATION info, int pid)
        {
            if (info.ImageName.Buffer == IntPtr.Zero || info.ImageName.Length == 0)
                return pid == 4 ? "System" : "Unknown";

            string name = Marshal.PtrToStringUni(info.ImageName.Buffer, info.ImageName.Length / 2);
            // Same as Process.ProcessName (and limits.cfg): no ".exe" suffix.
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);
            return name;
        }

        private double SampleSystemCpu()
        {
            long idle, kernel, user;
            if (!NativeMethods.GetSystemTimes(out idle, out kernel, out user)) return 0;
            long busy = kernel + user - idle; // kernel time includes idle time
            long dIdle = idle - prevSysIdle, dBusy = busy - prevSysBusy;
            bool first = prevSysIdle == 0 && prevSysBusy == 0;
            prevSysIdle = idle;
            prevSysBusy = busy;
            if (first || dIdle + dBusy <= 0) return 0;
            return Math.Max(0, Math.Min(100, dBusy * 100.0 / (dIdle + dBusy)));
        }

        private bool QueryProcesses()
        {
            if (buffer == IntPtr.Zero)
            {
                bufferSize = 256 * 1024;
                buffer = Marshal.AllocHGlobal(bufferSize);
            }

            for (int attempt = 0; attempt < 8; attempt++)
            {
                int needed;
                uint status = NativeMethods.NtQuerySystemInformation(NativeMethods.SystemProcessInformation, buffer, bufferSize, out needed);
                if (status == 0) return true;
                if (status != NativeMethods.STATUS_INFO_LENGTH_MISMATCH) return false;

                // Grow with headroom: new processes/threads may appear before the next call.
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
                bufferSize = Math.Max(needed + 64 * 1024, bufferSize * 2);
                buffer = Marshal.AllocHGlobal(bufferSize);
            }
            return false;
        }

        public void Dispose()
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
            }
        }
    }
}
