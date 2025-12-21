using SkiaSharp;

namespace Malweka.PdfiumSdk.Tests;

[Collection("PDF Tests")]
public class PdfDocumentTests : IDisposable
{
    private const string ContractPdfPath = "Docs/contract.pdf";
    private const string PresentationPdfPath = "Docs/presentation.pdf";
    
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
        var tempDir = Path.Combine(Path.GetTempPath(), $"PdfiumTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        _tempDirectories.Add(tempDir);
        return tempDir;
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_Empty_ShouldCreateNewDocument()
    {
        // Arrange & Act
        using var doc = new PdfDocument();

        // Assert
        Assert.NotNull(doc);
        Assert.Equal(0, doc.PageCount);
    }

    [Fact]
    public void Constructor_WithValidFilePath_ShouldLoadDocument()
    {
        // Arrange & Act
        using var doc = new PdfDocument(ContractPdfPath);
        
        // Assert
        Assert.NotNull(doc);
        Assert.True(doc.PageCount > 0);
    }
    
    [Fact]
    public void Constructor_WithInvalidFilePath_ShouldThrowException()
    {
        // Arrange & Act & Assert
        Assert.Throws<InvalidOperationException>(() => new PdfDocument("nonexistent.pdf"));
    }
    
    [Fact]
    public void Constructor_WithStream_ShouldLoadDocument()
    {
        // Arrange
        using var stream = File.OpenRead(ContractPdfPath);
        
        // Act
        using var doc = new PdfDocument(stream);
        
        // Assert
        Assert.NotNull(doc);
        Assert.True(doc.PageCount > 0);
    }
    
    [Fact]
    public void Constructor_WithByteArray_ShouldLoadDocument()
    {
        // Arrange
        var bytes = File.ReadAllBytes(ContractPdfPath);
        
        // Act
        using var doc = new PdfDocument(bytes);
        
        // Assert
        Assert.NotNull(doc);
        Assert.True(doc.PageCount > 0);
    }
    
    [Fact]
    public void Constructor_WithInvalidByteArray_ShouldThrowException()
    {
        // Arrange
        var invalidBytes = new byte[] { 1, 2, 3, 4, 5 };
        
        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => new PdfDocument(invalidBytes));
    }
    
    #endregion
    
    #region Page Count and Access Tests
    
    [Fact]
    public void PageCount_ShouldReturnCorrectNumber()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        
        // Act
        var pageCount = doc.PageCount;
        
        // Assert
        Assert.True(pageCount > 0);
    }
    
    [Fact]
    public void GetPage_WithValidIndex_ShouldReturnPage()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        
        // Act
        using var page = doc.GetPage(0);
        
        // Assert
        Assert.NotNull(page);
        Assert.Equal(0, page.PageIndex);
        Assert.True(page.Width > 0);
        Assert.True(page.Height > 0);
    }
    
    [Fact]
    public void GetPage_WithInvalidIndex_ShouldThrowException()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.GetPage(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.GetPage(doc.PageCount));
    }
    
    [Fact]
    public void GetAllPages_ShouldReturnAllPages()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        
        // Act
        var pages = doc.GetAllPages();
        
        // Assert
        Assert.NotNull(pages);
        Assert.Equal(doc.PageCount, pages.Length);
        
        // Clean up
        foreach (var page in pages)
        {
            page.Dispose();
        }
    }
    
    #endregion
    
    #region Page Size Tests
    
    [Fact]
    public void GetPageSize_WithValidIndex_ShouldReturnSize()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        
        // Act
        var (width, height) = doc.GetPageSize(0);
        
        // Assert
        Assert.True(width > 0);
        Assert.True(height > 0);
    }
    
    [Fact]
    public void GetPageSize_WithInvalidIndex_ShouldThrowException()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.GetPageSize(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.GetPageSize(doc.PageCount));
    }
    
    [Fact]
    public void GetAllPageSizes_ShouldReturnAllSizes()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        
        // Act
        var sizes = doc.GetAllPageSizes();
        
        // Assert
        Assert.NotNull(sizes);
        Assert.Equal(doc.PageCount, sizes.Length);
        Assert.All(sizes, size =>
        {
            Assert.True(size.width > 0);
            Assert.True(size.height > 0);
        });
    }
    
    #endregion
    
    #region Bitmap Conversion Tests
    
    [Fact]
    public void ConvertToBitmaps_ShouldReturnBitmapsForAllPages()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        
        // Act
        var bitmaps = doc.ConvertToBitmaps(dpi: 72); // Low DPI for faster test
        
        // Assert
        Assert.NotNull(bitmaps);
        Assert.Equal(doc.PageCount, bitmaps.Length);
        Assert.All(bitmaps, bitmap =>
        {
            Assert.NotNull(bitmap);
            Assert.True(bitmap.Width > 0);
            Assert.True(bitmap.Height > 0);
        });
        
        // Clean up
        foreach (var bitmap in bitmaps)
        {
            bitmap.Dispose();
        }
    }
    
    [Fact]
    public void ConvertToBitmaps_WithCustomDpi_ShouldScaleCorrectly()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        
        // Act
        var bitmaps72 = doc.ConvertToBitmaps(dpi: 72);
        var bitmaps144 = doc.ConvertToBitmaps(dpi: 144);
        
        // Assert
        Assert.NotNull(bitmaps72);
        Assert.NotNull(bitmaps144);
        
        // Higher DPI should result in larger bitmaps (approximately 2x)
        Assert.True(bitmaps144[0].Width > bitmaps72[0].Width);
        Assert.True(bitmaps144[0].Height > bitmaps72[0].Height);
        
        // Clean up
        foreach (var bitmap in bitmaps72)
        {
            bitmap.Dispose();
        }
        foreach (var bitmap in bitmaps144)
        {
            bitmap.Dispose();
        }
    }
    
    [Fact]
    public async Task ConvertToBitmapsAsync_ShouldReturnBitmapsForAllPages()
    {
        // Arrange
        using var doc = new PdfDocument(PresentationPdfPath);
        
        // Act
        var bitmaps = await doc.ConvertToBitmapsAsync(dpi: 72); // Low DPI for faster test
        
        // Assert
        Assert.NotNull(bitmaps);
        Assert.Equal(doc.PageCount, bitmaps.Length);
        Assert.All(bitmaps, bitmap =>
        {
            Assert.NotNull(bitmap);
            Assert.True(bitmap.Width > 0);
            Assert.True(bitmap.Height > 0);
        });
        
        // Clean up
        foreach (var bitmap in bitmaps)
        {
            bitmap.Dispose();
        }
    }
    
    #endregion
    
    #region Image Conversion Tests
    
    [Fact]
    public void ConvertToImageBytes_Png_ShouldReturnValidImages()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        
        // Act
        var images = doc.ConvertToImageBytes(SKEncodedImageFormat.Png, quality: 100, dpi: 72);
        
        // Assert
        Assert.NotNull(images);
        Assert.Equal(doc.PageCount, images.Count);
        Assert.All(images, imageBytes =>
        {
            Assert.NotNull(imageBytes);
            Assert.True(imageBytes.Length > 0);
        });
    }
    
    [Fact]
    public void ConvertToImageBytes_Jpeg_ShouldReturnValidImages()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        
        // Act
        var images = doc.ConvertToImageBytes(SKEncodedImageFormat.Jpeg, quality: 90, dpi: 72);
        
        // Assert
        Assert.NotNull(images);
        Assert.Equal(doc.PageCount, images.Count);
        Assert.All(images, imageBytes =>
        {
            Assert.NotNull(imageBytes);
            Assert.True(imageBytes.Length > 0);
        });
    }
    
    [Fact]
    public async Task ConvertToImageBytesAsync_ShouldReturnValidImages()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        
        // Act
        var images = await doc.ConvertToImageBytesAsync(SKEncodedImageFormat.Png, quality: 100, dpi: 72);
        
        // Assert
        Assert.NotNull(images);
        Assert.Equal(doc.PageCount, images.Count);
        Assert.All(images, imageBytes =>
        {
            Assert.NotNull(imageBytes);
            Assert.True(imageBytes.Length > 0);
        });
    }
    
    #endregion
    
    #region Save Image Tests
    
    [Fact]
    public void SaveAsPngs_ShouldCreatePngFiles()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        var outputDir = CreateTempDirectory();
        
        // Act
        doc.SaveAsPngs(outputDir, "test_page", dpi: 72);
        
        // Assert
        var files = Directory.GetFiles(outputDir, "*.png");
        Assert.Equal(doc.PageCount, files.Length);
        Assert.All(files, file => Assert.True(new FileInfo(file).Length > 0));
    }
    
    [Fact]
    public void SaveAsJpegs_ShouldCreateJpegFiles()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        var outputDir = CreateTempDirectory();
        
        // Act
        doc.SaveAsJpegs(outputDir, "test_page", quality: 90, dpi: 72);
        
        // Assert
        var files = Directory.GetFiles(outputDir, "*.jpg");
        Assert.Equal(doc.PageCount, files.Length);
        Assert.All(files, file => Assert.True(new FileInfo(file).Length > 0));
    }
    
    [Fact]
    public void SaveAsImages_ShouldCreateFilesWithCorrectNaming()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        var outputDir = CreateTempDirectory();
        
        // Act
        doc.SaveAsImages(outputDir, "custom_prefix", SKEncodedImageFormat.Png, quality: 100, dpi: 72);
        
        // Assert
        var files = Directory.GetFiles(outputDir, "custom_prefix_*.png");
        Assert.Equal(doc.PageCount, files.Length);
        
        // Check that files are numbered correctly
        for (int i = 0; i < doc.PageCount; i++)
        {
            var expectedFile = Path.Combine(outputDir, $"custom_prefix_{i + 1:D3}.png");
            Assert.True(File.Exists(expectedFile));
        }
    }
    
    [Fact]
    public async Task SaveAsImagesAsync_ShouldCreateFiles()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        var outputDir = CreateTempDirectory();
        
        // Act
        await doc.SaveAsImagesAsync(outputDir, "async_page", SKEncodedImageFormat.Png, quality: 100, dpiWidth: 72, dpiHeight: 72);
        
        // Assert
        var files = Directory.GetFiles(outputDir, "*.png");
        Assert.Equal(doc.PageCount, files.Length);
        Assert.All(files, file => Assert.True(new FileInfo(file).Length > 0));
    }
    
    [Fact]
    public void SaveAsImages_ToStreams_ShouldWriteToAllStreams()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        var streams = new MemoryStream[doc.PageCount];
        for (int i = 0; i < doc.PageCount; i++)
        {
            streams[i] = new MemoryStream();
        }
        
        try
        {
            // Act
            doc.SaveAsImages(streams, SKEncodedImageFormat.Png, quality: 100, dpiWidth: 72, dpiHeight: 72);
            
            // Assert
            Assert.All(streams, stream =>
            {
                Assert.True(stream.Length > 0);
            });
        }
        finally
        {
            // Clean up
            foreach (var stream in streams)
            {
                stream.Dispose();
            }
        }
    }
    
    [Fact]
    public void SaveAsImages_ToStreams_WithIncorrectCount_ShouldThrowException()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        var streams = new MemoryStream[doc.PageCount - 1]; // Wrong count
        
        // Act & Assert
        Assert.Throws<ArgumentException>(() => 
            doc.SaveAsImages(streams, SKEncodedImageFormat.Png, quality: 100, dpiWidth: 72, dpiHeight: 72));
    }
    
    #endregion
    
    #region Metadata Tests
    
    [Fact]
    public void Metadata_ShouldReturnMetadataObject()
    {
        // Arrange & Act
        using var doc = new PdfDocument(ContractPdfPath);
        
        // Assert
        Assert.NotNull(doc.Metadata);
    }
    
    [Fact]
    public void Metadata_ShouldReturnSameInstance()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        
        // Act
        var metadata1 = doc.Metadata;
        var metadata2 = doc.Metadata;
        
        // Assert
        Assert.Same(metadata1, metadata2);
    }
    
    #endregion
    
    #region Bookmarks Tests
    
    [Fact]
    public void Bookmarks_ShouldReturnBookmarksObject()
    {
        // Arrange & Act
        using var doc = new PdfDocument(ContractPdfPath);
        
        // Assert
        Assert.NotNull(doc.Bookmarks);
    }
    
    [Fact]
    public void Bookmarks_ShouldReturnSameInstance()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        
        // Act
        var bookmarks1 = doc.Bookmarks;
        var bookmarks2 = doc.Bookmarks;
        
        // Assert
        Assert.Same(bookmarks1, bookmarks2);
    }
    
    #endregion
    
    #region Attachments Tests
    
    [Fact]
    public void Attachments_ShouldReturnAttachmentsObject()
    {
        // Arrange & Act
        using var doc = new PdfDocument(ContractPdfPath);
        
        // Assert
        Assert.NotNull(doc.Attachments);
    }
    
    [Fact]
    public void Attachments_ShouldReturnSameInstance()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        
        // Act
        var attachments1 = doc.Attachments;
        var attachments2 = doc.Attachments;
        
        // Assert
        Assert.Same(attachments1, attachments2);
    }
    
    #endregion
    
    #region Permissions Tests
    
    [Fact]
    public void Permissions_ShouldReturnValue()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        
        // Act
        var permissions = doc.Permissions;
        
        // Assert - Contract PDF has all permissions (4294967295 = 0xFFFFFFFF)
        Assert.Equal((PdfPermissions)4294967295, permissions); // Should equal the uint max value
        
        // Verify all permission flags are set
        Assert.True(permissions.HasFlag(PdfPermissions.Print));
        Assert.True(permissions.HasFlag(PdfPermissions.ModifyContents));
        Assert.True(permissions.HasFlag(PdfPermissions.CopyContents));
        Assert.True(permissions.HasFlag(PdfPermissions.ModifyAnnotations));
        Assert.True(permissions.HasFlag(PdfPermissions.FillForms));
        Assert.True(permissions.HasFlag(PdfPermissions.ExtractForAccessibility));
        Assert.True(permissions.HasFlag(PdfPermissions.AssembleDocument));
        Assert.True(permissions.HasFlag(PdfPermissions.PrintHighQuality));
    }
    
    #endregion
    
    
    #region Disposal Tests
    
    [Fact]
    public void Dispose_ShouldNotThrow()
    {
        // Arrange
        var doc = new PdfDocument(ContractPdfPath);
        
        // Act & Assert
        doc.Dispose();
    }
    
    [Fact]
    public void Dispose_CalledMultipleTimes_ShouldNotThrow()
    {
        // Arrange
        var doc = new PdfDocument(ContractPdfPath);
        
        // Act & Assert
        doc.Dispose();
        doc.Dispose();
        doc.Dispose();
    }
    
    #endregion
    
    #region Edge Case Tests
    
    [Fact]
    public void MultipleDocuments_ShouldWorkConcurrently()
    {
        // Arrange & Act
        using var doc1 = new PdfDocument(ContractPdfPath);
        using var doc2 = new PdfDocument(PresentationPdfPath);
        
        // Assert
        Assert.NotNull(doc1);
        Assert.NotNull(doc2);
        Assert.True(doc1.PageCount > 0);
        Assert.True(doc2.PageCount > 0);
        
        // Should be able to access both simultaneously
        using var page1 = doc1.GetPage(0);
        using var page2 = doc2.GetPage(0);
        
        Assert.NotNull(page1);
        Assert.NotNull(page2);
    }
    
    [Fact]
    public void CreateNewDocument_ComplexScenario_ShouldWork()
    {
        // Arrange
        using var doc = new PdfDocument();

        // Act - Add multiple pages with different sizes
        doc.AddPage(612, 792); // US Letter
        doc.AddPage(595, 842); // A4
        doc.AddPage(200, 200); // Small square

        // Act - Add content to the first page
        using (var page = doc.GetPage(0))
        {
            page.AddText("Hello World", 100, 700);
            page.AddRectangle(50, 50, 200, 100, System.Drawing.Color.Blue, System.Drawing.Color.Black);
            page.GenerateContent();
        }

        // Save to a temp file
        var tempDir = CreateTempDirectory();
        var outputPath = Path.Combine(tempDir, "complex_test.pdf");
        doc.Save(outputPath);

        // Assert - Verify file exists
        Assert.True(File.Exists(outputPath));

        // Assert - Reload and verify
        using var loadedDoc = new PdfDocument(outputPath);
        Assert.Equal(3, loadedDoc.PageCount);

        var (w0, h0) = loadedDoc.GetPageSize(0);
        var (w1, h1) = loadedDoc.GetPageSize(1);
        var (w2, h2) = loadedDoc.GetPageSize(2);

        Assert.Equal(612, w0);
        Assert.Equal(792, h0);
        Assert.Equal(595, w1);
        Assert.Equal(842, h1);
        Assert.Equal(200, w2);
        Assert.Equal(200, h2);
    }

    #endregion
}