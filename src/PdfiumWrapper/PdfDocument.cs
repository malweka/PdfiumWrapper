using System.Buffers;
using System.Runtime.InteropServices;

namespace PdfiumWrapper;

/// <summary>
/// High-level wrapper class for easier PDF operations.
/// </summary>
/// <remarks>
/// Thread safety: operations on different objects may run concurrently; the wrapper serializes
/// native work. Do not use one object from two threads at once.
/// PDFium allows one native call per process at a time, so concurrent documents take turns for
/// native work (loading, rendering, text, saving) while image encoding and output writes overlap.
/// Async methods process pages sequentially and wait for the native gate without blocking a thread.
/// </remarks>
public class PdfDocument : IDisposable
{
    private IntPtr _document;
    private volatile bool _disposed;
    private int _activePageCount;
    private readonly object _pagesLock = new();
    private HashSet<PdfPage>? _activePages;
    private HashSet<PdfForm>? _forms;
    private HashSet<PdfPageObject>? _detachedObjects;
    private PdfMetadata? _metadata;
    private PdfBookmarks? _bookmarks;
    private PdfAttachments? _attachments;
    private byte[]? _documentBytes;
    private GCHandle _documentBytesHandle;
    private string? _spoolPath;

    /// <summary>
    /// Create a new empty PDF document
    /// </summary>
    public PdfDocument()
    {
        using var _ = PdfiumRuntime.Enter();

        Document = PDFium.FPDF_CreateNewDocument();
        if (Document == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Failed to create new PDF document. Error: {PDFium.FPDF_GetLastError()}");
        }

        PdfiumRuntime.HandleOpened();
    }

    public PdfDocument(string filePath, string? password = null)
    {
        using var _ = PdfiumRuntime.Enter();
        LoadFileDocument(filePath, password);
    }

    /// <summary>
    /// Load a PDF from a stream. The stream is read from its current position to its end before
    /// any native work starts, so the document is independent of the stream afterwards.
    /// Inputs larger than the spool threshold (64 MB by default) are copied to a temporary file
    /// that is deleted when the document is disposed.
    /// </summary>
    public PdfDocument(Stream pdfStream, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(pdfStream);

        // User I/O happens here, before the gate: a slow stream must not stall other callers.
        var spool = SpooledInput.From(pdfStream);

        using var _ = PdfiumRuntime.Enter();
        if (spool.TempPath != null)
        {
            _spoolPath = spool.TempPath;
            try
            {
                LoadFileDocument(spool.TempPath, password);
            }
            catch
            {
                SpooledInput.TryDelete(_spoolPath);
                _spoolPath = null;
                throw;
            }
        }
        else
        {
            LoadPinnedMemoryDocument(spool.Buffer!, spool.Offset, spool.Length, password);
        }
    }

    public PdfDocument(byte[] data, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(data);

        using var _ = PdfiumRuntime.Enter();
        LoadPinnedMemoryDocument(data, 0, data.Length, password);
    }

    [NoNativeCall]
    public PdfMetadata Metadata
    {
        get
        {
            ThrowIfDisposed();
            if (_metadata == null)
            {
                _metadata = new PdfMetadata(this);
            }
            return _metadata;
        }
    }

    [NoNativeCall]
    public PdfBookmarks Bookmarks
    {
        get
        {
            ThrowIfDisposed();
            if (_bookmarks == null)
            {
                _bookmarks = new PdfBookmarks(this);
            }
            return _bookmarks;
        }
    }

    [NoNativeCall]
    public PdfAttachments Attachments
    {
        get
        {
            ThrowIfDisposed();
            if (_attachments == null)
            {
                _attachments = new PdfAttachments(this);
            }
            return _attachments;
        }
    }

    public int PageCount
    {
        get
        {
            using var _ = PdfiumRuntime.Enter();
            return PageCountCore;
        }
    }

    /// <summary>The native gate must be held.</summary>
    internal int PageCountCore
    {
        get
        {
            PdfiumRuntime.AssertHeld();
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
            using var _ = PdfiumRuntime.Enter();
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
            using var _ = PdfiumRuntime.Enter();
            ThrowIfDisposed();

            // First, get the original file ID (type 0)
            var size = PDFium.FPDF_GetFileIdentifier(Document, 0, IntPtr.Zero, 0);
            if (size == 0)
                return null;

            var buffer = ArrayPool<byte>.Shared.Rent(checked((int)size));
            try
            {
                ulong actualSize;
                unsafe
                {
                    fixed (byte* bufferPtr = buffer)
                    {
                        actualSize = PDFium.FPDF_GetFileIdentifier(Document, 0, (IntPtr)bufferPtr, size);
                    }
                }

                if (actualSize == 0)
                    return null;

                return Convert.ToHexString(buffer.AsSpan(0, checked((int)actualSize)));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    internal IntPtr Document { get => _document; set => _document = value; }

    /// <summary>
    /// Indicates whether this document has been disposed. Used by PdfPage to detect
    /// use-after-free when the owning document is disposed while pages are still alive.
    /// </summary>
    internal bool IsDisposed => _disposed;

    internal void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    #region Ownership tracking

    /// <summary>
    /// Called by PdfPage during construction to register an active page.
    /// </summary>
    internal void RegisterPage(PdfPage page)
    {
        PdfiumRuntime.AssertHeld();
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
    /// Not called from finalizers: an unreachable page implies an unreachable document.
    /// </summary>
    internal void UnregisterPage(PdfPage page)
    {
        PdfiumRuntime.AssertHeld();
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
        PdfiumRuntime.AssertHeld();
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
                page.DisposeCore();
            }
            catch
            {
                // Dispose must not throw because active pages still exist.
            }
        }
    }

    /// <summary>A page object removed from its page: destroyed with the document unless disposed first.</summary>
    internal void RegisterDetachedObject(PdfPageObject pageObject)
    {
        PdfiumRuntime.AssertHeld();
        _detachedObjects ??= new HashSet<PdfPageObject>();
        _detachedObjects.Add(pageObject);
    }

    internal void UnregisterDetachedObject(PdfPageObject pageObject)
    {
        PdfiumRuntime.AssertHeld();
        _detachedObjects?.Remove(pageObject);
    }

    internal void UnregisterForm(PdfForm form)
    {
        PdfiumRuntime.AssertHeld();
        _forms?.Remove(form);
    }

    #endregion

    #region Loading

    private void LoadFileDocument(string filePath, string? password)
    {
        PdfiumRuntime.AssertHeld();

        using (PdfiumDiagnostics.NativeInterval(NativeOp.LoadDocument))
            Document = PDFium.FPDF_LoadDocument(filePath, password);

        // The error is read in the same gated scope as the failing call.
        if (Document == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Failed to load PDF document. Error: {PDFium.FPDF_GetLastError()}");
        }

        PdfiumRuntime.HandleOpened();
    }

    private void LoadPinnedMemoryDocument(byte[] data, int offset, int length, string? password)
    {
        PdfiumRuntime.AssertHeld();

        _documentBytes = data;
        _documentBytesHandle = GCHandle.Alloc(data, GCHandleType.Pinned);

        try
        {
            var dataPtr = IntPtr.Add(_documentBytesHandle.AddrOfPinnedObject(), offset);
            using (PdfiumDiagnostics.NativeInterval(NativeOp.LoadDocument))
                Document = PDFium.FPDF_LoadMemDocument(dataPtr, length, password);
            if (Document == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"Failed to load PDF document from memory. Error: {PDFium.FPDF_GetLastError()}");
            }

            PdfiumRuntime.HandleOpened();
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

    #endregion

    public PdfPage GetPage(int pageIndex)
    {
        using var _ = PdfiumRuntime.Enter();
        return GetPageCore(pageIndex);
    }

    /// <summary>The native gate must be held.</summary>
    internal PdfPage GetPageCore(int pageIndex)
    {
        PdfiumRuntime.AssertHeld();
        ThrowIfDisposed();
        if (pageIndex < 0 || pageIndex >= PDFium.FPDF_GetPageCount(Document))
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
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();

        if (index == -1)
            index = PDFium.FPDF_GetPageCount(Document); // Append at end

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
        using var _ = PdfiumRuntime.Enter();
        DeletePageCore(pageIndex);
    }

    /// <summary>The native gate must be held.</summary>
    internal void DeletePageCore(int pageIndex)
    {
        PdfiumRuntime.AssertHeld();
        ThrowIfDisposed();

        int pageCount = PDFium.FPDF_GetPageCount(Document);
        if (pageIndex < 0 || pageIndex >= pageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex), $"Page index must be between 0 and {pageCount - 1}");

        PDFium.FPDFPage_Delete(Document, pageIndex);
    }

    /// <summary>
    /// Delete a page from the document
    /// </summary>
    /// <param name="page">The page to delete</param>
    /// <exception cref="ArgumentNullException">Thrown when page is null</exception>
    public void DeletePage(PdfPage page)
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();

        if (page == null)
            throw new ArgumentNullException(nameof(page));

        DeletePageCore(page.PageIndex);
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
        using var _ = PdfiumRuntime.Enter();

        var pages = new PdfPage[PageCountCore];
        for (int i = 0; i < pages.Length; i++)
        {
            pages[i] = GetPageCore(i);
        }
        return pages;
    }

    /// <summary>
    /// Process all pages with automatic disposal. Safe for high-throughput scenarios.
    /// </summary>
    /// <remarks>
    /// The native gate is not held while <paramref name="processor"/> runs; each page member it
    /// calls enters the gate on its own.
    /// </remarks>
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
        int pageCount;
        using (PdfiumRuntime.Enter())
        {
            ThrowIfDisposed();
            if (processor == null)
                throw new ArgumentNullException(nameof(processor));
            pageCount = PageCountCore;
        }

        var results = new TResult[pageCount];
        for (int i = 0; i < pageCount; i++)
        {
            using var page = GetPage(i);
            results[i] = processor(page);
        }
        return results;
    }

    /// <summary>
    /// Process all pages with automatic disposal. Safe for high-throughput scenarios.
    /// </summary>
    /// <remarks>
    /// The native gate is not held while <paramref name="action"/> runs; each page member it
    /// calls enters the gate on its own.
    /// </remarks>
    /// <param name="action">Action to perform on each page</param>
    /// <example>
    /// <code>
    /// // Process each page (e.g., for side effects like logging)
    /// doc.ProcessAllPages(page => Console.WriteLine($"Page {page.PageIndex}: {page.Width}x{page.Height}"));
    /// </code>
    /// </example>
    public void ProcessAllPages(Action<PdfPage> action)
    {
        int pageCount;
        using (PdfiumRuntime.Enter())
        {
            ThrowIfDisposed();
            if (action == null)
                throw new ArgumentNullException(nameof(action));
            pageCount = PageCountCore;
        }

        for (int i = 0; i < pageCount; i++)
        {
            using var page = GetPage(i);
            action(page);
        }
    }

    /// <summary>
    /// Process all pages asynchronously with automatic disposal. Safe for high-throughput scenarios.
    /// Uses Task.Yield() for UI responsiveness while processing sequentially.
    /// </summary>
    /// <typeparam name="TResult">The type of result to return for each page</typeparam>
    /// <param name="processor">Function to process each page and return a result</param>
    /// <returns>Array of results from processing each page</returns>
    public async Task<TResult[]> ProcessAllPagesAsync<TResult>(Func<PdfPage, TResult> processor)
    {
        int pageCount;
        using (await PdfiumRuntime.EnterAsync())
        {
            ThrowIfDisposed();
            if (processor == null)
                throw new ArgumentNullException(nameof(processor));
            pageCount = PageCountCore;
        }

        var results = new TResult[pageCount];
        for (int i = 0; i < pageCount; i++)
        {
            await Task.Yield();
            var page = await GetPageAsync(i).ConfigureAwait(false);
            try
            {
                results[i] = processor(page);
            }
            finally
            {
                await DisposePageAsync(page).ConfigureAwait(false);
            }
        }
        return results;
    }

    /// <summary>
    /// Process all pages asynchronously with automatic disposal. Safe for high-throughput scenarios.
    /// </summary>
    /// <param name="action">Action to perform on each page</param>
    public async Task ProcessAllPagesAsync(Action<PdfPage> action)
    {
        int pageCount;
        using (await PdfiumRuntime.EnterAsync())
        {
            ThrowIfDisposed();
            if (action == null)
                throw new ArgumentNullException(nameof(action));
            pageCount = PageCountCore;
        }

        for (int i = 0; i < pageCount; i++)
        {
            await Task.Yield();
            var page = await GetPageAsync(i).ConfigureAwait(false);
            try
            {
                action(page);
            }
            finally
            {
                await DisposePageAsync(page).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<PdfPage> GetPageAsync(int pageIndex)
    {
        using (await PdfiumRuntime.EnterAsync())
        {
            return GetPageCore(pageIndex);
        }
    }

    private static async ValueTask DisposePageAsync(PdfPage page)
    {
        using (await PdfiumRuntime.EnterAsync())
        {
            page.DisposeCore();
        }
    }

    #region Rendering helpers

    /// <summary>Checks disposal and returns the page count, failing for an empty document.</summary>
    private int RequirePages()
    {
        using var _ = PdfiumRuntime.Enter();
        return RequirePagesCore();
    }

    private async ValueTask<int> RequirePagesAsync()
    {
        using (await PdfiumRuntime.EnterAsync())
        {
            return RequirePagesCore();
        }
    }

    private int RequirePagesCore()
    {
        int pageCount = PageCountCore;
        if (pageCount == 0)
            throw new InvalidOperationException("Document has no pages");
        return pageCount;
    }

    /// <summary>
    /// Loads a page, renders it at the given resolution and closes it again, all in one gated scope.
    /// The native gate must be held.
    /// </summary>
    private BitmapLease RenderPageLeaseCore(int pageIndex, int dpiWidth, int dpiHeight, int flags, bool gray = false)
    {
        PdfiumRuntime.AssertHeld();

        var page = GetPageCore(pageIndex);
        try
        {
            int widthPx = (int)Math.Round(page.WidthCore / 72.0 * dpiWidth);
            int heightPx = (int)Math.Round(page.HeightCore / 72.0 * dpiHeight);
            return page.RenderToBitmapLeaseCore(widthPx, heightPx, flags, gray);
        }
        finally
        {
            page.DisposeCore();
        }
    }

    /// <summary>Render inside the gate. The caller encodes from the lease with the gate free.</summary>
    private BitmapLease RenderPageLease(int pageIndex, int dpiWidth, int dpiHeight, int flags, bool gray = false)
    {
        using var _ = PdfiumRuntime.Enter();
        return RenderPageLeaseCore(pageIndex, dpiWidth, dpiHeight, flags, gray);
    }

    private async ValueTask<BitmapLease> RenderPageLeaseAsync(int pageIndex, int dpiWidth, int dpiHeight, int flags, bool gray = false)
    {
        using (await PdfiumRuntime.EnterAsync())
        {
            return RenderPageLeaseCore(pageIndex, dpiWidth, dpiHeight, flags, gray);
        }
    }

    private static void RequireStreamableFormat(ImageFormat format)
    {
        if (format is not (ImageFormat.Jpeg or ImageFormat.Png))
            throw new ArgumentOutOfRangeException(nameof(format), "Use SaveAsTiff for TIFF output");
    }

    private static RawBitmap ToRawBitmap(BitmapLease lease)
        => new(lease.ToArray(), lease.Width, lease.Height, lease.Stride);

    private static byte[] EncodeJpeg(JpegEncoder encoder, BitmapLease lease, int quality)
        => encoder.Encode(lease.Buffer, lease.Width, lease.Height, lease.Stride, quality: quality);

    private static byte[] EncodePng(BitmapLease lease)
        => PngEncoder.Encode(lease.Buffer, lease.Width, lease.Height, lease.Stride);

    private static void WriteTiffPage(TiffWriter writer, BitmapLease lease, int dpiWidth, int dpiHeight,
        TiffColorMode colorMode, byte threshold, int totalPages)
    {
        // TIFF pages are rendered into an 8-bit gray bitmap (see RenderToBitmapLeaseCore).
        switch (colorMode)
        {
            case TiffColorMode.Bilevel:
                var bilevelData = lease.IsGray
                    ? PixelConverter.GrayToPackedBilevel(lease.Buffer, lease.Width, lease.Height, lease.Stride, threshold)
                    : PixelConverter.BgraToPackedBilevel(lease.Buffer, lease.Width, lease.Height, lease.Stride, threshold);
                writer.WriteBilevelPage(bilevelData, lease.Width, lease.Height, dpiWidth, dpiHeight, totalPages);
                break;

            case TiffColorMode.Grayscale:
                var grayData = lease.IsGray
                    ? PixelConverter.GrayToGrayscale(lease.Buffer, lease.Width, lease.Height, lease.Stride)
                    : PixelConverter.BgraToGrayscale(lease.Buffer, lease.Width, lease.Height, lease.Stride);
                writer.WriteGrayscalePage(grayData, lease.Width, lease.Height, dpiWidth, dpiHeight, totalPages);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(colorMode));
        }
    }

    // FPDF_NO_NATIVETEXT: on macOS PDFium otherwise draws text into 32-bit bitmaps with
    // CoreGraphics, which gives heavier glyphs (about 20% more ink) than the Windows and Linux
    // output and than the 8-bit gray TIFF render. It has no effect on other platforms.
    private const int ImageRenderFlags = PDFium.FPDF_ANNOT | PDFium.FPDF_NO_NATIVETEXT;
    private const int TiffRenderFlags = PDFium.FPDF_PRINTING | PDFium.FPDF_ANNOT | PDFium.FPDF_NO_NATIVETEXT;

    #endregion

    /// <summary>
    /// Renders all pages to raw BGRA pixel buffers.
    /// </summary>
    public RawBitmap[] RenderPages(int dpi = 300)
    {
        return RenderPages(dpi, dpi);
    }

    public RawBitmap[] RenderPages(int dpiWidth, int dpiHeight)
    {
        int pageCount = RequirePages();

        var results = new RawBitmap[pageCount];
        for (int i = 0; i < pageCount; i++)
        {
            using var lease = RenderPageLease(i, dpiWidth, dpiHeight, ImageRenderFlags);
            results[i] = ToRawBitmap(lease);
        }
        return results;
    }

    public Task<RawBitmap[]> RenderPagesAsync(int dpi = 300)
    {
        return RenderPagesAsync(dpi, dpi);
    }

    public async Task<RawBitmap[]> RenderPagesAsync(int dpiWidth, int dpiHeight)
    {
        int pageCount = await RequirePagesAsync().ConfigureAwait(false);

        var results = new RawBitmap[pageCount];
        for (int i = 0; i < pageCount; i++)
        {
            await Task.Yield();
            var lease = await RenderPageLeaseAsync(i, dpiWidth, dpiHeight, ImageRenderFlags).ConfigureAwait(false);
            await using var leaseScope = lease.ConfigureAwait(false);
            results[i] = ToRawBitmap(lease);
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
    {
        return StreamImageBytes(format, quality, dpi, dpi);
    }

    /// <inheritdoc cref="StreamImageBytes(ImageFormat, int, int)"/>
    public IEnumerable<byte[]> StreamImageBytes(ImageFormat format, int quality, int dpiWidth, int dpiHeight)
    {
        // Validate eagerly; the iterator below runs lazily.
        int pageCount = RequirePages();
        RequireStreamableFormat(format);

        return StreamImageBytesCore(format, quality, dpiWidth, dpiHeight, pageCount);
    }

    private IEnumerable<byte[]> StreamImageBytesCore(ImageFormat format, int quality, int dpiWidth, int dpiHeight, int pageCount)
    {
        if (format == ImageFormat.Jpeg)
        {
            using var encoder = new JpegEncoder();
            for (int i = 0; i < pageCount; i++)
            {
                byte[] bytes;
                using (var lease = RenderPageLease(i, dpiWidth, dpiHeight, ImageRenderFlags))
                {
                    bytes = EncodeJpeg(encoder, lease, quality);
                }
                yield return bytes;
            }
        }
        else if (format == ImageFormat.Png)
        {
            for (int i = 0; i < pageCount; i++)
            {
                byte[] bytes;
                using (var lease = RenderPageLease(i, dpiWidth, dpiHeight, ImageRenderFlags))
                {
                    bytes = EncodePng(lease);
                }
                yield return bytes;
            }
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format), "Use SaveAsTiff for TIFF output");
        }
    }

    public (double width, double height) GetPageSize(int pageIndex)
    {
        using var _ = PdfiumRuntime.Enter();
        return GetPageSizeCore(pageIndex);
    }

    private (double width, double height) GetPageSizeCore(int pageIndex)
    {
        ThrowIfDisposed();

        if (pageIndex < 0 || pageIndex >= PDFium.FPDF_GetPageCount(Document))
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
        using var _ = PdfiumRuntime.Enter();
        return GetPageLabelCore(pageIndex);
    }

    private string? GetPageLabelCore(int pageIndex)
    {
        ThrowIfDisposed();

        if (pageIndex < 0 || pageIndex >= PDFium.FPDF_GetPageCount(Document))
            throw new ArgumentOutOfRangeException(nameof(pageIndex));

        // Get the required buffer size
        var size = PDFium.FPDF_GetPageLabel(Document, pageIndex, IntPtr.Zero, 0);
        if (size == 0)
            return null;

        var buffer = ArrayPool<char>.Shared.Rent(checked((int)(size / 2)));
        try
        {
            ulong actualSize;
            unsafe
            {
                fixed (char* bufferPtr = buffer)
                {
                    actualSize = PDFium.FPDF_GetPageLabel(Document, pageIndex, (IntPtr)bufferPtr, size);
                }
            }

            if (actualSize == 0)
                return null;

            return new string(buffer, 0, checked((int)(actualSize / 2)) - 1);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Get all page labels for the document
    /// </summary>
    /// <returns>Array of page labels (null for pages without labels)</returns>
    public string?[] GetAllPageLabels()
    {
        using var _ = PdfiumRuntime.Enter();

        var labels = new string?[PageCountCore];
        for (int i = 0; i < labels.Length; i++)
        {
            labels[i] = GetPageLabelCore(i);
        }
        return labels;
    }

    public (double width, double height)[] GetAllPageSizes()
    {
        using var _ = PdfiumRuntime.Enter();

        var sizes = new (double, double)[PageCountCore];
        for (int i = 0; i < sizes.Length; i++)
        {
            sizes[i] = GetPageSizeCore(i);
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
    /// <remarks>
    /// This call returns without waiting for the native gate. It throws at once if the document is
    /// disposed or <paramref name="format"/> cannot be streamed. An empty document is reported
    /// (<see cref="InvalidOperationException"/>) when enumeration starts, because reading the page
    /// count is native work and is awaited there.
    /// </remarks>
    public IAsyncEnumerable<byte[]> StreamImageBytesAsync(ImageFormat format, int quality = 100, int dpi = 300)
    {
        return StreamImageBytesAsync(format, quality, dpi, dpi);
    }

    /// <inheritdoc cref="StreamImageBytesAsync(ImageFormat, int, int)"/>
    public IAsyncEnumerable<byte[]> StreamImageBytesAsync(ImageFormat format, int quality, int dpiWidth, int dpiHeight)
    {
        // Managed validation only: a synchronous wait for the gate here would block the caller's
        // thread. The page count is native work and is awaited inside the iterator.
        ThrowIfDisposed();
        RequireStreamableFormat(format);

        return StreamImageBytesCoreAsync(format, quality, dpiWidth, dpiHeight);
    }

    private async IAsyncEnumerable<byte[]> StreamImageBytesCoreAsync(ImageFormat format, int quality, int dpiWidth, int dpiHeight)
    {
        int pageCount = await RequirePagesAsync().ConfigureAwait(false);

        if (format == ImageFormat.Jpeg)
        {
            using var encoder = new JpegEncoder();
            for (int i = 0; i < pageCount; i++)
            {
                await Task.Yield();
                byte[] bytes;
                var lease = await RenderPageLeaseAsync(i, dpiWidth, dpiHeight, ImageRenderFlags).ConfigureAwait(false);
                await using (lease.ConfigureAwait(false))
                {
                    bytes = EncodeJpeg(encoder, lease, quality);
                }
                yield return bytes;
            }
        }
        else if (format == ImageFormat.Png)
        {
            for (int i = 0; i < pageCount; i++)
            {
                await Task.Yield();
                byte[] bytes;
                var lease = await RenderPageLeaseAsync(i, dpiWidth, dpiHeight, ImageRenderFlags).ConfigureAwait(false);
                await using (lease.ConfigureAwait(false))
                {
                    bytes = EncodePng(lease);
                }
                yield return bytes;
            }
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format), "Use SaveAsTiff for TIFF output");
        }
    }

    public void SaveAsImages(Stream[] outputStreams, ImageFormat format, int quality, int dpiWidth, int dpiHeight)
    {
        int pageCount = PageCount;

        if (outputStreams.Length != pageCount)
            throw new ArgumentException($"Number of output streams ({outputStreams.Length}) must match page count ({pageCount})");

        if (format == ImageFormat.Jpeg)
        {
            using var encoder = new JpegEncoder();
            for (int i = 0; i < pageCount; i++)
            {
                using var lease = RenderPageLease(i, dpiWidth, dpiHeight, ImageRenderFlags);
                encoder.EncodeToStream(lease.Buffer, lease.Width, lease.Height, lease.Stride, outputStreams[i],
                    quality: quality);
            }
        }
        else if (format == ImageFormat.Png)
        {
            for (int i = 0; i < pageCount; i++)
            {
                using var lease = RenderPageLease(i, dpiWidth, dpiHeight, ImageRenderFlags);
                PngEncoder.EncodeToStream(lease.Buffer, lease.Width, lease.Height, lease.Stride, outputStreams[i]);
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
        int pageCount = RequirePages();

        using var writer = new TiffWriter(outputPath);
        WriteAllPagesToTiff(writer, pageCount, dpiWidth, dpiHeight, colorMode, threshold);
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
        int pageCount = RequirePages();

        using var writer = new TiffWriter(output);
        WriteAllPagesToTiff(writer, pageCount, dpiWidth, dpiHeight, colorMode, threshold);
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
        int pageCount = await RequirePagesAsync().ConfigureAwait(false);

        using var writer = new TiffWriter(outputPath);
        await WriteAllPagesToTiffAsync(writer, pageCount, dpiWidth, dpiHeight, colorMode, threshold).ConfigureAwait(false);
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
        int pageCount = await RequirePagesAsync().ConfigureAwait(false);

        using var writer = new TiffWriter(output);
        await WriteAllPagesToTiffAsync(writer, pageCount, dpiWidth, dpiHeight, colorMode, threshold).ConfigureAwait(false);
    }

    private void WriteAllPagesToTiff(TiffWriter writer, int pageCount, int dpiWidth, int dpiHeight,
        TiffColorMode colorMode, byte threshold)
    {
        for (int i = 0; i < pageCount; i++)
        {
            // Render under the gate; convert, compress and write with the gate free.
            using var lease = RenderPageLease(i, dpiWidth, dpiHeight, TiffRenderFlags, gray: true);
            WriteTiffPage(writer, lease, dpiWidth, dpiHeight, colorMode, threshold, pageCount);
        }
    }

    private async Task WriteAllPagesToTiffAsync(TiffWriter writer, int pageCount, int dpiWidth, int dpiHeight,
        TiffColorMode colorMode, byte threshold)
    {
        for (int i = 0; i < pageCount; i++)
        {
            await Task.Yield();
            var lease = await RenderPageLeaseAsync(i, dpiWidth, dpiHeight, TiffRenderFlags, gray: true).ConfigureAwait(false);
            await using var leaseScope = lease.ConfigureAwait(false);
            WriteTiffPage(writer, lease, dpiWidth, dpiHeight, colorMode, threshold, pageCount);
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
        int pageCount = RequirePages();

        if (!Directory.Exists(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        using var encoder = new JpegEncoder();

        for (int i = 0; i < pageCount; i++)
        {
            using var lease = RenderPageLease(i, dpiWidth, dpiHeight, ImageRenderFlags);
            encoder.EncodeToFile(lease.Buffer, lease.Width, lease.Height, lease.Stride,
                Path.Combine(outputDirectory, $"{fileNamePrefix}_{i + 1:D3}.jpg"), quality: quality);
        }
    }

    public Task SaveAsJpegsAsync(string outputDirectory, string fileNamePrefix = "page", int quality = 90, int dpi = 300)
    {
        return SaveAsJpegsAsync(outputDirectory, fileNamePrefix, quality, dpi, dpi);
    }

    public async Task SaveAsJpegsAsync(string outputDirectory, string fileNamePrefix, int quality, int dpiWidth, int dpiHeight)
    {
        int pageCount = await RequirePagesAsync().ConfigureAwait(false);

        if (!Directory.Exists(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        using var encoder = new JpegEncoder();

        for (int i = 0; i < pageCount; i++)
        {
            await Task.Yield();
            var lease = await RenderPageLeaseAsync(i, dpiWidth, dpiHeight, ImageRenderFlags).ConfigureAwait(false);
            await using var leaseScope = lease.ConfigureAwait(false);
            encoder.EncodeToFile(lease.Buffer, lease.Width, lease.Height, lease.Stride,
                Path.Combine(outputDirectory, $"{fileNamePrefix}_{i + 1:D3}.jpg"), quality: quality);
        }
    }

    /// <summary>
    /// Streams JPEG bytes one page at a time using <c>IEnumerable</c>.
    /// Uses native libjpeg-turbo — no SkiaSharp involved.
    /// </summary>
    public IEnumerable<byte[]> StreamJpegBytes(int quality = 90, int dpi = 300)
    {
        return StreamJpegBytes(quality, dpi, dpi);
    }

    public IEnumerable<byte[]> StreamJpegBytes(int quality, int dpiWidth, int dpiHeight)
    {
        int pageCount = RequirePages();

        return StreamImageBytesCore(ImageFormat.Jpeg, quality, dpiWidth, dpiHeight, pageCount);
    }

    /// <summary>
    /// Streams JPEG bytes one page at a time using <c>IAsyncEnumerable</c>.
    /// Uses native libjpeg-turbo — no SkiaSharp involved.
    /// </summary>
    /// <remarks>
    /// This call returns without waiting for the native gate. An empty document is reported
    /// (<see cref="InvalidOperationException"/>) when enumeration starts.
    /// </remarks>
    public IAsyncEnumerable<byte[]> StreamJpegBytesAsync(int quality = 90, int dpi = 300)
    {
        return StreamJpegBytesAsync(quality, dpi, dpi);
    }

    public IAsyncEnumerable<byte[]> StreamJpegBytesAsync(int quality, int dpiWidth, int dpiHeight)
    {
        ThrowIfDisposed();

        return StreamImageBytesCoreAsync(ImageFormat.Jpeg, quality, dpiWidth, dpiHeight);
    }

    public void SaveAsImages(string outputDirectory, string fileNamePrefix, ImageFormat format, int quality = 100, int dpi = 300)
    {
        SaveAsImages(outputDirectory, fileNamePrefix, format, quality, dpi, dpi);
    }

    public void SaveAsImages(string outputDirectory, string fileNamePrefix, ImageFormat format, int quality, int dpiWidth, int dpiHeight)
    {
        int pageCount = RequirePages();

        if (!Directory.Exists(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        string extension = GetExtensionForFormat(format);

        if (format == ImageFormat.Jpeg)
        {
            using var encoder = new JpegEncoder();
            for (int i = 0; i < pageCount; i++)
            {
                var filePath = Path.Combine(outputDirectory, $"{fileNamePrefix}_{i + 1:D3}.{extension}");
                using var lease = RenderPageLease(i, dpiWidth, dpiHeight, ImageRenderFlags);
                encoder.EncodeToFile(lease.Buffer, lease.Width, lease.Height, lease.Stride, filePath, quality: quality);
            }
        }
        else if (format == ImageFormat.Png)
        {
            for (int i = 0; i < pageCount; i++)
            {
                var filePath = Path.Combine(outputDirectory, $"{fileNamePrefix}_{i + 1:D3}.{extension}");
                using var lease = RenderPageLease(i, dpiWidth, dpiHeight, ImageRenderFlags);
                PngEncoder.EncodeToFile(lease.Buffer, lease.Width, lease.Height, lease.Stride, filePath);
            }
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format), "Use SaveAsTiff for TIFF output");
        }
    }

    public async Task SaveAsImagesAsync(string outputDirectory, string fileNamePrefix, ImageFormat format, int quality, int dpiWidth, int dpiHeight)
    {
        int pageCount = await RequirePagesAsync().ConfigureAwait(false);

        if (!Directory.Exists(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        string extension = GetExtensionForFormat(format);

        if (format == ImageFormat.Jpeg)
        {
            using var encoder = new JpegEncoder();
            for (int i = 0; i < pageCount; i++)
            {
                await Task.Yield();
                var filePath = Path.Combine(outputDirectory, $"{fileNamePrefix}_{i + 1:D3}.{extension}");
                var lease = await RenderPageLeaseAsync(i, dpiWidth, dpiHeight, ImageRenderFlags).ConfigureAwait(false);
                await using var leaseScope = lease.ConfigureAwait(false);
                encoder.EncodeToFile(lease.Buffer, lease.Width, lease.Height, lease.Stride, filePath, quality: quality);
            }
        }
        else if (format == ImageFormat.Png)
        {
            for (int i = 0; i < pageCount; i++)
            {
                await Task.Yield();
                var filePath = Path.Combine(outputDirectory, $"{fileNamePrefix}_{i + 1:D3}.{extension}");
                var lease = await RenderPageLeaseAsync(i, dpiWidth, dpiHeight, ImageRenderFlags).ConfigureAwait(false);
                await using var leaseScope = lease.ConfigureAwait(false);
                PngEncoder.EncodeToFile(lease.Buffer, lease.Width, lease.Height, lease.Stride, filePath);
            }
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format), "Use SaveAsTiff for TIFF output");
        }
    }

    private static string GetExtensionForFormat(ImageFormat format) => format switch
    {
        ImageFormat.Png => "png",
        ImageFormat.Jpeg => "jpg",
        ImageFormat.Tiff => "tiff",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    /// <summary>
    /// Gets the document's form, or null when it has none. The form belongs to this document and
    /// is disposed with it; it may also be disposed earlier.
    /// </summary>
    public PdfForm? GetForm()
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();

        var pdfForm = new PdfForm(this, PDFium.FPDF_GetPageCount(Document));
        if (pdfForm.HasFormFields)
        {
            // Tracked so the form environment is always exited before the document closes.
            _forms ??= new HashSet<PdfForm>();
            _forms.Add(pdfForm);
            return pdfForm;
        }

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
        byte[] buffer;
        int length;
        using (PdfiumRuntime.Enter())
        {
            ThrowIfDisposed();
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            (buffer, length) = SaveCore(flags);
        }

        try
        {
            using var fileStream = PdfHelpers.OpenWriteFileStream(filePath);
            fileStream.Write(buffer, 0, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Saves the PDF document to a stream
    /// </summary>
    /// <remarks>
    /// The document is serialized into a pooled buffer first and written to <paramref name="stream"/>
    /// afterwards, so a slow stream does not hold up other PDF work. An exception thrown by the
    /// stream propagates to the caller unchanged.
    /// </remarks>
    /// <param name="stream">The stream to write the PDF to</param>
    /// <param name="flags">Save flags (default is 0 for standard save, use PDFium.FPDF_INCREMENTAL for incremental save)</param>
    public void SaveToStream(Stream stream, uint flags = 0)
    {
        byte[] buffer;
        int length;
        using (PdfiumRuntime.Enter())
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(stream);
            (buffer, length) = SaveCore(flags);
        }

        try
        {
            stream.Write(buffer, 0, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Saves the PDF document to a new byte array. Serialized into a pooled buffer under the gate,
    /// copied out after the gate is released. Used by <see cref="PdfMerger.ToBytes"/>.
    /// </summary>
    internal byte[] SaveToArray(uint flags)
    {
        byte[] buffer;
        int length;
        using (PdfiumRuntime.Enter())
        {
            ThrowIfDisposed();
            (buffer, length) = SaveCore(flags);
        }

        try
        {
            return buffer.AsSpan(0, length).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Serializes into a pooled buffer the caller must return. The native gate must be held.</summary>
    private (byte[] Buffer, int Length) SaveCore(uint flags)
    {
        PdfiumRuntime.AssertHeld();
        if (!PooledFileWriter.TrySave(Document, flags, out var buffer, out int length, out uint error))
            throw new InvalidOperationException($"Failed to save PDF document. PDFium error code: {error}");

        return (buffer, length);
    }

    /// <summary>
    /// Releases all resources used by the PdfDocument.
    /// </summary>
    public void Dispose()
    {
        using (PdfiumRuntime.Enter())
        {
            Dispose(true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the unmanaged resources and optionally releases managed resources.
    /// </summary>
    /// <param name="disposing">
    /// true when called from <see cref="Dispose()"/>: native handles are closed now, under the native gate.
    /// false when called from the finalizer: handles are queued and closed by the next gated operation.
    /// </param>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            ReleaseFromFinalizer();
            return;
        }

        if (_disposed)
            return;

        using var _ = PdfiumRuntime.Enter();
        _disposed = true;

        if (_detachedObjects != null)
        {
            var detached = _detachedObjects.ToArray();
            _detachedObjects = null;
            foreach (var pageObject in detached)
            {
                try
                {
                    pageObject.DisposeFromOwner();
                }
                catch
                {
                    // Dispose must not throw.
                }
            }
        }

        DisposeActivePages();

        if (_forms != null)
        {
            var forms = _forms.ToArray();
            _forms = null;
            foreach (var form in forms)
            {
                form.DisposeFromOwner();
            }
        }

        if (Document != IntPtr.Zero)
        {
            using (PdfiumDiagnostics.NativeInterval(NativeOp.Close))
                PDFium.FPDF_CloseDocument(Document);
            PdfiumRuntime.HandleClosed();
            Document = IntPtr.Zero;
        }

        // Only after the document is closed: PDFium reads from these for as long as it is open.
        ReleasePinnedMemoryDocument();
        SpooledInput.TryDelete(_spoolPath);
        _spoolPath = null;
    }

    /// <summary>
    /// Finalizer path. Never calls PDFium, never waits on the gate, takes no locks. Everything this
    /// document owns is queued in dependency order (page objects, forms, pages, the document, then
    /// what the document was reading from) and closed by the next gated operation on any thread.
    /// Pages and forms are queued here rather than by their own finalizers so that none of them
    /// can be closed after the document.
    /// </summary>
    private void ReleaseFromFinalizer()
    {
        if (_detachedObjects != null)
        {
            foreach (var pageObject in _detachedObjects)
                PdfiumRuntime.EnqueueRelease(NativeHandleKind.PageObject, pageObject.TakeHandleForOwnerFinalizer());
        }

        if (_forms != null)
        {
            foreach (var form in _forms)
                PdfiumRuntime.EnqueueRelease(NativeHandleKind.Form, Interlocked.Exchange(ref form._formHandle, IntPtr.Zero));
        }

        if (_activePages != null)
        {
            foreach (var page in _activePages)
                PdfiumRuntime.EnqueueRelease(NativeHandleKind.Page, Interlocked.Exchange(ref page._page, IntPtr.Zero));
        }

        PdfiumRuntime.EnqueueRelease(NativeHandleKind.Document, Interlocked.Exchange(ref _document, IntPtr.Zero));

        if (_documentBytesHandle.IsAllocated)
        {
            PdfiumRuntime.EnqueueRelease(NativeHandleKind.PinnedBuffer, GCHandle.ToIntPtr(_documentBytesHandle));
            _documentBytesHandle = default;
        }

        if (_forms != null)
        {
            foreach (var form in _forms)
                PdfiumRuntime.EnqueueRelease(NativeHandleKind.NativeMemory, Interlocked.Exchange(ref form._formInfo, IntPtr.Zero));
        }

        PdfiumRuntime.EnqueueTempFile(_spoolPath);
    }

    /// <summary>
    /// Queues native handles for deferred release if Dispose was not called.
    /// </summary>
    ~PdfDocument()
    {
        Dispose(false);
    }
}
