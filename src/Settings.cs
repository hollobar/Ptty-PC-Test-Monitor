using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PttyPcTestMonitor;

/// <summary>How a section's graphs share their area: one above the other, side by side, or on top of each other.</summary>
public enum GraphArrange { Stack, Side, Overlap }

/// <summary>How one section draws its graphs: below or behind the values, arranged how, and how strongly (behind).</summary>
public sealed class GraphMode
{
    public bool Behind;
    /// <summary>Arrangement, kept separately for graphs below the values and behind them.</summary>
    public GraphArrange ArrangeBelow = GraphArrange.Stack, ArrangeBehind = GraphArrange.Overlap;
    /// <summary>Strength (0.05–1) of graphs drawn behind the values.</summary>
    public double Strength = 0.5;

    public GraphArrange Arrange => Behind ? ArrangeBehind : ArrangeBelow;

    /// <summary>"behind,Side,Overlap,0.45"</summary>
    public static GraphMode Parse(string text)
    {
        var m = new GraphMode();
        var p = text.Split(',');
        if (p.Length != 4) return m;
        m.Behind = p[0] == "behind";
        if (Enum.TryParse<GraphArrange>(p[1], out var below)) m.ArrangeBelow = below;
        if (Enum.TryParse<GraphArrange>(p[2], out var behind)) m.ArrangeBehind = behind;
        if (double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var strength))
            m.Strength = Math.Max(0.05, Math.Min(1.0, strength));
        return m;
    }

    public override string ToString() =>
        $"{(Behind ? "behind" : "below")},{ArrangeBelow},{ArrangeBehind},{Strength.ToString("0.00", CultureInfo.InvariantCulture)}";
}

/// <summary>Window preferences in %LOCALAPPDATA%\PttyPcTestMonitor\settings.ini (key=value lines).</summary>
public sealed class Settings
{
    public Point? Location;
    public bool TopMost = true;
    public double Opacity = 0.9;
    public int IntervalMs = 1000;

    /// <summary>Content scale; set by resizing the window horizontally.</summary>
    public double Zoom = 1.0;

    /// <summary>Compact view: every value on one horizontal line instead of panels.</summary>
    public bool Compact;

    /// <summary>Double-clicking the window resets min/avg/max. Off by default to avoid accidental resets.</summary>
    public bool DoubleClickReset;

    /// <summary>Show the reset button in the title bar.</summary>
    public bool ShowResetButton = true;

    /// <summary>Show the column titles (현재/최소/평균/최대) at the top of each panel.</summary>
    public bool ShowColumnTitles = true;

    /// <summary>Height of each graph in 96-DPI pixels before zoom; changed by resizing the window vertically.</summary>
    public int GraphHeight = 44;

    /// <summary>Show the opacity slider at the bottom of the window.</summary>
    public bool ShowOpacitySlider;

    /// <summary>
    /// Display keys the user turned off: sections ("cpu"), rows ("cpu.clock", "fan:/gpu-nvidia/0/fan/1"),
    /// graphs ("gpu.graph") and value columns ("col.min").
    /// </summary>
    public HashSet<string> Hidden = new(Sections.TempGraphKeys, StringComparer.Ordinal);

    /// <summary>Number of columns in the panel grid.</summary>
    public int GridColumns = 1;

    /// <summary>User order of each section's rows (row keys), by section key. Rows not listed keep their default order after these.</summary>
    public Dictionary<string, List<string>> RowOrder = new(StringComparer.Ordinal);

    /// <summary>User order of the value columns (col.now/min/avg/max) per section key.</summary>
    public Dictionary<string, List<string>> ColumnOrder = new(StringComparer.Ordinal);

    /// <summary>User order of each section's graphs (graph keys), by section key.</summary>
    public Dictionary<string, List<string>> GraphOrder = new(StringComparer.Ordinal);

    /// <summary>Graph display mode per section key; sections without an entry use the defaults.</summary>
    public Dictionary<string, GraphMode> GraphModes = new(StringComparer.Ordinal);

    /// <summary>The section's graph mode, created with defaults on first use so it can be edited in place.</summary>
    public GraphMode ModeOf(string sectionKey)
    {
        if (!GraphModes.TryGetValue(sectionKey, out var mode)) GraphModes[sectionKey] = mode = new GraphMode();
        return mode;
    }

    /// <summary>Share (0.15–0.85) of the graph area given to a section's first graph, by section key.</summary>
    public Dictionary<string, double> GraphSplit = new(StringComparer.Ordinal);

    /// <summary>Graph height (96-DPI px) for sections that override <see cref="GraphHeight"/>, by section key.</summary>
    public Dictionary<string, int> GraphHeights = new(StringComparer.Ordinal);

    public const double MinSplit = 0.15, MaxSplit = 0.85;

    /// <summary>Where each section's panel sits in the grid, by section key.</summary>
    public Dictionary<string, Placement> Placements = new(StringComparer.Ordinal);

    /// <summary>Preview rendering: never write the file.</summary>
    public bool ReadOnly;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PttyPcTestMonitor", "settings.ini");

    /// <summary>Where settings lived before the rename to "Ptty PC Test Monitor"; copied over on first run.</summary>
    private static string OldFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PttyPcTestMonitoring", "settings.ini");

    public static Settings Load()
    {
        var s = new Settings();
        try
        {
            if (!File.Exists(FilePath) && File.Exists(OldFilePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.Copy(OldFilePath, FilePath);
            }
            if (!File.Exists(FilePath)) return s;
            var map = File.ReadAllLines(FilePath)
                .Select(l => l.Split(new[] { '=' }, 2))
                .Where(p => p.Length == 2)
                .GroupBy(p => p[0].Trim())
                .ToDictionary(g => g.Key, g => g.Last()[1].Trim());

            if (Int(map, "X") is { } x && Int(map, "Y") is { } y) s.Location = new Point(x, y);
            s.TopMost = Bool(map, "TopMost") ?? s.TopMost;
            s.ShowOpacitySlider = Bool(map, "ShowOpacitySlider") ?? s.ShowOpacitySlider;
            s.Compact = Bool(map, "Compact") ?? s.Compact;
            s.DoubleClickReset = Bool(map, "DoubleClickReset") ?? s.DoubleClickReset;
            s.ShowResetButton = Bool(map, "ShowResetButton") ?? s.ShowResetButton;
            s.ShowColumnTitles = Bool(map, "ShowColumnTitles") ?? s.ShowColumnTitles;
            if (Dbl(map, "Opacity") is { } o) s.Opacity = Clamp(o, 0.2, 1.0);
            if (Dbl(map, "Zoom") is { } z) s.Zoom = Clamp(z, MinZoom, MaxZoom);
            if (Int(map, "IntervalMs") is { } i) s.IntervalMs = (int)Clamp(i, 250, 5000);
            if (Int(map, "GraphHeight") is { } gh) s.GraphHeight = (int)Clamp(gh, MinGraphHeight, MaxGraphHeight);
            if (Int(map, "GridColumns") is { } gc) s.GridColumns = (int)Clamp(gc, 1, Placement.MaxColumns);
            if (map.TryGetValue("Hidden", out var h))
                s.Hidden = new HashSet<string>(h.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
            if (map.TryGetValue("Layout", out var layout))
                s.Placements = Placement.ParseAll(layout);
            if (map.TryGetValue("RowOrder", out var order))
                s.RowOrder = ParseRowOrder(order);
            if (map.TryGetValue("ColumnOrder", out var columnOrder))
                s.ColumnOrder = ParseRowOrder(columnOrder);
            if (map.TryGetValue("GraphOrder", out var graphOrder))
                s.GraphOrder = ParseRowOrder(graphOrder);
            if (map.TryGetValue("GraphModes", out var modes))
            {
                foreach (var part in modes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = part.Split(new[] { ':' }, 2);
                    if (kv.Length == 2) s.GraphModes[kv[0]] = GraphMode.Parse(kv[1]);
                }
            }
            if (map.TryGetValue("GraphSplit", out var split))
                foreach (var (key, value) in ParseNumbers(split)) s.GraphSplit[key] = Clamp(value, MinSplit, MaxSplit);
            if (map.TryGetValue("GraphHeights", out var heights))
                foreach (var (key, value) in ParseNumbers(heights)) s.GraphHeights[key] = (int)Clamp(value, MinGraphHeight, MaxGraphHeight);
            // Settings saved before temperature graphs existed: start them hidden.
            if (!map.ContainsKey("TempGraphs")) s.Hidden.UnionWith(Sections.TempGraphKeys);
        }
        catch
        {
            // Broken settings file: fall back to defaults.
        }
        return s;
    }

    /// <summary>"cpu>cpu.temp|cpu.load;fan>fan:/lpc/0/fan/1|fan:/gpu/0/fan/1"</summary>
    private static Dictionary<string, List<string>> ParseRowOrder(string text)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var part in text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(new[] { '>' }, 2);
            if (kv.Length == 2) result[kv[0]] = kv[1].Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        return result;
    }

    /// <summary>"cpu:0.65;gpu:0.5"</summary>
    private static IEnumerable<(string, double)> ParseNumbers(string text) =>
        text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split(':'))
            .Where(kv => kv.Length == 2 && double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            .Select(kv => (kv[0], double.Parse(kv[1], CultureInfo.InvariantCulture)));

    public const double MinZoom = 0.6, MaxZoom = 3.0;
    public const int MinGraphHeight = 24, MaxGraphHeight = 300;

    private static int? Int(Dictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static double? Dbl(Dictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static bool? Bool(Dictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : null;

    private static double Clamp(double v, double min, double max) => Math.Max(min, Math.Min(max, v));

    public void Save()
    {
        if (ReadOnly) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var inv = CultureInfo.InvariantCulture;
            var lines = new List<string>();
            if (Location is { } p)
            {
                lines.Add($"X={p.X.ToString(inv)}");
                lines.Add($"Y={p.Y.ToString(inv)}");
            }
            lines.Add($"TopMost={TopMost}");
            lines.Add($"Opacity={Opacity.ToString("0.00", inv)}");
            lines.Add($"IntervalMs={IntervalMs.ToString(inv)}");
            lines.Add($"Zoom={Zoom.ToString("0.000", inv)}");
            lines.Add($"Compact={Compact}");
            lines.Add($"DoubleClickReset={DoubleClickReset}");
            lines.Add($"ShowResetButton={ShowResetButton}");
            lines.Add($"ShowColumnTitles={ShowColumnTitles}");
            lines.Add($"GraphHeight={GraphHeight.ToString(inv)}");
            lines.Add($"ShowOpacitySlider={ShowOpacitySlider}");
            lines.Add($"Hidden={string.Join(",", Hidden)}");
            lines.Add($"GridColumns={GridColumns.ToString(inv)}");
            lines.Add($"Layout={Placement.FormatAll(Placements)}");
            lines.Add("RowOrder=" + string.Join(";", RowOrder.Select(kv => kv.Key + ">" + string.Join("|", kv.Value))));
            lines.Add("ColumnOrder=" + string.Join(";", ColumnOrder.Select(kv => kv.Key + ">" + string.Join("|", kv.Value))));
            lines.Add("GraphOrder=" + string.Join(";", GraphOrder.Select(kv => kv.Key + ">" + string.Join("|", kv.Value))));
            // Only modes that differ from the defaults, so a changed default still reaches untouched sections.
            var defaults = new GraphMode().ToString();
            lines.Add("GraphModes=" + string.Join(";", GraphModes.Where(kv => kv.Value.ToString() != defaults).Select(kv => kv.Key + ":" + kv.Value)));
            lines.Add("GraphSplit=" + string.Join(";", GraphSplit.Select(kv => kv.Key + ":" + kv.Value.ToString("0.000", inv))));
            lines.Add("GraphHeights=" + string.Join(";", GraphHeights.Select(kv => kv.Key + ":" + kv.Value.ToString(inv))));
            lines.Add("TempGraphs=1");
            File.WriteAllLines(FilePath, lines);
        }
        catch
        {
            // Not critical.
        }
    }
}
