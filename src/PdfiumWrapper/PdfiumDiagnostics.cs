using System.Collections.Concurrent;
using System.Diagnostics;

namespace PdfiumWrapper;

/// <summary>Native call sites instrumented when diagnostics are enabled.</summary>
internal enum NativeOp { LoadDocument = 0, LoadPage = 1, Render = 2, Text = 3, Save = 4, Import = 5, FormFill = 6, Close = 7 }

/// <summary>One recorded interval. Ticks are <see cref="Stopwatch.GetTimestamp"/> values.</summary>
internal readonly record struct DiagnosticsEvent(long StartTicks, long EndTicks, int ThreadId, int Kind)
{
    public const int GateWait = 0;
    public const int GateHold = 1;
    public const int NativeBase = 100;

    public bool IsNative => Kind >= NativeBase;
    public NativeOp NativeOp => (NativeOp)(Kind - NativeBase);
    public TimeSpan Duration => Stopwatch.GetElapsedTime(StartTicks, EndTicks);
    public bool Overlaps(DiagnosticsEvent other) => StartTicks < other.EndTicks && other.StartTicks < EndTicks;
}

internal sealed record DiagnosticsSnapshot(
    long GateEntries,
    long GateAcquisitions,
    long GateWaits,
    long MaxActiveHolders,
    long MaxActiveNative,
    long InitCount,
    long Enqueued,
    long Drained,
    long WaitTicks,
    long HoldTicks,
    long EventsDropped,
    int DistinctHolderThreads,
    DiagnosticsEvent[] Events);

/// <summary>
/// Evidence for tests and instrumented benchmark runs: gate wait/hold intervals and native call
/// intervals around the hot call sites. Off unless the <c>PdfiumWrapper.Diagnostics</c>
/// <see cref="AppContext"/> switch is <c>true</c> before the first wrapper type is used.
/// </summary>
internal static class PdfiumDiagnostics
{
    public static readonly bool Enabled =
        AppContext.TryGetSwitch("PdfiumWrapper.Diagnostics", out var on) && on;

    private const int GateEntries = 0;        // every Enter(), including reentrant ones
    private const int GateWaits = 1;          // acquisitions that had to wait
    private const int ActiveHolders = 2;
    private const int MaxActiveHolders = 3;
    private const int ActiveNative = 4;
    private const int MaxActiveNative = 5;
    private const int InitCount = 6;
    private const int Enqueued = 7;
    private const int Drained = 8;
    private const int WaitTicks = 9;
    private const int HoldTicks = 10;
    private const int HoldStart = 11;         // written only by the gate holder
    private const int EventsDropped = 12;
    private const int GateAcquisitions = 13;  // outermost entries only

    private const int MaxEvents = 4_000_000;

    // Shared across assembly copies, same as the gate itself.
    private static readonly long[] s_counters =
        SharedState.GetOrCreate("PdfiumWrapper.DiagCounters", static () => new long[16]);
    private static readonly ConcurrentQueue<(long, long, int, int)> s_events =
        SharedState.GetOrCreate("PdfiumWrapper.DiagEvents", static () => new ConcurrentQueue<(long, long, int, int)>());
    private static readonly ConcurrentQueue<(int, long)> s_releases =
        SharedState.GetOrCreate("PdfiumWrapper.DiagReleases", static () => new ConcurrentQueue<(int, long)>());

    public readonly struct NativeScope : IDisposable
    {
        private readonly long _start;
        private readonly int _kind;

        internal NativeScope(long start, int kind)
        {
            _start = start;
            _kind = kind;
        }

        public void Dispose()
        {
            if (_kind == 0)
                return;

            Interlocked.Decrement(ref s_counters[ActiveNative]);
            Record(_start, Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId, _kind);
        }
    }

    /// <summary>Brackets one native call. A no-op value when diagnostics are off.</summary>
    public static NativeScope NativeInterval(NativeOp op)
    {
        if (!Enabled)
            return default;

        RaiseMax(MaxActiveNative, Interlocked.Increment(ref s_counters[ActiveNative]));
        return new NativeScope(Stopwatch.GetTimestamp(), DiagnosticsEvent.NativeBase + (int)op);
    }

    public static void RecordEntry() => Interlocked.Increment(ref s_counters[GateEntries]);

    public static void RecordGateAcquired(int threadId, long waitStart, bool waited)
    {
        long now = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref s_counters[GateAcquisitions]);
        if (waited)
        {
            Interlocked.Increment(ref s_counters[GateWaits]);
            Interlocked.Add(ref s_counters[WaitTicks], now - waitStart);
            Record(waitStart, now, threadId, DiagnosticsEvent.GateWait);
        }

        RaiseMax(MaxActiveHolders, Interlocked.Increment(ref s_counters[ActiveHolders]));
        Volatile.Write(ref s_counters[HoldStart], now);
    }

    public static void RecordGateReleased(int threadId)
    {
        long now = Stopwatch.GetTimestamp();
        long start = Volatile.Read(ref s_counters[HoldStart]);
        Interlocked.Add(ref s_counters[HoldTicks], now - start);
        Record(start, now, threadId, DiagnosticsEvent.GateHold);
        Interlocked.Decrement(ref s_counters[ActiveHolders]);
    }

    public static void RecordInit() => Interlocked.Increment(ref s_counters[InitCount]);

    public static void RecordEnqueued(NativeHandleKind kind) => Interlocked.Increment(ref s_counters[Enqueued]);

    public static void RecordDrained(NativeHandleKind kind, nint handle)
    {
        Interlocked.Increment(ref s_counters[Drained]);
        s_releases.Enqueue(((int)kind, handle));
    }

    /// <summary>Cheap read of the entry counter, for tests that check a single call entered the gate.</summary>
    public static long GateEntryCount => Interlocked.Read(ref s_counters[GateEntries]);

    /// <summary>Deferred releases in the order the drain performed them: (kind, handle).</summary>
    public static (NativeHandleKind Kind, nint Handle)[] DrainedReleases()
        => s_releases.ToArray().Select(r => ((NativeHandleKind)r.Item1, (nint)r.Item2)).ToArray();

    public static DiagnosticsSnapshot Snapshot()
    {
        var events = s_events.ToArray()
            .Select(e => new DiagnosticsEvent(e.Item1, e.Item2, e.Item3, e.Item4))
            .ToArray();

        return new DiagnosticsSnapshot(
            GateEntries: Interlocked.Read(ref s_counters[GateEntries]),
            GateAcquisitions: Interlocked.Read(ref s_counters[GateAcquisitions]),
            GateWaits: Interlocked.Read(ref s_counters[GateWaits]),
            MaxActiveHolders: Interlocked.Read(ref s_counters[MaxActiveHolders]),
            MaxActiveNative: Interlocked.Read(ref s_counters[MaxActiveNative]),
            InitCount: Interlocked.Read(ref s_counters[InitCount]),
            Enqueued: Interlocked.Read(ref s_counters[Enqueued]),
            Drained: Interlocked.Read(ref s_counters[Drained]),
            WaitTicks: Interlocked.Read(ref s_counters[WaitTicks]),
            HoldTicks: Interlocked.Read(ref s_counters[HoldTicks]),
            EventsDropped: Interlocked.Read(ref s_counters[EventsDropped]),
            DistinctHolderThreads: events.Where(e => e.Kind == DiagnosticsEvent.GateHold).Select(e => e.ThreadId).Distinct().Count(),
            Events: events);
    }

    /// <summary>
    /// Zero the counters and clear the events. Live state (who holds the gate now, how many native
    /// calls are in flight, the init count) is kept so a reset during activity stays consistent.
    /// </summary>
    public static void Reset()
    {
        foreach (int index in new[] { GateEntries, GateWaits, Enqueued, Drained, WaitTicks, HoldTicks, EventsDropped, GateAcquisitions })
            Interlocked.Exchange(ref s_counters[index], 0);

        Interlocked.Exchange(ref s_counters[MaxActiveHolders], Interlocked.Read(ref s_counters[ActiveHolders]));
        Interlocked.Exchange(ref s_counters[MaxActiveNative], Interlocked.Read(ref s_counters[ActiveNative]));
        s_events.Clear();
        s_releases.Clear();
    }

    private static void Record(long start, long end, int threadId, int kind)
    {
        if (s_events.Count >= MaxEvents)
        {
            Interlocked.Increment(ref s_counters[EventsDropped]);
            return;
        }

        s_events.Enqueue((start, end, threadId, kind));
    }

    private static void RaiseMax(int index, long value)
    {
        long current;
        while (value > (current = Interlocked.Read(ref s_counters[index])))
        {
            if (Interlocked.CompareExchange(ref s_counters[index], value, current) == current)
                break;
        }
    }
}
