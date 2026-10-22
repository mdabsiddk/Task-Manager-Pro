@echo off
title Smart Task Manager & Resource Limiter - Installer
color 0A

:: Check for Administrator privileges
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo [!] দয়া করে ফাইলটির ওপর Right-Click করে "Run as administrator" দিয়ে ওপেন করুন।
    pause
    exit /b
)

echo ========================================================
echo   Installing Smart Task Manager ^& Resource Governor...
echo ========================================================

set "INSTALL_DIR=%ProgramFiles%\SmartTaskManager"
if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"

echo [*] Extracting source code...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$lines = Get-Content -Path '%~f0'; " ^
  "$idx = [Array]::IndexOf($lines, '::CSHARP_CODE_BELOW::'); " ^
  "$code = $lines[($idx + 1)..($lines.Length - 1)]; " ^
  "[System.IO.File]::WriteAllLines('%INSTALL_DIR%\SmartTaskMgr.cs', $code, [System.Text.Encoding]::UTF8);"

echo [*] Compiling Lightweight Native Binary...
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

"%CSC%" /target:winexe /out:"%INSTALL_DIR%\SmartTaskMgr.exe" /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll "%INSTALL_DIR%\SmartTaskMgr.cs" >nul

if not exist "%INSTALL_DIR%\SmartTaskMgr.exe" (
    color 0C
    echo [X] কম্পাইল করতে সমস্যা হয়েছে!
    pause
    exit /b
)

echo [*] Creating Desktop Shortcut...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$s=(New-Object -COM WScript.Shell).CreateShortcut('%PUBLIC%\Desktop\Smart Task Manager.lnk'); " ^
  "$s.TargetPath='%INSTALL_DIR%\SmartTaskMgr.exe'; " ^
  "$s.WorkingDirectory='%INSTALL_DIR%'; " ^
  "$s.Save();"

echo.
echo [SUCCESS] ইনস্টলেশন সফলভাবে সম্পন্ন হয়েছে!
echo আপনার ডেস্কটপে "Smart Task Manager" নামে শর্টকাট তৈরি হয়েছে।
echo সফটওয়্যারটি এখন চালু হচ্ছে...
start "" "%INSTALL_DIR%\SmartTaskMgr.exe"
pause
exit /b

::CSHARP_CODE_BELOW::
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SmartTaskManager
{
    public class ProcessRule
    {
        public string Name;
        public int MaxCpuPercent;
        public int MaxRamMB;
        public int MaxDiskMBps;
        public int MaxNetKbps;
    }

    public class ProcessRow
    {
        public string Name;
        public int InstanceCount;
        public double CpuPercent;
        public long RamMB;
        public double DiskMBps;
        public bool HasWindow;
    }

    public class MainForm : Form
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetProcessWorkingSetSizeEx(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize, uint Flags);

        [DllImport("psapi.dll")]
        static extern bool EmptyWorkingSet(IntPtr hProcess);

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS lpIoCounters);

        const uint QUOTA_LIMITS_HARDWS_MAX_ENABLE = 0x00000004;

        private DataGridView grid;
        private Timer updateTimer;
        private NotifyIcon trayIcon;
        private CheckBox chkStartup;
        private Label lblSummary;

        private Dictionary<string, ProcessRule> rules = new Dictionary<string, ProcessRule>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<int, TimeSpan> prevCpuTime = new Dictionary<int, TimeSpan>();
        private Dictionary<int, ulong> prevIoBytes = new Dictionary<int, ulong>();
        private DateTime lastSampleTime = DateTime.UtcNow;

        private string sortColumn = "RamMB";
        private bool sortAscending = false;
        private bool isEditing = false;
        private string configPath;

        // Critical Windows processes that will never be throttled for safety
        private HashSet<string> criticalProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "System", "Idle", "smss", "csrss", "wininit", "services", "lsass", "svchost", "explorer", "winlogon", "dwm", "Memory Compression", "SmartTaskMgr"
        };

        public MainForm()
        {
            configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "limits.cfg");
            LoadRules();

            this.Text = "Smart Task Manager & Resource Governor (Safe Mode)";
            this.Size = new Size(1020, 620);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = new Font("Segoe UI", 9F);

            Panel topPanel = new Panel() { Dock = DockStyle.Top, Height = 45, BackColor = Color.FromArgb(240, 244, 248) };
            lblSummary = new Label() { Text = "স্ক্যান করা হচ্ছে...", Location = new Point(15, 13), AutoSize = true, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold) };

            chkStartup = new CheckBox() { Text = "উইন্ডোজ চালু হলে অটোমেটিক স্টার্ট হবে (Run All Year)", Location = new Point(680, 12), AutoSize = true };
            chkStartup.Checked = IsInStartup();
            chkStartup.CheckedChanged += ChkStartup_CheckedChanged;

            topPanel.Controls.Add(lblSummary);
            topPanel.Controls.Add(chkStartup);

            Label lblHint = new Label()
            {
                Dock = DockStyle.Bottom,
                Height = 28,
                Text = "  [টিপস] ডানপাশের সবুজ কলামগুলোতে ডাবল-ক্লিক করে সর্বোচ্চ লিমিট (সংখ্যা) বসিয়ে Enter চাপুন। লিমিট তুলে দিতে 0 লিখুন। কলামের নামে ক্লিক করে Sort করুন।",
                BackColor = Color.FromArgb(255, 250, 205),
                TextAlign = ContentAlignment.MiddleLeft
            };

            grid = new DataGridView()
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.None,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };

            // Enable Double Buffering to eliminate flicker and reduce CPU usage
            typeof(DataGridView).InvokeMember("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.SetProperty, null, grid, new object[] { true });

            SetupColumns();

            grid.ColumnHeaderMouseClick += Grid_ColumnHeaderMouseClick;
            grid.CellBeginEdit += (s, e) => { isEditing = true; };
            grid.CellEndEdit += Grid_CellEndEdit;

            this.Controls.Add(grid);
            this.Controls.Add(topPanel);
            this.Controls.Add(lblHint);

            // System Tray Icon setup
            trayIcon = new NotifyIcon()
            {
                Icon = SystemIcons.Shield,
                Text = "Smart Task Manager & Governor (Running)",
                Visible = true
            };
            ContextMenu trayMenu = new ContextMenu();
            trayMenu.MenuItems.Add("Open Task Manager", (s, e) => { this.Show(); this.WindowState = FormWindowState.Normal; });
            trayMenu.MenuItems.Add("Exit Completely", (s, e) => { trayIcon.Visible = false; Application.Exit(); });
            trayIcon.ContextMenu = trayMenu;
            trayIcon.DoubleClick += (s, e) => { this.Show(); this.WindowState = FormWindowState.Normal; };

            this.FormClosing += MainForm_FormClosing;

            updateTimer = new Timer() { Interval = 2500 }; // 2.5 sec interval for ultra-low overhead
            updateTimer.Tick += (s, e) => RefreshAndEnforce();
            updateTimer.Start();

            RefreshAndEnforce();
        }

        private void SetupColumns()
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn() { Name = "Name", HeaderText = "Software / Process", ReadOnly = true, FillWeight = 140 });
            grid.Columns.Add(new DataGridViewTextBoxColumn() { Name = "Status", HeaderText = "Type", ReadOnly = true, FillWeight = 85 });
            grid.Columns.Add(new DataGridViewTextBoxColumn() { Name = "Instances", HeaderText = "Processes", ReadOnly = true, FillWeight = 65 });
            grid.Columns.Add(new DataGridViewTextBoxColumn() { Name = "CpuPercent", HeaderText = "CPU (%)", ReadOnly = true, FillWeight = 75 });
            grid.Columns.Add(new DataGridViewTextBoxColumn() { Name = "RamMB", HeaderText = "Memory (MB)", ReadOnly = true, FillWeight = 90 });
            grid.Columns.Add(new DataGridViewTextBoxColumn() { Name = "DiskMBps", HeaderText = "Disk (MB/s)", ReadOnly = true, FillWeight = 85 });

            // 4 Editable Limit Columns
            Color editBg = Color.FromArgb(232, 248, 245);
            grid.Columns.Add(new DataGridViewTextBoxColumn() { Name = "LimitCPU", HeaderText = "Max CPU (%) ✎", ReadOnly = false, FillWeight = 90, DefaultCellStyle = new DataGridViewCellStyle() { BackColor = editBg, Alignment = DataGridViewContentAlignment.MiddleCenter } });
            grid.Columns.Add(new DataGridViewTextBoxColumn() { Name = "LimitRAM", HeaderText = "Max RAM (MB) ✎", ReadOnly = false, FillWeight = 95, DefaultCellStyle = new DataGridViewCellStyle() { BackColor = editBg, Alignment = DataGridViewContentAlignment.MiddleCenter } });
            grid.Columns.Add(new DataGridViewTextBoxColumn() { Name = "LimitDisk", HeaderText = "Max Disk (MB/s) ✎", ReadOnly = false, FillWeight = 100, DefaultCellStyle = new DataGridViewCellStyle() { BackColor = editBg, Alignment = DataGridViewContentAlignment.MiddleCenter } });
            grid.Columns.Add(new DataGridViewTextBoxColumn() { Name = "LimitNet", HeaderText = "Max Net (KB/s) ✎", ReadOnly = false, FillWeight = 100, DefaultCellStyle = new DataGridViewCellStyle() { BackColor = editBg, Alignment = DataGridViewContentAlignment.MiddleCenter } });

            foreach (DataGridViewColumn col in grid.Columns)
            {
                col.SortMode = DataGridViewColumnSortMode.Programmatic;
            }
        }

        private void RefreshAndEnforce()
        {
            if (isEditing) return;

            DateTime now = DateTime.UtcNow;
            double elapsedSec = (now - lastSampleTime).TotalSeconds;
            if (elapsedSec <= 0.1) elapsedSec = 1.0;
            lastSampleTime = now;

            Process[] allProcs = Process.GetProcesses();
            Dictionary<string, List<Process>> grouped = new Dictionary<string, List<Process>>(StringComparer.OrdinalIgnoreCase);

            foreach (Process p in allProcs)
            {
                if (p.Id <= 4) continue;
                if (!grouped.ContainsKey(p.ProcessName))
                    grouped[p.ProcessName] = new List<Process>();
                grouped[p.ProcessName].Add(p);
            }

            List<ProcessRow> rows = new List<ProcessRow>();
            Dictionary<int, TimeSpan> currentCpu = new Dictionary<int, TimeSpan>();
            Dictionary<int, ulong> currentIo = new Dictionary<int, ulong>();
            int coreCount = Environment.ProcessorCount;

            long totalSystemRamMB = 0;

            foreach (var kvp in grouped)
            {
                string procName = kvp.Key;
                List<Process> list = kvp.Value;

                double sumCpu = 0;
                long sumRamBytes = 0;
                double sumDiskBytesPerSec = 0;
                bool hasWin = false;

                foreach (Process p in list)
                {
                    try
                    {
                        if (p.MainWindowHandle != IntPtr.Zero) hasWin = true;
                        sumRamBytes += p.WorkingSet64;

                        TimeSpan cpuTime = p.TotalProcessorTime;
                        currentCpu[p.Id] = cpuTime;
                        if (prevCpuTime.ContainsKey(p.Id))
                        {
                            double cpuDelta = (cpuTime - prevCpuTime[p.Id]).TotalSeconds;
                            sumCpu += (cpuDelta / (elapsedSec * coreCount)) * 100.0;
                        }

                        IO_COUNTERS io;
                        if (GetProcessIoCounters(p.Handle, out io))
                        {
                            ulong totalIo = io.ReadTransferCount + io.WriteTransferCount;
                            currentIo[p.Id] = totalIo;
                            if (prevIoBytes.ContainsKey(p.Id) && totalIo >= prevIoBytes[p.Id])
                            {
                                sumDiskBytesPerSec += (totalIo - prevIoBytes[p.Id]) / elapsedSec;
                            }
                        }
                    }
                    catch { }
                }

                long ramMB = sumRamBytes / (1024 * 1024);
                totalSystemRamMB += ramMB;
                double diskMBps = sumDiskBytesPerSec / (1024.0 * 1024.0);
                if (sumCpu > 100.0) sumCpu = 100.0;

                // Enforce Limits Safely if Rule Exists
                if (rules.ContainsKey(procName) && !criticalProcesses.Contains(procName))
                {
                    EnforceRuleOnGroup(list, rules[procName], sumCpu, sumRamBytes, diskMBps, coreCount);
                }

                rows.Add(new ProcessRow()
                {
                    Name = procName,
                    InstanceCount = list.Count,
                    CpuPercent = Math.Round(sumCpu, 1),
                    RamMB = ramMB,
                    DiskMBps = Math.Round(diskMBps, 2),
                    HasWindow = hasWin
                });
            }

            prevCpuTime = currentCpu;
            prevIoBytes = currentIo;

            // Update UI only if visible (saves CPU/RAM when minimized to tray)
            if (this.Visible && this.WindowState != FormWindowState.Minimized)
            {
                UpdateGridUI(rows, allProcs.Length, totalSystemRamMB);
            }
        }

        private void EnforceRuleOnGroup(List<Process> procs, ProcessRule rule, double currentCpu, long currentRamBytes, double currentDiskMBps, int coreCount)
        {
            long maxRamBytes = (long)rule.MaxRamMB * 1024L * 1024L;
            long perProcRam = procs.Count > 0 ? maxRamBytes / procs.Count : maxRamBytes;
            if (perProcRam < 15 * 1024 * 1024) perProcRam = 15 * 1024 * 1024; // Safe minimum 15MB per process

            foreach (Process p in procs)
            {
                try
                {
                    // 1. Safe RAM Enforcement (Working Set Cap + Pagefile Trim)
                    if (rule.MaxRamMB > 0)
                    {
                        SetProcessWorkingSetSizeEx(p.Handle, (IntPtr)(1024 * 1024), (IntPtr)perProcRam, QUOTA_LIMITS_HARDWS_MAX_ENABLE);
                        if (currentRamBytes > maxRamBytes)
                        {
                            EmptyWorkingSet(p.Handle);
                        }
                    }

                    // 2. Safe CPU Enforcement (Priority Scaling + Core Affinity)
                    if (rule.MaxCpuPercent > 0)
                    {
                        int allowedCores = Math.Max(1, (int)Math.Ceiling((rule.MaxCpuPercent / 100.0) * coreCount));
                        long affinityMask = (1L << allowedCores) - 1;
                        p.ProcessorAffinity = (IntPtr)affinityMask;

                        if (currentCpu > rule.MaxCpuPercent)
                            p.PriorityClass = ProcessPriorityClass.Idle;
                        else
                            p.PriorityClass = ProcessPriorityClass.BelowNormal;
                    }

                    // 3. Safe Disk I/O Throttling
                    if (rule.MaxDiskMBps > 0 && currentDiskMBps > rule.MaxDiskMBps)
                    {
                        p.PriorityClass = ProcessPriorityClass.Idle;
                    }
                }
                catch { }
            }
        }

        private void ApplyNetworkQoS(string procName, int maxNetKbps)
        {
            try
            {
                string policyName = "SmartLimit_" + procName;
                string cmd = string.Format("Remove-NetQosPolicy -Name '{0}' -Confirm:$false -ErrorAction SilentlyContinue; ", policyName);
                if (maxNetKbps > 0)
                {
                    long bitsPerSec = (long)maxNetKbps * 8L * 1024L;
                    cmd += string.Format("New-NetQosPolicy -Name '{0}' -AppPathNameMatchCondition '{1}.exe' -ThrottleRateActionBitsPerSecond {2} -ErrorAction SilentlyContinue;", policyName, procName, bitsPerSec);
                }
                ProcessStartInfo psi = new ProcessStartInfo("powershell.exe", "-NoProfile -WindowStyle Hidden -Command \"" + cmd + "\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(psi);
            }
            catch { }
        }

        private void UpdateGridUI(List<ProcessRow> rows, int totalProcs, long totalRamMB)
        {
            lblSummary.Text = string.Format("মোট সফটওয়্যার গ্রুপ: {0} টি  |  মোট প্রসেস: {1} টি  |   ব্যবহৃত র‍্যাম: {2} MB  |  সক্রিয় লিমিট রুলস: {3} টি", rows.Count, totalProcs, totalRamMB, rules.Count);

            // Sort: Running Apps with UI on top first, or by user selected column
            rows.Sort((a, b) =>
            {
                // Keep active user apps on top before background processes unless sorting explicitly
                if (a.HasWindow != b.HasWindow && sortColumn == "RamMB" && !sortAscending)
                {
                    return b.HasWindow.CompareTo(a.HasWindow);
                }

                int cmp = 0;
                switch (sortColumn)
                {
                    case "Name": cmp = string.Compare(a.Name, b.Name, true); break;
                    case "Status": cmp = a.HasWindow.CompareTo(b.HasWindow); break;
                    case "Instances": cmp = a.InstanceCount.CompareTo(b.InstanceCount); break;
                    case "CpuPercent": cmp = a.CpuPercent.CompareTo(b.CpuPercent); break;
                    case "DiskMBps": cmp = a.DiskMBps.CompareTo(b.DiskMBps); break;
                    default: cmp = a.RamMB.CompareTo(b.RamMB); break;
                }
                return sortAscending ? cmp : -cmp;
            });

            string selectedProc = grid.CurrentRow != null ? grid.CurrentRow.Cells["Name"].Value as string : null;
            int firstVisibleRow = grid.FirstDisplayedScrollingRowIndex >= 0 ? grid.FirstDisplayedScrollingRowIndex : 0;

            grid.SuspendLayout();
            grid.Rows.Clear();

            foreach (var r in rows)
            {
                ProcessRule rule = rules.ContainsKey(r.Name) ? rules[r.Name] : null;
                string statusText = r.HasWindow ? "Running App" : "Background";

                int idx = grid.Rows.Add(
                    r.Name,
                    statusText,
                    r.InstanceCount,
                    r.CpuPercent.ToString("0.0") + " %",
                    r.RamMB.ToString() + " MB",
                    r.DiskMBps.ToString("0.00"),
                    rule != null && rule.MaxCpuPercent > 0 ? rule.MaxCpuPercent.ToString() : "",
                    rule != null && rule.MaxRamMB > 0 ? rule.MaxRamMB.ToString() : "",
                    rule != null && rule.MaxDiskMBps > 0 ? rule.MaxDiskMBps.ToString() : "",
                    rule != null && rule.MaxNetKbps > 0 ? rule.MaxNetKbps.ToString() : ""
                );

                if (r.HasWindow)
                {
                    grid.Rows[idx].Cells["Name"].Style.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
                }
                if (rule != null)
                {
                    grid.Rows[idx].DefaultCellStyle.BackColor = Color.FromArgb(255, 248, 220); // Highlight governed apps
                }
                if (selectedProc != null && string.Equals(r.Name, selectedProc, StringComparison.OrdinalIgnoreCase))
                {
                    grid.Rows[idx].Selected = true;
                }
            }

            if (firstVisibleRow < grid.Rows.Count)
            {
                try { grid.FirstDisplayedScrollingRowIndex = firstVisibleRow; } catch { }
            }
            grid.ResumeLayout();
        }

        private void Grid_ColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            string colName = grid.Columns[e.ColumnIndex].Name;
            if (colName.StartsWith("Limit")) return;

            if (sortColumn == colName)
                sortAscending = !sortAscending;
            else
            {
                sortColumn = colName;
                sortAscending = false;
            }
            RefreshAndEnforce();
        }

        private void Grid_CellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            isEditing = false;
            if (e.RowIndex < 0) return;

            string procName = grid.Rows[e.RowIndex].Cells["Name"].Value.ToString();
            if (criticalProcesses.Contains(procName))
            {
                MessageBox.Show("উইন্ডোজের নিরাপত্তার স্বার্থে '" + procName + "' সিস্টেম প্রসেসটিতে লিমিট দেওয়া যাবে না।", "Safety Protection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                RefreshAndEnforce();
                return;
            }

            int cpu = ParseCellInt(grid.Rows[e.RowIndex].Cells["LimitCPU"].Value);
            int ram = ParseCellInt(grid.Rows[e.RowIndex].Cells["LimitRAM"].Value);
            int disk = ParseCellInt(grid.Rows[e.RowIndex].Cells["LimitDisk"].Value);
            int net = ParseCellInt(grid.Rows[e.RowIndex].Cells["LimitNet"].Value);

            int prevNet = rules.ContainsKey(procName) ? rules[procName].MaxNetKbps : 0;

            if (cpu <= 0 && ram <= 0 && disk <= 0 && net <= 0)
            {
                if (rules.ContainsKey(procName)) rules.Remove(procName);
            }
            else
            {
                rules[procName] = new ProcessRule()
                {
                    Name = procName,
                    MaxCpuPercent = Math.Min(100, Math.Max(0, cpu)),
                    MaxRamMB = Math.Max(0, ram),
                    MaxDiskMBps = Math.Max(0, disk),
                    MaxNetKbps = Math.Max(0, net)
                };
            }

            if (net != prevNet)
            {
                ApplyNetworkQoS(procName, net);
            }

            SaveRules();
            RefreshAndEnforce();
        }

        private int ParseCellInt(object val)
        {
            if (val == null) return 0;
            int res;
            int.TryParse(val.ToString().Trim(), out res);
            return res;
        }

        private void SaveRules()
        {
            try
            {
                List<string> lines = new List<string>();
                foreach (var kvp in rules)
                {
                    var r = kvp.Value;
                    lines.Add(string.Format("{0}|{1}|{2}|{3}|{4}", r.Name, r.MaxCpuPercent, r.MaxRamMB, r.MaxDiskMBps, r.MaxNetKbps));
                }
                File.WriteAllLines(configPath, lines.ToArray());
            }
            catch { }
        }

        private void LoadRules()
        {
            try
            {
                if (!File.Exists(configPath)) return;
                string[] lines = File.ReadAllLines(configPath);
                foreach (string line in lines)
                {
                    string[] parts = line.Split('|');
                    if (parts.Length == 5)
                    {
                        rules[parts[0]] = new ProcessRule()
                        {
                            Name = parts[0],
                            MaxCpuPercent = int.Parse(parts[1]),
                            MaxRamMB = int.Parse(parts[2]),
                            MaxDiskMBps = int.Parse(parts[3]),
                            MaxNetKbps = int.Parse(parts[4])
                        };
                    }
                }
            }
            catch { }
        }

        private bool IsInStartup()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    return key != null && key.GetValue("SmartTaskManager") != null;
                }
            }
            catch { return false; }
        }

        private void ChkStartup_CheckedChanged(object sender, EventArgs e)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (chkStartup.Checked)
                        key.SetValue("SmartTaskManager", "\"" + Application.ExecutablePath + "\"");
                    else
                        key.DeleteValue("SmartTaskManager", false);
                }
            }
            catch { }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
                trayIcon.ShowBalloonTip(2000, "Smart Task Manager", "সফটওয়্যারটি ব্যাকগ্রাউন্ডে সচল আছে এবং আপনার সেট করা লিমিটগুলো নিয়ন্ত্রণ করছে।", ToolTipIcon.Info);
            }
        }

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}