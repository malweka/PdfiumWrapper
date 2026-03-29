using System.Reflection;

namespace PdfiumWrapper.Tests;

[Collection("PDF Tests")]
public class LifetimeCoordinationTests
{
    private const string ContractPdfPath = "Docs/contract.pdf";

    private static int GetActivePageCount(PdfDocument document)
    {
        var field = typeof(PdfDocument).GetField("_activePageCount", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (int)field.GetValue(document)!;
    }

    #region Page Tracking

    [Fact]
    public void GetPage_IncrementsActivePageCount()
    {
        using var doc = new PdfDocument(ContractPdfPath);
        Assert.Equal(0, GetActivePageCount(doc));

        var page = doc.GetPage(0);
        Assert.Equal(1, GetActivePageCount(doc));

        page.Dispose();
        Assert.Equal(0, GetActivePageCount(doc));
    }

    [Fact]
    public void MultiplePages_TrackedIndependently()
    {
        using var doc = new PdfDocument(ContractPdfPath);
        Assert.True(doc.PageCount >= 2, "Test requires a PDF with at least 2 pages");

        var page0 = doc.GetPage(0);
        var page1 = doc.GetPage(1);
        Assert.Equal(2, GetActivePageCount(doc));

        page0.Dispose();
        Assert.Equal(1, GetActivePageCount(doc));

        page1.Dispose();
        Assert.Equal(0, GetActivePageCount(doc));
    }

    [Fact]
    public void AddPage_IncrementsActivePageCount()
    {
        using var doc = new PdfDocument();
        Assert.Equal(0, GetActivePageCount(doc));

        var page = doc.AddPage();
        Assert.Equal(1, GetActivePageCount(doc));

        page.Dispose();
        Assert.Equal(0, GetActivePageCount(doc));
    }

    [Fact]
    public void ProcessAllPages_TracksAndReleasesPages()
    {
        using var doc = new PdfDocument(ContractPdfPath);

        doc.ProcessAllPages(page =>
        {
            // While processing, one page should be active
            Assert.Equal(1, GetActivePageCount(doc));
        });

        Assert.Equal(0, GetActivePageCount(doc));
    }

    #endregion

    #region Document Disposal Invalidates Pages

    [Fact]
    public void Page_ThrowsAfterDocumentDisposed()
    {
        PdfPage page;
        var doc = new PdfDocument(ContractPdfPath);
        page = doc.GetPage(0);
        doc.Dispose();

        // All public methods should throw ObjectDisposedException
        Assert.Throws<ObjectDisposedException>(() => _ = page.Width);
        Assert.Throws<ObjectDisposedException>(() => _ = page.Height);
        Assert.Throws<ObjectDisposedException>(() => page.ExtractText());
        Assert.Throws<ObjectDisposedException>(() => page.RenderToBytes(100, 100));
        Assert.Throws<ObjectDisposedException>(() => _ = page.HasEmbeddedThumbnail);
        Assert.Throws<ObjectDisposedException>(() => page.GetEmbeddedThumbnailBytes());
        Assert.Throws<ObjectDisposedException>(() => page.GetEmbeddedThumbnailSize());
        Assert.Throws<ObjectDisposedException>(() => page.GenerateContent());
        Assert.Throws<ObjectDisposedException>(() => _ = page.ObjectCount);
        Assert.Throws<ObjectDisposedException>(() => page.GetObject(0));

        // Dispose should still be safe (not throw)
        page.Dispose();
    }

    [Fact]
    public void Page_ThrowsAfterOwnDisposal()
    {
        using var doc = new PdfDocument(ContractPdfPath);
        var page = doc.GetPage(0);
        page.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = page.Width);
        Assert.Throws<ObjectDisposedException>(() => page.ExtractText());
    }

    [Fact]
    public void DocumentDispose_DoesNotThrow_WithActivePages()
    {
        var doc = new PdfDocument(ContractPdfPath);
        var page = doc.GetPage(0);

        // Per our policy: Dispose does not throw and disposes live pages first
        var ex = Record.Exception(() => doc.Dispose());
        Assert.Null(ex);

        // The page is now disposed by the document
        Assert.Throws<ObjectDisposedException>(() => _ = page.Width);
        Assert.Equal(0, GetActivePageCount(doc));

        // Page dispose is still safe
        page.Dispose();
    }

    [Fact]
    public void DocumentDispose_DisposesMultipleActivePages()
    {
        var doc = new PdfDocument(ContractPdfPath);
        Assert.True(doc.PageCount >= 2, "Test requires a PDF with at least 2 pages.");
        var page0 = doc.GetPage(0);
        var page1 = doc.GetPage(1);

        Assert.Equal(2, GetActivePageCount(doc));

        doc.Dispose();

        Assert.Equal(0, GetActivePageCount(doc));
        Assert.Throws<ObjectDisposedException>(() => _ = page0.Width);
        Assert.Throws<ObjectDisposedException>(() => _ = page1.Height);
    }

    [Fact]
    public void DocumentDispose_InvalidatesAttachedPageObjects()
    {
        using var doc = new PdfDocument();
        using var page = doc.AddPage();
        var text = page.AddText("Hello", 10, 10);

        doc.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = text.ObjectType);
        Assert.Throws<ObjectDisposedException>(() => text.GetBounds());
        Assert.Throws<ObjectDisposedException>(() => text.Transform(1, 0, 0, 1, 0, 0));
    }

    [Fact]
    public void GetPage_ThrowsAfterDocumentDisposed()
    {
        var doc = new PdfDocument(ContractPdfPath);
        doc.Dispose();

        Assert.Throws<ObjectDisposedException>(() => doc.GetPage(0));
    }

    #endregion

    #region Double Dispose Safety

    [Fact]
    public void Page_DoubleDispose_DoesNotDoubleDecrement()
    {
        using var doc = new PdfDocument(ContractPdfPath);
        var page = doc.GetPage(0);
        Assert.Equal(1, GetActivePageCount(doc));

        page.Dispose();
        Assert.Equal(0, GetActivePageCount(doc));

        // Second dispose should be a no-op
        page.Dispose();
        Assert.Equal(0, GetActivePageCount(doc));
    }

    [Fact]
    public void Document_DoubleDispose_IsSafe()
    {
        var doc = new PdfDocument(ContractPdfPath);
        doc.Dispose();

        var ex = Record.Exception(() => doc.Dispose());
        Assert.Null(ex);
    }

    #endregion

    #region Loop Stress Test

    [Fact]
    public void RepeatedGetPageDispose_DoesNotLeakTracking()
    {
        using var doc = new PdfDocument(ContractPdfPath);

        for (int i = 0; i < 100; i++)
        {
            using var page = doc.GetPage(0);
            Assert.Equal(1, GetActivePageCount(doc));
            _ = page.Width;
        }

        Assert.Equal(0, GetActivePageCount(doc));
    }

    #endregion
}
