using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PttyPcTestMonitor;

/// <summary>
/// The monitor window. Full view: section panels arranged on a grid (see <see cref="GridLayout"/>).
/// Compact view keeps the layout and only shortens the text (current ↓min ~avg ↑max on one line per value).
/// Resizing the width zooms the content; resizing the height grows the graphs. Right-click for view, opacity and options.
/// </summary>
public sealed class MainForm : Form
{
    public const string AppName = "Ptty PC Test Monitor";

    private static readonly Color Bg = Color.FromArgb(20, 22, 26);
    private static readonly Color HeaderBg = Color.FromArgb(34, 37, 43);
    private static readonly Color PanelBg = Color.FromArgb(30, 33, 39);
    private static readonly Color GraphBg = Color.FromArgb(24, 26, 31);
    private static readonly Color GridColor = Color.FromArgb(48, 52, 60);
    private static readonly Color Fg = Color.FromArgb(230, 232, 236);
    private static readonly Color Dim = Color.FromArgb(140, 146, 156);
    private static readonly Color WarnColor = Color.FromArgb(250, 204, 21);
    private static readonly Color CritColor = Color.FromArgb(248, 113, 113);
    private static readonly Color RecColor = Color.FromArgb(239, 68, 68);

    // Layout in 96-DPI pixels at zoom 1.
    private const float HeaderH = 30, Pad = 8, BannerH = 22, SliderH = 26;
    private const float PanelPad = 8, TitleH = 22, ColTitleH = 16, RowH = 20, GraphGap = 4, ColGap = 10, MaxLabelW = 120;
    private const float ButtonW = 24, StatGap = 6;
    private const int MaxHistory = 2000;

    private readonly SensorReader _reader = new();
    private readonly NetworkSampler _network = new();
    private readonly FrameMonitor _frames = new();
    private readonly List<Section> _sections;
    private readonly Section _fanSection, _fpsSection;
    private readonly CancellationTokenSource _cts = new();
    private readonly Settings _settings;
    private readonly float _dpi;
    private readonly Font _baseFont, _baseBold, _baseSmall;
    private readonly Dictionary<(string, int), float> _textWidths = new();

    private Font _font, _bold, _small, _icons;
    private float _fontZoom;

    private Snapshot _last;
    private string _status = "센서 초기화 중…";
    private string _fpsError;
    private int _fpsTarget;
    private bool _pawnIO = SensorReader.IsPawnIOInstalled();
    private DateTime _since = DateTime.Now;
    private StreamWriter _log;
    private List<string> _logFanIds;
    private OptionsForm _options;
    private readonly ContextMenuStrip _menu;

    private Point _dragStart;
    private bool _dragging, _sliding;
    private Rectangle _bannerRect, _closeRect, _pinRect, _resetRect, _sliderRect;

    public MainForm() : this(Settings.Load()) { }

    /// <param name="settings">Settings to use; <see cref="Preview"/> passes read-only defaults.</param>
    private MainForm(Settings settings)
    {
        _settings = settings;
        Text = AppName;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Bg;
        TopMost = _settings.TopMost;
        Opacity = _settings.Opacity;

        using (var g = CreateGraphics()) _dpi = g.DpiX / 96f;
        _baseFont = new Font("Malgun Gothic", 9f);
        _baseBold = new Font("Malgun Gothic", 9f, FontStyle.Bold);
        _baseSmall = new Font("Malgun Gothic", 8f);

        _sections = Sections.Create(() => _fpsError);
        _fanSection = _sections.Single(s => s.Key == "fan");
        _fpsSection = _sections.Single(s => s.Key == "fps");
        foreach (var section in _sections)
        {
            section.DefaultOrder.AddRange(section.Rows.Select(r => r.Key));
            section.ApplyOrder(_settings.RowOrder.TryGetValue(section.Key, out var order) ? order : null);
        }

        if (_settings.Placements.Count == 0)
            (_settings.GridColumns, _settings.Placements) = GridLayout.Preset("column", SectionKeys);
        GridLayout.Normalize(_settings.Placements, SectionKeys, _settings.GridColumns);

        EnsureFonts(Zoom);
        ClientSize = WindowSize(Zoom, _settings.GraphHeight);
        Location = _settings.Location is { } loc && Screen.AllScreens.Any(s => s.WorkingArea.Contains(loc))
            ? loc
            : new Point(Screen.PrimaryScreen.WorkingArea.Right - Width - (int)(16 * _dpi), Screen.PrimaryScreen.WorkingArea.Top + (int)(16 * _dpi));

        _menu = BuildMenu(); // shown by ShowMenu on right-click, not via ContextMenuStrip, to control its direction
        if (!_settings.ReadOnly) Shown += (_, _) => Task.Run(() => PollLoop(_cts.Token));
    }

    /// <summary>
    /// PttyPcTestMonitor.exe --render &lt;dir&gt;: draws the window with sample data in each view and layout,
    /// plus the options tabs, to PNG files (for checking the UI without real sensors).
    /// </summary>
    public static void Preview(string dir)
    {
        Directory.CreateDirectory(dir);
        using var form = new MainForm(new Settings { ReadOnly = true, Opacity = 1, TopMost = false }) { _pawnIO = true };
        form.CreateControl();
        var random = new Random(1);
        for (var i = 0; i < 120; i++) form.OnSnapshot(SampleSnapshot(random, i), null);

        void Save(string name)
        {
            form.ApplyLayout();
            using var bmp = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
            form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.ClientSize));
            bmp.Save(Path.Combine(dir, name + ".png"));
        }

        void Layout(string preset)
        {
            (form._settings.GridColumns, form._settings.Placements) = GridLayout.Preset(preset, form.SectionKeys);
        }

        Layout("column"); Save("full-column");
        Layout("two"); Save("full-two");
        Layout("row"); Save("full-row");

        // Like the user's sketch: CPU | GPU | 팬 on top, FPS across the bottom.
        form._settings.GridColumns = 3;
        form._settings.Placements = new Dictionary<string, Placement>
        {
            ["cpu"] = new(0, 0), ["gpu"] = new(1, 0), ["fan"] = new(2, 0),
            ["fps"] = new(0, 1, 3, 1), ["ram"] = new(0, 2), ["net"] = new(1, 2),
        };
        Save("full-custom");

        form._settings.Compact = true; Save("compact-custom");
        Layout("two"); Save("compact-two");
        form._settings.Compact = false;

        // A layout with holes (empty column 2, FPS under 네트워크): packing closes them.
        form._settings.GridColumns = 6;
        form._settings.Placements = new Dictionary<string, Placement>
        {
            ["cpu"] = new(0, 0), ["gpu"] = new(1, 0), ["net"] = new(3, 0), ["fan"] = new(4, 0),
            ["ram"] = new(0, 1, 2, 1), ["fps"] = new(3, 1),
        };
        form.SettingsChanged();
        Save("full-packed");
        File.WriteAllText(Path.Combine(dir, "packed.txt"), Placement.FormatAll(form._settings.Placements));
        Layout("column");

        // Usage + temperature graphs: below the values, then drawn behind them.
        form._settings.Hidden.ExceptWith(Sections.TempGraphKeys);
        Layout("two");
        Save("graphs-below");
        void AllModes(Action<GraphMode> set) { foreach (var key in form.SectionKeys) set(form._settings.ModeOf(key)); }
        AllModes(m => m.Behind = true);
        Save("graphs-behind");
        AllModes(m => { m.ArrangeBehind = GraphArrange.Side; m.Strength = 0.45; });
        Save("graphs-behind-side");
        AllModes(m => { m.Behind = false; m.ArrangeBelow = GraphArrange.Side; });
        form._settings.GraphSplit["cpu"] = 0.7;
        Save("graphs-side-70");
        AllModes(m => m.ArrangeBelow = GraphArrange.Overlap);
        Save("graphs-overlap");
        // Mixed: each section its own way.
        form._settings.ModeOf("cpu").ArrangeBelow = GraphArrange.Side;
        form._settings.ModeOf("gpu").Behind = true;
        form._settings.ModeOf("ram").ArrangeBelow = GraphArrange.Stack;
        Save("graphs-mixed");
        AllModes(m => { m.Behind = false; m.ArrangeBelow = GraphArrange.Side; });
        Layout("column");

        // Panel editor for CPU, before and after reordering columns and sub-items.
        var cpu = form._sections[0];
        void SaveEditor(string name)
        {
            using var editor = new PanelEditorForm(form, cpu, () => { }) { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000) };
            editor.Show();
            Application.DoEvents();
            using var bmp = new Bitmap(editor.Width, editor.Height);
            editor.DrawToBitmap(bmp, new Rectangle(Point.Empty, editor.Size));
            bmp.Save(Path.Combine(dir, name + ".png"));
            editor.Close();
        }
        SaveEditor("panel-editor");
        form.SetColumnOrder(cpu, ["col.max", "col.now", "col.avg", "col.min"]);
        form.SetRowOrder(cpu, ["cpu.temp", "cpu.load", "cpu.clock", "cpu.power"]);
        SaveEditor("panel-editor-reordered");
        form.SetColumnOrder(cpu, null);
        form.SetRowOrder(cpu, null);

        // Right-click menu with the view submenu open.
        var menu = form._menu;
        menu.Show(new Point(-4000, -4000));
        var viewItem = (ToolStripMenuItem)menu.Items[0];
        viewItem.ShowDropDown();
        Application.DoEvents();
        foreach (var (strip, name) in new[] { ((ToolStripDropDown)menu, "menu"), (viewItem.DropDown, "menu-view") })
        {
            using var bmp = new Bitmap(strip.Width, strip.Height);
            strip.DrawToBitmap(bmp, new Rectangle(Point.Empty, strip.Size));
            bmp.Save(Path.Combine(dir, name + ".png"));
        }
        menu.Close();

        // The options window only draws its controls once shown; show it off-screen.
        using var options = new OptionsForm(form) { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000) };
        options.Show();
        foreach (var tab in new[] { "general", "items", "layout", "tools" })
        {
            options.SelectTab(tab);
            Application.DoEvents();
            using var bmp = new Bitmap(options.Width, options.Height);
            options.DrawToBitmap(bmp, new Rectangle(Point.Empty, options.Size));
            bmp.Save(Path.Combine(dir, "options-" + tab + ".png"));
        }
    }

    private static Snapshot SampleSnapshot(Random r, int i) => new()
    {
        Time = DateTime.Now,
        Cpu = new CpuReading { Name = "AMD Ryzen 5 5600X 6-Core Processor", Load = 40 + r.Next(50), Temperature = 60 + r.Next(25), Power = 70 + r.Next(30), Clock = 4400 + r.Next(250) },
        Gpu = new GpuReading { Name = "NVIDIA GeForce RTX 3060 Ti", Load = 80 + r.Next(20), Temperature = 65 + r.Next(10), HotSpot = 78 + r.Next(10), Power = 190 + r.Next(30), VramUsedMB = 6200 + r.Next(800), VramTotalMB = 8192 },
        Ram = new RamReading { UsedGB = 14 + r.NextDouble() * 4, TotalGB = 31.9, Load = 45 + r.Next(10), Temperature = null },
        Fans =
        [
            new FanReading { Id = "/lpc/0/fan/1", Name = "Pump Fan", Rpm = 2200 + r.Next(60) },
            new FanReading { Id = "/lpc/0/fan/2", Name = "System Fan #1", Rpm = 1250 + r.Next(40) },
            new FanReading { Id = "/gpu/0/fan/1", Name = "GPU Fan 1", Rpm = 1600 + r.Next(80) },
            new FanReading { Id = "/usb/0/fan/0", Name = "Kraken Z3 Pump", Rpm = 2230 + r.Next(40) },
        ],
        Fps = new FpsReading { ProcessId = 1234, ProcessName = "Cyberpunk2077", Fps = 110 + r.Next(40), FrameTimeMs = 7 + (float)r.NextDouble() * 2, OnePercentLow = 80 + r.Next(10) },
        Net = new NetworkReading { DownloadMbps = r.NextDouble() * 120, UploadMbps = r.NextDouble() * 8 },
    };

    // ---------------------------------------------------------------- API for the options window

    public Settings Settings => _settings;
    public IReadOnlyList<Section> AllSections => _sections;
    public IReadOnlyList<string> SectionKeys => _sections.Select(s => s.Key).ToList();

    /// <summary>Saves a new row order for <paramref name="section"/> (null = back to the default order).</summary>
    public void SetRowOrder(Section section, List<string> keys)
    {
        if (keys is null) _settings.RowOrder.Remove(section.Key);
        else _settings.RowOrder[section.Key] = keys;
        section.ApplyOrder(keys);
        SettingsChanged();
    }
    public bool IsLogging => _log is not null;
    public bool PawnIOInstalled => _pawnIO;

    /// <summary>Applies and saves changed settings.</summary>
    public void SettingsChanged()
    {
        GridLayout.Normalize(_settings.Placements, SectionKeys, _settings.GridColumns);
        TopMost = _settings.TopMost;
        Opacity = _settings.Opacity;
        _settings.Save();
        ApplyLayout();
    }

    public void ShowOptions(string tab = null)
    {
        if (_options is null || _options.IsDisposed)
        {
            _options = new OptionsForm(this);
            _options.Show(this);
        }
        _options.SelectTab(tab);
        _options.Activate();
    }

    // ---------------------------------------------------------------- visibility & measuring

    private float Zoom => (float)_settings.Zoom;
    private float Unit => _dpi * Zoom;
    private int S(float px) => (int)Math.Round(px * Unit);

    private bool IsShown(string key) => !_settings.Hidden.Contains(key);
    private List<Section> VisibleSections => _sections.Where(s => IsShown(s.Key)).ToList();
    private List<Row> VisibleRows(Section s) => s.Rows.Where(r => IsShown(r.Key)).ToList();
    /// <summary>All of <paramref name="section"/>'s graphs in the user's order.</summary>
    public List<Graph> OrderedGraphs(Section section)
    {
        if (!_settings.GraphOrder.TryGetValue(section.Key, out var order)) return section.Graphs.ToList();
        return section.Graphs.OrderBy(g => order.IndexOf(g.Key) is var i && i >= 0 ? i : int.MaxValue)
                             .ThenBy(g => section.Graphs.IndexOf(g)).ToList();
    }

    private List<Graph> VisibleGraphs(Section s) => OrderedGraphs(s).Where(g => IsShown(g.Key)).ToList();

    /// <summary>Graphs that take space below the values (none when they are drawn behind).</summary>
    private List<Graph> GraphsBelow(Section s) => ModeOf(s).Behind ? [] : VisibleGraphs(s);

    public GraphMode ModeOf(Section s) => _settings.ModeOf(s.Key);


    public int GraphHeightOf(Section s) => _settings.GraphHeights.TryGetValue(s.Key, out var h) ? h : _settings.GraphHeight;
    public double SplitOf(Section s) => _settings.GraphSplit.TryGetValue(s.Key, out var v) ? v : 0.5;
    public int VisibleGraphCount(Section s) => VisibleGraphs(s).Count;

    /// <summary>Share of the graph area for <paramref name="section"/>'s first graph (null = half each).</summary>
    public void SetGraphSplit(Section section, double? split)
    {
        if (split is { } v) _settings.GraphSplit[section.Key] = Math.Max(Settings.MinSplit, Math.Min(Settings.MaxSplit, v));
        else _settings.GraphSplit.Remove(section.Key);
        SettingsChanged();
    }

    /// <summary>Graph height for <paramref name="section"/> (null = the common height).</summary>
    public void SetGraphHeight(Section section, int? height)
    {
        if (height is { } h) _settings.GraphHeights[section.Key] = Math.Max(Settings.MinGraphHeight, Math.Min(Settings.MaxGraphHeight, h));
        else _settings.GraphHeights.Remove(section.Key);
        SettingsChanged();
    }

    /// <summary>Saves a graph order for <paramref name="section"/> (null = default order).</summary>
    public void SetGraphOrder(Section section, List<string> keys)
    {
        if (keys is null) _settings.GraphOrder.Remove(section.Key);
        else _settings.GraphOrder[section.Key] = keys;
        SettingsChanged();
    }
    /// <summary>All four value columns in <paramref name="section"/>'s order (user order, else 현재·최소·평균·최대).</summary>
    public List<Column> OrderedColumns(Section section)
    {
        if (!_settings.ColumnOrder.TryGetValue(section.Key, out var order)) return Column.All.ToList();
        return Column.All.OrderBy(c => order.IndexOf(c.Key) is var i && i >= 0 ? i : int.MaxValue)
                         .ThenBy(c => Array.IndexOf(Column.All, c)).ToList();
    }

    private List<Column> VisibleColumnsFor(Section section) => OrderedColumns(section).Where(c => IsShown(c.Key)).ToList();

    /// <summary>Saves a value-column order for <paramref name="section"/> (null = default order).</summary>
    public void SetColumnOrder(Section section, List<string> keys)
    {
        if (keys is null) _settings.ColumnOrder.Remove(section.Key);
        else _settings.ColumnOrder[section.Key] = keys;
        SettingsChanged();
    }
    private bool NeedsBanner => !_pawnIO || !SensorReader.IsElevated;

    /// <summary>Text width in 96-DPI pixels at zoom 1 (0 = regular, 1 = bold, 2 = small font).</summary>
    private float TextWidth(string text, int font = 0)
    {
        if (_textWidths.TryGetValue((text, font), out var w)) return w;
        var f = font == 1 ? _baseBold : font == 2 ? _baseSmall : _baseFont;
        w = TextRenderer.MeasureText(text, f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width / _dpi;
        _textWidths[(text, font)] = w;
        return w;
    }

    private float ColumnWidth(Section section, Column column)
    {
        var rows = VisibleRows(section);
        if (rows.Count == 0) return 0;
        if (_settings.Compact && !column.IsCurrent)
            return rows.Max(r => TextWidth(CompactStat(column, r, r.Sample), 2)) + StatGap;
        var widest = rows.Max(r => TextWidth(column.IsCurrent && r.TextSample is not null ? r.TextSample : r.Display(r.Sample)));
        var title = ShowColumnTitles ? TextWidth(column.Title, 2) : 0;
        return Math.Max(widest, title) + (_settings.Compact ? StatGap : ColGap);
    }

    /// <summary>Compact min/avg/max text: "↓60.0" (no unit; the current value carries it).</summary>
    private static string CompactStat(Column column, Row row, float? v) => column.Symbol + row.Display(v, withUnit: false);

    private bool ShowColumnTitles => _settings.ShowColumnTitles && !_settings.Compact;

    private float LabelWidth(Section section)
    {
        var rows = VisibleRows(section);
        var widest = rows.Count == 0 ? 0 : rows.Max(r => TextWidth(r.Label));
        return Math.Max(40, Math.Min(MaxLabelW, widest)) + 6;
    }

    /// <summary>Height the graphs below the values need: one band each when stacked, else one shared band.</summary>
    private float GraphsBelowHeight(Section section, int graphHeight)
    {
        var n = GraphsBelow(section).Count;
        if (n == 0) return 0;
        var h = _settings.GraphHeights.TryGetValue(section.Key, out var own) ? own : graphHeight;
        return ModeOf(section).ArrangeBelow == GraphArrange.Stack ? n * (GraphGap + h) : GraphGap + h;
    }

    /// <summary>
    /// Splits <paramref name="area"/> between <paramref name="n"/> graphs: stacked or side by side by the section's
    /// split, or all in the same place when overlapping.
    /// </summary>
    private List<Rectangle> SplitArea(Rectangle area, int n, GraphArrange arrange, double split)
    {
        if (n <= 1 || arrange == GraphArrange.Overlap) return Enumerable.Repeat(area, n).ToList();
        var gap = S(GraphGap);
        if (arrange == GraphArrange.Stack)
        {
            var first = (int)Math.Round((area.Height - gap) * split);
            return [new Rectangle(area.X, area.Y, area.Width, first),
                    Rectangle.FromLTRB(area.X, area.Y + first + gap, area.Right, area.Bottom)];
        }
        var w = (int)Math.Round((area.Width - gap) * split);
        return [new Rectangle(area.X, area.Y, w, area.Height),
                Rectangle.FromLTRB(area.X + w + gap, area.Y, area.Right, area.Bottom)];
    }

    private SizeF PanelMinSize(Section section, int graphHeight)
    {
        var columns = VisibleColumnsFor(section);
        var rows = VisibleRows(section).Count;
        var width = PanelPad * 2 + LabelWidth(section) + columns.Sum(c => ColumnWidth(section, c));
        width = Math.Max(width, PanelPad * 2 + TextWidth(section.Title, 1) + 60);

        var height = PanelPad + TitleH
                     + (ShowColumnTitles && rows > 0 && columns.Count > 0 ? ColTitleH : 0)
                     + (section.Rows.Count == 0 ? 1 : rows) * RowH
                     + GraphsBelowHeight(section, graphHeight)
                     + PanelPad;
        return new SizeF(width, height);
    }

    // ---------------------------------------------------------------- full view layout

    private (Dictionary<string, RectangleF> Rects, SizeF Size) GridFor(int graphHeight)
    {
        var visible = VisibleSections.Select(s => s.Key).ToList();
        var packed = visible.ToDictionary(k => k, k => _settings.Placements[k].Clone());
        GridLayout.Pack(packed, visible);
        return GridLayout.Compute(visible, packed, key => PanelMinSize(_sections.First(s => s.Key == key), graphHeight));
    }

    private float TopAreaHeight => HeaderH + Pad + (NeedsBanner ? BannerH + Pad / 2 : 0);

    /// <summary>Window size in 96-DPI pixels at zoom 1.</summary>
    private SizeF LogicalSize(int graphHeight)
    {
        var grid = GridFor(graphHeight).Size;
        var width = Math.Max(grid.Width + Pad * 2, 240);
        var height = TopAreaHeight + grid.Height + Pad + (_settings.ShowOpacitySlider ? SliderH : 0);
        return new SizeF(width, height);
    }

    private Size WindowSize(float zoom, int graphHeight)
    {
        var size = LogicalSize(graphHeight);
        return new Size((int)Math.Round(size.Width * _dpi * zoom), (int)Math.Round(size.Height * _dpi * zoom));
    }

    /// <summary>Fits the window to what is currently shown.</summary>
    public void ApplyLayout()
    {
        var size = WindowSize(Zoom, _settings.GraphHeight);
        if (ClientSize != size) ClientSize = size;
        Invalidate();
    }

    private void EnsureFonts(float zoom)
    {
        if (_font is not null && Math.Abs(zoom - _fontZoom) < 0.005f) return;
        _fontZoom = zoom;
        _font?.Dispose(); _bold?.Dispose(); _small?.Dispose(); _icons?.Dispose();
        _font = new Font("Malgun Gothic", 9f * zoom);
        _bold = new Font("Malgun Gothic", 9f * zoom, FontStyle.Bold);
        _small = new Font("Malgun Gothic", 8f * zoom);
        _icons = new Font("Segoe MDL2 Assets", 8f * zoom);
    }

    // ---------------------------------------------------------------- borderless resizing

    private const int WM_NCHITTEST = 0x84, WM_SIZING = 0x214, WM_EXITSIZEMOVE = 0x232;
    private const int HTCLIENT = 1, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
                      HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const int WMSZ_LEFT = 1, WMSZ_RIGHT = 2, WMSZ_TOP = 3, WMSZ_TOPLEFT = 4, WMSZ_TOPRIGHT = 5,
                      WMSZ_BOTTOM = 6, WMSZ_BOTTOMLEFT = 7;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_NCHITTEST:
                base.WndProc(ref m);
                if ((int)m.Result == HTCLIENT) m.Result = (IntPtr)HitTestEdges(m.LParam);
                return;

            case WM_SIZING:
                ConstrainSizing(m.WParam.ToInt32(), m.LParam);
                m.Result = (IntPtr)1;
                return;

            case WM_EXITSIZEMOVE:
                _settings.Location = Location;
                _settings.Save();
                break;
        }
        base.WndProc(ref m);
    }

    private int HitTestEdges(IntPtr lParam)
    {
        var lp = lParam.ToInt64();
        var p = PointToClient(new Point(unchecked((short)lp), unchecked((short)(lp >> 16))));
        var grip = (int)Math.Ceiling(6 * _dpi);
        bool l = p.X < grip, r = p.X >= ClientSize.Width - grip, t = p.Y < grip, b = p.Y >= ClientSize.Height - grip;
        return t && l ? HTTOPLEFT : t && r ? HTTOPRIGHT : b && l ? HTBOTTOMLEFT : b && r ? HTBOTTOMRIGHT
             : l ? HTLEFT : r ? HTRIGHT : t ? HTTOP : b ? HTBOTTOM : HTCLIENT;
    }

    /// <summary>
    /// The width sets the zoom; the height comes from the content, with any vertical drag going to the graphs.
    /// </summary>
    private void ConstrainSizing(int edge, IntPtr rectPtr)
    {
        var rc = (RECT)Marshal.PtrToStructure(rectPtr, typeof(RECT));

        if (edge is not (WMSZ_TOP or WMSZ_BOTTOM))
        {
            var zoom = (rc.Right - rc.Left) / (LogicalSize(_settings.GraphHeight).Width * _dpi);
            _settings.Zoom = Math.Max(Settings.MinZoom, Math.Min(Settings.MaxZoom, zoom));
        }

        if (edge is not (WMSZ_LEFT or WMSZ_RIGHT) && VisibleSections.Any(s => GraphsBelow(s).Count > 0))
            _settings.GraphHeight = GraphHeightFor(rc.Bottom - rc.Top);

        var size = WindowSize(Zoom, _settings.GraphHeight);
        if (edge is WMSZ_LEFT or WMSZ_TOPLEFT or WMSZ_BOTTOMLEFT) rc.Left = rc.Right - size.Width; else rc.Right = rc.Left + size.Width;
        if (edge is WMSZ_TOP or WMSZ_TOPLEFT or WMSZ_TOPRIGHT) rc.Top = rc.Bottom - size.Height; else rc.Bottom = rc.Top + size.Height;

        Marshal.StructureToPtr(rc, rectPtr, false);
        Invalidate();
    }

    /// <summary>The largest graph height whose window still fits in <paramref name="heightPx"/> (height grows with it).</summary>
    private int GraphHeightFor(int heightPx)
    {
        int lo = Settings.MinGraphHeight, hi = Settings.MaxGraphHeight;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (WindowSize(Zoom, mid).Height <= heightPx) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    // ---------------------------------------------------------------- polling

    private async Task PollLoop(CancellationToken ct)
    {
        if (!_frames.Start()) _fpsError = "측정 불가: " + _frames.Error;

        var lastPawnCheck = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            Snapshot snap = null;
            string error = null;
            try
            {
                snap = _reader.Read();
                snap.Net = _network.Sample();
                snap.Fps = _fpsError is null ? _frames.Read() : null;
            }
            catch (Exception ex)
            {
                error = "센서 읽기 실패: " + ex.Message;
            }

            // Pick up a PawnIO install done while running.
            if (!_pawnIO && DateTime.UtcNow - lastPawnCheck > TimeSpan.FromSeconds(10))
            {
                lastPawnCheck = DateTime.UtcNow;
                if (SensorReader.IsPawnIOInstalled())
                {
                    _reader.Dispose(); // reopen so LibreHardwareMonitor attaches to the new driver
                    BeginInvokeSafe(() => { _pawnIO = true; ApplyLayout(); });
                }
            }

            BeginInvokeSafe(() => OnSnapshot(snap, error));

            var wait = TimeSpan.FromMilliseconds(_settings.IntervalMs) - (DateTime.UtcNow - started);
            if (wait > TimeSpan.Zero)
            {
                try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { }
            }
        }
    }

    private void BeginInvokeSafe(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch (InvalidOperationException) { }
    }

    private void OnSnapshot(Snapshot snap, string error)
    {
        if (snap is null)
        {
            _status = error;
            Invalidate();
            return;
        }

        _status = null;
        _last = snap;
        if (AddNewFans(snap)) ApplyLayout();

        // A different app came to the front: its frame stats start fresh.
        if (snap.Fps is { } fps && fps.ProcessId != _fpsTarget)
        {
            _fpsTarget = fps.ProcessId;
            foreach (var row in _fpsSection.Rows) row.Reset();
            foreach (var graph in _fpsSection.Graphs) graph.History.Clear();
        }

        foreach (var section in _sections)
        {
            foreach (var row in section.Rows)
            {
                if (row.Value(snap) is { } v) row.Add(v);
            }
            foreach (var graph in section.Graphs) graph.Add(snap);
        }
        WriteLog(snap);
        Invalidate();
    }

    /// <summary>
    /// Adds a row for each fan the first time it spins. Unused motherboard headers report 0 RPM forever and are
    /// skipped; a fan that later stops (e.g. GPU zero-RPM mode) keeps its row.
    /// </summary>
    private bool AddNewFans(Snapshot snap)
    {
        var added = false;
        foreach (var fan in snap.Fans.Where(f => f.Rpm > 0))
        {
            if (_fanSection.Rows.Any(r => r.Key == "fan:" + fan.Id)) continue;
            _fanSection.Rows.Add(Sections.FanRow(fan));
            _fanSection.DefaultOrder.Add("fan:" + fan.Id);
            added = true;
        }
        if (added) _fanSection.ApplyOrder(_settings.RowOrder.TryGetValue("fan", out var order) ? order : null);
        return added;
    }

    // ---------------------------------------------------------------- painting

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        EnsureFonts(Zoom);

        PaintFull(g);

        using var border = new Pen(Color.FromArgb(60, 64, 72));
        g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    private string Elapsed
    {
        get
        {
            var elapsed = DateTime.Now - _since;
            return $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        }
    }

    /// <summary>Reset / pin / close buttons, right-aligned ending at <paramref name="right"/>.</summary>
    private int DrawButtons(Graphics g, int right, int top, int height)
    {
        _closeRect = new Rectangle(right - S(ButtonW + 4), top, S(ButtonW + 4), height);
        _pinRect = new Rectangle(_closeRect.Left - S(ButtonW), top, S(ButtonW), height);
        _resetRect = _settings.ShowResetButton ? new Rectangle(_pinRect.Left - S(ButtonW), top, S(ButtonW), height) : Rectangle.Empty;
        DrawCentered(g, "", _icons, Dim, _closeRect);
        DrawCentered(g, TopMost ? "" : "", _icons, TopMost ? Sections.Accent : Dim, _pinRect);
        if (_settings.ShowResetButton) DrawCentered(g, "", _icons, Dim, _resetRect);
        return _settings.ShowResetButton ? _resetRect.Left : _pinRect.Left;
    }

    private void PaintFull(Graphics g)
    {
        var w = ClientSize.Width;

        // Title bar: name, recording dot, elapsed time since reset, buttons.
        using (var hb = new SolidBrush(HeaderBg)) g.FillRectangle(hb, 0, 0, w, S(HeaderH));
        var timeRight = DrawButtons(g, w, 0, S(HeaderH)) - S(2);
        DrawTextRight(g, Elapsed, _font, Dim, timeRight, 0, S(HeaderH));
        var titleRight = timeRight - TextRenderer.MeasureText(Elapsed, _font).Width - S(6);
        if (_log is not null)
        {
            var cx = titleRight - S(8);
            using var rb = new SolidBrush(RecColor);
            g.FillEllipse(rb, cx, S(HeaderH) / 2 - S(4), S(8), S(8));
            titleRight = cx - S(4);
        }
        DrawTextIn(g, AppName, _bold, Fg, new Rectangle(S(Pad), 0, Math.Max(0, titleRight - S(Pad)), S(HeaderH)));

        var y = S(HeaderH + Pad);
        if (NeedsBanner)
        {
            _bannerRect = new Rectangle(S(Pad), y, w - S(Pad * 2), S(BannerH));
            var msg = !SensorReader.IsElevated
                ? "⚠ 관리자 권한이 아님 — 온도·FPS가 표시되지 않을 수 있음"
                : "⚠ CPU·RAM 온도·메인보드 팬: PawnIO 필요 (클릭하여 설치)";
            using (var bb = new SolidBrush(Color.FromArgb(60, 250, 204, 21))) g.FillRectangle(bb, _bannerRect);
            DrawTextIn(g, msg, _small, WarnColor, Rectangle.Inflate(_bannerRect, -S(6), 0));
        }
        else
        {
            _bannerRect = Rectangle.Empty;
        }

        var (rects, _) = GridFor(_settings.GraphHeight);
        var originY = TopAreaHeight;
        foreach (var section in VisibleSections)
        {
            var r = rects[section.Key];
            var rect = new Rectangle(S(Pad + r.X), S(originY + r.Y), S(r.Width), S(r.Height));
            PaintPanel(g, section, rect);
        }

        if (_settings.ShowOpacitySlider)
            DrawSlider(g, ClientSize.Height - S(Pad) - S(SliderH), w);
        else
            _sliderRect = Rectangle.Empty;

        if (_status is not null)
            DrawText(g, _status, _small, WarnColor, S(Pad), ClientSize.Height - S(Pad + 4), S(Pad));
    }

    /// <summary>Where a painted panel's value columns and rows are, for the panel editor's drag hit-tests.</summary>
    public sealed class PanelGeometry
    {
        public readonly List<(string Key, int Left, int Right)> Columns = new();
        public readonly List<(string Key, int Top, int Bottom)> Rows = new();
        public readonly List<(string Key, Rectangle Rect)> Graphs = new();
        /// <summary>Whole graph area, and the gap between two stacked / side-by-side graphs (empty otherwise).</summary>
        public Rectangle GraphArea, Divider;
        public GraphArrange Arrange;
        public bool Behind;
        public int LabelLeft, LabelRight, Top, Bottom;
    }

    /// <summary>
    /// Size of <paramref name="section"/>'s panel as laid out on the monitor now (its minimum size if hidden),
    /// in 96-DPI pixels at zoom 1.
    /// </summary>
    public SizeF PanelLogicalSize(Section section)
    {
        var (rects, _) = GridFor(_settings.GraphHeight);
        return rects.TryGetValue(section.Key, out var rect) ? rect.Size : PanelMinSize(section, _settings.GraphHeight);
    }

    public float Dpi => _dpi;

    /// <summary>
    /// Paints <paramref name="section"/>'s panel exactly as the monitor does, onto another surface and at another
    /// zoom (the panel editor shows it enlarged). The monitor's own zoom is restored afterwards.
    /// </summary>
    public PanelGeometry PaintPanelTo(Graphics g, Section section, Rectangle r, float zoom)
    {
        var saved = _settings.Zoom;
        _settings.Zoom = zoom;
        try
        {
            EnsureFonts(zoom);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var geometry = new PanelGeometry();
            PaintPanel(g, section, r, geometry);
            return geometry;
        }
        finally
        {
            _settings.Zoom = saved; // fonts follow on the monitor's next paint
        }
    }

    private void PaintPanel(Graphics g, Section section, Rectangle r, PanelGeometry geometry = null)
    {
        using (var bg = new SolidBrush(PanelBg)) FillRounded(g, bg, r, S(4));

        var left = r.Left + S(PanelPad);
        var right = r.Right - S(PanelPad);
        var y = r.Top + S(PanelPad / 2);

        // Graphs behind the values fill everything under the title, drawn first so the text sits on top.
        var mode = ModeOf(section);
        var behind = mode.Behind ? VisibleGraphs(section) : [];
        if (behind.Count > 0)
        {
            var area = Rectangle.FromLTRB(left, y + S(TitleH), right, r.Bottom - S(PanelPad));
            var rects = SplitArea(area, behind.Count, mode.ArrangeBehind, SplitOf(section));
            // The first graph in the order is painted last, i.e. in front.
            for (var i = behind.Count - 1; i >= 0; i--) DrawGraph(g, behind[i], rects[i], GraphStyle.Faint, mode.Strength);
            RecordGraphs(geometry, behind, rects, area, mode.ArrangeBehind, behind: true);
        }

        // Title, device / app name, and (graphs behind) a small legend on the right.
        DrawText(g, section.Title, _bold, section.Color, left, y, S(TitleH));
        var legendLeft = right;
        for (var i = behind.Count - 1; i >= 0; i--)
        {
            var text = "▬ " + behind[i].Short;
            DrawTextRight(g, text, _small, behind[i].Color, legendLeft, y, S(TitleH));
            legendLeft -= TextRenderer.MeasureText(text, _small).Width + S(4);
        }
        var name = _last is null ? null : section.Name(_last);
        if (!string.IsNullOrEmpty(name))
        {
            var nameX = left + TextRenderer.MeasureText(section.Title, _bold).Width + S(2);
            DrawTextIn(g, Shorten(name), _small, Dim, new Rectangle(nameX, y, Math.Max(0, legendLeft - S(4) - nameX), S(TitleH)));
        }
        y += S(TitleH);

        // Column right edges, from the right.
        var columns = VisibleColumnsFor(section);
        var rights = new int[columns.Count];
        var x = right;
        for (var i = columns.Count - 1; i >= 0; i--)
        {
            rights[i] = x;
            x -= S(ColumnWidth(section, columns[i]));
        }
        var labelRight = x;
        var rows = VisibleRows(section);
        if (geometry is not null)
        {
            geometry.LabelLeft = left;
            geometry.LabelRight = labelRight;
            geometry.Top = y;
            for (var i = 0; i < columns.Count; i++)
                geometry.Columns.Add((columns[i].Key, rights[i] - S(ColumnWidth(section, columns[i])), rights[i]));
        }

        if (ShowColumnTitles && rows.Count > 0 && columns.Count > 0)
        {
            for (var i = 0; i < columns.Count; i++)
                DrawTextRight(g, columns[i].Title, _small, Dim, rights[i], y, S(ColTitleH));
            y += S(ColTitleH);
        }

        if (section.Rows.Count == 0)
        {
            DrawText(g, _last is null ? "…" : section.Placeholder, _small, Dim, left, y, S(RowH));
            y += S(RowH);
        }
        foreach (var row in rows)
        {
            geometry?.Rows.Add((row.Key, y, y + S(RowH)));
            DrawTextIn(g, row.Label, _font, Dim, new Rectangle(left, y, Math.Max(0, labelRight - left), S(RowH)));
            for (var i = 0; i < columns.Count; i++)
            {
                var c = columns[i];
                float? v = _last is null ? null : c.Value(row, _last);
                if (_settings.Compact && !c.IsCurrent)
                {
                    var stat = _last is null ? "" : CompactStat(c, row, v);
                    DrawTextRight(g, stat, _small, ColorFor(v, row, Dim), rights[i], y, S(RowH));
                    continue;
                }
                var text = _last is null ? "…" : c.IsCurrent && row.Text is not null ? row.Text(_last) : row.Display(v);
                DrawTextRight(g, text, _font, ColorFor(v, row, c.IsCurrent ? Fg : Dim), rights[i], y, S(RowH));
            }
            y += S(RowH);
        }

        if (geometry is not null) geometry.Bottom = y;

        // Graphs below share whatever height is left, so a panel stretched over several rows grows its graphs.
        var below = GraphsBelow(section);
        if (below.Count > 0)
        {
            var area = Rectangle.FromLTRB(left, y + S(GraphGap), right, r.Bottom - S(PanelPad));
            if (area.Height > S(10))
            {
                var arrange = mode.ArrangeBelow;
                var rects = SplitArea(area, below.Count, arrange, SplitOf(section));
                if (arrange == GraphArrange.Overlap && below.Count > 1)
                {
                    // One box: background once, lines back to front, labels left and right.
                    DrawGraphBackground(g, area);
                    for (var i = below.Count - 1; i >= 0; i--)
                        DrawGraph(g, below[i], area, i == 0 ? GraphStyle.LineLabelLeft : GraphStyle.LineLabelRight);
                }
                else
                {
                    for (var i = 0; i < below.Count; i++) DrawGraph(g, below[i], rects[i], GraphStyle.Full);
                }
                RecordGraphs(geometry, below, rects, area, arrange, behind: false);
            }
        }
    }

    private void RecordGraphs(PanelGeometry geometry, List<Graph> graphs, List<Rectangle> rects, Rectangle area, GraphArrange arrange, bool behind)
    {
        if (geometry is null) return;
        geometry.GraphArea = area;
        geometry.Arrange = arrange;
        geometry.Behind = behind;
        for (var i = 0; i < graphs.Count; i++) geometry.Graphs.Add((graphs[i].Key, rects[i]));
        geometry.Divider = graphs.Count == 2 && arrange != GraphArrange.Overlap
            ? arrange == GraphArrange.Stack
                ? Rectangle.FromLTRB(area.Left, rects[0].Bottom, area.Right, rects[1].Top)
                : Rectangle.FromLTRB(rects[0].Right, area.Top, rects[1].Left, area.Bottom)
            : Rectangle.Empty;
    }

    private enum GraphStyle
    {
        /// <summary>Own background, grid and label.</summary>
        Full,
        /// <summary>Line and label only, sharing a box with another graph (label left or right).</summary>
        LineLabelLeft, LineLabelRight,
        /// <summary>Light line and fill without background or label, for behind the values.</summary>
        Faint,
    }

    private void DrawGraphBackground(Graphics g, Rectangle r)
    {
        using (var bg = new SolidBrush(GraphBg)) g.FillRectangle(bg, r);
        using var grid = new Pen(GridColor);
        foreach (var pct in new[] { 25, 50, 75 })
        {
            var gy = r.Bottom - r.Height * pct / 100f;
            g.DrawLine(grid, r.Left, gy, r.Right, gy);
        }
    }

    private static readonly float[] FpsScales = [30, 60, 90, 120, 144, 165, 240, 360, 500, 1000];

    /// <summary>History, newest on the right; a wider graph shows a longer span.</summary>
    private void DrawGraph(Graphics g, Graph graph, Rectangle r, GraphStyle style, double strength = 0.5)
    {
        var faint = style == GraphStyle.Faint;
        if (style == GraphStyle.Full) DrawGraphBackground(g, r);

        var step = Math.Max(1f, 3 * Unit);
        var count = Math.Max(2, (int)(r.Width / step) + 1);
        var data = graph.History.Skip(Math.Max(0, graph.History.Count - count)).ToList();
        var valid = data.Where(v => !float.IsNaN(v)).ToList();

        // Vertical range: 0–100 %, 0 to a round peak (FPS), or around the temperatures shown.
        float bottom = 0, top = 100;
        string range = null;
        if (graph.Scale == GraphScale.Peak)
        {
            var peak = valid.Count > 0 ? valid.Max() : 0;
            top = FpsScales.FirstOrDefault(t => t >= peak * 1.05f);
            if (top == 0) top = (float)Math.Ceiling(peak / 100) * 100;
            range = $"위 {top:0}";
        }
        else if (graph.Scale == GraphScale.Temperature && valid.Count > 0)
        {
            bottom = (float)Math.Floor((valid.Min() - 5) / 10) * 10;
            top = (float)Math.Ceiling((valid.Max() + 5) / 10) * 10;
            if (top - bottom < 20) top = bottom + 20;
            range = $"{bottom:0}–{top:0}°C";
        }

        if (valid.Count >= 2) DrawGraphLine(g, graph, r, data, count, bottom, top, faint, strength);
        if (faint) return;

        var span = TimeSpan.FromMilliseconds((count - 1) * (double)_settings.IntervalMs);
        var spanText = span.TotalSeconds < 120 ? $"최근 {span.TotalSeconds:0}초" : $"최근 {span.TotalMinutes:0}분";
        var label = valid.Count == 0 && data.Count > 0 ? $"{graph.Label} · 센서 값 없음"
            : range is null ? $"{graph.Label} · {spanText}" : $"{graph.Label} · {spanText} · {range}";

        // Label on top of the line, on a backing so it stays readable. A second graph in the same box labels on the right.
        var labelSize = TextRenderer.MeasureText(label, _small, Size.Empty, TextFormatFlags.NoPrefix);
        var labelWidth = Math.Min(r.Width, labelSize.Width + S(6));
        var labelX = style == GraphStyle.LineLabelRight ? r.Right - labelWidth : r.Left;
        using (var back = new SolidBrush(Color.FromArgb(190, GraphBg)))
            g.FillRectangle(back, labelX, r.Top, labelWidth, labelSize.Height + S(2));
        var labelColor = style == GraphStyle.Full ? Dim : graph.Color;
        TextRenderer.DrawText(g, label, _small, new Rectangle(labelX + S(3), r.Top + S(1), labelWidth - S(3), labelSize.Height), labelColor,
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    /// <summary>Filled line for the samples; gaps (NaN) split it into separate pieces.</summary>
    private void DrawGraphLine(Graphics g, Graph graph, Rectangle r, List<float> data, int count, float bottom, float top, bool faint, double strength)
    {
        var clip = g.Clip;
        g.SetClip(r);
        // Behind the values the strength setting scales both; 0.5 (the default) shows clearly while the numbers stay readable.
        using var fill = new SolidBrush(Color.FromArgb(faint ? (int)(10 + 80 * strength) : 50, graph.Color));
        using var pen = new Pen(Color.FromArgb(faint ? (int)(30 + 225 * strength) : 255, graph.Color), Math.Max(1f, (faint ? 1f : 1.5f) * Unit));

        var piece = new List<PointF>();
        void Flush()
        {
            if (piece.Count >= 2)
            {
                using var path = new GraphicsPath();
                path.AddLines(piece.ToArray());
                path.AddLine(piece[piece.Count - 1], new PointF(piece[piece.Count - 1].X, r.Bottom));
                path.AddLine(new PointF(piece[0].X, r.Bottom), piece[0]);
                g.FillPath(fill, path);
                g.DrawLines(pen, piece.ToArray());
            }
            piece.Clear();
        }

        for (var i = 0; i < data.Count; i++)
        {
            if (float.IsNaN(data[i])) { Flush(); continue; }
            var px = r.Right - (data.Count - 1 - i) * (r.Width / (float)(count - 1));
            var t = Math.Max(0f, Math.Min(1f, (data[i] - bottom) / (top - bottom)));
            piece.Add(new PointF(px, r.Bottom - 1 - (r.Height - 2) * t));
        }
        Flush();
        g.Clip = clip;
    }

    private void DrawSlider(Graphics g, int y, int w)
    {
        DrawText(g, "투명도", _small, Dim, S(Pad), y, S(SliderH));
        DrawTextRight(g, $"{Opacity * 100:0}%", _small, Dim, w - S(Pad), y, S(SliderH));

        var left = S(Pad + 46);
        var right = w - S(Pad + 40);
        var cy = y + S(SliderH) / 2;
        _sliderRect = new Rectangle(left - S(6), y, right - left + S(12), S(SliderH));

        var t = (float)((Opacity - 0.2) / 0.8);
        var knobX = left + (right - left) * t;
        using (var track = new Pen(GridColor, Math.Max(2f, 3 * Unit)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(track, left, cy, right, cy);
        using (var filled = new Pen(Sections.Accent, Math.Max(2f, 3 * Unit)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(filled, left, cy, knobX, cy);
        var k = S(6);
        using var knob = new SolidBrush(Fg);
        g.FillEllipse(knob, knobX - k, cy - k, k * 2, k * 2);
    }

    private void SetOpacityFromSlider(int x)
    {
        var left = _sliderRect.Left + S(6);
        var right = _sliderRect.Right - S(6);
        var t = Math.Max(0, Math.Min(1, (x - left) / (double)Math.Max(1, right - left)));
        Opacity = Math.Round(0.2 + 0.8 * t, 2);
        _settings.Opacity = Opacity;
        _options?.SyncOpacity();
        Invalidate();
    }

    private static Color ColorFor(float? v, Row row, Color normal) =>
        v is not { } x ? normal : x >= row.Crit ? CritColor : x >= row.Warn ? WarnColor : normal;

    /// <summary>Drops vendor boilerplate so names fit.</summary>
    private static string Shorten(string name) => name
        .Replace("NVIDIA GeForce ", "").Replace("AMD Radeon ", "Radeon ").Replace("Intel(R) ", "")
        .Replace("(R)", "").Replace("(TM)", "").Replace(" Processor", "").Trim();

    private static void FillRounded(Graphics g, Brush brush, Rectangle r, int radius)
    {
        using var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    private static void DrawText(Graphics g, string text, Font font, Color color, int x, int y, int h) =>
        TextRenderer.DrawText(g, text, font, new Rectangle(x, y, 10000, h), color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

    private static void DrawTextIn(Graphics g, string text, Font font, Color color, Rectangle r) =>
        TextRenderer.DrawText(g, text, font, r, color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

    private static void DrawTextRight(Graphics g, string text, Font font, Color color, int right, int y, int h) =>
        TextRenderer.DrawText(g, text, font, new Rectangle(right - 10000, y, 10000, h), color,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

    private static void DrawCentered(Graphics g, string text, Font font, Color color, Rectangle r) =>
        TextRenderer.DrawText(g, text, font, r, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

    // ---------------------------------------------------------------- mouse

    private bool OnControl(Point p) =>
        _closeRect.Contains(p) || _pinRect.Contains(p) || _resetRect.Contains(p) || _sliderRect.Contains(p) || _bannerRect.Contains(p);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        if (_closeRect.Contains(e.Location)) Close();
        else if (_pinRect.Contains(e.Location)) { _settings.TopMost = !TopMost; SettingsChanged(); _options?.SyncFromSettings(); }
        else if (_resetRect.Contains(e.Location)) ResetStats();
        else if (_bannerRect.Contains(e.Location)) { if (!_pawnIO) InstallPawnIO(); }
        else if (_sliderRect.Contains(e.Location)) { _sliding = true; SetOpacityFromSlider(e.X); }
        else { _dragging = true; _dragStart = e.Location; }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_sliding) SetOpacityFromSlider(e.X);
        else if (_dragging) Location = new Point(Location.X + e.X - _dragStart.X, Location.Y + e.Y - _dragStart.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_sliding || _dragging)
        {
            _settings.Location = Location;
            _settings.Save();
        }
        _sliding = _dragging = false;

        if (e.Button == MouseButtons.Right) ShowMenu(e.Location);
    }

    /// <summary>
    /// Opens the menu from the click point toward the inside of the window (left/up when it would stick out
    /// on the right/bottom). Windows still flips it at the screen edge.
    /// </summary>
    private void ShowMenu(Point at)
    {
        var size = _menu.GetPreferredSize(Size.Empty);
        var left = at.X + size.Width > ClientSize.Width && at.X - size.Width >= 0;
        var up = at.Y + size.Height > ClientSize.Height && at.Y - size.Height >= 0;
        var direction = up
            ? left ? ToolStripDropDownDirection.AboveLeft : ToolStripDropDownDirection.AboveRight
            : left ? ToolStripDropDownDirection.BelowLeft : ToolStripDropDownDirection.BelowRight;
        _menu.Show(this, at, direction);
    }

    /// <summary>Opens a submenu on the side that stays inside the window; if neither fits, the side with more room.</summary>
    private void PlaceSubmenu(ToolStripMenuItem item)
    {
        var window = RectangleToScreen(ClientRectangle);
        var menu = item.Owner.Bounds;
        var width = item.DropDown.GetPreferredSize(Size.Empty).Width;
        var roomRight = window.Right - menu.Right;
        var roomLeft = menu.Left - window.Left;
        item.DropDownDirection = roomRight >= width || (roomLeft < width && roomRight >= roomLeft)
            ? ToolStripDropDownDirection.Right
            : ToolStripDropDownDirection.Left;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (_settings.DoubleClickReset && e.Button == MouseButtons.Left && !OnControl(e.Location))
            ResetStats();
    }

    // ---------------------------------------------------------------- menu / actions

    /// <summary>Right-click: view mode and the opacity slider toggle for quick changes; everything else lives in the options window.</summary>
    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { Font = new Font("Malgun Gothic", 9f), Renderer = new MenuRenderer() };

        // "보기 설정 (기본)" with the current mode dimmed; the modes open to the side on hover.
        var view = new SuffixMenuItem("보기 설정", () => _settings.Compact ? "(간략히)" : "(기본)");
        var full = new ToolStripMenuItem("기본");
        var compact = new ToolStripMenuItem("간략히 (↓최소 ~평균 ↑최대)");
        full.Click += (_, _) => SetCompact(false);
        compact.Click += (_, _) => SetCompact(true);
        view.DropDownItems.Add(full);
        view.DropDownItems.Add(compact);
        view.DropDownOpening += (_, _) => PlaceSubmenu(view);
        menu.Items.Add(view);

        var slider = new ToolStripMenuItem("투명도 슬라이더 표시");
        slider.Click += (_, _) =>
        {
            _settings.ShowOpacitySlider = !_settings.ShowOpacitySlider;
            SettingsChanged();
            _options?.SyncFromSettings();
        };
        menu.Items.Add(slider);
        menu.Items.Add(new ToolStripSeparator());

        menu.Opening += (_, _) =>
        {
            view.Refresh();
            full.Checked = !_settings.Compact;
            compact.Checked = _settings.Compact;
            slider.Checked = _settings.ShowOpacitySlider;
        };

        menu.Items.Add("옵션…", null, (_, _) => ShowOptions());
        menu.Items.Add("배치 설정…", null, (_, _) => ShowOptions("layout"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => Close());
        return menu;
    }

    /// <summary>
    /// Menu item followed by a dimmed note, e.g. "보기 설정 (기본)". The note is part of the item text so the menu
    /// sizes itself (and places submenus) normally; only its painting differs.
    /// </summary>
    private sealed class SuffixMenuItem : ToolStripMenuItem
    {
        private readonly Func<string> _suffix;

        public SuffixMenuItem(string main, Func<string> suffix)
        {
            Main = main;
            _suffix = suffix;
            Refresh();
        }

        public string Main { get; }
        public string Suffix { get; private set; }

        public void Refresh()
        {
            Suffix = _suffix();
            Text = Main + " " + Suffix;
        }
    }

    private sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item is not SuffixMenuItem item)
            {
                base.OnRenderItemText(e);
                return;
            }
            const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
            e.Text = item.Main;
            base.OnRenderItemText(e);
            // Suffix starts where it would in the full text (measuring "main " alone would drop the trailing space).
            var full = TextRenderer.MeasureText(e.Graphics, item.Text, e.TextFont, Size.Empty, flags).Width;
            var x = e.TextRectangle.X + full - TextRenderer.MeasureText(e.Graphics, item.Suffix, e.TextFont, Size.Empty, flags).Width;
            TextRenderer.DrawText(e.Graphics, item.Suffix, e.TextFont,
                new Rectangle(x, e.TextRectangle.Y, Math.Max(0, e.TextRectangle.Right - x), e.TextRectangle.Height), SystemColors.GrayText, flags);
        }
    }

    private void SetCompact(bool compact)
    {
        _settings.Compact = compact;
        SettingsChanged();
        _options?.SyncFromSettings();
    }

    public void ResetStats()
    {
        foreach (var row in _sections.SelectMany(s => s.Rows)) row.Reset();
        _frames.ResetStats();
        _since = DateTime.Now;
        Invalidate();
    }

    public static string LogDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PttyPcTestMonitor");

    public void ToggleLog()
    {
        if (_log is not null)
        {
            _log.Dispose();
            _log = null;
            Invalidate();
            return;
        }

        try
        {
            Directory.CreateDirectory(LogDir);
            var path = Path.Combine(LogDir, $"log_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            // UTF-8 BOM so Excel opens the Korean header correctly.
            _log = new StreamWriter(path, false, new UTF8Encoding(true)) { AutoFlush = true };
            // Fan columns are the fans known when recording starts.
            _logFanIds = _fanSection.Rows.Select(r => r.Key.Substring(4)).ToList();
            var fanHeaders = _fanSection.Rows.Select(r => $",{r.Label.Replace(",", " ")}(RPM)");
            _log.WriteLine("시간,CPU 사용률(%),CPU 온도(°C),CPU 전력(W),CPU 클럭(MHz)," +
                           "GPU 사용률(%),GPU 온도(°C),GPU 핫스팟(°C),GPU 전력(W),VRAM 사용(MB)," +
                           "RAM 사용(GB),RAM 사용률(%),RAM 온도(°C)," +
                           "FPS 대상,FPS,1% Low,프레임타임(ms),다운로드(Mbps),업로드(Mbps)"
                           + string.Concat(fanHeaders));
            Invalidate();
        }
        catch (Exception ex)
        {
            _log = null;
            MessageBox.Show(this, "기록 파일을 만들 수 없음:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void WriteLog(Snapshot s)
    {
        if (_log is null) return;
        static string F(float? v) => v?.ToString("0.#", CultureInfo.InvariantCulture) ?? "";
        static string F2(double? v) => v?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
        try
        {
            var fans = _logFanIds.Select(id => F(s.Fans.FirstOrDefault(f => f.Id == id)?.Rpm));
            _log.WriteLine(string.Join(",", new[]
            {
                s.Time.ToString("yyyy-MM-dd HH:mm:ss"),
                F(s.Cpu.Load), F(s.Cpu.Temperature), F(s.Cpu.Power), F(s.Cpu.Clock),
                F(s.Gpu.Load), F(s.Gpu.Temperature), F(s.Gpu.HotSpot), F(s.Gpu.Power), F(s.Gpu.VramUsedMB),
                F((float)s.Ram.UsedGB), F(s.Ram.Load), F(s.Ram.Temperature),
                (s.Fps?.ProcessName ?? "").Replace(",", " "), F(s.Fps?.Fps), F(s.Fps?.OnePercentLow), F(s.Fps?.FrameTimeMs),
                F2(s.Net?.DownloadMbps), F2(s.Net?.UploadMbps),
            }.Concat(fans)));
        }
        catch (IOException)
        {
            _log.Dispose();
            _log = null;
        }
    }

    public void SaveDump()
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var path = Path.Combine(LogDir, $"sensors_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(path, _reader.Dump(), new UTF8Encoding(true));
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "저장 실패:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public static void OpenLogFolder()
    {
        Directory.CreateDirectory(LogDir);
        Process.Start("explorer.exe", $"\"{LogDir}\"");
    }

    /// <summary>
    /// PawnIO is the signed driver LibreHardwareMonitor uses for CPU/SMBus/Super I/O access (the old WinRing0
    /// driver is blocked by Windows). Installed through winget, which ships with Windows 11.
    /// </summary>
    public void InstallPawnIO()
    {
        var answer = MessageBox.Show(this,
            "CPU 온도, RAM 온도, 메인보드 팬 속도를 읽으려면 PawnIO 드라이버가 필요함.\n\n" +
            "winget으로 설치함 (namazso.PawnIO, 공식 서명 드라이버).\n설치하시겠습니까?",
            Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        try
        {
            // The poll loop notices the driver within ~10 s after the installer finishes.
            Process.Start(new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = "install --id namazso.PawnIO -e --accept-source-agreements --accept-package-agreements",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "winget을 실행할 수 없음: " + ex.Message + "\n\nhttps://pawnio.eu 에서 직접 설치 후 이 프로그램을 다시 실행하세요.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ---------------------------------------------------------------- shutdown

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _settings.Location = Location;
        _settings.Save();
        _cts.Cancel();
        _log?.Dispose();
        _log = null;
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _frames.Dispose();
        _reader.Dispose();
    }
}
