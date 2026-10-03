using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Aspose.Pdf.Devices;

namespace PdfiumWrapper.Benchmarks.Comparison;

/// <summary>
/// A service-shaped workload: N requests arrive at once; each one reads a document's page count
/// and converts every page to a PNG file. Measures completion of the whole batch for one engine
/// in one deployment shape.
/// </summary>
/// <remarks>
/// <code>
/// dotnet run -c Release -- throughput --engine pdfium|ghostscript|aspose --n 1000 --concurrency 8 \
///   [--processes 4] [--dpi 150] [--input &lt;dir&gt;] [--out &lt;dir&gt;] [--report &lt;file&gt;]
/// </code>
/// <para>
/// <c>--concurrency</c> is the number of requests in flight per process. <c>--processes</c> splits
/// the batch over that many copies of this program (PdfiumWrapper and Aspose.PDF); Ghostscript is
/// always one console process per request, so its concurrency is its process count.
/// </para>
/// </remarks>
internal static class ThroughputRunner
{
    private sealed class Options
    {
        public string Engine = Comparison.Engine.Pdfium;
        public int N = 1000;
        public int Concurrency = 1;
        public int Processes = 1;
        public int Dpi = 150;
        public string Input = Path.Combine(AppContext.BaseDirectory, "Docs");
        public string Out = Path.Combine(Path.GetTempPath(), "PdfiumThroughput");
        public string? Report;

        // Worker mode: this process handles the requests whose index is congruent to WorkerIndex
        // modulo Processes, and reports to the parent through WorkerResults.
        public int WorkerIndex = -1;
        public long StartUtcTicks;
        public string? WorkerResults;
    }

    private sealed record RequestResult(int Id, double StartMs, double EndMs, int Pages, long Bytes, string? Error);

    private sealed record WorkerSummary(RequestResult[] Requests, double CpuSeconds, double PeakWorkingSetMb);

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

        var inputs = Directory.Exists(options.Input)
            ? Directory.GetFiles(options.Input, "*.pdf").OrderBy(f => f, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
        if (inputs.Length == 0)
        {
            Console.Error.WriteLine($"no .pdf files under '{options.Input}'");
            return 3;
        }

        if (options.Engine == Comparison.Engine.Ghostscript && !Ghostscript.IsAvailable)
        {
            Console.Error.WriteLine("Ghostscript was not found.");
            return 3;
        }

        if (options.Engine == Comparison.Engine.Aspose && !AsposeEngine.IsAvailable)
        {
            Console.Error.WriteLine($"Aspose.PDF needs a license: set {AsposeEngine.LicenseVariable}.");
            return 3;
        }

        return options.WorkerIndex >= 0
            ? RunWorker(options, inputs)
            : RunCoordinator(options, inputs);
    }

    // ---- Coordinator ----

    private static int RunCoordinator(Options options, string[] inputs)
    {
        string runDirectory = Path.Combine(options.Out, $"throughput-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runDirectory);

        try
        {
            WorkerSummary[] summaries;
            double wallSeconds;
            double sampledPeakMb = 0;

            if (options.Processes > 1 && options.Engine != Comparison.Engine.Ghostscript)
            {
                (summaries, wallSeconds) = RunWorkerProcesses(options, runDirectory);
            }
            else
            {
                Warm(options, inputs[0], runDirectory);
                long startTicks = DateTime.UtcNow.Ticks;
                var summary = Execute(options, inputs, runDirectory, startTicks, workerIndex: 0, processes: 1, out sampledPeakMb);
                summaries = new[] { summary };
                wallSeconds = summary.Requests.Max(r => r.EndMs) / 1000.0;
            }

            var requests = summaries.SelectMany(s => s.Requests).OrderBy(r => r.Id).ToArray();
            var succeeded = requests.Where(r => r.Error == null).ToArray();
            int pages = succeeded.Sum(r => r.Pages);
            double peakMb = options.Engine == Comparison.Engine.Ghostscript
                ? sampledPeakMb
                : summaries.Sum(s => s.PeakWorkingSetMb);

            var report = new Dictionary<string, object?>
            {
                ["engine"] = options.Engine,
                ["requests"] = options.N,
                ["concurrencyPerProcess"] = options.Concurrency,
                ["processes"] = options.Engine == Comparison.Engine.Ghostscript ? options.Concurrency : options.Processes,
                ["dpi"] = options.Dpi,
                ["documents"] = inputs.Select(Path.GetFileName).ToArray(),
                ["succeeded"] = succeeded.Length,
                ["failed"] = requests.Length - succeeded.Length,
                ["wallSeconds"] = Round(wallSeconds),
                ["requestsPerSecond"] = Round(succeeded.Length / wallSeconds),
                ["pages"] = pages,
                ["pagesPerSecond"] = Round(pages / wallSeconds),
                ["latencyMs"] = new Dictionary<string, object?>
                {
                    ["processing"] = Percentiles(requests.Select(r => r.EndMs - r.StartMs)),
                    ["sinceArrival"] = Percentiles(requests.Select(r => r.EndMs)),
                },
                ["cpuSeconds"] = Round(summaries.Sum(s => s.CpuSeconds)),
                ["cpuCoresUsed"] = Round(summaries.Sum(s => s.CpuSeconds) / wallSeconds),
                ["peakWorkingSetMB"] = Round(peakMb),
                ["outputBytes"] = succeeded.Sum(r => r.Bytes),
                ["firstErrors"] = requests.Where(r => r.Error != null).Select(r => r.Error).Distinct().Take(5).ToArray(),
            };

            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            if (options.Report != null)
            {
                var reportDirectory = Path.GetDirectoryName(Path.GetFullPath(options.Report));
                if (!string.IsNullOrEmpty(reportDirectory))
                    Directory.CreateDirectory(reportDirectory);
                File.WriteAllText(options.Report, json);
            }

            Console.WriteLine(
                $"{options.Engine,-13} processes={report["processes"],-3} concurrency={options.Concurrency,-3} " +
                $"{wallSeconds,8:F1}s  {succeeded.Length / wallSeconds,7:F2} req/s  {pages / wallSeconds,8:F1} pages/s  " +
                $"cpu {report["cpuCoresUsed"]} cores  peak {peakMb:F0} MB  failed {requests.Length - succeeded.Length}");

            return requests.Length == succeeded.Length ? 0 : 2;
        }
        finally
        {
            TryDeleteDirectory(runDirectory);
        }
    }

    private static (WorkerSummary[] summaries, double wallSeconds) RunWorkerProcesses(Options options, string runDirectory)
    {
        string self = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate this executable.");
        string? entryAssembly = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        bool viaDotnet = Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase);

        // Workers start, warm up, then wait for the shared start time so that process startup is
        // not charged to the first requests of some workers and not others.
        long startTicks = DateTime.UtcNow.AddSeconds(3 + options.Processes * 0.25).Ticks;

        var processes = new List<(Process process, string results)>();
        for (int i = 0; i < options.Processes; i++)
        {
            string results = Path.Combine(runDirectory, $"worker-{i}.json");
            var psi = new ProcessStartInfo(self) { UseShellExecute = false, CreateNoWindow = true };
            if (viaDotnet && entryAssembly != null)
                psi.ArgumentList.Add(entryAssembly);
            foreach (var argument in new[]
                     {
                         "throughput", "--engine", options.Engine, "--n", options.N.ToString(CultureInfo.InvariantCulture),
                         "--concurrency", options.Concurrency.ToString(CultureInfo.InvariantCulture),
                         "--processes", options.Processes.ToString(CultureInfo.InvariantCulture),
                         "--dpi", options.Dpi.ToString(CultureInfo.InvariantCulture),
                         "--input", options.Input, "--out", runDirectory,
                         "--worker-index", i.ToString(CultureInfo.InvariantCulture),
                         "--start-utc-ticks", startTicks.ToString(CultureInfo.InvariantCulture),
                         "--worker-results", results,
                     })
            {
                psi.ArgumentList.Add(argument);
            }

            processes.Add((Process.Start(psi)!, results));
        }

        foreach (var (process, _) in processes)
            process.WaitForExit();

        var summaries = processes
            .Select(p => JsonSerializer.Deserialize<WorkerSummary>(File.ReadAllText(p.results))!)
            .ToArray();
        foreach (var (process, _) in processes)
            process.Dispose();

        double wallSeconds = summaries.SelectMany(s => s.Requests).Max(r => r.EndMs) / 1000.0;
        return (summaries, wallSeconds);
    }

    // ---- Worker ----

    private static int RunWorker(Options options, string[] inputs)
    {
        Warm(options, inputs[0], options.Out);

        var wait = new DateTime(options.StartUtcTicks, DateTimeKind.Utc) - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
            Thread.Sleep(wait);

        var summary = Execute(options, inputs, options.Out, options.StartUtcTicks, options.WorkerIndex, options.Processes, out _);
        File.WriteAllText(options.WorkerResults!, JsonSerializer.Serialize(summary));
        return 0;
    }

    /// <summary>One-time costs (native init, license, JIT) are paid before the clock starts.</summary>
    private static void Warm(Options options, string input, string directory)
    {
        if (options.Engine == Comparison.Engine.Aspose)
            AsposeEngine.EnsureLicensed();
        if (options.Engine == Comparison.Engine.Ghostscript)
            return; // every Ghostscript request is a fresh process; there is nothing to warm

        string warmDirectory = Path.Combine(directory, $"warm-{Environment.ProcessId}");
        Directory.CreateDirectory(warmDirectory);
        Handle(options, input, warmDirectory);
        TryDeleteDirectory(warmDirectory);
    }

    private static WorkerSummary Execute(Options options, string[] inputs, string directory, long startUtcTicks,
        int workerIndex, int processes, out double sampledPeakMb)
    {
        var self = Process.GetCurrentProcess();
        var cpuBefore = self.TotalProcessorTime;
        long childCpuBefore = Ghostscript.ChildCpuTicks;

        var queue = new ConcurrentQueue<int>(Enumerable.Range(0, options.N).Where(i => i % processes == workerIndex));
        var results = new ConcurrentBag<RequestResult>();
        var cleanup = new BlockingCollection<string>();
        var start = new DateTime(startUtcTicks, DateTimeKind.Utc);

        // Output is deleted as soon as it has been measured, on its own thread, so a long run does
        // not fill the disk and workers do not spend their time deleting.
        var cleaner = new Thread(() =>
        {
            foreach (var path in cleanup.GetConsumingEnumerable())
                TryDeleteDirectory(path);
        }) { IsBackground = true, Name = "cleanup" };
        cleaner.Start();

        // Ghostscript's memory lives in its child processes; sample their working sets.
        double peakChildrenMb = 0;
        using var stopSampling = new CancellationTokenSource();
        var sampler = new Thread(() =>
        {
            while (!stopSampling.IsCancellationRequested)
            {
                peakChildrenMb = Math.Max(peakChildrenMb, Ghostscript.LiveWorkingSetBytes() / (1024.0 * 1024.0));
                stopSampling.Token.WaitHandle.WaitOne(100);
            }
        }) { IsBackground = true, Name = "sampler" };
        if (options.Engine == Comparison.Engine.Ghostscript)
            sampler.Start();

        var threads = Enumerable.Range(0, options.Concurrency).Select(t => new Thread(() =>
        {
            while (queue.TryDequeue(out int id))
            {
                string requestDirectory = Path.Combine(directory, $"req-{id:D6}");
                Directory.CreateDirectory(requestDirectory);
                double startMs = (DateTime.UtcNow - start).TotalMilliseconds;
                try
                {
                    int pages = Handle(options, inputs[id % inputs.Length], requestDirectory);
                    double endMs = (DateTime.UtcNow - start).TotalMilliseconds;
                    long bytes = Directory.GetFiles(requestDirectory).Sum(f => new FileInfo(f).Length);
                    results.Add(new RequestResult(id, startMs, endMs, pages, bytes, null));
                }
                catch (Exception ex)
                {
                    double endMs = (DateTime.UtcNow - start).TotalMilliseconds;
                    results.Add(new RequestResult(id, startMs, endMs, 0, 0, $"{ex.GetType().Name}: {ex.Message}"));
                }

                cleanup.Add(requestDirectory);
            }
        }) { IsBackground = true, Name = $"request-{t}" }).ToList();

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        stopSampling.Cancel();
        cleanup.CompleteAdding();
        cleaner.Join();

        self.Refresh();
        double cpuSeconds = (self.TotalProcessorTime - cpuBefore).TotalSeconds
                            + TimeSpan.FromTicks(Ghostscript.ChildCpuTicks - childCpuBefore).TotalSeconds;
        sampledPeakMb = peakChildrenMb + self.PeakWorkingSet64 / (1024.0 * 1024.0);

        return new WorkerSummary(results.ToArray(), cpuSeconds, self.PeakWorkingSet64 / (1024.0 * 1024.0));
    }

    /// <summary>One request: the page count, and every page as a PNG file. Returns the page count.</summary>
    private static int Handle(Options options, string input, string outputDirectory)
    {
        switch (options.Engine)
        {
            case Comparison.Engine.Pdfium:
            {
                using var doc = new PdfDocument(input);
                int pages = doc.PageCount;
                doc.SaveAsPngs(outputDirectory, "page", options.Dpi);
                return pages;
            }

            case Comparison.Engine.Aspose:
            {
                using var doc = new Aspose.Pdf.Document(input);
                int pages = doc.Pages.Count;
                var device = new PngDevice(new Resolution(options.Dpi));
                for (int page = 1; page <= pages; page++)
                {
                    using var output = File.Create(Path.Combine(outputDirectory, $"page_{page:D3}.png"));
                    device.Process(doc.Pages[page], output);
                }

                return pages;
            }

            case Comparison.Engine.Ghostscript:
            {
                // One process does the conversion; the page count is the number of files it wrote.
                // That is the cheapest way to get both from Ghostscript: a separate page-count
                // call would cost another process start.
                Ghostscript.Run("-q", "-dNOPAUSE", "-dBATCH", "-dSAFER", "-sDEVICE=png16m",
                    "-r" + options.Dpi.ToString(CultureInfo.InvariantCulture),
                    "-dTextAlphaBits=4", "-dGraphicsAlphaBits=4",
                    "-sOutputFile=" + Path.Combine(outputDirectory, "page_%03d.png"), input);
                return Directory.GetFiles(outputDirectory, "page_*.png").Length;
            }

            default:
                throw new ArgumentException($"unknown engine {options.Engine}");
        }
    }

    // ---- Helpers ----

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static Dictionary<string, object?> Percentiles(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        double At(double p) => sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
        return new Dictionary<string, object?>
        {
            ["p50"] = Round(At(0.50)),
            ["p95"] = Round(At(0.95)),
            ["p99"] = Round(At(0.99)),
            ["max"] = Round(sorted.Length == 0 ? 0 : sorted[^1]),
        };
    }

    private static double Round(double value) => Math.Round(value, 3);

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
                case "--engine":
                    options.Engine = value.ToLowerInvariant() switch
                    {
                        "pdfium" or "pdfiumwrapper" => Comparison.Engine.Pdfium,
                        "ghostscript" or "gs" => Comparison.Engine.Ghostscript,
                        "aspose" => Comparison.Engine.Aspose,
                        _ => throw new ArgumentException("--engine must be pdfium, ghostscript or aspose"),
                    };
                    break;
                case "--n": options.N = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--concurrency": options.Concurrency = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--processes": options.Processes = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--dpi": options.Dpi = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--input": options.Input = value; break;
                case "--out": options.Out = value; break;
                case "--report": options.Report = value; break;
                case "--worker-index": options.WorkerIndex = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--start-utc-ticks": options.StartUtcTicks = long.Parse(value, CultureInfo.InvariantCulture); break;
                case "--worker-results": options.WorkerResults = value; break;
                default: throw new ArgumentException($"unknown option {args[i]}");
            }
        }

        if (options.N <= 0 || options.Concurrency <= 0 || options.Processes <= 0 || options.Dpi <= 0)
            throw new ArgumentException("--n, --concurrency, --processes and --dpi must be positive");
        return options;
    }
}
