using BenchmarkDotNet.Attributes;

namespace PdfiumWrapper.Benchmarks;

public class PdfToJpegBenchmark : BenchmarkBase
{
    [Benchmark]
    public void ConvertToJpeg()
    {
        using var doc = new PdfDocument(GetDocPath());
        doc.SaveAsJpegs(OutputDirectory, "page", quality: 90, dpi: 300);
    }
}
