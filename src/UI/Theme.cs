using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TaskManagerPro
{
    /// <summary>Colours for one theme, modelled on the Windows 11 Task Manager.</summary>
    internal sealed class Palette
    {
        public bool IsDark;
        public Color Window, Surface, Text, SubText, Disabled, Border, Separator;
        public Color Hover, Selected, SelectedBorder, Expanded, Accent;
        public Color HeatLow, HeatHigh, HeatHot;
        public Color InputBack, InputBorder, InputHover;
        public Color Good, Warn, Bad;

        public static readonly Palette Light = new Palette
        {
            IsDark = false,
            Window = Color.FromArgb(243, 243, 243),
            Surface = Color.FromArgb(255, 255, 255),
            Text = Color.FromArgb(26, 26, 26),
            SubText = Color.FromArgb(96, 96, 96),
            Disabled = Color.FromArgb(160, 160, 160),
            Border = Color.FromArgb(229, 229, 229),
            Separator = Color.FromArgb(235, 235, 235),
            Hover = Color.FromArgb(243, 243, 243),
            Selected = Color.FromArgb(204, 228, 247),
            SelectedBorder = Color.FromArgb(153, 201, 239),
            Expanded = Color.FromArgb(248, 248, 248),
            Accent = Color.FromArgb(0, 103, 192),
            HeatLow = Color.FromArgb(255, 244, 196),
            HeatHigh = Color.FromArgb(255, 210, 125),
            HeatHot = Color.FromArgb(255, 160, 120),
            InputBack = Color.FromArgb(249, 249, 249),
            InputBorder = Color.FromArgb(214, 214, 214),
            InputHover = Color.FromArgb(0, 103, 192),
            Good = Color.FromArgb(16, 124, 16),
            Warn = Color.FromArgb(157, 93, 0),
            Bad = Color.FromArgb(196, 43, 28)
        };

        public static readonly Palette Dark = new Palette
        {
            IsDark = true,
            Window = Color.FromArgb(32, 32, 32),
            Surface = Color.FromArgb(32, 32, 32),
            Text = Color.FromArgb(255, 255, 255),
            SubText = Color.FromArgb(197, 197, 197),
            Disabled = Color.FromArgb(120, 120, 120),
            Border = Color.FromArgb(51, 51, 51),
            Separator = Color.FromArgb(46, 46, 46),
            Hover = Color.FromArgb(45, 45, 45),
            Selected = Color.FromArgb(56, 56, 56),
            SelectedBorder = Color.FromArgb(96, 96, 96),
            Expanded = Color.FromArgb(43, 43, 43),
            Accent = Color.FromArgb(96, 205, 255),
            HeatLow = Color.FromArgb(44, 32, 52),
            HeatHigh = Color.FromArgb(104, 58, 138),
            HeatHot = Color.FromArgb(150, 60, 110),
            InputBack = Color.FromArgb(45, 45, 45),
            InputBorder = Color.FromArgb(70, 70, 70),
            InputHover = Color.FromArgb(96, 205, 255),
            Good = Color.FromArgb(108, 203, 95),
            Warn = Color.FromArgb(252, 225, 0),
            Bad = Color.FromArgb(255, 153, 164)
        };

        /// <summary>Task Manager style heat map: the busier a cell, the stronger its colour (0..1).</summary>
        public Color Heat(double level)
        {
            level = Math.Max(0, Math.Min(1, level));
            if (level <= 0.75) return Blend(HeatLow, HeatHigh, level / 0.75);
            return Blend(HeatHigh, HeatHot, (level - 0.75) / 0.25);
        }

        public static Color Blend(Color a, Color b, double t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }
    }

    /// <summary>Follows the Windows light/dark app setting (or a fixed choice) and themes native parts.</summary>
    internal static class Theme
    {
        public static event EventHandler Changed;
        public static Palette Current = Palette.Light;
        private static string mode = "System";

        /// <summary>Segoe Fluent Icons (Windows 11) or Segoe MDL2 Assets (Windows 10).</summary>
        public static readonly string IconFontName = PickIconFont();

        public static void Initialize(string themeMode)
        {
            mode = themeMode ?? "System";
            Current = Resolve();
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color) Refresh();
            };
        }

        public static string Mode
        {
            get { return mode; }
            set
            {
                mode = value;
                Refresh();
            }
        }

        public static void Refresh()
        {
            Palette next = Resolve();
            if (next == Current) return;
            Current = next;
            if (Changed != null) Changed(null, EventArgs.Empty);
        }

        private static Palette Resolve()
        {
            if (mode == "Dark") return Palette.Dark;
            if (mode == "Light") return Palette.Light;
            return SystemUsesDarkMode() ? Palette.Dark : Palette.Light;
        }

        public static bool SystemUsesDarkMode()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = key == null ? null : key.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Dark or light window title bar (Windows 10 20H1+ / Windows 11).</summary>
        public static void ApplyTitleBar(Form form)
        {
            if (!form.IsHandleCreated) return;
            int dark = Current.IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, 19, ref dark, sizeof(int)); // pre-20H1 attribute id
            // Force the frame to repaint with the new colours.
            SetWindowPos(form.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0020);
        }

        /// <summary>Dark or light native scroll bars / edit controls.</summary>
        public static void ApplyControl(Control control)
        {
            if (!control.IsHandleCreated) return;
            SetWindowTheme(control.Handle, Current.IsDark ? "DarkMode_Explorer" : "Explorer", null);
        }

        public static Font IconFont(float size)
        {
            return new Font(IconFontName, size, FontStyle.Regular, GraphicsUnit.Point);
        }

        private static string PickIconFont()
        {
            foreach (string name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
            {
                using (var f = new Font(name, 9f))
                {
                    if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return name;
                }
            }
            return "Segoe UI Symbol";
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }

    /// <summary>Menu renderer that follows the current palette.</summary>
    internal sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
    {
        public ThemedMenuRenderer() : base(new Colors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Current.Text : Theme.Current.Disabled;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.Current.SubText;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = e.ImageRectangle;
            using (var font = Theme.IconFont(9f))
            {
                TextRenderer.DrawText(e.Graphics, "", font, r, Theme.Current.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        private sealed class Colors : ProfessionalColorTable
        {
            private static Palette P { get { return Theme.Current; } }
            public override Color ToolStripDropDownBackground { get { return P.IsDark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249); } }
            public override Color ImageMarginGradientBegin { get { return ToolStripDropDownBackground; } }
            public override Color ImageMarginGradientMiddle { get { return ToolStripDropDownBackground; } }
            public override Color ImageMarginGradientEnd { get { return ToolStripDropDownBackground; } }
            public override Color MenuBorder { get { return P.IsDark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(220, 220, 220); } }
            public override Color MenuItemBorder { get { return MenuItemSelected; } }
            public override Color MenuItemSelected { get { return P.IsDark ? Color.FromArgb(61, 61, 61) : Color.FromArgb(232, 232, 232); } }
            public override Color MenuItemSelectedGradientBegin { get { return MenuItemSelected; } }
            public override Color MenuItemSelectedGradientEnd { get { return MenuItemSelected; } }
            public override Color MenuItemPressedGradientBegin { get { return MenuItemSelected; } }
            public override Color MenuItemPressedGradientEnd { get { return MenuItemSelected; } }
            public override Color SeparatorDark { get { return P.IsDark ? Color.FromArgb(64, 64, 64) : Color.FromArgb(225, 225, 225); } }
            public override Color SeparatorLight { get { return ToolStripDropDownBackground; } }
            public override Color CheckBackground { get { return Color.Transparent; } }
            public override Color CheckSelectedBackground { get { return Color.Transparent; } }
            public override Color CheckPressedBackground { get { return Color.Transparent; } }
        }
    }
}
