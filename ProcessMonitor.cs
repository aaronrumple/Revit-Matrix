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
/// <para>
/// The first read of those counters can take Windows a minute or more. So the UI thread never
/// waits on a sample: <see cref="Watch"/> and <see cref="Dispose"/> only flag what to do, and
/// the sampling thread does it (closing the counters itself if a sample was still running).
/// </para>
/// </summary>
public sealed class ProcessMonitor : IDisposable
{
    public const int IntervalMs = 1000;
    private const double MB = 1024 * 1024;

    private readonly object _lock = new();     // guards _pid and _resetPending only; held briefly
    private readonly Timer _timer;
    private int? _pid;
    private bool _resetPending;
    private volatile bool _disposed;
    private int _busy;                         // 1 while a sample (or the clean-up) owns the sampling state
    private int _cleaned;

    // Sampling state: touched only by whoever holds _busy.
    private readonly GpuCounters _gpu = new();
    private Process? _process;
    private TimeSpan _lastCpu;
    private DateTime _lastWall;
    private bool _haveCpu;

    /// <summary>Raised on a background thread after each sample.</summary>
    public event Action<PerfSample>? Sampled;

    public ProcessMonitor() => _timer = new Timer(_ => Tick(), null, IntervalMs, IntervalMs);

    /// <summary>Start watching a process, or stop (null). Returns at once; the next sample switches over.</summary>
    public void Watch(int? pid)
    {
        lock (_lock)
        {
            if (pid == _pid) return;
            _pid = pid;
            _resetPending = true;
        }
    }

    private void Tick()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;   // a sample is still running
        try
        {
            if (_disposed) return;
            int? pid;
            bool reset;
            lock (_lock)
            {
                pid = _pid;
                reset = _resetPending;
                _resetPending = false;
            }
            if (reset) ResetSampling();
            if (pid is not int p) return;

            var sample = Sample(p);
            bool current;
            lock (_lock) current = _pid == p && !_resetPending;
            if (sample is PerfSample s && current && !_disposed) Sampled?.Invoke(s);
        }
        catch
        {
            // never let a sampling problem take the app down
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
            // Disposed while this sample ran: close the counters here, off the UI thread.
            if (_disposed && Interlocked.CompareExchange(ref _busy, 1, 0) == 0) CleanUp();
        }
    }

    private void ResetSampling()
    {
        _process?.Dispose();
        _process = null;
        _haveCpu = false;
        _gpu.Reset();
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

    /// <summary>Stops sampling without waiting for a sample in progress.</summary>
    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        Sampled = null;
        // If no sample is running, clean up now; otherwise the running sample does it when it finishes.
        if (Interlocked.CompareExchange(ref _busy, 1, 0) == 0) CleanUp();
    }

    private void CleanUp()
    {
        if (Interlocked.Exchange(ref _cleaned, 1) == 1) return;
        try
        {
            _process?.Dispose();
            _process = null;
            _gpu.Dispose();
        }
        catch
        {
            // closing; nothing more to do
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
