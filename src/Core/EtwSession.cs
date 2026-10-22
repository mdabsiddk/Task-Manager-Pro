using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TaskManagerPro
{
    /// <summary>
    /// A minimal real-time ETW (Event Tracing for Windows) consumer for one provider, using only
    /// mscorlib + advapi32. ETW is how Windows itself reports process starts and per-process network
    /// traffic: events are pushed to us, so nothing has to be polled. Requires administrator rights
    /// and a 64-bit process (the struct offsets below are for x64); Start returns null otherwise.
    /// </summary>
    internal sealed class EtwSession : IDisposable
    {
        public delegate void EventHandler(ushort eventId, IntPtr userData, int userDataLength);

        public static readonly Guid KernelProcessProvider = new Guid("22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716");
        public static readonly Guid KernelNetworkProvider = new Guid("7DD42A49-5329-4832-8DFD-43D979153A88");

        private const int PropertiesSize = 120;     // EVENT_TRACE_PROPERTIES (x64)
        private const int NameBytes = 1024;
        private const int LogFileSize = 448;        // EVENT_TRACE_LOGFILEW (x64)
        private const uint ERROR_ALREADY_EXISTS = 183;
        private const ulong InvalidTraceHandle = ulong.MaxValue;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void EventRecordCallback(IntPtr eventRecord);

        private readonly string name;
        private readonly EventHandler handler;
        private readonly EventRecordCallback callback; // field: must stay alive while ETW calls it
        private ulong session;
        private ulong trace = InvalidTraceHandle;
        private IntPtr logFile, namePtr;
        private Thread thread;

        private EtwSession(string name, EventHandler handler)
        {
            this.name = name;
            this.handler = handler;
            callback = OnEvent;
        }

        /// <param name="bufferKb">Size of each kernel buffer (KB).</param>
        /// <param name="maxBuffers">Buffers the kernel may use before dropping events (more for busy providers).</param>
        /// <param name="flushMs">How often buffers are delivered (ms). Under 1000 uses EVENT_TRACE_USE_MS_FLUSH_TIMER (Windows 8+).</param>
        public static EtwSession Start(string name, Guid provider, ulong keywords, byte level, int bufferKb, int maxBuffers, int flushMs, EventHandler handler)
        {
            if (IntPtr.Size != 8) return null;
            var s = new EtwSession(name, handler);
            try
            {
                if (s.Open(provider, keywords, level, bufferKb, maxBuffers, flushMs)) return s;
            }
            catch (Exception ex)
            {
                Log.Error("ETW " + name, ex);
            }
            s.Dispose();
            return null;
        }

        private bool Open(Guid provider, ulong keywords, byte level, int bufferKb, int maxBuffers, int flushMs)
        {
            IntPtr props = NewProperties(bufferKb, maxBuffers, flushMs);
            try
            {
                uint err = StartTrace(out session, name, props);
                if (err == ERROR_ALREADY_EXISTS)
                {
                    // Left over from a previous run that was killed: stop it and start fresh.
                    ControlTrace(0, name, NewProperties(props, bufferKb, maxBuffers, flushMs), 1 /* STOP */);
                    err = StartTrace(out session, name, NewProperties(props, bufferKb, maxBuffers, flushMs));
                }
                if (err != 0)
                {
                    session = 0;
                    Log.Write("ETW " + name + ": StartTrace failed (" + err + ").");
                    return false;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(props);
            }

            uint enable = EnableTraceEx2(session, ref provider, 1 /* ENABLE_PROVIDER */, level, keywords, 0, 0, IntPtr.Zero);
            if (enable != 0)
            {
                Log.Write("ETW " + name + ": EnableTraceEx2 failed (" + enable + ").");
                return false;
            }

            logFile = Marshal.AllocHGlobal(LogFileSize);
            for (int i = 0; i < LogFileSize; i += 8) Marshal.WriteInt64(logFile, i, 0);
            namePtr = Marshal.StringToHGlobalUni(name);
            Marshal.WriteIntPtr(logFile, 8, namePtr);                                     // LoggerName
            Marshal.WriteInt32(logFile, 28, 0x00000100 | 0x10000000);                    // REAL_TIME | EVENT_RECORD
            Marshal.WriteIntPtr(logFile, 424, Marshal.GetFunctionPointerForDelegate(callback)); // EventRecordCallback

            trace = OpenTrace(logFile);
            if (trace == InvalidTraceHandle)
            {
                Log.Write("ETW " + name + ": OpenTrace failed (" + Marshal.GetLastWin32Error() + ").");
                return false;
            }

            thread = new Thread(Pump) { IsBackground = true, Name = "ETW " + name };
            thread.Start();
            return true;
        }

        private void Pump()
        {
            var handles = new[] { trace };
            ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero); // returns when the session stops
        }

        private void OnEvent(IntPtr record)
        {
            try
            {
                // EVENT_RECORD: EventHeader.EventDescriptor.Id @40, UserDataLength @86, UserData @96
                ushort id = (ushort)Marshal.ReadInt16(record, 40);
                int length = (ushort)Marshal.ReadInt16(record, 86);
                IntPtr data = Marshal.ReadIntPtr(record, 96);
                if (data != IntPtr.Zero) handler(id, data, length);
            }
            catch
            {
                // Never let an exception escape into the ETW callback.
            }
        }

        private static IntPtr NewProperties(int bufferKb, int maxBuffers, int flushMs)
        {
            return NewProperties(Marshal.AllocHGlobal(PropertiesSize + 2 * NameBytes), bufferKb, maxBuffers, flushMs);
        }

        /// <summary>Fills an EVENT_TRACE_PROPERTIES block for a small real-time session.</summary>
        private static IntPtr NewProperties(IntPtr p, int bufferKb, int maxBuffers, int flushMs)
        {
            int total = PropertiesSize + 2 * NameBytes;
            for (int i = 0; i < total; i += 4) Marshal.WriteInt32(p, i, 0);
            Marshal.WriteInt32(p, 0, total);          // Wnode.BufferSize
            Marshal.WriteInt32(p, 40, 1);             // Wnode.ClientContext: QPC timestamps
            Marshal.WriteInt32(p, 44, 0x00020000);    // Wnode.Flags: WNODE_FLAG_TRACED_GUID
            Marshal.WriteInt32(p, 48, bufferKb);      // BufferSize (KB)
            Marshal.WriteInt32(p, 52, 2);             // MinimumBuffers
            Marshal.WriteInt32(p, 56, Math.Max(2, maxBuffers)); // MaximumBuffers
            bool msTimer = flushMs < 1000;
            // LogFileMode: EVENT_TRACE_REAL_TIME_MODE (+ EVENT_TRACE_USE_MS_FLUSH_TIMER for sub-second delivery)
            Marshal.WriteInt32(p, 64, 0x00000100 | (msTimer ? 0x00000010 : 0));
            Marshal.WriteInt32(p, 68, msTimer ? Math.Max(1, flushMs) : flushMs / 1000); // FlushTimer (ms or s)
            Marshal.WriteInt32(p, 112, PropertiesSize + NameBytes); // LogFileNameOffset
            Marshal.WriteInt32(p, 116, PropertiesSize);             // LoggerNameOffset
            return p;
        }

        public void Dispose()
        {
            if (session != 0)
            {
                IntPtr props = NewProperties(16, 2, 1000);
                ControlTrace(session, null, props, 1 /* STOP */); // ProcessTrace then returns
                Marshal.FreeHGlobal(props);
                session = 0;
            }
            if (trace != InvalidTraceHandle)
            {
                CloseTrace(trace);
                trace = InvalidTraceHandle;
            }
            if (thread != null) thread.Join(2000);
            if (logFile != IntPtr.Zero) Marshal.FreeHGlobal(logFile);
            if (namePtr != IntPtr.Zero) Marshal.FreeHGlobal(namePtr);
            logFile = namePtr = IntPtr.Zero;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "StartTraceW")]
        private static extern uint StartTrace(out ulong sessionHandle, string sessionName, IntPtr properties);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "ControlTraceW")]
        private static extern uint ControlTrace(ulong sessionHandle, string sessionName, IntPtr properties, uint controlCode);

        [DllImport("advapi32.dll")]
        private static extern uint EnableTraceEx2(ulong sessionHandle, ref Guid providerId, uint controlCode, byte level,
            ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

        [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "OpenTraceW")]
        private static extern ulong OpenTrace(IntPtr logFile);

        [DllImport("advapi32.dll")]
        private static extern uint ProcessTrace(ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

        [DllImport("advapi32.dll")]
        private static extern uint CloseTrace(ulong traceHandle);
    }
}
