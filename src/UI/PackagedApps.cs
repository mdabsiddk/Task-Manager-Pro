using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskManagerPro
{
    /// <summary>
    /// Name and logo of Store / MSIX apps (WhatsApp, Microsoft Store, Photos, Settings, ...).
    /// Their program files usually carry no icon and no proper name; Windows keeps both in the app
    /// package. Like Task Manager, this asks Windows which app a process belongs to (its
    /// AppUserModelID) and reads the name and logo from the Start menu's Apps folder
    /// (shell:AppsFolder\&lt;AppUserModelID&gt;), so every installed app is covered automatically.
    /// </summary>
    internal static class PackagedApps
    {
        private static bool unavailable; // Windows 7: no app packages

        /// <summary>The app the process belongs to, or null when it isn't part of an app package.</summary>
        /// <param name="ownApp">true when Windows started the process as that app (not as a helper).</param>
        public static PackagedApp Find(int pid, long createTime, string exePath, int iconSize, out bool ownApp)
        {
            ownApp = false;
            if (unavailable) return null;
            IntPtr h = NativeMethods.OpenProcessVerified(pid, createTime, 0);
            if (h == IntPtr.Zero) return null;
            try
            {
                string aumid = ApplicationUserModelId(h);
                ownApp = aumid != null;
                if (aumid == null)
                {
                    // A helper process of an app (no app ID of its own): use the package's first app,
                    // but only for programs that live inside the package. A system program such as
                    // RuntimeBroker runs with the package of its client, yet is not that app.
                    string fullName = PackageFullName(h);
                    if (fullName == null) return null;
                    string folder = PackagePath(fullName);
                    if (folder == null || exePath == null || !exePath.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return null;
                    aumid = FirstApplicationId(fullName);
                    if (aumid == null) return null;
                }
                return FromAppsFolder(aumid, iconSize);
            }
            catch (EntryPointNotFoundException)
            {
                unavailable = true;
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("Packaged app of " + exePath, ex);
                return null;
            }
            finally
            {
                NativeMethods.CloseHandle(h);
            }
        }

        // ---- Which app ----

        private static string ApplicationUserModelId(IntPtr process)
        {
            int length = 256;
            var sb = new StringBuilder(length);
            return GetApplicationUserModelId(process, ref length, sb) == 0 ? sb.ToString() : null;
        }

        private static string PackageFullName(IntPtr process)
        {
            int length = 256;
            var sb = new StringBuilder(length);
            return GetPackageFullName(process, ref length, sb) == 0 ? sb.ToString() : null;
        }

        private static string PackagePath(string fullName)
        {
            int length = 1024;
            var sb = new StringBuilder(length);
            return GetPackagePathByFullName(fullName, ref length, sb) == 0 ? sb.ToString() : null;
        }

        private static string FirstApplicationId(string fullName)
        {
            IntPtr info;
            if (OpenPackageInfoByFullName(fullName, 0, out info) != 0) return null;
            try
            {
                int length = 0, count;
                GetPackageApplicationIds(info, ref length, IntPtr.Zero, out count);
                if (length == 0) return null;
                IntPtr buffer = Marshal.AllocHGlobal(length);
                try
                {
                    // The buffer starts with an array of string pointers into itself.
                    if (GetPackageApplicationIds(info, ref length, buffer, out count) != 0 || count == 0) return null;
                    return Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer));
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                ClosePackageInfo(info);
            }
        }

        // ---- Name and logo from the Apps folder ----

        private static PackagedApp FromAppsFolder(string aumid, int iconSize)
        {
            object item;
            Guid iid = typeof(IShellItem).GUID;
            if (SHCreateItemFromParsingName("shell:AppsFolder\\" + aumid, IntPtr.Zero, ref iid, out item) != 0 || item == null) return null;
            try
            {
                var app = new PackagedApp();
                IntPtr name;
                if (((IShellItem)item).GetDisplayName(0 /* SIGDN_NORMALDISPLAY */, out name) == 0 && name != IntPtr.Zero)
                {
                    app.Name = Marshal.PtrToStringUni(name);
                    Marshal.FreeCoTaskMem(name);
                }
                var factory = item as IShellItemImageFactory;
                IntPtr hbm;
                if (factory != null && factory.GetImage(new NativeSize { cx = iconSize, cy = iconSize }, 0x4 /* SIIGBF_ICONONLY */, out hbm) == 0 && hbm != IntPtr.Zero)
                {
                    try
                    {
                        Bitmap logo = ToBitmap(hbm);
                        app.Icon = IsWhiteGlyph(logo) ? Plate(logo) : logo;
                    }
                    finally
                    {
                        DeleteObject(hbm);
                    }
                }
                return app.Name == null && app.Icon == null ? null : app;
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }

        /// <summary>HBITMAP → Bitmap, keeping the transparency (Image.FromHbitmap would drop it).</summary>
        private static Bitmap ToBitmap(IntPtr hbm)
        {
            using (Bitmap raw = Image.FromHbitmap(hbm))
            {
                var rect = new Rectangle(0, 0, raw.Width, raw.Height);
                BitmapData data = raw.LockBits(rect, ImageLockMode.ReadOnly, raw.PixelFormat);
                try
                {
                    if (Image.GetPixelFormatSize(raw.PixelFormat) != 32 || !HasAlpha(data)) return new Bitmap(raw);
                    using (var premultiplied = new Bitmap(data.Width, data.Height, data.Stride, PixelFormat.Format32bppPArgb, data.Scan0))
                    {
                        var result = new Bitmap(data.Width, data.Height, PixelFormat.Format32bppArgb);
                        using (Graphics g = Graphics.FromImage(result)) g.DrawImageUnscaled(premultiplied, 0, 0);
                        return result;
                    }
                }
                finally
                {
                    raw.UnlockBits(data);
                }
            }
        }

        /// <summary>
        /// Many Windows apps (Search, Start, Widgets, ...) ship a white logo meant for a coloured tile,
        /// which would be invisible on a light background.
        /// </summary>
        private static bool IsWhiteGlyph(Bitmap logo)
        {
            int visible = 0;
            for (int y = 0; y < logo.Height; y++)
            {
                for (int x = 0; x < logo.Width; x++)
                {
                    Color c = logo.GetPixel(x, y);
                    if (c.A < 64) continue;
                    if (c.R < 200 || c.G < 200 || c.B < 200) return false;
                    visible++;
                }
            }
            return visible > 0;
        }

        /// <summary>The white logo on a rounded accent-coloured tile, as Windows shows such apps.</summary>
        private static Bitmap Plate(Bitmap logo)
        {
            int n = logo.Width;
            var result = new Bitmap(n, n, PixelFormat.Format32bppArgb);
            using (logo)
            using (Graphics g = Graphics.FromImage(result))
            using (var brush = new SolidBrush(Color.FromArgb(0, 103, 192)))
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                int r = Math.Max(2, n / 4);
                path.AddArc(0, 0, r, r, 180, 90);
                path.AddArc(n - r - 1, 0, r, r, 270, 90);
                path.AddArc(n - r - 1, n - r - 1, r, r, 0, 90);
                path.AddArc(0, n - r - 1, r, r, 90, 90);
                path.CloseFigure();
                g.FillPath(brush, path);
                int inset = Math.Max(1, n / 8);
                g.DrawImage(logo, new Rectangle(inset, inset, n - 2 * inset, n - 2 * inset));
            }
            return result;
        }

        private static bool HasAlpha(BitmapData data)
        {
            for (int y = 0; y < data.Height; y++)
            {
                for (int x = 0; x < data.Width; x++)
                {
                    if (Marshal.ReadByte(data.Scan0, y * data.Stride + x * 4 + 3) != 0) return true;
                }
            }
            return false;
        }

        // ---- Interop ----

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize
        {
            public int cx, cy;
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
            [PreserveSig] int GetParent(out IShellItem parent);
            [PreserveSig] int GetDisplayName(int sigdn, out IntPtr name);
            [PreserveSig] int GetAttributes(uint mask, out uint attributes);
            [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
        }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr bitmap);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object item);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetApplicationUserModelId(IntPtr process, ref int length, StringBuilder aumid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPackageFullName(IntPtr process, ref int length, StringBuilder fullName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPackagePathByFullName(string fullName, ref int length, StringBuilder path);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int OpenPackageInfoByFullName(string fullName, int reserved, out IntPtr info);

        [DllImport("kernel32.dll")]
        private static extern int GetPackageApplicationIds(IntPtr info, ref int length, IntPtr buffer, out int count);

        [DllImport("kernel32.dll")]
        private static extern int ClosePackageInfo(IntPtr info);
    }

    internal sealed class PackagedApp
    {
        public string Name;
        public Image Icon;
    }
}
