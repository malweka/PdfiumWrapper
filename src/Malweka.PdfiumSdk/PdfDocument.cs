using System.Runtime.InteropServices;
using ExifLibrary;
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
                throw new InvalidOperationException($"Failed to load PDF document from memory. Error: {PDFium.FPDF_GetLastError()}");
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

    public void ConvertToTiff(string outputPath, int dpi = 300, CompressionMethod compression = CompressionMethod.LZW)
    {
        if (PageCount == 0)
            throw new InvalidOperationException("Document has no pages");

        using var images = new MagickImageCollection();

        for (int i = 0; i < PageCount; i++)
        {
            using var page = GetPage(i);
            var magickImage = RenderPageToMagickImage(page, dpi, compression);
            images.Add(magickImage);
        }

        images.Write(outputPath);
    }

    public void ConvertToPngs(string outputDirectory, string fileNamePrefix = "page", int dpi = 300)
    {
        if (PageCount == 0)
            throw new InvalidOperationException("Document has no pages");

        if (!Directory.Exists(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        for (int i = 0; i < PageCount; i++)
        {
            using var page = GetPage(i);
            using var magickImage = RenderPageToMagickImage(page, dpi);

            var fileName = $"{fileNamePrefix}_{i + 1:D3}.png";
            var filePath = Path.Combine(outputDirectory, fileName);

            magickImage.Format = MagickFormat.Png;
            magickImage.Write(filePath);
            //AddExifToFile(filePath, magickImage.Width > magickImage.Height); // Add EXIF based on orientation
        }
        
    }

    public void ConvertToJpeg(string outputDirectory, string fileNamePrefix = "page", int dpi = 300)
    {
        if (PageCount == 0)
            throw new InvalidOperationException("Document has no pages");

        if (!Directory.Exists(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        for (int i = 0; i < PageCount; i++)
        {
            using var page = GetPage(i);
            using var magickImage = RenderPageToMagickImage(page, dpi);

            var fileName = $"{fileNamePrefix}_{i + 1:D3}.jpg";
            var filePath = Path.Combine(outputDirectory, fileName);

            magickImage.Format = MagickFormat.Jpg;
            magickImage.Write(filePath);
            AddExifToFile(filePath, magickImage.Width > magickImage.Height); // Add EXIF based on orientation
        }

    }

    private void AddExifToFile(string filePath, bool isLandscape)
    {
        try
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            // ExifLib primarily works with JPEG
            var image = ImageFile.FromFile(filePath);

            // Set orientation
            ushort orientationValue = (ushort)1; // Normal orientation
            image.Properties.Set(ExifLibrary.ExifTag.Orientation, orientationValue);

            // Add software tag
            image.Properties.Set(ExifLibrary.ExifTag.Software, "PDFium Wrapper");

            // Add timestamp
            image.Properties.Set(ExifLibrary.ExifTag.DateTime, DateTime.Now);

            // Save the changes
            image.Save(filePath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to add EXIF to {filePath}: {ex.Message}");
        }
    }

    private MagickImage RenderPageToMagickImage(PdfPage page, int dpi, CompressionMethod? compression = null)
    {
        // Get original page dimensions in points
        var originalWidthPoints = page.Width;
        var originalHeightPoints = page.Height;

        // Convert to inches (points / 72)
        var originalWidthInches = originalWidthPoints / 72.0;
        var originalHeightInches = originalHeightPoints / 72.0;

        // US Letter dimensions in inches
        const double US_LETTER_WIDTH_INCHES = 8.5;
        const double US_LETTER_HEIGHT_INCHES = 11.0;

        // Determine if page is landscape
        bool isLandscape = originalWidthInches > originalHeightInches;

        // Calculate target dimensions for US Letter size in inches
        double targetWidthInches, targetHeightInches;
        if (isLandscape)
        {
            // Landscape: swap US Letter dimensions
            targetWidthInches = US_LETTER_HEIGHT_INCHES;  // 11 inches
            targetHeightInches = US_LETTER_WIDTH_INCHES;  // 8.5 inches
        }
        else
        {
            // Portrait: use normal US Letter dimensions
            targetWidthInches = US_LETTER_WIDTH_INCHES;   // 8.5 inches
            targetHeightInches = US_LETTER_HEIGHT_INCHES; // 11 inches
        }

        // Calculate scale factors to fit within US Letter while maintaining aspect ratio
        double scaleX = targetWidthInches / originalWidthInches;
        double scaleY = targetHeightInches / originalHeightInches;
        double scale = Math.Min(scaleX, scaleY); // Use the smaller scale to ensure it fits

        // Don't upscale - if the document already fits, keep original size
        scale = Math.Min(scale, 1.0);

        // Calculate final dimensions in inches
        double finalWidthInches = originalWidthInches * scale;
        double finalHeightInches = originalHeightInches * scale;

        // Convert to pixels based on DPI
        uint finalWidthPixels = (uint)Math.Round(finalWidthInches * dpi);
        uint finalHeightPixels = (uint)Math.Round(finalHeightInches * dpi);

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

        // Set density and format
        magickImage.Density = new Density(dpi, dpi, DensityUnit.PixelsPerInch);

        // Set a white background (in case of transparency)
        magickImage.BackgroundColor = MagickColors.White;
        magickImage.Alpha(AlphaOption.Remove);

        // Set EXIF orientation metadata
        var exifProfile = magickImage.GetExifProfile();
        if (exifProfile == null)
        {
            exifProfile = new ExifProfile();
            magickImage.SetProfile(exifProfile);
        }

        exifProfile.SetValue(ExifTag.DateTime, DateTime.Now.ToString("yyyy:MM:dd HH:mm:ss"));
        exifProfile.SetValue(ExifTag.Copyright, "Malweka.Pdfium");

        // Set orientation based on landscape/portrait
        if (isLandscape)
        {
            // Landscape orientation - EXIF orientation value 1 (normal, since we render correctly)
            exifProfile.SetValue(ExifTag.Orientation, (ushort)6);
        }
        else
        {
            // Portrait orientation - EXIF orientation value 1 (normal/upright)
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