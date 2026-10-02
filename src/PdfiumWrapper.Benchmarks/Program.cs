using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Perfolizer.Horology;
using PdfiumWrapper.Benchmarks;

// "burst" runs the deadline-oriented batch runner instead of BenchmarkDotNet.
if (args.Length > 0 && args[0] == "burst")
{
    return BurstRunner.Run(args.Skip(1).ToArray());
}

var config = ManualConfig.CreateMinimumViable()
    .AddExporter(new CsvExporter(
        CsvSeparator.Comma,
        new SummaryStyle(
            cultureInfo: System.Globalization.CultureInfo.InvariantCulture,
            printUnitsInHeader: true,
            printUnitsInContent: false,
            timeUnit: TimeUnit.Millisecond,
            sizeUnit: null)))
    .WithSummaryStyle(new SummaryStyle(
        cultureInfo: System.Globalization.CultureInfo.InvariantCulture,
        printUnitsInHeader: true,
        printUnitsInContent: false,
        timeUnit: TimeUnit.Millisecond,
        sizeUnit: null));

var benchmarkTypes = new[]
{
    typeof(PdfToJpegBenchmark),
    typeof(PdfToPngBenchmark),
    typeof(PdfToTiffBenchmark),
    typeof(PdfMergeBenchmark),
    typeof(SmallDocumentBenchmark),
};

// "--only A,B" restricts the run to the named benchmark classes.
int onlyIndex = Array.IndexOf(args, "--only");
if (onlyIndex >= 0 && onlyIndex + 1 < args.Length)
{
    var names = args[onlyIndex + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    benchmarkTypes = benchmarkTypes.Where(t => names.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
    if (benchmarkTypes.Length == 0)
    {
        Console.Error.WriteLine($"--only matched no benchmark class: {args[onlyIndex + 1]}");
        return 3;
    }
}

BenchmarkRunner.Run(benchmarkTypes.Select(t => BenchmarkConverter.TypeToBenchmarks(t, config)).ToArray());
return 0;
