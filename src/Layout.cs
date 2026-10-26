using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;

namespace PttyPcTestMonitor;

/// <summary>A panel's cell range in the layout grid.</summary>
public sealed class Placement
{
    public const int MaxColumns = 6, MaxRows = 12;

    public int Col, Row, ColSpan = 1, RowSpan = 1;

    public Placement() { }
    public Placement(int col, int row, int colSpan = 1, int rowSpan = 1)
    {
        Col = col; Row = row; ColSpan = colSpan; RowSpan = rowSpan;
    }

    public Placement Clone() => new(Col, Row, ColSpan, RowSpan);

    public bool Overlaps(Placement o) =>
        Col < o.Col + o.ColSpan && o.Col < Col + ColSpan && Row < o.Row + o.RowSpan && o.Row < Row + RowSpan;

    /// <summary>"cpu:0,0,1,1;gpu:0,1,1,1"</summary>
    public static Dictionary<string, Placement> ParseAll(string text)
    {
        var result = new Dictionary<string, Placement>(StringComparer.Ordinal);
        foreach (var part in text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':');
            if (kv.Length != 2) continue;
            var n = kv[1].Split(',').Select(v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : -1).ToArray();
            if (n.Length != 4 || n.Any(v => v < 0)) continue;
            result[kv[0]] = new Placement(n[0], n[1], Math.Max(1, n[2]), Math.Max(1, n[3]));
        }
        return result;
    }

    public static string FormatAll(Dictionary<string, Placement> placements) =>
        string.Join(";", placements.Select(kv => $"{kv.Key}:{kv.Value.Col},{kv.Value.Row},{kv.Value.ColSpan},{kv.Value.RowSpan}"));
}

/// <summary>Grid arrangement of the section panels: presets, editing and size computation.</summary>
public static class GridLayout
{
    /// <summary>Gap between panels, in 96-DPI pixels at zoom 1.</summary>
    public const float Gap = 6;

    // ---------------------------------------------------------------- presets & editing

    public static (int Columns, Dictionary<string, Placement> Placements) Preset(string name, IReadOnlyList<string> keys)
    {
        var p = new Dictionary<string, Placement>(StringComparer.Ordinal);
        switch (name)
        {
            case "two":
                // CPU | RAM / GPU | FPS / 네트워크 | 팬
                for (var i = 0; i < keys.Count; i++) p[keys[i]] = new Placement(i % 2, i / 2);
                if (keys.Count >= 6)
                {
                    p[keys[0]] = new Placement(0, 0); p[keys[2]] = new Placement(1, 0);
                    p[keys[1]] = new Placement(0, 1); p[keys[3]] = new Placement(1, 1);
                    p[keys[4]] = new Placement(0, 2); p[keys[5]] = new Placement(1, 2);
                }
                return (2, p);
            case "row":
                for (var i = 0; i < keys.Count; i++) p[keys[i]] = new Placement(i, 0);
                return (Math.Min(Placement.MaxColumns, keys.Count), p);
            default: // "column"
                for (var i = 0; i < keys.Count; i++) p[keys[i]] = new Placement(0, i);
                return (1, p);
        }
    }

    /// <summary>Gives every key a valid, non-overlapping place inside <paramref name="columns"/> columns.</summary>
    public static void Normalize(Dictionary<string, Placement> placements, IReadOnlyList<string> keys, int columns)
    {
        foreach (var key in placements.Keys.Where(k => !keys.Contains(k)).ToList()) placements.Remove(key);

        var placed = new List<string>();
        foreach (var key in keys)
        {
            if (!placements.TryGetValue(key, out var p))
            {
                p = new Placement();
                placements[key] = p;
                PlaceFree(placements, placed, key, columns);
                placed.Add(key);
                continue;
            }
            p.ColSpan = Math.Max(1, Math.Min(columns, p.ColSpan));
            p.RowSpan = Math.Max(1, Math.Min(Placement.MaxRows, p.RowSpan));
            if (p.Col + p.ColSpan > columns) p.Col = Math.Max(0, columns - p.ColSpan);
            if (placed.Any(o => placements[o].Overlaps(p))) PlaceFree(placements, placed, key, columns);
            placed.Add(key);
        }
        Pack(placements, keys);
    }

    /// <summary>
    /// Drops columns and rows that no panel uses, shifting later panels back. Single empty cells inside a used
    /// column or row stay as they are. Only <paramref name="keys"/> take part, so the window can pack just the
    /// visible panels.
    /// </summary>
    public static void Pack(Dictionary<string, Placement> placements, IReadOnlyCollection<string> keys)
    {
        var items = keys.Select(k => placements[k]).ToList();
        if (items.Count == 0) return;

        for (var c = items.Max(i => i.Col + i.ColSpan) - 1; c >= 0; c--)
        {
            if (items.Any(i => i.Col <= c && c < i.Col + i.ColSpan)) continue;
            foreach (var i in items.Where(i => i.Col > c)) i.Col--;
        }
        for (var r = items.Max(i => i.Row + i.RowSpan) - 1; r >= 0; r--)
        {
            if (items.Any(i => i.Row <= r && r < i.Row + i.RowSpan)) continue;
            foreach (var i in items.Where(i => i.Row > r)) i.Row--;
        }
    }

    /// <summary>Moves <paramref name="key"/> to the first free spot (row by row) among the <paramref name="others"/>.</summary>
    public static void PlaceFree(Dictionary<string, Placement> placements, IEnumerable<string> others, string key, int columns)
    {
        var p = placements[key];
        var occupied = others.Where(o => o != key).Select(o => placements[o]).ToList();
        p.ColSpan = Math.Min(p.ColSpan, columns);
        for (var row = 0; row < 100; row++)
        {
            for (var col = 0; col + p.ColSpan <= columns; col++)
            {
                var candidate = new Placement(col, row, p.ColSpan, p.RowSpan);
                if (occupied.All(o => !o.Overlaps(candidate)))
                {
                    p.Col = col;
                    p.Row = row;
                    return;
                }
            }
        }
    }

    /// <summary>Puts <paramref name="key"/> at <paramref name="target"/> and moves any panel it now covers out of the way.</summary>
    public static void Move(Dictionary<string, Placement> placements, string key, Placement target, int columns)
    {
        var old = placements[key].Clone();
        placements[key] = target;
        var displaced = placements.Keys.Where(k => k != key && placements[k].Overlaps(target)).ToList();
        var settled = placements.Keys.Where(k => !displaced.Contains(k)).ToList();
        foreach (var other in displaced)
        {
            // Prefer the spot the moved panel came from (a swap), else the first free one.
            var p = placements[other];
            var swap = new Placement(old.Col, old.Row, Math.Min(p.ColSpan, columns - old.Col), p.RowSpan);
            if (settled.All(s => !placements[s].Overlaps(swap)) && swap.ColSpan == p.ColSpan)
                placements[other] = swap;
            else
                PlaceFree(placements, settled, other, columns);
            settled.Add(other);
        }
    }

    // ---------------------------------------------------------------- size computation

    /// <summary>
    /// Lays the visible panels out. <paramref name="minSize"/> gives each panel's minimum size; grid columns take the
    /// widest panel they hold and rows the tallest, panels spanning several cells fill them all, and columns or rows
    /// holding no visible panel collapse. Result rectangles are in the same units as <paramref name="minSize"/>.
    /// </summary>
    public static (Dictionary<string, RectangleF> Rects, SizeF Size) Compute(
        IReadOnlyList<string> visibleKeys, Dictionary<string, Placement> placements, Func<string, SizeF> minSize)
    {
        var rects = new Dictionary<string, RectangleF>(StringComparer.Ordinal);
        if (visibleKeys.Count == 0) return (rects, SizeF.Empty);

        var items = visibleKeys.Select(k => (Key: k, P: placements[k], Min: minSize(k))).ToList();
        var cols = items.Max(i => i.P.Col + i.P.ColSpan);
        var rows = items.Max(i => i.P.Row + i.P.RowSpan);

        var colUsed = new bool[cols];
        var rowUsed = new bool[rows];
        foreach (var i in items)
        {
            for (var c = i.P.Col; c < i.P.Col + i.P.ColSpan; c++) colUsed[c] = true;
            for (var r = i.P.Row; r < i.P.Row + i.P.RowSpan; r++) rowUsed[r] = true;
        }

        var widths = Fit(cols, colUsed, items.Select(i => (i.P.Col, i.P.ColSpan, i.Min.Width)));
        var heights = Fit(rows, rowUsed, items.Select(i => (i.P.Row, i.P.RowSpan, i.Min.Height)));

        var xs = Offsets(widths, colUsed);
        var ys = Offsets(heights, rowUsed);
        foreach (var i in items)
        {
            var x = xs[i.P.Col];
            var y = ys[i.P.Row];
            var w = Span(widths, colUsed, i.P.Col, i.P.ColSpan);
            var h = Span(heights, rowUsed, i.P.Row, i.P.RowSpan);
            rects[i.Key] = new RectangleF(x, y, w, h);
        }

        var total = new SizeF(xs[cols - 1] + widths[cols - 1], ys[rows - 1] + heights[rows - 1]);
        return (rects, total);
    }

    /// <summary>Track sizes: single-cell items first, then spanning items widen the tracks they cover evenly.</summary>
    private static float[] Fit(int count, bool[] used, IEnumerable<(int Start, int Span, float Size)> items)
    {
        var sizes = new float[count];
        var list = items.ToList();
        foreach (var (start, span, size) in list.Where(i => i.Span == 1))
            sizes[start] = Math.Max(sizes[start], size);
        foreach (var (start, span, size) in list.Where(i => i.Span > 1).OrderBy(i => i.Span))
        {
            var current = Span(sizes, used, start, span);
            if (current >= size) continue;
            var tracks = Enumerable.Range(start, span).Where(t => used[t]).ToList();
            foreach (var t in tracks) sizes[t] += (size - current) / tracks.Count;
        }
        return sizes;
    }

    private static float[] Offsets(float[] sizes, bool[] used)
    {
        var offsets = new float[sizes.Length];
        float pos = 0;
        var first = true;
        for (var i = 0; i < sizes.Length; i++)
        {
            if (used[i] && !first) pos += Gap;
            offsets[i] = pos;
            if (used[i])
            {
                pos += sizes[i];
                first = false;
            }
        }
        return offsets;
    }

    /// <summary>Total size of tracks start..start+span, including the gaps between used ones.</summary>
    private static float Span(float[] sizes, bool[] used, int start, int span)
    {
        float total = 0;
        var n = 0;
        for (var i = start; i < start + span; i++)
        {
            if (!used[i]) continue;
            total += sizes[i];
            n++;
        }
        return total + Math.Max(0, n - 1) * Gap;
    }
}
