using Xunit;

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
}
