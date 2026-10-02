using System.Diagnostics;

namespace PdfiumWrapper.Benchmarks;

/// <summary>
/// Gate measurements for the burst runner's instrumented run (<c>--diagnostics true</c>).
/// Timing runs leave diagnostics off; the fields are null then.
/// </summary>
internal static class BurstDiagnostics
{
    /// <param name="GateWaitShare">Average fraction of a caller's time spent waiting for the gate.</param>
    /// <param name="GateHoldShare">Fraction of wall time during which the gate was held by some caller.</param>
    /// <param name="PendingDrainedPerOp">Deferred releases performed per gate acquisition.</param>
    public readonly record struct Result(double? GateWaitShare, double? GateHoldShare, double? PendingDrainedPerOp);

    private static bool s_enabled;
    private static long s_start;

    /// <summary>Must be called before the first wrapper type is touched: the switch is read once.</summary>
    public static void Configure(bool enabled)
    {
        s_enabled = enabled;
        AppContext.SetSwitch("PdfiumWrapper.Diagnostics", enabled);
    }

    public static void Begin()
    {
        if (!s_enabled)
            return;

        PdfiumDiagnostics.Reset();
        s_start = Stopwatch.GetTimestamp();
    }

    public static Result End(int callers)
    {
        if (!s_enabled)
            return new Result(null, null, null);

        long wallTicks = Math.Max(1, Stopwatch.GetTimestamp() - s_start);
        var snapshot = PdfiumDiagnostics.Snapshot();

        return new Result(
            GateWaitShare: Math.Round((double)snapshot.WaitTicks / wallTicks / callers, 4),
            GateHoldShare: Math.Round((double)snapshot.HoldTicks / wallTicks, 4),
            PendingDrainedPerOp: snapshot.GateAcquisitions == 0
                ? 0
                : Math.Round((double)snapshot.Drained / snapshot.GateAcquisitions, 6));
    }
}
