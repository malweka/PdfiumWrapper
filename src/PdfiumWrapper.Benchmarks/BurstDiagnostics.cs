namespace PdfiumWrapper.Benchmarks;

/// <summary>
/// Gate measurements for the burst runner's instrumented run.
/// The fields are null until the native gate and its diagnostics exist.
/// </summary>
internal static class BurstDiagnostics
{
    public readonly record struct Result(double? GateWaitShare, double? GateHoldShare, double? PendingDrainedPerOp);

    public static void Configure(bool enabled) { }

    public static void Begin() { }

    public static Result End() => new(null, null, null);
}
