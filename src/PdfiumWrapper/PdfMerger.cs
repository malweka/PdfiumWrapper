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
    // The merged document. It owns the native handle, the pinned input buffer or spool file, and
    // their release on dispose or, if the merger is dropped, through its own finalizer.
    private readonly PdfDocument _target;

    /// <summary>
    /// Create a new empty PDF document for merging
    /// </summary>
    public PdfMerger()
    {
        _target = new PdfDocument();
    }

    /// <summary>
    /// Start with an existing PDF document
    /// </summary>
    public PdfMerger(string filePath, string? password = null)
    {
        _target = new PdfDocument(filePath, password);
    }

    /// <summary>
    /// Start with an existing PDF document from bytes. The array is used in place, without a copy:
    /// it is pinned and PDFium reads from it for as long as the merger is open, so it must not be
    /// modified until the merger is disposed. Pass a copy if the array may be reused.
    /// </summary>
    public PdfMerger(byte[] data, string? password = null)
    {
        _target = new PdfDocument(data, password);
    }

    /// <summary>
    /// Start with an existing PDF document from stream.
    /// The stream is read from its current position to its end before any native work starts,
    /// into a buffer the merger owns, so the merger is independent of the stream afterwards. This
    /// holds for a <see cref="MemoryStream"/> too: its buffer may be reset, overwritten or reused as
    /// soon as the constructor returns. Inputs larger than the spool threshold (64 MB by default)
    /// are copied to a temporary file that is deleted when the merger is disposed.
    /// </summary>
    public PdfMerger(Stream pdfStream, string? password = null)
    {
        _target = new PdfDocument(pdfStream, password);
    }

    public int PageCount
    {
        get
        {
            using var _ = PdfiumRuntime.Enter();
            ThrowIfDisposed();
            return _target.PageCountCore;
        }
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
            success = PDFium.FPDF_ImportPages(_target.Document, sourceHandle, pageRange, _target.PageCountCore);

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
            success = PDFium.FPDF_ImportPagesByIndex(_target.Document, sourceHandle,
                pageIndices, (ulong)pageIndices.Length, _target.PageCountCore);

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
        ThrowIfInvalidInsertIndex(insertAtIndex);

        var sourceHandle = GetDocumentHandle(sourceDoc);
        bool success;
        using (PdfiumDiagnostics.NativeInterval(NativeOp.Import))
            success = PDFium.FPDF_ImportPages(_target.Document, sourceHandle, pageRange, insertAtIndex);

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
        ThrowIfInvalidInsertIndex(insertAtIndex);

        var sourceHandle = GetDocumentHandle(sourceDoc);
        bool success;
        using (PdfiumDiagnostics.NativeInterval(NativeOp.Import))
            success = PDFium.FPDF_ImportPagesByIndex(_target.Document, sourceHandle,
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
        _target.DeletePageCore(pageIndex);
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
            _target.DeletePageCore(index);
        }
    }

    /// <summary>
    /// Save the merged document to a file
    /// </summary>
    public void Save(string outputPath, uint flags = 0)
    {
        // Checked here so that a disposed merger names itself; the target repeats the check inside
        // the gate before any native call. Entering the gate here would hold it across the write.
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        _target.Save(outputPath, flags);
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
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(outputStream);
        _target.SaveToStream(outputStream, flags);
    }

    /// <summary>
    /// Save the merged document to a byte array
    /// </summary>
    public byte[] ToBytes(uint flags = 0)
    {
        ThrowIfDisposed();
        return _target.SaveToArray(flags);
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
        bool success = PDFium.FPDF_CopyViewerPreferences(_target.Document, sourceHandle);
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

    /// <summary>The native gate must be held.</summary>
    private void ThrowIfInvalidInsertIndex(int insertAtIndex)
    {
        int pageCount = _target.PageCountCore;
        if (insertAtIndex < 0 || insertAtIndex > pageCount)
            throw new ArgumentOutOfRangeException(nameof(insertAtIndex), $"Insert index must be between 0 and {pageCount}");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_target.IsDisposed, this);
    }

    /// <summary>
    /// Releases all resources used by the PdfMerger.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the unmanaged resources and optionally releases managed resources.
    /// </summary>
    /// <param name="disposing">
    /// true when called from <see cref="Dispose()"/>: the merged document is closed now, under the native gate.
    /// false when called from a derived type's finalizer: nothing to do here, because the merged
    /// document's own finalizer queues its handles for the next gated operation.
    /// </param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
            _target.Dispose();
    }
}
