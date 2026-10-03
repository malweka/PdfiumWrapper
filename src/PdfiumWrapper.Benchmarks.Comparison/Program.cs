using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Perfolizer.Horology;
using PdfiumWrapper.Benchmarks.Comparison;

// Head-to-head benchmarks: PdfiumWrapper against Ghostscript and Aspose.PDF.
//
//   dotnet run -c Release                     run every comparison for the engines that are available
//   dotnet run -c Release -- --only Tiff,Png  run only the named comparisons
//   dotnet run -c Release -- check            run each engine once and print what it produced
//   dotnet run -c Release -- throughput ...   service-shaped batch for one engine (see ThroughputRunner)
//
// GHOSTSCRIPT_EXE    path to gswin64c / gs, when it is not on PATH
// ASPOSE_PDF_LICENSE path to an Aspose.PDF license file (kept outside this repository)

var engines = new List<string> { Engine.Pdfium };
Console.WriteLine("PdfiumWrapper: " + typeof(PdfiumWrapper.PdfDocument).Assembly.GetName().Version);

if (Ghostscript.IsAvailable)
{
    engines.Add(Engine.Ghostscript);
    Console.WriteLine($"Ghostscript:   {Ghostscript.Version()} ({Ghostscript.Executable})");
}
else
{
    Console.WriteLine($"Ghostscript:   not found (put it on PATH or set {Ghostscript.PathVariable}); its benchmarks are skipped");
}

if (AsposeEngine.IsAvailable)
{
    engines.Add(Engine.Aspose);
    Console.WriteLine($"Aspose.PDF:    {AsposeEngine.Version()} (licensed)");
}
else
{
    Console.WriteLine($"Aspose.PDF:    no license (set {AsposeEngine.LicenseVariable}); its benchmarks are skipped, because evaluation mode limits pages and watermarks output");
}

if (args.Length > 0 && args[0] == "throughput")
{
    return ThroughputRunner.Run(args.Skip(1).ToArray());
}

if (args.Length > 0 && args[0] == "check")
{
    return EngineCheck.Run(engines);
}

var style = new SummaryStyle(
    cultureInfo: System.Globalization.CultureInfo.InvariantCulture,
    printUnitsInHeader: true,
    printUnitsInContent: false,
    timeUnit: TimeUnit.Millisecond,
    sizeUnit: null);

// Results for other engines are not published. Keep them out of the shared BenchmarkDotNet
// artifacts folder (which gets imported into benchmark.db) and under the git-ignored ai/tmp.
var config = ManualConfig.CreateMinimumViable()
    .WithArtifactsPath(PrivateOutput.Directory("comparison-artifacts"))
    .AddExporter(new CsvExporter(CsvSeparator.Comma, style))
    .WithSummaryStyle(style)
    .AddFilter(new SimpleFilter(benchmark => benchmark.Descriptor.Categories.Any(engines.Contains)));

var comparisons = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
{
    ["PageCount"] = typeof(PageCountComparison),
    ["Tiff"] = typeof(TiffComparison),
    ["Png"] = typeof(PngComparison),
    ["Jpeg"] = typeof(JpegComparison),
    ["Merge"] = typeof(MergeComparison),
    ["Text"] = typeof(TextComparison),
    ["Startup"] = typeof(StartupBenchmark),
};

var selected = comparisons.Values.ToArray();
int onlyIndex = Array.IndexOf(args, "--only");
if (onlyIndex >= 0 && onlyIndex + 1 < args.Length)
{
    var names = args[onlyIndex + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var unknown = names.Where(n => !comparisons.ContainsKey(n)).ToArray();
    if (unknown.Length > 0)
    {
        Console.Error.WriteLine($"--only: unknown comparison {string.Join(", ", unknown)}. Known: {string.Join(", ", comparisons.Keys)}");
        return 3;
    }

    selected = names.Select(n => comparisons[n]).ToArray();
}

BenchmarkRunner.Run(selected.Select(t => BenchmarkConverter.TypeToBenchmarks(t, config)).ToArray());
return 0;
