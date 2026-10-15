using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CoreLasso
{
    /// <summary>ListView that does not flicker during the 2-second auto-refresh.</summary>
    internal sealed class BufferedListView : ListView
    {
        public BufferedListView()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            View = View.Details;
            FullRowSelect = true;
            HideSelection = false;
        }
    }

    public sealed class MainForm : Form
    {
        // ---- CPU selection ----
        readonly Dictionary<int, CheckBox> _cpuBoxes = new Dictionary<int, CheckBox>();
        bool _suppress;

        // ---- controls ----
        ListView      _lvProcs, _lvRules;
        TextBox       _txtFilter, _txtLog, _txtMaskHex;
        Label         _lblSummary, _lblTarget, _lblHeader;
        ComboBox      _cmbPreset, _cmbPriority;
        RadioButton   _rbCpuSets, _rbAffinity, _rbBoth;
        CheckBox      _chkWatcher, _chkReapply, _chkLiveFollow, _chkHideSystem;
        NumericUpDown _numInterval;
        Button        _btnApply;
        SplitContainer _splitVert, _splitHorz;
        FlowLayoutPanel _cpuFlow;

        readonly List<Rule> _rules = new List<Rule>();
        Watcher _watcher;
        Timer   _refreshTimer;
        int     _selectedPid = -1;
        string  _selectedName = "";

        static readonly Color PColor = Color.FromArgb(215, 233, 255);
        static readonly Color EColor = Color.FromArgb(255, 234, 205);

        public MainForm()
        {
            Text = "CoreLasso - CPU affinity & CPU-set rules" +
                   (ProcessOps.IsElevated() ? "   [Administrator]" : "");
            StartPosition = FormStartPosition.CenterScreen;
            var work = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(1180, work.Width - 60), Math.Min(900, work.Height - 60));
            MinimumSize = new Size(Math.Min(900, work.Width), Math.Min(620, work.Height));
            Font = new Font("Segoe UI", 9f);

            BuildUi();
            LoadTopologyHeader();
            BuildCpuGrid();

            _rules.AddRange(RuleStore.Load());
            RefreshRulesView();

            _watcher = new Watcher(() => _rules.Select(r => r.Clone()).ToList());
            _watcher.Logged += OnWatcherLog;

            _refreshTimer = new Timer { Interval = 2000 };
            _refreshTimer.Tick += (s, e) => { if (_chkLiveFollow.Checked) RefreshProcesses(true); };
            _refreshTimer.Start();

            RefreshProcesses(false);
            SelectPreset(0);

            FormClosing += (s, e) =>
            {
                try { if (_watcher != null) _watcher.Stop(); } catch { }
                try { RuleStore.Save(_rules); } catch { }
            };
        }

        /// <summary>
        /// Splitter geometry must be applied after Dock.Fill has given the containers
        /// their real size. Setting Panel1MinSize/Panel2MinSize/SplitterDistance during
        /// construction throws, because at that point the container is still at its
        /// default size and the two minimums do not fit inside it.
        /// </summary>
        static void ConfigureSplit(SplitContainer sc, int min1, int min2, int distance)
        {
            try
            {
                int total = (sc.Orientation == Orientation.Horizontal ? sc.Height : sc.Width)
                            - sc.SplitterWidth;
                if (total <= 0) return;

                // If the container is too small to honour both minimums, scale them down
                // rather than throwing.
                if (min1 + min2 > total) { min1 = total / 3; min2 = total / 3; }

                sc.Panel1MinSize = Math.Max(0, min1);
                sc.Panel2MinSize = Math.Max(0, min2);
                sc.SplitterDistance = Math.Min(Math.Max(distance, min1), total - min2);
            }
            catch { /* splitter geometry is cosmetic; never let it stop startup */ }
        }

        void LayoutSplitters()
        {
            ConfigureSplit(_splitVert, 320, 140, _splitVert.Height - 190);
            ConfigureSplit(_splitHorz, 300, 460, 430);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            LayoutSplitters();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            LayoutSplitters();   // sizes are final here; idempotent
        }

        // ================= UI construction =================
        //
        // Everything below uses TableLayoutPanel rather than stacked Dock.Top/Dock.Fill
        // siblings. Dock layout order depends on z-order (last control in the Controls
        // collection is positioned first), which is easy to get wrong and is what made
        // the header overlap the body. A table has no such ambiguity.

        static TableLayoutPanel Rows(params SizeType[] kinds)
        {
            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = kinds.Length,
                Margin = Padding.Empty,
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            foreach (var k in kinds)
                t.RowStyles.Add(k == SizeType.Percent
                    ? new RowStyle(SizeType.Percent, 100f)
                    : new RowStyle(SizeType.AutoSize));
            return t;
        }

        static Button Btn(string text, EventHandler onClick)
        {
            var b = new Button
            {
                Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10, 5, 10, 5), Margin = new Padding(0, 0, 6, 0),
                MinimumSize = new Size(0, 30),
            };
            if (onClick != null) b.Click += onClick;
            return b;
        }

        static FlowLayoutPanel Row(bool wrap)
        {
            return new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight, WrapContents = wrap,
                Margin = Padding.Empty, Padding = Padding.Empty,
            };
        }

        void BuildUi()
        {
            var root = Rows(SizeType.AutoSize, SizeType.Percent);
            Controls.Add(root);

            _lblHeader = new Label
            {
                Dock = DockStyle.Top, AutoSize = true,
                Padding = new Padding(12, 9, 12, 9),
                BackColor = Color.FromArgb(243, 246, 250),
                ForeColor = Color.FromArgb(35, 35, 35),
            };
            root.Controls.Add(_lblHeader, 0, 0);

            _splitVert = new SplitContainer
            {
                Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
                SplitterWidth = 6,   // min sizes + distance are applied in LayoutSplitters()
            };
            root.Controls.Add(_splitVert, 0, 1);

            _splitHorz = new SplitContainer
            {
                Dock = DockStyle.Fill, Orientation = Orientation.Vertical,
                SplitterWidth = 6,   // min sizes + distance are applied in LayoutSplitters()
            };
            _splitVert.Panel1.Controls.Add(_splitHorz);

            BuildProcessPanel(_splitHorz.Panel1);
            BuildControlPanel(_splitHorz.Panel2);
            BuildBottomTabs(_splitVert.Panel2);
        }

        void BuildProcessPanel(Control host)
        {
            var box = new GroupBox
            {
                Dock = DockStyle.Fill, Text = "Running processes", Padding = new Padding(8, 4, 8, 8),
            };
            host.Controls.Add(box);

            var grid = Rows(SizeType.AutoSize, SizeType.AutoSize, SizeType.Percent);
            box.Controls.Add(grid);

            var bar = Row(true);
            bar.Controls.Add(new Label
            { Text = "Filter:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });

            _txtFilter = new TextBox { Width = 150, Margin = new Padding(0, 3, 6, 0) };
            _txtFilter.TextChanged += (s, e) => RefreshProcesses(true);
            bar.Controls.Add(_txtFilter);

            bar.Controls.Add(Btn("Refresh", (s, e) => RefreshProcesses(false)));

            _chkLiveFollow = new CheckBox
            { Text = "Auto", AutoSize = true, Checked = true, Margin = new Padding(0, 7, 0, 0) };
            bar.Controls.Add(_chkLiveFollow);
            grid.Controls.Add(bar, 0, 0);

            _chkHideSystem = new CheckBox
            {
                Text = "Hide system / inaccessible processes", AutoSize = true, Checked = true,
                Margin = new Padding(0, 2, 0, 4),
            };
            _chkHideSystem.CheckedChanged += (s, e) => RefreshProcesses(false);
            grid.Controls.Add(_chkHideSystem, 0, 1);

            _lvProcs = new BufferedListView { Dock = DockStyle.Fill, MultiSelect = false };
            _lvProcs.Columns.Add("Process", 145);
            _lvProcs.Columns.Add("PID", 52, HorizontalAlignment.Right);
            _lvProcs.Columns.Add("Thr", 38, HorizontalAlignment.Right);
            _lvProcs.Columns.Add("Current CPUs", 135);
            _lvProcs.SelectedIndexChanged += OnProcessSelected;
            grid.Controls.Add(_lvProcs, 0, 2);
        }

        void BuildControlPanel(Control host)
        {
            // Plain docking rather than a Percent row: a TableLayoutPanel would not hand
            // the leftover height to the CPU grid once the two auto-sized boxes below it
            // had taken their natural size. Added Fill-first, then bottom-most last.
            var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 4, 6, 6) };
            host.Controls.Add(pad);

            pad.Controls.Add(BuildCpuBox());        // Fill   - added first
            pad.Controls.Add(BuildModeBox());       // Bottom
            pad.Controls.Add(BuildActionsPanel());  // Bottom - added last, sits lowest
        }

        GroupBox BuildCpuBox()
        {
            var box = new GroupBox
            {
                Dock = DockStyle.Fill, Text = "CPU selection", Padding = new Padding(8, 4, 8, 8),
            };
            var grid = Rows(SizeType.AutoSize, SizeType.AutoSize, SizeType.Percent);
            box.Controls.Add(grid);

            var bar = Row(true);
            bar.Controls.Add(new Label
            { Text = "Preset:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });

            _cmbPreset = new ComboBox
            {
                Width = 290, DropDownStyle = ComboBoxStyle.DropDownList,
                Margin = new Padding(0, 3, 8, 0),
            };
            _cmbPreset.SelectedIndexChanged += (s, e) => SelectPreset(_cmbPreset.SelectedIndex);
            bar.Controls.Add(_cmbPreset);

            bar.Controls.Add(Btn("All", (s, e) => SetMask(Topology.PresetAll())));
            bar.Controls.Add(Btn("Invert",
                (s, e) => SetMask(~CurrentMask() & Topology.SystemMaskGroup0)));

            bar.Controls.Add(new Label
            { Text = "Mask 0x", AutoSize = true, Margin = new Padding(8, 7, 2, 0) });

            _txtMaskHex = new TextBox { Width = 86, Margin = new Padding(0, 3, 0, 0) };
            _txtMaskHex.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                ulong m;
                if (ulong.TryParse(_txtMaskHex.Text.Trim().Replace("0x", "").Replace("0X", ""),
                        System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out m))
                    SetMask(m & Topology.SystemMaskGroup0);
                else
                    UpdateSummary();
            };
            bar.Controls.Add(_txtMaskHex);
            grid.Controls.Add(bar, 0, 0);

            _lblSummary = new Label
            {
                Dock = DockStyle.Top, AutoSize = true,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Margin = new Padding(0, 6, 0, 4),
            };
            grid.Controls.Add(_lblSummary, 0, 1);

            _cpuFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, AutoScroll = true, Name = "cpuflow",
                Margin = Padding.Empty, Padding = new Padding(0, 2, 0, 2),
            };
            grid.Controls.Add(_cpuFlow, 0, 2);
            return box;
        }

        GroupBox BuildModeBox()
        {
            var box = new GroupBox
            {
                Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Text = "How to apply", Padding = new Padding(10, 4, 10, 8),
            };
            var grid = Rows(SizeType.AutoSize, SizeType.AutoSize, SizeType.AutoSize, SizeType.AutoSize);
            grid.Dock = DockStyle.Top;                       // Fill + AutoSize parent == collapsed box
            grid.AutoSize = true;
            grid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            box.Controls.Add(grid);

            _rbCpuSets = new RadioButton
            {
                AutoSize = true,
                Text = "CPU Sets only - scheduler hint, reversible, best for performance tuning",
            };
            _rbAffinity = new RadioButton
            {
                AutoSize = true,
                Text = "Hard affinity only - the process SEES fewer CPUs (this fixes old games)",
            };
            _rbBoth = new RadioButton
            {
                AutoSize = true, Checked = true,
                Text = "Both (recommended) - hard affinity + matching CPU sets",
            };
            grid.Controls.Add(_rbCpuSets,  0, 0);
            grid.Controls.Add(_rbAffinity, 0, 1);
            grid.Controls.Add(_rbBoth,     0, 2);

            var row = Row(true);
            row.Margin = new Padding(0, 6, 0, 0);
            _chkReapply = new CheckBox
            {
                AutoSize = true, Margin = new Padding(0, 4, 20, 0),
                Text = "Keep re-applying (for apps that reset their own affinity)",
            };
            row.Controls.Add(_chkReapply);
            row.Controls.Add(new Label
            { Text = "Priority:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });

            _cmbPriority = new ComboBox
            {
                Width = 125, DropDownStyle = ComboBoxStyle.DropDownList,
                Margin = new Padding(0, 3, 0, 0),
            };
            _cmbPriority.Items.AddRange(new object[]
            { "Unchanged", "Idle", "BelowNormal", "Normal", "AboveNormal", "High" });
            _cmbPriority.SelectedIndex = 0;
            row.Controls.Add(_cmbPriority);
            grid.Controls.Add(row, 0, 3);

            return box;
        }

        Control BuildActionsPanel()
        {
            var grid = Rows(SizeType.AutoSize, SizeType.AutoSize);
            grid.Dock = DockStyle.Bottom;
            grid.AutoSize = true;
            grid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            grid.Padding = new Padding(0, 6, 0, 2);

            _lblTarget = new Label
            {
                Dock = DockStyle.Top, AutoSize = true, Text = "No process selected.",
                ForeColor = Color.FromArgb(70, 70, 70), Margin = new Padding(2, 0, 2, 5),
            };
            grid.Controls.Add(_lblTarget, 0, 0);

            var row = Row(true);
            _btnApply = Btn("Apply to selected process", OnApplyClicked);
            row.Controls.Add(_btnApply);
            row.Controls.Add(Btn("Reset to all CPUs", OnResetClicked));
            row.Controls.Add(Btn("Save as rule", OnSaveRuleClicked));
            row.Controls.Add(Btn("Launch .exe with rule...", OnLaunchClicked));
            row.Controls.Add(Btn("Is this safe?", (s, e) => ShowSafetyInfo()));
            grid.Controls.Add(row, 0, 1);

            return grid;
        }

        void BuildBottomTabs(Control host)
        {
            var tabs = new TabControl { Dock = DockStyle.Fill };
            host.Controls.Add(tabs);

            // ---- Rules tab ----
            var tabRules = new TabPage("Rules & auto-apply watcher") { Padding = new Padding(6) };
            tabs.TabPages.Add(tabRules);

            var grid = Rows(SizeType.AutoSize, SizeType.Percent);
            tabRules.Controls.Add(grid);

            var bar = Row(true);
            _chkWatcher = new CheckBox
            {
                Text = "Watcher ON - apply rules automatically when a process starts",
                AutoSize = true, Margin = new Padding(0, 7, 16, 0),
            };
            _chkWatcher.CheckedChanged += OnWatcherToggled;
            bar.Controls.Add(_chkWatcher);

            bar.Controls.Add(new Label
            { Text = "Poll ms:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });

            _numInterval = new NumericUpDown
            {
                Width = 68, Minimum = 150, Maximum = 10000, Increment = 50, Value = 500,
                Margin = new Padding(0, 3, 10, 0),
            };
            _numInterval.ValueChanged += (s, e) =>
            {
                if (_watcher != null) _watcher.IntervalMs = (int)_numInterval.Value;
            };
            bar.Controls.Add(_numInterval);

            bar.Controls.Add(Btn("Enable/disable", (s, e) => ToggleSelectedRule()));
            bar.Controls.Add(Btn("Load into editor", (s, e) => LoadSelectedRuleIntoEditor()));
            bar.Controls.Add(Btn("Delete", (s, e) => DeleteSelectedRule()));
            bar.Controls.Add(Btn("Open config folder", (s, e) =>
            {
                try
                {
                    System.IO.Directory.CreateDirectory(RuleStore.Dir);
                    Process.Start("explorer.exe", RuleStore.Dir);
                }
                catch (Exception ex) { Warn(ex.Message); }
            }));
            grid.Controls.Add(bar, 0, 0);

            _lvRules = new BufferedListView { Dock = DockStyle.Fill };
            _lvRules.Columns.Add("On", 40);
            _lvRules.Columns.Add("Executable", 170);
            _lvRules.Columns.Add("CPUs", 230);
            _lvRules.Columns.Add("Mode", 100);
            _lvRules.Columns.Add("Priority", 90);
            _lvRules.Columns.Add("Re-apply", 70);
            _lvRules.Columns.Add("Path filter", 180);
            _lvRules.DoubleClick += (s, e) => LoadSelectedRuleIntoEditor();
            grid.Controls.Add(_lvRules, 0, 1);

            // ---- Log tab ----
            var tabLog = new TabPage("Activity log") { Padding = new Padding(6) };
            tabs.TabPages.Add(tabLog);
            _txtLog = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
                ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9f),
                BackColor = Color.White,
            };
            tabLog.Controls.Add(_txtLog);
        }

        // ================= topology / CPU grid =================

        void LoadTopologyHeader()
        {
            try
            {
                var cpus = Topology.Cpus;
                int pThreads = cpus.Count(c => c.IsPerformanceCore);
                int eThreads = cpus.Count(c => !c.IsPerformanceCore);
                int physP = cpus.Count(c => c.IsPerformanceCore && !c.IsSmtSibling);
                int physE = cpus.Count(c => !c.IsPerformanceCore && !c.IsSmtSibling);

                string line1 = cpus.Count + " logical CPUs";
                if (Topology.HybridDetected)
                    line1 += "     " + physP + " P-cores / " + pThreads + " threads  (" +
                             Topology.RangeString(Topology.PresetPCores()) + ")" +
                             "     " + physE + " E-cores / " + eThreads + " threads  (" +
                             Topology.RangeString(Topology.PresetECores()) + ")";
                else
                    line1 += "     homogeneous (no P/E split detected)";

                string line2 = Topology.MultiGroup
                    ? "NOTE: more than one processor group detected - hard affinity applies to group 0 only."
                    : "Changes are per-process and vanish when the process exits. Nothing is written to the registry.";
                line2 += ProcessOps.IsElevated()
                    ? "   Running as administrator."
                    : "   Not elevated - admin-only programs will need \"Run as Admin.cmd\".";

                _lblHeader.Text = line1 + "\r\n" + line2;
            }
            catch (Exception ex)
            {
                _lblHeader.Text = "Could not read CPU topology: " + ex.Message;
            }
        }

        void BuildCpuGrid()
        {
            if (_cpuFlow == null) return;
            _cpuFlow.SuspendLayout();
            _cpuFlow.Controls.Clear();
            _cpuBoxes.Clear();

            var cores = Topology.Cpus
                .GroupBy(c => new { c.Group, c.CoreIndex })
                .OrderBy(g => g.Key.Group)
                .ThenBy(g => g.Min(c => c.CpuIndex))
                .ToList();

            foreach (var core in cores)
            {
                var members = core.OrderBy(c => c.CpuIndex).ToList();
                bool isP = members[0].IsPerformanceCore;

                var panel = new Panel
                {
                    Width = 104,
                    Height = 26 + 22 * members.Count,
                    BackColor = isP ? PColor : EColor,
                    Margin = new Padding(4),
                    BorderStyle = BorderStyle.FixedSingle,
                };

                panel.Controls.Add(new Label
                {
                    Text = (isP ? "P-core " : "E-core ") + core.Key.CoreIndex,
                    Left = 5, Top = 4, Width = 92, Height = 16,
                    Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                    ForeColor = Color.FromArgb(40, 40, 40),
                });

                int y = 22;
                foreach (var cpu in members)
                {
                    var cb = new CheckBox
                    {
                        Text = cpu.ShortLabel, Left = 6, Top = y, Width = 92, Height = 20,
                        Tag = cpu, Font = new Font("Segoe UI", 8.5f),
                    };
                    if (cpu.Parked) { cb.ForeColor = Color.Gray; cb.Text += " *"; }
                    cb.CheckedChanged += (s, e) => { if (!_suppress) UpdateSummary(); };
                    panel.Controls.Add(cb);
                    _cpuBoxes[cpu.MaskBit] = cb;
                    y += 22;
                }
                _cpuFlow.Controls.Add(panel);
            }
            _cpuFlow.ResumeLayout();

            _suppress = true;
            _cmbPreset.Items.Clear();
            _cmbPreset.Items.AddRange(new object[]
            {
                "All CPUs (no restriction)",
                "P-cores only (all P threads)",
                "P-cores, 1 thread per core (no HT)",
                "E-cores only",
                "Legacy game: first 2 P-cores, no HT",
                "Legacy game: first 4 P-cores, no HT",
                "Legacy game: first 4 CPUs (2 P-cores + HT)",
                "Custom (use checkboxes)",
            });
            _cmbPreset.SelectedIndex = 0;
            _suppress = false;
        }

        void SelectPreset(int idx)
        {
            ulong m;
            switch (idx)
            {
                case 0: m = Topology.PresetAll(); break;
                case 1: m = Topology.PresetPCores(); break;
                case 2: m = Topology.PresetPNoSmt(); break;
                case 3: m = Topology.PresetECores(); break;
                case 4: m = Topology.PresetFirstPhysicalPCores(2); break;
                case 5: m = Topology.PresetFirstPhysicalPCores(4); break;
                case 6: m = Topology.PresetFirstPCoresWithSmt(2); break;
                default: return;   // Custom - leave checkboxes alone
            }
            SetMask(m);
        }

        ulong CurrentMask()
        {
            ulong m = 0;
            foreach (var kv in _cpuBoxes)
                if (kv.Value.Checked) m |= 1UL << kv.Key;
            return m;
        }

        void SetMask(ulong mask)
        {
            bool prev = _suppress;
            _suppress = true;
            foreach (var kv in _cpuBoxes)
                kv.Value.Checked = (mask & (1UL << kv.Key)) != 0;
            _suppress = prev;
            UpdateSummary();
        }

        void UpdateSummary()
        {
            if (_lblSummary == null) return;
            ulong m = CurrentMask();
            int n = Topology.PopCount(m);
            if (_txtMaskHex != null) _txtMaskHex.Text = m.ToString("X");
            _lblSummary.Text = n == 0
                ? "Nothing selected - Apply is blocked (an empty mask would freeze the process)."
                : "Selected: " + n + " logical CPUs  ->  " + Topology.DescribeMask(m);
            _lblSummary.ForeColor = n == 0 ? Color.Firebrick : Color.FromArgb(20, 90, 30);
            if (_btnApply != null) _btnApply.Enabled = n > 0 && _selectedPid > 0;
        }

        ApplyMode Mode()
        {
            if (_rbCpuSets.Checked) return ApplyMode.CpuSets;
            if (_rbAffinity.Checked) return ApplyMode.HardAffinity;
            return ApplyMode.Both;
        }

        PriorityChoice Priority()
        {
            return (PriorityChoice)Enum.Parse(typeof(PriorityChoice), (string)_cmbPriority.SelectedItem);
        }

        // ================= process list =================

        void RefreshProcesses(bool preserveScroll)
        {
            string filter = _txtFilter.Text.Trim();
            int topIdx = 0;
            try { if (preserveScroll && _lvProcs.TopItem != null) topIdx = _lvProcs.TopItem.Index; } catch { }

            var rows = new List<ListViewItem>();
            Process[] all;
            try { all = Process.GetProcesses(); }
            catch (Exception ex) { Log("Cannot enumerate processes: " + ex.Message); return; }

            foreach (var p in all)
            {
                string name;
                int pid;
                try { name = p.ProcessName; pid = p.Id; }
                catch { try { p.Dispose(); } catch { } continue; }

                try
                {
                    if (filter.Length > 0 && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    if (_chkHideSystem.Checked && Safety.IsCritical(name)) continue;

                    var snap = ProcessOps.Read(p);
                    if (_chkHideSystem.Checked && !snap.Accessible) continue;

                    string cpus;
                    if (!snap.Accessible) cpus = "(no access)";
                    else
                    {
                        ulong eff = snap.HasCpuSetRestriction
                            ? (snap.AffinityMask & snap.CpuSetMask)
                            : snap.AffinityMask;
                        cpus = eff == Topology.SystemMaskGroup0
                            ? "all " + Topology.PopCount(eff)
                            : Topology.PopCount(eff) + ": " + Topology.DescribeMask(eff);
                    }

                    var it = new ListViewItem(name);
                    it.SubItems.Add(pid.ToString());
                    it.SubItems.Add(snap.Threads.ToString());
                    it.SubItems.Add(cpus);
                    it.Tag = snap;
                    if (!snap.Accessible) it.ForeColor = Color.Gray;
                    else if (snap.HasCpuSetRestriction ||
                             (snap.AffinityMask != 0 && snap.AffinityMask != Topology.SystemMaskGroup0))
                        it.ForeColor = Color.FromArgb(0, 100, 0);
                    rows.Add(it);
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }

            rows.Sort((a, b) => string.Compare(a.Text, b.Text, StringComparison.OrdinalIgnoreCase));

            _lvProcs.BeginUpdate();
            try
            {
                _lvProcs.Items.Clear();
                _lvProcs.Items.AddRange(rows.ToArray());
                if (_selectedPid > 0)
                {
                    foreach (ListViewItem it in _lvProcs.Items)
                    {
                        var s = (ProcSnapshot)it.Tag;
                        if (s.Pid == _selectedPid) { it.Selected = true; break; }
                    }
                }
                if (preserveScroll && topIdx > 0 && topIdx < _lvProcs.Items.Count)
                {
                    try { _lvProcs.TopItem = _lvProcs.Items[topIdx]; } catch { }
                }
            }
            finally { _lvProcs.EndUpdate(); }
        }

        void OnProcessSelected(object sender, EventArgs e)
        {
            if (_lvProcs.SelectedItems.Count == 0) return;
            var snap = _lvProcs.SelectedItems[0].Tag as ProcSnapshot;
            if (snap == null) return;

            bool changedTarget = snap.Pid != _selectedPid;
            _selectedPid = snap.Pid;
            _selectedName = snap.Name;

            string flags = snap.HasCpuSetRestriction ? "   [has CPU-set restriction]" : "";
            _lblTarget.Text = "Target: " + snap.Name + "  (PID " + snap.Pid + ")  priority " +
                              (snap.PriorityText.Length > 0 ? snap.PriorityText : "?") + flags +
                              (Safety.IsCritical(snap.Name) ? "   -- BLOCKED: critical system process" : "");
            _lblTarget.ForeColor = Safety.IsCritical(snap.Name) ? Color.Firebrick : Color.FromArgb(70, 70, 70);

            // Load the process's live mask into the editor, but only when the user actually
            // picks a different process - otherwise the 2-second auto-refresh would keep
            // overwriting a selection they are still editing.
            if (changedTarget && snap.Accessible && snap.AffinityMask != 0)
            {
                ulong show = snap.HasCpuSetRestriction
                    ? (snap.AffinityMask & snap.CpuSetMask) : snap.AffinityMask;
                if (show != 0)
                {
                    _suppress = true;
                    _cmbPreset.SelectedIndex = _cmbPreset.Items.Count - 1;  // Custom
                    _suppress = false;
                    SetMask(show);
                }
            }
            UpdateSummary();
        }

        // ================= actions =================

        void OnApplyClicked(object sender, EventArgs e)
        {
            if (_selectedPid <= 0) { Warn("Select a process first."); return; }
            ulong mask = CurrentMask();
            var mode = Mode();

            string reason;
            if (!Safety.Validate(_selectedName, mask, out reason)) { Blocked(reason); return; }

            var warnings = Safety.Warnings(_selectedName, mask, mode);
            if (warnings.Count > 0)
            {
                string msg = "About to apply to " + _selectedName + " (PID " + _selectedPid + "):\r\n" +
                "  " + Topology.DescribeMask(mask) + "\r\n  mode: " + mode + "\r\n\r\n" +
                             string.Join("\r\n\r\n", warnings.Select(w => "- " + w).ToArray()) +
                             "\r\n\r\nThis is fully reversible: press \"Reset to all CPUs\", or just restart the process.\r\n\r\nProceed?";
                if (MessageBox.Show(this, msg, "Confirm", MessageBoxButtons.OKCancel,
                        MessageBoxIcon.Warning) != DialogResult.OK) return;
            }

            var res = ProcessOps.Apply(_selectedPid, _selectedName, mask, mode, Priority());
            if (res.Success)
            {
                Log("Applied to " + _selectedName + " (PID " + _selectedPid + "): " +
                    Topology.DescribeMask(mask) + " via " + mode + " -> " + res);
            }
            else
            {
                Log("FAILED on " + _selectedName + " (PID " + _selectedPid + "): " + res.Error);
                if (res.ElevationRequired) OfferElevation(res.Error);
                else Warn(res.Error);
            }
            RefreshProcesses(true);
        }

        void OnResetClicked(object sender, EventArgs e)
        {
            if (_selectedPid <= 0) { Warn("Select a process first."); return; }
            var res = ProcessOps.Reset(_selectedPid);
            Log("Reset " + _selectedName + " (PID " + _selectedPid + ") to all CPUs -> " + res);
            if (!res.Success) Warn(res.Error);
            RefreshProcesses(true);
        }

        void OnSaveRuleClicked(object sender, EventArgs e)
        {
            ulong mask = CurrentMask();
            string exe = _selectedName;

            if (string.IsNullOrEmpty(exe))
            {
                exe = Prompt("Executable name for this rule (e.g. game.exe):", "");
                if (string.IsNullOrEmpty(exe)) return;
            }

            string reason;
            if (!Safety.Validate(exe, mask, out reason)) { Blocked(reason); return; }

            var rule = new Rule
            {
                Exe = Safety.Normalize(exe),
                Mask = mask,
                Mode = Mode(),
                Priority = Priority(),
                Reapply = _chkReapply.Checked,
                Enabled = true,
            };

            var existing = _rules.FirstOrDefault(
                r => string.Equals(r.Exe, rule.Exe, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (MessageBox.Show(this, "A rule for \"" + rule.Exe + "\" already exists. Replace it?",
                "Replace rule", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
                _rules.Remove(existing);
            }

            _rules.Add(rule);
            RuleStore.Save(_rules);
            RefreshRulesView();
            Log("Saved rule: " + rule.Exe + " -> " + Topology.DescribeMask(rule.Mask) + " (" + rule.Mode + ")");

            if (!_chkWatcher.Checked)
                MessageBox.Show(this,
                    "Rule saved.\r\n\r\nTurn on the watcher (checkbox in the Rules tab) so it is applied " +
                    "automatically the next time " + rule.Exe + " starts.\r\n\r\n" +
                    "For an old game that crashes during startup, prefer \"Launch .exe with rule\" - " +
                    "that applies the limit before the game executes a single instruction.",
                    "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void OnLaunchClicked(object sender, EventArgs e)
        {
            ulong mask = CurrentMask();
            if (mask == 0) { Warn("Select at least one CPU first."); return; }

            using (var dlg = new OpenFileDialog
            {
                Title = "Choose the game / program to launch",
                Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*",
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                string exe = dlg.FileName;
                string reason;
                if (!Safety.Validate(exe, mask, out reason)) { Blocked(reason); return; }

                string args = Prompt("Command-line arguments (optional):", "");
                if (args == null) return;

                int pid;
                var res = ProcessOps.LaunchWithRule(exe, args, null, mask, Mode(), Priority(), out pid);
                if (res.Success)
                {
                    Log("Launched " + System.IO.Path.GetFileName(exe) + " (PID " + pid +
                    ") suspended, applied " + Topology.DescribeMask(mask) + " via " + Mode() +
                    ", then resumed -> " + res);
                }
                else
                {
                    Log("Launch FAILED: " + res.Error.Replace("\r\n", " "));
                    if (res.ElevationRequired) OfferElevation(res.Error);
                    else Warn(res.Error);
                }
                RefreshProcesses(false);
            }
        }

        void ShowSafetyInfo()
        {
            MessageBox.Show(this,
                "What this program does\r\n" +
                "----------------------\r\n" +
                "It sets two per-process kernel properties:\r\n\r\n" +
                "1. Hard affinity (SetProcessAffinityMask) - restricts which logical CPUs the\r\n" +
                "   process may run on, AND changes what the process sees when it asks the OS\r\n" +
                "   how many CPUs exist. That second part is what fixes old games whose CPU\r\n" +
                "   detection breaks on 20 logical processors or on hybrid P/E topologies.\r\n\r\n" +
                "2. CPU Sets (SetProcessDefaultCpuSets) - the modern equivalent. Friendlier to\r\n" +
                "   the Windows scheduler, but it does NOT change the reported CPU count, so on\r\n" +
                "   its own it will not fix a game that miscounts cores.\r\n\r\n" +
                "Why it cannot break your system\r\n" +
                "-------------------------------\r\n" +
                "- Both settings live in the kernel's process object. They die with the process.\r\n" +
                "  Restarting the app, or rebooting, restores defaults. There is nothing to undo.\r\n" +
                "- No driver, no service, no registry writes, no system-wide setting is touched.\r\n" +
                "  The only file written is " + RuleStore.Dir + ".\r\n" +
                "- OS-critical processes (csrss, lsass, services, svchost, dwm, ...) are hard\r\n" +
                "  blocked. Starving those is the one way affinity can genuinely hang Windows.\r\n" +
                "- An empty selection is refused: a process with zero allowed CPUs can never be\r\n" +
                "  scheduled again.\r\n" +
                "- The watcher only touches processes that start AFTER you enable it, and only\r\n" +
                "  those matching a rule you wrote yourself.\r\n\r\n" +
                "Known limits\r\n" +
                "------------\r\n" +
                "- Anti-cheat protected games (EAC, BattlEye, Vanguard) may refuse or revert\r\n" +
                "  external modification. Use \"Launch .exe with rule\" or the game's own options.\r\n" +
                "- Elevated processes need CoreLasso running as administrator.\r\n" +
                "- Restricting a process to fewer cores than it wants makes it slower. That is\r\n" +
                "  the intended trade when the alternative is a game that will not start.",
                "Safety and how it works", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ================= rules view =================

        void RefreshRulesView()
        {
            _lvRules.BeginUpdate();
            try
            {
                _lvRules.Items.Clear();
                foreach (var r in _rules)
                {
                    var it = new ListViewItem(r.Enabled ? "yes" : "no");
                    it.SubItems.Add(r.Exe);
                    it.SubItems.Add(Topology.DescribeMask(r.Mask));
                    it.SubItems.Add(r.Mode.ToString());
                    it.SubItems.Add(r.Priority.ToString());
                    it.SubItems.Add(r.Reapply ? "yes" : "no");
                    it.SubItems.Add(r.PathContains);
                    it.Tag = r;
                    if (!r.Enabled) it.ForeColor = Color.Gray;
                    _lvRules.Items.Add(it);
                }
            }
            finally { _lvRules.EndUpdate(); }
        }

        Rule SelectedRule()
        {
            return _lvRules.SelectedItems.Count == 0 ? null : _lvRules.SelectedItems[0].Tag as Rule;
        }

        void ToggleSelectedRule()
        {
            var r = SelectedRule();
            if (r == null) { Warn("Select a rule."); return; }
            r.Enabled = !r.Enabled;
            RuleStore.Save(_rules);
            RefreshRulesView();
            Log("Rule " + r.Exe + " " + (r.Enabled ? "enabled" : "disabled"));
        }

        void DeleteSelectedRule()
        {
            var r = SelectedRule();
            if (r == null) { Warn("Select a rule."); return; }
            if (MessageBox.Show(this, "Delete the rule for \"" + r.Exe + "\"?", "Delete",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _rules.Remove(r);
            RuleStore.Save(_rules);
            RefreshRulesView();
            Log("Deleted rule " + r.Exe);
        }

        void LoadSelectedRuleIntoEditor()
        {
            var r = SelectedRule();
            if (r == null) return;
            _suppress = true;
            _cmbPreset.SelectedIndex = _cmbPreset.Items.Count - 1;      // Custom
            _rbCpuSets.Checked  = r.Mode == ApplyMode.CpuSets;
            _rbAffinity.Checked = r.Mode == ApplyMode.HardAffinity;
            _rbBoth.Checked     = r.Mode == ApplyMode.Both;
            _cmbPriority.SelectedItem = r.Priority.ToString();
            _chkReapply.Checked = r.Reapply;
            _suppress = false;
            SetMask(r.Mask);
            Log("Loaded rule " + r.Exe + " into the editor. Edit the CPUs, then \"Save as rule\" to overwrite.");
        }

        void OnWatcherToggled(object sender, EventArgs e)
        {
            try
            {
                if (_chkWatcher.Checked)
                {
                    _watcher.IntervalMs = (int)_numInterval.Value;
                    _watcher.Start();
                }
                else _watcher.Stop();
            }
            catch (Exception ex)
            {
                Warn("Watcher error: " + ex.Message);
                _chkWatcher.Checked = false;
            }
        }

        void OnWatcherLog(string msg)
        {
            // Called from the watcher thread - marshal to the UI thread.
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)(() => Log(msg)));
            }
            catch { }
        }

        // ================= small helpers =================

        void Log(string msg)
        {
            if (_txtLog == null) return;
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg + "\r\n";
            if (_txtLog.TextLength > 80000) _txtLog.Clear();
            _txtLog.AppendText(line);
        }

        /// <summary>
        /// Offers a one-click elevated restart. CreateProcess cannot elevate a child, and
        /// ShellExecute("runas") cannot create one suspended, so for an exe that demands
        /// admin the only way to still apply the mask before first instruction is for
        /// CoreLasso itself to be elevated.
        /// </summary>
        void OfferElevation(string problem)
        {
            if (ProcessOps.IsElevated())
            {
                Warn(problem + "\r\n\r\nCoreLasso is already running as administrator, so this is " +
                               "most likely an anti-cheat protected process rather than a permissions problem. " +
                               "Protected processes refuse handles even to administrators.");
                return;
            }

            var dr = MessageBox.Show(this,
                problem + "\r\n\r\nRestart CoreLasso as administrator now?\r\n\r\n" +
                          "Your saved rules are kept. Note that a game launched from an elevated CoreLasso " +
                          "also runs elevated - which is what this program wants anyway.",
                          "Administrator rights needed", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (dr != DialogResult.Yes) return;

            try
            {
                RuleStore.Save(_rules);
                Process.Start(new ProcessStartInfo(Application.ExecutablePath)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                });
                Application.Exit();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // The user dismissed the UAC prompt. Stay where we are.
            }
            catch (Exception ex) { Warn("Could not restart elevated: " + ex.Message); }
        }

        void Warn(string msg)
        {
            MessageBox.Show(this, msg, "CoreLasso", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        void Blocked(string msg)
        {
            MessageBox.Show(this, msg, "Blocked for safety", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        static string Prompt(string label, string initial)
        {
            using (var f = new Form
            {
                Text = "CoreLasso", FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(460, 118),
                MinimizeBox = false, MaximizeBox = false, Font = new Font("Segoe UI", 9f),
            })
            {
                f.Controls.Add(new Label { Text = label, Left = 12, Top = 12, Width = 430, Height = 20 });
                var tb = new TextBox { Left = 12, Top = 36, Width = 430, Text = initial };
                f.Controls.Add(tb);
                var ok = new Button { Text = "OK", Left = 276, Top = 70, Width = 80, DialogResult = DialogResult.OK };
                var cancel = new Button
                { Text = "Cancel", Left = 362, Top = 70, Width = 80, DialogResult = DialogResult.Cancel };
                f.Controls.Add(ok);
                f.Controls.Add(cancel);
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                return f.ShowDialog() == DialogResult.OK ? tb.Text.Trim() : null;
            }
        }
    }
}
