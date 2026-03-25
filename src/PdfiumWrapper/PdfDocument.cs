﻿using SkiaSharp;
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
    private IntPtr document;
    private bool _disposed;
    private PdfMetadata? _metadata;
    private PdfBookmarks? _bookmarks;
    private PdfAttachments? _attachments;

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
        : this(pdfStream.ReadStreamToBytes(), password)
    {
    }

    public PdfDocument(byte[] data, string? password = null)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            Document = PDFium.FPDF_LoadMemDocument(handle.AddrOfPinnedObject(), data.Length, password);
            if (Document == IntPtr.Zero)
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

    public PdfMetadata Metadata
    {
        get
        {
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
            if (_attachments == null)
            {
                _attachments = new PdfAttachments(Document);
            }
            return _attachments;
        }
    }

    public int PageCount => PDFium.FPDF_GetPageCount(Document);

    /// <summary>
    /// Gets the document permissions as a PdfPermissions flags enum
    /// </summary>
    public PdfPermissions Permissions
    {
        get
        {
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

    internal IntPtr Document { get => document; set => document = value; }

    public PdfPage GetPage(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));

        return new PdfPage(Document, pageIndex);
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
        if (index == -1)
            index = PageCount; // Append at end

        var pageHandle = PDFium.FPDFPage_New(Document, index, width, height);
        if (pageHandle == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to create new page. Error: {PDFium.FPDF_GetLastError()}");

        // Close the page handle and re-open it using the standard method
        PDFium.FPDF_ClosePage(pageHandle);
        return new PdfPage(Document, index);
    }

    /// <summary>
    /// Delete a page from the document by index
    /// </summary>
    /// <param name="pageIndex">The 0-based index of the page to delete</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when pageIndex is out of range</exception>
    public void DeletePage(int pageIndex)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PdfDocument));

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

    public SKBitmap[] ConvertToBitmaps(int dpi = 300)
    {
        return ConvertToBitmaps(dpi, dpi);
    }

    public SKBitmap[] ConvertToBitmaps(int dpiWidth, int dpiHeight)
    {
        if (PageCount == 0)
            throw new InvalidOperationException("Document has no pages");

        var bitmaps = new List<SKBitmap>(PageCount);
        try
        {
            for (int i = 0; i < PageCount; i++)
            {
                using var page = GetPage(i);
                bitmaps.Add(RenderPageToSkBitmap(page, dpiWidth, dpiHeight));
            }
            return bitmaps.ToArray();
        }
        catch
        {
            // Clean up any bitmaps created before the exception
            foreach (var bitmap in bitmaps)
                bitmap.Dispose();
            throw;
        }
    }

    public async Task<SKBitmap[]> ConvertToBitmapsAsync(int dpi = 300)
    {
        return await ConvertToBitmapsAsync(dpi, dpi);
    }

    public async Task<SKBitmap[]> ConvertToBitmapsAsync(int dpiWidth, int dpiHeight)
    {
        if (PageCount == 0)
            throw new InvalidOperationException("Document has no pages");

        var bitmaps = new List<SKBitmap>(PageCount);
        try
        {
            for (int i = 0; i < PageCount; i++)
            {
                await Task.Yield();
                using var page = GetPage(i);
                bitmaps.Add(RenderPageToSkBitmap(page, dpiWidth, dpiHeight));
            }
            return bitmaps.ToArray();
        }
        catch
        {
            // Clean up any bitmaps created before the exception
            foreach (var bitmap in bitmaps)
                bitmap.Dispose();
            throw;
        }
    }

    public List<byte[]> ConvertToImageBytes(SKEncodedImageFormat format, int quality = 100, int dpi = 300)
    {
        return ConvertToImageBytes(format, quality, dpi, dpi);
    }

    public List<byte[]> ConvertToImageBytes(SKEncodedImageFormat format, int quality, int dpiWidth, int dpiHeight)
    {
        if (PageCount == 0)
            throw new InvalidOperationException("Document has no pages");

        var imageList = new List<byte[]>();
        for (int i = 0; i < PageCount; i++)
        {
            using var page = GetPage(i);
            using var bitmap = RenderPageToSkBitmap(page, dpiWidth, dpiHeight);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(format, quality);
            imageList.Add(data.ToArray());
        }
        return imageList;
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

    public async Task<List<byte[]>> ConvertToImageBytesAsync(SKEncodedImageFormat format, int quality = 100, int dpi = 300)
    {
        return await ConvertToImageBytesAsync(format, quality, dpi, dpi);
    }

    public async Task<List<byte[]>> ConvertToImageBytesAsync(SKEncodedImageFormat format, int quality, int dpiWidth, int dpiHeight)
    {
        if (PageCount == 0)
            throw new InvalidOperationException("Document has no pages");

        var imageList = new List<byte[]>();
        
        for (int i = 0; i < PageCount; i++)
        {
            await Task.Yield();
            using var page = GetPage(i);
            using var bitmap = RenderPageToSkBitmap(page, dpiWidth, dpiHeight);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(format, quality);
            imageList.Add(data.ToArray());
        }
        
        return imageList;
    }

    public void SaveAsImages(Stream[] outputStreams, SKEncodedImageFormat format, int quality, int dpiWidth, int dpiHeight)
    {
        if (outputStreams.Length != PageCount)
            throw new ArgumentException($"Number of output streams ({outputStreams.Length}) must match page count ({PageCount})");

        for (int i = 0; i < PageCount; i++)
        {
            using var page = GetPage(i);
            using var bitmap = RenderPageToSkBitmap(page, dpiWidth, dpiHeight);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(format, quality);
            data.SaveTo(outputStreams[i]);
        }
    }

    // Convenience methods for saving to directory (calls stream-based methods internally)
    public void SaveAsPngs(string outputDirectory, string fileNamePrefix = "page", int dpi = 300)
    {
        SaveAsImages(outputDirectory, fileNamePrefix, SKEncodedImageFormat.Png, 100, dpi, dpi);
    }

    public void SaveAsJpegs(string outputDirectory, string fileNamePrefix = "page", int quality = 90, int dpi = 300)
    {
        SaveAsImages(outputDirectory, fileNamePrefix, SKEncodedImageFormat.Jpeg, quality, dpi, dpi);
    }

    public void SaveAsImages(string outputDirectory, string fileNamePrefix, SKEncodedImageFormat format, int quality = 100, int dpi = 300)
    {
        SaveAsImages(outputDirectory, fileNamePrefix, format, quality, dpi, dpi);
    }

    public void SaveAsImages(string outputDirectory, string fileNamePrefix, SKEncodedImageFormat format, int quality, int dpiWidth, int dpiHeight)
    {
        if (PageCount == 0)
            throw new InvalidOperationException("Document has no pages");

        if (!Directory.Exists(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        string extension = GetExtensionForFormat(format);

        for (int i = 0; i < PageCount; i++)
        {
            var fileName = $"{fileNamePrefix}_{i + 1:D3}.{extension}";
            var filePath = Path.Combine(outputDirectory, fileName);

            using var stream = File.OpenWrite(filePath);
            using var page = GetPage(i);
            using var bitmap = RenderPageToSkBitmap(page, dpiWidth, dpiHeight);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(format, quality);
            data.SaveTo(stream);
        }
    }

    public async Task SaveAsImagesAsync(string outputDirectory, string fileNamePrefix, SKEncodedImageFormat format, int quality, int dpiWidth, int dpiHeight)
    {
        if (PageCount == 0)
            throw new InvalidOperationException("Document has no pages");

        if (!Directory.Exists(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        string extension = GetExtensionForFormat(format);

        for (int i = 0; i < PageCount; i++)
        {
            await Task.Yield();
            
            using var page = GetPage(i);
            using var bitmap = RenderPageToSkBitmap(page, dpiWidth, dpiHeight);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(format, quality);
            
            var fileName = $"{fileNamePrefix}_{i + 1:D3}.{extension}";
            var filePath = Path.Combine(outputDirectory, fileName);

            using var stream = File.Create(filePath);
            await data.AsStream().CopyToAsync(stream);
        }
    }

    private SKBitmap RenderPageToSkBitmap(PdfPage page, int dpiWidth, int dpiHeight)
    {
        // Get original page dimensions in points
        var originalWidthPoints = page.Width;
        var originalHeightPoints = page.Height;

        // Convert points to inches (points / 72)
        var originalWidthInches = originalWidthPoints / 72.0;
        var originalHeightInches = originalHeightPoints / 72.0;

        // Calculate final dimensions in pixels based on DPI
        int finalWidthPixels = (int)Math.Round(originalWidthInches * dpiWidth);
        int finalHeightPixels = (int)Math.Round(originalHeightInches * dpiHeight);

        // Render PDF page to bytes using PDFium (BGRA format)
        var pdfBytes = page.RenderToBytes(finalWidthPixels, finalHeightPixels, PDFium.FPDF_ANNOT);

        // Create SKBitmap and copy the BGRA data
        var bitmap = new SKBitmap(finalWidthPixels, finalHeightPixels, SKColorType.Bgra8888, SKAlphaType.Premul);

        // Copy the PDFium buffer directly into the SKBitmap
        var pixels = bitmap.GetPixels();
        Marshal.Copy(pdfBytes, 0, pixels, pdfBytes.Length);

        return bitmap;
    }


    private static string GetExtensionForFormat(SKEncodedImageFormat format)
    {
        return format switch
        {
            SKEncodedImageFormat.Png => "png",
            SKEncodedImageFormat.Jpeg => "jpg",
            SKEncodedImageFormat.Webp => "webp",
            SKEncodedImageFormat.Gif => "gif",
            SKEncodedImageFormat.Bmp => "bmp",
            SKEncodedImageFormat.Ico => "ico",
            _ => "img"
        };
    }

    public PdfForm? GetForm()
    {
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
        if (_disposed)
            throw new ObjectDisposedException(nameof(PdfDocument));

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
        if (_disposed)
            throw new ObjectDisposedException(nameof(PdfDocument));

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
            if (disposing)
            {
                // Dispose managed resources if needed
            }

            // Always release native handle
            if (Document != IntPtr.Zero)
            {
                PDFium.FPDF_CloseDocument(Document);
                Document = IntPtr.Zero;
            }

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