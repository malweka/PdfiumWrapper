using BenchmarkDotNet.Attributes;

namespace PdfiumWrapper.Benchmarks;

public class PdfToTiffBenchmark : BenchmarkBase
{
    [Benchmark]
    public void ConvertToTiff()
    {
        using var doc = new PdfDocument(GetDocPath());
        var outputPath = Path.Combine(OutputDirectory, "output.tiff");
        doc.SaveAsTiff(outputPath, dpi: 200);
    }
}
