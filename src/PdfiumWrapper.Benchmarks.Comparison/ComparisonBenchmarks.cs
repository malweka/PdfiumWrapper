using Aspose.Pdf.Devices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace PdfiumWrapper.Benchmarks.Comparison;

/// <summary>Benchmark categories, one per engine, used to leave out engines that are not installed.</summary>
internal static class Engine
{
    public const string Pdfium = "PdfiumWrapper";
    public const string Ghostscript = "Ghostscript";
    public const string Aspose = "Aspose";
    public const string Pool = "PdfiumWrapper.Processing";
}

public sealed class CorpusDocument
{
    public CorpusDocument(string fileName, int pages)
    {
        FileName = fileName;
        Pages = pages;
    }

    public string FileName { get; }
    public int Pages { get; }

    public override string ToString() => $"{FileName} ({Pages}p)";
}

/// <summary>
/// One operation, one document, three engines. Each engine does the whole job end to end:
/// open the file, do the work, write the output to disk. PdfiumWrapper is the baseline for the
/// ratio column.
/// </summary>
[MemoryDiagnoser]
public abstract class ComparisonBase
{
    private static readonly string DocsDirectory = Path.Combine(AppContext.BaseDirectory, "Docs");

    public static IEnumerable<CorpusDocument> Documents => new[]
    {
        new CorpusDocument("doc-1-page.pdf", 1),
        new CorpusDocument("doc-3-pages-with-comments.pdf", 3),
        new CorpusDocument("contract.pdf", 10),
        new CorpusDocument("fw2.pdf", 11),
        new CorpusDocument("presentation.pdf", 37),
    };

    [ParamsSource(nameof(Documents))]
    public CorpusDocument Document { get; set; } = null!;

    protected string Input = null!;
    protected string OutputDirectory = null!;

    [GlobalSetup]
    public void Setup()
    {
        Input = Path.GetFullPath(Path.Combine(DocsDirectory, Document.FileName));
        OutputDirectory = Path.Combine(Path.GetTempPath(), "PdfiumComparison", GetType().Name);
        Directory.CreateDirectory(OutputDirectory);

        // Pay one-time initialization outside the measurements.
        using (var doc = new PdfDocument(Input))
            _ = doc.PageCount;
        if (AsposeEngine.IsAvailable)
            AsposeEngine.EnsureLicensed();
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        foreach (var file in Directory.GetFiles(OutputDirectory))
            File.Delete(file);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(OutputDirectory))
            Directory.Delete(OutputDirectory, recursive: true);
    }

    protected string Output(string fileName) => Path.Combine(OutputDirectory, fileName);

    /// <summary>Arguments common to every Ghostscript conversion.</summary>
    protected static readonly string[] GhostscriptBatch = { "-q", "-dNOPAUSE", "-dBATCH", "-dSAFER" };

    /// <summary>4-bit anti-aliasing for text and graphics, to match PDFium's smoothed rendering.</summary>
    protected static readonly string[] GhostscriptAntiAlias = { "-dTextAlphaBits=4", "-dGraphicsAlphaBits=4" };

    protected static string RunGhostscript(params string[][] argumentGroups)
        => Ghostscript.Run(argumentGroups.SelectMany(g => g).ToArray());
}

/// <summary>Open a document and read its page count.</summary>
[SimpleJob(RunStrategy.Monitoring, warmupCount: 2, iterationCount: 15)]
public class PageCountComparison : ComparisonBase
{
    [Benchmark(Baseline = true), BenchmarkCategory(Engine.Pdfium)]
    public int Pdfium()
    {
        using var doc = new PdfDocument(Input);
        return doc.PageCount;
    }

    [Benchmark, BenchmarkCategory(Engine.Ghostscript)]
    public int Ghostscript()
    {
        string output = Comparison.Ghostscript.Run("-q", "-dNODISPLAY", "-dNOSAFER", "-c",
            $"{Comparison.Ghostscript.PostScriptString(Input)} (r) file runpdfbegin pdfpagecount = quit");
        return int.Parse(output.Trim());
    }

    [Benchmark, BenchmarkCategory(Engine.Aspose)]
    public int Aspose()
    {
        using var doc = new global::Aspose.Pdf.Document(Input);
        return doc.Pages.Count;
    }
}

/// <summary>All pages to one multi-page bilevel TIFF, CCITT Group 4, 200 DPI.</summary>
[SimpleJob(RunStrategy.Monitoring, warmupCount: 1, iterationCount: 5)]
public class TiffComparison : ComparisonBase
{
    [Benchmark(Baseline = true), BenchmarkCategory(Engine.Pdfium)]
    public void Pdfium()
    {
        using var doc = new PdfDocument(Input);
        doc.SaveAsTiff(Output("output.tiff"), dpi: 200, TiffColorMode.Bilevel);
    }

    [Benchmark, BenchmarkCategory(Engine.Ghostscript)]
    public void Ghostscript()
    {
        RunGhostscript(GhostscriptBatch, new[] { "-sDEVICE=tiffg4", "-r200", "-sOutputFile=" + Output("output.tiff"), Input });
    }

    [Benchmark, BenchmarkCategory(Engine.Aspose)]
    public void Aspose()
    {
        using var doc = new global::Aspose.Pdf.Document(Input);
        var settings = new TiffSettings
        {
            Compression = CompressionType.CCITT4,
            Depth = ColorDepth.Format1bpp,
            SkipBlankPages = false,
        };
        new TiffDevice(new Resolution(200), settings).Process(doc, Output("output.tiff"));
    }
}

/// <summary>Every page to its own 24-bit PNG file, 300 DPI.</summary>
[SimpleJob(RunStrategy.Monitoring, warmupCount: 1, iterationCount: 5)]
public class PngComparison : ComparisonBase
{
    [Benchmark(Baseline = true), BenchmarkCategory(Engine.Pdfium)]
    public void Pdfium()
    {
        using var doc = new PdfDocument(Input);
        doc.SaveAsPngs(OutputDirectory, "page", dpi: 300);
    }

    [Benchmark, BenchmarkCategory(Engine.Ghostscript)]
    public void Ghostscript()
    {
        RunGhostscript(GhostscriptBatch, GhostscriptAntiAlias,
            new[] { "-sDEVICE=png16m", "-r300", "-sOutputFile=" + Output("page_%03d.png"), Input });
    }

    [Benchmark, BenchmarkCategory(Engine.Aspose)]
    public void Aspose()
    {
        using var doc = new global::Aspose.Pdf.Document(Input);
        var device = new PngDevice(new Resolution(300));
        for (int page = 1; page <= doc.Pages.Count; page++)
        {
            using var output = File.Create(Output($"page_{page:D3}.png"));
            device.Process(doc.Pages[page], output);
        }
    }
}

/// <summary>Every page to its own JPEG file, quality 90, 300 DPI.</summary>
[SimpleJob(RunStrategy.Monitoring, warmupCount: 1, iterationCount: 5)]
public class JpegComparison : ComparisonBase
{
    [Benchmark(Baseline = true), BenchmarkCategory(Engine.Pdfium)]
    public void Pdfium()
    {
        using var doc = new PdfDocument(Input);
        doc.SaveAsJpegs(OutputDirectory, "page", quality: 90, dpi: 300);
    }

    [Benchmark, BenchmarkCategory(Engine.Ghostscript)]
    public void Ghostscript()
    {
        RunGhostscript(GhostscriptBatch, GhostscriptAntiAlias,
            new[] { "-sDEVICE=jpeg", "-r300", "-dJPEGQ=90", "-sOutputFile=" + Output("page_%03d.jpg"), Input });
    }

    [Benchmark, BenchmarkCategory(Engine.Aspose)]
    public void Aspose()
    {
        using var doc = new global::Aspose.Pdf.Document(Input);
        var device = new JpegDevice(new Resolution(300), 90);
        for (int page = 1; page <= doc.Pages.Count; page++)
        {
            using var output = File.Create(Output($"page_{page:D3}.jpg"));
            device.Process(doc.Pages[page], output);
        }
    }
}

/// <summary>Append a document to itself and save the result.</summary>
[SimpleJob(RunStrategy.Monitoring, warmupCount: 2, iterationCount: 10)]
public class MergeComparison : ComparisonBase
{
    [Benchmark(Baseline = true), BenchmarkCategory(Engine.Pdfium)]
    public void Pdfium()
    {
        using var merger = new PdfMerger(Input);
        using var second = new PdfDocument(Input);
        merger.AppendDocument(second);
        merger.Save(Output("merged.pdf"));
    }

    /// <summary>
    /// Ghostscript has no page-import operation: <c>pdfwrite</c> interprets both inputs and writes
    /// a new PDF. That is more work than copying pages, and it is the only way Ghostscript merges.
    /// </summary>
    [Benchmark, BenchmarkCategory(Engine.Ghostscript)]
    public void Ghostscript()
    {
        RunGhostscript(GhostscriptBatch, new[] { "-sDEVICE=pdfwrite", "-sOutputFile=" + Output("merged.pdf"), Input, Input });
    }

    [Benchmark, BenchmarkCategory(Engine.Aspose)]
    public void Aspose()
    {
        using var first = new global::Aspose.Pdf.Document(Input);
        using var second = new global::Aspose.Pdf.Document(Input);
        first.Pages.Add(second.Pages);
        first.Save(Output("merged.pdf"));
    }
}

/// <summary>Extract the text of every page.</summary>
[SimpleJob(RunStrategy.Monitoring, warmupCount: 2, iterationCount: 10)]
public class TextComparison : ComparisonBase
{
    [Benchmark(Baseline = true), BenchmarkCategory(Engine.Pdfium)]
    public int Pdfium()
    {
        using var doc = new PdfDocument(Input);
        return doc.ProcessAllPages(page => page.ExtractText()).Sum(text => text.Length);
    }

    [Benchmark, BenchmarkCategory(Engine.Ghostscript)]
    public long Ghostscript()
    {
        RunGhostscript(GhostscriptBatch, new[] { "-sDEVICE=txtwrite", "-sOutputFile=" + Output("text.txt"), Input });
        return new FileInfo(Output("text.txt")).Length;
    }

    [Benchmark, BenchmarkCategory(Engine.Aspose)]
    public int Aspose()
    {
        using var doc = new global::Aspose.Pdf.Document(Input);
        var absorber = new global::Aspose.Pdf.Text.TextAbsorber();
        doc.Pages.Accept(absorber);
        return absorber.Text.Length;
    }
}

/// <summary>
/// What a Ghostscript process costs before it does any work. Every Ghostscript figure in the
/// other classes includes this once.
/// </summary>
[SimpleJob(RunStrategy.Monitoring, warmupCount: 2, iterationCount: 15)]
public class StartupBenchmark
{
    [Benchmark, BenchmarkCategory(Engine.Ghostscript)]
    public void GhostscriptProcessStartOnly()
    {
        Comparison.Ghostscript.Run("-q", "-dNODISPLAY", "-c", "quit");
    }
}
