using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Perfolizer.Horology;
using PdfiumWrapper.Benchmarks;

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

BenchmarkRunner.Run(new[]
{
    BenchmarkConverter.TypeToBenchmarks(typeof(PdfToJpegBenchmark), config),
    BenchmarkConverter.TypeToBenchmarks(typeof(PdfToPngBenchmark), config),
    BenchmarkConverter.TypeToBenchmarks(typeof(PdfToTiffBenchmark), config),
    BenchmarkConverter.TypeToBenchmarks(typeof(PdfMergeBenchmark), config),
});
