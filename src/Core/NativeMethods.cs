using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskManagerPro
{
    /// <summary>
    /// Win32 / NT declarations shared by the engine and the UI. Only mscorlib types are used here so
    /// the background engine never has to load System.dll or WinForms.
    /// </summary>
    internal static class NativeMethods
    {
        // ---- Process access rights ----
        public const uint PROCESS_TERMINATE = 0x0001;
        public const uint PROCESS_SET_QUOTA = 0x0100;
        public const uint PROCESS_SET_INFORMATION = 0x0200;
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint SYNCHRONIZE = 0x00100000;

        // ---- Priority classes ----
        public const uint IDLE_PRIORITY_CLASS = 0x00000040;
        public const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
        public const uint NORMAL_PRIORITY_CLASS = 0x00000020;
        public const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;
        public const uint HIGH_PRIORITY_CLASS = 0x00000080;
        public const uint REALTIME_PRIORITY_CLASS = 0x00000100;

        // ---- Working set quota flags ----
        public const uint QUOTA_LIMITS_HARDWS_MIN_ENABLE = 0x1;
        public const uint QUOTA_LIMITS_HARDWS_MIN_DISABLE = 0x2;
        public const uint QUOTA_LIMITS_HARDWS_MAX_ENABLE = 0x4;
        public const uint QUOTA_LIMITS_HARDWS_MAX_DISABLE = 0x8;

        // ---- NT information classes ----
        public const int SystemProcessInformation = 5;
        public const int ProcessIoPriority = 33;
        public const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;

        // ---- Job objects (CPU hard cap) ----
        public const int JobObjectCpuRateControlInformation = 15;
        public const uint JOB_OBJECT_CPU_RATE_CONTROL_ENABLE = 0x1;
        public const uint JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x4;

        // ---- Synchronization / misc ----
        public const uint EVENT_MODIFY_STATE = 0x0002;
        public const uint EVENT_ALL_ACCESS = 0x1F0003;
        public const uint INFINITE = 0xFFFFFFFF;
        public const uint WAIT_OBJECT_0 = 0;
        public const uint WAIT_TIMEOUT = 258;
        public const int ERROR_ACCESS_DENIED = 5;
        public const int ERROR_ALREADY_EXISTS = 183;
        public const uint PAGE_READWRITE = 0x04;
        public const uint FILE_MAP_ALL_ACCESS = 0xF001F;
        public const uint FILE_MAP_READ = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        public struct UNICODE_STRING
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        /// <summary>
        /// One entry of NtQuerySystemInformation(SystemProcessInformation): CPU times, memory and I/O
        /// counters for every process in a single call, without opening any process handle (the same
        /// technique Task Manager and System Informer use). 256 bytes on x64, 184 bytes on x86.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_PROCESS_INFORMATION
        {
            public uint NextEntryOffset;
            public uint NumberOfThreads;
            public long WorkingSetPrivateSize;
            public uint HardFaultCount;
            public uint NumberOfThreadsHighWatermark;
            public ulong CycleTime;
            public long CreateTime;
            public long UserTime;
            public long KernelTime;
            public UNICODE_STRING ImageName;
            public int BasePriority;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
            public uint HandleCount;
            public uint SessionId;
            public UIntPtr UniqueProcessKey;
            public UIntPtr PeakVirtualSize;
            public UIntPtr VirtualSize;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivatePageCount;
            public long ReadOperationCount;
            public long WriteOperationCount;
            public long OtherOperationCount;
            public long ReadTransferCount;
            public long WriteTransferCount;
            public long OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            public uint ControlFlags;
            public uint CpuRate; // 1/100 of a percent of total CPU (10000 = 100%)
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int cb;
            public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            public string lpVerb, lpFile, lpParameters, lpDirectory;
            public int nShow;
            public IntPtr hInstApp, lpIDList;
            public string lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon, hProcess;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)] // LUID is only 4-byte aligned
        private struct TOKEN_PRIVILEGES
        {
            public int PrivilegeCount;
            public long Luid;
            public int Attributes;
        }

        // ---- ntdll ----
        [DllImport("ntdll.dll")]
        public static extern uint NtQuerySystemInformation(int infoClass, IntPtr info, int infoLength, out int returnLength);

        [DllImport("ntdll.dll")]
        public static extern int NtQueryInformationProcess(IntPtr hProcess, int infoClass, out int info, int infoLength, out int returnLength);

        [DllImport("ntdll.dll")]
        public static extern int NtSetInformationProcess(IntPtr hProcess, int infoClass, ref int info, int infoLength);

        // ---- kernel32: processes ----
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(IntPtr hProcess, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessTimes(IntPtr hProcess, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint GetPriorityClass(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetPriorityClass(IntPtr hProcess, uint priorityClass);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessAffinityMask(IntPtr hProcess, out UIntPtr processMask, out UIntPtr systemMask);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessAffinityMask(IntPtr hProcess, UIntPtr processMask);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessWorkingSetSizeEx(IntPtr hProcess, out UIntPtr minimum, out UIntPtr maximum, out uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessWorkingSetSizeEx(IntPtr hProcess, UIntPtr minimum, UIntPtr maximum, uint flags);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder exeName, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        public static extern int GetCurrentProcessId();

        [DllImport("kernel32.dll")]
        public static extern bool QueryPerformanceCounter(out long value);

        [DllImport("kernel32.dll")]
        public static extern bool QueryPerformanceFrequency(out long value);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
            ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr min, IntPtr max);

        // ---- kernel32: jobs ----
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateJobObject(IntPtr jobAttributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool IsProcessInJob(IntPtr hProcess, IntPtr hJob, out bool result);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JOBOBJECT_CPU_RATE_CONTROL_INFORMATION info, int length);

        public const uint PROCESS_DUP_HANDLE = 0x0040;
        public const uint JOB_OBJECT_QUERY = 0x0004;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess,
            out IntPtr targetHandle, uint desiredAccess, bool inheritHandle, uint options);

        // ---- kernel32: events & shared memory ----
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateEvent(ref SECURITY_ATTRIBUTES attributes, bool manualReset, bool initialState, string name);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string name);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr OpenEvent(uint access, bool inherit, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetEvent(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFileMapping(IntPtr file, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, string name);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr OpenFileMapping(uint access, bool inherit, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UnmapViewOfFile(IntPtr address);

        [DllImport("kernel32.dll")]
        public static extern IntPtr LocalFree(IntPtr memory);

        // ---- advapi32: security ----
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string system, string name, out long luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, int length, IntPtr previous, IntPtr returnLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returnLength);

        // ---- shell / user ----
        [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(int processId);

        // ---- psapi ----
        [DllImport("psapi.dll", SetLastError = true)]
        public static extern bool EmptyWorkingSet(IntPtr hProcess);

        /// <summary>
        /// Opens a process only if it is still the same process (PID + creation time), so a PID that
        /// Windows recycled for a new process is never touched by mistake.
        /// </summary>
        public static IntPtr OpenProcessVerified(int pid, long createTime, uint access)
        {
            IntPtr h = OpenProcess(access | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return IntPtr.Zero;
            long creation, exit, kernel, user;
            if (createTime != 0 && GetProcessTimes(h, out creation, out exit, out kernel, out user) && creation != createTime)
            {
                CloseHandle(h);
                return IntPtr.Zero;
            }
            return h;
        }

        public static string GetImagePath(int pid, long createTime)
        {
            IntPtr h = OpenProcessVerified(pid, createTime, 0);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        public static bool IsElevated()
        {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), 0x0008 /* TOKEN_QUERY */, out token)) return false;
            try
            {
                int elevated, length;
                return GetTokenInformation(token, 20 /* TokenElevation */, out elevated, sizeof(int), out length) && elevated != 0;
            }
            finally
            {
                CloseHandle(token);
            }
        }

        /// <summary>Enables SeDebugPrivilege (like Task Manager) so other users' processes can be managed.</summary>
        public static void EnableDebugPrivilege()
        {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008 /* ADJUST_PRIVILEGES | QUERY */, out token)) return;
            try
            {
                var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Attributes = 0x2 /* SE_PRIVILEGE_ENABLED */ };
                if (LookupPrivilegeValue(null, "SeDebugPrivilege", out tp.Luid))
                    AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally
            {
                CloseHandle(token);
            }
        }

        /// <summary>Gives unused memory pages back to Windows (they come back on demand).</summary>
        public static void TrimWorkingSet()
        {
            SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
        }

        /// <summary>Starts a process (optionally waiting for its exit code) without System.Diagnostics.</summary>
        public static int RunProcess(string exe, string arguments, bool wait, int timeoutMs, out IntPtr processHandle, out int pid)
        {
            processHandle = IntPtr.Zero;
            pid = 0;
            var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)), dwFlags = 0x1 /* USESHOWWINDOW */, wShowWindow = 0 };
            PROCESS_INFORMATION pi;
            var cmd = new StringBuilder("\"" + exe + "\" " + arguments);
            uint flags = wait ? 0x08000000u /* CREATE_NO_WINDOW */ : 0u;
            if (!CreateProcess(exe, cmd, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, null, ref si, out pi))
                return -1;
            CloseHandle(pi.hThread);
            pid = pi.dwProcessId;
            if (!wait)
            {
                processHandle = pi.hProcess;
                return 0;
            }
            try
            {
                if (WaitForSingleObject(pi.hProcess, (uint)timeoutMs) != WAIT_OBJECT_0) return -1;
                uint code;
                return GetExitCodeProcess(pi.hProcess, out code) ? (int)code : -1;
            }
            finally
            {
                CloseHandle(pi.hProcess);
            }
        }
    }
}
