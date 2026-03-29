using System.Runtime.InteropServices;

namespace PdfiumWrapper;

/// <summary>
/// High-level wrapper class for easier PDF operations.
/// </summary>
/// <remarks>
/// This class is NOT thread-safe. Do not access the same PdfDocument instance from multiple threads concurrently.
/// Each PdfDocument instance should be used from a single thread at a time, or external synchronization must be provided.
/// Async methods process pages sequentially and use Task.Yield() for responsiveness, not parallelism.
/// </remarks>
public class PdfDocument : IDisposable
{
    private IntPtr _document;
    private volatile bool _disposed;
    private int _activePageCount;
    private readonly object _pagesLock = new();
    private HashSet<PdfPage>? _activePages;
    private PdfMetadata? _metadata;
    private PdfBookmarks? _bookmarks;
    private PdfAttachments? _attachments;
    private byte[]? _documentBytes;
    private GCHandle _documentBytesHandle;

    static PdfDocument()
    {
        PDFium.FPDF_InitLibrary();
    }

    /// <summary>
    /// Create a new empty PDF document
    /// </summary>
    public PdfDocument()
    {
        Document = PDFium.FPDF_CreateNewDocument();
        if (Document == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Failed to create new PDF document. Error: {PDFium.FPDF_GetLastError()}");
        }
    }

    public PdfDocument(string filePath, string? password = null)
    {
        Document = PDFium.FPDF_LoadDocument(filePath, password);
        if (Document == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Failed to load PDF document. Error: {PDFium.FPDF_GetLastError()}");
        }
    }

    public PdfDocument(Stream pdfStream, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(pdfStream);

        // For large streams, FPDF_LoadCustomDocument is the better long-term option because it can avoid
        // the full managed copy. This constructor currently preserves the simpler ownership model where the
        // PdfDocument becomes independent from the source stream after construction.
        if (pdfStream is MemoryStream memoryStream
            && memoryStream.TryGetBuffer(out ArraySegment<byte> buffer))
        {
            int offset = buffer.Offset + checked((int)memoryStream.Position);
            int length = checked((int)(memoryStream.Length - memoryStream.Position));
            LoadPinnedMemoryDocument(buffer.Array!, offset, length, password);

            // Match the previous CopyTo() behavior by consuming the stream.
            memoryStream.Position = memoryStream.Length;
            return;
        }

        var bytes = pdfStream.ReadStreamToBytes();
        LoadPinnedMemoryDocument(bytes, 0, bytes.Length, password);
    }

    public PdfDocument(byte[] data, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        LoadPinnedMemoryDocument(data, 0, data.Length, password);
    }

    public PdfMetadata Metadata
    {
        get
        {
            ThrowIfDisposed();
            if (_metadata == null)
            {
                _metadata = new PdfMetadata(Document);
            }
            return _metadata;
        }
    }

    public PdfBookmarks Bookmarks
    {
        get
        {
            ThrowIfDisposed();
            if (_bookmarks == null)
            {
                _bookmarks = new PdfBookmarks(Document);
            }
            return _bookmarks;
        }
    }

    public PdfAttachments Attachments
    {
        get
        {
            ThrowIfDisposed();
            if (_attachments == null)
            {
                _attachments = new PdfAttachments(Document);
            }
            return _attachments;
        }
    }

    public int PageCount
    {
        get
        {
            ThrowIfDisposed();
            return PDFium.FPDF_GetPageCount(Document);
        }
    }

    /// <summary>
    /// Gets the document permissions as a PdfPermissions flags enum
    /// </summary>
    public PdfPermissions Permissions
    {
        get
        {
            ThrowIfDisposed();
            uint rawPermissions = PDFium.FPDF_GetDocPermissions(Document);
            return (PdfPermissions)rawPermissions;
        }
    }

    /// <summary>
    /// Gets the document identifier (ID) from the trailer dictionary
    /// </summary>
    public string? DocumentId
    {
        get
        {
            ThrowIfDisposed();

            // First, get the original file ID (type 0)
            var size = PDFium.FPDF_GetFileIdentifier(Document, 0, IntPtr.Zero, 0);
            if (size == 0)
                return null;

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                var actualSize = PDFium.FPDF_GetFileIdentifier(Document, 0, buffer, size);
                if (actualSize == 0)
                    return null;

                // Convert bytes to hex string
                var bytes = new byte[actualSize];
                Marshal.Copy(buffer, bytes, 0, (int)actualSize);
                return BitConverter.ToString(bytes).Replace("-", "");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    internal IntPtr Document { get => _document; set => _document = value; }

    /// <summary>
    /// Indicates whether this document has been disposed. Used by PdfPage to detect
    /// use-after-free when the owning document is disposed while pages are still alive.
    /// </summary>
    internal bool IsDisposed => _disposed;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>
    /// Called by PdfPage during construction to register an active page.
    /// </summary>
    internal void RegisterPage(PdfPage page)
    {
        ThrowIfDisposed();

        lock (_pagesLock)
        {
            ThrowIfDisposed();
            _activePages ??= new HashSet<PdfPage>();
            if (_activePages.Add(page))
            {
                Interlocked.Increment(ref _activePageCount);
            }
        }
    }

    /// <summary>
    /// Called by PdfPage.Dispose to signal that a page has been released.
    /// Must be safe to call from finalizer threads.
    /// </summary>
    internal void UnregisterPage(PdfPage page)
    {
        lock (_pagesLock)
        {
            if (_activePages != null && _activePages.Remove(page))
            {
                Interlocked.Decrement(ref _activePageCount);
                if (_activePages.Count == 0)
                {
                    _activePages = null;
                }
            }
        }
    }

    private PdfPage[] SnapshotAndClearActivePages()
    {
        lock (_pagesLock)
        {
            if (_activePages == null || _activePages.Count == 0)
            {
                Interlocked.Exchange(ref _activePageCount, 0);
                return Array.Empty<PdfPage>();
            }

            var pages = _activePages.ToArray();
            _activePages.Clear();
            _activePages = null;
            Interlocked.Exchange(ref _activePageCount, 0);
            return pages;
        }
    }

    private void DisposeActivePages()
    {
        foreach (var page in SnapshotAndClearActivePages())
        {
            try
            {
                page.DisposeFromOwner();
            }
            catch
            {
                // Dispose must not throw because active pages still exist.
            }
        }
    }

    private void LoadPinnedMemoryDocument(byte[] data, int offset, int length, string? password)
    {
        _documentBytes = data;
        _documentBytesHandle = GCHandle.Alloc(data, GCHandleType.Pinned);

        try
        {
            var dataPtr = IntPtr.Add(_documentBytesHandle.AddrOfPinnedObject(), offset);
            Document = PDFium.FPDF_LoadMemDocument(dataPtr, length, password);
            if (Document == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"Failed to load PDF document from memory. Error: {PDFium.FPDF_GetLastError()}");
            }
        }
        catch
        {
            ReleasePinnedMemoryDocument();
            throw;
        }
    }

    private void ReleasePinnedMemoryDocument()
    {
        if (_documentBytesHandle.IsAllocated)
        {
            _documentBytesHandle.Free();
        }

        _documentBytes = null;
    }

    public PdfPage GetPage(int pageIndex)
    {
        ThrowIfDisposed();
        if (pageIndex < 0 || pageIndex >= PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));

        return new PdfPage(this, pageIndex);
    }

    /// <summary>
    /// Add a new page to the document
    /// </summary>
    /// <param name="width">Page width in points (default: 612 = US Letter width)</param>
    /// <param name="height">Page height in points (default: 792 = US Letter height)</param>
    /// <param name="index">Index where to insert the page (default: -1 = append at end)</param>
    /// <returns>The newly created page</returns>
    public PdfPage AddPage(int width = 612, int height = 792, int index = -1)
    {
        ThrowIfDisposed();

        if (index == -1)
            index = PageCount; // Append at end

        var pageHandle = PDFium.FPDFPage_New(Document, index, width, height);
        if (pageHandle == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to create new page. Error: {PDFium.FPDF_GetLastError()}");

        // Close the page handle and re-open it using the standard method
        PDFium.FPDF_ClosePage(pageHandle);
        return new PdfPage(this, index);
    }

    /// <summary>
    /// Delete a page from the document by index
    /// </summary>
    /// <param name="pageIndex">The 0-based index of the page to delete</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when pageIndex is out of range</exception>
    public void DeletePage(int pageIndex)
    {
        ThrowIfDisposed();

        if (pageIndex < 0 || pageIndex >= PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex), $"Page index must be between 0 and {PageCount - 1}");

        PDFium.FPDFPage_Delete(Document, pageIndex);
    }

    /// <summary>
    /// Delete a page from the document
    /// </summary>
    /// <param name="page">The page to delete</param>
    /// <exception cref="ArgumentNullException">Thrown when page is null</exception>
    public void DeletePage(PdfPage page)
    {
        if (page == null)
            throw new ArgumentNullException(nameof(page));

        DeletePage(page.PageIndex);
    }

    /// <summary>
    /// Gets all pages in the document. CALLER IS RESPONSIBLE FOR DISPOSING EACH PAGE.
    /// </summary>
    /// <remarks>
    /// ⚠️ WARNING: Each PdfPage in the returned array must be disposed by the caller.
    /// For high-throughput scenarios, prefer <see cref="ProcessAllPages{TResult}(Func{PdfPage, TResult})"/> 
    /// or <see cref="ProcessAllPages(Action{PdfPage})"/> which handle disposal automatically.
    /// </remarks>
    /// <returns>Array of PdfPage objects that must be disposed by the caller</returns>
    [Obsolete("Use ProcessAllPages() for automatic disposal, or ensure each page is disposed manually. This method may cause memory leaks if pages are not disposed.")]
    public PdfPage[] GetAllPages()
    {
        var pages = new PdfPage[PageCount];
        for (int i = 0; i < PageCount; i++)
        {
            pages[i] = GetPage(i);
        }
        return pages;
    }

    /// <summary>
    /// Process all pages with automatic disposal. Safe for high-throughput scenarios.
    /// </summary>
    /// <typeparam name="TResult">The type of result to return for each page</typeparam>
    /// <param name="processor">Function to process each page and return a result</param>
    /// <returns>Array of results from processing each page</returns>
    /// <example>
    /// <code>
    /// // Extract text from all pages safely
    /// var texts = doc.ProcessAllPages(page => page.ExtractText());
    /// 
    /// // Get all page sizes safely
    /// var sizes = doc.ProcessAllPages(page => (page.Width, page.Height));
    /// </code>
    /// </example>
    public TResult[] ProcessAllPages<TResult>(Func<PdfPage, TResult> processor)
    {
        if (processor == null)
            throw new ArgumentNullException(nameof(processor));

        var results = new TResult[PageCount];
        for (int i = 0; i < PageCount; i++)
        {
            using var page = GetPage(i);
            results[i] = processor(page);
        }
        return results;
    }

    /// <summary>
    /// Process all pages with automatic disposal. Safe for high-throughput scenarios.
    /// </summary>
    /// <param name="action">Action to perform on each page</param>
    /// <example>
    /// <code>
    /// // Process each page (e.g., for side effects like logging)
    /// doc.ProcessAllPages(page => Console.WriteLine($"Page {page.PageIndex}: {page.Width}x{page.Height}"));
    /// </code>
    /// </example>
    public void ProcessAllPages(Action<PdfPage> action)
    {
        if (action == null)
            throw new ArgumentNullException(nameof(action));

        for (int i = 0; i < PageCount; i++)
        {
            using var page = GetPage(i);
            action(page);
        }
    }

    /// <summary>
    /// Process all pages asynchronously with automatic disposal. Safe for high-throughput scenarios.
    /// Uses Task.Yield() for UI responsiveness while processing sequentially (PDFium is not thread-safe).
    /// </summary>
    /// <typeparam name="TResult">The type of result to return for each page</typeparam>
    /// <param name="processor">Function to process each page and return a result</param>
    /// <returns>Array of results from processing each page</returns>
    public async Task<TResult[]> ProcessAllPagesAsync<TResult>(Func<PdfPage, TResult> processor)
    {
        if (processor == null)
            throw new ArgumentNullException(nameof(processor));

        var results = new TResult[PageCount];
        for (int i = 0; i < PageCount; i++)
        {
            await Task.Yield();
            using var page = GetPage(i);
            results[i] = processor(page);
        }
        return results;
    }

    /// <summary>
    /// Process all pages asynchronously with automatic disposal. Safe for high-throughput scenarios.
    /// </summary>
    /// <param name="action">Action to perform on each page</param>
    public async Task ProcessAllPagesAsync(Action<PdfPage> action)
    {
        if (action == null)
            throw new ArgumentNullException(nameof(action));

        for (int i = 0; i < PageCount; i++)
        {
            await Task.Yield();
            using var page = GetPage(i);
            action(page);
        }
    }

    /// <summary>
    /// Renders all pages to raw BGRA pixel buffers.
    /// </summary>
    public RawBitmap[] RenderPages(int dpi = 300)
    {
        return RenderPages(dpi, dpi);
    }

    public RawBitmap[] RenderPages(int dpiWidth, int dpiHeight)
    {
        if (PageCount == 0)
            throw new InvalidOperationException("Document has no pages");

        var results = new RawBitmap[PageCount];
        for (int i = 0; i < PageCount; i++)
        {
            using var page = GetPage(i);
            results[i] = RenderPageToRawBitmap(page, dpiWidth, dpiHeight);
        }
        return results;
    }

    public async Task<RawBitmap[]> RenderPagesAsync(int dpi = 300)
    {
        return await RenderPagesAsync(dpi, dpi);
    }

    public async Task<RawBitmap[]> RenderPagesAsync(int dpiWidth, int dpiHeight)
    {
        if (PageCount == 0)
            throw new InvalidOperationException("Document has no pages");

        var results = new RawBitmap[PageCount];
        for (int i = 0; i < PageCount; i++)
        {
            await Task.Yield();
            using var page = GetPage(i);
            results[i] = RenderPageToRawBitmap(page, dpiWidth, dpiHeight);
        }
        return results;
    }

    /// <summary>
    /// Streams image bytes one page at a time using <c>IEnumerable</c>.
    /// Only one page's encoded bytes exist in memory at any point.
    /// JPEG uses native libjpeg-turbo; PNG uses native libpng.
    /// </summary>
    /// <example>
    /// <code>
    /// int i = 0;
    /// foreach (var bytes in doc.StreamImageBytes(ImageFormat.Jpeg, 90, 300))
    /// {
    ///     File.WriteAllBytes($"page_{i++}.jpg", bytes);
    /// }
    /// </code>
    /// </example>
    public IEnumerable<byte[]> StreamImageBytes(ImageFormat format, int quality = 100, int dpi = 300)
        => StreamImageBytes(format, quality, dpi, dpi);

    /// <inheritdoc cref="StreamImageBytes(ImageFormat, int, int)"/>
    public IEnumerable<byte[]> StreamImageBytes(ImageFormat format, int quality, int dpiWidth, int dpiHeight)
    {
        if (PageCount == 0)
            throw new InvalidOperationException("Document has no pages");

        return StreamImageBytesCore(format, quality, dpiWidth, dpiHeight);
    }

    private IEnumerable<byte[]> StreamImageBytesCore(ImageFormat format, int quality, int dpiWidth, int dpiHeight)
    {
        if (format == ImageFormat.Jpeg)
        {
            using var encoder = new JpegEncoder();
            for (int i = 0; i < PageCount; i++)
            {
                using var page = GetPage(i);
                yield return RenderPageToJpegBytes(encoder, page, dpiWidth, dpiHeight, quality);
            }
        }
        else if (format == ImageFormat.Png)
        {
            for (int i = 0; i < PageCount; i++)
            {
                using var page = GetPage(i);
                yield return RenderPageToPngBytes(page, dpiWidth, dpiHeight);
            }
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format), "Use SaveAsTiff for TIFF output");
        }
    }

    public (double width, double height) GetPageSize(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));

        var result = PDFium.FPDF_GetPageSizeByIndex(Document, pageIndex, out double width, out double height);
        if (result == 0)
            throw new InvalidOperationException($"Failed to get size for page {pageIndex}");

        return (width, height);
    }

    /// <summary>
    /// Get the label for a specific page
    /// </summary>
    /// <param name="pageIndex">0-based page index</param>
    /// <returns>The page label string, or null if no label is defined</returns>
    public string? GetPageLabel(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));

        // Get the required buffer size
        var size = PDFium.FPDF_GetPageLabel(Document, pageIndex, IntPtr.Zero, 0);
        if (size == 0)
            return null;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var actualSize = PDFium.FPDF_GetPageLabel(Document, pageIndex, buffer, size);
            if (actualSize == 0)
                return null;

            // Convert UTF-16LE to string
            return Marshal.PtrToStringUni(buffer, (int)(actualSize / 2) - 1); // -1 to exclude null terminator
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Get all page labels for the document
    /// </summary>
    /// <returns>Array of page labels (null for pages without labels)</returns>
    public string?[] GetAllPageLabels()
    {
        var labels = new string?[PageCount];
        for (int i = 0; i < PageCount; i++)
        {
            labels[i] = GetPageLabel(i);
        }
        return labels;
    }

    public (double width, double height)[] GetAllPageSizes()
    {
        var sizes = new (double, double)[PageCount];
        for (int i = 0; i < PageCount; i++)
        {
            sizes[i] = GetPageSize(i);
        }
        return sizes;
    }

    /// <summary>
    /// Streams image bytes one page at a time using <c>IAsyncEnumerable</c>.
    /// Only one page's encoded bytes exist in memory at any point,
    /// making this significantly more memory-efficient than collecting all pages into a list.
    /// JPEG uses native libjpeg-turbo; PNG uses native libpng.
    /// </summary>
    /// <example>
    /// <code>
    /// int i = 0;
    /// await foreach (var bytes in doc.StreamImageBytesAsync(ImageFormat.Jpeg, 90, 300))
    /// {
    ///     await File.WriteAllBytesAsync($"page_{i++}.jpg", bytes);
    ///     // bytes from the previous page are now eligible for GC
    /// }
    /// </code>
    /// </example>
    public IAsyncEnumerable<byte[]> StreamImageBytesAsync(ImageFormat format, int quality = 100, int dpi = 300)
        => StreamImageBytesAsync(format, quality, dpi, dpi);

    /// <inheritdoc cref="StreamImageBytesAsync(ImageFormat, int, int)"/>
    public IAsyncEnumerable<byte[]> StreamImageBytesAsync(ImageFormat format, int quality, int dpiWidth, int dpiHeight)
    {
        if (PageCount == 0)
            throw new InvalidOperationException("Document has no pages");

        return StreamImageBytesCoreAsync(format, quality, dpiWidth, dpiHeight);
    }

    private async IAsyncEnumerable<byte[]> StreamImageBytesCoreAsync(ImageFormat format, int quality, int dpiWidth, int dpiHeight)
    {
        if (format == ImageFormat.Jpeg)
        {
            using var encoder = new JpegEncoder();
            for (int i = 0; i < PageCount; i++)
            {
                await Task.Yield();
                using var page = GetPage(i);
                yield return RenderPageToJpegBytes(encoder, page, dpiWidth, dpiHeight, quality);
            }
        }
        else if (format == ImageFormat.Png)
        {
            for (int i = 0; i < PageCount; i++)
            {
                await Task.Yield();
                using var page = GetPage(i);
                yield return RenderPageToPngBytes(page, dpiWidth, dpiHeight);
            }
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format), "Use SaveAsTiff for TIFF output");
        }
    }

    public void SaveAsImages(Stream[] outputStreams, ImageFormat format, int quality, int dpiWidth, int dpiHeight)
    {
        if (outputStreams.Length != PageCount)
            throw new ArgumentException($"Number of output streams ({outputStreams.Length}) must match page count ({PageCount})");

        if (format == ImageFormat.Jpeg)
        {
            using var encoder = new JpegEncoder();
            for (int i = 0; i < PageCount; i++)
            {
                using var page = GetPage(i);
                RenderPageToJpegStream(encoder, page, dpiWidth, dpiHeight, quality, outputStreams[i]);
            }
        }
        else if (format == ImageFormat.Png)
        {
            for (int i = 0; i < PageCount; i++)
            {
                using var page = GetPage(i);
                RenderPageToPngStream(page, dpiWidth, dpiHeight, outputStreams[i]);
            }
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format), "Use SaveAsTiff for TIFF output");
        }
    }

    /// <summary>
    /// Saves all pages as a single multi-page TIFF file.
    /// Uses a direct PDFium-to-libtiff pipeline with no intermediate image encoding,
    /// making it significantly faster than going through SkiaSharp for high-throughput workloads.
    /// </summary>
    /// <param name="outputPath">Path to the output .tiff file.</param>
    /// <param name="dpi">Resolution in dots per inch (default: 200).</param>
    /// <param name="colorMode">Bilevel (1-bit CCITT G4) or Grayscale (8-bit LZW). Default: Bilevel.</param>
    /// <param name="threshold">Luminance threshold 0–255 for bilevel mode. Ignored for grayscale. Default: 128.</param>
    public void SaveAsTiff(string outputPath, int dpi = 200,
        TiffColorMode colorMode = TiffColorMode.Bilevel, byte threshold = 128)
    {
        SaveAsTiff(outputPath, dpi, dpi, colorMode, threshold);
    }

    /// <summary>
    /// Saves all pages as a single multi-page TIFF file with separate horizontal and vertical DPI.
    /// </summary>
    public void SaveAsTiff(string outputPath, int dpiWidth, int dpiHeight,
        TiffColorMode colorMode = TiffColorMode.Bilevel, byte threshold = 128)
    {
        using var writer = new TiffWriter(outputPath);
        WriteAllPagesToTiff(writer, dpiWidth, dpiHeight, colorMode, threshold);
    }

    /// <summary>
    /// Saves all pages as a single multi-page TIFF to a Stream.
    /// The stream must be writable and seekable (e.g. MemoryStream or FileStream).
    /// </summary>
    public void SaveAsTiff(Stream output, int dpi = 200,
        TiffColorMode colorMode = TiffColorMode.Bilevel, byte threshold = 128)
    {
        SaveAsTiff(output, dpi, dpi, colorMode, threshold);
    }

    /// <summary>
    /// Saves all pages as a single multi-page TIFF to a Stream with separate horizontal and vertical DPI.
    /// </summary>
    public void SaveAsTiff(Stream output, int dpiWidth, int dpiHeight,
        TiffColorMode colorMode = TiffColorMode.Bilevel, byte threshold = 128)
    {
        using var writer = new TiffWriter(output);
        WriteAllPagesToTiff(writer, dpiWidth, dpiHeight, colorMode, threshold);
    }

    /// <summary>
    /// Async version of <see cref="SaveAsTiff(string, int, TiffColorMode, byte)"/>.
    /// Uses Task.Yield() between pages for UI responsiveness.
    /// </summary>
    public Task SaveAsTiffAsync(string outputPath, int dpi = 200,
        TiffColorMode colorMode = TiffColorMode.Bilevel, byte threshold = 128)
    {
        return SaveAsTiffAsync(outputPath, dpi, dpi, colorMode, threshold);
    }

    /// <summary>
    /// Async version of <see cref="SaveAsTiff(string, int, int, TiffColorMode, byte)"/>.
    /// </summary>
    public async Task SaveAsTiffAsync(string outputPath, int dpiWidth, int dpiHeight,
        TiffColorMode colorMode = TiffColorMode.Bilevel, byte threshold = 128)
    {
        using var writer = new TiffWriter(outputPath);
        await WriteAllPagesToTiffAsync(writer, dpiWidth, dpiHeight, colorMode, threshold);
    }

    /// <summary>
    /// Async version of <see cref="SaveAsTiff(Stream, int, TiffColorMode, byte)"/>.
    /// </summary>
    public Task SaveAsTiffAsync(Stream output, int dpi = 200,
        TiffColorMode colorMode = TiffColorMode.Bilevel, byte threshold = 128)
    {
        return SaveAsTiffAsync(output, dpi, dpi, colorMode, threshold);
    }

    /// <summary>
    /// Async version of <see cref="SaveAsTiff(Stream, int, int, TiffColorMode, byte)"/>.
    /// </summary>
    public async Task SaveAsTiffAsync(Stream output, int dpiWidth, int dpiHeight,
        TiffColorMode colorMode = TiffColorMode.Bilevel, byte threshold = 128)
    {
        using var writer = new TiffWriter(output);
        await WriteAllPagesToTiffAsync(writer, dpiWidth, dpiHeight, colorMode, threshold);
    }

    private void WriteAllPagesToTiff(TiffWriter writer, int dpiWidth, int dpiHeight,
        TiffColorMode colorMode, byte threshold)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PdfDocument));
        if (PageCount == 0)
            throw new InvalidOperationException("Document has no pages");

        int pageCount = PageCount;
        for (int i = 0; i < pageCount; i++)
        {
            RenderPageToTiff(writer, i, dpiWidth, dpiHeight, colorMode, threshold, pageCount);
        }
    }

    private async Task WriteAllPagesToTiffAsync(TiffWriter writer, int dpiWidth, int dpiHeight,
        TiffColorMode colorMode, byte threshold)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PdfDocument));
        if (PageCount == 0)
            throw new InvalidOperationException("Document has no pages");

        int pageCount = PageCount;
        for (int i = 0; i < pageCount; i++)
        {
            await Task.Yield();
            RenderPageToTiff(writer, i, dpiWidth, dpiHeight, colorMode, threshold, pageCount);
        }
    }

    private void RenderPageToTiff(TiffWriter writer, int pageIndex, int dpiWidth, int dpiHeight,
        TiffColorMode colorMode, byte threshold, int totalPages)
    {
        using var page = GetPage(pageIndex);

        int widthPx = (int)Math.Round(page.Width / 72.0 * dpiWidth);
        int heightPx = (int)Math.Round(page.Height / 72.0 * dpiHeight);

        var bitmap = page.RenderToBitmapHandle(widthPx, heightPx, PDFium.FPDF_PRINTING | PDFium.FPDF_ANNOT);
        try
        {
            var buffer = PDFium.FPDFBitmap_GetBuffer(bitmap);
            var stride = PDFium.FPDFBitmap_GetStride(bitmap);

            switch (colorMode)
            {
                case TiffColorMode.Bilevel:
                    var bilevelData = PixelConverter.BgraToPackedBilevel(buffer, widthPx, heightPx, stride, threshold);
                    writer.WriteBilevelPage(bilevelData, widthPx, heightPx, dpiWidth, dpiHeight, totalPages);
                    break;

                case TiffColorMode.Grayscale:
                    var grayData = PixelConverter.BgraToGrayscale(buffer, widthPx, heightPx, stride);
                    writer.WriteGrayscalePage(grayData, widthPx, heightPx, dpiWidth, dpiHeight, totalPages);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(colorMode));
            }
        }
        finally
        {
            PDFium.FPDFBitmap_Destroy(bitmap);
        }
    }

    // Convenience methods for saving to directory
    public void SaveAsPngs(string outputDirectory, string fileNamePrefix = "page", int dpi = 300)
    {
        SaveAsImages(outputDirectory, fileNamePrefix, ImageFormat.Png, 100, dpi, dpi);
    }

    public void SaveAsJpegs(string outputDirectory, string fileNamePrefix = "page", int quality = 90, int dpi = 300)
    {
        SaveAsJpegs(outputDirectory, fileNamePrefix, quality, dpi, dpi);
    }

    public void SaveAsJpegs(string outputDirectory, string fileNamePrefix, int quality, int dpiWidth, int dpiHeight)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PdfDocument));
        if (PageCount == 0)
            throw new InvalidOperationException("Document has no pages");

        if (!Directory.Exists(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        using var encoder = new JpegEncoder();

        for (int i = 0; i < PageCount; i++)
        {
            using var page = GetPage(i);
            RenderPageToJpeg(encoder, page, dpiWidth, dpiHeight, quality,
                Path.Combine(outputDirectory, $"{fileNamePrefix}_{i + 1:D3}.jpg"));
        }
    }

    public async Task SaveAsJpegsAsync(string outputDirectory, string fileNamePrefix = "page", int quality = 90, int dpi = 300)
    {
        await SaveAsJpegsAsync(outputDirectory, fileNamePrefix, quality, dpi, dpi);
    }

    public async Task SaveAsJpegsAsync(string outputDirectory, string fileNamePrefix, int quality, int dpiWidth, int dpiHeight)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PdfDocument));
        if (PageCount == 0)
            throw new InvalidOperationException("Document has no pages");

        if (!Directory.Exists(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        using var encoder = new JpegEncoder();

        for (int i = 0; i < PageCount; i++)
        {
            await Task.Yield();
            using var page = GetPage(i);
            RenderPageToJpeg(encoder, page, dpiWidth, dpiHeight, quality,
                Path.Combine(outputDirectory, $"{fileNamePrefix}_{i + 1:D3}.jpg"));
        }
    }

    /// <summary>
    /// Streams JPEG bytes one page at a time using <c>IEnumerable</c>.
    /// Uses native libjpeg-turbo — no SkiaSharp involved.
    /// </summary>
    public IEnumerable<byte[]> StreamJpegBytes(int quality = 90, int dpi = 300)
        => StreamJpegBytes(quality, dpi, dpi);

    public IEnumerable<byte[]> StreamJpegBytes(int quality, int dpiWidth, int dpiHeight)
    {
        if (PageCount == 0)
            throw new InvalidOperationException("Document has no pages");

        return StreamJpegBytesCore(quality, dpiWidth, dpiHeight);
    }

    private IEnumerable<byte[]> StreamJpegBytesCore(int quality, int dpiWidth, int dpiHeight)
    {
        using var encoder = new JpegEncoder();

        for (int i = 0; i < PageCount; i++)
        {
            using var page = GetPage(i);
            yield return RenderPageToJpegBytes(encoder, page, dpiWidth, dpiHeight, quality);
        }
    }

    /// <summary>
    /// Streams JPEG bytes one page at a time using <c>IAsyncEnumerable</c>.
    /// Uses native libjpeg-turbo — no SkiaSharp involved.
    /// </summary>
    public IAsyncEnumerable<byte[]> StreamJpegBytesAsync(int quality = 90, int dpi = 300)
        => StreamJpegBytesAsync(quality, dpi, dpi);

    public IAsyncEnumerable<byte[]> StreamJpegBytesAsync(int quality, int dpiWidth, int dpiHeight)
    {
        if (PageCount == 0)
            throw new InvalidOperationException("Document has no pages");

        return StreamJpegBytesCoreAsync(quality, dpiWidth, dpiHeight);
    }

    private async IAsyncEnumerable<byte[]> StreamJpegBytesCoreAsync(int quality, int dpiWidth, int dpiHeight)
    {
        using var encoder = new JpegEncoder();

        for (int i = 0; i < PageCount; i++)
        {
            await Task.Yield();
            using var page = GetPage(i);
            yield return RenderPageToJpegBytes(encoder, page, dpiWidth, dpiHeight, quality);
        }
    }

    private void RenderPageToJpeg(JpegEncoder encoder, PdfPage page, int dpiWidth, int dpiHeight, int quality, string outputPath)
    {
        int widthPx = (int)Math.Round(page.Width / 72.0 * dpiWidth);
        int heightPx = (int)Math.Round(page.Height / 72.0 * dpiHeight);

        var bitmap = page.RenderToBitmapHandle(widthPx, heightPx, PDFium.FPDF_ANNOT);
        try
        {
            var buffer = PDFium.FPDFBitmap_GetBuffer(bitmap);
            var stride = PDFium.FPDFBitmap_GetStride(bitmap);
            encoder.EncodeToFile(buffer, widthPx, heightPx, stride, outputPath,
                quality: quality);
        }
        finally
        {
            PDFium.FPDFBitmap_Destroy(bitmap);
        }
    }

    private void RenderPageToJpegStream(JpegEncoder encoder, PdfPage page, int dpiWidth, int dpiHeight, int quality, Stream output)
    {
        int widthPx = (int)Math.Round(page.Width / 72.0 * dpiWidth);
        int heightPx = (int)Math.Round(page.Height / 72.0 * dpiHeight);

        var bitmap = page.RenderToBitmapHandle(widthPx, heightPx, PDFium.FPDF_ANNOT);
        try
        {
            var buffer = PDFium.FPDFBitmap_GetBuffer(bitmap);
            var stride = PDFium.FPDFBitmap_GetStride(bitmap);
            encoder.EncodeToStream(buffer, widthPx, heightPx, stride, output,
                quality: quality);
        }
        finally
        {
            PDFium.FPDFBitmap_Destroy(bitmap);
        }
    }

    private byte[] RenderPageToJpegBytes(JpegEncoder encoder, PdfPage page, int dpiWidth, int dpiHeight, int quality)
    {
        int widthPx = (int)Math.Round(page.Width / 72.0 * dpiWidth);
        int heightPx = (int)Math.Round(page.Height / 72.0 * dpiHeight);

        var bitmap = page.RenderToBitmapHandle(widthPx, heightPx, PDFium.FPDF_ANNOT);
        try
        {
            var buffer = PDFium.FPDFBitmap_GetBuffer(bitmap);
            var stride = PDFium.FPDFBitmap_GetStride(bitmap);
            return encoder.Encode(buffer, widthPx, heightPx, stride, quality: quality);
        }
        finally
        {
            PDFium.FPDFBitmap_Destroy(bitmap);
        }
    }

    public void SaveAsImages(string outputDirectory, string fileNamePrefix, ImageFormat format, int quality = 100, int dpi = 300)
    {
        SaveAsImages(outputDirectory, fileNamePrefix, format, quality, dpi, dpi);
    }

    public void SaveAsImages(string outputDirectory, string fileNamePrefix, ImageFormat format, int quality, int dpiWidth, int dpiHeight)
    {
        if (PageCount == 0)
            throw new InvalidOperationException("Document has no pages");

        if (!Directory.Exists(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        string extension = GetExtensionForFormat(format);

        if (format == ImageFormat.Jpeg)
        {
            using var encoder = new JpegEncoder();
            for (int i = 0; i < PageCount; i++)
            {
                var filePath = Path.Combine(outputDirectory, $"{fileNamePrefix}_{i + 1:D3}.{extension}");
                using var page = GetPage(i);
                RenderPageToJpeg(encoder, page, dpiWidth, dpiHeight, quality, filePath);
            }
        }
        else if (format == ImageFormat.Png)
        {
            for (int i = 0; i < PageCount; i++)
            {
                var filePath = Path.Combine(outputDirectory, $"{fileNamePrefix}_{i + 1:D3}.{extension}");
                using var page = GetPage(i);
                RenderPageToPngFile(page, dpiWidth, dpiHeight, filePath);
            }
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format), "Use SaveAsTiff for TIFF output");
        }
    }

    public async Task SaveAsImagesAsync(string outputDirectory, string fileNamePrefix, ImageFormat format, int quality, int dpiWidth, int dpiHeight)
    {
        if (PageCount == 0)
            throw new InvalidOperationException("Document has no pages");

        if (!Directory.Exists(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        string extension = GetExtensionForFormat(format);

        if (format == ImageFormat.Jpeg)
        {
            using var encoder = new JpegEncoder();
            for (int i = 0; i < PageCount; i++)
            {
                await Task.Yield();
                var filePath = Path.Combine(outputDirectory, $"{fileNamePrefix}_{i + 1:D3}.{extension}");
                using var page = GetPage(i);
                RenderPageToJpeg(encoder, page, dpiWidth, dpiHeight, quality, filePath);
            }
        }
        else if (format == ImageFormat.Png)
        {
            for (int i = 0; i < PageCount; i++)
            {
                await Task.Yield();
                var filePath = Path.Combine(outputDirectory, $"{fileNamePrefix}_{i + 1:D3}.{extension}");
                using var page = GetPage(i);
                RenderPageToPngFile(page, dpiWidth, dpiHeight, filePath);
            }
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format), "Use SaveAsTiff for TIFF output");
        }
    }

    private void RenderPageToPngFile(PdfPage page, int dpiWidth, int dpiHeight, string outputPath)
    {
        int widthPx = (int)Math.Round(page.Width / 72.0 * dpiWidth);
        int heightPx = (int)Math.Round(page.Height / 72.0 * dpiHeight);

        var bitmap = page.RenderToBitmapHandle(widthPx, heightPx, PDFium.FPDF_ANNOT);
        try
        {
            var buffer = PDFium.FPDFBitmap_GetBuffer(bitmap);
            var stride = PDFium.FPDFBitmap_GetStride(bitmap);
            PngEncoder.EncodeToFile(buffer, widthPx, heightPx, stride, outputPath);
        }
        finally
        {
            PDFium.FPDFBitmap_Destroy(bitmap);
        }
    }

    private byte[] RenderPageToPngBytes(PdfPage page, int dpiWidth, int dpiHeight)
    {
        int widthPx = (int)Math.Round(page.Width / 72.0 * dpiWidth);
        int heightPx = (int)Math.Round(page.Height / 72.0 * dpiHeight);

        var bitmap = page.RenderToBitmapHandle(widthPx, heightPx, PDFium.FPDF_ANNOT);
        try
        {
            var buffer = PDFium.FPDFBitmap_GetBuffer(bitmap);
            var stride = PDFium.FPDFBitmap_GetStride(bitmap);
            return PngEncoder.Encode(buffer, widthPx, heightPx, stride);
        }
        finally
        {
            PDFium.FPDFBitmap_Destroy(bitmap);
        }
    }

    private void RenderPageToPngStream(PdfPage page, int dpiWidth, int dpiHeight, Stream output)
    {
        int widthPx = (int)Math.Round(page.Width / 72.0 * dpiWidth);
        int heightPx = (int)Math.Round(page.Height / 72.0 * dpiHeight);

        var bitmap = page.RenderToBitmapHandle(widthPx, heightPx, PDFium.FPDF_ANNOT);
        try
        {
            var buffer = PDFium.FPDFBitmap_GetBuffer(bitmap);
            var stride = PDFium.FPDFBitmap_GetStride(bitmap);
            PngEncoder.EncodeToStream(buffer, widthPx, heightPx, stride, output);
        }
        finally
        {
            PDFium.FPDFBitmap_Destroy(bitmap);
        }
    }

    private RawBitmap RenderPageToRawBitmap(PdfPage page, int dpiWidth, int dpiHeight)
    {
        int widthPx = (int)Math.Round(page.Width / 72.0 * dpiWidth);
        int heightPx = (int)Math.Round(page.Height / 72.0 * dpiHeight);

        var pdfBitmap = page.RenderToBitmapHandle(widthPx, heightPx, PDFium.FPDF_ANNOT);
        try
        {
            var buffer = PDFium.FPDFBitmap_GetBuffer(pdfBitmap);
            var stride = PDFium.FPDFBitmap_GetStride(pdfBitmap);
            var pixels = new byte[stride * heightPx];
            Marshal.Copy(buffer, pixels, 0, pixels.Length);
            return new RawBitmap(pixels, widthPx, heightPx, stride);
        }
        finally
        {
            PDFium.FPDFBitmap_Destroy(pdfBitmap);
        }
    }

    private static string GetExtensionForFormat(ImageFormat format) => format switch
    {
        ImageFormat.Png => "png",
        ImageFormat.Jpeg => "jpg",
        ImageFormat.Tiff => "tiff",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    public PdfForm? GetForm()
    {
        ThrowIfDisposed();

        var pdfForm = new PdfForm(Document, PageCount);
        if(pdfForm.HasFormFields)
            return pdfForm;

        pdfForm.Dispose();
        return null;
    }

    /// <summary>
    /// Saves the PDF document to a file path
    /// </summary>
    /// <param name="filePath">The path where the PDF should be saved</param>
    /// <param name="flags">Save flags (default is 0 for standard save, use PDFium.FPDF_INCREMENTAL for incremental save)</param>
    public void Save(string filePath, uint flags = 0)
    {
        ThrowIfDisposed();

        using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        SaveToStream(fileStream, flags);
    }

    /// <summary>
    /// Saves the PDF document to a stream
    /// </summary>
    /// <param name="stream">The stream to write the PDF to</param>
    /// <param name="flags">Save flags (default is 0 for standard save, use PDFium.FPDF_INCREMENTAL for incremental save)</param>
    public void SaveToStream(Stream stream, uint flags = 0)
    {
        ThrowIfDisposed();

        // Create a GCHandle to keep the stream alive during the save operation.
        // We need TWO GCHandles here:
        // 1. streamHandle - keeps the stream object alive and accessible from the callback
        // 2. delegateHandle - prevents the delegate from being garbage collected during P/Invoke
        // Without both, the GC could collect either object while PDFium is still using them.
        var streamHandle = GCHandle.Alloc(stream);

        try
        {
            // Create the write callback delegate
            WriteBlockDelegate writeDelegate = (_, dataPtr, size) =>
            {
                try
                {
                    var targetStream = (Stream)streamHandle.Target!;
                    var buffer = new byte[size];
                    Marshal.Copy(dataPtr, buffer, 0, (int)size);
                    targetStream.Write(buffer, 0, (int)size);
                    return 1; // Success
                }
                catch
                {
                    return 0; // Failure
                }
            };

            // Keep the delegate alive
            var delegateHandle = GCHandle.Alloc(writeDelegate);

            try
            {
                // Create FPDF_FILEWRITE structure
                var fileWrite = new PDFium.FPDF_FILEWRITE
                {
                    version = 1,
                    WriteBlock = Marshal.GetFunctionPointerForDelegate(writeDelegate)
                };

                // Call PDFium save function - pass fileWrite by ref
                bool success = PDFium.FPDF_SaveAsCopy(Document, ref fileWrite, flags);

                if (!success)
                {
                    var error = PDFium.FPDF_GetLastError();
                    throw new InvalidOperationException($"Failed to save PDF document. PDFium error code: {error}");
                }
            }
            finally
            {
                if (delegateHandle.IsAllocated)
                    delegateHandle.Free();
            }
        }
        finally
        {
            if (streamHandle.IsAllocated)
                streamHandle.Free();
        }
    }

    // Delegate for the WriteBlock callback
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WriteBlockDelegate(IntPtr pThis, IntPtr data, uint size);

    /// <summary>
    /// Releases all resources used by the PdfDocument.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the unmanaged resources and optionally releases managed resources.
    /// </summary>
    /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;

            DisposeActivePages();

            // Always release native handle
            if (Document != IntPtr.Zero)
            {
                PDFium.FPDF_CloseDocument(Document);
                Document = IntPtr.Zero;
            }

            ReleasePinnedMemoryDocument();

            _disposed = true;
        }
    }

    /// <summary>
    /// Destructor to ensure native resources are released if Dispose is not called.
    /// </summary>
    ~PdfDocument()
    {
        Dispose(false);
    }
}
