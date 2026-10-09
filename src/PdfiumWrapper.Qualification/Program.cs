using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using PdfiumWrapper.Processing;
using PdfiumWrapper.Qualification;

// Workers are copies of this executable (WorkerPath = null): in a worker process TryRun runs the
// worker loop and returns true.
if (PdfWorkerHost.TryRun())
    return 0;

const string usage = """
    PdfiumWrapper.Processing qualification (see ai/plans/plan-pool-qualification.md)

      burst  [--jobs 10000] [--batch 500]   submit as fast as backpressure allows, then a batch
      soak   [--minutes 30]                 cycles of 4 min full load, an idle reading, 90 s at one job
      smoke  [--jobs 1000] [--batch 100]    a short burst

      common: [--min 2] [--max 8] [--seed 1] [--work <dir>] [--report <file.json>]

    Exit code: 0 pass, 1 a correctness or cleanup check failed, 2 only idle growth above the limit, 3 usage.
    """;

if (args.Length == 0 || args[0] is not ("burst" or "soak" or "smoke"))
{
    Console.Error.WriteLine(usage);
    return 3;
}

string mode = args[0];
var named = new Dictionary<string, string>(StringComparer.Ordinal);
for (int i = 1; i < args.Length; i += 2)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
    {
        Console.Error.WriteLine(usage);
        return 3;
    }

    named[args[i][2..]] = args[i + 1];
}

int Int(string name, int fallback) => named.TryGetValue(name, out var v) ? int.Parse(v) : fallback;

int jobs = Int("jobs", mode == "smoke" ? 1_000 : 10_000);
int batch = mode == "soak" ? 0 : Int("batch", mode == "smoke" ? 100 : 500);
var duration = TimeSpan.FromMinutes(Int("minutes", 30));
int minWorkers = Int("min", 2);
int maxWorkers = Int("max", 8);
int seed = Int("seed", 1);
string work = Path.GetFullPath(named.GetValueOrDefault("work") ?? Path.Combine(Path.GetTempPath(), $"pdfq-{mode}-{Environment.ProcessId}"));
string reportPath = Path.GetFullPath(named.GetValueOrDefault("report") ?? Path.Combine(work, $"report-{mode}.json"));
string outputRoot = Path.Combine(work, "out");
string poolTemp = Path.Combine(work, "pool-temp");
Directory.CreateDirectory(outputRoot);
Directory.CreateDirectory(poolTemp);

// Full load: the pool is kept saturated with this many jobs in flight (its queue holds 256, so
// submissions also wait on backpressure). Burst: the pool is read idle after a warm-up and then
// every 2,500 jobs (the queue drains for each reading). Soak: cycles of 240 s at full load, an
// idle reading once the queue drains, then 90 s at one job, longer than the 60 s idle timeout, so
// the pool shrinks to MinWorkers every cycle. Growth is judged on the idle readings only.
const int fullLoad = 512;
var loadPeriod = TimeSpan.FromSeconds(240);
var tricklePeriod = TimeSpan.FromSeconds(90);
int warmup = Math.Min(500, jobs / 10);
const int idleEvery = 2_500;

Console.WriteLine($"Qualification {mode} on {RuntimeInformation.RuntimeIdentifier}, work directory {work}");
var (good, encrypted, corrupt) = Corpus.Load(work);
var references = good.ToDictionary(d => d.Name,
    d => Corpus.BuildReference(d, Path.Combine(work, "reference", Path.GetFileNameWithoutExtension(d.Name))));
Console.WriteLine($"In-process references built for {references.Count} documents.");

var clock = Stopwatch.StartNew();
Runner? runner = null;
using var sampler = new Sampler(() => runner?.Completed ?? 0);

var options = new PdfPoolOptions
{
    MinWorkers = minWorkers,
    MaxWorkers = maxWorkers,
    QueueCapacity = 256,
    TempDirectory = poolTemp,
};
var pool = new PdfProcessingPool(options);
pool.Events += sampler.OnEvent;
runner = new Runner(pool, good, encrypted, corrupt, references, outputRoot, sampler, seed);
var started = DateTimeOffset.UtcNow;

using var progress = new Timer(_ => Console.WriteLine(
    $"{clock.Elapsed:hh\\:mm\\:ss} submitted {runner.Submitted:N0} completed {runner.Completed:N0} in flight {runner.InFlight} " +
    $"workers {pool.Workers} unexpected {runner.Unexpected} mismatches {runner.Mismatches}"), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

if (mode == "soak")
{
    while (clock.Elapsed < duration)
    {
        var loadEnd = Min(clock.Elapsed + loadPeriod, duration);
        await runner.RunAsync(_ => clock.Elapsed < loadEnd, () => fullLoad);
        await IdleReadingAsync();
        var trickleEnd = Min(clock.Elapsed + tricklePeriod, duration);
        await runner.RunAsync(_ => clock.Elapsed < trickleEnd, () => 1);
    }
}
else
{
    await runner.RunAsync(submitted => submitted < warmup, () => fullLoad);
    await IdleReadingAsync();
    for (long checkpoint = idleEvery; checkpoint < jobs; checkpoint += idleEvery)
    {
        long until = checkpoint;
        await runner.RunAsync(submitted => submitted < until, () => fullLoad);
        await IdleReadingAsync();
    }

    await runner.RunAsync(submitted => submitted < jobs, () => fullLoad);
    if (batch > 0)
        await runner.RunBatchAsync(batch);
}

await IdleReadingAsync();
var statistics = pool.Statistics;
await pool.DisposeAsync();
double seconds = clock.Elapsed.TotalSeconds;

// Every worker must be gone shortly after DisposeAsync.
var seen = sampler.SeenWorkers;
var deadline = Stopwatch.StartNew();
int alive;
while ((alive = seen.Count(IsAlive)) > 0 && deadline.Elapsed < TimeSpan.FromSeconds(5))
    await Task.Delay(100);

var samples = sampler.Samples;
var growth = samples.GroupBy(s => s.Pid)
    .Select(g => Growth.Of(g.Key, g.Key == Environment.ProcessId ? "coordinator" : "worker", g.ToList(), judge: mode != "smoke"))
    .ToList();

var report = new QualificationReport(
    mode,
    RuntimeInformation.RuntimeIdentifier,
    RuntimeInformation.FrameworkDescription,
    File.Exists(Path.Combine(AppContext.BaseDirectory, "System.Private.CoreLib.dll")),
    minWorkers,
    maxWorkers,
    seed,
    started,
    seconds,
    runner.Submitted,
    runner.Completed,
    batch,
    runner.StatusCounts,
    runner.Unexpected,
    runner.Mismatches,
    runner.Leftovers,
    runner.Retried,
    QualificationReport.Percentile(runner.TotalMs, 0.50),
    QualificationReport.Percentile(runner.TotalMs, 0.95),
    QualificationReport.Percentile(runner.TotalMs, 0.99),
    QualificationReport.Percentile(runner.ProcessingMs, 0.50),
    QualificationReport.Percentile(runner.ProcessingMs, 0.95),
    QualificationReport.Percentile(runner.ProcessingMs, 0.99),
    statistics,
    sampler.EventCounts.ToDictionary(k => k.Key.ToString(), k => k.Value),
    seen.Count,
    alive,
    Directory.EnumerateFiles(outputRoot, "*", SearchOption.AllDirectories).Count(),
    Directory.EnumerateFiles(poolTemp, "*", SearchOption.AllDirectories).Count(),
    growth,
    runner.Problems,
    sampler.WorkerEvents,
    samples);

Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
string markdown = report.ToMarkdown();
File.WriteAllText(Path.ChangeExtension(reportPath, ".md"), markdown);
if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
    File.AppendAllText(summary, markdown + Environment.NewLine);

Console.WriteLine();
Console.WriteLine(markdown);
Console.WriteLine($"Report: {reportPath}");
return report.ExitCode;

// RunAsync returns once every job has finished; the readings follow a short settle.
async Task IdleReadingAsync()
{
    await Task.Delay(TimeSpan.FromSeconds(2));
    sampler.Take(idle: true);
}

static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

static bool IsAlive(int pid)
{
    try
    {
        using var process = Process.GetProcessById(pid);
        return !process.HasExited;
    }
    catch (Exception)
    {
        return false;
    }
}
