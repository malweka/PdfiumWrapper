using System.Text;

namespace PdfiumWrapper.Qualification;

/// <summary>
/// Growth of one process between its first and its last idle reading. The first idle reading
/// comes after warm-up (burst) or after the first full-load period (soak).
/// </summary>
internal sealed record Growth(
    int Pid, string Role, int IdleReadings, int LoadReadings, bool Judged,
    long BaselineWorkingSet, long FinalWorkingSet, double WorkingSetGrowth,
    long BaselinePrivateBytes, long FinalPrivateBytes, double PrivateBytesGrowth,
    int BaselineHandles, int FinalHandles, double HandleGrowth,
    long PeakWorkingSet, IReadOnlyList<long> IdlePrivateBytes)
{
    public const double Limit = 0.10;

    // Below these a change is allocator or handle-cache movement, not growth worth a look.
    public const long MinBytes = 8L * 1024 * 1024;
    public const int MinHandles = 20;

    public bool Exceeds => Judged && (
        (WorkingSetGrowth > Limit && FinalWorkingSet - BaselineWorkingSet > MinBytes) ||
        (PrivateBytesGrowth > Limit && FinalPrivateBytes - BaselinePrivateBytes > MinBytes) ||
        (HandleGrowth > Limit && FinalHandles - BaselineHandles > MinHandles));

    /// <summary>
    /// Judged when <paramref name="judge"/> is set (not for a smoke run, too short to be warm) and
    /// the process has at least two idle readings.
    /// </summary>
    public static Growth Of(int pid, string role, IReadOnlyList<Sample> samples, bool judge)
    {
        var idle = samples.Where(s => s.Idle).ToList();
        int load = samples.Count - idle.Count;
        long peak = samples.Count == 0 ? 0 : samples.Max(s => s.WorkingSet);
        if (idle.Count == 0)
            return new Growth(pid, role, 0, load, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, peak, []);

        var first = idle[0];
        var last = idle[^1];
        return new Growth(pid, role, idle.Count, load, judge && idle.Count >= 2,
            first.WorkingSet, last.WorkingSet, Ratio(first.WorkingSet, last.WorkingSet),
            first.PrivateBytes, last.PrivateBytes, Ratio(first.PrivateBytes, last.PrivateBytes),
            first.Handles, last.Handles, Ratio(first.Handles, last.Handles),
            peak, idle.Select(s => s.PrivateBytes).ToArray());
    }

    private static double Ratio(long baseline, long final) => baseline <= 0 ? 0 : (double)final / baseline - 1;
}

internal sealed record QualificationReport(
    string Mode,
    string Rid,
    string Framework,
    bool SelfContained,
    int MinWorkers,
    int MaxWorkers,
    int Seed,
    DateTimeOffset Started,
    double Seconds,
    long JobsSubmitted,
    long JobsCompleted,
    long BatchDocuments,
    IReadOnlyDictionary<string, long> StatusCounts,
    long Unexpected,
    long Mismatches,
    long LeftoversAfterFailure,
    long Retried,
    double TotalMsP50, double TotalMsP95, double TotalMsP99,
    double ProcessingMsP50, double ProcessingMsP95, double ProcessingMsP99,
    object PoolStatistics,
    IReadOnlyDictionary<string, long> PoolEvents,
    int DistinctWorkers,
    int WorkersAliveAfterDispose,
    int FilesLeftInOutput,
    int FilesLeftInPoolTemp,
    IReadOnlyList<Growth> Growth,
    IReadOnlyCollection<string> Problems,
    IReadOnlyCollection<string> WorkerEvents,
    IReadOnlyList<Sample> Samples)
{
    public bool CorrectnessPassed =>
        Unexpected == 0 && Mismatches == 0 && LeftoversAfterFailure == 0
        && WorkersAliveAfterDispose == 0 && FilesLeftInOutput == 0 && FilesLeftInPoolTemp == 0
        && JobsCompleted == JobsSubmitted + BatchDocuments;

    public bool GrowthPassed => !Growth.Any(g => g.Exceeds);

    /// <summary>0 pass, 1 a correctness or cleanup check failed, 2 only growth above the limit.</summary>
    public int ExitCode => !CorrectnessPassed ? 1 : !GrowthPassed ? 2 : 0;

    public static double Percentile(IReadOnlyCollection<double> values, double p)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.Order().ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    public string ToMarkdown()
    {
        string verdict = ExitCode switch { 0 => "PASS", 1 => "FAIL (correctness or cleanup)", _ => "INVESTIGATE (idle growth above the limit)" };
        var sb = new StringBuilder();
        sb.AppendLine($"## Pool qualification: {Mode} on {Rid} — {verdict}");
        sb.AppendLine();
        sb.AppendLine($"{(SelfContained ? "Self-contained" : "Framework-dependent")} {Framework}, MinWorkers {MinWorkers}, MaxWorkers {MaxWorkers}, seed {Seed}, {Seconds / 60:F1} min, started {Started:u}.");
        sb.AppendLine();
        sb.AppendLine("| Check | Result |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Jobs completed / submitted (+ batch) | {JobsCompleted:N0} / {JobsSubmitted:N0} (+ {BatchDocuments:N0}) |");
        sb.AppendLine($"| Unexpected status or exception | {Unexpected} |");
        sb.AppendLine($"| Output differs from in-process reference | {Mismatches} |");
        sb.AppendLine($"| Output left by a failed job | {LeftoversAfterFailure} |");
        sb.AppendLine($"| Jobs retried (attempts > 1) | {Retried} |");
        sb.AppendLine($"| Workers alive 5 s after dispose | {WorkersAliveAfterDispose} of {DistinctWorkers} started |");
        sb.AppendLine($"| Files left in output root / pool temp | {FilesLeftInOutput} / {FilesLeftInPoolTemp} |");
        sb.AppendLine($"| Job total ms p50 / p95 / p99 | {TotalMsP50:F0} / {TotalMsP95:F0} / {TotalMsP99:F0} |");
        sb.AppendLine($"| Job processing ms p50 / p95 / p99 | {ProcessingMsP50:F0} / {ProcessingMsP95:F0} / {ProcessingMsP99:F0} |");
        sb.AppendLine();
        sb.AppendLine("Statuses: " + string.Join(", ", StatusCounts.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value:N0}")));
        sb.AppendLine();
        sb.AppendLine("Pool events: " + string.Join(", ", PoolEvents.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value:N0}")));
        sb.AppendLine();
        sb.AppendLine("### Growth between the first and last idle reading (limit 10% and 8 MB / 20 handles)");
        sb.AppendLine();
        sb.AppendLine("| Process | Idle / load readings | Peak MB | Working set MB | Private MB | Handles | Idle private MB, in order | Judged |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var g in Growth.OrderByDescending(g => g.IdleReadings).ThenByDescending(g => g.LoadReadings))
        {
            sb.AppendLine($"| {g.Role} {g.Pid} | {g.IdleReadings} / {g.LoadReadings} | {Mb(g.PeakWorkingSet)} | {Mb(g.BaselineWorkingSet)} → {Mb(g.FinalWorkingSet)} ({g.WorkingSetGrowth:+0.0%;-0.0%}) " +
                $"| {Mb(g.BaselinePrivateBytes)} → {Mb(g.FinalPrivateBytes)} ({g.PrivateBytesGrowth:+0.0%;-0.0%}) " +
                $"| {g.BaselineHandles} → {g.FinalHandles} ({g.HandleGrowth:+0.0%;-0.0%}) | {string.Join(", ", g.IdlePrivateBytes.Select(Mb))} | {(g.Judged ? g.Exceeds ? "**over limit**" : "ok" : "not judged")} |");
        }

        if (Problems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Problems (first 200)");
            sb.AppendLine();
            foreach (var p in Problems)
                sb.AppendLine($"- {p}");
        }

        return sb.ToString();
    }

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("F0");
}
