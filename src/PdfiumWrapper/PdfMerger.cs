using System.Runtime.InteropServices;

namespace PdfiumWrapper;

/// <summary>
/// High-level class for merging and manipulating PDF documents.
/// </summary>
/// <remarks>
/// This class is NOT thread-safe. Do not access the same PdfMerger instance from multiple threads concurrently.
/// Each PdfMerger instance should be used from a single thread at a time, or external synchronization must be provided.
/// </remarks>
public class PdfMerger : IDisposable
{
    private IntPtr _document;
    private volatile bool _disposed;
    private byte[]? _documentBytes;
    private GCHandle _documentBytesHandle;

    /// <summary>
    /// Create a new empty PDF document for merging
    /// </summary>
    public PdfMerger()
    {
        _document = PDFium.FPDF_CreateNewDocument();
        if (_document == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create new PDF document");
        }
    }

    /// <summary>
    /// Start with an existing PDF document
    /// </summary>
    public PdfMerger(string filePath, string? password = null)
    {
        _document = PDFium.FPDF_LoadDocument(filePath, password);
        if (_document == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Failed to load PDF document. Error: {PDFium.FPDF_GetLastError()}");
        }
    }

    /// <summary>
    /// Start with an existing PDF document from bytes
    /// </summary>
    public PdfMerger(byte[] data, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        LoadPinnedMemoryDocument(data, 0, data.Length, password);
    }

    /// <summary>
    /// Start with an existing PDF document from stream
    /// </summary>
    public PdfMerger(Stream pdfStream, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(pdfStream);

        // For large streams, FPDF_LoadCustomDocument is the better long-term option because it can avoid
        // the full managed copy. This constructor currently preserves the simpler ownership model where the
        // PdfMerger becomes independent from the source stream after construction.
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

    public int PageCount
    {
        get
        {
            ThrowIfDisposed();
            return PDFium.FPDF_GetPageCount(_document);
        }
    }

    private void LoadPinnedMemoryDocument(byte[] data, int offset, int length, string? password)
    {
        _documentBytes = data;
        _documentBytesHandle = GCHandle.Alloc(data, GCHandleType.Pinned);

        try
        {
            var dataPtr = IntPtr.Add(_documentBytesHandle.AddrOfPinnedObject(), offset);
            _document = PDFium.FPDF_LoadMemDocument(dataPtr, length, password);
            if (_document == IntPtr.Zero)
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

    /// <summary>
    /// Append all pages from another PDF document
    /// </summary>
    public void AppendDocument(PdfDocument sourceDoc)
    {
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
        ThrowIfDisposed();
        using var sourceDoc = new PdfDocument(filePath, password);
        AppendDocument(sourceDoc);
    }

    /// <summary>
    /// Append all pages from PDF bytes
    /// </summary>
    public void AppendDocument(byte[] pdfData, string? password = null)
    {
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
        ThrowIfDisposed();
        if (sourceDoc == null)
            throw new ArgumentNullException(nameof(sourceDoc));

        var sourceHandle = GetDocumentHandle(sourceDoc);
        bool success = PDFium.FPDF_ImportPages(_document, sourceHandle, pageRange, PageCount);

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
        ThrowIfDisposed();
        if (sourceDoc == null)
            throw new ArgumentNullException(nameof(sourceDoc));
        if (pageIndices == null)
            throw new ArgumentNullException(nameof(pageIndices));

        var sourceHandle = GetDocumentHandle(sourceDoc);
        bool success = PDFium.FPDF_ImportPagesByIndex(_document, sourceHandle,
            pageIndices, (ulong)pageIndices.Length, PageCount);

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
        ThrowIfDisposed();
        if (sourceDoc == null)
            throw new ArgumentNullException(nameof(sourceDoc));
        if (insertAtIndex < 0 || insertAtIndex > PageCount)
            throw new ArgumentOutOfRangeException(nameof(insertAtIndex));

        var sourceHandle = GetDocumentHandle(sourceDoc);
        bool success = PDFium.FPDF_ImportPages(_document, sourceHandle, pageRange, insertAtIndex);

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
        ThrowIfDisposed();
        if (sourceDoc == null)
            throw new ArgumentNullException(nameof(sourceDoc));
        if (pageIndices == null)
            throw new ArgumentNullException(nameof(pageIndices));
        if (insertAtIndex < 0 || insertAtIndex > PageCount)
            throw new ArgumentOutOfRangeException(nameof(insertAtIndex));

        var sourceHandle = GetDocumentHandle(sourceDoc);
        bool success = PDFium.FPDF_ImportPagesByIndex(_document, sourceHandle,
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
        ThrowIfDisposed();
        if (pageIndex < 0 || pageIndex >= PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));

        PDFium.FPDFPage_Delete(_document, pageIndex);
    }

    /// <summary>
    /// Delete multiple pages from the document
    /// </summary>
    /// <param name="pageIndices">0-based page indices to delete (will be sorted in descending order)</param>
    public void DeletePages(int[] pageIndices)
    {
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
        ThrowIfDisposed();
        using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        Save(fileStream, flags);
    }

    /// <summary>
    /// Save the merged document to a stream
    /// </summary>
    public void Save(Stream outputStream, uint flags = 0)
    {
        ThrowIfDisposed();
        if (outputStream == null)
            throw new ArgumentNullException(nameof(outputStream));

        using var writer = new StreamFileWriter(outputStream);
        var fileWrite = writer.GetFileWriteStruct();

        bool success = PDFium.FPDF_SaveAsCopy(_document, ref fileWrite, flags);

        if (!success)
        {
            throw new InvalidOperationException($"Failed to save PDF. Error: {PDFium.FPDF_GetLastError()}");
        }
    }

    /// <summary>
    /// Save the merged document to a byte array
    /// </summary>
    public byte[] ToBytes(uint flags = 0)
    {
        ThrowIfDisposed();
        using var memoryStream = new MemoryStream();
        Save(memoryStream, flags);
        return memoryStream.ToArray();
    }

    /// <summary>
    /// Copy viewer preferences from source document
    /// </summary>
    public void CopyViewerPreferences(PdfDocument sourceDoc)
    {
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

            if (disposing)
            {
                // Dispose managed resources if needed
            }

            // Always release native handle
            if (_document != IntPtr.Zero)
            {
                PDFium.FPDF_CloseDocument(_document);
                _document = IntPtr.Zero;
            }

            ReleasePinnedMemoryDocument();
        }
    }

    /// <summary>
    /// Destructor to ensure native resources are released if Dispose is not called.
    /// </summary>
    ~PdfMerger()
    {
        Dispose(false);
    }

    /// <summary>
    /// Helper class to write PDF data to a stream.
    /// Implements IDisposable for deterministic cleanup of GCHandle in high-throughput scenarios.
    /// </summary>
    private sealed class StreamFileWriter : IDisposable
    {
        private readonly Stream _stream;
        private readonly WriteBlockDelegate _writeDelegate;
        private GCHandle _delegateHandle;
        private bool _disposed;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int WriteBlockDelegate(IntPtr pThis, IntPtr pData, ulong size);

        public StreamFileWriter(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _writeDelegate = WriteBlock;
            _delegateHandle = GCHandle.Alloc(_writeDelegate);
        }

        public PDFium.FPDF_FILEWRITE GetFileWriteStruct()
        {
            return new PDFium.FPDF_FILEWRITE
            {
                version = 1,
                WriteBlock = Marshal.GetFunctionPointerForDelegate(_writeDelegate)
            };
        }

        private int WriteBlock(IntPtr pThis, IntPtr pData, ulong size)
        {
            try
            {
                int bytesToWrite = checked((int)size);
                unsafe
                {
                    var span = new ReadOnlySpan<byte>(pData.ToPointer(), bytesToWrite);
                    _stream.Write(span);
                }

                return 1; // Success
            }
            catch
            {
                return 0; // Failure
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                if (_delegateHandle.IsAllocated)
                {
                    _delegateHandle.Free();
                }
                _disposed = true;
            }
        }
    }
}
