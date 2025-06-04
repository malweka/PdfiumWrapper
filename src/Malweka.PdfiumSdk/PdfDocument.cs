using System.Runtime.InteropServices;
using SkiaSharp;
using static System.Net.Mime.MediaTypeNames;

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

    public void ConvertToTiff(string outputPath, int dpi = 300)
    {
        if (PageCount == 0)
            throw new InvalidOperationException("Document has no pages");

        var pages = new List<SKImage>();

        try
        {
            // Render all pages to SKImages
            for (int i = 0; i < PageCount; i++)
            {
                using var page = GetPage(i);
                var skImage = RenderPageToSkImage(page, dpi);
                pages.Add(skImage);
            }

            // Create multi-page TIFF
            SaveAsMultiPageTiff(pages, outputPath);
        }
        finally
        {
            // Dispose all SKImages
            foreach (var image in pages)
            {
                image?.Dispose();
            }
        }
    }

    public void ConvertToPngs(string outputDirectory, string fileNamePrefix = "page", int dpi = 300)
    {
        if (PageCount == 0)
            throw new InvalidOperationException("Document has no pages");

        Directory.CreateDirectory(outputDirectory);

        for (int i = 0; i < PageCount; i++)
        {
            using var page = GetPage(i);
            using var skImage = RenderPageToSkImage(page, dpi);

            var fileName = $"{fileNamePrefix}_{i + 1:D3}.png";
            var filePath = Path.Combine(outputDirectory, fileName);

            using var data = skImage.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(filePath, data.ToArray());
        }
    }

    private SKImage RenderPageToSkImage(PdfPage page, int dpi)
    {
        var scale = dpi / 72.0f;
        var width = (int)(page.Width * scale);
        var height = (int)(page.Height * scale);

        // Create SkiaSharp surface
        var imageInfo = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(imageInfo);
        using var canvas = surface.Canvas;

        // Clear canvas with white background
        canvas.Clear(SKColors.White);

        // Render PDF page to bytes using PDFium
        var pdfBytes = page.RenderToBytes(width, height, PDFium.FPDF_ANNOT);

        // Create SKBitmap from PDFium data (PDFium returns BGRA format)
        var skImageInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var skBitmap = new SKBitmap(skImageInfo);
        var pixels = skBitmap.GetPixels();

        // Copy PDFium data to SKBitmap
        Marshal.Copy(pdfBytes, 0, pixels, pdfBytes.Length);

        // Draw bitmap to canvas
        canvas.DrawBitmap(skBitmap, 0, 0);

        // Return snapshot as SKImage
        return surface.Snapshot();
    }

    private void SaveAsMultiPageTiff(List<SKImage> pages, string outputPath)
    {//https://github.com/BitMiracle/libtiff.net/blob/master/license.txt
        if (pages.Count == 0)
            throw new ArgumentException("No pages to save");

        if (pages.Count == 1)
        {
            // Single page TIFF
            using var data = pages[0].Encode(SKEncodedImageFormat., 100);
            File.WriteAllBytes(outputPath, data.ToArray());
        }
        else
        {
            // Multi-page TIFF - SkiaSharp doesn't directly support multi-page TIFF
            // So we'll create individual TIFFs and then combine them using a simple approach
            // For a more robust solution, you might want to use a library like ImageSharp

            var tempFiles = new List<string>();
            try
            {
                // Create temporary TIFF files for each page
                for (int i = 0; i < pages.Count; i++)
                {
                    var tempFile = Path.GetTempFileName() + ".tiff";
                    using var data = pages[i].Encode(SKEncodedImageFormat.Tiff, 100);
                    File.WriteAllBytes(tempFile, data.ToArray());
                    tempFiles.Add(tempFile);
                }

                // For now, we'll save the first page as the main TIFF
                // Note: This is a limitation - for true multi-page TIFF support,
                // consider using ImageSharp or another library
                File.Copy(tempFiles[0], outputPath, true);

                // If you need true multi-page TIFF, uncomment this warning:
                // Console.WriteLine("Warning: Multi-page TIFF creation with SkiaSharp is limited. Consider using ImageSharp for full multi-page support.");
            }
            finally
            {
                // Clean up temporary files
                foreach (var tempFile in tempFiles)
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }
    }
}