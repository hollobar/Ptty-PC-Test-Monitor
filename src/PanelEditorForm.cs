using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace PttyPcTestMonitor;

/// <summary>
/// Edits one section (opened by double-clicking its block in the layout editor). Left: the panel exactly as the
/// monitor draws it (live), where value columns, sub-items and graphs are reordered by dragging and the divider
/// between two graphs sets their share. Right: what to show and how this section draws its graphs.
/// </summary>
public sealed class PanelEditorForm : Form
{
    private enum Drag { None, Column, Row, Graph, Divider }

    private static readonly Color Bg = Color.FromArgb(20, 22, 26);
    private static readonly Color Marker = Sections.Accent; // the app's accent blue: visible on dark, not alarming like red
    private static readonly Color Hover = Color.FromArgb(90, 96, 165, 250);

    private readonly MainForm _main;
    private readonly Section _section;
    private readonly Action _changed;
    private readonly Timer _timer = new() { Interval = 1000 };
    private readonly Panel _canvas;
    private readonly int _margin;
    /// <summary>Width the side panel needs for its contents (measured, so it fits at any display scaling).</summary>
    private int _sideWidth;

    private MainForm.PanelGeometry _geometry;
    private Drag _drag;
    private int _dragIndex, _target;
    private Point _mouse;
    private Rectangle _hover;

    // Side panel.
    private bool _syncing;
    private readonly FlowLayoutPanel _rowsFlow = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly List<CheckBox> _valueChecks = new(), _graphChecks = new();
    private RadioButton _below, _behind;
    private ComboBox _arrange;
    private TrackBar _strength, _split, _height;
    private Label _strengthValue, _splitValue, _heightValue;
    private List<string> _rowKeys = new();

    public PanelEditorForm(MainForm main, Section section, Action changed)
    {
        _main = main;
        _section = section;
        _changed = changed;

        Text = $"{MainForm.AppName} — {section.Title} 편집";
        Font = new Font("Malgun Gothic", 9f);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Bg;
        _margin = (int)(8 * main.Dpi);

        _canvas = new DoubleBufferedPanel { Dock = DockStyle.Fill, BackColor = Bg, Margin = new Padding(0) };
        _canvas.Paint += (_, e) => PaintCanvas(e.Graphics);
        _canvas.MouseDown += OnCanvasDown;
        _canvas.MouseMove += OnCanvasMove;
        _canvas.MouseUp += OnCanvasUp;
        _canvas.MouseLeave += (_, _) => { _hover = Rectangle.Empty; _canvas.Invalidate(); };

        var hint = new Label
        {
            Text = "값 열(현재·최소·평균·최대)은 좌우로, 세부항목은 이름을 잡고 위아래로 끌어서 순서를 바꿈. " +
                   "그래프는 잡고 끌어서 순서를, 두 그래프 사이 경계를 끌어서 비율을 바꿈 ('값 아래에 따로'일 때). " +
                   "창 크기를 바꾸면 패널도 커짐.",
            AutoSize = true, ForeColor = Color.FromArgb(150, 156, 166), Margin = new Padding(_margin, 0, _margin, _margin),
        };

        var side = SidePanel();
        SyncSide();
        _sideWidth = side.GetPreferredSize(Size.Empty).Width;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, _sideWidth));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_canvas, 0, 0);
        root.Controls.Add(hint, 0, 1);
        root.Controls.Add(side, 1, 0);
        root.SetRowSpan(side, 2);
        Controls.Add(root);

        // Wrap the hint to the canvas width instead of letting it widen the window.
        Resize += (_, _) => hint.MaximumSize = new Size(Math.Max(100, ClientSize.Width - _sideWidth - _margin * 2), 0);

        FitToPanel(StartZoom, side);
        // Never shorter than the side panel, so it needs no scrollbar.
        MinimumSize = new Size(Width / 2, Height - ClientSize.Height + side.GetPreferredSize(Size.Empty).Height);
        _canvas.Resize += (_, _) => _canvas.Invalidate();
        _timer.Tick += (_, _) =>
        {
            _canvas.Invalidate(); // live values
            if (!_section.Rows.Select(r => r.Key).SequenceEqual(_rowKeys)) SyncSide(); // fans appear while running
        };
        _timer.Start();
        FormClosed += (_, _) => _timer.Dispose();
    }

    /// <summary>WS_EX_COMPOSITED: paint the window and all its child controls in one buffered pass (no flicker).</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x02000000;
            return cp;
        }
    }

    // ---------------------------------------------------------------- side panel

    private Control SidePanel()
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            BackColor = SystemColors.Control, Padding = new Padding(10, 8, 10, 8), Margin = new Padding(0),
        };
        var groups = new List<GroupBox>();

        GroupBox Group(string text)
        {
            var box = new GroupBox { Text = text, AutoSize = true, Padding = new Padding(8, 4, 8, 6), Margin = new Padding(0, 0, 0, 8) };
            groups.Add(box);
            return box;
        }

        // What to show.
        var rows = Group($"{_section.Title} 세부항목");
        rows.Controls.Add(_rowsFlow);
        _rowsFlow.Dock = DockStyle.Fill;
        flow.Controls.Add(rows);

        var values = Group("표시할 값 (모든 항목 공통)");
        // Two per line.
        var valuesFlow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        foreach (var c in Column.All)
        {
            var box = KeyCheck(c.Title, c.Key, 80);
            _valueChecks.Add(box);
            valuesFlow.Controls.Add(box);
        }
        values.Controls.Add(valuesFlow);
        flow.Controls.Add(values);

        if (_section.Graphs.Count > 0)
        {
            var graphs = Group("그래프");
            var g = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
            g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int row = 0;
            void Line(string label, Control control, Control extra = null)
            {
                g.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 7, 6, 0) }, 0, row);
                g.Controls.Add(control, 1, row);
                if (extra is not null) g.Controls.Add(extra, 2, row);
                row++;
            }

            var shown = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            foreach (var graph in _section.Graphs)
            {
                var box = KeyCheck(graph.Short, graph.Key, 70);
                _graphChecks.Add(box);
                shown.Controls.Add(box);
            }
            Line("표시", shown);
            g.SetColumnSpan(shown, 2);

            var where = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            _below = new RadioButton { Text = "값 아래에 따로", AutoSize = true };
            _behind = new RadioButton { Text = "값 뒤에 옅게", AutoSize = true };
            _below.CheckedChanged += (_, _) => { if (_below.Checked) EditMode(m => m.Behind = false); };
            _behind.CheckedChanged += (_, _) => { if (_behind.Checked) EditMode(m => m.Behind = true); };
            where.Controls.AddRange(new Control[] { _below, _behind });
            Line("위치", where);
            g.SetColumnSpan(where, 2);

            _arrange = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
            _arrange.Items.AddRange(new object[] { "위아래로", "나란히", "겹쳐서" });
            _arrange.SelectedIndexChanged += (_, _) => EditMode(m =>
            {
                if (m.Behind) m.ArrangeBehind = (GraphArrange)_arrange.SelectedIndex;
                else m.ArrangeBelow = (GraphArrange)_arrange.SelectedIndex;
            });
            Line("2개 배치", _arrange);

            (_strength, _strengthValue) = Slider(5, 100, v => EditMode(m => m.Strength = v / 100.0));
            Line("진하기", _strength, _strengthValue);
            (_split, _splitValue) = Slider(15, 85, v =>
            {
                _main.SetGraphSplit(_section, v / 100.0);
                AfterChange();
            });
            Line("비율", _split, _splitValue);
            (_height, _heightValue) = Slider(Settings.MinGraphHeight, 200, v =>
            {
                _main.SetGraphHeight(_section, v);
                AfterChange();
            });
            Line("높이", _height, _heightValue);

            var heightReset = new LinkLabel { Text = "높이를 공통 높이로", AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
            heightReset.LinkClicked += (_, _) => { _main.SetGraphHeight(_section, null); AfterChange(); };
            g.Controls.Add(heightReset, 1, row);
            g.SetColumnSpan(heightReset, 2);

            graphs.Controls.Add(g);
            flow.Controls.Add(graphs);
        }

        // Resets, then close.
        var resets = Group("순서 초기화");
        var resetFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        void Reset(string text, Action click)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += (_, _) => click();
            resetFlow.Controls.Add(b);
        }
        Reset("값", ResetColumns);
        Reset("세부항목", ResetRows);
        if (_section.Graphs.Count > 1) Reset("그래프", ResetGraphs);
        resets.Controls.Add(resetFlow);
        flow.Controls.Add(resets);

        var close = new Button { Text = "닫기", AutoSize = true, MinimumSize = new Size(90, 0) };
        close.Click += (_, _) => Close();
        CancelButton = close;
        flow.Controls.Add(close);

        // Same width for every group: the widest one's.
        var width = groups.Max(g => g.GetPreferredSize(Size.Empty).Width);
        foreach (var g in groups) g.MinimumSize = new Size(width, 0);
        return flow;
    }

    private (TrackBar, Label) Slider(int min, int max, Action<int> changed)
    {
        var bar = new TrackBar { Minimum = min, Maximum = max, TickStyle = TickStyle.None, AutoSize = false, Height = (int)(26 * _main.Dpi), Width = (int)(120 * _main.Dpi) };
        // Fixed width (the longest text it shows) so changing values don't re-lay-out the panel.
        var label = new Label { AutoSize = true, Margin = new Padding(2, 6, 0, 0) };
        label.MinimumSize = new Size(TextRenderer.MeasureText("사용률 85 : 15", Font).Width + 4, 0);
        bar.ValueChanged += (_, _) =>
        {
            if (!_syncing) changed(bar.Value);
        };
        return (bar, label);
    }

    private CheckBox KeyCheck(string text, string key, int minWidth)
    {
        var box = new CheckBox { Text = text, AutoSize = true, MinimumSize = new Size(minWidth, 0), Tag = key };
        box.CheckedChanged += (_, _) =>
        {
            if (_syncing) return;
            if (box.Checked) _main.Settings.Hidden.Remove(key); else _main.Settings.Hidden.Add(key);
            _main.SettingsChanged();
            AfterChange(optionsToo: true);
        };
        return box;
    }

    private void EditMode(Action<GraphMode> edit)
    {
        if (_syncing) return;
        edit(_main.ModeOf(_section));
        _main.SettingsChanged();
        AfterChange();
    }

    /// <summary>
    /// After an edit: refresh this window. Only show/hide changes concern the options window, so it is resynced just
    /// then (a slider drag would otherwise rebuild it on every step).
    /// </summary>
    private void AfterChange(bool optionsToo = false)
    {
        SyncSide();
        _changed();
        if (optionsToo) (Owner as OptionsForm)?.SyncFromSettings();
        _canvas.Invalidate();
    }

    /// <summary>
    /// Loads the side panel from the settings. Controls are only touched when their value differs, and the
    /// sub-item checkboxes are only rebuilt when the sub-items themselves change, so dragging a slider doesn't
    /// make the panel redraw itself.
    /// </summary>
    private void SyncSide()
    {
        _syncing = true;
        var hidden = _main.Settings.Hidden;

        var keys = _section.Rows.Select(r => r.Key).ToList();
        if (!keys.SequenceEqual(_rowKeys) || _rowsFlow.Controls.Count == 0)
        {
            _rowKeys = keys;
            _rowsFlow.SuspendLayout();
            _rowsFlow.Controls.Clear();
            foreach (var row in _section.Rows) _rowsFlow.Controls.Add(KeyCheck(row.Label, row.Key, 0));
            if (_section.Rows.Count == 0)
                _rowsFlow.Controls.Add(new Label { Text = _section.Placeholder, AutoSize = true, ForeColor = SystemColors.GrayText });
            _rowsFlow.ResumeLayout();
        }
        foreach (var box in _rowsFlow.Controls.OfType<CheckBox>().Concat(_valueChecks).Concat(_graphChecks))
            Set(box, !hidden.Contains((string)box.Tag));

        if (_section.Graphs.Count > 0)
        {
            var mode = _main.ModeOf(_section);
            if (_below.Checked != !mode.Behind) _below.Checked = !mode.Behind;
            if (_behind.Checked != mode.Behind) _behind.Checked = mode.Behind;
            var visible = _main.OrderedGraphs(_section).Where(g => !hidden.Contains(g.Key)).ToList();
            var two = visible.Count == 2;

            if (_arrange.SelectedIndex != (int)mode.Arrange) _arrange.SelectedIndex = (int)mode.Arrange;
            Enable(_arrange, two);

            Set(_strength, (int)Math.Round(mode.Strength * 100));
            Enable(_strength, mode.Behind);
            Set(_strengthValue, mode.Behind ? $"{_strength.Value}%" : "(뒤일 때)");

            Set(_split, (int)Math.Round(_main.SplitOf(_section) * 100));
            Enable(_split, two && mode.Arrange != GraphArrange.Overlap);
            Set(_splitValue, _split.Enabled ? $"{visible[0].Short} {_split.Value} : {100 - _split.Value}" : "(2개일 때)");

            Set(_height, Math.Max(_height.Minimum, Math.Min(_height.Maximum, _main.GraphHeightOf(_section))));
            Enable(_height, !mode.Behind);
            Set(_heightValue, mode.Behind ? "(아래일 때)"
                : _main.Settings.GraphHeights.ContainsKey(_section.Key) ? $"{_height.Value}" : $"{_height.Value} (공통)");
        }
        _syncing = false;
    }

    // Change a control only when needed: each change makes WinForms re-lay-out and repaint it.
    private static void Set(CheckBox box, bool value) { if (box.Checked != value) box.Checked = value; }
    private static void Set(TrackBar bar, int value) { if (bar.Value != value) bar.Value = value; }
    private static void Set(Label label, string text) { if (label.Text != text) label.Text = text; }
    private static void Enable(Control control, bool value) { if (control.Enabled != value) control.Enabled = value; }

    // ---------------------------------------------------------------- sizing

    /// <summary>Opens the panel at about twice the monitor's size, limited to most of the screen.</summary>
    private float StartZoom
    {
        get
        {
            var logical = _main.PanelLogicalSize(_section);
            var screen = Screen.FromControl(_main).WorkingArea;
            var fit = Math.Min((screen.Width * 0.85f - _sideWidth) / (logical.Width * _main.Dpi), screen.Height * 0.7f / (logical.Height * _main.Dpi));
            return Math.Min(Math.Max((float)_main.Settings.Zoom * 2f, 1.6f), fit);
        }
    }

    private void FitToPanel(float zoom, Control side)
    {
        var logical = _main.PanelLogicalSize(_section);
        var panel = new Size((int)(logical.Width * _main.Dpi * zoom), (int)(logical.Height * _main.Dpi * zoom));
        var sideHeight = side.GetPreferredSize(Size.Empty).Height;
        var height = Math.Max(panel.Height + _margin * 2 + (int)(60 * _main.Dpi), sideHeight);
        ClientSize = new Size(panel.Width + _margin * 2 + _sideWidth, height);
    }

    /// <summary>The panel scaled to fill the canvas while keeping its proportions, centered.</summary>
    private (Rectangle Rect, float Zoom) PanelLayout
    {
        get
        {
            var logical = _main.PanelLogicalSize(_section);
            var area = Rectangle.Inflate(_canvas.ClientRectangle, -_margin, -_margin);
            var zoom = Math.Max(0.5f, Math.Min(area.Width / (logical.Width * _main.Dpi), area.Height / (logical.Height * _main.Dpi)));
            var size = new Size((int)(logical.Width * _main.Dpi * zoom), (int)(logical.Height * _main.Dpi * zoom));
            var location = new Point(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2);
            return (new Rectangle(location, size), zoom);
        }
    }

    // ---------------------------------------------------------------- painting

    private void PaintCanvas(Graphics g)
    {
        var (panel, zoom) = PanelLayout;
        _geometry = _main.PaintPanelTo(g, _section, panel, zoom);
        var geo = _geometry;

        if (_drag == Drag.None)
        {
            if (!_hover.IsEmpty)
            {
                using var pen = new Pen(Hover, 1.5f) { DashStyle = DashStyle.Dash };
                g.DrawRectangle(pen, _hover);
            }
            return;
        }

        // Where it is now: gray dashed box. Where it will go: blue dashed box over the slot it will take.
        using var from = new Pen(Color.FromArgb(150, 156, 166), 2f) { DashStyle = DashStyle.Dash };
        using var to = new Pen(Marker, 2.5f) { DashStyle = DashStyle.Dash };
        var slot = TargetSlot;
        if (_drag == Drag.Column)
        {
            var c = geo.Columns[_dragIndex];
            g.DrawRectangle(from, Rectangle.FromLTRB(c.Left, geo.Top, c.Right, geo.Bottom));
            if (slot != _dragIndex)
            {
                var t = geo.Columns[slot];
                g.DrawRectangle(to, Rectangle.FromLTRB(t.Left, geo.Top, t.Right, geo.Bottom));
            }
            DrawGhost(g, Column.All.First(k => k.Key == c.Key).Title);
        }
        else if (_drag == Drag.Graph)
        {
            var gr = geo.Graphs[_dragIndex];
            g.DrawRectangle(from, Rectangle.Inflate(gr.Rect, 2, 2));
            if (slot != _dragIndex) g.DrawRectangle(to, Rectangle.Inflate(geo.Graphs[slot].Rect, 2, 2));
            DrawGhost(g, _section.Graphs.First(k => k.Key == gr.Key).Label);
        }
        else if (_drag == Drag.Divider)
        {
            using var line = new Pen(Marker, 3f);
            var d = geo.Divider;
            if (geo.Arrange == GraphArrange.Stack) g.DrawLine(line, d.Left, (d.Top + d.Bottom) / 2, d.Right, (d.Top + d.Bottom) / 2);
            else g.DrawLine(line, (d.Left + d.Right) / 2, d.Top, (d.Left + d.Right) / 2, d.Bottom);
            DrawGhost(g, _splitValue.Text);
        }
        else
        {
            var r = geo.Rows[_dragIndex];
            g.DrawRectangle(from, Rectangle.FromLTRB(geo.LabelLeft - 2, r.Top, RowsRight(geo), r.Bottom));
            if (slot != _dragIndex)
            {
                var t = geo.Rows[slot];
                g.DrawRectangle(to, Rectangle.FromLTRB(geo.LabelLeft - 2, t.Top, RowsRight(geo), t.Bottom));
            }
            DrawGhost(g, _section.Rows.First(k => k.Key == r.Key).Label);
        }
    }

    /// <summary>The index the dragged column/row/graph will occupy once dropped.</summary>
    private int TargetSlot => _target > _dragIndex ? _target - 1 : _target;

    private static int RowsRight(MainForm.PanelGeometry geo) =>
        geo.Columns.Count > 0 ? geo.Columns[geo.Columns.Count - 1].Right : geo.LabelRight;

    /// <summary>Name of what is being dragged, next to the pointer.</summary>
    private void DrawGhost(Graphics g, string text)
    {
        var size = TextRenderer.MeasureText(text, Font);
        var rect = new Rectangle(_mouse.X + 14, _mouse.Y + 16, size.Width + 10, size.Height + 4);
        using (var fill = new SolidBrush(Color.FromArgb(235, 245, 250, 255))) g.FillRectangle(fill, rect);
        using (var border = new Pen(Color.FromArgb(0, 120, 215))) g.DrawRectangle(border, rect);
        TextRenderer.DrawText(g, text, Font, rect, Color.FromArgb(20, 20, 20),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    // ---------------------------------------------------------------- dragging

    private int ColumnAt(Point p)
    {
        if (_geometry is null || p.Y < _geometry.Top || p.Y > _geometry.Bottom) return -1;
        return _geometry.Columns.FindIndex(c => p.X >= c.Left && p.X <= c.Right);
    }

    private int RowAt(Point p)
    {
        if (_geometry is null || p.X < _geometry.LabelLeft || p.X > _geometry.LabelRight) return -1;
        return _geometry.Rows.FindIndex(r => p.Y >= r.Top && p.Y < r.Bottom);
    }

    /// <summary>Graphs can be dragged when they sit below the values in separate boxes.</summary>
    private int GraphAt(Point p)
    {
        if (_geometry is null || _geometry.Behind || _geometry.Arrange == GraphArrange.Overlap) return -1;
        return _geometry.Graphs.FindIndex(g => g.Rect.Contains(p));
    }

    private bool OnDivider(Point p) =>
        _geometry is { Behind: false } && !_geometry.Divider.IsEmpty && Rectangle.Inflate(_geometry.Divider, 4, 4).Contains(p);

    private void OnCanvasDown(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _geometry is null) return;
        _mouse = e.Location;
        if (OnDivider(e.Location))
        {
            _drag = Drag.Divider;
        }
        else if (ColumnAt(e.Location) is var c and >= 0 && _geometry.Columns.Count > 1)
        {
            _drag = Drag.Column;
            _dragIndex = _target = c;
        }
        else if (RowAt(e.Location) is var r and >= 0 && _geometry.Rows.Count > 1)
        {
            _drag = Drag.Row;
            _dragIndex = _target = r;
        }
        else if (GraphAt(e.Location) is var gi and >= 0 && _geometry.Graphs.Count > 1)
        {
            _drag = Drag.Graph;
            _dragIndex = _target = gi;
        }
        _canvas.Invalidate();
    }

    private void OnCanvasMove(object sender, MouseEventArgs e)
    {
        _mouse = e.Location;
        if (_drag == Drag.Column)
            _target = _geometry.Columns.Count(c => (c.Left + c.Right) / 2 < e.X);
        else if (_drag == Drag.Row)
            _target = _geometry.Rows.Count(r => (r.Top + r.Bottom) / 2 < e.Y);
        else if (_drag == Drag.Graph)
            _target = _geometry.Arrange == GraphArrange.Side
                ? _geometry.Graphs.Count(g => (g.Rect.Left + g.Rect.Right) / 2 < e.X)
                : _geometry.Graphs.Count(g => (g.Rect.Top + g.Rect.Bottom) / 2 < e.Y);
        else if (_drag == Drag.Divider)
        {
            // Share of the area left of / above the pointer goes to the first graph.
            var a = _geometry.GraphArea;
            var split = _geometry.Arrange == GraphArrange.Stack
                ? (e.Y - a.Top) / (double)Math.Max(1, a.Height)
                : (e.X - a.Left) / (double)Math.Max(1, a.Width);
            _main.SetGraphSplit(_section, split);
            SyncSide();
        }
        else
            UpdateHover(e.Location);
        _canvas.Invalidate();
    }

    private void UpdateHover(Point p)
    {
        _hover = Rectangle.Empty;
        _canvas.Cursor = Cursors.Default;
        if (_geometry is null) return;
        if (OnDivider(p))
        {
            _hover = Rectangle.Inflate(_geometry.Divider, 1, 1);
            _canvas.Cursor = _geometry.Arrange == GraphArrange.Stack ? Cursors.HSplit : Cursors.VSplit;
        }
        else if (ColumnAt(p) is var c and >= 0)
        {
            var col = _geometry.Columns[c];
            _hover = Rectangle.FromLTRB(col.Left, _geometry.Top, col.Right, _geometry.Bottom);
            _canvas.Cursor = Cursors.SizeWE;
        }
        else if (RowAt(p) is var r and >= 0)
        {
            var row = _geometry.Rows[r];
            _hover = Rectangle.FromLTRB(_geometry.LabelLeft - 2, row.Top, RowsRight(_geometry), row.Bottom); // whole row, like columns
            _canvas.Cursor = Cursors.SizeNS;
        }
        else if (GraphAt(p) is var gi and >= 0 && _geometry.Graphs.Count > 1)
        {
            _hover = Rectangle.Inflate(_geometry.Graphs[gi].Rect, 2, 2);
            _canvas.Cursor = _geometry.Arrange == GraphArrange.Side ? Cursors.SizeWE : Cursors.SizeNS;
        }
    }

    private void OnCanvasUp(object sender, MouseEventArgs e)
    {
        var drag = _drag;
        _drag = Drag.None;
        if (drag == Drag.None) return;
        if (drag == Drag.Divider)
        {
            AfterChange();
            UpdateHover(e.Location);
            return;
        }

        // Dropping right after itself means "same place".
        var to = TargetSlot;
        if (to != _dragIndex)
        {
            if (drag == Drag.Column)
            {
                var visible = _geometry.Columns.Select(c => c.Key).ToList();
                var all = _main.OrderedColumns(_section).Select(c => c.Key).ToList();
                _main.SetColumnOrder(_section, Reorder(all, visible, _dragIndex, to));
            }
            else if (drag == Drag.Graph)
            {
                var visible = _geometry.Graphs.Select(r => r.Key).ToList();
                var all = _main.OrderedGraphs(_section).Select(r => r.Key).ToList();
                _main.SetGraphOrder(_section, Reorder(all, visible, _dragIndex, to));
            }
            else
            {
                var visible = _geometry.Rows.Select(r => r.Key).ToList();
                var all = _section.Rows.Select(r => r.Key).ToList();
                _main.SetRowOrder(_section, Reorder(all, visible, _dragIndex, to));
            }
            AfterChange();
        }
        UpdateHover(e.Location);
        _canvas.Invalidate();
    }

    /// <summary>
    /// Moves visible item <paramref name="from"/> to <paramref name="to"/>; hidden items in <paramref name="all"/>
    /// keep their slots.
    /// </summary>
    private static List<string> Reorder(List<string> all, List<string> visible, int from, int to)
    {
        var moved = visible.ToList();
        var key = moved[from];
        moved.RemoveAt(from);
        moved.Insert(to, key);
        var queue = new Queue<string>(moved);
        return all.Select(k => visible.Contains(k) ? queue.Dequeue() : k).ToList();
    }

    // ---------------------------------------------------------------- reset

    private bool Confirm(string message) =>
        MessageBox.Show(this, message, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    private void Inform(string message) =>
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void ResetColumns()
    {
        if (!_main.Settings.ColumnOrder.ContainsKey(_section.Key)) { Inform($"{_section.Title}의 값 순서는 이미 기본 순서입니다."); return; }
        if (!Confirm($"{_section.Title}의 값 순서를 기본(현재·최소·평균·최대)으로 초기화하시겠습니까?")) return;
        _main.SetColumnOrder(_section, null);
        AfterChange();
    }

    private void ResetRows()
    {
        if (!_main.Settings.RowOrder.ContainsKey(_section.Key)) { Inform($"{_section.Title}의 세부항목은 이미 기본 순서입니다."); return; }
        if (!Confirm($"{_section.Title}의 세부항목 순서를 기본 순서로 초기화하시겠습니까?")) return;
        _main.SetRowOrder(_section, null);
        AfterChange();
    }

    private void ResetGraphs()
    {
        if (!_main.Settings.GraphOrder.ContainsKey(_section.Key)) { Inform($"{_section.Title}의 그래프는 이미 기본 순서입니다."); return; }
        if (!Confirm($"{_section.Title}의 그래프 순서를 기본(사용률·온도)으로 초기화하시겠습니까?")) return;
        _main.SetGraphOrder(_section, null);
        AfterChange();
    }

    private sealed class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }
    }
}
