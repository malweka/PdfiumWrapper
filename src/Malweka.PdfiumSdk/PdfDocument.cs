using SkiaSharp;
using System.Runtime.InteropServices;

namespace Malweka.PdfiumSdk;

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
    private bool _disposed;
    private PdfMetadata? _metadata;
    private PdfBookmarks? _bookmarks;
    private PdfAttachments? _attachments;

    static PdfDocument()
    {
        PDFium.FPDF_InitLibrary();
    }

    public PdfDocument(string filePath, string password = null)
    {
        _document = PDFium.FPDF_LoadDocument(filePath, password);
        if (_document == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Failed to load PDF document. Error: {PDFium.FPDF_GetLastError()}");
        }
    }

    public PdfDocument(Stream pdfStream, string password = null)
        : this(pdfStream.ReadStreamToBytes(), password)
    {
    }

    public PdfDocument(byte[] data, string password = null)
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

    public PdfMetadata Metadata
    {
        get
        {
            if (_metadata == null)
            {
                _metadata = new PdfMetadata(_document);
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
                _bookmarks = new PdfBookmarks(_document);
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
                _attachments = new PdfAttachments(_document);
            }
            return _attachments;
        }
    }

    public int PageCount => PDFium.FPDF_GetPageCount(_document);

    public uint Permissions => PDFium.FPDF_GetDocPermissions(_document);

    public PdfPage GetPage(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));

        return new PdfPage(_document, pageIndex);
    }

    public PdfPage[] GetAllPages()
    {
        var pages = new PdfPage[PageCount];
        for (int i = 0; i < PageCount; i++)
        {
            pages[i] = GetPage(i);
        }
        return pages;
    }

    public SKBitmap[] ConvertToBitmaps(int dpi = 300)
    {
        return ConvertToBitmaps(dpi, dpi);
    }

    public SKBitmap[] ConvertToBitmaps(int dpiWidth, int dpiHeight)
    {
        if (PageCount == 0)
            throw new InvalidOperationException("Document has no pages");

        var bitmaps = new SKBitmap[PageCount];
        for (int i = 0; i < PageCount; i++)
        {
            using var page = GetPage(i);
            bitmaps[i] = RenderPageToSkBitmap(page, dpiWidth, dpiHeight);
        }
        return bitmaps;
    }

    public async Task<SKBitmap[]> ConvertToBitmapsAsync(int dpi = 300)
    {
        return await ConvertToBitmapsAsync(dpi, dpi);
    }

    public async Task<SKBitmap[]> ConvertToBitmapsAsync(int dpiWidth, int dpiHeight)
    {
        if (PageCount == 0)
            throw new InvalidOperationException("Document has no pages");

        var bitmaps = new SKBitmap[PageCount];
        
        for (int i = 0; i < PageCount; i++)
        {
            await Task.Yield();
            using var page = GetPage(i);
            bitmaps[i] = RenderPageToSkBitmap(page, dpiWidth, dpiHeight);
        }
        
        return bitmaps;
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

        var result = PDFium.FPDF_GetPageSizeByIndex(_document, pageIndex, out double width, out double height);
        if (result == 0)
            throw new InvalidOperationException($"Failed to get size for page {pageIndex}");

        return (width, height);
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
        var pdfForm = new PdfForm(_document, PageCount);
        if(pdfForm.HasFormFields)
            return pdfForm;

        pdfForm.Dispose();
        return null;
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
}