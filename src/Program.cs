using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace PttyPcTestMonitor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbedded;

        // PttyPcTestMonitor.exe --dump <file>: write every sensor to a text file and exit (troubleshooting).
        if (args.Length == 2 && args[0] == "--dump")
        {
            Dump(args[1]);
            return;
        }
        // --render <dir>: draw the UI with sample data to PNG files.
        if (args.Length == 2 && args[0] == "--render")
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            RenderPreview(args[1]);
            return;
        }
        // --fps-test <file>: log what the frame counter sees for 10 seconds.
        if (args.Length == 2 && args[0] == "--fps-test")
        {
            FpsTest(args[1]);
            return;
        }

        using var single = new Mutex(true, "PttyPcTestMonitor.SingleInstance", out var first);
        if (!first) return;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Run();
    }

    // Kept separate so the JIT doesn't load LibreHardwareMonitorLib before AssemblyResolve is hooked.
    private static void Run() => Application.Run(new MainForm());

    private static void RenderPreview(string dir) => MainForm.Preview(dir);

    private static void FpsTest(string path)
    {
        using var frames = new FrameMonitor();
        var log = new System.Text.StringBuilder();
        log.AppendLine("start: " + (frames.Start() ? "ok" : frames.Error));
        for (var i = 0; i < 10; i++)
        {
            Thread.Sleep(1000);
            var r = frames.Read();
            log.AppendLine($"[{i}] fg={r.ProcessName}({r.ProcessId}) fps={r.Fps:0.0} ft={r.FrameTimeMs:0.00} low1={r.OnePercentLow:0.0}");
            log.AppendLine(frames.Summary());
        }
        File.WriteAllText(path, log.ToString());
    }

    private static void Dump(string path)
    {
        using var reader = new SensorReader();
        reader.Read();
        Thread.Sleep(1000); // load sensors need two samples
        var s = reader.Read();
        File.WriteAllText(path,
            $"CPU {s.Cpu.Name}: load={s.Cpu.Load} temp={s.Cpu.Temperature} power={s.Cpu.Power} clock={s.Cpu.Clock}\r\n" +
            $"GPU {s.Gpu.Name}: load={s.Gpu.Load} temp={s.Gpu.Temperature} hotspot={s.Gpu.HotSpot} power={s.Gpu.Power} vram={s.Gpu.VramUsedMB}/{s.Gpu.VramTotalMB}\r\n" +
            $"RAM {s.Ram.UsedGB:0.0}/{s.Ram.TotalGB:0.0} GB load={s.Ram.Load} temp={s.Ram.Temperature}\r\n" +
            string.Concat(s.Fans.Select(f => $"FAN {f.Name}: {f.Rpm:0} RPM ({f.Id})\r\n")) + "\r\n" +
            reader.Dump());
    }

    /// <summary>Loads dependency DLLs embedded by the EmbedDependencies build target.</summary>
    private static Assembly ResolveEmbedded(object sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name + ".dll";
        using var stream = typeof(Program).Assembly.GetManifestResourceStream("deps." + name);
        if (stream is null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Assembly.Load(ms.ToArray());
    }
}
