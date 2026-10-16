using System.Collections.Concurrent;
using System.Diagnostics;
using PdfiumWrapper.Processing;

namespace PdfiumWrapper.Qualification;

/// <summary>
/// One reading of a process. <see cref="Idle"/>: taken with no job in flight, after the queue
/// drained; only these are compared for growth, since a reading under load mostly reflects which
/// documents the worker happens to hold.
/// </summary>
internal sealed record Sample(
    int Pid, long JobsCompleted, double Seconds, bool Idle,
    long WorkingSet, long PrivateBytes, int Handles, long ManagedHeap, int Threads);

/// <summary>
/// Tracks the pool's worker processes (from events and job results) and samples each one, and this
/// process, every 1,000 completed jobs and every 60 seconds (under load), and when asked (idle).
/// </summary>
internal sealed class Sampler : IDisposable
{
    public const long JobInterval = 1_000;
    private static readonly TimeSpan s_timeInterval = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<int, byte> _live = new();
    // Start time of each worker, so a PID that Windows hands to another process is not taken for it.
    private readonly ConcurrentDictionary<int, DateTime> _seen = new();
    private readonly ConcurrentDictionary<PdfPoolEventKind, long> _events = new();
    private readonly ConcurrentQueue<string> _workerEvents = new();
    private readonly List<Sample> _samples = [];
    private readonly Lock _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Func<long> _completed;
    private readonly Timer _timer;
    private long _nextJobMark = JobInterval;

    public Sampler(Func<long> completed)
    {
        _completed = completed;
        _timer = new Timer(_ => Take(idle: false), null, s_timeInterval, s_timeInterval);
    }

    public int SeenWorkers => _seen.Count;

    /// <summary>Workers seen during the run that are still running.</summary>
    public int AliveWorkers()
    {
        int alive = 0;
        foreach (int pid in _seen.Keys)
        {
            using var process = Open(pid);
            if (process is { HasExited: false })
                alive++;
        }

        return alive;
    }

    public IReadOnlyDictionary<PdfPoolEventKind, long> EventCounts => _events;

    /// <summary>Worker start, stop, crash, retirement and scaling events, time-outs and retries, in order, at most 500.</summary>
    public IReadOnlyCollection<string> WorkerEvents => _workerEvents;

    public List<Sample> Samples
    {
        get { lock (_gate) return [.. _samples]; }
    }

    public void OnEvent(object? sender, PdfPoolEvent e)
    {
        _events.AddOrUpdate(e.Kind, 1, (_, n) => n + 1);
        switch (e.Kind)
        {
            case PdfPoolEventKind.WorkerStopped:
            case PdfPoolEventKind.WorkerCrashed:
                _live.TryRemove(e.WorkerPid, out _);
                break;
            default:
                Track(e.WorkerPid);
                break;
        }

        if (e.Kind is not (PdfPoolEventKind.JobDispatched or PdfPoolEventKind.JobCompleted or PdfPoolEventKind.JobFailed
                or PdfPoolEventKind.JobCancelled or PdfPoolEventKind.QueueFull or PdfPoolEventKind.WorkerMessage)
            && _workerEvents.Count < 500)
            _workerEvents.Enqueue($"{_clock.Elapsed.TotalSeconds,8:F1}s {e}");
    }

    /// <summary>Records a worker seen in a job result, in case its start event came before the subscription.</summary>
    public void Track(int pid)
    {
        if (pid <= 0)
            return;
        if (!_seen.ContainsKey(pid))
            _seen.TryAdd(pid, StartTime(pid));
        _live.TryAdd(pid, 0);
    }

    public void OnJobCompleted(long completed)
    {
        long mark = Volatile.Read(ref _nextJobMark);
        if (completed >= mark && Interlocked.CompareExchange(ref _nextJobMark, mark + JobInterval, mark) == mark)
            _ = Task.Run(() => Take(idle: false));
    }

    public void Take(bool idle)
    {
        lock (_gate)
        {
            long completed = _completed();
            double seconds = _clock.Elapsed.TotalSeconds;
            foreach (int pid in _live.Keys)
            {
                try
                {
                    using var process = Open(pid);
                    if (process is null || process.HasExited)
                    {
                        _live.TryRemove(pid, out _);
                        continue;
                    }

                    process.Refresh();
                    _samples.Add(new Sample(pid, completed, seconds, idle,
                        process.WorkingSet64, process.PrivateMemorySize64, process.HandleCount, 0, process.Threads.Count));
                }
                catch (Exception)
                {
                    // Exited between the listing and the reading.
                    _live.TryRemove(pid, out _);
                }
            }

            using var self = Process.GetCurrentProcess();
            _samples.Add(new Sample(Environment.ProcessId, completed, seconds, idle,
                self.WorkingSet64, self.PrivateMemorySize64, self.HandleCount, GC.GetTotalMemory(false), self.Threads.Count));
        }
    }

    public void Dispose() => _timer.Dispose();

    /// <summary>The worker with this PID, or null when it has exited, even if another process now has the PID.</summary>
    private Process? Open(int pid)
    {
        if (!_seen.TryGetValue(pid, out var started) || started == default)
            return null;
        try
        {
            var process = Process.GetProcessById(pid);
            // Within a second: on Linux the start time is derived from the boot time and can shift slightly.
            if ((process.StartTime - started).Duration() < TimeSpan.FromSeconds(1))
                return process;
            process.Dispose();
        }
        catch (Exception)
        {
            // Gone, or not ours to read.
        }

        return null;
    }

    private static DateTime StartTime(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime;
        }
        catch (Exception)
        {
            return default;
        }
    }
}
