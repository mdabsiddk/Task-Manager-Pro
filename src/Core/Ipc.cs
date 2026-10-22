using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskManagerPro
{
    /// <summary>
    /// Names and helpers for talking between the background engine and the window (UI) process.
    ///   launcher / UI  → engine : Show, Exit, ConfigChanged, Pause, Resume   (named events)
    ///   engine         → UI     : UiActivate, UiClose                        (named events)
    ///   engine         → UI     : governor status of every limited app       (shared memory)
    /// </summary>
    internal static class Ipc
    {
        public const string EngineMutex = @"Local\TaskManagerPro.Engine";
        public const string ShowEvent = @"Local\TaskManagerPro.Show";
        public const string ExitEvent = @"Local\TaskManagerPro.Exit";
        public const string ConfigEvent = @"Local\TaskManagerPro.ConfigChanged";
        public const string PauseEvent = @"Local\TaskManagerPro.Pause";
        public const string ResumeEvent = @"Local\TaskManagerPro.Resume";
        public const string UiActivateEvent = @"Local\TaskManagerPro.UiActivate";
        public const string UiCloseEvent = @"Local\TaskManagerPro.UiClose";
        public const string StatusMapping = @"Local\TaskManagerPro.Status";

        /// <summary>
        /// Creates an auto-reset event. With <paramref name="openToStandardUsers"/> the event gets a
        /// Medium integrity label, so a non-elevated launcher can signal the elevated engine.
        /// </summary>
        public static IntPtr CreateEvent(string name, bool openToStandardUsers)
        {
            if (!openToStandardUsers) return NativeMethods.CreateEvent(IntPtr.Zero, false, false, name);

            IntPtr descriptor;
            uint size;
            // SYSTEM + Administrators + Authenticated Users: full access; mandatory label: Medium, no-write-up.
            if (!NativeMethods.ConvertStringSecurityDescriptorToSecurityDescriptor(
                    "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x1F0003;;;AU)S:(ML;;NW;;;ME)", 1, out descriptor, out size))
                return NativeMethods.CreateEvent(IntPtr.Zero, false, false, name);
            try
            {
                var sa = new NativeMethods.SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf(typeof(NativeMethods.SECURITY_ATTRIBUTES)),
                    lpSecurityDescriptor = descriptor
                };
                return NativeMethods.CreateEvent(ref sa, false, false, name);
            }
            finally
            {
                NativeMethods.LocalFree(descriptor);
            }
        }

        /// <summary>Signals an existing named event. Returns false if nobody created it.</summary>
        public static bool Signal(string name)
        {
            IntPtr h = NativeMethods.OpenEvent(NativeMethods.EVENT_MODIFY_STATE, false, name);
            if (h == IntPtr.Zero) return false;
            try
            {
                return NativeMethods.SetEvent(h);
            }
            finally
            {
                NativeMethods.CloseHandle(h);
            }
        }
    }

    /// <summary>
    /// Shared-memory channel the engine uses to publish governor state; the UI only reads it.
    /// Layout: [int seq][int paused][int charCount][chars "name\tstate\n"...]. A sequence lock
    /// (odd while writing) lets the reader detect torn reads without any kernel object.
    /// </summary>
    internal sealed class StatusChannel : IDisposable
    {
        private const int Size = 64 * 1024;
        private const int HeaderBytes = 12;
        private readonly IntPtr mapping;
        private readonly IntPtr view;
        private int seq;

        private StatusChannel(IntPtr mapping, IntPtr view)
        {
            this.mapping = mapping;
            this.view = view;
        }

        public static StatusChannel Create()
        {
            IntPtr m = NativeMethods.CreateFileMapping(new IntPtr(-1), IntPtr.Zero, NativeMethods.PAGE_READWRITE, 0, Size, Ipc.StatusMapping);
            if (m == IntPtr.Zero) return null;
            IntPtr v = NativeMethods.MapViewOfFile(m, NativeMethods.FILE_MAP_ALL_ACCESS, 0, 0, (UIntPtr)Size);
            if (v == IntPtr.Zero)
            {
                NativeMethods.CloseHandle(m);
                return null;
            }
            return new StatusChannel(m, v);
        }

        public static StatusChannel Open()
        {
            IntPtr m = NativeMethods.OpenFileMapping(NativeMethods.FILE_MAP_READ, false, Ipc.StatusMapping);
            if (m == IntPtr.Zero) return null;
            IntPtr v = NativeMethods.MapViewOfFile(m, NativeMethods.FILE_MAP_READ, 0, 0, (UIntPtr)Size);
            if (v == IntPtr.Zero)
            {
                NativeMethods.CloseHandle(m);
                return null;
            }
            return new StatusChannel(m, v);
        }

        public void Write(bool paused, Dictionary<string, GovernorState> states)
        {
            var sb = new StringBuilder();
            if (states != null)
            {
                foreach (var kv in states) sb.Append(kv.Key).Append('\t').Append((int)kv.Value).Append('\n');
            }
            int maxChars = (Size - HeaderBytes) / 2;
            if (sb.Length > maxChars) sb.Length = maxChars;
            char[] chars = sb.ToString().ToCharArray();

            seq |= 1;                                          // odd: write in progress
            Marshal.WriteInt32(view, 0, seq);
            Marshal.WriteInt32(view, 4, paused ? 1 : 0);
            Marshal.WriteInt32(view, 8, chars.Length);
            Marshal.Copy(chars, 0, new IntPtr(view.ToInt64() + HeaderBytes), chars.Length);
            seq++;                                             // even: consistent
            Marshal.WriteInt32(view, 0, seq);
        }

        /// <summary>Returns false if the engine is mid-write (try again next tick).</summary>
        public bool TryRead(out bool paused, out Dictionary<string, GovernorState> states)
        {
            paused = false;
            states = null;
            int before = Marshal.ReadInt32(view, 0);
            if ((before & 1) != 0) return false;

            paused = Marshal.ReadInt32(view, 4) != 0;
            int count = Math.Max(0, Math.Min((Size - HeaderBytes) / 2, Marshal.ReadInt32(view, 8)));
            var chars = new char[count];
            Marshal.Copy(new IntPtr(view.ToInt64() + HeaderBytes), chars, 0, count);
            if (Marshal.ReadInt32(view, 0) != before) return false;

            states = new Dictionary<string, GovernorState>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in new string(chars).Split('\n'))
            {
                int tab = line.IndexOf('\t');
                int state;
                if (tab > 0 && int.TryParse(line.Substring(tab + 1), out state))
                    states[line.Substring(0, tab)] = (GovernorState)state;
            }
            return true;
        }

        public void Dispose()
        {
            NativeMethods.UnmapViewOfFile(view);
            NativeMethods.CloseHandle(mapping);
        }
    }
}
