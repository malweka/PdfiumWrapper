using BenchmarkDotNet.Attributes;

namespace PdfiumWrapper.Benchmarks;

public class PdfToPngBenchmark : BenchmarkBase
{
    [Benchmark]
    public void ConvertToPng()
    {
        using var doc = new PdfDocument(GetDocPath());
        doc.SaveAsPngs(OutputDirectory, "page", dpi: 300);
    }
}
