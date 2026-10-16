using System.Text;

namespace PdfiumWrapper.Qualification;

/// <summary>
/// Whether a process's idle memory and handles still rise in the second half of the run. A leak
/// rises with every job; an allocator's high-water mark (glibc keeps freed memory, the Windows heap
/// and the GC keep committed pages) rises in steps early on and then levels off. Measured on the
/// idle readings after the first (warm-up), with readings that no job separates counted once: the
/// end of the run (median of the last three, so one high reading does not decide) against the
/// highest reading of the first half. A plateau passes; a leak of 0.5 KB per job fails a
/// 38,000-job soak. Memory is private bytes on Windows (committed memory) and the working set
/// elsewhere: on Linux private bytes is address space, 8 MB per thread stack included, and on
/// macOS .NET reports it as 0.
/// </summary>
internal sealed record Growth(
    int Pid, string Role, string Metric, int IdleReadings, int LoadReadings, bool Judged,
    long HighWater, long Final, double MemoryGrowth,
    int HandleHighWater, int FinalHandles, double HandleGrowth,
    long PeakWorkingSet, IReadOnlyList<long> IdleMemory)
{
    public const double Limit = 0.10;

    // Below these a change is allocator or handle-cache movement, not growth worth a look.
    public const long MinBytes = 8L * 1024 * 1024;
    public const int MinHandles = 20;

    /// <summary>Readings after the warm-up needed to judge: three in each half.</summary>
    public const int MinReadings = 6;

    public bool Exceeds => Judged && (
        (MemoryGrowth > Limit && Final - HighWater > MinBytes) ||
        (HandleGrowth > Limit && FinalHandles - HandleHighWater > MinHandles));

    /// <summary>Judged when <paramref name="judge"/> is set (not for a smoke run) and there are enough readings.</summary>
    public static Growth Of(int pid, string role, IReadOnlyList<Sample> samples, bool judge, bool privateBytes)
    {
        int load = samples.Count(s => !s.Idle);
        long peak = samples.Count == 0 ? 0 : samples.Max(s => s.WorkingSet);
        string metric = privateBytes ? "private" : "working set";
        Func<Sample, long> memory = privateBytes ? s => s.PrivateBytes : s => s.WorkingSet;

        var idle = new List<Sample>();
        foreach (var s in samples.Where(s => s.Idle))
        {
            if (idle.Count > 0 && idle[^1].JobsCompleted == s.JobsCompleted)
                idle[^1] = s;
            else
                idle.Add(s);
        }

        var window = idle.Skip(1).ToList();
        if (window.Count < 2)
            return new Growth(pid, role, metric, idle.Count, load, false, 0, 0, 0, 0, 0, 0, peak, idle.Select(memory).ToArray());

        var firstHalf = window.Take(window.Count / 2).ToList();
        var end = window.TakeLast(Math.Min(3, window.Count - firstHalf.Count)).ToList();
        long highWater = firstHalf.Max(memory);
        long final = Median(end.Select(memory));
        int handleHighWater = firstHalf.Max(s => s.Handles);
        int finalHandles = (int)Median(end.Select(s => (long)s.Handles));
        return new Growth(pid, role, metric, idle.Count, load, judge && window.Count >= MinReadings,
            highWater, final, Ratio(highWater, final),
            handleHighWater, finalHandles, Ratio(handleHighWater, finalHandles),
            peak, idle.Select(memory).ToArray());
    }

    private static long Median(IEnumerable<long> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
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
        sb.AppendLine($"### Growth in the second half of the run (limit 10% and 8 MB / 20 handles; at least {PdfiumWrapper.Qualification.Growth.MinReadings} idle readings after the first)");
        sb.AppendLine();
        sb.AppendLine("End of the run (median of the last three idle readings) against the highest idle reading of the first half. Memory is private bytes on Windows and the working set elsewhere.");
        sb.AppendLine();
        sb.AppendLine("| Process | Idle / load readings | Peak working set MB | Memory | First-half high → end MB | Handles | Idle memory MB, in order | Judged |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var g in Growth.OrderByDescending(g => g.IdleReadings).ThenByDescending(g => g.LoadReadings))
        {
            string judged = g.Judged ? g.Exceeds ? "**over limit**" : "ok" : "not judged";
            sb.AppendLine($"| {g.Role} {g.Pid} | {g.IdleReadings} / {g.LoadReadings} | {Mb(g.PeakWorkingSet)} | {g.Metric} " +
                $"| {Mb(g.HighWater)} → {Mb(g.Final)} ({g.MemoryGrowth:+0.0%;-0.0%}) " +
                $"| {g.HandleHighWater} → {g.FinalHandles} ({g.HandleGrowth:+0.0%;-0.0%}) | {string.Join(", ", g.IdleMemory.Select(Mb))} | {judged} |");
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
