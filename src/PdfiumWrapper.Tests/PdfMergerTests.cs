using System.Reflection;
using System.Runtime.InteropServices;
using Xunit.Abstractions;

namespace PdfiumWrapper.Tests;

[Collection("PDF Tests")]
public class PdfMergerTests : IDisposable
{
    private readonly ITestOutputHelper _testOutputHelper;
    private readonly List<string> _tempFiles = new();

    // Load template PDFs into memory once
    private static readonly byte[] Doc1PageBytes = File.ReadAllBytes("Docs/doc-1-page.pdf");
    private static readonly byte[] Doc3PagesBytes = File.ReadAllBytes("Docs/doc-3-pages-with-comments.pdf");

    public PdfMergerTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    private static GCHandle GetDocumentBytesHandle(PdfMerger merger)
    {
        var field = typeof(PdfMerger).GetField("_documentBytesHandle", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (GCHandle)field.GetValue(merger)!;
    }

    private static byte[]? GetDocumentBytes(PdfMerger merger)
    {
        var field = typeof(PdfMerger).GetField("_documentBytes", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (byte[]?)field.GetValue(merger);
    }

    private static object? GetStreamDocumentLoader(PdfMerger merger)
    {
        var field = typeof(PdfMerger).GetField("_streamDocumentLoader", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field.GetValue(merger);
    }

    public void Dispose()
    {
        // Clean up any temp files created during tests
        foreach (var file in _tempFiles)
        {
            if (File.Exists(file))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
    }

    private string GetUniqueTestFilePath(string baseName)
    {
        var path = Path.Combine(Bootstrapper.WorkingDirectory, $"{baseName}_{Guid.NewGuid():N}.pdf");
        _tempFiles.Add(path);
        return path;
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_Default_ShouldCreateEmptyDocument()
    {
        // Arrange & Act
        using var merger = new PdfMerger();

        // Assert
        Assert.NotNull(merger);
        Assert.Equal(0, merger.PageCount);
    }

    [Fact]
    public void Constructor_WithValidFilePath_ShouldLoadDocument()
    {
        // Arrange - Save bytes to temp file
        var tempPath = GetUniqueTestFilePath("constructor_test");
        File.WriteAllBytes(tempPath, Doc1PageBytes);

        // Act
        using var merger = new PdfMerger(tempPath);

        // Assert
        Assert.NotNull(merger);
        Assert.Equal(1, merger.PageCount);
    }

    [Fact]
    public void Constructor_WithInvalidFilePath_ShouldThrowException()
    {
        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => new PdfMerger("nonexistent.pdf"));
    }

    [Fact]
    public void Constructor_WithByteArray_ShouldLoadDocument()
    {
        // Arrange
        var bytes = (byte[])Doc1PageBytes.Clone();

        // Act
        using var merger = new PdfMerger(bytes);

        // Assert
        Assert.NotNull(merger);
        Assert.Equal(1, merger.PageCount);
    }

    [Fact]
    public void Constructor_WithByteArray_ShouldKeepPinnedBufferUntilDispose()
    {
        // Arrange
        var bytes = (byte[])Doc1PageBytes.Clone();
        var merger = new PdfMerger(bytes);

        try
        {
            Assert.True(GetDocumentBytesHandle(merger).IsAllocated);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.Equal(1, merger.PageCount);
        }
        finally
        {
            merger.Dispose();
        }

        Assert.False(GetDocumentBytesHandle(merger).IsAllocated);
        Assert.Null(GetDocumentBytes(merger));
    }

    [Fact]
    public void Constructor_WithStream_ShouldLoadDocument()
    {
        // Arrange
        using var stream = new MemoryStream(Doc1PageBytes);

        // Act
        using var merger = new PdfMerger(stream);

        // Assert
        Assert.NotNull(merger);
        Assert.Equal(1, merger.PageCount);
    }

    [Fact]
    public void Constructor_WithSeekableFileStream_ShouldUseCustomLoader()
    {
        // Arrange
        var tempPath = GetUniqueTestFilePath("constructor_stream_file");
        File.WriteAllBytes(tempPath, Doc1PageBytes);
        using var stream = File.OpenRead(tempPath);

        // Act
        using var merger = new PdfMerger(stream);

        // Assert
        Assert.Equal(1, merger.PageCount);
        Assert.Null(GetDocumentBytes(merger));
        Assert.False(GetDocumentBytesHandle(merger).IsAllocated);
        Assert.NotNull(GetStreamDocumentLoader(merger));
    }

    [Fact]
    public void Constructor_WithSeekableFileStream_ShouldRemainUsableAfterGc()
    {
        // Arrange
        var tempPath = GetUniqueTestFilePath("constructor_stream_gc");
        File.WriteAllBytes(tempPath, Doc1PageBytes);
        using var stream = File.OpenRead(tempPath);
        using var merger = new PdfMerger(stream);

        // Act
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Assert
        Assert.Equal(1, merger.PageCount);
        var bytes = merger.ToBytes();
        Assert.True(bytes.Length > 0);
    }

    [Fact]
    public void Constructor_WithStream_ShouldRemainUsableAfterSourceStreamDisposed()
    {
        // Arrange
        PdfMerger merger;
        using (var stream = new MemoryStream((byte[])Doc1PageBytes.Clone()))
        {
            merger = new PdfMerger(stream);
        }

        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.Equal(1, merger.PageCount);

            var bytes = merger.ToBytes();
            Assert.True(bytes.Length > 0);
        }
        finally
        {
            merger.Dispose();
        }
    }

    [Fact]
    public void Constructor_WithExposableMemoryStream_ShouldReuseBackingBuffer()
    {
        // Arrange
        var bytes = (byte[])Doc1PageBytes.Clone();
        using var stream = new MemoryStream();
        stream.Write(bytes, 0, bytes.Length);
        stream.Position = 0;

        Assert.True(stream.TryGetBuffer(out var segment));

        // Act
        using var merger = new PdfMerger(stream);

        // Assert
        Assert.Same(segment.Array, GetDocumentBytes(merger));
        Assert.True(GetDocumentBytesHandle(merger).IsAllocated);
    }

    [Fact]
    public void Constructor_WithNonSeekableStream_ShouldFallBackToPinnedBytes()
    {
        // Arrange
        using var innerStream = new MemoryStream((byte[])Doc1PageBytes.Clone());
        using var stream = new NonSeekableReadOnlyStream(innerStream);

        // Act
        using var merger = new PdfMerger(stream);

        // Assert
        Assert.Equal(1, merger.PageCount);
        Assert.NotNull(GetDocumentBytes(merger));
        Assert.True(GetDocumentBytesHandle(merger).IsAllocated);
        Assert.Null(GetStreamDocumentLoader(merger));
    }

    [Fact]
    public void Constructor_WithInvalidByteArray_ShouldThrowException()
    {
        // Arrange
        var invalidBytes = new byte[] { 1, 2, 3, 4, 5 };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => new PdfMerger(invalidBytes));
    }

    #endregion

    #region PageCount Tests

    [Fact]
    public void PageCount_EmptyDocument_ShouldReturnZero()
    {
        // Arrange
        using var merger = new PdfMerger();

        // Act
        var count = merger.PageCount;

        // Assert
        Assert.Equal(0, count);
    }

    [Fact]
    public void PageCount_AfterAppendingDocument_ShouldUpdateCorrectly()
    {
        // Arrange
        using var merger = new PdfMerger();
        using var sourceDoc = new PdfDocument((byte[])Doc3PagesBytes.Clone());

        // Act
        merger.AppendDocument(sourceDoc);

        // Assert
        Assert.Equal(3, merger.PageCount);
    }

    #endregion

    #region AppendDocument Tests

    [Fact]
    public void AppendDocument_WithPdfDocument_ShouldAddAllPages()
    {
        // Arrange
        using var merger = new PdfMerger();
        using var doc1 = new PdfDocument((byte[])Doc1PageBytes.Clone());
        using var doc3 = new PdfDocument((byte[])Doc3PagesBytes.Clone());

        // Act
        merger.AppendDocument(doc1);
        merger.AppendDocument(doc3);

        // Assert
        Assert.Equal(4, merger.PageCount);
    }

    [Fact]
    public void AppendDocument_WithFilePath_ShouldAddAllPages()
    {
        // Arrange
        using var merger = new PdfMerger();
        var tempPath1 = GetUniqueTestFilePath("append_file_1");
        var tempPath2 = GetUniqueTestFilePath("append_file_2");
        File.WriteAllBytes(tempPath1, Doc1PageBytes);
        File.WriteAllBytes(tempPath2, Doc3PagesBytes);

        // Act
        merger.AppendDocument(tempPath1);
        merger.AppendDocument(tempPath2);

        // Assert
        Assert.Equal(4, merger.PageCount);
    }

    [Fact]
    public void AppendDocument_WithByteArray_ShouldAddAllPages()
    {
        // Arrange
        using var merger = new PdfMerger();
        var bytes1 = (byte[])Doc1PageBytes.Clone();
        var bytes3 = (byte[])Doc3PagesBytes.Clone();

        // Act
        merger.AppendDocument(bytes1);
        merger.AppendDocument(bytes3);

        // Assert
        Assert.Equal(4, merger.PageCount);
    }

    [Fact]
    public void AppendDocument_MultipleDocuments_ShouldMaintainOrder()
    {
        // Arrange
        using var merger = new PdfMerger();
        using var doc1 = new PdfDocument((byte[])Doc1PageBytes.Clone());
        using var doc3 = new PdfDocument((byte[])Doc3PagesBytes.Clone());

        // Act
        merger.AppendDocument(doc1);
        merger.AppendDocument(doc3);

        // Assert
        Assert.Equal(4, merger.PageCount);
        
        // Save and verify
        var outputPath = GetUniqueTestFilePath("append_multiple");
        merger.Save(outputPath);
        
        using var verifyDoc = new PdfDocument(outputPath);
        Assert.Equal(4, verifyDoc.PageCount);
    }

    #endregion

    #region AppendPages Tests

    [Fact]
    public void AppendPages_WithPageRange_ShouldAddSpecificPages()
    {
        // Arrange
        using var merger = new PdfMerger();
        using var sourceDoc = new PdfDocument((byte[])Doc3PagesBytes.Clone());

        // Act - Append pages 1 and 3 (1-based)
        merger.AppendPages(sourceDoc, "1,3");

        // Assert
        Assert.Equal(2, merger.PageCount);
    }

    [Fact]
    public void AppendPages_WithPageRangeSpan_ShouldAddRangeOfPages()
    {
        // Arrange
        using var merger = new PdfMerger();
        using var sourceDoc = new PdfDocument((byte[])Doc3PagesBytes.Clone());

        // Act - Append pages 1-2 (1-based)
        merger.AppendPages(sourceDoc, "1-2");

        // Assert
        Assert.Equal(2, merger.PageCount);
    }

    [Fact]
    public void AppendPages_WithNullPageRange_ShouldAddAllPages()
    {
        // Arrange
        using var merger = new PdfMerger();
        using var sourceDoc = new PdfDocument((byte[])Doc3PagesBytes.Clone());

        // Act
        merger.AppendPages(sourceDoc, (string)null);

        // Assert
        Assert.Equal(3, merger.PageCount);
    }

    [Fact]
    public void AppendPages_WithPageIndices_ShouldAddSpecificPages()
    {
        // Arrange
        using var merger = new PdfMerger();
        using var sourceDoc = new PdfDocument((byte[])Doc3PagesBytes.Clone());

        // Act - Append pages at indices 0 and 2 (0-based)
        merger.AppendPages(sourceDoc, new[] { 0, 2 });

        // Assert
        Assert.Equal(2, merger.PageCount);
    }

    [Fact]
    public void AppendPages_WithMultiplePageIndices_ShouldMaintainOrder()
    {
        // Arrange
        using var merger = new PdfMerger();
        using var sourceDoc = new PdfDocument((byte[])Doc3PagesBytes.Clone());

        // Act - Append in specific order
        merger.AppendPages(sourceDoc, new[] { 2, 0, 1 });

        // Assert
        Assert.Equal(3, merger.PageCount);
        
        var outputPath = GetUniqueTestFilePath("append_pages_order");
        merger.Save(outputPath);
        
        using var verifyDoc = new PdfDocument(outputPath);
        Assert.Equal(3, verifyDoc.PageCount);
    }

    #endregion

    #region InsertDocument Tests

    [Fact]
    public void InsertDocument_AtBeginning_ShouldInsertAtCorrectPosition()
    {
        // Arrange
        using var merger = new PdfMerger((byte[])Doc3PagesBytes.Clone());
        using var doc1 = new PdfDocument((byte[])Doc1PageBytes.Clone());

        // Act - Insert at beginning (index 0)
        merger.InsertDocument(doc1, 0);

        // Assert
        Assert.Equal(4, merger.PageCount);
    }

    [Fact]
    public void InsertDocument_InMiddle_ShouldInsertAtCorrectPosition()
    {
        // Arrange
        using var merger = new PdfMerger((byte[])Doc3PagesBytes.Clone());
        using var doc1 = new PdfDocument((byte[])Doc1PageBytes.Clone());

        // Act - Insert in middle (index 1)
        merger.InsertDocument(doc1, 1);

        // Assert
        Assert.Equal(4, merger.PageCount);
    }

    [Fact]
    public void InsertDocument_AtEnd_ShouldInsertAtCorrectPosition()
    {
        // Arrange
        using var merger = new PdfMerger((byte[])Doc3PagesBytes.Clone());
        using var doc1 = new PdfDocument((byte[])Doc1PageBytes.Clone());

        // Act - Insert at end (index equal to PageCount)
        merger.InsertDocument(doc1, 3);

        // Assert
        Assert.Equal(4, merger.PageCount);
    }

    [Fact]
    public void InsertDocument_WithInvalidIndex_ShouldThrowException()
    {
        // Arrange
        using var merger = new PdfMerger((byte[])Doc1PageBytes.Clone());
        using var sourceDoc = new PdfDocument((byte[])Doc3PagesBytes.Clone());

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => merger.InsertDocument(sourceDoc, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => merger.InsertDocument(sourceDoc, 10));
    }

    #endregion

    #region InsertPages Tests

    [Fact]
    public void InsertPages_WithPageRange_ShouldInsertAtCorrectPosition()
    {
        // Arrange
        using var merger = new PdfMerger((byte[])Doc3PagesBytes.Clone());
        using var sourceDoc = new PdfDocument((byte[])Doc1PageBytes.Clone());

        // Act - Insert at index 1
        merger.InsertPages(sourceDoc, "1", 1);

        // Assert
        Assert.Equal(4, merger.PageCount);
    }

    [Fact]
    public void InsertPages_WithPageIndices_ShouldInsertAtCorrectPosition()
    {
        // Arrange
        using var merger = new PdfMerger((byte[])Doc3PagesBytes.Clone());
        using var doc3 = new PdfDocument((byte[])Doc3PagesBytes.Clone());

        // Act - Insert specific pages at index 1
        merger.InsertPages(doc3, new[] { 0, 2 }, 1);

        // Assert
        Assert.Equal(5, merger.PageCount);
    }

    [Fact]
    public void InsertPages_WithInvalidIndex_ShouldThrowException()
    {
        // Arrange
        using var merger = new PdfMerger((byte[])Doc1PageBytes.Clone());
        using var sourceDoc = new PdfDocument((byte[])Doc3PagesBytes.Clone());

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => merger.InsertPages(sourceDoc, "1", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => merger.InsertPages(sourceDoc, new[] { 0 }, 10));
    }

    #endregion

    #region DeletePage Tests

    [Fact]
    public void DeletePage_WithValidIndex_ShouldRemovePage()
    {
        // Arrange
        using var merger = new PdfMerger((byte[])Doc3PagesBytes.Clone());
        var initialCount = merger.PageCount;

        // Act
        merger.DeletePage(1);

        // Assert
        Assert.Equal(initialCount - 1, merger.PageCount);
    }

    [Fact]
    public void DeletePage_WithInvalidIndex_ShouldThrowException()
    {
        // Arrange
        using var merger = new PdfMerger((byte[])Doc1PageBytes.Clone());

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => merger.DeletePage(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => merger.DeletePage(10));
    }

    [Fact]
    public void DeletePage_FirstPage_ShouldRemoveCorrectPage()
    {
        // Arrange
        using var merger = new PdfMerger((byte[])Doc3PagesBytes.Clone());

        // Act
        merger.DeletePage(0);

        // Assert
        Assert.Equal(2, merger.PageCount);
    }

    [Fact]
    public void DeletePage_LastPage_ShouldRemoveCorrectPage()
    {
        // Arrange
        using var merger = new PdfMerger((byte[])Doc3PagesBytes.Clone());

        // Act
        merger.DeletePage(2);

        // Assert
        Assert.Equal(2, merger.PageCount);
    }

    #endregion

    #region DeletePages Tests

    [Fact]
    public void DeletePages_WithMultipleIndices_ShouldRemoveAllPages()
    {
        // Arrange
        using var merger = new PdfMerger((byte[])Doc3PagesBytes.Clone());

        // Act - Delete pages at indices 0 and 2
        merger.DeletePages(new[] { 0, 2 });

        // Assert
        Assert.Equal(1, merger.PageCount);
    }

    [Fact]
    public void DeletePages_WithAllIndices_ShouldRemoveAllPages()
    {
        // Arrange
        using var merger = new PdfMerger((byte[])Doc3PagesBytes.Clone());

        // Act
        merger.DeletePages(new[] { 0, 1, 2 });

        // Assert
        Assert.Equal(0, merger.PageCount);
    }

    [Fact]
    public void DeletePages_WithUnorderedIndices_ShouldRemoveAllPages()
    {
        // Arrange
        using var merger = new PdfMerger((byte[])Doc3PagesBytes.Clone());

        // Act - Delete in non-sequential order
        merger.DeletePages(new[] { 2, 0, 1 });

        // Assert
        Assert.Equal(0, merger.PageCount);
    }

    #endregion

    #region Save Tests

    [Fact]
    public void Save_ToFile_ShouldCreateValidPdfFile()
    {
        // Arrange
        using var merger = new PdfMerger();
        using var doc1 = new PdfDocument((byte[])Doc1PageBytes.Clone());
        using var doc3 = new PdfDocument((byte[])Doc3PagesBytes.Clone());
        merger.AppendDocument(doc1);
        merger.AppendDocument(doc3);

        var outputPath = GetUniqueTestFilePath("save_to_file");

        // Act
        merger.Save(outputPath);

        // Assert
        Assert.True(File.Exists(outputPath));
        Assert.True(new FileInfo(outputPath).Length > 0);

        // Verify the saved PDF is valid
        using var verifyDoc = new PdfDocument(outputPath);
        Assert.Equal(4, verifyDoc.PageCount);
    }

    [Fact]
    public void Save_ToStream_ShouldWriteValidPdfData()
    {
        // Arrange
        using var merger = new PdfMerger();
        using var doc1 = new PdfDocument((byte[])Doc1PageBytes.Clone());
        merger.AppendDocument(doc1);

        using var stream = new MemoryStream();

        // Act
        merger.Save(stream);

        // Assert
        Assert.True(stream.Length > 0);

        // Verify the saved PDF is valid
        stream.Position = 0;
        using var verifyDoc = new PdfDocument(stream);
        Assert.Equal(1, verifyDoc.PageCount);
    }

    [Fact]
    public void Save_ToStream_Repeatedly_ShouldRemainStable()
    {
        // Arrange
        using var merger = new PdfMerger();
        using var doc1 = new PdfDocument((byte[])Doc1PageBytes.Clone());
        using var doc3 = new PdfDocument((byte[])Doc3PagesBytes.Clone());
        merger.AppendDocument(doc1);
        merger.AppendDocument(doc3);

        // Act / Assert
        for (int i = 0; i < 25; i++)
        {
            using var stream = new MemoryStream();
            merger.Save(stream);

            Assert.True(stream.Length > 0);

            stream.Position = 0;
            using var verifyDoc = new PdfDocument(stream);
            Assert.Equal(4, verifyDoc.PageCount);
        }
    }

    [Fact]
    public void Save_EmptyDocument_ShouldCreateValidPdf()
    {
        // Arrange
        using var merger = new PdfMerger();
        var outputPath = GetUniqueTestFilePath("save_empty");

        // Act
        merger.Save(outputPath);

        // Assert
        Assert.True(File.Exists(outputPath));
        
        using var verifyDoc = new PdfDocument(outputPath);
        Assert.Equal(0, verifyDoc.PageCount);
    }

    [Fact]
    public void Save_WithFlags_ShouldNotThrow()
    {
        // Arrange
        using var merger = new PdfMerger((byte[])Doc1PageBytes.Clone());
        var outputPath = GetUniqueTestFilePath("save_with_flags");

        // Act & Assert - Should not throw
        merger.Save(outputPath, flags: 1); // FPDF_INCREMENTAL flag

        Assert.True(File.Exists(outputPath));
    }

    #endregion

    #region ToBytes Tests

    [Fact]
    public void ToBytes_ShouldReturnValidPdfData()
    {
        // Arrange
        using var merger = new PdfMerger();
        using var doc1 = new PdfDocument((byte[])Doc1PageBytes.Clone());
        merger.AppendDocument(doc1);

        // Act
        var bytes = merger.ToBytes();

        // Assert
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);

        // Verify the bytes represent a valid PDF
        using var verifyDoc = new PdfDocument(bytes);
        Assert.Equal(1, verifyDoc.PageCount);
    }

    [Fact]
    public void ToBytes_EmptyDocument_ShouldReturnValidPdfData()
    {
        // Arrange
        using var merger = new PdfMerger();

        // Act
        var bytes = merger.ToBytes();

        // Assert
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);

        using var verifyDoc = new PdfDocument(bytes);
        Assert.Equal(0, verifyDoc.PageCount);
    }

    #endregion

    #region CopyViewerPreferences Tests

    [Fact]
    public void CopyViewerPreferences_ShouldNotThrow()
    {
        // Arrange
        using var merger = new PdfMerger();
        using var sourceDoc = new PdfDocument((byte[])Doc3PagesBytes.Clone());

        // Act & Assert - Should not throw
        merger.CopyViewerPreferences(sourceDoc);
    }

    #endregion

    #region Complex Scenarios Tests

    [Fact]
    public void ComplexScenario_MergeInsertDeleteSave_ShouldWorkCorrectly()
    {
        // Arrange
        using var merger = new PdfMerger();
        using var doc1 = new PdfDocument((byte[])Doc1PageBytes.Clone());
        using var doc3 = new PdfDocument((byte[])Doc3PagesBytes.Clone());

        // Act - Complex operations
        merger.AppendDocument(doc3); // 3 pages
        merger.AppendDocument(doc1); // 4 pages total
        merger.InsertDocument(doc1, 2); // 5 pages total
        merger.DeletePage(0); // 4 pages total

        var outputPath = GetUniqueTestFilePath("complex_scenario");
        merger.Save(outputPath);

        // Assert
        Assert.Equal(4, merger.PageCount);
        Assert.True(File.Exists(outputPath));

        using var verifyDoc = new PdfDocument(outputPath);
        Assert.Equal(4, verifyDoc.PageCount);
    }

    [Fact]
    public void ComplexScenario_AppendPagesWithIndices_ShouldWorkCorrectly()
    {
        // Arrange
        using var merger = new PdfMerger();
        using var doc3 = new PdfDocument((byte[])Doc3PagesBytes.Clone());

        // Act - Append specific pages multiple times
        merger.AppendPages(doc3, new[] { 0 }); // 1 page
        merger.AppendPages(doc3, new[] { 1, 2 }); // 3 pages total
        merger.AppendPages(doc3, new[] { 0, 1, 2 }); // 6 pages total

        var outputPath = GetUniqueTestFilePath("complex_append_indices");
        merger.Save(outputPath);

        // Assert
        Assert.Equal(6, merger.PageCount);

        using var verifyDoc = new PdfDocument(outputPath);
        Assert.Equal(6, verifyDoc.PageCount);
    }

    [Fact]
    public void ComplexScenario_MergeFromDifferentSources_ShouldWorkCorrectly()
    {
        // Arrange
        using var merger = new PdfMerger();
        
        // From file path
        var tempPath = GetUniqueTestFilePath("complex_source_file");
        File.WriteAllBytes(tempPath, Doc1PageBytes);
        merger.AppendDocument(tempPath);
        
        // From byte array
        var bytes = (byte[])Doc1PageBytes.Clone();
        merger.AppendDocument(bytes);
        
        // From PdfDocument
        using var doc = new PdfDocument((byte[])Doc3PagesBytes.Clone());
        merger.AppendDocument(doc);

        var outputPath = GetUniqueTestFilePath("complex_different_sources");
        merger.Save(outputPath);

        // Assert
        Assert.Equal(5, merger.PageCount);

        using var verifyDoc = new PdfDocument(outputPath);
        Assert.Equal(5, verifyDoc.PageCount);
    }

    [Fact]
    public void ComplexScenario_BuildDocumentFromScratch_ShouldWorkCorrectly()
    {
        // Arrange
        using var merger = new PdfMerger();
        using var doc1 = new PdfDocument((byte[])Doc1PageBytes.Clone());
        using var doc3 = new PdfDocument((byte[])Doc3PagesBytes.Clone());

        // Act - Build a custom document
        // Page 1 from doc1
        merger.AppendPages(doc1, new[] { 0 });
        
        // Pages 1,3 from doc3
        merger.AppendPages(doc3, new[] { 0, 2 });
        
        // Page 1 from doc1 again
        merger.AppendPages(doc1, new[] { 0 });
        
        // Page 2 from doc3
        merger.AppendPages(doc3, new[] { 1 });

        var outputPath = GetUniqueTestFilePath("complex_custom_build");
        merger.Save(outputPath);

        // Assert
        Assert.Equal(5, merger.PageCount);

        using var verifyDoc = new PdfDocument(outputPath);
        Assert.Equal(5, verifyDoc.PageCount);
        
        _testOutputHelper.WriteLine($"Successfully created custom document with {verifyDoc.PageCount} pages");
    }

    [Fact]
    public void ComplexScenario_StartWithExistingDocumentAndModify_ShouldWorkCorrectly()
    {
        // Arrange
        using var merger = new PdfMerger((byte[])Doc3PagesBytes.Clone());
        using var doc1 = new PdfDocument((byte[])Doc1PageBytes.Clone());

        var initialCount = merger.PageCount;
        Assert.Equal(3, initialCount);

        // Act - Modify the existing document
        merger.DeletePage(1); // Remove middle page
        merger.InsertDocument(doc1, 1); // Insert in middle
        merger.AppendDocument(doc1); // Append at end

        var outputPath = GetUniqueTestFilePath("complex_modify_existing");
        merger.Save(outputPath);

        // Assert
        Assert.Equal(4, merger.PageCount);

        using var verifyDoc = new PdfDocument(outputPath);
        Assert.Equal(4, verifyDoc.PageCount);
    }

    #endregion

    #region Disposal Tests

    [Fact]
    public void Dispose_ShouldNotThrow()
    {
        // Arrange
        var merger = new PdfMerger();

        // Act & Assert
        merger.Dispose();
    }

    [Fact]
    public void Dispose_CalledMultipleTimes_ShouldNotThrow()
    {
        // Arrange
        var merger = new PdfMerger();

        // Act & Assert
        merger.Dispose();
        merger.Dispose();
        merger.Dispose();
    }

    [Fact]
    public void Dispose_WithLoadedDocument_ShouldNotThrow()
    {
        // Arrange
        var merger = new PdfMerger((byte[])Doc1PageBytes.Clone());

        // Act & Assert
        merger.Dispose();
    }

    [Fact]
    public void PageCount_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        var merger = new PdfMerger((byte[])Doc1PageBytes.Clone());
        merger.Dispose();

        // Act & Assert
        Assert.Throws<ObjectDisposedException>(() => _ = merger.PageCount);
    }

    [Fact]
    public void AppendDocument_WithDisposedSource_ShouldThrowObjectDisposedException()
    {
        // Arrange
        using var merger = new PdfMerger();
        var sourceDoc = new PdfDocument((byte[])Doc1PageBytes.Clone());
        sourceDoc.Dispose();

        // Act & Assert
        Assert.Throws<ObjectDisposedException>(() => merger.AppendDocument(sourceDoc));
    }

    #endregion

    #region Edge Cases Tests

    [Fact]
    public void EdgeCase_AppendSameDocumentMultipleTimes_ShouldWork()
    {
        // Arrange
        using var merger = new PdfMerger();
        using var doc1 = new PdfDocument((byte[])Doc1PageBytes.Clone());

        // Act - Append same document 3 times
        merger.AppendDocument(doc1);
        merger.AppendDocument(doc1);
        merger.AppendDocument(doc1);

        // Assert
        Assert.Equal(3, merger.PageCount);

        var outputPath = GetUniqueTestFilePath("edge_same_doc_multiple");
        merger.Save(outputPath);

        using var verifyDoc = new PdfDocument(outputPath);
        Assert.Equal(3, verifyDoc.PageCount);
    }

    [Fact]
    public void EdgeCase_DeleteAndReAddPages_ShouldWork()
    {
        // Arrange
        using var merger = new PdfMerger((byte[])Doc3PagesBytes.Clone());
        using var doc1 = new PdfDocument((byte[])Doc1PageBytes.Clone());

        // Act
        merger.DeletePage(1);
        Assert.Equal(2, merger.PageCount);

        merger.InsertDocument(doc1, 1);
        Assert.Equal(3, merger.PageCount);

        // Assert
        var outputPath = GetUniqueTestFilePath("edge_delete_readd");
        merger.Save(outputPath);

        using var verifyDoc = new PdfDocument(outputPath);
        Assert.Equal(3, verifyDoc.PageCount);
    }

    [Fact]
    public void EdgeCase_SaveMultipleTimes_ShouldWork()
    {
        // Arrange
        using var merger = new PdfMerger();
        using var doc1 = new PdfDocument((byte[])Doc1PageBytes.Clone());
        merger.AppendDocument(doc1);

        // Act & Assert - Save multiple times to different locations
        var outputPath1 = GetUniqueTestFilePath("edge_save_multiple_1");
        var outputPath2 = GetUniqueTestFilePath("edge_save_multiple_2");
        var outputPath3 = GetUniqueTestFilePath("edge_save_multiple_3");

        merger.Save(outputPath1);
        merger.Save(outputPath2);
        merger.Save(outputPath3);

        Assert.True(File.Exists(outputPath1));
        Assert.True(File.Exists(outputPath2));
        Assert.True(File.Exists(outputPath3));
    }

    [Fact]
    public void EdgeCase_ToBytesMultipleTimes_ShouldWork()
    {
        // Arrange
        using var merger = new PdfMerger();
        using var doc1 = new PdfDocument((byte[])Doc1PageBytes.Clone());
        merger.AppendDocument(doc1);

        // Act
        var bytes1 = merger.ToBytes();
        var bytes2 = merger.ToBytes();
        var bytes3 = merger.ToBytes();

        // Assert
        Assert.NotNull(bytes1);
        Assert.NotNull(bytes2);
        Assert.NotNull(bytes3);
        Assert.True(bytes1.Length > 0);
        Assert.True(bytes2.Length > 0);
        Assert.True(bytes3.Length > 0);
    }

    #endregion
}

internal sealed class NonSeekableReadOnlyStream : Stream
{
    private readonly Stream _inner;

    public NonSeekableReadOnlyStream(Stream inner)
    {
        _inner = inner;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
