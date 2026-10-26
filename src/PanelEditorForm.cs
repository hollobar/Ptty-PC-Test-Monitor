using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace PttyPcTestMonitor;

/// <summary>
/// Shows one section's panel exactly as the monitor draws it (live) and lets the user drag the value columns
/// (현재/최소/평균/최대) sideways and the sub-items up or down to reorder them. Opened by double-clicking a block
/// in the layout editor.
/// </summary>
public sealed class PanelEditorForm : Form
{
    private enum Drag { None, Column, Row }

    private static readonly Color Bg = Color.FromArgb(20, 22, 26);
    private static readonly Color Marker = Sections.Accent; // the app's accent blue: visible on dark, not alarming like red
    private static readonly Color Hover = Color.FromArgb(90, 96, 165, 250);

    private readonly MainForm _main;
    private readonly Section _section;
    private readonly Action _changed;
    private readonly Timer _timer = new() { Interval = 1000 };
    private readonly Panel _canvas;
    private readonly int _margin;

    private MainForm.PanelGeometry _geometry;
    private Drag _drag;
    private int _dragIndex, _target;
    private Point _mouse;
    private Rectangle _hover;

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

        _canvas = new DoubleBufferedPanel { Dock = DockStyle.Fill, BackColor = Bg };
        _canvas.Paint += (_, e) => PaintCanvas(e.Graphics);
        _canvas.MouseDown += OnCanvasDown;
        _canvas.MouseMove += OnCanvasMove;
        _canvas.MouseUp += OnCanvasUp;
        _canvas.MouseLeave += (_, _) => { _hover = Rectangle.Empty; _canvas.Invalidate(); };

        var hint = new Label
        {
            Text = "값 열(현재·최소·평균·최대)은 좌우로, 세부항목은 이름을 잡고 위아래로 끌어서 순서를 바꿈. 창 크기를 바꾸면 패널도 커짐.",
            AutoSize = true, ForeColor = Color.FromArgb(150, 156, 166), Margin = new Padding(0, 0, 0, 6),
        };
        // Wrap the hint to the window width instead of letting it widen the window.
        Resize += (_, _) => hint.MaximumSize = new Size(Math.Max(100, ClientSize.Width - _margin * 2), 0);
        var resetColumns = new Button { Text = "값 순서 초기화", AutoSize = true, BackColor = SystemColors.Control };
        var resetRows = new Button { Text = "세부항목 순서 초기화", AutoSize = true, BackColor = SystemColors.Control };
        var close = new Button { Text = "닫기", AutoSize = true, BackColor = SystemColors.Control };
        resetColumns.Click += (_, _) => ResetColumns();
        resetRows.Click += (_, _) => ResetRows();
        close.Click += (_, _) => Close();
        CancelButton = close;

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        buttons.Controls.AddRange(new Control[] { resetColumns, resetRows, close });
        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 1, Padding = new Padding(_margin, 0, _margin, _margin) };
        bottom.Controls.Add(hint, 0, 0);
        bottom.Controls.Add(buttons, 0, 1);

        Controls.Add(_canvas);
        Controls.Add(bottom);

        FitToPanel(StartZoom);
        MinimumSize = new Size(Width / 2, Height / 2);
        _canvas.Resize += (_, _) => _canvas.Invalidate();
        _timer.Tick += (_, _) => _canvas.Invalidate(); // live values
        _timer.Start();
        FormClosed += (_, _) => _timer.Dispose();
    }

    /// <summary>Opens at about twice the monitor's size, limited to most of the screen.</summary>
    private float StartZoom
    {
        get
        {
            var logical = _main.PanelLogicalSize(_section);
            var screen = Screen.FromControl(_main).WorkingArea;
            var fit = Math.Min(screen.Width * 0.8f / (logical.Width * _main.Dpi), screen.Height * 0.7f / (logical.Height * _main.Dpi));
            return Math.Min(Math.Max((float)_main.Settings.Zoom * 2f, 1.6f), fit);
        }
    }

    private Control BottomBar => Controls.OfType<TableLayoutPanel>().First();

    private void FitToPanel(float zoom)
    {
        var logical = _main.PanelLogicalSize(_section);
        var panel = new Size((int)(logical.Width * _main.Dpi * zoom), (int)(logical.Height * _main.Dpi * zoom));
        ClientSize = new Size(panel.Width + _margin * 2, panel.Height + _margin * 2 + BottomBar.GetPreferredSize(new Size(panel.Width, 0)).Height);
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

        // Where it is now: gray dashed box. Where it will go: red dashed box over the slot it will take.
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

    /// <summary>The index the dragged column/row will occupy once dropped.</summary>
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

    private void OnCanvasDown(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _mouse = e.Location;
        if (ColumnAt(e.Location) is var c and >= 0 && _geometry.Columns.Count > 1)
        {
            _drag = Drag.Column;
            _dragIndex = _target = c;
        }
        else if (RowAt(e.Location) is var r and >= 0 && _geometry.Rows.Count > 1)
        {
            _drag = Drag.Row;
            _dragIndex = _target = r;
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
        else
            UpdateHover(e.Location);
        _canvas.Invalidate();
    }

    private void UpdateHover(Point p)
    {
        _hover = Rectangle.Empty;
        _canvas.Cursor = Cursors.Default;
        if (_geometry is null) return;
        if (ColumnAt(p) is var c and >= 0)
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
    }

    private void OnCanvasUp(object sender, MouseEventArgs e)
    {
        var drag = _drag;
        _drag = Drag.None;
        if (drag == Drag.None) return;

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
            else
            {
                var visible = _geometry.Rows.Select(r => r.Key).ToList();
                var all = _section.Rows.Select(r => r.Key).ToList();
                _main.SetRowOrder(_section, Reorder(all, visible, _dragIndex, to));
            }
            _changed();
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

    private void ResetColumns()
    {
        if (!_main.Settings.ColumnOrder.ContainsKey(_section.Key))
        {
            MessageBox.Show(this, $"{_section.Title}의 값 순서는 이미 기본 순서입니다.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!Confirm($"{_section.Title}의 값 순서를 기본(현재·최소·평균·최대)으로 초기화하시겠습니까?")) return;
        _main.SetColumnOrder(_section, null);
        _changed();
        _canvas.Invalidate();
    }

    private void ResetRows()
    {
        if (!_main.Settings.RowOrder.ContainsKey(_section.Key))
        {
            MessageBox.Show(this, $"{_section.Title}의 세부항목은 이미 기본 순서입니다.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!Confirm($"{_section.Title}의 세부항목 순서를 기본 순서로 초기화하시겠습니까?")) return;
        _main.SetRowOrder(_section, null);
        _changed();
        _canvas.Invalidate();
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
