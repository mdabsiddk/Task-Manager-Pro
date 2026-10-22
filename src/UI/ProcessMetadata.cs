using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace TaskManagerPro
{
    /// <summary>What the list shows for an executable: friendly name, icon and where it lives.</summary>
    internal sealed class AppInfo
    {
        public string Path;
        public string Description;
        public Image Icon;
        public bool InWindowsFolder;
    }

    /// <summary>
    /// Resolves and caches the image path (per process) and description + small icon (per exe file),
    /// the way Task Manager shows "Google Chrome" with its icon instead of "chrome". Lookups only
    /// happen once per new process / new file, on the sampling thread.
    /// </summary>
    internal sealed class ProcessMetadata
    {
        private struct PathEntry
        {
            public long CreateTime;
            public string Path;
        }

        private readonly Dictionary<int, PathEntry> paths = new Dictionary<int, PathEntry>();
        private readonly Dictionary<string, AppInfo> byPath = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AppInfo> noPath = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        private readonly Image defaultIcon;
        private readonly Image systemIcon;
        private readonly Image serviceIcon;
        private readonly int iconSize = System.Windows.Forms.SystemInformation.SmallIconSize.Width;

        public ProcessMetadata()
        {
            defaultIcon = StockIcon(2 /* SIID_APPLICATION */) ?? SystemIcons.Application.ToBitmap();
            systemIcon = FileIcon(Path.Combine(Environment.SystemDirectory, "ntoskrnl.exe")) ?? defaultIcon;
            serviceIcon = GlyphIcon("" /* Settings gear */) ?? systemIcon;
        }

        public AppInfo Get(ProcInfo p)
        {
            string path = GetPath(p);
            if (path == null)
            {
                AppInfo info;
                if (!noPath.TryGetValue(p.Name, out info))
                {
                    info = new AppInfo
                    {
                        Description = p.Name,
                        Icon = ProtectedProcesses.IsWindowsComponent(p.Name) || p.Pid == 4 ? systemIcon : defaultIcon,
                        InWindowsFolder = ProtectedProcesses.IsWindowsComponent(p.Name)
                    };
                    noPath[p.Name] = info;
                }
                return info;
            }

            AppInfo app;
            if (!byPath.TryGetValue(path, out app))
            {
                app = new AppInfo
                {
                    Path = path,
                    Description = Describe(path, p.Name),
                    InWindowsFolder = path.StartsWith(windowsDir + "\\", StringComparison.OrdinalIgnoreCase)
                };
                bool hasIcon = ExtractIconEx(path, -1, null, null, 0) > 0;
                app.Icon = hasIcon ? FileIcon(path) : null;

                // Store / MSIX apps: name and logo come from the app package, as in Task Manager.
                bool ownApp;
                PackagedApp package = PackagedApps.Find(p.Pid, p.CreateTime, path, iconSize, out ownApp);
                if (package != null)
                {
                    if (package.Icon != null && (ownApp || !hasIcon)) app.Icon = package.Icon;
                    if (!string.IsNullOrEmpty(package.Name) && (ownApp || IsPlainName(app.Description, path))) app.Description = package.Name;
                }
                if (app.Icon == null)
                {
                    // Like Task Manager: Windows programs without an icon of their own get the
                    // Windows icon, and service hosts a gear; other programs the generic one.
                    if (string.Equals(p.Name, "svchost", StringComparison.OrdinalIgnoreCase)) app.Icon = serviceIcon;
                    else app.Icon = app.InWindowsFolder || ProtectedProcesses.IsWindowsComponent(p.Name) ? systemIcon : defaultIcon;
                }
                byPath[path] = app;
            }
            return app;
        }

        /// <summary>True when the "description" is only the file name (no real product name).</summary>
        private static bool IsPlainName(string description, string path)
        {
            string file = Path.GetFileName(path);
            return string.Equals(description, file, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(description, Path.GetFileNameWithoutExtension(file), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Forgets processes that no longer exist.</summary>
        public void Prune(List<ProcInfo> alive)
        {
            if (paths.Count < alive.Count + 64) return;
            var keep = new HashSet<int>();
            foreach (ProcInfo p in alive) keep.Add(p.Pid);
            var gone = new List<int>();
            foreach (int pid in paths.Keys)
            {
                if (!keep.Contains(pid)) gone.Add(pid);
            }
            foreach (int pid in gone) paths.Remove(pid);
        }

        private string GetPath(ProcInfo p)
        {
            PathEntry e;
            if (paths.TryGetValue(p.Pid, out e) && e.CreateTime == p.CreateTime) return e.Path;
            string path = p.Pid <= 4 ? null : NativeMethods.GetImagePath(p.Pid, p.CreateTime);
            paths[p.Pid] = new PathEntry { CreateTime = p.CreateTime, Path = path };
            return path;
        }

        private static string Describe(string path, string fallback)
        {
            try
            {
                string d = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrEmpty(d) && d.Trim().Length > 0) return d.Trim();
            }
            catch
            {
                // no version resource
            }
            return fallback;
        }

        private static Image FileIcon(string path)
        {
            var info = new SHFILEINFO();
            IntPtr result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), 0x100 | 0x1 /* ICON | SMALLICON */);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            return ToBitmap(info.hIcon);
        }

        /// <summary>A Segoe icon-font glyph in grey, readable on light and dark backgrounds.</summary>
        private Image GlyphIcon(string glyph)
        {
            try
            {
                var bmp = new Bitmap(iconSize, iconSize);
                using (Graphics g = Graphics.FromImage(bmp))
                using (Font font = new Font(Theme.IconFontName, iconSize * 0.8f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(Color.FromArgb(120, 120, 120)))
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    g.DrawString(glyph, font, brush, new RectangleF(0, 0, iconSize, iconSize), format);
                }
                return bmp;
            }
            catch (ArgumentException)
            {
                return null; // icon font missing
            }
        }

        private static Image StockIcon(int id)
        {
            var info = new SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf(typeof(SHSTOCKICONINFO)) };
            if (SHGetStockIconInfo(id, 0x100 | 0x1, ref info) != 0 || info.hIcon == IntPtr.Zero) return null;
            return ToBitmap(info.hIcon);
        }

        private static Image ToBitmap(IntPtr hIcon)
        {
            try
            {
                using (Icon icon = Icon.FromHandle(hIcon)) return icon.ToBitmap();
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHSTOCKICONINFO
        {
            public uint cbSize;
            public IntPtr hIcon;
            public int iSysImageIndex, iIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szPath;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

        [DllImport("shell32.dll")]
        private static extern int SHGetStockIconInfo(int id, uint flags, ref SHSTOCKICONINFO info);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, int count);
    }
}
