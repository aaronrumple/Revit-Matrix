using System.ComponentModel;
using System.Diagnostics;

namespace RevitJournalViewer;

/// <summary>One reading of a Revit process. Null values could not be read.</summary>
public readonly record struct PerfSample(
    int Pid,
    double? CpuPercent,      // % of all logical processors, like Task Manager
    double? PrivateMB,       // memory Revit has committed (private bytes)
    double? WorkingSetMB,    // memory currently in RAM
    double? GpuPercent,      // busiest GPU engine, like Task Manager's GPU column
    double? GpuMemoryMB,     // dedicated video memory
    string? Note);           // why something is missing, if it is

/// <summary>
/// Samples a process's CPU, memory and GPU use once a second on a background thread.
/// GPU figures come from the Windows "GPU Engine" / "GPU Process Memory" performance
/// counters (Windows 10 1709+ with a WDDM 2.x driver), the same source Task Manager uses.
/// </summary>
public sealed class ProcessMonitor : IDisposable
{
    public const int IntervalMs = 1000;
    private const double MB = 1024 * 1024;

    private readonly object _lock = new();
    private readonly Timer _timer;
    private readonly GpuCounters _gpu = new();
    private int? _pid;
    private Process? _process;
    private TimeSpan _lastCpu;
    private DateTime _lastWall;
    private bool _haveCpu;
    private int _busy;
    private bool _disposed;

    /// <summary>Raised on a background thread after each sample.</summary>
    public event Action<PerfSample>? Sampled;

    public ProcessMonitor() => _timer = new Timer(_ => Tick(), null, IntervalMs, IntervalMs);

    /// <summary>Start watching a process, or stop (null).</summary>
    public void Watch(int? pid)
    {
        lock (_lock)
        {
            if (pid == _pid) return;
            _pid = pid;
            _process?.Dispose();
            _process = null;
            _haveCpu = false;
            _gpu.Reset();
        }
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;   // previous sample still running
        try
        {
            PerfSample? sample;
            lock (_lock)
            {
                if (_disposed || _pid is not int pid) return;
                sample = Sample(pid);
            }
            if (sample is PerfSample s) Sampled?.Invoke(s);
        }
        catch
        {
            // never let a sampling problem take the app down
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private PerfSample? Sample(int pid)
    {
        try
        {
            _process ??= Process.GetProcessById(pid);
            _process.Refresh();
        }
        catch (ArgumentException) { return null; }          // process is gone
        catch (InvalidOperationException) { return null; }

        string? note = null;
        double? cpu = null, priv = null, ws = null;

        try
        {
            priv = _process.PrivateMemorySize64 / MB;
            ws = _process.WorkingSet64 / MB;
        }
        catch (InvalidOperationException) { return null; }  // exited between calls

        try
        {
            var now = DateTime.UtcNow;
            var total = _process.TotalProcessorTime;
            if (_haveCpu)
            {
                double wall = (now - _lastWall).TotalMilliseconds;
                if (wall > 0)
                    cpu = Math.Clamp((total - _lastCpu).TotalMilliseconds / wall / Environment.ProcessorCount * 100, 0, 100);
            }
            _lastCpu = total;
            _lastWall = now;
            _haveCpu = true;
        }
        catch (Win32Exception)
        {
            note = "Revit is running as administrator, so its CPU use can't be read. Run The Revit Matrix as administrator to see it.";
        }
        catch (InvalidOperationException) { return null; }

        var (gpu, gpuMem, gpuNote) = _gpu.Read(pid);
        return new PerfSample(pid, cpu, priv, ws, gpu, gpuMem, note ?? gpuNote);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _timer.Dispose();
            _process?.Dispose();
            _gpu.Dispose();
        }
    }

    /// <summary>Per-process GPU counters. Instance names look like "pid_1234_luid_..._engtype_3D".</summary>
    private sealed class GpuCounters : IDisposable
    {
        private const string EngineCategory = "GPU Engine";
        private const string MemoryCategory = "GPU Process Memory";

        private readonly Dictionary<string, PerformanceCounter> _engines = new();
        private readonly Dictionary<string, PerformanceCounter> _memory = new();
        private DateTime _nextScan = DateTime.MinValue;
        private bool _unavailable;

        public void Reset()
        {
            DisposeAll();
            _nextScan = DateTime.MinValue;
        }

        public (double? Gpu, double? MemoryMB, string? Note) Read(int pid)
        {
            if (_unavailable)
                return (null, null, "GPU use isn't available on this PC. It needs Windows 10 version 1709 or later and a WDDM 2 graphics driver.");
            try
            {
                // Engines come and go as Revit uses the GPU, so look for new ones every few seconds.
                if (DateTime.UtcNow >= _nextScan)
                {
                    Rescan(pid);
                    _nextScan = DateTime.UtcNow.AddSeconds(5);
                }

                double gpu = 0;
                foreach (var name in _engines.Keys.ToList())
                {
                    var v = Next(_engines, name);
                    if (v is double d) gpu = Math.Max(gpu, d);
                }

                double mem = 0;
                foreach (var name in _memory.Keys.ToList())
                {
                    var v = Next(_memory, name);
                    if (v is double d) mem += d / MB;
                }

                return (Math.Clamp(gpu, 0, 100), mem, null);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
            {
                _unavailable = true;
                DisposeAll();
                return (null, null, "GPU use isn't available on this PC. It needs Windows 10 version 1709 or later and a WDDM 2 graphics driver.");
            }
        }

        private static double? Next(Dictionary<string, PerformanceCounter> set, string name)
        {
            try
            {
                return set[name].NextValue();
            }
            catch (InvalidOperationException)
            {
                // that engine instance went away
                set[name].Dispose();
                set.Remove(name);
                return null;
            }
        }

        private void Rescan(int pid)
        {
            string prefix = $"pid_{pid}_";
            Sync(_engines, EngineCategory, "Utilization Percentage", prefix);
            Sync(_memory, MemoryCategory, "Dedicated Usage", prefix);
        }

        private static void Sync(Dictionary<string, PerformanceCounter> set, string category, string counter, string prefix)
        {
            var names = new PerformanceCounterCategory(category).GetInstanceNames()
                .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var gone in set.Keys.Where(k => !names.Contains(k)).ToList())
            {
                set[gone].Dispose();
                set.Remove(gone);
            }
            foreach (var name in names.Where(n => !set.ContainsKey(n)))
            {
                var pc = new PerformanceCounter(category, counter, name, readOnly: true);
                pc.NextValue();   // the first reading of a rate counter is always 0
                set[name] = pc;
            }
        }

        private void DisposeAll()
        {
            foreach (var c in _engines.Values.Concat(_memory.Values)) c.Dispose();
            _engines.Clear();
            _memory.Clear();
        }

        public void Dispose() => DisposeAll();
    }
}
