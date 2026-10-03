using System.Drawing;
﻿using Xunit;

namespace PdfiumWrapper.Tests;

[Collection("PDF Tests")]
public class PdfPageEditingTests : IDisposable
{
    private const string ContractPdfPath = "Docs/contract.pdf";
    private readonly List<string> _tempDirectories = new();

    public void Dispose()
    {
        // Clean up any temp directories created during tests
        foreach (var dir in _tempDirectories)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    private string CreateTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"PdfiumTests_Editing_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        _tempDirectories.Add(tempDir);
        return tempDir;
    }

    #region Page Object Tests

    [Fact]
    public void GetMatrix_AfterSetMatrix_ShouldReturnTheSameValues()
    {
        // Arrange
        using var doc = new PdfDocument();
        using var page = doc.AddPage();
        var rectangle = page.AddRectangle(10, 20, 30, 40);

        // Act
        rectangle.SetMatrix(2, 0, 0, 3, 50, 60);
        var matrix = rectangle.GetMatrix();

        // Assert
        Assert.Equal((2d, 0d, 0d, 3d, 50d, 60d), matrix);
    }

    [Fact]
    public void RemoveObject_ThenDisposeObject_ShouldReleaseItOnce()
    {
        // Arrange
        using var doc = new PdfDocument();
        using var page = doc.AddPage();
        var rectangle = page.AddRectangle(10, 20, 30, 40);
        long liveBefore = PdfiumRuntime.LiveHandleCount;

        // Act: the caller owns the object again after removal
        Assert.True(page.RemoveObject(rectangle));
        Assert.Equal(liveBefore + 1, PdfiumRuntime.LiveHandleCount);
        _ = rectangle.GetBounds();
        rectangle.Dispose();

        // Assert
        Assert.Equal(liveBefore, PdfiumRuntime.LiveHandleCount);
        Assert.Throws<ObjectDisposedException>(() => rectangle.GetBounds());
    }

    [Fact]
    public void RemoveObject_ThenDisposeDocument_ShouldReleaseTheDetachedObject()
    {
        // Arrange
        long liveBefore = PdfiumRuntime.LiveHandleCount;
        var doc = new PdfDocument();
        var page = doc.AddPage();
        var rectangle = page.AddRectangle(10, 20, 30, 40);
        Assert.True(page.RemoveObject(rectangle));

        // Act: the document destroys what was removed from its pages but never disposed
        doc.Dispose();

        // Assert
        Assert.Equal(liveBefore, PdfiumRuntime.LiveHandleCount);
        Assert.Throws<ObjectDisposedException>(() => rectangle.GetBounds());
    }

    [Fact]
    public void ImageObject_GetBitmap_ReturnsManagedBgraPixels_AndLeavesNoNativeBitmapBehind()
    {
        // Arrange: a PNG of known size
        byte[] png;
        RawBitmap source;
        using (var sourceDoc = new PdfDocument(ContractPdfPath))
        {
            source = sourceDoc.RenderPages(20)[0];
            png = sourceDoc.StreamImageBytes(ImageFormat.Png, 100, 20).First();
        }

        long liveBaseline = PdfiumRuntime.LiveHandleCount;
        var doc = new PdfDocument();
        var page = doc.AddPage();
        var image = page.AddImage(png, 50, 50, 200, 260);
        page.GenerateContent();
        long liveBefore = PdfiumRuntime.LiveHandleCount;

        // Act
        RawBitmap? bitmap = image.GetBitmap();
        RawBitmap? rendered = image.GetRenderedBitmap(page);
        RawBitmap? renderedWithoutPage = image.GetRenderedBitmap();

        // Assert: pixels are managed, BGRA, and sized like the image
        Assert.NotNull(bitmap);
        Assert.Equal(source.Width, bitmap!.Width);
        Assert.Equal(source.Height, bitmap.Height);
        Assert.Equal(bitmap.Width * 4, bitmap.Stride);
        Assert.Equal(bitmap.Stride * bitmap.Height, bitmap.Pixels.Length);
        Assert.Contains(bitmap.Pixels, b => b != 0);

        Assert.NotNull(rendered);
        Assert.Equal(rendered!.Stride * rendered.Height, rendered.Pixels.Length);
        Assert.NotNull(renderedWithoutPage);

        // Nothing native is left for the caller to release, and the accounting agrees
        Assert.Equal(liveBefore, PdfiumRuntime.LiveHandleCount);

        doc.Dispose();
        Assert.Equal(liveBaseline, PdfiumRuntime.LiveHandleCount);
        Assert.Throws<ObjectDisposedException>(() => image.GetBitmap());
    }

    [Fact]
    public void AddImage_JpegWithOversizedHeader_ThrowsBeforeDecoding()
    {
        // Arrange: a real JPEG whose SOF0 header claims 32768 x 32769. In 32-bit arithmetic the BGRA
        // buffer size wraps to 128 KB, and the decoder would write 4 GB into it.
        byte[] jpeg;
        using (var sourceDoc = new PdfDocument(ContractPdfPath))
            jpeg = sourceDoc.StreamImageBytes(ImageFormat.Jpeg, 90, 20).First();

        int sof = -1;
        for (int i = 0; i < jpeg.Length - 1; i++)
        {
            if (jpeg[i] == 0xFF && jpeg[i + 1] == 0xC0)
            {
                sof = i;
                break;
            }
        }

        Assert.True(sof > 0, "the encoder writes a baseline SOF0 marker");
        jpeg[sof + 5] = 0x80; // height 32769
        jpeg[sof + 6] = 0x01;
        jpeg[sof + 7] = 0x80; // width 32768
        jpeg[sof + 8] = 0x00;

        using var doc = new PdfDocument();
        using var page = doc.AddPage();
        long liveBefore = PdfiumRuntime.LiveHandleCount;
        int objectsBefore = page.ObjectCount;

        // Act / Assert
        Assert.Throws<InvalidDataException>(() => page.AddImage(jpeg, 0, 0, 100, 100));
        Assert.Equal(objectsBefore, page.ObjectCount);
        Assert.Equal(liveBefore, PdfiumRuntime.LiveHandleCount);

        // 20000 x 20000 fits an array (1.6 GB) but not the render limit: rejected before allocating
        jpeg[sof + 5] = 0x4E; // height 20000
        jpeg[sof + 6] = 0x20;
        jpeg[sof + 7] = 0x4E; // width 20000
        jpeg[sof + 8] = 0x20;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var limit = Assert.Throws<InvalidOperationException>(() => page.AddImage(jpeg, 0, 0, 100, 100));
        Assert.Contains(RenderLimits.MaxPixelsKey, limit.Message);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore < 16 * 1024 * 1024);
        Assert.Equal(objectsBefore, page.ObjectCount);
        Assert.Equal(liveBefore, PdfiumRuntime.LiveHandleCount);
    }

    #endregion

    #region AddPage Tests

    [Fact]
    public void AddPage_ToNewDocument_ShouldIncreasePageCount()
    {
        // Arrange
        using var doc = new PdfDocument();
        int initialCount = doc.PageCount;

        // Act
        doc.AddPage();

        // Assert
        Assert.Equal(initialCount + 1, doc.PageCount);
    }

    [Fact]
    public void AddPage_WithCustomSize_ShouldCreatePageWithCorrectSize()
    {
        // Arrange
        using var doc = new PdfDocument();
        int width = 500;
        int height = 800;

        // Act
        doc.AddPage(width, height);
        var pageSize = doc.GetPageSize(0);

        // Assert
        Assert.Equal(width, pageSize.width);
        Assert.Equal(height, pageSize.height);
    }

    [Fact]
    public void AddPage_AtIndex_ShouldInsertPageAtCorrectLocation()
    {
        // Arrange
        // Create a doc with 2 pages
        using var doc = new PdfDocument();
        doc.AddPage(100, 100); // Page 0
        doc.AddPage(200, 200); // Page 1 (now)

        // Act
        // Insert at index 1 (between 0 and 1)
        doc.AddPage(150, 150, 1);

        // Assert
        Assert.Equal(3, doc.PageCount);

        var size0 = doc.GetPageSize(0);
        var size1 = doc.GetPageSize(1);
        var size2 = doc.GetPageSize(2);

        Assert.Equal(100, size0.width);
        Assert.Equal(150, size1.width);
        Assert.Equal(200, size2.width);
    }

    #endregion

    #region DeletePage Tests

    [Fact]
    public void DeletePage_ByIndex_ShouldDecreasePageCount()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        int initialCount = doc.PageCount;
        Assert.True(initialCount >= 2, "Test requires at least 2 pages");

        // Act
        doc.DeletePage(0);

        // Assert
        Assert.Equal(initialCount - 1, doc.PageCount);
    }

    [Fact]
    public void DeletePage_ByObject_ShouldDecreasePageCount()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        int initialCount = doc.PageCount;
        Assert.True(initialCount >= 2, "Test requires at least 2 pages");
        using var page = doc.GetPage(0);

        // Act
        doc.DeletePage(page);

        // Assert
        Assert.Equal(initialCount - 1, doc.PageCount);
    }

    [Fact]
    public void DeletePage_InvalidIndex_ShouldThrowException()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.DeletePage(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.DeletePage(doc.PageCount));
    }

    #endregion

    #region New Document Save Tests

    [Fact]
    public void CreateNewDocument_AddPage_Save_ShouldCreateValidPdf()
    {
        // Arrange
        using var doc = new PdfDocument();
        doc.AddPage();
        var tempDir = CreateTempDirectory();
        var outputPath = Path.Combine(tempDir, "new_doc.pdf");

        // Act
        doc.Save(outputPath);

        // Assert
        Assert.True(File.Exists(outputPath));

        // Verify we can load it back
        using var loadedDoc = new PdfDocument(outputPath);
        Assert.Equal(1, loadedDoc.PageCount);
    }

    #endregion

    #region Page Content Editing Tests

    [Fact]
    public void FontSize_Set_IsWrittenToTheDocument()
    {
        // Arrange / Act: the same text added at 12 pt and resized to 24, added at 24, and left at 12
        static byte[] Render(float addedAt, float? resizeTo)
        {
            using var stream = new MemoryStream();
            using (var doc = new PdfDocument())
            using (var page = doc.AddPage(300, 100))
            {
                var text = page.AddText("Hello World", 10, 40, fontSize: addedAt);
                if (resizeTo is float size)
                {
                    text.FontSize = size;
                    Assert.Equal(size, text.FontSize);
                    Assert.Throws<ArgumentOutOfRangeException>(() => text.FontSize = -1);
                }

                page.GenerateContent();
                doc.SaveToStream(stream);
            }

            using var saved = new PdfDocument(stream.ToArray());
            return saved.RenderPages(72)[0].Pixels;
        }

        var resized = Render(12, 24);
        var added = Render(24, null);
        var unchanged = Render(12, null);

        // Assert: the size is applied to the object, not only remembered
        Assert.Equal(added, resized);
        Assert.NotEqual(unchanged, resized);
    }

    [Fact]
    public void TextObject_OutlivingItsDocument_CanStillBeDisposed()
    {
        // Arrange: the documented pattern, where the text object is never disposed explicitly
        var doc = new PdfDocument();
        var page = doc.AddPage();
        var text = page.AddText("Hello", 50, 500, "Times-Roman", 14);
        Assert.Equal(14f, text.FontSize);

        // Act
        doc.Dispose();

        // Assert
        Assert.Throws<ObjectDisposedException>(() => text.FontSize);
        text.Dispose();
    }

    [Fact]
    public void AddText_ShouldIncreaseObjectCount()
    {
        // Arrange
        using var doc = new PdfDocument();
        using var page = doc.AddPage();
        int initialObjects = page.ObjectCount;

        // Act
        page.AddText("Hello World", 100, 100);
        page.GenerateContent();

        // Assert
        Assert.Equal(initialObjects + 1, page.ObjectCount);
    }

    [Fact]
    public void AddRectangle_ShouldIncreaseObjectCount()
    {
        // Arrange
        using var doc = new PdfDocument();
        using var page = doc.AddPage();
        int initialObjects = page.ObjectCount;

        // Act
        page.AddRectangle(50, 50, 200, 100, Color.Red, Color.Black);
        page.GenerateContent();

        // Assert
        Assert.Equal(initialObjects + 1, page.ObjectCount);
    }

    [Fact]
    public void AddPath_ShouldIncreaseObjectCount()
    {
        // Arrange
        using var doc = new PdfDocument();
        using var page = doc.AddPage();
        int initialObjects = page.ObjectCount;

        // Act
        var path = page.AddPath();
        // Just adding a path object should increase count even if empty
        // But usually we would add segments. The SDK method just creates and inserts it.
        page.GenerateContent();

        // Assert
        Assert.Equal(initialObjects + 1, page.ObjectCount);
    }

    [Fact]
    public void RemoveObject_ShouldDecreaseObjectCount()
    {
        // Arrange
        using var doc = new PdfDocument();
        using var page = doc.AddPage();
        var textObj = page.AddText("To be removed", 100, 100);
        page.GenerateContent();
        int countAfterAdd = page.ObjectCount;

        // Act
        page.RemoveObject(textObj);
        page.GenerateContent();

        // Assert
        Assert.Equal(countAfterAdd - 1, page.ObjectCount);
    }

    [Fact]
    public void AddImage_ShouldIncreaseObjectCount()
    {
        // Arrange
        using var doc = new PdfDocument();
        using var page = doc.AddPage();
        int initialObjects = page.ObjectCount;

        // Create a simple 1x1 bitmap byte array (fake image data)
        // In a real scenario we would need valid image bytes.
        // For unit test without external dependencies, we might need a small valid image resource.
        // However, looking at PdfPage.AddImage implementation, it calls PDFium functions.
        // If we pass invalid bytes, it might fail or crash if PDFium validates it immediately.

        // Let's try to create a small valid BMP or similar if possible.
        // Or check if there is a helper to get sample image.
        // We can use a 1x1 pixel PNG represented as bytes.
        byte[] pngBytes = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53,
            0xDE, 0x00, 0x00, 0x00, 0x01, 0x73, 0x52, 0x47, 0x42, 0x00, 0xAE, 0xCE, 0x1C, 0xE9, 0x00, 0x00,
            0x00, 0x04, 0x67, 0x41, 0x4D, 0x41, 0x00, 0x00, 0xB1, 0x8F, 0x0B, 0xFC, 0x61, 0x05, 0x00, 0x00,
            0x00, 0x09, 0x70, 0x48, 0x59, 0x73, 0x00, 0x00, 0x0E, 0xC3, 0x00, 0x00, 0x0E, 0xC3, 0x01, 0xC7,
            0x6F, 0xA8, 0x64, 0x00, 0x00, 0x00, 0x0C, 0x49, 0x44, 0x41, 0x54, 0x18, 0x57, 0x63, 0xF8, 0xFF,
            0xFF, 0x3F, 0x00, 0x05, 0xFE, 0x02, 0xFE, 0xA7, 0x35, 0x81, 0x84, 0x00, 0x00, 0x00, 0x00, 0x49,
            0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82
        };

        // Act
        page.AddImage(pngBytes, 50, 50, 100, 100);
        page.GenerateContent();

        // Assert
        Assert.Equal(initialObjects + 1, page.ObjectCount);
    }

    [Fact]
    public void Page_Edit_And_Save_ShouldPersistChanges()
    {
        // Arrange
        using var doc = new PdfDocument();
        using var page = doc.AddPage();
        page.AddText("Persistent Text", 100, 100);
        page.GenerateContent();

        var tempDir = CreateTempDirectory();
        var outputPath = Path.Combine(tempDir, "edited_doc.pdf");

        // Act
        doc.Save(outputPath);

        // Assert
        Assert.True(File.Exists(outputPath));

        // Verify content persists
        using var loadedDoc = new PdfDocument(outputPath);
        using var loadedPage = loadedDoc.GetPage(0);
        Assert.Equal(1, loadedPage.ObjectCount);
    }

    #endregion
}
