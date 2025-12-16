using System.Drawing;
﻿using Xunit;

namespace Malweka.PdfiumSdk.Tests;

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
