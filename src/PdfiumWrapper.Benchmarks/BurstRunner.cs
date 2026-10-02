using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace PdfiumWrapper.Benchmarks;

/// <summary>
/// Deadline-oriented batch runner: enqueue N conversion jobs at once and measure completion of the
/// whole burst, including queueing and output writes.
/// </summary>
/// <remarks>
/// <code>
/// dotnet run -c Release --project src/PdfiumWrapper.Benchmarks -- burst \
///   --n 1000 --t 60 --callers 4 --mix tiff:50,png:30,jpeg:20 --dpi 200 \
///   --input src/PdfiumWrapper.Tests/Docs --out TestOutput/burst --report burst.json \
///   [--mode sync|async|async-starved] [--prewarm true|false] [--abandon-fraction 0.05]
///   [--diagnostics true|false] [--keep-output true|false]
/// </code>
/// Each run writes into a new <c>burst-...</c> directory under <c>--out</c> and removes only that
/// directory afterwards (or keeps it with <c>--keep-output true</c>). Whatever else is in
/// <c>--out</c> is left alone.
/// </remarks>
internal static class BurstRunner
{
    private sealed record Job(int Id, string Input, string Format, bool Abandon, bool ExpectFailure);

    private enum Outcome { Success, ExpectedFailure, UnexpectedFailure }

    private sealed record JobResult(
        int Id, Outcome Outcome, double QueueMs, double ProcessingMs, double EndToEndMs,
        int Pages, long Bytes, string? Error);

    private sealed class Options
    {
        public int N = 1000;
        public double T = 60;
        public int Callers = 1;
        public string Mix = "tiff:50,png:30,jpeg:20";
        public int Dpi = 200;
        public string Input = Path.Combine(AppContext.BaseDirectory, "Docs");
        public string Out = Path.Combine(Path.GetTempPath(), "PdfiumBurst");
        public string Report = "burst.json";
        public string Mode = "sync";
        public bool Prewarm = true;
        public double AbandonFraction;
        public bool Diagnostics;
        public bool KeepOutput;

        public (string format, int weight)[] ParsedMix = Array.Empty<(string, int)>();

        /// <summary>The directory this run writes into: a fresh child of <see cref="Out"/>.</summary>
        public string RunDirectory = "";
    }

    public static int Run(string[] args)
    {
        Options options;
        try
        {
            options = Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }

        // Must happen before the first wrapper type is touched.
        BurstDiagnostics.Configure(options.Diagnostics);

        var inputs = Directory.Exists(options.Input)
            ? Directory.GetFiles(options.Input, "*.pdf", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
        if (inputs.Length == 0)
        {
            Console.Error.WriteLine($"no .pdf files under '{options.Input}'");
            return 3;
        }

        var mix = options.ParsedMix;
        int mixTotalWeight = mix.Sum(m => m.weight);
        int mixStride = InterleavingStride(mixTotalWeight);
        var jobs = BuildJobs(options, inputs, mix, mixTotalWeight, mixStride);

        // --out may point at a directory that already holds other data. Never delete it: every run
        // writes into its own new child directory and cleans up only that.
        bool createdParent = !Directory.Exists(options.Out);
        options.RunDirectory = Path.Combine(options.Out, $"burst-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(options.RunDirectory);

        bool starved = options.Mode == "async-starved";
        if (starved)
        {
            int pc = Environment.ProcessorCount;
            ThreadPool.GetMinThreads(out _, out int minIo);
            ThreadPool.GetMaxThreads(out _, out int maxIo);
            ThreadPool.SetMinThreads(pc, minIo);
            ThreadPool.SetMaxThreads(pc, maxIo);
        }

        if (options.Prewarm)
            Prewarm(inputs[0]);

        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var results = new JobResult?[jobs.Length];
        var queue = new ConcurrentQueue<Job>(jobs);
        var queueDepthSamples = new List<int>();
        var workingSetSamples = new List<long>();
        var heartbeatMs = new List<double>();
        using var stop = new CancellationTokenSource();

        BurstDiagnostics.Begin();
        long t0 = Stopwatch.GetTimestamp();

        var sampler = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                queueDepthSamples.Add(queue.Count);
                process.Refresh();
                workingSetSamples.Add(process.WorkingSet64);
                stop.Token.WaitHandle.WaitOne(250);
            }
        }) { IsBackground = true, Name = "burst-sampler" };
        sampler.Start();

        Thread? heartbeat = null;
        if (starved)
        {
            // Dedicated thread: measures how long a trivial work item waits for a pool thread.
            heartbeat = new Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    long start = Stopwatch.GetTimestamp();
                    Task.Run(static () => { }).Wait();
                    heartbeatMs.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    stop.Token.WaitHandle.WaitOne(50);
                }
            }) { IsBackground = true, Name = "burst-heartbeat" };
            heartbeat.Start();
        }

        if (options.Mode == "sync")
        {
            var threads = Enumerable.Range(0, options.Callers).Select(i => new Thread(() =>
            {
                while (queue.TryDequeue(out var job))
                    results[job.Id] = RunJob(job, options, t0);
            }) { IsBackground = true, Name = $"burst-caller-{i}" }).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());
        }
        else
        {
            var tasks = Enumerable.Range(0, options.Callers).Select(_ => Task.Run(async () =>
            {
                while (queue.TryDequeue(out var job))
                    results[job.Id] = await RunJobAsync(job, options, t0).ConfigureAwait(false);
            })).ToArray();
            Task.WaitAll(tasks);
        }

        double totalSeconds = Stopwatch.GetElapsedTime(t0).TotalSeconds;
        var diagnostics = BurstDiagnostics.End(options.Callers);
        stop.Cancel();
        sampler.Join();
        heartbeat?.Join();

        process.Refresh();
        double cpuSeconds = (process.TotalProcessorTime - cpuBefore).TotalSeconds;
        var done = results.Select(r => r!).ToArray();
        var succeeded = done.Where(r => r.Outcome == Outcome.Success).ToArray();
        int pages = succeeded.Sum(r => r.Pages);
        double lastCompleted = done.Max(r => r.EndToEndMs) / 1000.0;

        var report = new Dictionary<string, object?>
        {
            ["n"] = options.N,
            ["t"] = options.T,
            ["callers"] = options.Callers,
            ["mode"] = options.Mode,
            ["mix"] = options.Mix,
            ["mixTotalWeight"] = mixTotalWeight,
            ["mixStride"] = mixStride,
            ["jobsByFormat"] = jobs.GroupBy(j => j.Format).ToDictionary(g => g.Key, g => (object?)g.Count()),
            ["outputDirectory"] = options.KeepOutput ? options.RunDirectory : null,
            ["dpi"] = options.Dpi,
            ["prewarm"] = options.Prewarm,
            ["abandonFraction"] = options.AbandonFraction,
            ["requiredDocsPerSec"] = Round(options.N / options.T),
            ["totalSeconds"] = Round(totalSeconds),
            ["lastJobCompletedAt"] = Round(lastCompleted),
            ["metDeadline"] = lastCompleted <= options.T,
            ["jobs"] = new Dictionary<string, object?>
            {
                ["success"] = succeeded.Length,
                ["expectedFailure"] = done.Count(r => r.Outcome == Outcome.ExpectedFailure),
                ["unexpectedFailure"] = done.Count(r => r.Outcome == Outcome.UnexpectedFailure),
            },
            ["latencyMs"] = new Dictionary<string, object?>
            {
                ["queue"] = Percentiles(done.Select(r => r.QueueMs)),
                ["processing"] = Percentiles(done.Select(r => r.ProcessingMs)),
                ["endToEnd"] = Percentiles(done.Select(r => r.EndToEndMs)),
            },
            ["pages"] = pages,
            ["pagesPerSec"] = Round(pages / totalSeconds),
            ["docsPerSec"] = Round(succeeded.Length / totalSeconds),
            ["peakWorkingSetMB"] = Round(process.PeakWorkingSet64 / (1024.0 * 1024.0)),
            ["steadyWorkingSetMB"] = Round(SteadyWorkingSet(workingSetSamples) / (1024.0 * 1024.0)),
            ["cpuSeconds"] = Round(cpuSeconds),
            ["outputBytes"] = succeeded.Sum(r => r.Bytes),
            ["queueDepthSamples"] = queueDepthSamples,
            ["gateWaitShare"] = diagnostics.GateWaitShare,
            ["gateHoldShare"] = diagnostics.GateHoldShare,
            ["heartbeatP99Ms"] = starved && heartbeatMs.Count > 0 ? Round(Percentile(heartbeatMs.OrderBy(x => x).ToArray(), 0.99)) : null,
            ["heartbeatP50Ms"] = starved && heartbeatMs.Count > 0 ? Round(Percentile(heartbeatMs.OrderBy(x => x).ToArray(), 0.50)) : null,
            ["pendingDrainedPerOp"] = diagnostics.PendingDrainedPerOp,
            ["firstErrors"] = done.Where(r => r.Outcome == Outcome.UnexpectedFailure).Select(r => r.Error).Take(5).ToArray(),
        };

        var reportDirectory = Path.GetDirectoryName(Path.GetFullPath(options.Report));
        if (!string.IsNullOrEmpty(reportDirectory))
            Directory.CreateDirectory(reportDirectory);
        File.WriteAllText(options.Report, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        if (!options.KeepOutput)
        {
            Directory.Delete(options.RunDirectory, recursive: true);

            // Remove the parent only if this run created it and nothing else has appeared in it.
            if (createdParent && !Directory.EnumerateFileSystemEntries(options.Out).Any())
                Directory.Delete(options.Out);
        }

        Console.WriteLine(
            $"burst n={options.N} callers={options.Callers} mode={options.Mode}: {totalSeconds:F2}s, " +
            $"{succeeded.Length / totalSeconds:F1} docs/s, {pages / totalSeconds:F1} pages/s, " +
            $"deadline {(lastCompleted <= options.T ? "met" : "missed")} (t={options.T}s), report: {options.Report}");

        return done.Any(r => r.Outcome == Outcome.UnexpectedFailure) ? 2 : 0;
    }

    private static void Prewarm(string input)
    {
        using var doc = new PdfDocument(input);
        using var page = doc.GetPage(0);
        _ = page.RenderToBytes((int)Math.Round(page.Width), (int)Math.Round(page.Height));
    }

    private static JobResult RunJob(Job job, Options options, long t0)
    {
        long start = Stopwatch.GetTimestamp();
        int pages = 0;
        long bytes = 0;
        try
        {
            if (job.Abandon)
                Abandon(job.Input);

            using var doc = new PdfDocument(job.Input);
            pages = doc.PageCount;
            string prefix = Path.Combine(options.RunDirectory, $"job-{job.Id:D6}");

            switch (job.Format)
            {
                case "tiff":
                    doc.SaveAsTiff(prefix + ".tiff", options.Dpi, TiffColorMode.Bilevel);
                    bytes = new FileInfo(prefix + ".tiff").Length;
                    break;
                case "png":
                {
                    int p = 0;
                    foreach (var data in doc.StreamImageBytes(ImageFormat.Png, 100, options.Dpi))
                    {
                        File.WriteAllBytes($"{prefix}-p{++p:D3}.png", data);
                        bytes += data.Length;
                    }
                    break;
                }
                case "jpeg":
                {
                    int p = 0;
                    foreach (var data in doc.StreamImageBytes(ImageFormat.Jpeg, 85, options.Dpi))
                    {
                        File.WriteAllBytes($"{prefix}-p{++p:D3}.jpg", data);
                        bytes += data.Length;
                    }
                    break;
                }
            }

            return Finish(job, Outcome.Success, start, t0, pages, bytes, null);
        }
        catch (Exception ex)
        {
            return Finish(job, job.ExpectFailure ? Outcome.ExpectedFailure : Outcome.UnexpectedFailure,
                start, t0, pages, bytes, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<JobResult> RunJobAsync(Job job, Options options, long t0)
    {
        long start = Stopwatch.GetTimestamp();
        int pages = 0;
        long bytes = 0;
        try
        {
            if (job.Abandon)
                Abandon(job.Input);

            using var doc = new PdfDocument(job.Input);
            pages = doc.PageCount;
            string prefix = Path.Combine(options.RunDirectory, $"job-{job.Id:D6}");

            switch (job.Format)
            {
                case "tiff":
                    await doc.SaveAsTiffAsync(prefix + ".tiff", options.Dpi, TiffColorMode.Bilevel).ConfigureAwait(false);
                    bytes = new FileInfo(prefix + ".tiff").Length;
                    break;
                case "png":
                {
                    int p = 0;
                    await foreach (var data in doc.StreamImageBytesAsync(ImageFormat.Png, 100, options.Dpi).ConfigureAwait(false))
                    {
                        await File.WriteAllBytesAsync($"{prefix}-p{++p:D3}.png", data).ConfigureAwait(false);
                        bytes += data.Length;
                    }
                    break;
                }
                case "jpeg":
                {
                    int p = 0;
                    await foreach (var data in doc.StreamImageBytesAsync(ImageFormat.Jpeg, 85, options.Dpi).ConfigureAwait(false))
                    {
                        await File.WriteAllBytesAsync($"{prefix}-p{++p:D3}.jpg", data).ConfigureAwait(false);
                        bytes += data.Length;
                    }
                    break;
                }
            }

            return Finish(job, Outcome.Success, start, t0, pages, bytes, null);
        }
        catch (Exception ex)
        {
            return Finish(job, job.ExpectFailure ? Outcome.ExpectedFailure : Outcome.UnexpectedFailure,
                start, t0, pages, bytes, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Opens a document and a page and drops both without disposing, leaving cleanup to finalizers.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Abandon(string input)
    {
        var doc = new PdfDocument(input);
        _ = doc.GetPage(0);
    }

    private static JobResult Finish(Job job, Outcome outcome, long start, long t0, int pages, long bytes, string? error)
    {
        long end = Stopwatch.GetTimestamp();
        return new JobResult(job.Id, outcome,
            QueueMs: Stopwatch.GetElapsedTime(t0, start).TotalMilliseconds,
            ProcessingMs: Stopwatch.GetElapsedTime(start, end).TotalMilliseconds,
            EndToEndMs: Stopwatch.GetElapsedTime(t0, end).TotalMilliseconds,
            pages, bytes, error);
    }

    private static Job[] BuildJobs(Options options, string[] inputs, (string format, int weight)[] mix, int totalWeight, int stride)
    {
        int abandonEvery = options.AbandonFraction > 0 ? Math.Max(1, (int)Math.Round(1 / options.AbandonFraction)) : 0;
        var jobs = new Job[options.N];

        for (int i = 0; i < jobs.Length; i++)
        {
            // The stride shares no factor with the total weight, so every block of totalWeight
            // consecutive jobs visits each slot exactly once: exact proportions, interleaved.
            int slot = (int)((long)i * stride % totalWeight);
            string format = mix[^1].format;
            foreach (var (name, weight) in mix)
            {
                if (slot < weight) { format = name; break; }
                slot -= weight;
            }

            string input = inputs[i % inputs.Length];
            bool expectFailure = Path.GetFileName(input).StartsWith("bad-", StringComparison.OrdinalIgnoreCase)
                                 || input.Contains($"{Path.DirectorySeparatorChar}malformed{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
            jobs[i] = new Job(i, input, format, abandonEvery > 0 && i % abandonEvery == 0, expectFailure);
        }

        return jobs;
    }

    /// <summary>Largest accepted sum of mix weights.</summary>
    /// <remarks>
    /// Proportions are exact over each block of total-weight jobs. A total far beyond any real
    /// batch size would leave the first jobs in the first format, so such mixes are rejected
    /// rather than run with a misleading workload. Use ratios: <c>png:1,jpeg:1</c>, not
    /// <c>png:1000000,jpeg:1000000</c>.
    /// </remarks>
    internal const int MaxTotalWeight = 10_000;

    /// <summary>
    /// The step used to walk the mix slots. It is coprime with <paramref name="totalWeight"/>, so
    /// every block of total-weight consecutive jobs visits each slot exactly once, and it is close
    /// to 0.37 of the total so that a shorter run still spreads across the formats. For the
    /// default total of 100 this is 37. A step of +1 or -1, which would run each format in one
    /// block, is avoided when the total allows another choice.
    /// </summary>
    /// <returns>A positive stride coprime with <paramref name="totalWeight"/>.</returns>
    internal static int InterleavingStride(int totalWeight)
    {
        if (totalWeight is < 1 or > MaxTotalWeight)
            throw new ArgumentOutOfRangeException(nameof(totalWeight), $"The mix total must be between 1 and {MaxTotalWeight}.");

        // long arithmetic: the bound below must not wrap.
        long start = Math.Max(1L, (long)Math.Round(0.37 * totalWeight));
        long firstCoprime = 0;
        for (long stride = start; stride < start + totalWeight; stride++)
        {
            if (Gcd(stride, totalWeight) != 1)
                continue;

            if (firstCoprime == 0)
                firstCoprime = stride;

            long step = stride % totalWeight;
            if (step != 1 && step != totalWeight - 1)
                return (int)stride;
        }

        // Totals such as 1, 2, 3, 4 and 6 have no coprime step other than +1 and -1.
        // totalWeight consecutive integers always include one coprime with it, so this is positive.
        return (int)firstCoprime;
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0)
            (a, b) = (b, a % b);
        return a;
    }

    private static (string format, int weight)[] ParseMix(string mix)
    {
        var parts = mix.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split(':'))
            .Select(p => p.Length == 2 && int.TryParse(p[1], out int w) && w >= 0 && p[0] is "tiff" or "png" or "jpeg"
                ? (p[0], w)
                : throw new ArgumentException($"--mix entries must look like tiff:50,png:30,jpeg:20; got '{mix}'"))
            .Where(p => p.Item2 > 0)
            .ToArray();
        if (parts.Length == 0)
            throw new ArgumentException("--mix must contain at least one format with a positive weight");

        long total = parts.Sum(p => (long)p.Item2);
        if (total > MaxTotalWeight)
            throw new ArgumentException(
                $"--mix weights sum to {total}; the largest supported total is {MaxTotalWeight}. Use ratios, for example png:1,jpeg:1.");
        return parts;
    }
    private static Options Parse(string[] args)
    {
        var options = new Options();
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length)
                throw new ArgumentException($"missing value for {args[i]}");
            string value = args[i + 1];
            switch (args[i])
            {
                case "--n": options.N = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--t": options.T = double.Parse(value, CultureInfo.InvariantCulture); break;
                case "--callers": options.Callers = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--mix": options.Mix = value; break;
                case "--dpi": options.Dpi = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--input": options.Input = value; break;
                case "--out": options.Out = value; break;
                case "--report": options.Report = value; break;
                case "--mode": options.Mode = value; break;
                case "--prewarm": options.Prewarm = bool.Parse(value); break;
                case "--abandon-fraction": options.AbandonFraction = double.Parse(value, CultureInfo.InvariantCulture); break;
                case "--diagnostics": options.Diagnostics = bool.Parse(value); break;
                case "--keep-output": options.KeepOutput = bool.Parse(value); break;
                default: throw new ArgumentException($"unknown option {args[i]}");
            }
        }

        if (options.N <= 0 || options.T <= 0 || options.Callers <= 0 || options.Dpi <= 0)
            throw new ArgumentException("--n, --t, --callers and --dpi must be positive");
        if (options.Mode is not ("sync" or "async" or "async-starved"))
            throw new ArgumentException("--mode must be sync, async or async-starved");
        if (options.AbandonFraction is < 0 or > 1)
            throw new ArgumentException("--abandon-fraction must be between 0 and 1");
        options.ParsedMix = ParseMix(options.Mix);
        return options;
    }

    private static Dictionary<string, object?> Percentiles(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return new Dictionary<string, object?>
        {
            ["p50"] = Round(Percentile(sorted, 0.50)),
            ["p95"] = Round(Percentile(sorted, 0.95)),
            ["p99"] = Round(Percentile(sorted, 0.99)),
            ["max"] = Round(sorted.Length == 0 ? 0 : sorted[^1]),
        };
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0)
            return 0;
        int rank = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    /// <summary>Median working set over the second half of the run.</summary>
    private static double SteadyWorkingSet(List<long> samples)
    {
        if (samples.Count == 0)
            return 0;
        var secondHalf = samples.Skip(samples.Count / 2).OrderBy(s => s).ToArray();
        return secondHalf[secondHalf.Length / 2];
    }

    private static double Round(double value) => Math.Round(value, 3);
}
