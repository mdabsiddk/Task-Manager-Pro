using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace TaskManagerPro
{
    /// <summary>
    /// File locations. Data lives in %ProgramData%\TaskManagerPro because the program folder is
    /// read-only during normal use.
    /// </summary>
    internal static class AppPaths
    {
        public static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TaskManagerPro");

        public static readonly string RulesFile = Path.Combine(DataDir, "limits.cfg");
        public static readonly string SettingsFile = Path.Combine(DataDir, "settings.cfg");
        public static readonly string StateFile = Path.Combine(DataDir, "state.cfg");
        public static readonly string ProfilesFile = Path.Combine(DataDir, "profiles.cfg");
        public static readonly string LogFile = Path.Combine(DataDir, "error.log");

        /// <summary>Where earlier versions (Smart Task Manager) kept their limits.</summary>
        public static readonly string[] LegacyRulesFiles =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"SmartTaskManager\limits.cfg"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"SmartTaskManager\limits.cfg"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "limits.cfg")
        };

        public static readonly string SealKeyFile = Path.Combine(DataDir, "seal.key");

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly object secureLock = new object();
        private static bool secured;
        private static byte[] sealKey;

        public static void EnsureDataDir()
        {
            Directory.CreateDirectory(DataDir);
            Secure();
        }

        /// <summary>
        /// Locks the data folder: only SYSTEM and Administrators may read or change anything in it
        /// (no inherited "Users may create files" from ProgramData). So a program running without
        /// administrator rights - typical malware - can't change the limits, read the seal key or
        /// plant files here. Applied once per process, and only by an elevated process.
        /// </summary>
        private static void Secure()
        {
            lock (secureLock)
            {
                if (secured || !NativeMethods.IsElevated()) return;
                secured = true;
                try
                {
                    var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                    var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                    // Never locked before: anything already in here (including a seal key) may have been
                    // planted by a non-admin program, so the key is not trusted and gets replaced.
                    if (!Directory.GetAccessControl(DataDir).AreAccessRulesProtected && File.Exists(SealKeyFile))
                        File.Delete(SealKeyFile);

                    var acl = new DirectorySecurity();
                    acl.SetAccessRuleProtection(true, false); // drop everything inherited from ProgramData
                    const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                    acl.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
                    acl.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
                    acl.SetOwner(admins);
                    Directory.SetAccessControl(DataDir, acl);

                    // Files planted before the folder was locked could carry their own permissions: reset them.
                    foreach (string file in Directory.GetFiles(DataDir))
                    {
                        var fileAcl = new FileSecurity();
                        fileAcl.SetAccessRuleProtection(false, false); // inherit the folder's rules only
                        fileAcl.SetOwner(admins);
                        File.SetAccessControl(file, fileAcl);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Secure data folder", ex);
                }
            }
        }

        /// <summary>The secret key for the limits.cfg seal (created on first use, admin-only folder).</summary>
        public static byte[] GetSealKey()
        {
            lock (secureLock)
            {
                if (sealKey != null) return sealKey;
            }
            EnsureDataDir();
            lock (secureLock)
            {
                if (sealKey != null) return sealKey;
                if (File.Exists(SealKeyFile))
                {
                    byte[] existing = File.ReadAllBytes(SealKeyFile);
                    if (existing.Length == 32) return sealKey = existing;
                }
                var key = new byte[32];
                using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(key);
                File.WriteAllBytes(SealKeyFile, key);
                return sealKey = key;
            }
        }

        /// <summary>Writes to a temp file first, so a crash or power cut never leaves a half-written file.</summary>
        public static void WriteAllLinesAtomic(string path, IEnumerable<string> lines)
        {
            EnsureDataDir();
            string temp = path + ".tmp";
            File.WriteAllLines(temp, lines, Utf8NoBom);
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(temp, path, null);
                    return;
                }
                catch (IOException)
                {
                    File.Delete(path);
                }
            }
            File.Move(temp, path);
        }
    }

    /// <summary>Simple key=value file. Unknown keys are kept, so older/newer versions can share a file.</summary>
    internal class KeyValueFile
    {
        private readonly string path;
        private readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        protected KeyValueFile(string path)
        {
            this.path = path;
            try
            {
                if (!File.Exists(path)) return;
                foreach (string line in File.ReadAllLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0 || line.StartsWith("#")) continue;
                    values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Load " + path, ex);
            }
        }

        public void Save()
        {
            try
            {
                var lines = new List<string> { "# Task Manager Pro" };
                foreach (var kv in values) lines.Add(kv.Key + "=" + kv.Value);
                AppPaths.WriteAllLinesAtomic(path, lines);
            }
            catch (Exception ex)
            {
                Log.Error("Save " + path, ex);
            }
        }

        protected string GetString(string key, string fallback)
        {
            string v;
            return values.TryGetValue(key, out v) ? v : fallback;
        }

        protected void SetString(string key, string value)
        {
            values[key] = value ?? "";
        }

        protected bool GetBool(string key, bool fallback)
        {
            bool b;
            return bool.TryParse(GetString(key, ""), out b) ? b : fallback;
        }

        protected void SetBool(string key, bool value)
        {
            SetString(key, value ? "True" : "False");
        }

        protected int GetInt(string key, int fallback, int min, int max)
        {
            int i;
            return int.TryParse(GetString(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out i)
                ? Math.Max(min, Math.Min(max, i))
                : fallback;
        }

        protected void SetInt(string key, int value)
        {
            SetString(key, value.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>User preferences (settings.cfg). Written by the UI, read by both processes.</summary>
    internal sealed class AppSettings : KeyValueFile
    {
        public AppSettings() : base(AppPaths.SettingsFile) { }

        /// <summary>
        /// Off (default): nothing runs after the window closes; limits stay on running apps and apps
        /// started later get the persistent priority limits. On: a small event-driven engine stays in
        /// the tray and applies the exact CPU / memory caps to apps started later too.
        /// </summary>
        /// <summary>
        /// Keep limits on apps opened later and after a restart: while any limit is set, the small
        /// engine runs (it starts at sign-in and when the window closes). Nothing runs without limits.
        /// </summary>
        public bool KeepLimits { get { return GetBool("KeepLimits", true); } set { SetBool("KeepLimits", value); } }
        public bool StartHiddenOnLogon { get { return GetBool("StartHiddenOnLogon", true); } set { SetBool("StartHiddenOnLogon", value); } }
        public bool GroupByType { get { return GetBool("GroupByType", true); } set { SetBool("GroupByType", value); } }
        /// <summary>Weekly check on GitHub for a new version (scheduled task "TaskManagerPro Update").</summary>
        public bool AutoCheckUpdates { get { return GetBool("AutoCheckUpdates", true); } set { SetBool("AutoCheckUpdates", value); } }
        /// <summary>Off (default): a found update is installed right away. On: ask first.</summary>
        public bool AskBeforeUpdating { get { return GetBool("AskBeforeUpdating", false); } set { SetBool("AskBeforeUpdating", value); } }
        public DateTime LastUpdateCheckUtc
        {
            get
            {
                DateTime d;
                return DateTime.TryParse(GetString("LastUpdateCheckUtc", ""), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d) ? d : DateTime.MinValue;
            }
            set { SetString("LastUpdateCheckUtc", value.ToString("o", CultureInfo.InvariantCulture)); }
        }
        public bool AlwaysOnTop { get { return GetBool("AlwaysOnTop", false); } set { SetBool("AlwaysOnTop", value); } }
        /// <summary>"System", "Light" or "Dark".</summary>
        public string Theme { get { return GetString("Theme", "System"); } set { SetString("Theme", value); } }
        public int RefreshMs { get { return GetInt("RefreshMs", 2000, 500, 10000); } set { SetInt("RefreshMs", value); } }
        public string SortColumn { get { return GetString("SortColumn", "Memory"); } set { SetString("SortColumn", value); } }
        public bool SortAscending { get { return GetBool("SortAscending", false); } set { SetBool("SortAscending", value); } }
        public string ColumnWidths { get { return GetString("ColumnWidths", ""); } set { SetString("ColumnWidths", value); } }
        public string WindowBounds { get { return GetString("WindowBounds", ""); } set { SetString("WindowBounds", value); } }
        public bool WindowMaximized { get { return GetBool("WindowMaximized", false); } set { SetBool("WindowMaximized", value); } }
    }

    /// <summary>
    /// Limit state (state.cfg), written only by the process that currently enforces limits: the
    /// background engine when it runs, otherwise the window.
    /// </summary>
    internal sealed class LimitState : KeyValueFile
    {
        public LimitState() : base(AppPaths.StateFile) { }

        public bool Paused { get { return GetBool("Paused", false); } set { SetBool("Paused", value); } }
        public bool TrayHintShown { get { return GetBool("TrayHintShown", false); } set { SetBool("TrayHintShown", value); } }
    }

    /// <summary>Small append-only error log (error.log in the data folder, capped at 512 KB).</summary>
    internal static class Log
    {
        private static readonly object sync = new object();

        public static void Error(string context, Exception ex)
        {
            Write(context + ": " + (ex == null ? "unknown error" : ex.ToString()));
        }

        public static void Write(string message)
        {
            try
            {
                lock (sync)
                {
                    AppPaths.EnsureDataDir();
                    var file = new FileInfo(AppPaths.LogFile);
                    if (file.Exists && file.Length > 512 * 1024) file.Delete();
                    File.AppendAllText(AppPaths.LogFile,
                        string.Format(CultureInfo.InvariantCulture, "[{0:yyyy-MM-dd HH:mm:ss}] {1}{2}", DateTime.Now, message, Environment.NewLine));
                }
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
    }
}
