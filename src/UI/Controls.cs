using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskManagerPro
{
    /// <summary>Flat command button with an icon glyph, like the Windows 11 Task Manager toolbar.</summary>
    internal sealed class ToolbarButton : Control
    {
        private bool hover, pressed;
        private string glyph;
        private Font glyphFont;

        public ToolbarButton(string glyph, string text)
        {
            this.glyph = glyph;
            Text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                     | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
            Cursor = Cursors.Default;
            Theme.Changed += OnThemeChanged;
        }

        public string Glyph
        {
            get { return glyph; }
            set
            {
                glyph = value;
                Invalidate();
            }
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Theme.Changed -= OnThemeChanged;
                if (glyphFont != null) glyphFont.Dispose();
            }
            base.Dispose(disposing);
        }

        public Size Measure()
        {
            float s = DeviceScale();
            int textWidth = string.IsNullOrEmpty(Text) ? 0 : TextRenderer.MeasureText(Text, Font).Width + (int)(6 * s);
            return new Size((int)(40 * s) + textWidth, (int)(34 * s));
        }

        private float DeviceScale()
        {
            using (Graphics g = CreateGraphics()) return g.DpiX / 96f;
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Palette p = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : p.Window);
            float s = DeviceScale();
            if (glyphFont == null) glyphFont = Theme.IconFont(10.5f);

            if (Enabled && (hover || pressed))
            {
                using (var path = Round(new Rectangle(0, 0, Width - 1, Height - 1), (int)(4 * s)))
                using (var b = new SolidBrush(pressed ? p.Selected : p.Hover))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillPath(b, path);
                }
            }

            Color fore = Enabled ? p.Text : p.Disabled;
            var glyphRect = new Rectangle((int)(10 * s), 0, (int)(20 * s), Height);
            TextRenderer.DrawText(g, glyph, glyphFont, glyphRect, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (!string.IsNullOrEmpty(Text))
            {
                var textRect = new Rectangle((int)(36 * s), 0, Width - (int)(40 * s), Height);
                TextRenderer.DrawText(g, Text, Font, textRect, fore, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
        }

        internal static GraphicsPath Round(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    /// <summary>Rounded search box with a magnifier glyph and placeholder text.</summary>
    internal sealed class SearchBox : Control
    {
        public readonly TextBox Box;
        private Font glyphFont;
        private readonly string placeholder;

        public SearchBox(string placeholder)
        {
            this.placeholder = placeholder;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Box = new TextBox { BorderStyle = BorderStyle.None };
            Box.HandleCreated += delegate { SendMessage(Box.Handle, 0x1501 /* EM_SETCUEBANNER */, (IntPtr)1, this.placeholder); };
            Box.GotFocus += delegate { Invalidate(); };
            Box.LostFocus += delegate { Invalidate(); };
            Controls.Add(Box);
            Theme.Changed += OnThemeChanged;
            ApplyColors();
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            ApplyColors();
            Invalidate();
        }

        private void ApplyColors()
        {
            Box.BackColor = Theme.Current.InputBack;
            Box.ForeColor = Theme.Current.Text;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Theme.Changed -= OnThemeChanged;
                if (glyphFont != null) glyphFont.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            Box.Font = Font;
            LayoutBox();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutBox();
        }

        private void LayoutBox()
        {
            int h = Box.PreferredHeight;
            int left = Height; // room for the glyph
            Box.Bounds = new Rectangle(left, (Height - h) / 2, Math.Max(10, Width - left - Height / 3), h);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Box.Focus();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Palette p = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : p.Window);
            if (glyphFont == null) glyphFont = Theme.IconFont(9.5f);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = ToolbarButton.Round(r, Height / 5))
            using (var b = new SolidBrush(p.InputBack))
            using (var pen = new Pen(Box.Focused ? p.InputHover : p.InputBorder))
            {
                g.FillPath(b, path);
                g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, "", glyphFont, new Rectangle(0, 0, Height, Height), p.SubText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
    }
}
