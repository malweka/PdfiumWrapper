using BenchmarkDotNet.Attributes;

namespace PdfiumWrapper.Benchmarks;

/// <summary>
/// One-page document operations. Fixed per-operation costs (synchronization, handle bookkeeping)
/// are proportionally largest here, so this is the regression reference for the native gate.
/// </summary>
[MemoryDiagnoser]
public class SmallDocumentBenchmark
{
    private string _path = null!;

    [GlobalSetup]
    public virtual void Setup()
    {
        _path = Path.Combine(AppContext.BaseDirectory, "Docs", "doc-1-page.pdf");
    }

    [Benchmark]
    public int LoadCountClose()
    {
        using var doc = new PdfDocument(_path);
        return doc.PageCount;
    }

    [Benchmark]
    public int LoadRender72Close()
    {
        using var doc = new PdfDocument(_path);
        int pages = doc.PageCount;
        using var page = doc.GetPage(0);

        // 72 DPI: one pixel per PDF point.
        int width = (int)Math.Round(page.Width);
        int height = (int)Math.Round(page.Height);
        return page.RenderToBytes(width, height).Length + pages;
    }
}
