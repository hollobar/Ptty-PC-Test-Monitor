using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using LibreHardwareMonitor.Hardware;

namespace PttyPcTestMonitor;

public sealed class CpuReading
{
    public string Name;
    public float? Load, Temperature, Power, Clock;
}

public sealed class GpuReading
{
    public string Name;
    public float? Load, Temperature, HotSpot, Power, VramUsedMB, VramTotalMB;
}

public sealed class RamReading
{
    public double UsedGB, TotalGB;
    public float Load;
    /// <summary>Hottest DIMM. Only DIMMs with an SPD thermal sensor (most DDR5, few DDR4) report one.</summary>
    public float? Temperature;
}

/// <summary>A fan or pump. <see cref="Id"/> is LibreHardwareMonitor's stable sensor identifier.</summary>
public sealed class FanReading
{
    public string Id, Name;
    public float Rpm;
}

public sealed class Snapshot
{
    public DateTime Time;
    public CpuReading Cpu;
    public GpuReading Gpu;
    public RamReading Ram;
    public List<FanReading> Fans;
    /// <summary>Null when the frame counter couldn't start.</summary>
    public FpsReading Fps;
    public NetworkReading Net;
}

/// <summary>
/// Reads sensors through LibreHardwareMonitorLib. GPUs use the vendor's own driver API (NVAPI/NVML for NVIDIA,
/// ADL for AMD), so they work with only the graphics driver installed. CPU and DIMM temperatures need the
/// PawnIO kernel driver; without it those values stay empty. Fans come from the GPU driver, the motherboard's
/// Super I/O chip (needs PawnIO) and USB coolers such as AIO pumps.
/// </summary>
public sealed class SensorReader : IDisposable
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsMotherboardEnabled = true,
        IsControllerEnabled = true,
    };
    private readonly UpdateVisitor _visitor = new();
    private readonly object _lock = new();
    private bool _opened;

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>PawnIO registers as a kernel driver service named "PawnIO".</summary>
    public static bool IsPawnIOInstalled()
    {
        try
        {
            return ServiceController.GetDevices()
                .Any(d => string.Equals(d.ServiceName, "PawnIO", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public Snapshot Read()
    {
        lock (_lock)
        {
            EnsureOpen();
            _computer.Accept(_visitor);
            return new Snapshot { Time = DateTime.Now, Cpu = ReadCpu(), Gpu = ReadGpu(), Ram = ReadRam(), Fans = ReadFans() };
        }
    }

    /// <summary>Every sensor LibreHardwareMonitor sees, for troubleshooting on an unfamiliar PC.</summary>
    public string Dump()
    {
        lock (_lock)
        {
            EnsureOpen();
            _computer.Accept(_visitor);
            var sb = new StringBuilder();
            sb.AppendLine($"Admin: {IsElevated}, PawnIO: {IsPawnIOInstalled()}");
            foreach (var hw in _computer.Hardware)
                Dump(hw, sb, "");
            return sb.ToString();
        }
    }

    private static void Dump(IHardware hw, StringBuilder sb, string indent)
    {
        sb.AppendLine($"{indent}[{hw.HardwareType}] {hw.Name}");
        foreach (var s in hw.Sensors)
            sb.AppendLine($"{indent}  {s.SensorType,-12} {s.Name,-36} {s.Value?.ToString("0.##") ?? "-"}");
        foreach (var sub in hw.SubHardware)
            Dump(sub, sb, indent + "  ");
    }

    private void EnsureOpen()
    {
        if (_opened) return;
        _computer.Open();
        _opened = true;
    }

    private CpuReading ReadCpu()
    {
        var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        if (cpu is null) return new CpuReading();

        var sensors = AllSensors(cpu).ToList();
        var clocks = sensors.Where(s => s.SensorType == SensorType.Clock && s.Name.StartsWith("Core #") && s.Value > 0)
                            .Select(s => s.Value.Value).ToList();
        return new CpuReading
        {
            Name = cpu.Name,
            Load = Find(sensors, SensorType.Load, "CPU Total"),
            // AMD: Tctl/Tdie, Intel: CPU Package.
            Temperature = Find(sensors, SensorType.Temperature, "Core (Tctl/Tdie)", "CPU Package", "Core (Tctl)", "Core (Tdie)", "Core Max", "Core Average")
                          ?? sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value > 0).Max(s => s.Value),
            Power = Find(sensors, SensorType.Power, "Package", "CPU Package"),
            Clock = clocks.Count > 0 ? clocks.Max() : null,
        };
    }

    private GpuReading ReadGpu()
    {
        // Prefer NVIDIA/AMD over Intel, and among those the one with the most dedicated memory,
        // so a discrete card wins over a Ryzen iGPU (which also shows up as GpuAmd).
        var gpu = _computer.Hardware
            .Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            .OrderBy(h => h.HardwareType == HardwareType.GpuIntel ? 1 : 0)
            .ThenByDescending(h => VramTotal(AllSensors(h).ToList()) ?? 0)
            .FirstOrDefault();
        if (gpu is null) return new GpuReading();

        var sensors = AllSensors(gpu).ToList();
        return new GpuReading
        {
            Name = gpu.Name,
            Load = Find(sensors, SensorType.Load, "GPU Core", "D3D 3D"),
            Temperature = Find(sensors, SensorType.Temperature, "GPU Core", "GPU Edge"),
            HotSpot = Find(sensors, SensorType.Temperature, "GPU Hot Spot", "GPU Junction"),
            Power = Find(sensors, SensorType.Power, "GPU Package", "GPU Power", "GPU Core"),
            VramUsedMB = Find(sensors, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used"),
            VramTotalMB = VramTotal(sensors),
        };
    }

    private List<FanReading> ReadFans()
    {
        var fans = new List<FanReading>();
        foreach (var hw in _computer.Hardware)
        {
            foreach (var s in AllSensors(hw).Where(s => s.SensorType == SensorType.Fan && s.Value.HasValue))
            {
                var name = s.Name;
                if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                {
                    if (!name.StartsWith("GPU")) name = "GPU " + name;
                }
                else if (hw.HardwareType == HardwareType.Cooler)
                {
                    // e.g. "Kraken Z3 Pump", so an AIO's pump isn't confused with a motherboard header.
                    name = hw.Name.Replace("NZXT ", "").Replace("Corsair ", "") + " " + name;
                }
                fans.Add(new FanReading { Id = s.Identifier.ToString(), Name = name, Rpm = s.Value.Value });
            }
        }
        return fans;
    }

    private static float? VramTotal(List<ISensor> sensors) =>
        Find(sensors, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total");

    private RamReading ReadRam()
    {
        var reading = new RamReading();
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
        if (GlobalMemoryStatusEx(ref status))
        {
            const double gb = 1024d * 1024 * 1024;
            reading.TotalGB = status.ullTotalPhys / gb;
            reading.UsedGB = (status.ullTotalPhys - status.ullAvailPhys) / gb;
            reading.Load = status.dwMemoryLoad;
        }

        var dimmTemps = _computer.Hardware
            .Where(h => h.HardwareType == HardwareType.Memory)
            .SelectMany(AllSensors)
            .Where(s => s.SensorType == SensorType.Temperature && s.Value > 0)
            .Select(s => s.Value.Value)
            .ToList();
        reading.Temperature = dimmTemps.Count > 0 ? dimmTemps.Max() : null;
        return reading;
    }

    private static IEnumerable<ISensor> AllSensors(IHardware hw) =>
        hw.Sensors.Concat(hw.SubHardware.SelectMany(AllSensors));

    private static float? Find(List<ISensor> sensors, SensorType type, params string[] names)
    {
        foreach (var name in names)
        {
            // Temperatures read as 0 when the driver can't reach the sensor, which is never a real value.
            var match = sensors.FirstOrDefault(s => s.SensorType == type && s.Name == name && s.Value.HasValue
                                                    && (type != SensorType.Temperature || s.Value > 0));
            if (match is not null) return match.Value;
        }
        return null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_opened) _computer.Close();
            _opened = false;
        }
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware)
                sub.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
