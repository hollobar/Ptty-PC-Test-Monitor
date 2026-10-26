using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace PttyPcTestMonitor;

public sealed class FpsReading
{
    public int ProcessId;
    public string ProcessName;
    public float? Fps, FrameTimeMs, OnePercentLow;
}

/// <summary>
/// Measures the frame rate of the foreground app by counting present calls through ETW, the same
/// method PresentMon uses. Nothing is injected into the game, so it is safe with anti-cheat.
/// Sources, in order of preference per process:
///   DXGI Present (D3D10/11/12, and Vulkan/OpenGL drivers that present through DXGI), D3D9 Present,
///   and DxgKrnl present for anything else.
/// Needs administrator rights to start the trace session.
/// </summary>
public sealed class FrameMonitor : IDisposable
{
    private const string SessionName = "PttyPcTestMonitor-Frames";

    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private static readonly Guid DxgKrnlProvider = new("802EC45A-1E99-4B83-9920-87C98277BA9D");

    private const ushort DxgiPresentStart = 42, DxgiPresentMpoStart = 55, D3D9PresentStart = 1, DxgKrnlPresent = 184;

    /// <summary>Frame times are histogrammed in 0.1 ms buckets up to 1 s for the 1% low.</summary>
    private const int Buckets = 10000;

    private enum Source { Api, Kernel }

    /// <summary>Present timestamps (QPC ticks) of one process from one source.</summary>
    private sealed class Track
    {
        public readonly Queue<long> Recent = new();
        public long Last;
        public readonly int[] Histogram = new int[Buckets + 1];
        public long HistogramCount;
    }

    private sealed class ProcessFrames
    {
        public readonly Track Api = new(), Kernel = new();
        public long LastEvent;
    }

    private readonly object _lock = new();
    private readonly Dictionary<int, ProcessFrames> _processes = new();
    private readonly EventRecordCallback _callback;
    private readonly int _ownPid = Process.GetCurrentProcess().Id;
    private readonly double _qpcPerMs = Stopwatch.Frequency / 1000.0;
    private ulong _session, _trace;
    private IntPtr _properties, _logfile, _loggerName;
    private Thread _thread;
    private int _target;
    private string _targetName;

    public string Error { get; private set; }

    public FrameMonitor()
    {
        _callback = OnEvent;
    }

    public bool Start()
    {
        try
        {
            StopSession(); // a session left behind by a crash would block StartTrace
            StartSession();
            OpenAndProcess();
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Dispose();
            return false;
        }
    }

    /// <summary>Clears the 1% low statistics (e.g. when the user resets the max values).</summary>
    public void ResetStats()
    {
        lock (_lock)
        {
            foreach (var p in _processes.Values)
            {
                Array.Clear(p.Api.Histogram, 0, p.Api.Histogram.Length);
                Array.Clear(p.Kernel.Histogram, 0, p.Kernel.Histogram.Length);
                p.Api.HistogramCount = p.Kernel.HistogramCount = 0;
            }
        }
    }

    /// <summary>Frame rate of the foreground app over the last second. Our own window never becomes the target.</summary>
    public FpsReading Read()
    {
        var fg = ForegroundProcessId();
        if (fg != 0 && fg != _ownPid && fg != _target)
        {
            _target = fg;
            try { _targetName = Process.GetProcessById(fg).ProcessName; } catch { _targetName = null; }
            lock (_lock)
            {
                if (_processes.TryGetValue(fg, out var p))
                {
                    Array.Clear(p.Api.Histogram, 0, p.Api.Histogram.Length);
                    Array.Clear(p.Kernel.Histogram, 0, p.Kernel.Histogram.Length);
                    p.Api.HistogramCount = p.Kernel.HistogramCount = 0;
                }
            }
        }

        var reading = new FpsReading { ProcessId = _target, ProcessName = _targetName };
        var now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            // Forget processes that stopped presenting.
            foreach (var pid in _processes.Where(kv => now - kv.Value.LastEvent > Stopwatch.Frequency * 10).Select(kv => kv.Key).ToList())
                _processes.Remove(pid);

            if (!_processes.TryGetValue(_target, out var frames)) return reading;
            var track = frames.Api.Recent.Count >= 2 ? frames.Api : frames.Kernel;
            Trim(track, now);
            if (track.Recent.Count < 2) return reading;

            var spanMs = (track.Recent.Last() - track.Recent.Peek()) / _qpcPerMs;
            if (spanMs <= 0) return reading;
            var frameTime = spanMs / (track.Recent.Count - 1);
            reading.FrameTimeMs = (float)frameTime;
            reading.Fps = (float)(1000.0 / frameTime);
            reading.OnePercentLow = OnePercentLow(track);
        }
        return reading;
    }

    /// <summary>Presents per second of every process seen in the last second, for troubleshooting.</summary>
    public string Summary()
    {
        var now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            return string.Join(Environment.NewLine, _processes.Select(kv =>
            {
                Trim(kv.Value.Api, now);
                Trim(kv.Value.Kernel, now);
                string name;
                try { name = Process.GetProcessById(kv.Key).ProcessName; } catch { name = "?"; }
                return $"  pid {kv.Key} {name}: api={kv.Value.Api.Recent.Count}/s kernel={kv.Value.Kernel.Recent.Count}/s";
            }));
        }
    }

    /// <summary>Frame rate at the 99th-percentile frame time since the last reset.</summary>
    private static float? OnePercentLow(Track track)
    {
        if (track.HistogramCount < 100) return null;
        var threshold = track.HistogramCount * 0.99;
        long seen = 0;
        for (var i = 0; i <= Buckets; i++)
        {
            seen += track.Histogram[i];
            if (seen >= threshold) return (float)(1000.0 / Math.Max(0.1, i / 10.0));
        }
        return null;
    }

    private void Trim(Track track, long now)
    {
        // Keep one second of presents; a stalled app drops to empty and reads as no FPS.
        var cutoff = now - Stopwatch.Frequency;
        while (track.Recent.Count > 0 && track.Recent.Peek() < cutoff) track.Recent.Dequeue();
    }

    private void OnEvent(IntPtr record)
    {
        var id = (ushort)Marshal.ReadInt16(record, 40);
        Source source;
        if (id == DxgiPresentStart || id == DxgiPresentMpoStart || id == D3D9PresentStart)
        {
            var provider = ReadGuid(record, 24);
            if (provider == DxgiProvider) source = Source.Api;
            else if (provider == D3D9Provider && id == D3D9PresentStart) source = Source.Api;
            else return;
        }
        else if (id == DxgKrnlPresent && ReadGuid(record, 24) == DxgKrnlProvider)
        {
            source = Source.Kernel;
        }
        else
        {
            return;
        }

        var pid = Marshal.ReadInt32(record, 12);
        var time = Marshal.ReadInt64(record, 16);
        lock (_lock)
        {
            if (!_processes.TryGetValue(pid, out var frames)) _processes[pid] = frames = new ProcessFrames();
            frames.LastEvent = time;
            var track = source == Source.Api ? frames.Api : frames.Kernel;

            if (track.Last != 0 && pid == _target)
            {
                var ms = (time - track.Last) / _qpcPerMs;
                if (ms > 0)
                {
                    track.Histogram[Math.Min(Buckets, (int)(ms * 10))]++;
                    track.HistogramCount++;
                }
            }
            track.Last = time;
            track.Recent.Enqueue(time);
            if (track.Recent.Count > 2000) track.Recent.Dequeue();
        }
    }

    private static Guid ReadGuid(IntPtr p, int offset)
    {
        var bytes = new byte[16];
        Marshal.Copy(p + offset, bytes, 0, 16);
        return new Guid(bytes);
    }

    private static int ForegroundProcessId()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    // ---------------------------------------------------------------- ETW session

    private static int PropertiesSize => Marshal.SizeOf(typeof(EVENT_TRACE_PROPERTIES));

    private IntPtr AllocProperties()
    {
        var size = PropertiesSize + 1024 * 2;
        var ptr = Marshal.AllocHGlobal(size);
        var zero = new byte[size];
        Marshal.Copy(zero, 0, ptr, size);
        var props = new EVENT_TRACE_PROPERTIES
        {
            Wnode = new WNODE_HEADER { BufferSize = (uint)size, Flags = 0x00020000 /* WNODE_FLAG_TRACED_GUID */, ClientContext = 1 /* QPC */ },
            LogFileMode = 0x00000100, // EVENT_TRACE_REAL_TIME_MODE
            FlushTimer = 1,
            BufferSize = 64,
            MinimumBuffers = 4,
            MaximumBuffers = 16,
            LoggerNameOffset = (uint)PropertiesSize,
        };
        Marshal.StructureToPtr(props, ptr, false);
        return ptr;
    }

    private void StartSession()
    {
        _properties = AllocProperties();
        var err = StartTraceW(out _session, SessionName, _properties);
        if (err != 0) throw new InvalidOperationException($"StartTrace 실패 ({err})");

        Enable(DxgiProvider, 4, 0xFFFFFFFFFFFFFFFF);
        Enable(D3D9Provider, 4, 0xFFFFFFFFFFFFFFFF);
        Enable(DxgKrnlProvider, 4, 0x1 /* Base */);
    }

    private void Enable(Guid provider, byte level, ulong keywords)
    {
        var err = EnableTraceEx2(_session, ref provider, 1 /* EVENT_CONTROL_CODE_ENABLE_PROVIDER */, level, keywords, 0, 0, IntPtr.Zero);
        if (err != 0) throw new InvalidOperationException($"EnableTraceEx2 실패 ({err})");
    }

    private void OpenAndProcess()
    {
        // EVENT_TRACE_LOGFILEW is 448 bytes on x64; only the fields below are needed.
        const int size = 448;
        _logfile = Marshal.AllocHGlobal(size);
        Marshal.Copy(new byte[size], 0, _logfile, size);
        _loggerName = Marshal.StringToHGlobalUni(SessionName);
        Marshal.WriteIntPtr(_logfile, 8, _loggerName);                                   // LoggerName
        // PROCESS_TRACE_MODE_REAL_TIME | EVENT_RECORD | RAW_TIMESTAMP (keep QPC ticks, comparable with Stopwatch)
        Marshal.WriteInt32(_logfile, 28, 0x00000100 | 0x10000000 | 0x00001000);
        Marshal.WriteIntPtr(_logfile, 424, Marshal.GetFunctionPointerForDelegate(_callback)); // EventRecordCallback

        _trace = OpenTraceW(_logfile);
        if (_trace == ulong.MaxValue) throw new InvalidOperationException($"OpenTrace 실패 ({Marshal.GetLastWin32Error()})");

        _thread = new Thread(() =>
        {
            var handles = new[] { _trace };
            ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);
        }) { IsBackground = true, Name = "ETW frames" };
        _thread.Start();
    }

    private static void StopSession()
    {
        var size = PropertiesSize + 1024 * 2;
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, ptr, size);
            var props = new EVENT_TRACE_PROPERTIES
            {
                Wnode = new WNODE_HEADER { BufferSize = (uint)size },
                LoggerNameOffset = (uint)PropertiesSize,
            };
            Marshal.StructureToPtr(props, ptr, false);
            ControlTraceW(0, SessionName, ptr, 1 /* EVENT_TRACE_CONTROL_STOP */);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public void Dispose()
    {
        if (_session != 0)
        {
            StopSession();
            _session = 0;
        }
        if (_trace != 0 && _trace != ulong.MaxValue)
        {
            CloseTrace(_trace);
            _trace = 0;
        }
        var stopped = _thread?.Join(2000) ?? true;
        _thread = null;
        if (_properties != IntPtr.Zero) { Marshal.FreeHGlobal(_properties); _properties = IntPtr.Zero; }
        // The logfile struct may still be referenced if ProcessTrace didn't return; leak it rather than risk a crash.
        if (stopped && _logfile != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_logfile);
            Marshal.FreeHGlobal(_loggerName);
            _logfile = _loggerName = IntPtr.Zero;
        }
    }

    // ---------------------------------------------------------------- interop

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EventRecordCallback(IntPtr eventRecord);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNODE_HEADER
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_TRACE_PROPERTIES
    {
        public WNODE_HEADER Wnode;
        public uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize, LogFileMode, FlushTimer, EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset, LoggerNameOffset;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint StartTraceW(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ControlTraceW(ulong sessionHandle, string sessionName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll")]
    private static extern uint EnableTraceEx2(ulong traceHandle, ref Guid providerId, uint controlCode, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern ulong OpenTraceW(IntPtr logfile);

    [DllImport("advapi32.dll")]
    private static extern uint ProcessTrace(ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll")]
    private static extern uint CloseTrace(ulong traceHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
