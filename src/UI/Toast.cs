using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskManagerPro
{
    internal enum ToastKind
    {
        Info,
        Success,
        Warning,
        Error
    }

    /// <summary>
    /// The one way Task Manager Pro shows a notice: a card in the bottom-right corner, above the
    /// taskbar, in the current theme. A ring around the notice icon counts down the time the card stays
    /// (3 seconds; paused while the mouse is over the card), and the ✕ button closes it at once.
    /// Cards never take the focus from what the user is doing. Up to three are stacked; a notice with
    /// the same key replaces the earlier one (for example the hint for the limit being edited).
    /// Must be called on a UI thread; the background engine uses "--toast" (<see cref="RunStandalone"/>).
    /// </summary>
    internal sealed class Toast : Form
    {
        public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(3);
        private const int MaxVisible = 3;
        private const int TickMs = 30;    // smooth ring over a 3-second card
        private const int AnimationMs = 180;

        private static readonly List<Toast> open = new List<Toast>();

        /// <summary>Raised when the last card has closed (the "--toast" process then exits).</summary>
        public static event EventHandler AllClosed;

        private readonly string key;
        private readonly string title;
        private readonly string message;
        private readonly ToastKind kind;
        private readonly Action onClick;
        private readonly TimeSpan duration;
        private readonly float scale;
        private readonly Timer timer = new Timer { Interval = TickMs };
        private readonly Font titleFont, messageFont, captionFont, glyphFont, closeFont;
        private readonly Image appIcon;

        private TimeSpan remaining;
        private DateTime lastTick;
        private bool hovered, closeHovered, closing;
        private DateTime animationStart;
        private int targetY;
        private Rectangle closeBox, ringBox, timeBox;

        /// <summary>Shows a notice with the default 3-minute duration.</summary>
        public static Toast Show(string title, string message, ToastKind kind)
        {
            return Show(title, message, kind, null, null);
        }

        /// <summary>
        /// Shows a notice. <paramref name="key"/> (optional): a card with the same key is replaced.
        /// <paramref name="onClick"/> (optional): runs when the card itself is clicked.
        /// </summary>
        public static Toast Show(string title, string message, ToastKind kind, string key, Action onClick)
        {
            try
            {
                foreach (Toast t in open.ToArray())
                {
                    if ((key != null && t.key == key) || (t.title == title && t.message == message)) t.Dismiss(false);
                }
                var toast = new Toast(key, title, message, kind, onClick, DefaultDuration);
                open.Add(toast);
                while (open.Count > MaxVisible) open[0].Dismiss(false);
                toast.Present();
                return toast;
            }
            catch (Exception ex)
            {
                Log.Error("Toast " + title, ex);
                return null;
            }
        }

        /// <summary>"--toast kind title message": shows one card from a separate process (used by the engine).</summary>
        public static int RunStandalone(string kindName, string title, string message)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Theme.Initialize(new AppSettings().Theme);
            ToastKind kind;
            if (!Enum.TryParse(kindName ?? "", true, out kind)) kind = ToastKind.Info;
            AllClosed += delegate { Application.ExitThread(); };
            Show(title ?? Program.AppName, message ?? "", kind, null, OpenWindow);
            if (open.Count > 0) Application.Run();
            return 0;
        }

        private static void OpenWindow()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Assembly.GetEntryAssembly().Location) { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                Log.Error("Open window from toast", ex);
            }
        }

        private Toast(string key, string title, string message, ToastKind kind, Action onClick, TimeSpan duration)
        {
            this.key = key;
            this.title = title;
            this.message = message;
            this.kind = kind;
            this.onClick = onClick;
            this.duration = duration;
            remaining = duration;

            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            titleFont = new Font("Segoe UI Semibold", 10.5f);
            messageFont = new Font("Segoe UI", 9.5f);
            captionFont = new Font("Segoe UI", 8.5f);
            glyphFont = Theme.IconFont(13f);
            closeFont = Theme.IconFont(8f);
            try
            {
                using (Icon icon = new Icon(Icon.ExtractAssociatedIcon(Assembly.GetEntryAssembly().Location), S(16), S(16))) appIcon = icon.ToBitmap();
            }
            catch
            {
                appIcon = null;
            }

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            Text = Program.AppName;
            Cursor = onClick != null ? Cursors.Hand : Cursors.Default;
            Size = new Size(S(372), MeasureHeight());
            PlaceParts(); // the rectangles of the close button, ring and time

            timer.Tick += OnTick;
            Theme.Changed += OnThemeChanged;
        }

        // ---------------- Window behaviour: no focus stealing, no taskbar button, shadow ----------------

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x80 /* WS_EX_TOOLWINDOW */ | 0x8 /* WS_EX_TOPMOST */;
                cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2; // DWMWCP_ROUND: Windows 11 rounded corners (ignored on Windows 10)
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        }

        // ---------------- Lifetime ----------------

        private void Present()
        {
            Opacity = 0;
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - S(16), area.Bottom - Height - S(16));
            Show();
            Relayout();
            Location = new Point(Left, targetY + S(14));
            animationStart = lastTick = DateTime.UtcNow;
            timer.Start();
        }

        /// <summary>Places the cards from the bottom up, newest at the bottom.</summary>
        private static void Relayout()
        {
            if (open.Count == 0) return;
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            int gap = open[0].S(10);
            int y = area.Bottom - open[0].S(16);
            for (int i = open.Count - 1; i >= 0; i--)
            {
                Toast t = open[i];
                y -= t.Height;
                t.targetY = y;
                if (t.Opacity >= 1) t.Location = new Point(area.Right - t.Width - t.S(16), y);
                y -= gap;
            }
        }

        /// <summary>Closes the card, with a short fade unless <paramref name="animate"/> is false.</summary>
        public void Dismiss(bool animate)
        {
            if (closing) return;
            closing = true;
            open.Remove(this);
            if (animate && IsHandleCreated && Visible)
            {
                animationStart = DateTime.UtcNow;
                timer.Interval = 15;
                return; // OnTick fades out, then closes
            }
            Finish();
        }

        private void Finish()
        {
            timer.Stop();
            Theme.Changed -= OnThemeChanged;
            Close();
            Relayout();
            if (open.Count == 0 && AllClosed != null) AllClosed(null, EventArgs.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                foreach (IDisposable d in new IDisposable[] { titleFont, messageFont, captionFont, glyphFont, closeFont, appIcon })
                {
                    if (d != null) d.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        private void OnTick(object sender, EventArgs e)
        {
            DateTime now = DateTime.UtcNow;
            double t = Math.Min(1, (now - animationStart).TotalMilliseconds / AnimationMs);
            double eased = 1 - Math.Pow(1 - t, 3);

            if (closing)
            {
                Opacity = 1 - eased;
                if (t >= 1) Finish();
                return;
            }

            if (Opacity < 1)
            {
                Opacity = eased;
                Top = targetY + (int)Math.Round(S(14) * (1 - eased));
                if (t >= 1) Top = targetY;
            }
            else if (Top != targetY)
            {
                Top = targetY;
            }

            if (!hovered) remaining -= now - lastTick;
            lastTick = now;
            if (remaining <= TimeSpan.Zero)
            {
                Dismiss(true);
                return;
            }
            Invalidate(Rectangle.Union(ringBox, timeBox));
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            Invalidate();
        }

        // ---------------- Mouse ----------------

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hovered = true; // the countdown waits while the card is being read
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hovered = false;
            if (closeHovered)
            {
                closeHovered = false;
                Invalidate(closeBox);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool over = closeBox.Contains(e.Location);
            if (over != closeHovered)
            {
                closeHovered = over;
                Cursor = over || onClick != null ? Cursors.Hand : Cursors.Default;
                Invalidate(closeBox);
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            if (!closeBox.Contains(e.Location) && onClick != null) onClick();
            Dismiss(true);
        }

        // ---------------- Layout and drawing ----------------

        private int S(int px)
        {
            return (int)Math.Round(px * scale);
        }

        private int TextLeft
        {
            get { return S(16) + S(44) + S(14); }
        }

        private int TextWidth
        {
            get { return S(372) - TextLeft - S(18); }
        }

        private int MeasureHeight()
        {
            const TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
            int titleH = TextRenderer.MeasureText(title, titleFont, new Size(TextWidth, int.MaxValue), flags).Height;
            int messageH = string.IsNullOrEmpty(message) ? 0
                : Math.Min(TextRenderer.MeasureText(message, messageFont, new Size(TextWidth, int.MaxValue), flags).Height, messageFont.Height * 4);
            int body = Math.Max(S(44), titleH + (messageH > 0 ? S(2) + messageH : 0));
            return S(38) + body + S(16);
        }

        private void PlaceParts()
        {
            closeBox = new Rectangle(Width - S(40), S(4), S(34), S(30));
            ringBox = new Rectangle(S(16), S(38), S(44), S(44));
            timeBox = new Rectangle(closeBox.Left - S(48), S(4), S(46), S(30));
        }

        private Color KindColor(Palette p)
        {
            switch (kind)
            {
                case ToastKind.Success: return p.Good;
                case ToastKind.Warning: return p.IsDark ? Color.FromArgb(252, 200, 0) : Color.FromArgb(202, 120, 0);
                case ToastKind.Error: return p.Bad;
                default: return p.Accent;
            }
        }

        private string Glyph
        {
            get
            {
                switch (kind)
                {
                    case ToastKind.Success: return ""; // check mark
                    case ToastKind.Warning: return ""; // warning
                    case ToastKind.Error: return "";   // error
                    default: return "";                // info
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Palette p = Theme.Current;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color back = p.IsDark ? Color.FromArgb(44, 44, 44) : Color.White;
            Color accent = KindColor(p);
            g.Clear(back);
            using (var border = new Pen(p.IsDark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(222, 222, 222)))
                g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

            // Header: app icon, app name, time left, close button.
            if (appIcon != null) g.DrawImage(appIcon, S(16), S(11), S(16), S(16));
            TextRenderer.DrawText(g, Program.AppName, captionFont, new Rectangle(S(40), S(4), S(200), S(30)), p.SubText,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            TimeSpan left = remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
            string time = ((int)Math.Ceiling(left.TotalSeconds)).ToString() + "s";
            TextRenderer.DrawText(g, hovered ? "paused" : time, captionFont, timeBox, p.SubText,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
            if (closeHovered)
            {
                using (var hover = new SolidBrush(p.IsDark ? Color.FromArgb(62, 62, 62) : Color.FromArgb(234, 234, 234)))
                using (GraphicsPath path = Rounded(Rectangle.Inflate(closeBox, -S(3), -S(3)), S(4)))
                    g.FillPath(hover, path);
            }
            TextRenderer.DrawText(g, "", closeFont, closeBox, p.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // Icon with the countdown ring: the coloured arc is the time left.
            float ringWidth = Math.Max(2f, 3f * scale);
            RectangleF ring = RectangleF.Inflate(ringBox, -ringWidth / 2, -ringWidth / 2);
            using (var track = new Pen(Color.FromArgb(p.IsDark ? 60 : 40, accent), ringWidth))
                g.DrawEllipse(track, ring);
            float sweep = (float)(360.0 * left.TotalMilliseconds / duration.TotalMilliseconds);
            if (sweep > 0.5f)
            {
                using (var arc = new Pen(accent, ringWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(arc, ring, -90, sweep);
            }
            RectangleF disc = RectangleF.Inflate(ring, -ringWidth * 1.6f, -ringWidth * 1.6f);
            using (var fill = new SolidBrush(Color.FromArgb(p.IsDark ? 50 : 28, accent)))
                g.FillEllipse(fill, disc);
            TextRenderer.DrawText(g, Glyph, glyphFont, Rectangle.Round(disc), accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // Title and message.
            const TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
            int y = S(38);
            Size titleSize = TextRenderer.MeasureText(title, titleFont, new Size(TextWidth, int.MaxValue), flags);
            if (string.IsNullOrEmpty(message)) y = ringBox.Top + (ringBox.Height - titleSize.Height) / 2;
            TextRenderer.DrawText(g, title, titleFont, new Rectangle(TextLeft, y, TextWidth, titleSize.Height), p.Text, flags);
            if (!string.IsNullOrEmpty(message))
            {
                y += titleSize.Height + S(2);
                TextRenderer.DrawText(g, message, messageFont, new Rectangle(TextLeft, y, TextWidth, Height - y - S(12)), p.SubText,
                    flags | TextFormatFlags.EndEllipsis);
            }
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
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

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
