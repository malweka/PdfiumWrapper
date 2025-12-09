using System.Runtime.InteropServices;

namespace Malweka.PdfiumSdk;

/// <summary>
/// High-level class for merging and manipulating PDF documents
/// </summary>
public class PdfMerger : IDisposable
{
    private IntPtr _document;
    private bool _disposed;

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
    public PdfMerger(string filePath, string password = null)
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
    public PdfMerger(byte[] data, string password = null)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            _document = PDFium.FPDF_LoadMemDocument(handle.AddrOfPinnedObject(), data.Length, password);
            if (_document == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"Failed to load PDF document from memory. Error: {PDFium.FPDF_GetLastError()}");
            }
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// Start with an existing PDF document from stream
    /// </summary>
    public PdfMerger(Stream pdfStream, string password = null)
        : this(pdfStream.ReadStreamToBytes(), password)
    {
    }

    public int PageCount => PDFium.FPDF_GetPageCount(_document);

    /// <summary>
    /// Append all pages from another PDF document
    /// </summary>
    public void AppendDocument(PdfDocument sourceDoc)
    {
        AppendPages(sourceDoc, (string)null);
    }

    /// <summary>
    /// Append all pages from a PDF file
    /// </summary>
    public void AppendDocument(string filePath, string password = null)
    {
        using var sourceDoc = new PdfDocument(filePath, password);
        AppendDocument(sourceDoc);
    }

    /// <summary>
    /// Append all pages from PDF bytes
    /// </summary>
    public void AppendDocument(byte[] pdfData, string password = null)
    {
        using var sourceDoc = new PdfDocument(pdfData, password);
        AppendDocument(sourceDoc);
    }

    /// <summary>
    /// Append specific pages from another PDF document
    /// </summary>
    /// <param name="sourceDoc">Source PDF document</param>
    /// <param name="pageRange">Page range like "1,3,5-7" or null for all pages (1-based)</param>
    public void AppendPages(PdfDocument sourceDoc, string pageRange)
    {
        var sourceHandle = GetDocumentHandle(sourceDoc);
        bool success = PDFium.FPDF_ImportPages(_document, sourceHandle, pageRange, PageCount);

        if (!success)
        {
            throw new InvalidOperationException($"Failed to import pages. Error: {PDFium.FPDF_GetLastError()}");
        }
    }

    /// <summary>
    /// Append specific pages by index from another PDF document
    /// </summary>
    /// <param name="sourceDoc">Source PDF document</param>
    /// <param name="pageIndices">0-based page indices to import</param>
    public void AppendPages(PdfDocument sourceDoc, int[] pageIndices)
    {
        var sourceHandle = GetDocumentHandle(sourceDoc);
        bool success = PDFium.FPDF_ImportPagesByIndex(_document, sourceHandle,
            pageIndices, (ulong)pageIndices.Length, PageCount);

        if (!success)
        {
            throw new InvalidOperationException($"Failed to import pages. Error: {PDFium.FPDF_GetLastError()}");
        }
    }

    /// <summary>
    /// Insert all pages from another PDF at a specific position
    /// </summary>
    /// <param name="sourceDoc">Source PDF document</param>
    /// <param name="insertAtIndex">0-based index where to insert pages</param>
    public void InsertDocument(PdfDocument sourceDoc, int insertAtIndex)
    {
        InsertPages(sourceDoc, (string)null, insertAtIndex);
    }

    /// <summary>
    /// Insert specific pages from another PDF at a specific position
    /// </summary>
    /// <param name="sourceDoc">Source PDF document</param>
    /// <param name="pageRange">Page range like "1,3,5-7" or null for all pages (1-based)</param>
    /// <param name="insertAtIndex">0-based index where to insert pages</param>
    public void InsertPages(PdfDocument sourceDoc, string pageRange, int insertAtIndex)
    {
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
        using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        Save(fileStream, flags);
    }

    /// <summary>
    /// Save the merged document to a stream
    /// </summary>
    public void Save(Stream outputStream, uint flags = 0)
    {
        var writer = new StreamFileWriter(outputStream);
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
        using var memoryStream = new MemoryStream();
        Save(memoryStream, flags);
        return memoryStream.ToArray();
    }

    /// <summary>
    /// Copy viewer preferences from source document
    /// </summary>
    public void CopyViewerPreferences(PdfDocument sourceDoc)
    {
        var sourceHandle = GetDocumentHandle(sourceDoc);
        PDFium.FPDF_CopyViewerPreferences(_document, sourceHandle);
    }

    private IntPtr GetDocumentHandle(PdfDocument doc)
    {
        return doc.Document;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_document != IntPtr.Zero)
            {
                PDFium.FPDF_CloseDocument(_document);
                _document = IntPtr.Zero;
            }

            _disposed = true;
        }
    }

    /// <summary>
    /// Helper class to write PDF data to a stream
    /// </summary>
    private class StreamFileWriter
    {
        private readonly Stream _stream;
        private readonly WriteBlockDelegate _writeDelegate;
        private GCHandle _delegateHandle;

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
                byte[] buffer = new byte[size];
                Marshal.Copy(pData, buffer, 0, (int)size);
                _stream.Write(buffer, 0, (int)size);
                return 1; // Success
            }
            catch
            {
                return 0; // Failure
            }
        }

        ~StreamFileWriter()
        {
            if (_delegateHandle.IsAllocated)
            {
                _delegateHandle.Free();
            }
        }
    }
}