using BenchmarkDotNet.Attributes;

namespace PdfiumWrapper.Benchmarks;

public abstract class BenchmarkBase
{
    private static readonly string DocsDirectory = Path.Combine(AppContext.BaseDirectory, "Docs");

    protected string OutputDirectory = null!;

    public static IEnumerable<PdfTestDocument> TestDocuments => new[]
    {
        new PdfTestDocument("doc-1-page.pdf", 1),
        new PdfTestDocument("doc-3-pages-with-comments.pdf", 3),
        new PdfTestDocument("contract.pdf", 10),
        new PdfTestDocument("fw2.pdf", 11),
        new PdfTestDocument("presentation.pdf", 30),
    };

    [ParamsSource(nameof(TestDocuments))]
    public PdfTestDocument Document { get; set; } = null!;

    [GlobalSetup]
    public void Setup()
    {
        OutputDirectory = Path.Combine(Path.GetTempPath(), "PdfiumBenchmarks", GetType().Name);
        Directory.CreateDirectory(OutputDirectory);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(OutputDirectory))
        {
            Directory.Delete(OutputDirectory, recursive: true);
        }
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        // Clean output files between iterations so disk usage doesn't accumulate
        if (Directory.Exists(OutputDirectory))
        {
            foreach (var file in Directory.GetFiles(OutputDirectory))
            {
                File.Delete(file);
            }
        }
    }

    protected string GetDocPath() => Path.GetFullPath(Path.Combine(DocsDirectory, Document.FileName));
}

public class PdfTestDocument
{
    public string FileName { get; }
    public int Pages { get; }

    public PdfTestDocument(string fileName, int pages)
    {
        FileName = fileName;
        Pages = pages;
    }

    public override string ToString() => $"{FileName} ({Pages}p)";
}
