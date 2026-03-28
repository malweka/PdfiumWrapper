using BenchmarkDotNet.Attributes;

namespace PdfiumWrapper.Benchmarks;

public class PdfMergeBenchmark : BenchmarkBase
{
    [Benchmark]
    public void MergeTwoDocuments()
    {
        using var merger = new PdfMerger(GetDocPath());
        using var second = new PdfDocument(GetDocPath());
        merger.AppendDocument(second);
        var outputPath = Path.Combine(OutputDirectory, "merged.pdf");
        merger.Save(outputPath);
    }
}
