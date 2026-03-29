using System.Reflection;
using System.Runtime.InteropServices;
namespace PdfiumWrapper.Tests;

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

    private static GCHandle GetDocumentBytesHandle(PdfDocument document)
    {
        var field = typeof(PdfDocument).GetField("_documentBytesHandle", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (GCHandle)field.GetValue(document)!;
    }

    private static byte[]? GetDocumentBytes(PdfDocument document)
    {
        var field = typeof(PdfDocument).GetField("_documentBytes", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (byte[]?)field.GetValue(document);
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
    public void Constructor_WithByteArray_ShouldKeepPinnedBufferUntilDispose()
    {
        // Arrange
        var bytes = File.ReadAllBytes(ContractPdfPath);
        var doc = new PdfDocument(bytes);

        try
        {
            // Assert
            Assert.True(GetDocumentBytesHandle(doc).IsAllocated);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            using var page = doc.GetPage(0);
            Assert.True(page.Width > 0);
        }
        finally
        {
            doc.Dispose();
        }

        Assert.False(GetDocumentBytesHandle(doc).IsAllocated);
        Assert.Null(GetDocumentBytes(doc));
    }

    [Fact]
    public void Constructor_WithStream_ShouldRemainUsableAfterSourceStreamDisposed()
    {
        // Arrange
        PdfDocument doc;
        using (var stream = File.OpenRead(ContractPdfPath))
        {
            doc = new PdfDocument(stream);
        }

        try
        {
            // Act
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // Assert
            Assert.True(doc.PageCount > 0);
            using var page = doc.GetPage(0);
            Assert.False(string.IsNullOrWhiteSpace(page.ExtractText()));
        }
        finally
        {
            doc.Dispose();
        }
    }

    [Fact]
    public void Constructor_WithExposableMemoryStream_ShouldReuseBackingBuffer()
    {
        // Arrange
        var bytes = File.ReadAllBytes(ContractPdfPath);
        using var stream = new MemoryStream();
        stream.Write(bytes, 0, bytes.Length);
        stream.Position = 0;

        Assert.True(stream.TryGetBuffer(out var segment));

        // Act
        using var doc = new PdfDocument(stream);

        // Assert
        Assert.Same(segment.Array, GetDocumentBytes(doc));
        Assert.True(GetDocumentBytesHandle(doc).IsAllocated);
    }
    
    [Fact]
    public void Constructor_WithInvalidByteArray_ShouldThrowException()
    {
        // Arrange
        var invalidBytes = new byte[] { 1, 2, 3, 4, 5 };
        
        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => new PdfDocument(invalidBytes));
    }

    [Fact]
    public void CreateNewDocument_AndAddMultiplePages_ShouldSucceed()
    {
        // Arrange
        using var doc = new PdfDocument();
        int expectedPages = 5;

        // Act
        for (int i = 0; i < expectedPages; i++)
        {
            doc.AddPage();
        }

        // Assert
        Assert.Equal(expectedPages, doc.PageCount);
    }

    [Fact]
    public void CreateNewDocument_AddContent_AndSave_ShouldCreateValidFile()
    {
        // Arrange
        using var doc = new PdfDocument();
        using var page = doc.AddPage(800, 600);
        page.AddText("New Document Test", 50, 550);
        page.GenerateContent();

        var tempDir = CreateTempDirectory();
        var outputPath = Path.Combine(tempDir, "created_doc.pdf");

        // Act
        doc.Save(outputPath);

        // Assert
        Assert.True(File.Exists(outputPath));
        Assert.True(new FileInfo(outputPath).Length > 0);

        // Validation load
        using var loadedDoc = new PdfDocument(outputPath);
        Assert.Equal(1, loadedDoc.PageCount);
        var size = loadedDoc.GetPageSize(0);
        Assert.Equal(800, size.width);
        Assert.Equal(600, size.height);
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
#pragma warning disable CS0618 // Type or member is obsolete
        var pages = doc.GetAllPages();
#pragma warning restore CS0618 // Type or member is obsolete

        // Assert
        Assert.NotNull(pages);
        Assert.Equal(doc.PageCount, pages.Length);

        // Clean up
        foreach (var page in pages)
        {
            page.Dispose();
        }
    }

    [Fact]
    public void ProcessAllPages_WithFunc_ShouldReturnResults()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);

        // Act
        var sizes = doc.ProcessAllPages(page => (page.Width, page.Height));

        // Assert
        Assert.NotNull(sizes);
        Assert.Equal(doc.PageCount, sizes.Length);
        Assert.All(sizes, size =>
        {
            Assert.True(size.Width > 0);
            Assert.True(size.Height > 0);
        });
    }

    [Fact]
    public void ProcessAllPages_WithFunc_ShouldExtractTextFromAllPages()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);

        // Act
        var texts = doc.ProcessAllPages(page => page.ExtractText());

        // Assert
        Assert.NotNull(texts);
        Assert.Equal(doc.PageCount, texts.Length);
        // At least some pages should have text content
        Assert.Contains(texts, text => !string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public void ProcessAllPages_WithFunc_ShouldHandleComplexProcessing()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);

        // Act - Complex processing: get page info objects
        var pageInfos = doc.ProcessAllPages(page => new
        {
            Index = page.PageIndex,
            Width = page.Width,
            Height = page.Height,
            TextLength = page.ExtractText().Length
        });

        // Assert
        Assert.NotNull(pageInfos);
        Assert.Equal(doc.PageCount, pageInfos.Length);
        for (int i = 0; i < pageInfos.Length; i++)
        {
            Assert.Equal(i, pageInfos[i].Index);
            Assert.True(pageInfos[i].Width > 0);
            Assert.True(pageInfos[i].Height > 0);
            Assert.True(pageInfos[i].TextLength >= 0);
        }
    }

    [Fact]
    public void ProcessAllPages_WithFunc_NullProcessor_ShouldThrowArgumentNullException()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => 
            doc.ProcessAllPages<string>(null!));
    }

    [Fact]
    public void ProcessAllPages_WithAction_ShouldProcessAllPages()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        var processedIndices = new List<int>();

        // Act
        doc.ProcessAllPages(page =>
        {
            processedIndices.Add(page.PageIndex);
        });

        // Assert
        Assert.Equal(doc.PageCount, processedIndices.Count);
        for (int i = 0; i < doc.PageCount; i++)
        {
            Assert.Contains(i, processedIndices);
        }
    }

    [Fact]
    public void ProcessAllPages_WithAction_ShouldAllowSideEffects()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        var totalTextLength = 0;
        var maxWidth = 0.0;

        // Act
        doc.ProcessAllPages(page =>
        {
            totalTextLength += page.ExtractText().Length;
            maxWidth = Math.Max(maxWidth, page.Width);
        });

        // Assert
        Assert.True(totalTextLength >= 0);
        Assert.True(maxWidth > 0);
    }

    [Fact]
    public void ProcessAllPages_WithAction_NullAction_ShouldThrowArgumentNullException()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => 
            doc.ProcessAllPages((Action<PdfPage>)null!));
    }

    [Fact]
    public async Task ProcessAllPagesAsync_WithFunc_ShouldReturnResults()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);

        // Act
        var sizes = await doc.ProcessAllPagesAsync(page => (page.Width, page.Height));

        // Assert
        Assert.NotNull(sizes);
        Assert.Equal(doc.PageCount, sizes.Length);
        Assert.All(sizes, size =>
        {
            Assert.True(size.Width > 0);
            Assert.True(size.Height > 0);
        });
    }

    [Fact]
    public async Task ProcessAllPagesAsync_WithFunc_ShouldExtractTextFromAllPages()
    {
        // Arrange
        using var doc = new PdfDocument(PresentationPdfPath);

        // Act
        var texts = await doc.ProcessAllPagesAsync(page => page.ExtractText());

        // Assert
        Assert.NotNull(texts);
        Assert.Equal(doc.PageCount, texts.Length);
    }

    [Fact]
    public async Task ProcessAllPagesAsync_WithFunc_NullProcessor_ShouldThrowArgumentNullException()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(async () => 
            await doc.ProcessAllPagesAsync<string>(null!));
    }

    [Fact]
    public async Task ProcessAllPagesAsync_WithAction_ShouldProcessAllPages()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        var processedIndices = new List<int>();

        // Act
        await doc.ProcessAllPagesAsync(page =>
        {
            processedIndices.Add(page.PageIndex);
        });

        // Assert
        Assert.Equal(doc.PageCount, processedIndices.Count);
        for (int i = 0; i < doc.PageCount; i++)
        {
            Assert.Contains(i, processedIndices);
        }
    }

    [Fact]
    public async Task ProcessAllPagesAsync_WithAction_NullAction_ShouldThrowArgumentNullException()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(async () => 
            await doc.ProcessAllPagesAsync((Action<PdfPage>)null!));
    }

    [Fact]
    public void ProcessAllPages_WithFunc_ShouldNotLeakMemory_HighThroughput()
    {
        // Arrange - Simulate high-throughput scenario
        using var doc = new PdfDocument(ContractPdfPath);

        // Act - Process pages multiple times to ensure no leaks
        for (int iteration = 0; iteration < 10; iteration++)
        {
            var results = doc.ProcessAllPages(page => new
            {
                Index = page.PageIndex,
                Text = page.ExtractText(),
                Size = (page.Width, page.Height)
            });

            // Assert
            Assert.Equal(doc.PageCount, results.Length);
        }

        // If we get here without exceptions or hanging, disposal is working correctly
    }

    [Fact]
    public async Task ProcessAllPagesAsync_WithFunc_ShouldNotLeakMemory_HighThroughput()
    {
        // Arrange - Simulate high-throughput scenario
        using var doc = new PdfDocument(PresentationPdfPath);

        // Act - Process pages multiple times to ensure no leaks
        for (int iteration = 0; iteration < 10; iteration++)
        {
            var results = await doc.ProcessAllPagesAsync(page => new
            {
                Index = page.PageIndex,
                Size = (page.Width, page.Height)
            });

            // Assert
            Assert.Equal(doc.PageCount, results.Length);
        }

        // If we get here without exceptions or hanging, disposal is working correctly
    }

    [Fact]
    public void ProcessAllPages_ComparedToGetAllPages_ShouldProduceSameResults()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);

        // Act - Using old GetAllPages method
        List<string> textsOld = new();
#pragma warning disable CS0618 // Type or member is obsolete
        var pages = doc.GetAllPages();
#pragma warning restore CS0618 // Type or member is obsolete
        foreach (var page in pages)
        {
            textsOld.Add(page.ExtractText());
            page.Dispose();
        }

        // Act - Using new ProcessAllPages method
        var textsNew = doc.ProcessAllPages(page => page.ExtractText());

        // Assert - Should produce identical results
        Assert.Equal(textsOld.Count, textsNew.Length);
        for (int i = 0; i < textsOld.Count; i++)
        {
            Assert.Equal(textsOld[i], textsNew[i]);
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
    public void RenderPages_ShouldReturnBitmapsForAllPages()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);

        // Act
        var bitmaps = doc.RenderPages(dpi: 72);

        // Assert
        Assert.NotNull(bitmaps);
        Assert.Equal(doc.PageCount, bitmaps.Length);
        Assert.All(bitmaps, bitmap =>
        {
            Assert.NotNull(bitmap);
            Assert.True(bitmap.Width > 0);
            Assert.True(bitmap.Height > 0);
            Assert.True(bitmap.Pixels.Length > 0);
            Assert.Equal(bitmap.Stride * bitmap.Height, bitmap.Pixels.Length);
        });
    }

    [Fact]
    public void RenderPages_WithCustomDpi_ShouldScaleCorrectly()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);

        // Act
        var bitmaps72 = doc.RenderPages(dpi: 72);
        var bitmaps144 = doc.RenderPages(dpi: 144);

        // Assert
        Assert.NotNull(bitmaps72);
        Assert.NotNull(bitmaps144);

        // Higher DPI should result in larger bitmaps (approximately 2x)
        Assert.True(bitmaps144[0].Width > bitmaps72[0].Width);
        Assert.True(bitmaps144[0].Height > bitmaps72[0].Height);
    }

    [Fact]
    public async Task RenderPagesAsync_ShouldReturnBitmapsForAllPages()
    {
        // Arrange
        using var doc = new PdfDocument(PresentationPdfPath);

        // Act
        var bitmaps = await doc.RenderPagesAsync(dpi: 72);

        // Assert
        Assert.NotNull(bitmaps);
        Assert.Equal(doc.PageCount, bitmaps.Length);
        Assert.All(bitmaps, bitmap =>
        {
            Assert.NotNull(bitmap);
            Assert.True(bitmap.Width > 0);
            Assert.True(bitmap.Height > 0);
            Assert.True(bitmap.Pixels.Length > 0);
        });
    }

    #endregion
    
    #region Image Conversion Tests
    
    [Fact]
    public void StreamImageBytes_Png_ShouldReturnValidImages()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);

        // Act
        var images = doc.StreamImageBytes(ImageFormat.Png, quality: 100, dpi: 72).ToList();

        // Assert
        Assert.Equal(doc.PageCount, images.Count);
        Assert.All(images, imageBytes =>
        {
            Assert.NotNull(imageBytes);
            Assert.True(imageBytes.Length > 0);
        });
    }

    [Fact]
    public void StreamImageBytes_Jpeg_ShouldReturnValidImages()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);

        // Act
        var images = doc.StreamImageBytes(ImageFormat.Jpeg, quality: 90, dpi: 72).ToList();

        // Assert
        Assert.Equal(doc.PageCount, images.Count);
        Assert.All(images, imageBytes =>
        {
            Assert.NotNull(imageBytes);
            Assert.True(imageBytes.Length > 0);
        });
    }
    
    [Fact]
    public async Task StreamImageBytesAsync_ShouldReturnValidImages()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);

        // Act
        var images = new List<byte[]>();
        await foreach (var bytes in doc.StreamImageBytesAsync(ImageFormat.Png, quality: 100, dpi: 72))
        {
            images.Add(bytes);
        }

        // Assert
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
        doc.SaveAsImages(outputDir, "custom_prefix", ImageFormat.Png, quality: 100, dpi: 72);
        
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
        await doc.SaveAsImagesAsync(outputDir, "async_page", ImageFormat.Png, quality: 100, dpiWidth: 72, dpiHeight: 72);
        
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
            doc.SaveAsImages(streams, ImageFormat.Png, quality: 100, dpiWidth: 72, dpiHeight: 72);
            
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
            doc.SaveAsImages(streams, ImageFormat.Png, quality: 100, dpiWidth: 72, dpiHeight: 72));
    }

    #endregion

    #region Save As TIFF Tests

    [Fact]
    public void SaveAsTiff_Bilevel_ShouldCreateValidFile()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        var outputDir = CreateTempDirectory();
        var outputPath = Path.Combine(outputDir, "output.tiff");

        // Act
        doc.SaveAsTiff(outputPath, dpi: 72, colorMode: TiffColorMode.Bilevel);

        // Assert
        Assert.True(File.Exists(outputPath));
        var fileInfo = new FileInfo(outputPath);
        Assert.True(fileInfo.Length > 0);
    }

    [Fact]
    public void SaveAsTiff_Grayscale_ShouldCreateValidFile()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        var outputDir = CreateTempDirectory();
        var outputPath = Path.Combine(outputDir, "output_gray.tiff");

        // Act
        doc.SaveAsTiff(outputPath, dpi: 72, colorMode: TiffColorMode.Grayscale);

        // Assert
        Assert.True(File.Exists(outputPath));
        var fileInfo = new FileInfo(outputPath);
        Assert.True(fileInfo.Length > 0);
    }

    [Fact]
    public void SaveAsTiff_SeparateDpi_ShouldCreateValidFile()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        var outputDir = CreateTempDirectory();
        var outputPath = Path.Combine(outputDir, "output_dpi.tiff");

        // Act
        doc.SaveAsTiff(outputPath, dpiWidth: 150, dpiHeight: 200, colorMode: TiffColorMode.Bilevel);

        // Assert
        Assert.True(File.Exists(outputPath));
        Assert.True(new FileInfo(outputPath).Length > 0);
    }

    [Fact]
    public void SaveAsTiff_DefaultParameters_ShouldCreateValidFile()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        var outputDir = CreateTempDirectory();
        var outputPath = Path.Combine(outputDir, "output_default.tiff");

        // Act
        doc.SaveAsTiff(outputPath);

        // Assert
        Assert.True(File.Exists(outputPath));
        Assert.True(new FileInfo(outputPath).Length > 0);
    }

    [Fact]
    public void SaveAsTiff_MultiPageDocument_ShouldProduceLargerFileThanSinglePage()
    {
        // Arrange
        using var multiPageDoc = new PdfDocument(ContractPdfPath);
        Assert.True(multiPageDoc.PageCount > 1, "Test requires a multi-page PDF");

        var outputDir = CreateTempDirectory();
        var multiPagePath = Path.Combine(outputDir, "multi.tiff");

        // Act
        multiPageDoc.SaveAsTiff(multiPagePath, dpi: 72);

        // Assert — multi-page TIFF should have non-trivial size
        var fileInfo = new FileInfo(multiPagePath);
        Assert.True(fileInfo.Length > 0);
    }

    [Fact]
    public void SaveAsTiff_ToStream_ShouldWriteValidData()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        using var stream = new MemoryStream();

        // Act
        doc.SaveAsTiff(stream, dpi: 72, colorMode: TiffColorMode.Bilevel);

        // Assert
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public void SaveAsTiff_ToStream_Grayscale_ShouldWriteValidData()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        using var stream = new MemoryStream();

        // Act
        doc.SaveAsTiff(stream, dpi: 72, colorMode: TiffColorMode.Grayscale);

        // Assert
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public void SaveAsTiff_ToStream_ShouldMatchFileOutput()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        var outputDir = CreateTempDirectory();
        var filePath = Path.Combine(outputDir, "file.tiff");
        using var stream = new MemoryStream();

        // Act
        doc.SaveAsTiff(filePath, dpi: 72);
        doc.SaveAsTiff(stream, dpi: 72);

        // Assert — both outputs should have the same length
        var fileBytes = File.ReadAllBytes(filePath);
        var streamBytes = stream.ToArray();
        Assert.Equal(fileBytes.Length, streamBytes.Length);
    }

    [Fact]
    public async Task SaveAsTiffAsync_ShouldCreateValidFile()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        var outputDir = CreateTempDirectory();
        var outputPath = Path.Combine(outputDir, "async_output.tiff");

        // Act
        await doc.SaveAsTiffAsync(outputPath, dpi: 72);

        // Assert
        Assert.True(File.Exists(outputPath));
        Assert.True(new FileInfo(outputPath).Length > 0);
    }

    [Fact]
    public async Task SaveAsTiffAsync_ToStream_ShouldWriteValidData()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);
        using var stream = new MemoryStream();

        // Act
        await doc.SaveAsTiffAsync(stream, dpi: 72);

        // Assert
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public void SaveAsTiff_EmptyDocument_ShouldThrow()
    {
        // Arrange
        using var doc = new PdfDocument();
        var outputDir = CreateTempDirectory();
        var outputPath = Path.Combine(outputDir, "empty.tiff");

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => doc.SaveAsTiff(outputPath));
    }

    [Fact]
    public void SaveAsTiff_ToStream_EmptyDocument_ShouldThrow()
    {
        // Arrange
        using var doc = new PdfDocument();
        using var stream = new MemoryStream();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => doc.SaveAsTiff(stream));
    }

    [Fact]
    public void SaveAsTiff_PresentationPdf_ShouldCreateValidFile()
    {
        // Arrange
        using var doc = new PdfDocument(PresentationPdfPath);
        var outputDir = CreateTempDirectory();
        var outputPath = Path.Combine(outputDir, "presentation.tiff");

        // Act
        doc.SaveAsTiff(outputPath, dpi: 72);

        // Assert
        Assert.True(File.Exists(outputPath));
        Assert.True(new FileInfo(outputPath).Length > 0);
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
