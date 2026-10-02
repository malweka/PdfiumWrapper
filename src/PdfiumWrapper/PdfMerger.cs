using System.Buffers;
using System.Runtime.InteropServices;

namespace PdfiumWrapper;

/// <summary>
/// High-level class for merging and manipulating PDF documents.
/// </summary>
/// <remarks>
/// Thread safety: operations on different objects may run concurrently; the wrapper serializes
/// native work. Do not use one object from two threads at once.
/// </remarks>
public class PdfMerger : IDisposable
{
    private IntPtr _document;
    private volatile bool _disposed;
    private byte[]? _documentBytes;
    private GCHandle _documentBytesHandle;
    private string? _spoolPath;

    /// <summary>
    /// Create a new empty PDF document for merging
    /// </summary>
    public PdfMerger()
    {
        using var _ = PdfiumRuntime.Enter();

        _document = PDFium.FPDF_CreateNewDocument();
        if (_document == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create new PDF document");
        }

        PdfiumRuntime.HandleOpened();
    }

    /// <summary>
    /// Start with an existing PDF document
    /// </summary>
    public PdfMerger(string filePath, string? password = null)
    {
        using var _ = PdfiumRuntime.Enter();
        LoadFileDocument(filePath, password);
    }

    /// <summary>
    /// Start with an existing PDF document from bytes
    /// </summary>
    public PdfMerger(byte[] data, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(data);

        using var _ = PdfiumRuntime.Enter();
        LoadPinnedMemoryDocument(data, 0, data.Length, password);
    }

    /// <summary>
    /// Start with an existing PDF document from stream.
    /// The stream is read from its current position to its end before any native work starts,
    /// so the merger is independent of the stream afterwards. Inputs larger than the spool
    /// threshold (64 MB by default) are copied to a temporary file that is deleted when the
    /// merger is disposed.
    /// </summary>
    public PdfMerger(Stream pdfStream, string? password = null)
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

    public int PageCount
    {
        get
        {
            using var _ = PdfiumRuntime.Enter();
            ThrowIfDisposed();
            return PDFium.FPDF_GetPageCount(_document);
        }
    }

    private void LoadFileDocument(string filePath, string? password)
    {
        PdfiumRuntime.AssertHeld();

        using (PdfiumDiagnostics.NativeInterval(NativeOp.LoadDocument))
            _document = PDFium.FPDF_LoadDocument(filePath, password);

        // The error is read in the same gated scope as the failing call.
        if (_document == IntPtr.Zero)
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
                _document = PDFium.FPDF_LoadMemDocument(dataPtr, length, password);
            if (_document == IntPtr.Zero)
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

    /// <summary>
    /// Append all pages from another PDF document
    /// </summary>
    public void AppendDocument(PdfDocument sourceDoc)
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();
        if (sourceDoc == null)
            throw new ArgumentNullException(nameof(sourceDoc));

        AppendPages(sourceDoc, (string?)null);
    }

    /// <summary>
    /// Append all pages from a PDF file
    /// </summary>
    public void AppendDocument(string filePath, string? password = null)
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();
        using var sourceDoc = new PdfDocument(filePath, password);
        AppendDocument(sourceDoc);
    }

    /// <summary>
    /// Append all pages from PDF bytes
    /// </summary>
    public void AppendDocument(byte[] pdfData, string? password = null)
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();
        using var sourceDoc = new PdfDocument(pdfData, password);
        AppendDocument(sourceDoc);
    }

    /// <summary>
    /// Append specific pages from another PDF document
    /// </summary>
    /// <param name="sourceDoc">Source PDF document</param>
    /// <param name="pageRange">Page range like "1,3,5-7" or null for all pages (1-based)</param>
    public void AppendPages(PdfDocument sourceDoc, string? pageRange)
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();
        if (sourceDoc == null)
            throw new ArgumentNullException(nameof(sourceDoc));

        var sourceHandle = GetDocumentHandle(sourceDoc);
        bool success;
        using (PdfiumDiagnostics.NativeInterval(NativeOp.Import))
            success = PDFium.FPDF_ImportPages(_document, sourceHandle, pageRange, PDFium.FPDF_GetPageCount(_document));

        if (!success)
        {
            throw new InvalidOperationException($"Failed to import pages. Error: {PDFium.FPDF_GetLastError()}");
        }
    }

    /// <summary>
    /// Append specific pages from a PDF file
    /// </summary>
    /// <param name="filePath">Path to the PDF file</param>
    /// <param name="pageRange">Page range like "1,3,5-7" or null for all pages (1-based)</param>
    /// <param name="password">Optional password for encrypted PDFs</param>
    public void AppendPages(string filePath, string? pageRange, string? password = null)
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();
        using var sourceDoc = new PdfDocument(filePath, password);
        AppendPages(sourceDoc, pageRange);
    }

    /// <summary>
    /// Append specific pages by index from another PDF document
    /// </summary>
    /// <param name="sourceDoc">Source PDF document</param>
    /// <param name="pageIndices">0-based page indices to import</param>
    public void AppendPages(PdfDocument sourceDoc, int[] pageIndices)
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();
        if (sourceDoc == null)
            throw new ArgumentNullException(nameof(sourceDoc));
        if (pageIndices == null)
            throw new ArgumentNullException(nameof(pageIndices));

        var sourceHandle = GetDocumentHandle(sourceDoc);
        bool success;
        using (PdfiumDiagnostics.NativeInterval(NativeOp.Import))
            success = PDFium.FPDF_ImportPagesByIndex(_document, sourceHandle,
                pageIndices, (ulong)pageIndices.Length, PDFium.FPDF_GetPageCount(_document));

        if (!success)
        {
            throw new InvalidOperationException($"Failed to import pages. Error: {PDFium.FPDF_GetLastError()}");
        }
    }

    /// <summary>
    /// Append specific pages by index from a PDF file
    /// </summary>
    /// <param name="filePath">Path to the PDF file</param>
    /// <param name="pageIndices">0-based page indices to import</param>
    /// <param name="password">Optional password for encrypted PDFs</param>
    public void AppendPages(string filePath, int[] pageIndices, string? password = null)
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();
        using var sourceDoc = new PdfDocument(filePath, password);
        AppendPages(sourceDoc, pageIndices);
    }

    /// <summary>
    /// Insert all pages from another PDF at a specific position
    /// </summary>
    /// <param name="sourceDoc">Source PDF document</param>
    /// <param name="insertAtIndex">0-based index where to insert pages</param>
    public void InsertDocument(PdfDocument sourceDoc, int insertAtIndex)
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();
        InsertPages(sourceDoc, (string?)null, insertAtIndex);
    }

    /// <summary>
    /// Insert specific pages from another PDF at a specific position
    /// </summary>
    /// <param name="sourceDoc">Source PDF document</param>
    /// <param name="pageRange">Page range like "1,3,5-7" or null for all pages (1-based)</param>
    /// <param name="insertAtIndex">0-based index where to insert pages</param>
    public void InsertPages(PdfDocument sourceDoc, string? pageRange, int insertAtIndex)
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();
        if (sourceDoc == null)
            throw new ArgumentNullException(nameof(sourceDoc));
        if (insertAtIndex < 0 || insertAtIndex > PDFium.FPDF_GetPageCount(_document))
            throw new ArgumentOutOfRangeException(nameof(insertAtIndex));

        var sourceHandle = GetDocumentHandle(sourceDoc);
        bool success;
        using (PdfiumDiagnostics.NativeInterval(NativeOp.Import))
            success = PDFium.FPDF_ImportPages(_document, sourceHandle, pageRange, insertAtIndex);

        if (!success)
        {
            throw new InvalidOperationException($"Failed to import pages. Error: {PDFium.FPDF_GetLastError()}");
        }
    }

    /// <summary>
    /// Insert specific pages by index from another PDF at a specific position
    /// </summary>
    /// <param name="sourceDoc">Source PDF document</param>
    /// <param name="pageIndices">0-based page indices to import</param>
    /// <param name="insertAtIndex">0-based index where to insert pages</param>
    public void InsertPages(PdfDocument sourceDoc, int[] pageIndices, int insertAtIndex)
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();
        if (sourceDoc == null)
            throw new ArgumentNullException(nameof(sourceDoc));
        if (pageIndices == null)
            throw new ArgumentNullException(nameof(pageIndices));
        if (insertAtIndex < 0 || insertAtIndex > PDFium.FPDF_GetPageCount(_document))
            throw new ArgumentOutOfRangeException(nameof(insertAtIndex));

        var sourceHandle = GetDocumentHandle(sourceDoc);
        bool success;
        using (PdfiumDiagnostics.NativeInterval(NativeOp.Import))
            success = PDFium.FPDF_ImportPagesByIndex(_document, sourceHandle,
                pageIndices, (ulong)pageIndices.Length, insertAtIndex);

        if (!success)
        {
            throw new InvalidOperationException($"Failed to import pages. Error: {PDFium.FPDF_GetLastError()}");
        }
    }

    /// <summary>
    /// Delete a page from the document
    /// </summary>
    /// <param name="pageIndex">0-based page index to delete</param>
    public void DeletePage(int pageIndex)
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();
        if (pageIndex < 0 || pageIndex >= PDFium.FPDF_GetPageCount(_document))
            throw new ArgumentOutOfRangeException(nameof(pageIndex));

        PDFium.FPDFPage_Delete(_document, pageIndex);
    }

    /// <summary>
    /// Delete multiple pages from the document
    /// </summary>
    /// <param name="pageIndices">0-based page indices to delete (will be sorted in descending order)</param>
    public void DeletePages(int[] pageIndices)
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();
        if (pageIndices == null)
            throw new ArgumentNullException(nameof(pageIndices));

        // Sort in descending order to avoid index shifting issues
        var sortedIndices = pageIndices.OrderByDescending(i => i).ToArray();

        foreach (var index in sortedIndices)
        {
            DeletePage(index);
        }
    }

    /// <summary>
    /// Save the merged document to a file
    /// </summary>
    public void Save(string outputPath, uint flags = 0)
    {
        byte[] buffer;
        int length;
        using (PdfiumRuntime.Enter())
        {
            ThrowIfDisposed();
            ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
            (buffer, length) = SaveCore(flags);
        }

        try
        {
            using var fileStream = PdfHelpers.OpenWriteFileStream(outputPath);
            fileStream.Write(buffer, 0, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Save the merged document to a stream
    /// </summary>
    /// <remarks>
    /// The document is serialized into a pooled buffer first and written to
    /// <paramref name="outputStream"/> afterwards, so a slow stream does not hold up other PDF work.
    /// An exception thrown by the stream propagates to the caller unchanged.
    /// </remarks>
    public void Save(Stream outputStream, uint flags = 0)
    {
        byte[] buffer;
        int length;
        using (PdfiumRuntime.Enter())
        {
            ThrowIfDisposed();
            if (outputStream == null)
                throw new ArgumentNullException(nameof(outputStream));
            (buffer, length) = SaveCore(flags);
        }

        try
        {
            outputStream.Write(buffer, 0, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Save the merged document to a byte array
    /// </summary>
    public byte[] ToBytes(uint flags = 0)
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
        if (!PooledFileWriter.TrySave(_document, flags, out var buffer, out int length, out uint error))
            throw new InvalidOperationException($"Failed to save PDF. Error: {error}");

        return (buffer, length);
    }

    /// <summary>
    /// Copy viewer preferences from source document
    /// </summary>
    public void CopyViewerPreferences(PdfDocument sourceDoc)
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();
        if (sourceDoc == null)
            throw new ArgumentNullException(nameof(sourceDoc));

        var sourceHandle = GetDocumentHandle(sourceDoc);
        bool success = PDFium.FPDF_CopyViewerPreferences(_document, sourceHandle);
        if (!success)
        {
            throw new InvalidOperationException($"Failed to copy viewer preferences. Error: {PDFium.FPDF_GetLastError()}");
        }
    }

    private IntPtr GetDocumentHandle(PdfDocument doc)
    {
        ObjectDisposedException.ThrowIf(doc.IsDisposed, doc);
        return doc.Document;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>
    /// Releases all resources used by the PdfMerger.
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
    /// true when called from <see cref="Dispose()"/>: the document is closed now, under the native gate.
    /// false when called from the finalizer: the handle is queued and closed by the next gated operation.
    /// </param>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            // Finalizer thread: never call PDFium, never wait on the gate.
            // The document goes first; what it was reading from is released after it closes.
            PdfiumRuntime.EnqueueRelease(NativeHandleKind.Document, Interlocked.Exchange(ref _document, IntPtr.Zero));
            if (_documentBytesHandle.IsAllocated)
            {
                PdfiumRuntime.EnqueueRelease(NativeHandleKind.PinnedBuffer, GCHandle.ToIntPtr(_documentBytesHandle));
                _documentBytesHandle = default;
            }

            PdfiumRuntime.EnqueueTempFile(_spoolPath);
            return;
        }

        if (_disposed)
            return;

        using var _ = PdfiumRuntime.Enter();
        _disposed = true;

        if (_document != IntPtr.Zero)
        {
            using (PdfiumDiagnostics.NativeInterval(NativeOp.Close))
                PDFium.FPDF_CloseDocument(_document);
            PdfiumRuntime.HandleClosed();
            _document = IntPtr.Zero;
        }

        // Only after the document is closed: PDFium reads from these for as long as it is open.
        ReleasePinnedMemoryDocument();
        SpooledInput.TryDelete(_spoolPath);
        _spoolPath = null;
    }

    /// <summary>
    /// Queues the native handle for deferred release if Dispose was not called.
    /// </summary>
    ~PdfMerger()
    {
        Dispose(false);
    }

}
