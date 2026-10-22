using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace TaskManagerPro
{
    /// <summary>
    /// The Task Manager Pro window. A background timer samples processes (and, unless the background
    /// engine is running, applies the limits); the UI thread only turns snapshots into list rows.
    /// </summary>
    internal sealed class MainForm : Form
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private const string ColName = "Name", ColStatus = "Status", ColCpu = "Cpu", ColMaxCpu = "MaxCpu",
            ColMem = "Memory", ColMaxMem = "MaxMemory", ColDisk = "Disk", ColMaxDisk = "MaxDisk",
            ColNet = "Network", ColMaxNet = "MaxNetwork";

        /// <summary>Everything one sampling pass produced.</summary>
        private sealed class Snapshot
        {
            public List<ProcessGroup> Groups;
            public Dictionary<int, AppInfo> Info;
            public Dictionary<string, GovernorState> States;
            public bool Paused, EngineRunning;
            public int ProcessCount;
            public double Cpu, Disk, Network;
            public uint MemoryLoad;
            public ulong TotalPhys;
        }

        private readonly AppSettings settings;
        private readonly LimitController limits = new LimitController();
        private readonly ProcessSampler sampler = new ProcessSampler();
        private readonly ProcessMetadata metadata = new ProcessMetadata();
        private readonly NetworkMonitor network;
        private readonly DiskMonitor disk = new DiskMonitor();
        private readonly object tickLock = new object();
        private readonly HashSet<string> expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly string exePath = Assembly.GetEntryAssembly().Location;
        private readonly float scale;

        private System.Threading.Timer timer;
        private volatile bool closing, engineRunning;
        private bool engineAskedUsToClose;
        private StatusChannel statusReader;
        private Snapshot snapshot;

        private EventWaitHandle activateEvent, closeEvent, showEvent;
        private RegisteredWaitHandle activateWait, closeWait, showWait;

        private Panel topBar, reviewBar;
        private Label reviewText;
        private ToolbarButton btnReview;
        private Label title;
        private SearchBox search;
        private ToolbarButton btnRun, btnEnd, btnPause, btnMore;
        private ProcessListView list;
        private ContextMenuStrip rowMenu, moreMenu;

        public MainForm(AppSettings settings)
        {
            this.settings = settings;
            // Screen DPI without creating our window handle before the child controls exist.
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;

            Text = Program.AppName;
            Font = new Font("Segoe UI", 9F);
            Icon = Icon.ExtractAssociatedIcon(exePath);
            MinimumSize = new Size(S(820), S(480));
            KeyPreview = true;
            LoadWindowBounds();
            TopMost = settings.AlwaysOnTop;

            BuildTopBar();
            BuildReviewBar();
            BuildList();
            BuildMenus();
            Controls.Add(list);
            Controls.Add(reviewBar);
            Controls.Add(topBar);

            Theme.Changed += OnThemeChanged;
            ApplyTheme();

            engineRunning = Engine.IsRunning();
            network = new NetworkMonitor();
            ConnectEvents();
            if (!engineRunning)
            {
                // This window enforces: undo any outside change to the registry / QoS limits.
                ThreadPool.QueueUserWorkItem(delegate { limits.SyncPersistent(); });
            }

            IntPtr unused = Handle; // BeginInvoke from the sampling thread needs a handle
            timer = new System.Threading.Timer(Tick, null, 0, Timeout.Infinite);
        }

        private int S(int px)
        {
            return (int)Math.Round(px * scale);
        }

        // =====================================================================
        // UI construction
        // =====================================================================

        private void BuildTopBar()
        {
            topBar = new Panel { Dock = DockStyle.Top, Height = S(60) };
            title = new Label { Text = "Processes", AutoSize = true, Font = new Font("Segoe UI Semibold", 14f) };
            search = new SearchBox("Type a name, publisher, or PID to search") { Font = Font };
            search.Box.TextChanged += delegate { RebuildRows(); };
            search.Box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { search.Box.Clear(); list.Focus(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Enter) { list.Focus(); e.SuppressKeyPress = true; }
            };

            btnRun = new ToolbarButton("", "Run new task") { Font = Font };
            btnRun.Click += delegate { ProcessActions.RunNewTask(Handle); };
            btnEnd = new ToolbarButton("", "End task") { Font = Font, Enabled = false };
            btnEnd.Click += delegate { EndTask(list.SelectedRow); };
            btnPause = new ToolbarButton("", "Pause limits") { Font = Font };
            btnPause.Click += delegate { TogglePause(); };
            btnMore = new ToolbarButton("", "") { Font = Font };
            btnMore.Click += delegate { moreMenu.Show(btnMore, new Point(btnMore.Width - moreMenu.PreferredSize.Width, btnMore.Height)); };

            var tips = new ToolTip();
            tips.SetToolTip(btnPause, "Temporarily put every app back to normal. Your limits are kept.");
            tips.SetToolTip(btnMore, "Options");

            topBar.Controls.AddRange(new Control[] { title, search, btnRun, btnEnd, btnPause, btnMore });
            topBar.Resize += delegate { LayoutTopBar(); };
        }

        private static string FloorKind(string column)
        {
            switch (column)
            {
                case ColMaxCpu: return "cpu";
                case ColMaxMem: return "memory";
                case ColMaxDisk: return "disk";
                default: return "network";
            }
        }

        /// <summary>Warning strip shown when limits.cfg was changed outside Task Manager Pro.</summary>
        private void BuildReviewBar()
        {
            reviewBar = new Panel { Dock = DockStyle.Top, Height = S(44), Visible = false };
            reviewText = new Label
            {
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "\u26A0  The limits file was changed outside Task Manager Pro (by another program or by hand). " +
                       "For your safety its limits are not applied until you review them."
            };
            btnReview = new ToolbarButton("\uE7BA", "Review limits") { Font = Font };
            btnReview.Click += delegate { ReviewLimits(); };
            reviewBar.Controls.Add(reviewText);
            reviewBar.Controls.Add(btnReview);
            reviewBar.Resize += delegate
            {
                Size size = btnReview.Measure();
                btnReview.Bounds = new Rectangle(reviewBar.Width - size.Width - S(16), (reviewBar.Height - size.Height) / 2, size.Width, size.Height);
                reviewText.Bounds = new Rectangle(S(20), 0, Math.Max(0, btnReview.Left - S(32)), reviewBar.Height);
            };
        }

        private void UpdateReviewBar()
        {
            bool show = limits.Rules.NeedsReview;
            if (reviewBar.Visible != show) reviewBar.Visible = show;
        }

        private void ReviewLimits()
        {
            IList<ProcessRule> pending = limits.Rules.PendingReview;
            if (pending == null) return;
            var text = new System.Text.StringBuilder();
            text.AppendLine("The limits file was changed outside Task Manager Pro. It contains these limits");
            text.AppendLine("(any value below an app's own minimum was already raised):");
            text.AppendLine();
            foreach (ProcessRule r in pending)
            {
                var parts = new List<string>();
                if (r.MaxCpuPercent > 0) parts.Add("CPU " + r.MaxCpuPercent + "%");
                if (r.MaxRamMB > 0) parts.Add("Memory " + r.MaxRamMB.ToString("#,0", Inv) + " MB");
                if (r.MaxDiskMBps > 0) parts.Add("Disk " + r.MaxDiskMBps + " MB/s");
                if (r.MaxNetKBps > 0) parts.Add("Network " + LimitPolicy.Mbps(r.MaxNetKBps) + " Mbps");
                text.AppendLine("  \u2022 " + r.Name + ":  " + string.Join(", ", parts));
            }
            if (pending.Count == 0) text.AppendLine("  (no valid limits)");
            text.AppendLine();
            text.AppendLine("Yes = apply these limits.   No = discard them (a copy is kept in the data folder).   Cancel = decide later.");

            DialogResult answer = MessageBox.Show(this, text.ToString(), "Review limits", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer == DialogResult.Cancel) return;
            bool ok = answer == DialogResult.Yes ? limits.AcceptReview() : limits.RejectReview();
            if (engineRunning) Ipc.Signal(Ipc.ConfigEvent);
            if (!ok)
                MessageBox.Show(this, "The limits file could not be saved. Details: " + AppPaths.LogFile, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            UpdateReviewBar();
            RebuildRows();
            RequestRefresh();
        }

        private void LayoutTopBar()
        {
            int h = topBar.Height;
            title.Location = new Point(S(20), (h - title.Height) / 2);

            int x = topBar.Width - S(12);
            foreach (ToolbarButton b in new[] { btnMore, btnPause, btnEnd, btnRun })
            {
                Size size = b.Measure();
                x -= size.Width;
                b.Bounds = new Rectangle(x, (h - size.Height) / 2, size.Width, size.Height);
                x -= S(4);
            }

            int searchLeft = title.Right + S(28);
            int searchWidth = Math.Min(S(360), x - searchLeft - S(16));
            search.Visible = searchWidth > S(120);
            search.Bounds = new Rectangle(searchLeft, (h - S(34)) / 2, Math.Max(0, searchWidth), S(34));
        }

        private void BuildList()
        {
            list = new ProcessListView { Dock = DockStyle.Fill, Font = Font };
            list.Columns.Add(new ListColumn { Key = ColName, Title = "Name", Width = 260, MinWidth = 200, Fill = true });
            list.Columns.Add(new ListColumn { Key = ColStatus, Title = "Status", Width = 120, MinWidth = 60 });
            list.Columns.Add(new ListColumn { Key = ColCpu, Title = "CPU", Width = 72, MinWidth = 50, RightAlign = true });
            list.Columns.Add(Limit(ColMaxCpu, "Max CPU", "%", 82, "Highest share of the whole CPU this app may use. Click to type, leave empty for no limit. The lowest allowed value comes from the app's own usage."));
            list.Columns.Add(new ListColumn { Key = ColMem, Title = "Memory", Width = 96, MinWidth = 60, RightAlign = true });
            list.Columns.Add(Limit(ColMaxMem, "Max memory", "MB", 104, "Most memory (RAM) this app may keep, in MB. Extra pages go to the page file; the app doesn't crash. It can't be set below half of what the app normally uses."));
            list.Columns.Add(new ListColumn { Key = ColDisk, Title = "Disk", Width = 82, MinWidth = 50, RightAlign = true });
            list.Columns.Add(Limit(ColMaxDisk, "Max disk", "MB/s", 92, "Disk read + write in MB/s. Above it the app gets the lowest disk priority. The lowest allowed value comes from the app's own usage."));
            list.Columns.Add(new ListColumn { Key = ColNet, Title = "Network", Width = 86, MinWidth = 50, RightAlign = true });
            list.Columns.Add(Limit(ColMaxNet, "Max network", "Mbps", 106, "Upload speed limit in Mbps (Windows QoS policy; stays active after a restart). The lowest allowed value comes from the app's own usage."));
            RestoreColumnWidths();

            list.SortKey = settings.SortColumn;
            list.SortAscending = settings.SortAscending;
            list.HeaderClicked += (s, e) => SortBy(e.Column);
            list.ExpandToggled += (s, e) => ToggleExpand(e.Row);
            list.ContextMenuRequested += (s, e) => ShowRowMenu(e.Row, e.ScreenLocation);
            list.EndTaskRequested += (s, e) => EndTask(e.Row);
            list.LimitEdited += List_LimitEdited;
            list.EditStarted += (s, e) =>
            {
                if (e.Row.Group == null) return;
                LimitFloors floors = limits.FloorsFor(e.Row.Group.Name, e.Row.Group);
                list.ShowHint(LimitPolicy.Describe(floors, FloorKind(e.Column.Key)), e.ScreenLocation);
            };
            list.EditBlocked += (s, e) => list.ShowHint(BlockedReason(e.Row), e.ScreenLocation);
            list.SelectionChanged += delegate { UpdateButtons(); };
            list.ColumnsResized += delegate { SaveColumnWidths(); };
        }

        private static ListColumn Limit(string key, string title, string unit, int width, string tooltip)
        {
            return new ListColumn { Key = key, Title = title, TopText = unit, TopMuted = true, Width = width, MinWidth = 60, RightAlign = true, IsLimit = true, Tooltip = tooltip };
        }

        private void BuildMenus()
        {
            rowMenu = new ContextMenuStrip { Renderer = new ThemedMenuRenderer(), ShowImageMargin = false };
            moreMenu = new ContextMenuStrip { Renderer = new ThemedMenuRenderer() };
            moreMenu.Opening += delegate { FillMoreMenu(); };
        }

        private void FillMoreMenu()
        {
            moreMenu.Items.Clear();
            Add(moreMenu.Items, "Run in background", settings.BackgroundEngine, ToggleBackgroundEngine,
                "Keeps a small engine in the tray (starts with Windows) so the exact CPU and memory limits also\n" +
                "apply to apps you open later. When off, nothing runs after you close this window.");
            moreMenu.Items.Add(new ToolStripSeparator());
            Add(moreMenu.Items, "Group by type", settings.GroupByType, delegate
            {
                settings.GroupByType = !settings.GroupByType;
                settings.Save();
                RebuildRows();
            }, null);
            Add(moreMenu.Items, "Always on top", settings.AlwaysOnTop, delegate
            {
                settings.AlwaysOnTop = !settings.AlwaysOnTop;
                settings.Save();
                TopMost = settings.AlwaysOnTop;
            }, null);

            var theme = new ToolStripMenuItem("Theme");
            foreach (string mode in new[] { "System", "Light", "Dark" })
            {
                string m = mode;
                Add(theme.DropDownItems, m == "System" ? "Use system setting" : m, settings.Theme == m, delegate
                {
                    settings.Theme = m;
                    settings.Save();
                    Theme.Mode = m;
                }, null);
            }
            moreMenu.Items.Add(theme);

            var speed = new ToolStripMenuItem("Update speed");
            foreach (var option in new[] { new KeyValuePair<string, int>("High", 1000), new KeyValuePair<string, int>("Normal", 2000), new KeyValuePair<string, int>("Low", 4000) })
            {
                int ms = option.Value;
                Add(speed.DropDownItems, option.Key, settings.RefreshMs == ms, delegate
                {
                    settings.RefreshMs = ms;
                    settings.Save();
                    RequestRefresh();
                }, null);
            }
            moreMenu.Items.Add(speed);
            moreMenu.Items.Add(new ToolStripSeparator());

            bool paused = snapshot != null && snapshot.Paused;
            Add(moreMenu.Items, "Pause all limits", paused, TogglePause, null);
            Add(moreMenu.Items, "Remove all limits...", false, RemoveAllLimits, null);
            moreMenu.Items.Add(new ToolStripSeparator());
            Add(moreMenu.Items, "Open data folder", false, OpenDataFolder, null);
            Add(moreMenu.Items, "About Task Manager Pro", false, ShowAbout, null);
            Add(moreMenu.Items, "Exit", false, Close, null);
            StyleMenu(moreMenu);
        }

        private static void Add(ToolStripItemCollection items, string text, bool isChecked, Action onClick, string tooltip)
        {
            var item = new ToolStripMenuItem(text) { Checked = isChecked, ToolTipText = tooltip };
            item.Click += delegate { onClick(); };
            items.Add(item);
        }

        private static void StyleMenu(ToolStrip menu)
        {
            menu.BackColor = Theme.Current.IsDark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249);
            menu.ForeColor = Theme.Current.Text;
            foreach (ToolStripItem item in menu.Items)
            {
                item.ForeColor = Theme.Current.Text;
                var sub = item as ToolStripMenuItem;
                if (sub != null && sub.HasDropDownItems) StyleMenu(sub.DropDown);
            }
        }

        // =====================================================================
        // Theme, window state
        // =====================================================================

        private void OnThemeChanged(object sender, EventArgs e)
        {
            if (InvokeRequired) BeginInvoke(new Action(ApplyTheme));
            else ApplyTheme();
        }

        private void ApplyTheme()
        {
            Palette p = Theme.Current;
            BackColor = p.Window;
            topBar.BackColor = p.Window;
            title.ForeColor = p.Text;
            reviewBar.BackColor = p.IsDark ? Color.FromArgb(67, 53, 25) : Color.FromArgb(255, 244, 206);
            reviewText.ForeColor = p.Text;
            btnReview.Invalidate();
            Theme.ApplyTitleBar(this);
            foreach (Control c in topBar.Controls) c.Invalidate();
            list.Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyTitleBar(this);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            LayoutTopBar();
            list.Focus();
            Activate();
        }

        private void LoadWindowBounds()
        {
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(S(1240), S(760));
            string[] parts = settings.WindowBounds.Split(',');
            int x, y, w, h;
            if (parts.Length == 4 && int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y)
                && int.TryParse(parts[2], out w) && int.TryParse(parts[3], out h))
            {
                var bounds = new Rectangle(x, y, w, h);
                foreach (Screen screen in Screen.AllScreens)
                {
                    if (!screen.WorkingArea.IntersectsWith(bounds)) continue;
                    StartPosition = FormStartPosition.Manual;
                    Bounds = bounds;
                    break;
                }
            }
            if (settings.WindowMaximized) WindowState = FormWindowState.Maximized;
        }

        private void SaveWindowState()
        {
            Rectangle b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            settings.WindowBounds = string.Format(Inv, "{0},{1},{2},{3}", b.X, b.Y, b.Width, b.Height);
            settings.WindowMaximized = WindowState == FormWindowState.Maximized;
            settings.Save();
        }

        private void RestoreColumnWidths()
        {
            foreach (string part in settings.ColumnWidths.Split(','))
            {
                int colon = part.IndexOf(':');
                int width;
                if (colon <= 0 || !int.TryParse(part.Substring(colon + 1), out width)) continue;
                ListColumn col = list.Columns.Find(c => c.Key == part.Substring(0, colon));
                if (col != null && !col.Fill) col.Width = Math.Max(col.MinWidth, Math.Min(600, width));
            }
        }

        private void SaveColumnWidths()
        {
            var parts = new List<string>();
            foreach (ListColumn c in list.Columns)
            {
                if (!c.Fill) parts.Add(c.Key + ":" + c.Width.ToString(Inv));
            }
            settings.ColumnWidths = string.Join(",", parts);
            settings.Save();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.F:
                case Keys.Control | Keys.E:
                    search.Box.Focus();
                    search.Box.SelectAll();
                    return true;
                case Keys.F5:
                    RequestRefresh();
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // =====================================================================
        // Talking to the background engine
        // =====================================================================

        private void ConnectEvents()
        {
            activateEvent = OpenOrCreate(Ipc.UiActivateEvent, false);
            closeEvent = OpenOrCreate(Ipc.UiCloseEvent, false);
            activateWait = ThreadPool.RegisterWaitForSingleObject(activateEvent, delegate { SafeInvoke(ActivateWindow); }, null, -1, false);
            closeWait = ThreadPool.RegisterWaitForSingleObject(closeEvent, delegate
            {
                SafeInvoke(delegate
                {
                    engineAskedUsToClose = true; // tray "Exit": close everything
                    Close();
                });
            }, null, -1, false);
            UpdateShowEvent();
        }

        /// <summary>Without an engine, this window answers "Show" requests from shortcuts itself.</summary>
        private void UpdateShowEvent()
        {
            bool want = !engineRunning && !closing;
            if (want && showEvent == null)
            {
                showEvent = OpenOrCreate(Ipc.ShowEvent, true);
                showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, delegate { SafeInvoke(ActivateWindow); }, null, -1, false);
            }
            else if (!want && showEvent != null)
            {
                showWait.Unregister(null);
                showEvent.Dispose();
                showEvent = null;
                showWait = null;
            }
        }

        private static EventWaitHandle OpenOrCreate(string name, bool openToStandardUsers)
        {
            IntPtr h = Ipc.CreateEvent(name, openToStandardUsers);
            var wait = new EventWaitHandle(false, EventResetMode.AutoReset);
            wait.SafeWaitHandle = new SafeWaitHandle(h, true);
            return wait;
        }

        private void ActivateWindow()
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show();
            Activate();
        }

        private void SafeInvoke(Action action)
        {
            if (closing || !IsHandleCreated) return;
            try { BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }

        // =====================================================================
        // Sampling (background thread)
        // =====================================================================

        private void Tick(object state)
        {
            if (!Monitor.TryEnter(tickLock)) return;
            try
            {
                if (closing) return;
                SampleResult sample = sampler.Sample();
                if (sample == null) return;

                HashSet<int> windows = WindowFinder.GetAppWindowProcessIds();
                Dictionary<int, double> net = network.TakeRates();
                var info = new Dictionary<int, AppInfo>(sample.Processes.Count);
                foreach (ProcInfo p in sample.Processes)
                {
                    p.HasWindow = windows.Contains(p.Pid);
                    double rate;
                    if (net.TryGetValue(p.Pid, out rate)) p.NetBytesPerSec = rate;
                    info[p.Pid] = metadata.Get(p);
                }
                metadata.Prune(sample.Processes);
                List<ProcessGroup> groups = ProcessTree.Group(sample.Processes);

                var snap = new Snapshot
                {
                    Groups = groups,
                    Info = info,
                    ProcessCount = sample.Processes.Count,
                    Cpu = sample.CpuPercent,
                    MemoryLoad = sample.MemoryLoad,
                    TotalPhys = sample.TotalPhysBytes,
                    Disk = disk.ActivePercent(),
                    Network = network.InterfaceUtilization(),
                    EngineRunning = Engine.IsRunning()
                };

                if (snap.EngineRunning)
                {
                    // The engine enforces; we only show its state.
                    if (statusReader == null) statusReader = StatusChannel.Open();
                    bool paused;
                    Dictionary<string, GovernorState> states;
                    if (statusReader != null && statusReader.TryRead(out paused, out states))
                    {
                        snap.Paused = paused;
                        snap.States = states;
                    }
                    else if (snapshot != null)
                    {
                        snap.Paused = snapshot.Paused;
                        snap.States = snapshot.States;
                    }
                }
                else
                {
                    if (engineRunning) limits.Reload(); // the engine just stopped: take over
                    snap.States = limits.Enforce(groups, true);
                    snap.Paused = limits.Paused;
                }
                if (snap.States == null) snap.States = new Dictionary<string, GovernorState>(StringComparer.OrdinalIgnoreCase);
                // Learn what each unlimited app normally uses: that's where its minimum limit comes from.
                limits.Profiles.Observe(groups, limits.Rules.Snapshot(), !snap.Paused);
                engineRunning = snap.EngineRunning;

                if (!closing) SafeInvoke(delegate { ApplySnapshot(snap); });
            }
            catch (Exception ex)
            {
                Log.Error("Sampling", ex);
            }
            finally
            {
                if (!closing)
                {
                    try { timer.Change(settings.RefreshMs, Timeout.Infinite); }
                    catch (ObjectDisposedException) { }
                }
                Monitor.Exit(tickLock);
            }
        }

        private void RequestRefresh()
        {
            if (closing || timer == null) return;
            try { timer.Change(0, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
        }

        // =====================================================================
        // Rendering (UI thread)
        // =====================================================================

        private void ApplySnapshot(Snapshot snap)
        {
            if (closing) return;
            snapshot = snap;
            UpdateShowEvent();

            SetTop(ColCpu, Percent(snap.Cpu));
            SetTop(ColMem, snap.MemoryLoad.ToString(Inv) + "%");
            SetTop(ColDisk, Percent(snap.Disk));
            SetTop(ColNet, Percent(snap.Network));

            btnPause.Text = snap.Paused ? "Resume limits" : "Pause limits";
            btnPause.Glyph = snap.Paused ? "" : "";
            btnPause.Enabled = limits.Rules.Count > 0 || snap.Paused;
            UpdateReviewBar();
            LayoutTopBar();
            RebuildRows();
        }

        private void SetTop(string key, string text)
        {
            ListColumn c = list.Columns.Find(x => x.Key == key);
            if (c != null) c.TopText = text;
        }

        private static string Percent(double v)
        {
            return Math.Round(v).ToString("0", Inv) + "%";
        }

        private void RebuildRows()
        {
            if (snapshot == null) return;
            Snapshot snap = snapshot;
            IDictionary<string, ProcessRule> rules = limits.Rules.Snapshot();
            string filter = search.Box.Text.Trim();

            var visible = new List<ProcessGroup>();
            var filterExpands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ProcessGroup g in snap.Groups)
            {
                bool childOnly;
                if (Matches(g, snap, filter, out childOnly))
                {
                    visible.Add(g);
                    if (childOnly) filterExpands.Add(g.Name);
                }
            }
            Comparison<ProcessGroup> byColumn = GroupComparison(snap, rules);
            visible.Sort(byColumn);

            var rows = new List<ListRow>(visible.Count + 8);
            if (settings.GroupByType && filter.Length == 0)
            {
                var sections = new[] { new List<ProcessGroup>(), new List<ProcessGroup>(), new List<ProcessGroup>() };
                foreach (ProcessGroup g in visible) sections[SectionOf(g, snap)].Add(g);
                string[] names = { "Apps", "Background processes", "Windows processes" };
                for (int i = 0; i < 3; i++)
                {
                    if (sections[i].Count == 0) continue;
                    rows.Add(new ListRow { Kind = RowKind.Section, Key = "s:" + i, Text = names[i] + " (" + sections[i].Count + ")" });
                    foreach (ProcessGroup g in sections[i]) AddGroupRows(rows, g, snap, rules, filterExpands);
                }
            }
            else
            {
                foreach (ProcessGroup g in visible) AddGroupRows(rows, g, snap, rules, filterExpands);
            }

            list.SetRows(rows);
            UpdateButtons();
        }

        private static int SectionOf(ProcessGroup g, Snapshot snap)
        {
            if (g.HasWindow) return 0;
            AppInfo info;
            bool windows = ProtectedProcesses.IsWindowsComponent(g.Name)
                           || (snap.Info.TryGetValue(g.Root.Pid, out info) && info.InWindowsFolder);
            return windows ? 2 : 1;
        }

        private bool Matches(ProcessGroup g, Snapshot snap, string filter, out bool childOnly)
        {
            childOnly = false;
            if (filter.Length == 0) return true;
            if (Contains(g.Name, filter) || Contains(Describe(g.Root, snap), filter)) return true;
            int pid;
            bool isPid = int.TryParse(filter, out pid);
            foreach (ProcInfo p in g.Processes)
            {
                if ((isPid && p.Pid == pid) || Contains(p.Name, filter) || Contains(Describe(p, snap), filter))
                {
                    childOnly = g.Processes.Count > 1;
                    return true;
                }
            }
            return false;
        }

        private static bool Contains(string text, string part)
        {
            return text != null && text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Describe(ProcInfo p, Snapshot snap)
        {
            AppInfo info;
            return snap.Info.TryGetValue(p.Pid, out info) && !string.IsNullOrEmpty(info.Description) ? info.Description : p.Name;
        }

        private void AddGroupRows(List<ListRow> rows, ProcessGroup g, Snapshot snap, IDictionary<string, ProcessRule> rules, HashSet<string> filterExpands)
        {
            ProcessRule rule;
            rules.TryGetValue(g.Name, out rule);
            GovernorState state;
            snap.States.TryGetValue(g.Name, out state);
            bool canLimit = !ProtectedProcesses.CannotLimit(g.Name);

            string key = "g:" + g.Name;
            bool expandable = g.Processes.Count > 1;
            bool isExpanded = expandable && (expanded.Contains(key) || filterExpands.Contains(g.Name));
            AppInfo info;
            snap.Info.TryGetValue(g.Root.Pid, out info);
            string name = info != null && !string.IsNullOrEmpty(info.Description) ? info.Description : g.Name;

            var row = new ListRow
            {
                Kind = RowKind.Group,
                Key = key,
                Text = expandable ? name + " (" + g.Processes.Count.ToString(Inv) + ")" : name,
                Expandable = expandable,
                Expanded = isExpanded,
                InExpandedBlock = isExpanded,
                Icon = info != null ? info.Icon : null,
                Group = g,
                Proc = g.Root,
                LimitsEditable = canLimit
            };
            FillValues(row, g.CpuPercent, g.PrivateBytes, g.DiskBytesPerSec, g.NetBytesPerSec, snap);
            FillLimits(row, rule);
            SetStatus(row, rule, state, snap.Paused, canLimit);
            rows.Add(row);

            if (!isExpanded) return;
            var children = new List<ProcInfo>(g.Processes);
            children.Sort(ProcessComparison(snap));
            foreach (ProcInfo p in children)
            {
                AppInfo childInfo;
                snap.Info.TryGetValue(p.Pid, out childInfo);
                var child = new ListRow
                {
                    Kind = RowKind.Process,
                    Key = "p:" + p.Pid.ToString(Inv) + ":" + p.CreateTime.ToString(Inv),
                    Text = childInfo != null && !string.IsNullOrEmpty(childInfo.Description) ? childInfo.Description : p.Name,
                    Depth = 1,
                    InExpandedBlock = true,
                    Icon = childInfo != null ? childInfo.Icon : null,
                    Group = g,
                    Proc = p
                };
                FillValues(child, p.CpuPercent, p.PrivateBytes, p.DiskBytesPerSec, p.NetBytesPerSec, snap);
                child.Cells = child.Cells ?? new string[list.Columns.Count];
                rows.Add(child);
            }
        }

        private void FillValues(ListRow row, double cpu, long memory, double diskBps, double netBps, Snapshot snap)
        {
            int n = list.Columns.Count;
            row.Cells = new string[n];
            row.Heat = new double[n];
            for (int i = 0; i < n; i++) row.Heat[i] = double.NaN;

            double mb = memory / (1024.0 * 1024.0);
            double diskMBps = diskBps / (1024.0 * 1024.0);
            double mbps = netBps * 8 / 1e6;
            double memRef = Math.Max(512.0, snap.TotalPhys / (1024.0 * 1024.0) / 4); // a quarter of RAM = full colour

            Set(row, ColCpu, cpu < 0.05 ? "0%" : cpu.ToString("0.0", Inv) + "%", Math.Sqrt(cpu / 100.0));
            Set(row, ColMem, mb.ToString("#,0.0", Inv) + " MB", Math.Sqrt(Math.Min(1, mb / memRef)));
            Set(row, ColDisk, diskMBps < 0.05 ? "0 MB/s" : diskMBps.ToString("0.0", Inv) + " MB/s", Math.Sqrt(Math.Min(1, diskMBps / 100)));
            Set(row, ColNet, mbps < 0.05 ? "0 Mbps" : mbps.ToString("0.0", Inv) + " Mbps", Math.Sqrt(Math.Min(1, mbps / 100)));
        }

        private void Set(ListRow row, string key, string text, double heat)
        {
            int i = list.Columns.FindIndex(c => c.Key == key);
            row.Cells[i] = text;
            row.Heat[i] = heat;
        }

        private void FillLimits(ListRow row, ProcessRule rule)
        {
            int n = list.Columns.Count;
            row.EditValues = new string[n];
            if (rule == null) return;
            SetLimit(row, ColMaxCpu, rule.MaxCpuPercent, rule.MaxCpuPercent + "%", rule.MaxCpuPercent.ToString(Inv));
            SetLimit(row, ColMaxMem, rule.MaxRamMB, rule.MaxRamMB.ToString("#,0", Inv) + " MB", rule.MaxRamMB.ToString(Inv));
            SetLimit(row, ColMaxDisk, rule.MaxDiskMBps, rule.MaxDiskMBps.ToString(Inv) + " MB/s", rule.MaxDiskMBps.ToString(Inv));
            double mbps = NetworkQos.ToBitsPerSecond(rule.MaxNetKBps) / 1e6;
            string net = mbps.ToString(mbps < 10 ? "0.##" : "0.#", Inv);
            SetLimit(row, ColMaxNet, rule.MaxNetKBps, net + " Mbps", net);
        }

        private void SetLimit(ListRow row, string key, int value, string text, string edit)
        {
            if (value <= 0) return;
            int i = list.Columns.FindIndex(c => c.Key == key);
            row.Cells[i] = text;
            row.EditValues[i] = edit;
        }

        private static void SetStatus(ListRow row, ProcessRule rule, GovernorState state, bool paused, bool canLimit)
        {
            Palette p = Theme.Current;
            if (rule == null || !canLimit) return;
            if (paused)
            {
                row.Status = "Limits paused";
                row.StatusColor = p.SubText;
                return;
            }
            switch (state)
            {
                case GovernorState.Throttling:
                    row.Status = "Limited · throttling";
                    row.StatusColor = p.Warn;
                    break;
                case GovernorState.NoAccess:
                    row.Status = "Access denied";
                    row.StatusColor = p.Bad;
                    break;
                default:
                    row.Status = "Limited";
                    row.StatusColor = p.Good;
                    break;
            }
        }

        // ---------------- Sorting ----------------

        private void SortBy(ListColumn column)
        {
            if (list.SortKey == column.Key) list.SortAscending = !list.SortAscending;
            else
            {
                list.SortKey = column.Key;
                list.SortAscending = column.Key == ColName || column.Key == ColStatus;
            }
            settings.SortColumn = list.SortKey;
            settings.SortAscending = list.SortAscending;
            settings.Save();
            RebuildRows();
        }

        private Comparison<ProcessGroup> GroupComparison(Snapshot snap, IDictionary<string, ProcessRule> rules)
        {
            string key = list.SortKey;
            int sign = list.SortAscending ? 1 : -1;
            return (a, b) =>
            {
                int c;
                switch (key)
                {
                    case ColName: c = string.Compare(Describe(a.Root, snap), Describe(b.Root, snap), StringComparison.CurrentCultureIgnoreCase); break;
                    case ColStatus: c = StatusRank(a, rules, snap).CompareTo(StatusRank(b, rules, snap)); break;
                    case ColCpu: c = a.CpuPercent.CompareTo(b.CpuPercent); break;
                    case ColDisk: c = a.DiskBytesPerSec.CompareTo(b.DiskBytesPerSec); break;
                    case ColNet: c = a.NetBytesPerSec.CompareTo(b.NetBytesPerSec); break;
                    case ColMaxCpu: c = LimitOf(a, rules, 0).CompareTo(LimitOf(b, rules, 0)); break;
                    case ColMaxMem: c = LimitOf(a, rules, 1).CompareTo(LimitOf(b, rules, 1)); break;
                    case ColMaxDisk: c = LimitOf(a, rules, 2).CompareTo(LimitOf(b, rules, 2)); break;
                    case ColMaxNet: c = LimitOf(a, rules, 3).CompareTo(LimitOf(b, rules, 3)); break;
                    default: c = a.PrivateBytes.CompareTo(b.PrivateBytes); break;
                }
                c *= sign;
                // Stable tie-break so equal rows don't jump around on every refresh.
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            };
        }

        private Comparison<ProcInfo> ProcessComparison(Snapshot snap)
        {
            string key = list.SortKey;
            int sign = list.SortAscending ? 1 : -1;
            return (a, b) =>
            {
                int c;
                switch (key)
                {
                    case ColName: c = string.Compare(Describe(a, snap), Describe(b, snap), StringComparison.CurrentCultureIgnoreCase); break;
                    case ColCpu: c = a.CpuPercent.CompareTo(b.CpuPercent); break;
                    case ColDisk: c = a.DiskBytesPerSec.CompareTo(b.DiskBytesPerSec); break;
                    case ColNet: c = a.NetBytesPerSec.CompareTo(b.NetBytesPerSec); break;
                    default: c = a.PrivateBytes.CompareTo(b.PrivateBytes); break;
                }
                c *= sign;
                return c != 0 ? c : a.Pid.CompareTo(b.Pid);
            };
        }

        private static int StatusRank(ProcessGroup g, IDictionary<string, ProcessRule> rules, Snapshot snap)
        {
            if (!rules.ContainsKey(g.Name)) return 0;
            GovernorState s;
            snap.States.TryGetValue(g.Name, out s);
            return 1 + (int)s;
        }

        private static int LimitOf(ProcessGroup g, IDictionary<string, ProcessRule> rules, int which)
        {
            ProcessRule r;
            if (!rules.TryGetValue(g.Name, out r)) return 0;
            switch (which)
            {
                case 0: return r.MaxCpuPercent;
                case 1: return r.MaxRamMB;
                case 2: return r.MaxDiskMBps;
                default: return r.MaxNetKBps;
            }
        }

        private void ToggleExpand(ListRow row)
        {
            if (row.Expanded) expanded.Remove(row.Key);
            else expanded.Add(row.Key);
            RebuildRows();
        }

        private void UpdateButtons()
        {
            ListRow row = list.SelectedRow;
            btnEnd.Enabled = row != null && row.Kind != RowKind.Section;
        }

        // =====================================================================
        // Limits
        // =====================================================================

        private static string BlockedReason(ListRow row)
        {
            string name = row.Group != null ? row.Group.Name : "";
            return ProtectedProcesses.IsSelf(name)
                ? "Task Manager Pro can't limit itself."
                : "This is part of Windows. Limits can't be set on it, to keep the system stable.";
        }

        private void List_LimitEdited(object sender, LimitEditEventArgs e)
        {
            ProcessGroup g = e.Row.Group;
            if (g == null) return;
            if (limits.Rules.NeedsReview)
            {
                e.Error = "First review the limits file that was changed outside Task Manager Pro (yellow bar at the top).";
                return;
            }
            string text = e.Text.Replace(",", "").Replace("%", "").Replace("Mbps", "").Replace("MB/s", "").Replace("MB", "").Trim();
            int value = 0;

            if (e.Column.Key == ColMaxNet)
            {
                double mbps = 0;
                if (text.Length > 0 && (!double.TryParse(text, NumberStyles.Float, Inv, out mbps) || mbps < 0))
                {
                    e.Error = "Enter the upload limit in Mbps (for example 2 or 0.5), or leave it empty for no limit.";
                    return;
                }
                value = mbps <= 0 ? 0 : Math.Max(1, (int)Math.Round(mbps * 1e6 / 8 / 1024));
            }
            else if (text.Length > 0 && (!int.TryParse(text, NumberStyles.Integer, Inv, out value) || value < 0))
            {
                e.Error = "Enter a whole number, or leave it empty for no limit.";
                return;
            }
            // The lowest allowed limit comes from this app's own usage (never a fixed list).
            LimitFloors floors = limits.FloorsFor(g.Name, g);
            string app = e.Row.Text;
            int paren = app.LastIndexOf(" (", StringComparison.Ordinal);
            if (paren > 0) app = app.Substring(0, paren);
            switch (e.Column.Key)
            {
                case ColMaxCpu: e.Error = LimitPolicy.CheckCpu(value, floors, app); break;
                case ColMaxMem: e.Error = LimitPolicy.CheckMemory(value, floors, app); break;
                case ColMaxDisk: e.Error = LimitPolicy.CheckDisk(value, floors, app); break;
                case ColMaxNet: e.Error = LimitPolicy.CheckNetwork(value, floors, app); break;
            }
            if (e.Error != null) return;

            ProcessRule current = limits.Rules.Get(g.Name);
            ProcessRule rule = current != null ? current.Clone() : new ProcessRule { Name = g.Name };
            switch (e.Column.Key)
            {
                case ColMaxCpu: rule.MaxCpuPercent = value; break;
                case ColMaxMem: rule.MaxRamMB = value; break;
                case ColMaxDisk: rule.MaxDiskMBps = value; break;
                case ColMaxNet: rule.MaxNetKBps = value; break;
            }
            SaveRule(g, rule);
        }

        private void SaveRule(ProcessGroup g, ProcessRule rule)
        {
            bool local = !engineRunning;
            if (!local) limits.Reload();
            bool saved = limits.SetRule(g.Name, rule, g, local);
            if (!local) Ipc.Signal(Ipc.ConfigEvent);
            if (!saved)
            {
                MessageBox.Show(this, "The limit could not be saved to:\n" + AppPaths.RulesFile + "\n\nDetails: " + AppPaths.LogFile,
                    Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            RebuildRows();
            RequestRefresh();
        }

        private void TogglePause()
        {
            bool paused = snapshot != null && snapshot.Paused;
            if (engineRunning) Ipc.Signal(paused ? Ipc.ResumeEvent : Ipc.PauseEvent);
            else limits.SetPaused(!paused, snapshot != null ? snapshot.Groups : null);
            RequestRefresh();
        }

        private void RemoveAllLimits()
        {
            int count = limits.Rules.Count;
            if (count == 0)
            {
                MessageBox.Show(this, "No limits are set.", Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this, string.Format(Inv, "Remove the limits of all {0} apps?\nEvery app goes back to normal right away.", count),
                    Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            bool local = !engineRunning;
            limits.ClearAll(snapshot != null ? snapshot.Groups : null, local);
            if (!local) Ipc.Signal(Ipc.ConfigEvent);
            RebuildRows();
            RequestRefresh();
        }

        private void ToggleBackgroundEngine()
        {
            bool enable = !settings.BackgroundEngine;
            settings.BackgroundEngine = enable;
            settings.Save();
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = enable ? StartupManager.Enable(exePath) : StartupManager.Disable();
                if (!enable && Engine.IsRunning()) Ipc.Signal(Ipc.ExitEvent); // this window takes over until it closes
                if (!ok)
                {
                    SafeInvoke(delegate
                    {
                        MessageBox.Show(this, "Windows Task Scheduler did not accept the change. See " + AppPaths.LogFile,
                            Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    });
                }
            });
        }

        // =====================================================================
        // Row actions
        // =====================================================================

        private void ShowRowMenu(ListRow row, Point screen)
        {
            rowMenu.Items.Clear();
            if (row.Expandable)
                Add(rowMenu.Items, row.Expanded ? "Collapse" : "Expand", false, delegate { ToggleExpand(row); }, null);

            var pids = new List<int>();
            bool hasWindow = false;
            foreach (ProcInfo p in row.Kind == RowKind.Group ? row.Group.Processes : new List<ProcInfo> { row.Proc })
            {
                pids.Add(p.Pid);
                hasWindow |= p.HasWindow;
            }
            if (hasWindow) Add(rowMenu.Items, "Switch to", false, delegate { WindowFinder.SwitchTo(pids); }, null);
            Add(rowMenu.Items, "End task", false, delegate { EndTask(row); }, null);

            if (row.Kind == RowKind.Group && row.LimitsEditable)
            {
                rowMenu.Items.Add(new ToolStripSeparator());
                Add(rowMenu.Items, "Set limits", false, delegate
                {
                    ListRow fresh = FindRow(row.Key) ?? row;
                    list.BeginEdit(fresh, list.Columns.Find(c => c.Key == ColMaxCpu));
                }, null);
                if (limits.Rules.Get(row.Group.Name) != null)
                    Add(rowMenu.Items, "Remove limits", false, delegate { SaveRule(row.Group, null); }, null);
            }

            AppInfo info = snapshot != null && row.Proc != null && snapshot.Info.ContainsKey(row.Proc.Pid) ? snapshot.Info[row.Proc.Pid] : null;
            if (info != null && info.Path != null)
            {
                string path = info.Path;
                rowMenu.Items.Add(new ToolStripSeparator());
                Add(rowMenu.Items, "Open file location", false, delegate { ProcessActions.OpenFileLocation(path); }, null);
                Add(rowMenu.Items, "Properties", false, delegate { ProcessActions.ShowProperties(path); }, null);
            }
            StyleMenu(rowMenu);
            rowMenu.Show(screen);
        }

        private ListRow FindRow(string key)
        {
            foreach (ListRow r in list.Rows)
            {
                if (r.Key == key) return r;
            }
            return null;
        }

        /// <summary>
        /// Like Task Manager: an app row ends all of its processes, a process row only that process.
        /// Ending a Windows component asks first; critical system processes can't be ended.
        /// </summary>
        private void EndTask(ListRow row)
        {
            if (row == null || row.Kind == RowKind.Section) return;
            List<ProcInfo> targets = row.Kind == RowKind.Group ? new List<ProcInfo>(row.Group.Processes) : new List<ProcInfo> { row.Proc };
            string name = row.Kind == RowKind.Group ? row.Group.Name : row.Proc.Name;
            int self = NativeMethods.GetCurrentProcessId();

            if (targets.Exists(p => p.Pid == self))
            {
                MessageBox.Show(this, "To close Task Manager Pro, use Exit in the ••• menu.", Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (ProtectedProcesses.IsCritical(name))
            {
                MessageBox.Show(this, "\"" + row.Text + "\" is a critical Windows process. Ending it would make Windows shut down or sign you out, so it can't be ended.",
                    Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (ProtectedProcesses.IsWindowsComponent(name) &&
                MessageBox.Show(this, "Do you want to end the system process \"" + row.Text + "\"?\n\nEnding it can make Windows unstable until it restarts.",
                    Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            string label = row.Text;
            ThreadPool.QueueUserWorkItem(delegate
            {
                int failed;
                ProcessActions.Terminate(targets, out failed);
                SafeInvoke(delegate
                {
                    if (failed > 0)
                        MessageBox.Show(this, "Some processes of \"" + label + "\" could not be ended (access denied).",
                            Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    RequestRefresh();
                });
            });
        }

        // =====================================================================
        // Misc
        // =====================================================================

        private void OpenDataFolder()
        {
            try
            {
                AppPaths.EnsureDataDir();
                Process.Start("explorer.exe", "\"" + AppPaths.DataDir + "\"");
            }
            catch (Exception ex)
            {
                Log.Error("Open data folder", ex);
            }
        }

        private void ShowAbout()
        {
            Version v = Assembly.GetExecutingAssembly().GetName().Version;
            MessageBox.Show(this,
                Program.AppName + " v" + v.ToString(2) + "\n\n" +
                "A lightweight task manager that can limit how much CPU, memory, disk and network each app may use.\n" +
                "Free and open source (MIT License): https://github.com/mdabsiddk/Task-Manager-Pro\n\n" +
                "Limits stay in place after you close this window. " +
                (settings.BackgroundEngine
                    ? "The background engine also applies the exact limits to apps you open later."
                    : "Apps you open later start with lower priority; turn on \"Run in background\" for exact limits.") + "\n\n" +
                "Limits: " + AppPaths.RulesFile + "\nSettings: " + AppPaths.SettingsFile + "\nLog: " + AppPaths.LogFile,
                "About " + Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel) return;
            closing = true;
            list.CancelEdit();
            if (timer != null) timer.Dispose();
            lock (tickLock) { } // wait for a running sample to finish
            SaveWindowState();

            foreach (RegisteredWaitHandle w in new[] { activateWait, closeWait, showWait })
            {
                if (w != null) w.Unregister(null);
            }
            if (showEvent != null) showEvent.Dispose();
            showEvent = null;

            // Limits stay on the running apps; only Pause / removing limits / uninstall undo them.
            if (!engineRunning)
            {
                limits.Detach();
                if (settings.BackgroundEngine && !engineAskedUsToClose && e.CloseReason != CloseReason.WindowsShutDown)
                {
                    IntPtr h;
                    int pid;
                    if (NativeMethods.RunProcess(exePath, "--engine", false, 0, out h, out pid) == 0) NativeMethods.CloseHandle(h);
                }
            }

            limits.Profiles.Save();
            network.Dispose();
            disk.Dispose();
            sampler.Dispose();
            if (statusReader != null) statusReader.Dispose();
            Theme.Changed -= OnThemeChanged;
        }
    }
}
