using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace PdfiumWrapper.Benchmarks;

/// <summary>
/// In-process scaling: <see cref="Callers"/> threads, released together, each converting the whole
/// corpus with its own documents. Native rendering is serialized by the wrapper; encoding overlaps.
/// </summary>
/// <remarks>
/// Every caller does the same work (all corpus pages), so
/// pages/sec = Callers * <see cref="BenchmarkBase.CorpusPages"/> / mean seconds, and the speedup at
/// W callers is pages/sec at W over pages/sec at 1.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, warmupCount: 1, iterationCount: 5)]
public class ConcurrentCallersBenchmark
{
    [Params(1, 2, 4, 8)]
    public int Callers;

    [Params("tiff", "png", "jpeg")]
    public string Format = "tiff";

    private string[] _inputs = null!;

    [GlobalSetup]
    public void Setup()
    {
        _inputs = BenchmarkBase.CorpusFiles();

        using var warm = new PdfDocument(_inputs[0]);
        using var page = warm.GetPage(0);
        _ = page.RenderToBytes((int)Math.Round(page.Width), (int)Math.Round(page.Height));
    }

    [Benchmark]
    public void ConvertBatch()
    {
        using var start = new Barrier(Callers);
        var threads = Enumerable.Range(0, Callers).Select(_ => new Thread(() =>
        {
            start.SignalAndWait();
            foreach (var input in _inputs)
                Convert(input);
        })).ToList();

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
    }

    private void Convert(string input)
    {
        using var doc = new PdfDocument(input);
        switch (Format)
        {
            case "tiff":
            {
                using var sink = new MemoryStream();
                doc.SaveAsTiff(sink, 200, TiffColorMode.Bilevel);
                break;
            }
            case "png":
                foreach (var _ in doc.StreamImageBytes(ImageFormat.Png, 100, 150)) { }
                break;
            case "jpeg":
                foreach (var _ in doc.StreamImageBytes(ImageFormat.Jpeg, 85, 150)) { }
                break;
        }
    }
}
