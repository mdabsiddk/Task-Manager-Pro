using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;

namespace TaskManagerPro
{
    /// <summary>The developer of Task Manager Pro, shown in About and in the README.</summary>
    internal static class Developer
    {
        public const string Name = "Md. Abu Bakar";
        public const string Handle = "mdabsiddk";
        public const string Website = "https://mdabsiddk.dev/";
        public const string Email = "mdabsiddk@gmail.com";
        public const string GitHub = "https://github.com/mdabsiddk";
        public const string LinkedIn = "https://www.linkedin.com/in/absiddk/";
        public const string Facebook = "https://www.facebook.com/mdabubakarpage/";
        public const string Project = "https://github.com/mdabsiddk/Task-Manager-Pro";
    }

    /// <summary>
    /// About window: app, version, license and the developer's links. Every link has an icon, opens
    /// in the browser when clicked, and its full address is also shown as selectable text so it can be
    /// read or copied. Links are opened through Explorer, so the browser does not run as administrator.
    /// </summary>
    internal sealed class AboutDialog : Form
    {
        private readonly float scale;

        public AboutDialog()
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            Palette p = Theme.Current;
            Text = "About " + Program.AppName;
            Font = new Font("Segoe UI", 9F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = p.Window;
            ForeColor = p.Text;
            ClientSize = new Size(S(520), S(512));

            int x = S(28), y = S(24);

            // ---- App ----
            var icon = new PictureBox { Image = Icon.ExtractAssociatedIcon(Assembly.GetEntryAssembly().Location).ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(x, y, S(48), S(48)) };
            var name = new Label { Text = Program.AppName, Font = new Font("Segoe UI Semibold", 16f), AutoSize = true, Location = new Point(x + S(62), y - S(2)) };
            var version = new Label { Text = "Version " + AppVersion.Current + "  ·  free and open source (MIT License)", ForeColor = p.SubText, AutoSize = true, Location = new Point(x + S(64), y + S(30)) };
            Controls.AddRange(new Control[] { icon, name, version });
            y += S(70);

            var description = new Label
            {
                Text = "A Windows task manager that can limit how much CPU, memory, disk and network each app may use. " +
                       "Limits stay active after the app is closed.",
                AutoSize = false, Bounds = new Rectangle(x, y, ClientSize.Width - 2 * x, S(40)), ForeColor = p.Text
            };
            Controls.Add(description);
            y += S(52);

            // ---- Developer ----
            Controls.Add(new Label { Text = "DEVELOPER", Font = new Font("Segoe UI Semibold", 8.5f), ForeColor = p.SubText, AutoSize = true, Location = new Point(x, y) });
            y += S(22);
            Controls.Add(new Label { Text = Developer.Name, Font = new Font("Segoe UI Semibold", 12.5f), AutoSize = true, Location = new Point(x, y) });
            y += S(36);

            y = AddLink(y, "Website", Developer.Website, Color.FromArgb(0, 137, 123), "");
            y = AddLink(y, "Email", Developer.Email, Color.FromArgb(234, 67, 53), "");
            y = AddLink(y, "GitHub", Developer.GitHub, Color.FromArgb(36, 41, 47), "");
            y = AddLink(y, "LinkedIn", Developer.LinkedIn, Color.FromArgb(10, 102, 194), "in");
            y = AddLink(y, "Facebook", Developer.Facebook, Color.FromArgb(24, 119, 242), "f");
            y += S(8);

            var project = new LinkLabel { Text = "Project page and source code on GitHub", AutoSize = true, Location = new Point(x, y), LinkColor = p.Accent, ActiveLinkColor = p.Accent };
            project.LinkClicked += delegate { Open(Developer.Project); };
            Controls.Add(project);

            var ok = ThemedButton("OK", new Size(S(96), S(30)));
            ok.DialogResult = DialogResult.OK;
            ok.Location = new Point(ClientSize.Width - ok.Width - x, ClientSize.Height - ok.Height - S(18));
            Controls.Add(ok);
            AcceptButton = CancelButton = ok;
        }

        private int S(int px)
        {
            return (int)Math.Round(px * scale);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyTitleBar(this);
        }

        /// <summary>One row: brand-coloured icon · name (click to open) · full address (selectable) · copy.</summary>
        private int AddLink(int y, string label, string address, Color brand, string glyph)
        {
            Palette p = Theme.Current;
            int x = S(28), rowH = S(44);
            string target = address.Contains("@") ? "mailto:" + address : address;

            var badge = new PictureBox { Bounds = new Rectangle(x, y + S(4), S(32), S(32)), Image = Badge(brand, glyph, S(32)), Cursor = Cursors.Hand };
            badge.Click += delegate { Open(target); };
            new ToolTip().SetToolTip(badge, "Open " + label);

            var link = new LinkLabel
            {
                Text = label, AutoSize = true, Location = new Point(x + S(44), y + S(2)),
                Font = new Font("Segoe UI Semibold", 9.5f), LinkColor = p.Accent, ActiveLinkColor = p.Accent, LinkBehavior = LinkBehavior.HoverUnderline
            };
            link.LinkClicked += delegate { Open(target); };

            // The address itself, readable and selectable, so it can also be typed or pasted into a browser.
            var text = new TextBox
            {
                Text = address, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = p.Window, ForeColor = p.SubText,
                Bounds = new Rectangle(x + S(44), y + S(22), S(330), S(18)), TabStop = false
            };

            var copy = ThemedButton("Copy", new Size(S(66), S(26)));
            copy.Location = new Point(ClientSize.Width - x - S(66), y + S(7));
            copy.Click += delegate
            {
                try
                {
                    Clipboard.SetText(address);
                    copy.Text = "Copied";
                }
                catch (Exception ex)
                {
                    Log.Error("Copy link", ex);
                }
            };

            Controls.AddRange(new Control[] { badge, link, text, copy });
            return y + rowH;
        }

        private static Button ThemedButton(string text, Size size)
        {
            Palette p = Theme.Current;
            var b = new Button { Text = text, Size = size, FlatStyle = FlatStyle.Flat, BackColor = p.InputBack, ForeColor = p.Text, Cursor = Cursors.Hand };
            b.FlatAppearance.BorderColor = p.InputBorder;
            b.FlatAppearance.MouseOverBackColor = p.Hover;
            return b;
        }

        /// <summary>Rounded brand-coloured square with a white glyph (Segoe icon font) or letters.</summary>
        private static Image Badge(Color brand, string glyph, int size)
        {
            var bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            using (var brush = new SolidBrush(brand))
            using (var path = new GraphicsPath())
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                int r = size / 4;
                path.AddArc(0, 0, 2 * r, 2 * r, 180, 90);
                path.AddArc(size - 2 * r - 1, 0, 2 * r, 2 * r, 270, 90);
                path.AddArc(size - 2 * r - 1, size - 2 * r - 1, 2 * r, 2 * r, 0, 90);
                path.AddArc(0, size - 2 * r - 1, 2 * r, 2 * r, 90, 90);
                path.CloseFigure();
                g.FillPath(brush, path);

                bool isIconGlyph = glyph.Length == 1 && glyph[0] >= '';
                using (Font f = isIconGlyph ? Theme.IconFont(size * 0.36f) : new Font("Segoe UI", size * (glyph.Length > 1 ? 0.34f : 0.46f), FontStyle.Bold, GraphicsUnit.Pixel))
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.DrawString(glyph, f, Brushes.White, new RectangleF(0, isIconGlyph ? 0 : size * 0.04f, size, size), format);
                }
            }
            return bmp;
        }

        /// <summary>Opens a link through Explorer, so the browser runs with normal (not administrator) rights.</summary>
        public static void Open(string target)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + target + "\"") { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                Log.Error("Open " + target, ex);
            }
        }
    }
}
