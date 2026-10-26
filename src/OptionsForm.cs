using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace PttyPcTestMonitor;

/// <summary>Options window (opened from the monitor's right-click menu). Every change applies immediately.</summary>
public sealed class OptionsForm : Form
{
    private readonly MainForm _main;
    private readonly Settings _settings;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private bool _syncing;

    // General
    private RadioButton _viewFull, _viewCompact;
    private CheckBox _topMost, _slider, _doubleClick, _resetButton, _columnTitles;
    private TrackBar _opacity;
    private Label _opacityValue;
    private ComboBox _interval;

    // Items
    private TreeView _tree;
    private readonly List<CheckBox> _columnChecks = new(), _graphChecks = new();

    // Layout
    private NumericUpDown _gridColumns;
    private LayoutEditor _editor;

    // Tools
    private Button _logButton;
    private Label _pawnStatus;

    public OptionsForm(MainForm main)
    {
        _main = main;
        _settings = main.Settings;

        Text = $"{MainForm.AppName} — 옵션";
        Font = new Font("Malgun Gothic", 9f);
        AutoScaleMode = AutoScaleMode.Font;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(700, 700);
        MinimumSize = new Size(620, 620);
        ShowInTaskbar = false;
        MinimizeBox = false;

        _tabs.TabPages.Add(GeneralTab());
        _tabs.TabPages.Add(ItemsTab());
        _tabs.TabPages.Add(LayoutTab());
        _tabs.TabPages.Add(ToolsTab());

        var close = new Button { Text = "닫기", AutoSize = true, Anchor = AnchorStyles.Right };
        close.Click += (_, _) => Close();
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
        bottom.Controls.Add(close);
        CancelButton = close;

        _tabs.SelectedIndexChanged += (_, _) =>
        {
            switch ((string)_tabs.SelectedTab?.Tag)
            {
                case "items": BuildTree(); break; // fans appear while running
                case "layout": _editor.UpdateSize(); break;
                case "tools": SyncTools(); break;
            }
        };

        Controls.Add(_tabs);
        Controls.Add(bottom);
        SyncFromSettings();
    }

    public void SelectTab(string name)
    {
        if (name is null) return;
        var page = _tabs.TabPages.Cast<TabPage>().FirstOrDefault(p => (string)p.Tag == name);
        if (page is not null) _tabs.SelectedTab = page;
    }

    private void Changed()
    {
        if (_syncing) return;
        _main.SettingsChanged();
        _editor?.Invalidate();
    }

    // ---------------------------------------------------------------- general

    private TabPage GeneralTab()
    {
        var page = new TabPage("일반") { Tag = "general", Padding = new Padding(12) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };

        var view = new GroupBox { Text = "보기 모드", AutoSize = true, Padding = new Padding(10, 6, 10, 6) };
        var viewFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true };
        _viewFull = new RadioButton { Text = "기본 — 항목별 패널 (배치 탭에서 자유 배치)", AutoSize = true };
        _viewCompact = new RadioButton { Text = "간략히 — 배치는 그대로, 값만 간단히 (현재 ↓최소 ~평균 ↑최대)", AutoSize = true };
        _viewFull.CheckedChanged += (_, _) => { if (_viewFull.Checked) { _settings.Compact = false; Changed(); } };
        _viewCompact.CheckedChanged += (_, _) => { if (_viewCompact.Checked) { _settings.Compact = true; Changed(); } };
        viewFlow.Controls.AddRange(new Control[] { _viewFull, _viewCompact });
        view.Controls.Add(viewFlow);
        flow.Controls.Add(view);


        _topMost = Check("항상 위에 표시 (다른 창 위에 고정)", v => _settings.TopMost = v);
        flow.Controls.Add(_topMost);

        var opacityRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        _opacity = new TrackBar { Minimum = 20, Maximum = 100, TickFrequency = 10, Width = 260, SmallChange = 5, LargeChange = 10 };
        _opacityValue = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 8, 0, 0) };
        _opacity.ValueChanged += (_, _) =>
        {
            _opacityValue.Text = $"{_opacity.Value}%";
            if (_syncing) return;
            _settings.Opacity = _opacity.Value / 100.0;
            Changed();
        };
        opacityRow.Controls.Add(new Label { Text = "투명도", AutoSize = true, Margin = new Padding(0, 8, 8, 0) });
        opacityRow.Controls.Add(_opacity);
        opacityRow.Controls.Add(_opacityValue);
        flow.Controls.Add(opacityRow);

        _slider = Check("모니터 창 아래에 투명도 슬라이더 표시", v => _settings.ShowOpacitySlider = v);
        flow.Controls.Add(_slider);

        var intervalRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        _interval = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
        _interval.Items.AddRange(new object[] { "0.5초", "1초", "2초" });
        _interval.SelectedIndexChanged += (_, _) =>
        {
            if (_syncing) return;
            _settings.IntervalMs = new[] { 500, 1000, 2000 }[_interval.SelectedIndex];
            Changed();
        };
        intervalRow.Controls.Add(new Label { Text = "갱신 주기", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
        intervalRow.Controls.Add(_interval);
        flow.Controls.Add(intervalRow);

        flow.Controls.Add(new Label { Text = "초기화", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 14, 0, 2) });
        _resetButton = Check("제목줄에 초기화 버튼(↻) 표시", v => _settings.ShowResetButton = v);
        _doubleClick = Check("모니터 창 더블클릭으로 초기화 (실수 방지를 위해 기본 꺼짐)", v => _settings.DoubleClickReset = v);
        flow.Controls.Add(_resetButton);
        flow.Controls.Add(_doubleClick);

        flow.Controls.Add(new Label { Text = "패널", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 14, 0, 2) });
        _columnTitles = Check("패널마다 열 제목(현재/최소/평균/최대) 표시", v => _settings.ShowColumnTitles = v);
        flow.Controls.Add(_columnTitles);

        page.Controls.Add(flow);
        return page;
    }

    private CheckBox Check(string text, Action<bool> set)
    {
        var box = new CheckBox { Text = text, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        box.CheckedChanged += (_, _) =>
        {
            if (_syncing) return;
            set(box.Checked);
            Changed();
        };
        return box;
    }

    // ---------------------------------------------------------------- items

    private TabPage ItemsTab()
    {
        var page = new TabPage("표시 항목") { Tag = "items", Padding = new Padding(12) };
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.Controls.Add(new Label { Text = "표시할 항목 (체크 해제 = 숨김)", AutoSize = true, Margin = new Padding(0, 0, 0, 4) }, 0, 0);
        _tree = new TreeView { Dock = DockStyle.Fill, CheckBoxes = true, HideSelection = true, ShowLines = true };
        _tree.AfterCheck += (_, e) =>
        {
            if (_syncing || e.Action == TreeViewAction.Unknown) return;
            SetShown((string)e.Node.Tag, e.Node.Checked);
        };
        EnableRowDrag();
        left.Controls.Add(_tree, 0, 1);

        var right = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = false };
        var values = new GroupBox { Text = "표시할 값", AutoSize = true, MinimumSize = new Size(200, 0), Padding = new Padding(10, 6, 10, 6) };
        var valuesFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true, MaximumSize = new Size(210, 0) };
        foreach (var c in Column.All)
        {
            var box = KeyCheck(c.Title, c.Key);
            _columnChecks.Add(box);
            valuesFlow.Controls.Add(box);
        }
        values.Controls.Add(valuesFlow);
        right.Controls.Add(values);

        var graphs = new GroupBox { Text = "그래프", AutoSize = true, MinimumSize = new Size(200, 0), Padding = new Padding(10, 6, 10, 6), Margin = new Padding(3, 12, 3, 3) };
        // One line per section: "CPU  ☑ 사용률  ☐ 온도".
        var graphsTable = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
        foreach (var s in _main.AllSections.Where(s => s.Graphs.Count > 0))
        {
            graphsTable.Controls.Add(new Label { Text = s.Title, AutoSize = true, Margin = new Padding(0, 5, 8, 0) });
            for (var i = 0; i < 2; i++)
            {
                if (i < s.Graphs.Count)
                {
                    var box = KeyCheck(s.Graphs[i].Short, s.Graphs[i].Key, minWidth: 70);
                    _graphChecks.Add(box);
                    graphsTable.Controls.Add(box);
                }
                else
                {
                    graphsTable.Controls.Add(new Label { AutoSize = true });
                }
            }
        }
        graphs.Controls.Add(graphsTable);
        right.Controls.Add(graphs);
        right.Controls.Add(new Label
        {
            Text = "세부항목은 끌어서 순서를 바꿀 수 있음 (같은 항목 안에서).\n팬은 한 번이라도 돈 팬만 목록에 나옴.",
            AutoSize = true, MaximumSize = new Size(230, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 12, 3, 3),
        });
        var resetOrder = new Button { Text = "선택한 항목의 순서 초기화", AutoSize = true, MinimumSize = new Size(200, 0), Margin = new Padding(3, 6, 3, 3) };
        resetOrder.Click += (_, _) => ResetSelectedOrder();
        right.Controls.Add(resetOrder);
        var resetAll = new Button { Text = "전체 순서 초기화", AutoSize = true, MinimumSize = new Size(200, 0), Margin = new Padding(3, 3, 3, 3) };
        resetAll.Click += (_, _) => ResetAllOrders();
        right.Controls.Add(resetAll);

        table.Controls.Add(left, 0, 0);
        table.Controls.Add(right, 1, 0);
        page.Controls.Add(table);
        return page;
    }

    private bool Confirm(string message) =>
        MessageBox.Show(this, message, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    private void Inform(string message) =>
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);

    /// <summary>Resets the sub-item order of the section the selected node belongs to, after confirming.</summary>
    private void ResetSelectedOrder()
    {
        var node = _tree.SelectedNode;
        var key = (string)(node?.Parent ?? node)?.Tag;
        if (_main.AllSections.FirstOrDefault(s => s.Key == key) is not { } section)
        {
            Inform("초기화할 항목을 목록에서 먼저 선택하세요.");
            return;
        }
        if (!_settings.RowOrder.ContainsKey(section.Key))
        {
            Inform($"{section.Title}의 세부항목은 이미 기본 순서입니다.");
            return;
        }
        if (!Confirm($"{section.Title}의 세부항목 순서를 기본 순서로 초기화하시겠습니까?")) return;
        _main.SetRowOrder(section, null);
        BuildTree();
    }

    /// <summary>Resets every section whose sub-item order was changed, after confirming (listing them).</summary>
    private void ResetAllOrders()
    {
        var changed = _main.AllSections.Where(s => _settings.RowOrder.ContainsKey(s.Key)).ToList();
        if (changed.Count == 0)
        {
            Inform("순서를 변경한 항목이 없습니다.");
            return;
        }
        var names = string.Join(", ", changed.Select(s => s.Title));
        if (!Confirm($"변경한 전체 항목({names})의 세부항목 순서를 기본 순서로 초기화하시겠습니까?")) return;
        foreach (var section in changed) _main.SetRowOrder(section, null);
        BuildTree();
    }

    /// <summary>Sub-items (child nodes) can be dragged up or down within their own section.</summary>
    private void EnableRowDrag()
    {
        _tree.AllowDrop = true;
        DragCursor ok = null, no = null;
        _tree.ItemDrag += (_, e) =>
        {
            if (e.Item is not TreeNode { Parent: not null } node) return;
            // Show what is being dragged next to the pointer instead of the plain drag box.
            using (ok = new DragCursor($"↕ {node.Parent.Text} · {node.Text}", _tree.Font, allowed: true))
            using (no = new DragCursor($"⊘ {node.Parent.Text} · {node.Text}", _tree.Font, allowed: false))
                _tree.DoDragDrop(node, DragDropEffects.Move);
            ok = no = null;
        };
        _tree.GiveFeedback += (_, e) =>
        {
            if (ok is null) return;
            e.UseDefaultCursors = false;
            Cursor.Current = e.Effect == DragDropEffects.Move ? ok.Cursor : no.Cursor;
        };

        TreeNode Target(DragEventArgs e, out TreeNode dragged)
        {
            dragged = e.Data.GetData(typeof(TreeNode)) as TreeNode;
            var target = _tree.GetNodeAt(_tree.PointToClient(new Point(e.X, e.Y)));
            if (dragged is null || target is null) return null;
            // Same section only: a sibling, or the section node itself (= move to the top).
            return target.Parent == dragged.Parent || target == dragged.Parent ? target : null;
        }

        _tree.DragOver += (_, e) =>
        {
            var target = Target(e, out TreeNode _);
            e.Effect = target is null ? DragDropEffects.None : DragDropEffects.Move;
            if (target is not null) _tree.SelectedNode = target;
        };
        _tree.DragDrop += (_, e) =>
        {
            var target = Target(e, out var dragged);
            if (target is null) return;
            var parent = dragged.Parent;
            var to = target == parent ? 0 : target.Index;
            if (to == dragged.Index) return;

            var section = _main.AllSections.First(s => s.Key == (string)parent.Tag);
            var keys = section.Rows.Select(r => r.Key).ToList();
            var key = keys[dragged.Index];
            keys.RemoveAt(dragged.Index);
            keys.Insert(to, key);
            _main.SetRowOrder(section, keys);

            // Move the node in place so the tree keeps its scroll position.
            _syncing = true;
            dragged.Remove();
            parent.Nodes.Insert(to, dragged);
            _syncing = false;
            _tree.SelectedNode = dragged;
        };
    }

    private CheckBox KeyCheck(string text, string key, int minWidth = 98)
    {
        var box = new CheckBox { Text = text, AutoSize = true, MinimumSize = new Size(minWidth, 0), Tag = key };
        box.CheckedChanged += (_, _) =>
        {
            if (!_syncing) SetShown(key, box.Checked);
        };
        return box;
    }

    private void SetShown(string key, bool shown)
    {
        if (shown) _settings.Hidden.Remove(key); else _settings.Hidden.Add(key);
        Changed();
    }

    private void BuildTree()
    {
        _syncing = true;
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        foreach (var section in _main.AllSections)
        {
            var node = new TreeNode(section.Title) { Tag = section.Key, Checked = !_settings.Hidden.Contains(section.Key) };
            foreach (var row in section.Rows)
                node.Nodes.Add(new TreeNode(row.Label) { Tag = row.Key, Checked = !_settings.Hidden.Contains(row.Key) });
            _tree.Nodes.Add(node);
        }
        _tree.ExpandAll();
        if (_tree.Nodes.Count > 0) _tree.Nodes[0].EnsureVisible();
        _tree.EndUpdate();
        _syncing = false;
    }

    // ---------------------------------------------------------------- layout

    private TabPage LayoutTab()
    {
        var page = new TabPage("배치") { Tag = "layout", Padding = new Padding(12) };
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var top = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        top.Controls.Add(new Label { Text = "열 개수", AutoSize = true, Margin = new Padding(0, 7, 6, 0) });
        _gridColumns = new NumericUpDown { Minimum = 1, Maximum = Placement.MaxColumns, Width = 50 };
        _gridColumns.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            _settings.GridColumns = (int)_gridColumns.Value;
            Changed();
            _editor.UpdateSize();
        };
        top.Controls.Add(_gridColumns);
        top.Controls.Add(new Label { Text = "빠른 배치:", AutoSize = true, Margin = new Padding(18, 7, 6, 0) });
        foreach (var (label, name) in new[] { ("세로 1열", "column"), ("2열", "two"), ("가로 1줄", "row") })
        {
            var button = new Button { Text = label, AutoSize = true };
            button.Click += (_, _) =>
            {
                (_settings.GridColumns, _settings.Placements) = GridLayout.Preset(name, _main.SectionKeys);
                SyncFromSettings();
                Changed();
                _editor.UpdateSize();
            };
            top.Controls.Add(button);
        }
        table.Controls.Add(top, 0, 0);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(245, 246, 248) };
        _editor = new LayoutEditor(_main, Changed) { Location = new Point(0, 0) };
        scroll.Controls.Add(_editor);
        table.Controls.Add(scroll, 0, 1);

        table.Controls.Add(new Label
        {
            Text = "• 칸을 끌어서 위치 이동, 오른쪽 아래 모서리(◢)를 끌어서 차지하는 칸 수 조절. 겹치면 자리를 바꿈.\n" +
                   "• 칸을 더블클릭하면 그 항목의 실제 모습이 새 창으로 열림. 값 열(현재·최소·평균·최대)과 세부항목을 끌어서 순서 변경.\n" +
                   "• 통째로 빈 열·행은 자동으로 없어짐 (모니터 창과 같은 모양). 빗금 칸은 숨긴 항목.",
            AutoSize = true, MaximumSize = new Size(620, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 6, 0, 0),
        }, 0, 2);

        page.Controls.Add(table);
        return page;
    }

    // ---------------------------------------------------------------- tools

    private TabPage ToolsTab()
    {
        var page = new TabPage("기록·도구") { Tag = "tools", Padding = new Padding(12) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };

        Button Action(string text, Action click)
        {
            var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(240, 30), Margin = new Padding(0, 4, 0, 4) };
            b.Click += (_, _) => click();
            return b;
        }

        flow.Controls.Add(Action("초기화 (최소·평균·최대, 경과 시간)", _main.ResetStats));

        flow.Controls.Add(new Label { Text = "CSV 기록", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 14, 0, 2) });
        _logButton = Action("", () => { _main.ToggleLog(); SyncTools(); });
        flow.Controls.Add(_logButton);
        flow.Controls.Add(Action("기록 폴더 열기", MainForm.OpenLogFolder));
        flow.Controls.Add(new Label { Text = "저장 위치: 문서\\PttyPcTestMonitor", AutoSize = true, ForeColor = SystemColors.GrayText });

        flow.Controls.Add(new Label { Text = "드라이버 · 문제 확인", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 14, 0, 2) });
        _pawnStatus = new Label { AutoSize = true };
        flow.Controls.Add(_pawnStatus);
        flow.Controls.Add(Action("PawnIO 설치 (CPU·RAM 온도, 메인보드 팬)", () => { _main.InstallPawnIO(); SyncTools(); }));
        flow.Controls.Add(Action("센서 목록 저장 (값이 —로 나올 때 확인용)", _main.SaveDump));

        page.Controls.Add(flow);
        return page;
    }

    private void SyncTools()
    {
        _logButton.Text = _main.IsLogging ? "CSV 기록 중지" : "CSV 기록 시작";
        _pawnStatus.Text = _main.PawnIOInstalled ? "PawnIO: 설치됨" : "PawnIO: 설치되지 않음 — CPU·RAM 온도, 메인보드 팬이 표시되지 않음";
        _pawnStatus.ForeColor = _main.PawnIOInstalled ? Color.SeaGreen : Color.DarkOrange;
    }

    // ---------------------------------------------------------------- sync

    /// <summary>Reloads every control from the settings (e.g. after a preset or a change made in the monitor window).</summary>
    public void SyncFromSettings()
    {
        _syncing = true;
        _viewFull.Checked = !_settings.Compact;
        _viewCompact.Checked = _settings.Compact;
        _topMost.Checked = _settings.TopMost;
        _slider.Checked = _settings.ShowOpacitySlider;
        _doubleClick.Checked = _settings.DoubleClickReset;
        _resetButton.Checked = _settings.ShowResetButton;
        _columnTitles.Checked = _settings.ShowColumnTitles;
        _opacity.Value = (int)Math.Round(_settings.Opacity * 100);
        _opacityValue.Text = $"{_opacity.Value}%";
        _interval.SelectedIndex = _settings.IntervalMs <= 500 ? 0 : _settings.IntervalMs <= 1000 ? 1 : 2;
        foreach (var box in _columnChecks.Concat(_graphChecks)) box.Checked = !_settings.Hidden.Contains((string)box.Tag);
        _gridColumns.Value = _settings.GridColumns;
        _syncing = false;
        BuildTree();
        SyncTools();
        _editor?.Invalidate();
    }

    public void SyncOpacity()
    {
        _syncing = true;
        _opacity.Value = (int)Math.Round(_settings.Opacity * 100);
        _syncing = false;
    }
}

/// <summary>
/// RTSS-style layout editor: each section is a block on a grid. Drag a block to move it; drag its bottom-right
/// corner to change how many columns/rows it spans.
/// </summary>
public sealed class LayoutEditor : Control
{
    private readonly MainForm _main;
    private readonly Action _changed;
    private readonly float _dpi;

    private string _active;
    private bool _resizing;
    private Point _grab;          // grabbed cell offset inside the block
    private Placement _preview;
    private Point _pointer, _grabPixel; // while moving, the block follows the pointer at the grabbed spot

    // Blocks glide to their new places: _shown is where each is drawn now, _targets where it is heading.
    private readonly Dictionary<string, RectangleF> _shown = new(), _targets = new();
    private readonly Timer _glide = new() { Interval = 15 };

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        var key = HitTest(e.Location);
        var section = _main.AllSections.FirstOrDefault(s => s.Key == key);
        if (section is null) return;
        _active = null;
        _preview = null;
        Capture = false;
        new PanelEditorForm(_main, section, _changed).Show(FindForm());
    }

    public LayoutEditor(MainForm main, Action changed)
    {
        _main = main;
        _changed = changed;
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(245, 246, 248);
        using (var g = CreateGraphics()) _dpi = g.DpiX / 96f;
        _glide.Tick += (_, _) => Glide();
        Disposed += (_, _) => _glide.Dispose();
        UpdateSize();
    }

    /// <summary>
    /// The layout as it would be if the dragged block were dropped now: the others make room (and empty
    /// columns/rows close) exactly as they will on release.
    /// </summary>
    private Dictionary<string, Placement> Arranged()
    {
        if (_active is null || _preview is null) return Settings.Placements;
        var copy = Settings.Placements.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
        GridLayout.Move(copy, _active, _preview.Clone(), Settings.GridColumns);
        GridLayout.Pack(copy, copy.Keys.ToList());
        return copy;
    }

    /// <summary>Moves every block a step toward its target; stops when all have arrived.</summary>
    private void Glide()
    {
        var moving = false;
        foreach (var key in _targets.Keys.ToList())
        {
            var from = _shown.TryGetValue(key, out var r) ? r : _targets[key];
            var to = _targets[key];
            var next = new RectangleF(
                from.X + (to.X - from.X) * 0.35f, from.Y + (to.Y - from.Y) * 0.35f,
                from.Width + (to.Width - from.Width) * 0.35f, from.Height + (to.Height - from.Height) * 0.35f);
            var close = Math.Abs(next.X - to.X) < 0.5f && Math.Abs(next.Y - to.Y) < 0.5f &&
                        Math.Abs(next.Width - to.Width) < 0.5f && Math.Abs(next.Height - to.Height) < 0.5f;
            _shown[key] = close ? to : next;
            moving |= !close;
        }
        if (!moving) _glide.Stop();
        Invalidate();
    }

    private Settings Settings => _main.Settings;
    private int CellW => (int)(118 * _dpi);
    private int CellH => (int)(54 * _dpi);
    private int Gap => (int)(6 * _dpi);
    private int Margin0 => (int)(8 * _dpi);
    private int HandleSize => (int)(14 * _dpi);

    private int RowsShown =>
        Math.Min(Placement.MaxRows, Math.Max(3, Settings.Placements.Values.Max(p => p.Row + p.RowSpan) + 1));

    public void UpdateSize()
    {
        Size = new Size(Margin0 * 2 + Settings.GridColumns * (CellW + Gap), Margin0 * 2 + RowsShown * (CellH + Gap));
        Invalidate();
    }

    private Rectangle CellRect(Placement p) => new(
        Margin0 + p.Col * (CellW + Gap),
        Margin0 + p.Row * (CellH + Gap),
        p.ColSpan * CellW + (p.ColSpan - 1) * Gap,
        p.RowSpan * CellH + (p.RowSpan - 1) * Gap);

    private Point CellAt(Point pt) => new(
        Math.Max(0, (pt.X - Margin0) / (CellW + Gap)),
        Math.Max(0, (pt.Y - Margin0) / (CellH + Gap)));

    private string HitTest(Point pt) =>
        Settings.Placements.FirstOrDefault(kv => CellRect(kv.Value).Contains(pt)).Key;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var cols = Settings.GridColumns;

        using (var cellPen = new Pen(Color.FromArgb(200, 204, 212)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
        {
            for (var r = 0; r < RowsShown; r++)
                for (var c = 0; c < cols; c++)
                    g.DrawRectangle(cellPen, CellRect(new Placement(c, r)));
        }

        // Every block heads for where it would end up; the dragged one is drawn last, as a dashed ghost at its drop spot.
        var arranged = Arranged();
        var glide = false;
        foreach (var section in _main.AllSections)
        {
            var target = (RectangleF)CellRect(arranged[section.Key]);
            _targets[section.Key] = target;
            if (!_shown.ContainsKey(section.Key)) _shown[section.Key] = target;
            glide |= _shown[section.Key] != target;
        }
        if (glide && !_glide.Enabled) _glide.Start();

        foreach (var section in _main.AllSections.OrderBy(s => s.Key == _active))
        {
            var p = arranged[section.Key];
            var rect = Rectangle.Round(_shown[section.Key]);
            var hidden = Settings.Hidden.Contains(section.Key);
            var dragged = section.Key == _active;

            using (var fill = hidden
                       ? (Brush)new System.Drawing.Drawing2D.HatchBrush(System.Drawing.Drawing2D.HatchStyle.BackwardDiagonal, Color.FromArgb(70, section.Color), Color.FromArgb(235, 236, 240))
                       : new SolidBrush(Color.FromArgb(dragged ? 60 : 90, section.Color)))
                g.FillRectangle(fill, rect);
            using (var border = new Pen(dragged ? Sections.Accent : section.Color, dragged ? 2.5f * _dpi : 1.5f * _dpi))
            {
                if (dragged) border.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                g.DrawRectangle(border, rect);
            }

            var title = hidden ? $"{section.Title} (숨김)" : section.Title;
            var span = $"{p.ColSpan}×{p.RowSpan}";
            using (var bold = new Font(Font, FontStyle.Bold))
                TextRenderer.DrawText(g, title, bold, new Rectangle(rect.X + 6, rect.Y + 4, rect.Width - 12, rect.Height / 2), Color.FromArgb(30, 32, 36),
                    TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, span, Font, new Rectangle(rect.X + 6, rect.Bottom - (int)(22 * _dpi), rect.Width - 12, (int)(18 * _dpi)), Color.FromArgb(80, 84, 92),
                TextFormatFlags.Left);

            // Resize handle.
            using var handle = new SolidBrush(section.Color);
            g.FillPolygon(handle, new[]
            {
                new Point(rect.Right, rect.Bottom - HandleSize),
                new Point(rect.Right, rect.Bottom),
                new Point(rect.Right - HandleSize, rect.Bottom),
            });
        }

        // The block being moved floats with the pointer; its dashed ghost above shows where it will land.
        if (_active is not null && !_resizing)
        {
            var section = _main.AllSections.First(s => s.Key == _active);
            var size = CellRect(arranged[_active]).Size;
            var floating = new Rectangle(new Point(_pointer.X - _grabPixel.X, _pointer.Y - _grabPixel.Y), size);
            using (var shadow = new SolidBrush(Color.FromArgb(40, 0, 0, 0)))
                g.FillRectangle(shadow, new Rectangle(floating.X + 4, floating.Y + 4, floating.Width, floating.Height));
            using (var fill = new SolidBrush(Color.FromArgb(220, Blend(section.Color, Color.White, 0.55f))))
                g.FillRectangle(fill, floating);
            using (var border = new Pen(section.Color, 2.5f * _dpi))
                g.DrawRectangle(border, floating);
            using var bold = new Font(Font, FontStyle.Bold);
            TextRenderer.DrawText(g, section.Title, bold, new Rectangle(floating.X + 6, floating.Y + 4, floating.Width - 12, floating.Height / 2),
                Color.FromArgb(30, 32, 36), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }

    private static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _active = HitTest(e.Location);
        if (_active is null) return;

        var p = Settings.Placements[_active];
        var rect = CellRect(p);
        _resizing = e.X > rect.Right - HandleSize && e.Y > rect.Bottom - HandleSize;
        var cell = CellAt(e.Location);
        _grab = new Point(cell.X - p.Col, cell.Y - p.Row);
        _grabPixel = new Point(e.X - rect.X, e.Y - rect.Y);
        _pointer = e.Location;
        _preview = p.Clone();
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var cols = Settings.GridColumns;

        if (_active is null)
        {
            var key = HitTest(e.Location);
            Cursor = key is null ? Cursors.Default
                : IsOnHandle(key, e.Location) ? Cursors.SizeNWSE : Cursors.SizeAll;
            return;
        }

        _pointer = e.Location;
        var p = Settings.Placements[_active];
        // Snap by the block's center (not the pointer) so the drop spot follows what the user sees.
        var cell = _resizing ? CellAt(e.Location)
            : CellAt(new Point(e.X - _grabPixel.X + CellW / 2, e.Y - _grabPixel.Y + CellH / 2));
        if (!_resizing) _grab = Point.Empty;
        if (_resizing)
        {
            _preview = new Placement(p.Col, p.Row,
                Math.Max(1, Math.Min(cols - p.Col, cell.X - p.Col + 1)),
                Math.Max(1, Math.Min(Placement.MaxRows - p.Row, cell.Y - p.Row + 1)));
        }
        else
        {
            _preview = new Placement(
                Math.Max(0, Math.Min(cols - p.ColSpan, cell.X - _grab.X)),
                Math.Max(0, Math.Min(Placement.MaxRows - p.RowSpan, cell.Y - _grab.Y)),
                p.ColSpan, p.RowSpan);
        }
        Invalidate();
    }

    private bool IsOnHandle(string key, Point pt)
    {
        var rect = CellRect(Settings.Placements[key]);
        return pt.X > rect.Right - HandleSize && pt.Y > rect.Bottom - HandleSize;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_active is null) return;
        Capture = false;
        var key = _active;
        var preview = _preview;
        _active = null;
        _preview = null;

        var current = Settings.Placements[key];
        if (preview is not null && (preview.Col != current.Col || preview.Row != current.Row ||
                                    preview.ColSpan != current.ColSpan || preview.RowSpan != current.RowSpan))
        {
            GridLayout.Move(Settings.Placements, key, preview, Settings.GridColumns);
            _changed();
        }
        UpdateSize();
    }
}

/// <summary>A mouse cursor made of the normal arrow plus a label box, used while dragging a sub-item.</summary>
public sealed class DragCursor : IDisposable
{
    private IntPtr _handle;

    public Cursor Cursor { get; }

    public DragCursor(string text, Font font, bool allowed)
    {
        var textSize = TextRenderer.MeasureText(text, font);
        var box = new Rectangle(16, 20, textSize.Width + 12, textSize.Height + 6);
        using var bmp = new Bitmap(box.Right + 2, box.Bottom + 2, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            Cursors.Default.Draw(g, new Rectangle(0, 0, 32, 32));
            using var fill = new SolidBrush(allowed ? Color.FromArgb(245, 250, 255) : Color.FromArgb(240, 240, 240));
            using var border = new Pen(allowed ? Color.FromArgb(0, 120, 215) : Color.FromArgb(150, 150, 150));
            g.FillRectangle(fill, box);
            g.DrawRectangle(border, box);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var brush = new SolidBrush(allowed ? Color.FromArgb(20, 20, 20) : Color.FromArgb(130, 130, 130));
            g.DrawString(text, font, brush, box.X + 5, box.Y + 3);
        }

        // Turn the bitmap into a cursor whose hot spot is the arrow tip (top-left).
        var icon = bmp.GetHicon();
        GetIconInfo(icon, out var info);
        DestroyIcon(icon);
        info.fIcon = false;
        info.xHotspot = 0;
        info.yHotspot = 0;
        _handle = CreateIconIndirect(ref info);
        DeleteObject(info.hbmMask);
        DeleteObject(info.hbmColor);
        Cursor = new Cursor(_handle);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        DestroyIcon(_handle);
        _handle = IntPtr.Zero;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO info);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
