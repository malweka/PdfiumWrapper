using System.Security.Cryptography;

namespace PdfiumWrapper.Qualification;

internal enum Op { PageCount, Png, Jpeg, Tiff, Text }

internal enum InputKind { File, Bytes, Stream }

/// <summary>A test document. An invalid one must end Failed and leave no output.</summary>
internal sealed record Doc(string Name, string Path, bool Valid)
{
    public byte[] Bytes { get; } = File.ReadAllBytes(Path);
}

/// <summary>The results of the same calls made in this process, which every pool result must match.</summary>
internal sealed record Reference(int PageCount, string[] PngHashes, string[] JpegHashes, string TiffHash, string[] Text);

internal static class Corpus
{
    public const int PngDpi = 100;
    public const int JpegDpi = 100;
    public const int JpegQuality = 85;
    public const int TiffDpi = 150;

    private static readonly string[] s_good =
    [
        "contract.pdf", "doc-1-page.pdf", "doc-3-pages-with-comments.pdf", "fw2.pdf", "presentation.pdf",
    ];

    public static (Doc[] Good, Doc Encrypted, Doc Corrupt) Load(string workDirectory)
    {
        string docs = System.IO.Path.Combine(AppContext.BaseDirectory, "Docs");
        var good = s_good.Select(n => new Doc(n, System.IO.Path.Combine(docs, n), true)).ToArray();

        // Opened without its password: Failed (Password).
        var encrypted = new Doc("encrypted.pdf", System.IO.Path.Combine(docs, "encrypted.pdf"), false);

        // A PDF header followed by noise: Failed (Format). Checked below so the run never depends on
        // PDFium recovering something from it.
        string corruptPath = System.IO.Path.Combine(workDirectory, "corrupt.pdf");
        var noise = new byte[8192];
        new Random(7).NextBytes(noise);
        File.WriteAllBytes(corruptPath, [.. "%PDF-1.7\n"u8, .. noise]);
        var corrupt = new Doc("corrupt.pdf", corruptPath, false);

        foreach (var bad in new[] { encrypted, corrupt })
        {
            try
            {
                using var _ = new PdfDocument(bad.Path);
                throw new InvalidOperationException($"{bad.Name} opened in process; it cannot serve as a failing input.");
            }
            catch (PdfiumException)
            {
            }
        }

        return (good, encrypted, corrupt);
    }

    public static Reference BuildReference(Doc doc, string directory)
    {
        string png = System.IO.Path.Combine(directory, "png");
        string jpg = System.IO.Path.Combine(directory, "jpg");
        string tiff = System.IO.Path.Combine(directory, "out.tiff");
        Directory.CreateDirectory(directory);

        using var document = new PdfDocument(doc.Path);
        document.SaveAsPngs(png, "page", dpi: PngDpi);
        document.SaveAsJpegs(jpg, "page", JpegQuality, JpegDpi);
        document.SaveAsTiff(tiff, TiffDpi);
        string[] text = document.ProcessAllPages(p => p.ExtractText());

        var reference = new Reference(
            document.PageCount,
            HashAll(Directory.GetFiles(png)),
            HashAll(Directory.GetFiles(jpg)),
            Sha256(tiff),
            text);
        Directory.Delete(directory, true);
        return reference;
    }

    public static string[] HashAll(IEnumerable<string> files) =>
        files.Order(StringComparer.Ordinal).Select(Sha256).ToArray();

    public static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
