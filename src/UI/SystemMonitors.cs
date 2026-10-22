using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TaskManagerPro
{
    /// <summary>
    /// Per-process network throughput from the Microsoft-Windows-Kernel-Network ETW provider (TCP and
    /// UDP send/receive events carry the PID and byte count), plus overall network utilisation for the
    /// column header. Runs only while the window is open.
    /// </summary>
    internal sealed class NetworkMonitor : IDisposable
    {
        private readonly object sync = new object();
        private EtwSession session;
        private Dictionary<int, long> bytes = new Dictionary<int, long>();
        private long lastQpc;
        private readonly long qpcFrequency;
        private readonly Dictionary<string, long> lastInterfaceBytes = new Dictionary<string, long>();
        private long lastInterfaceQpc;

        public NetworkMonitor()
        {
            NativeMethods.QueryPerformanceFrequency(out qpcFrequency);
            NativeMethods.QueryPerformanceCounter(out lastQpc);
            session = EtwSession.Start("TaskManagerPro-Network", EtwSession.KernelNetworkProvider, ulong.MaxValue, 4, 64, 32, 1000, OnEvent);
        }

        /// <summary>False if ETW could not be started (no admin rights / 32-bit): per-process values stay 0.</summary>
        public bool PerProcessAvailable
        {
            get { return session != null; }
        }

        private void OnEvent(ushort id, IntPtr data, int length)
        {
            switch (id)
            {
                case 10: case 11: case 26: case 27: // TCP send/receive IPv4/IPv6
                case 42: case 43: case 58: case 59: // UDP send/receive IPv4/IPv6
                    if (length < 8) return;
                    int pid = Marshal.ReadInt32(data, 0);
                    int size = Marshal.ReadInt32(data, 4);
                    if (size <= 0) return;
                    lock (sync)
                    {
                        long total;
                        bytes.TryGetValue(pid, out total);
                        bytes[pid] = total + size;
                    }
                    break;
            }
        }

        /// <summary>Bytes per second per PID since the previous call.</summary>
        public Dictionary<int, double> TakeRates()
        {
            Dictionary<int, long> taken;
            lock (sync)
            {
                taken = bytes;
                bytes = new Dictionary<int, long>();
            }
            long now;
            NativeMethods.QueryPerformanceCounter(out now);
            double seconds = Math.Max(0.001, (now - lastQpc) / (double)qpcFrequency);
            lastQpc = now;

            var rates = new Dictionary<int, double>(taken.Count);
            foreach (var kv in taken) rates[kv.Key] = kv.Value / seconds;
            return rates;
        }

        /// <summary>Highest utilisation (%) of any active network adapter, like Task Manager's header.</summary>
        public double InterfaceUtilization()
        {
            double best = 0;
            try
            {
                long now;
                NativeMethods.QueryPerformanceCounter(out now);
                double seconds = lastInterfaceQpc == 0 ? 0 : (now - lastInterfaceQpc) / (double)qpcFrequency;
                lastInterfaceQpc = now;

                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback
                        || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel || ni.Speed <= 0)
                        continue;
                    IPInterfaceStatistics stats = ni.GetIPStatistics();
                    long total = stats.BytesReceived + stats.BytesSent;
                    long previous;
                    if (seconds > 0 && lastInterfaceBytes.TryGetValue(ni.Id, out previous) && total >= previous)
                    {
                        double bitsPerSecond = (total - previous) * 8.0 / seconds;
                        best = Math.Max(best, bitsPerSecond * 100.0 / ni.Speed);
                    }
                    lastInterfaceBytes[ni.Id] = total;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Network utilisation", ex);
            }
            return Math.Min(100, best);
        }

        public void Dispose()
        {
            if (session != null) session.Dispose();
            session = null;
        }
    }

    /// <summary>
    /// Disk "active time" (%) like Task Manager's Disk header: the busiest physical disk, from the
    /// disk driver's own performance counters (IOCTL_DISK_PERFORMANCE: idle time vs. elapsed time).
    /// </summary>
    internal sealed class DiskMonitor : IDisposable
    {
        private const uint IOCTL_DISK_PERFORMANCE = 0x70020;
        private readonly List<SafeFileHandle> disks = new List<SafeFileHandle>();
        private readonly Dictionary<int, long[]> previous = new Dictionary<int, long[]>();
        private readonly IntPtr buffer = Marshal.AllocHGlobal(256);

        public DiskMonitor()
        {
            for (int i = 0; i < 32; i++)
            {
                SafeFileHandle h = CreateFile(@"\\.\PhysicalDrive" + i, 0, 3 /* share read|write */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
                if (h.IsInvalid)
                {
                    h.Dispose();
                    continue;
                }
                disks.Add(h);
            }
        }

        public double ActivePercent()
        {
            double best = 0;
            for (int i = 0; i < disks.Count; i++)
            {
                int returned;
                if (!DeviceIoControl(disks[i], IOCTL_DISK_PERFORMANCE, IntPtr.Zero, 0, buffer, 256, out returned, IntPtr.Zero) || returned < 64)
                    continue;
                long idle = Marshal.ReadInt64(buffer, 32);   // DISK_PERFORMANCE.IdleTime
                long query = Marshal.ReadInt64(buffer, 56);  // DISK_PERFORMANCE.QueryTime
                long[] last;
                if (previous.TryGetValue(i, out last) && query > last[1])
                {
                    double active = 100.0 * (1.0 - (idle - last[0]) / (double)(query - last[1]));
                    best = Math.Max(best, Math.Max(0, Math.Min(100, active)));
                }
                previous[i] = new[] { idle, query };
            }
            return best;
        }

        public void Dispose()
        {
            foreach (SafeFileHandle h in disks) h.Dispose();
            disks.Clear();
            Marshal.FreeHGlobal(buffer);
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, int inSize,
            IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);
    }

    /// <summary>
    /// Finds processes that own a real app window (visible, unowned, not a tool window, not cloaked,
    /// with a title) in one EnumWindows pass.
    /// </summary>
    internal static class WindowFinder
    {
        public static List<KeyValuePair<IntPtr, int>> GetAppWindows()
        {
            var result = new List<KeyValuePair<IntPtr, int>>();
            EnumWindows((hWnd, lParam) =>
            {
                if (IsAppWindow(hWnd))
                {
                    int pid;
                    GetWindowThreadProcessId(hWnd, out pid);
                    if (pid != 0) result.Add(new KeyValuePair<IntPtr, int>(hWnd, pid));
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        public static HashSet<int> GetAppWindowProcessIds()
        {
            var pids = new HashSet<int>();
            foreach (var w in GetAppWindows()) pids.Add(w.Value);
            return pids;
        }

        /// <summary>Restores and focuses the first window of the given processes ("Switch to").</summary>
        public static bool SwitchTo(ICollection<int> pids)
        {
            foreach (var w in GetAppWindows())
            {
                if (!pids.Contains(w.Value)) continue;
                if (IsIconic(w.Key)) ShowWindow(w.Key, 9 /* SW_RESTORE */);
                return SetForegroundWindow(w.Key);
            }
            return false;
        }

        public static int CloseWindows(ICollection<int> pids)
        {
            int closed = 0;
            foreach (var w in GetAppWindows())
            {
                if (pids.Contains(w.Value) && PostMessage(w.Key, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero)) closed++;
            }
            return closed;
        }

        private static bool IsAppWindow(IntPtr hWnd)
        {
            if (!IsWindowVisible(hWnd)) return false;
            if (GetWindow(hWnd, 4 /* GW_OWNER */) != IntPtr.Zero) return false;
            if ((GetWindowLong(hWnd, -20 /* GWL_EXSTYLE */) & 0x80 /* WS_EX_TOOLWINDOW */) != 0) return false;
            if (GetWindowTextLength(hWnd) == 0) return false;
            int cloaked;
            if (DwmGetWindowAttribute(hWnd, 14 /* DWMWA_CLOAKED */, out cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
            return true;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int cmd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);
    }
}
