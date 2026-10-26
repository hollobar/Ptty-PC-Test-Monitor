using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;

namespace PttyPcTestMonitor;

/// <summary>One displayed value (e.g. CPU temperature) with its statistics since the last reset.</summary>
public sealed class Row
{
    public string Key, Label;
    public Func<Snapshot, float?> Value;
    /// <summary>Current-value text; defaults to Value + Unit.</summary>
    public Func<Snapshot, string> Text;
    public string Unit;
    public string Format = "0";
    /// <summary>Smaller values get two decimals (network speeds).</summary>
    public bool FineBelow10;
    public float Warn = float.MaxValue, Crit = float.MaxValue;
    /// <summary>A typically widest value, used to size the columns so they never overlap.</summary>
    public float Sample = 100;
    /// <summary>Widest current-value text when <see cref="Text"/> is set.</summary>
    public string TextSample;

    public float? Min, Max;
    public double Sum;
    public long Count;

    public float? Avg => Count > 0 ? (float)(Sum / Count) : null;

    public void Add(float v)
    {
        if (Min is null || v < Min) Min = v;
        if (Max is null || v > Max) Max = v;
        Sum += v;
        Count++;
    }

    public void Reset()
    {
        Min = Max = null;
        Sum = 0;
        Count = 0;
    }

    public string Display(float? v, bool withUnit = true)
    {
        if (v is not { } x) return "—";
        var text = x.ToString(FineBelow10 && x < 10 ? "0.00" : Format, CultureInfo.InvariantCulture);
        if (!withUnit) return text;
        return text + (Unit is "" or "%" or "°C" ? Unit : " " + Unit);
    }
}

/// <summary>A group of rows shown as one panel (CPU, GPU, …).</summary>
public sealed class Section
{
    public string Key, Title;
    public Color Color;
    public Func<Snapshot, string> Name;
    public List<Row> Rows;
    /// <summary>Value plotted in the graph; null for sections without one.</summary>
    public Func<Snapshot, float?> Graph;
    public string GraphLabel;
    /// <summary>Fixed graph range (0–100 %), or auto-scaled when false (FPS).</summary>
    public bool GraphIsPercent = true;
    public readonly List<float> History = new();
    /// <summary>Shown as a row when <see cref="Rows"/> is empty.</summary>
    public string Placeholder;
    /// <summary>Row keys in their built-in order (fans: detection order), for "기본 순서로".</summary>
    public readonly List<string> DefaultOrder = new();

    /// <summary>Sorts <see cref="Rows"/> by <paramref name="order"/>; rows it doesn't list follow in default order.</summary>
    public void ApplyOrder(List<string> order)
    {
        int Rank(Row r)
        {
            var i = order?.IndexOf(r.Key) ?? -1;
            return i >= 0 ? i : int.MaxValue;
        }
        var sorted = Rows.OrderBy(Rank).ThenBy(r => DefaultOrder.IndexOf(r.Key)).ToList();
        Rows.Clear();
        Rows.AddRange(sorted);
    }
}

/// <summary>A statistic column: current, min, average or max.</summary>
public sealed class Column
{
    public string Key, Title;
    /// <summary>Prefix used in the compact view.</summary>
    public string Symbol;
    public Func<Row, Snapshot, float?> Value;

    public bool IsCurrent => Key == "col.now";

    public static readonly Column[] All =
    [
        new Column { Key = "col.now", Title = "현재", Symbol = "", Value = (r, s) => r.Value(s) },
        new Column { Key = "col.min", Title = "최소", Symbol = "↓", Value = (r, _) => r.Min },
        new Column { Key = "col.avg", Title = "평균", Symbol = "~", Value = (r, _) => r.Avg },
        new Column { Key = "col.max", Title = "최대", Symbol = "↑", Value = (r, _) => r.Max },
    ];
}

public static class Sections
{
    public static readonly Color Accent = Color.FromArgb(96, 165, 250);

    /// <param name="fpsError">Text shown as the FPS panel's name when the frame counter couldn't start.</param>
    public static List<Section> Create(Func<string> fpsError) =>
    [
        new Section
        {
            Key = "cpu", Title = "CPU", Color = Accent, Name = s => s.Cpu.Name,
            Graph = s => s.Cpu.Load, GraphLabel = "CPU 사용률",
            Rows =
            [
                new Row { Key = "cpu.load", Label = "사용률", Value = s => s.Cpu.Load, Unit = "%" },
                new Row { Key = "cpu.temp", Label = "온도", Value = s => s.Cpu.Temperature, Unit = "°C", Format = "0.0", Warn = 80, Crit = 90 },
                new Row { Key = "cpu.power", Label = "전력", Value = s => s.Cpu.Power, Unit = "W", Format = "0.0", Sample = 300 },
                new Row { Key = "cpu.clock", Label = "클럭", Value = s => s.Cpu.Clock, Unit = "MHz", Sample = 6000 },
            ],
        },
        new Section
        {
            Key = "gpu", Title = "GPU", Color = Color.FromArgb(74, 222, 128), Name = s => s.Gpu.Name,
            Graph = s => s.Gpu.Load, GraphLabel = "GPU 사용률",
            Rows =
            [
                new Row { Key = "gpu.load", Label = "사용률", Value = s => s.Gpu.Load, Unit = "%" },
                new Row { Key = "gpu.temp", Label = "온도", Value = s => s.Gpu.Temperature, Unit = "°C", Format = "0.0", Warn = 75, Crit = 85 },
                new Row { Key = "gpu.hotspot", Label = "핫스팟", Value = s => s.Gpu.HotSpot, Unit = "°C", Format = "0.0", Warn = 90, Crit = 100 },
                new Row { Key = "gpu.power", Label = "전력", Value = s => s.Gpu.Power, Unit = "W", Format = "0.0", Sample = 600 },
                new Row
                {
                    Key = "gpu.vram", Label = "VRAM", Value = s => s.Gpu.VramUsedMB / 1024f, Unit = "GB", Format = "0.0", Sample = 24,
                    Text = s => s.Gpu.VramUsedMB is { } u
                        ? s.Gpu.VramTotalMB is { } t ? $"{u / 1024:0.0}/{t / 1024:0} GB" : $"{u / 1024:0.0} GB"
                        : "—",
                    TextSample = "24.0/24 GB",
                },
            ],
        },
        new Section
        {
            Key = "ram", Title = "RAM", Color = Color.FromArgb(192, 132, 252), Name = _ => null,
            Graph = s => s.Ram.Load, GraphLabel = "RAM 사용률",
            Rows =
            [
                new Row
                {
                    Key = "ram.used", Label = "사용량", Value = s => (float)s.Ram.UsedGB, Unit = "GB", Format = "0.0", Sample = 64,
                    Text = s => $"{s.Ram.UsedGB:0.0}/{s.Ram.TotalGB:0} GB",
                    TextSample = "64.0/64 GB",
                },
                new Row { Key = "ram.load", Label = "사용률", Value = s => s.Ram.Load, Unit = "%" },
                new Row { Key = "ram.temp", Label = "온도", Value = s => s.Ram.Temperature, Unit = "°C", Format = "0.0", Warn = 60, Crit = 75 },
            ],
        },
        new Section
        {
            Key = "fps", Title = "FPS", Color = Color.FromArgb(251, 146, 60),
            Name = s => s.Fps?.ProcessName is { } n ? n + ".exe" : fpsError(),
            Graph = s => s.Fps?.Fps, GraphLabel = "FPS", GraphIsPercent = false,
            Rows =
            [
                new Row { Key = "fps.fps", Label = "FPS", Value = s => s.Fps?.Fps, Unit = "", Sample = 999 },
                new Row { Key = "fps.low", Label = "1% Low", Value = s => s.Fps?.OnePercentLow, Unit = "", Sample = 999 },
                new Row { Key = "fps.frametime", Label = "프레임타임", Value = s => s.Fps?.FrameTimeMs, Unit = "ms", Format = "0.0", Sample = 99 },
            ],
        },
        new Section
        {
            Key = "net", Title = "네트워크", Color = Color.FromArgb(56, 189, 248), Name = _ => null,
            Rows =
            [
                new Row { Key = "net.down", Label = "다운로드", Value = s => (float?)s.Net?.DownloadMbps, Unit = "Mbps", Format = "0.0", FineBelow10 = true, Sample = 999 },
                new Row { Key = "net.up", Label = "업로드", Value = s => (float?)s.Net?.UploadMbps, Unit = "Mbps", Format = "0.0", FineBelow10 = true, Sample = 999 },
            ],
        },
        // Rows are added as fans are detected.
        new Section
        {
            Key = "fan", Title = "팬", Color = Color.FromArgb(45, 212, 191), Name = _ => null, Rows = [],
            Placeholder = "감지된 팬 없음",
        },
    ];

    public static Row FanRow(FanReading fan)
    {
        var id = fan.Id;
        return new Row
        {
            Key = "fan:" + id, Label = fan.Name, Unit = "RPM", Sample = 9999,
            Value = s => s.Fans.Find(f => f.Id == id)?.Rpm,
        };
    }
}
