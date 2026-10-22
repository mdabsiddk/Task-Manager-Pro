using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TaskManagerPro
{
    /// <summary>A newer version found on GitHub.</summary>
    internal sealed class UpdateInfo
    {
        public Version Version;
        public string Tag;
        public string PageUrl;
    }

    /// <summary>
    /// Updates from GitHub Releases. The newest version is read from the redirect of
    /// /releases/latest (no API key, no rate limit). The update package (TaskManagerPro.zip) is
    /// installed only if its SHA-256 matches SHA256SUMS.txt of the same release. Installing is done by
    /// the package's own install.bat in "/update" mode, started as a separate process, because this
    /// program file itself gets replaced.
    /// </summary>
    internal static class Updater
    {
        public const string Repo = "mdabsiddk/Task-Manager-Pro";
        private const string TempPrefix = "TaskManagerPro-update-";
        private static readonly TimeSpan CheckEvery = TimeSpan.FromDays(7);

        public static Version Current
        {
            get
            {
                Version v = Assembly.GetEntryAssembly().GetName().Version;
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        /// <summary>Returns the newer version, or null when this one is the newest. Throws when offline.</summary>
        public static UpdateInfo Check()
        {
            EnableTls12();
            var request = (HttpWebRequest)WebRequest.Create("https://github.com/" + Repo + "/releases/latest");
            request.AllowAutoRedirect = false;
            request.Method = "HEAD";
            request.UserAgent = "TaskManagerPro-Updater/" + Current;
            request.Timeout = 20000;
            string location;
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                location = response.Headers["Location"];
            }
            int tagAt = location == null ? -1 : location.LastIndexOf("/tag/", StringComparison.Ordinal);
            if (tagAt < 0) throw new InvalidOperationException("No release found on GitHub.");

            string tag = Uri.UnescapeDataString(location.Substring(tagAt + 5));
            Version latest;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out latest)) throw new InvalidOperationException("Unexpected release name: " + tag);
            latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));

            if (latest <= Current) return null;
            return new UpdateInfo { Version = latest, Tag = tag, PageUrl = "https://github.com/" + Repo + "/releases/tag/" + tag };
        }

        /// <summary>Downloads and verifies the update; returns the install.bat to run.</summary>
        public static string Download(UpdateInfo update)
        {
            EnableTls12();
            CleanOldDownloads();
            string root = Path.Combine(Path.GetTempPath(), TempPrefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string baseUrl = "https://github.com/" + Repo + "/releases/download/" + update.Tag + "/";
            string zip = Path.Combine(root, "TaskManagerPro.zip");

            using (var web = new WebClient())
            {
                web.Headers[HttpRequestHeader.UserAgent] = "TaskManagerPro-Updater/" + Current;
                web.DownloadFile(baseUrl + "TaskManagerPro.zip", zip);
                string sums = Encoding.UTF8.GetString(web.DownloadData(baseUrl + "SHA256SUMS.txt"));
                string expected = null;
                foreach (string line in sums.Split('\n'))
                {
                    string[] parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && parts[1] == "TaskManagerPro.zip") expected = parts[0].ToLowerInvariant();
                }
                if (expected == null || expected != Sha256(zip))
                    throw new InvalidOperationException("The downloaded update is damaged or was changed (checksum mismatch). Nothing was installed.");
            }

            string extracted = Path.Combine(root, "pkg");
            ZipFile.ExtractToDirectory(zip, extracted);
            string[] found = Directory.GetFiles(extracted, "install.bat", SearchOption.AllDirectories);
            if (found.Length == 0) throw new InvalidOperationException("The update package has no install.bat.");
            return found[0];
        }

        /// <summary>
        /// Starts the installer and returns at once (the caller should exit so its file can be replaced).
        /// <paramref name="visible"/>: show the installer's console. <paramref name="reopenWindow"/>: open
        /// Task Manager Pro again when done.
        /// </summary>
        public static void StartInstall(string installBat, bool visible, bool reopenWindow)
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c \"\"" + installBat + "\" /update" + (reopenWindow ? " /reopen" : "") + " < nul\"")
            {
                UseShellExecute = false,
                CreateNoWindow = !visible,
                WorkingDirectory = Path.GetDirectoryName(installBat)
            };
            Process.Start(psi).Dispose();
        }

        public static bool IsWindowOpen()
        {
            Mutex m;
            try
            {
                if (!Mutex.TryOpenExisting(@"Local\TaskManagerPro.Window", out m)) return false;
                m.Dispose();
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        /// <summary>
        /// "--update-check" (weekly scheduled task): if a newer version exists, install it (or ask first,
        /// when "Ask before updating" is on); if not, do nothing. Offline: try again next time.
        /// </summary>
        public static int RunScheduled()
        {
            var settings = new AppSettings();
            if (!settings.AutoCheckUpdates) return 0;

            UpdateInfo update;
            try
            {
                update = Check();
            }
            catch (Exception ex)
            {
                Log.Write("Weekly update check failed (will retry next time): " + ex.Message);
                return 0;
            }
            settings.LastUpdateCheckUtc = DateTime.UtcNow;
            settings.Save();
            if (update == null) return 0;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (settings.AskBeforeUpdating && !Ask(null, update)) return 0;

            bool windowWasOpen = IsWindowOpen();
            try
            {
                string installer = Download(update);
                // Visible when the user is involved; quiet when updating automatically.
                StartInstall(installer, settings.AskBeforeUpdating || windowWasOpen, windowWasOpen);
            }
            catch (Exception ex)
            {
                Log.Error("Automatic update", ex);
            }
            return 0;
        }

        /// <summary>"Update now?" question. Returns true for Yes.</summary>
        public static bool Ask(IWin32Window owner, UpdateInfo update)
        {
            string text = string.Format("Task Manager Pro {0} is available (you have {1}).\n\nUpdate now? Your limits and settings are kept.\n\nWhat's new: {2}",
                update.Version, Current, update.PageUrl);
            if (owner != null)
                return MessageBox.Show(owner, text, "Update available", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes;
            using (var top = new Form { TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterScreen, Size = new Size(1, 1), Opacity = 0 })
            {
                top.Show();
                return MessageBox.Show(top, text, "Task Manager Pro - update available", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes;
            }
        }

        public static bool CheckIsDue(AppSettings settings)
        {
            return settings.AutoCheckUpdates && DateTime.UtcNow - settings.LastUpdateCheckUtc > CheckEvery;
        }

        private static string Sha256(string file)
        {
            using (var sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(file))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>Removes update downloads older than a day (the installer can't delete its own folder).</summary>
        private static void CleanOldDownloads()
        {
            try
            {
                foreach (string dir in Directory.GetDirectories(Path.GetTempPath(), TempPrefix + "*"))
                {
                    if (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) > TimeSpan.FromDays(1))
                    {
                        try { Directory.Delete(dir, true); } catch { }
                    }
                }
            }
            catch
            {
                // best effort
            }
        }

        private static void EnableTls12()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
        }
    }
}
