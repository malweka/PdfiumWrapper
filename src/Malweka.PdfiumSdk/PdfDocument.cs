using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImageMagick;
using ExifTag = ImageMagick.ExifTag;

namespace Malweka.PdfiumSdk;

/// <summary>
/// High-level wrapper class for easier PDF operations
/// </summary>
public class PdfDocument : IDisposable
{
    private IntPtr _document;
    private bool _disposed;

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

    public int PageCount => PDFium.FPDF_GetPageCount(_document);

    public uint Permissions => PDFium.FPDF_GetDocPermissions(_document);

    public PdfPage GetPage(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));

        return new PdfPage(_document, pageIndex);
    }

    public void ConvertToTiff(string outputPath, int dpi,
        CompressionMethod compression = CompressionMethod.LZW)
    {
        ConvertToTiff(outputPath, dpi, dpi, compression);
    }

    public void ConvertToTiff(string outputPath, int dpiWidth, int dpiHeight,
        CompressionMethod compression = CompressionMethod.LZW)
    {
        if (PageCount == 0)
            throw new InvalidOperationException("Document has no pages");

        using var images = new MagickImageCollection();

        for (int i = 0; i < PageCount; i++)
        {
            using var page = GetPage(i);
            var magickImage = RenderPageToMagickImage(page, dpiWidth, dpiHeight, compression);
            images.Add(magickImage);
        }

        images.Write(outputPath);
    }

    public void ConvertToPngs(string outputDirectory, string fileNamePrefix = "page", int dpi = 300)
    {
        ConvertToPngs(outputDirectory, fileNamePrefix, dpi, dpi);
    }

    public void ConvertToPngs(string outputDirectory, string fileNamePrefix, int dpiWidth, int dpiHeight)
    {
        if (PageCount == 0)
            throw new InvalidOperationException("Document has no pages");

        if (!Directory.Exists(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        for (int i = 0; i < PageCount; i++)
        {
            using var page = GetPage(i);
            using var magickImage = RenderPageToMagickImage(page, dpiWidth: dpiWidth, dpiHeight: dpiHeight);

            var fileName = $"{fileNamePrefix}_{i + 1:D3}.png";
            var filePath = Path.Combine(outputDirectory, fileName);

            magickImage.Format = MagickFormat.Png;
            magickImage.Write(filePath);
        }
    }

    public void ConvertToJpegs(string outputDirectory, string fileNamePrefix = "page", int dpiWidth = 300,
        int dpiHeight = 300)
    {
        if (PageCount == 0)
            throw new InvalidOperationException("Document has no pages");

        if (!Directory.Exists(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        for (int i = 0; i < PageCount; i++)
        {
            using var page = GetPage(i);
            using var magickImage = RenderPageToMagickImage(page, dpiWidth: dpiWidth, dpiHeight: dpiHeight);

            var fileName = $"{fileNamePrefix}_{i + 1:D3}.jpg";
            var filePath = Path.Combine(outputDirectory, fileName);

            magickImage.Format = MagickFormat.Jpg;
            magickImage.Write(filePath);
            // AddExifToFile(filePath, magickImage.Width > magickImage.Height); // Add EXIF based on orientation
        }
    }
    
    // Add this field to your PdfDocument class
private readonly object _pdfiumLock = new object();

// Replace your async methods with these thread-safe versions:

public async Task ConvertToTiffAsync(string outputPath, int dpiWidth, int dpiHeight,
    CompressionMethod compression = CompressionMethod.LZW)
{
    if (PageCount == 0)
        throw new InvalidOperationException("Document has no pages");

    // Pre-render all pages with synchronization to avoid PDFium threading issues
    var renderTasks = new Task<MagickImage>[PageCount];

    for (int i = 0; i < PageCount; i++)
    {
        int pageIndex = i; // Capture loop variable
        renderTasks[i] = Task.Run(() =>
        {
            // Synchronize PDFium operations to prevent concurrent access
            lock (_pdfiumLock)
            {
                using var page = GetPage(pageIndex);
                return RenderPageToMagickImage(page, dpiWidth, dpiHeight, compression);
            }
        });
    }

    // Wait for all pages to render
    var renderedImages = await Task.WhenAll(renderTasks);

    try
    {
        // Create the TIFF collection and write to file
        using var images = new MagickImageCollection();

        foreach (var image in renderedImages)
        {
            images.Add(image);
        }

        images.Write(outputPath);
    }
    finally
    {
        // Dispose all rendered images
        foreach (var image in renderedImages)
        {
            image?.Dispose();
        }
    }
}

public async Task ConvertToPngsAsync(string outputDirectory, string fileNamePrefix, int dpiWidth = 300, int dpiHeight = 300)
{
    if (PageCount == 0)
        throw new InvalidOperationException("Document has no pages");

    if (!Directory.Exists(outputDirectory))
        Directory.CreateDirectory(outputDirectory);

    // Create tasks for each page rendering and saving
    var renderTasks = new Task[PageCount];

    for (int i = 0; i < PageCount; i++)
    {
        int pageIndex = i; // Capture loop variable
        renderTasks[i] = Task.Run(() =>
        {
            MagickImage magickImage;
            
            // Synchronize PDFium operations
            lock (_pdfiumLock)
            {
                using var page = GetPage(pageIndex);
                magickImage = RenderPageToMagickImage(page, dpiWidth, dpiHeight);
            }
            
            // File I/O can happen outside the lock
            try
            {
                var fileName = $"{fileNamePrefix}_{pageIndex + 1:D3}.png";
                var filePath = Path.Combine(outputDirectory, fileName);

                magickImage.Format = MagickFormat.Png;
                magickImage.Write(filePath);
            }
            finally
            {
                magickImage?.Dispose();
            }
        });
    }

    // Wait for all pages to complete
    await Task.WhenAll(renderTasks);
}

public async Task ConvertToJpegsAsync(string outputDirectory, string fileNamePrefix, int dpiWidth, int dpiHeight)
{
    if (PageCount == 0)
        throw new InvalidOperationException("Document has no pages");

    if (!Directory.Exists(outputDirectory))
        Directory.CreateDirectory(outputDirectory);

    // Create tasks for each page rendering and saving
    var renderTasks = new Task[PageCount];

    for (int i = 0; i < PageCount; i++)
    {
        int pageIndex = i; // Capture loop variable
        renderTasks[i] = Task.Run(() =>
        {
            MagickImage magickImage;
            
            // Synchronize PDFium operations
            lock (_pdfiumLock)
            {
                using var page = GetPage(pageIndex);
                magickImage = RenderPageToMagickImage(page, dpiWidth, dpiHeight);
            }
            
            // File I/O can happen outside the lock
            try
            {
                var fileName = $"{fileNamePrefix}_{pageIndex + 1:D3}.jpg";
                var filePath = Path.Combine(outputDirectory, fileName);

                magickImage.Format = MagickFormat.Jpg;
                magickImage.Write(filePath);
            }
            finally
            {
                magickImage?.Dispose();
            }
        });
    }

    // Wait for all pages to complete
    await Task.WhenAll(renderTasks);
}

    // Overload for uniform DPI (backwards compatibility)
    private MagickImage RenderPageToMagickImage(PdfPage page, int dpi, CompressionMethod? compression = null)
    {
        return RenderPageToMagickImage(page, dpi, dpi, compression);
    }

    // Main method with separate DPI for width and height
    private MagickImage RenderPageToMagickImage(PdfPage page, int dpiWidth, int dpiHeight,
        CompressionMethod? compression = null)
    {
        // Get original page dimensions in points
        var originalWidthPoints = page.Width;
        var originalHeightPoints = page.Height;

        // Convert points to inches (points / 72)
        var originalWidthInches = originalWidthPoints / 72.0;
        var originalHeightInches = originalHeightPoints / 72.0;

        // Calculate final dimensions in pixels based on DPI
        uint finalWidthPixels = (uint)Math.Round(originalWidthInches * dpiWidth);
        uint finalHeightPixels = (uint)Math.Round(originalHeightInches * dpiHeight);

        // Render PDF page to bytes using PDFium
        var pdfBytes = page.RenderToBytes((int)finalWidthPixels, (int)finalHeightPixels, PDFium.FPDF_ANNOT);

        // Create MagickReadSettings for BGRA format
        var settings = new MagickReadSettings()
        {
            Format = MagickFormat.Bgra,
            Width = finalWidthPixels,
            Height = finalHeightPixels,
            Depth = 8
        };

        // Create MagickImage from PDFium data
        var magickImage = new MagickImage(pdfBytes, settings);

        // Set density using the provided DPI values
        magickImage.Density = new Density(dpiWidth, dpiHeight, DensityUnit.PixelsPerInch);

        // Set a white background (in case of transparency)
        magickImage.BackgroundColor = MagickColors.White;
        magickImage.Alpha(AlphaOption.Remove);

        // Set EXIF metadata
        var exifProfile = magickImage.GetExifProfile();
        if (exifProfile == null)
        {
            exifProfile = new ExifProfile();
            magickImage.SetProfile(exifProfile);
        }

        exifProfile.SetValue(ExifTag.DateTime, DateTime.Now.ToString("yyyy:MM:dd HH:mm:ss"));
        exifProfile.SetValue(ExifTag.Software, "Malweka.Pdfium");

        // Determine orientation based on dimensions
        bool isLandscape = originalWidthInches > originalHeightInches;

        if (isLandscape)
        {
            // Landscape orientation
            exifProfile.SetValue(ExifTag.Orientation, (ushort)6);
        }
        else
        {
            // Portrait orientation
            exifProfile.SetValue(ExifTag.Orientation, (ushort)1);
        }

        // Apply compression if specified (for TIFF)
        if (compression.HasValue)
        {
            magickImage.Format = MagickFormat.Tiff;
            magickImage.Settings.Compression = compression.Value;
        }

        magickImage.SetProfile(exifProfile);
        return magickImage;
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