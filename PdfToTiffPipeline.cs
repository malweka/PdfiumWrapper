using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Malweka.PdfiumSdk.Tiff;

#region LibTiff P/Invoke

/// <summary>
/// Minimal P/Invoke surface for libtiff — covers multi-page TIFF writing with CCITT G4.
/// Link against tiff.dll (Windows) or libtiff.so (Linux).
/// </summary>
internal static class LibTiffNative
{
    private const string LibTiff = "tiff";

    // --- Lifecycle ---
    [DllImport(LibTiff, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr TIFFOpen(string filename, string mode);

    [DllImport(LibTiff, CallingConvention = CallingConvention.Cdecl)]
    public static extern void TIFFClose(IntPtr tiff);

    // --- Field setters (overloads for different value types) ---
    [DllImport(LibTiff, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TIFFSetField(IntPtr tiff, TiffTag tag, int value);

    [DllImport(LibTiff, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TIFFSetField(IntPtr tiff, TiffTag tag, uint value);

    [DllImport(LibTiff, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TIFFSetField(IntPtr tiff, TiffTag tag, float value);

    [DllImport(LibTiff, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TIFFSetField(IntPtr tiff, TiffTag tag, double value);

    [DllImport(LibTiff, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TIFFSetField(IntPtr tiff, TiffTag tag, short value);

    [DllImport(LibTiff, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TIFFSetField(IntPtr tiff, TiffTag tag, ushort value);

    // --- Writing ---
    [DllImport(LibTiff, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TIFFWriteScanline(IntPtr tiff, byte[] buf, int row, ushort sample);

    [DllImport(LibTiff, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TIFFWriteScanline(IntPtr tiff, IntPtr buf, int row, ushort sample);

    [DllImport(LibTiff, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TIFFWriteEncodedStrip(IntPtr tiff, int strip, byte[] data, int cc);

    // --- Directory (multi-page) ---
    [DllImport(LibTiff, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TIFFWriteDirectory(IntPtr tiff);

    [DllImport(LibTiff, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TIFFFlush(IntPtr tiff);

    // --- Error handling (optional but recommended) ---
    public delegate void TIFFErrorHandler(string module, string fmt, IntPtr args);

    [DllImport(LibTiff, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr TIFFSetErrorHandler(TIFFErrorHandler handler);

    [DllImport(LibTiff, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr TIFFSetWarningHandler(TIFFErrorHandler handler);
}

internal enum TiffTag : uint
{
    IMAGEWIDTH = 256,
    IMAGELENGTH = 257,
    BITSPERSAMPLE = 258,
    COMPRESSION = 259,
    PHOTOMETRIC = 262,
    FILLORDER = 266,
    SAMPLESPERPIXEL = 277,
    ROWSPERSTRIP = 278,
    XRESOLUTION = 282,
    YRESOLUTION = 283,
    PLANARCONFIG = 284,
    RESOLUTIONUNIT = 296,
    PAGENUMBER = 297,
    SUBFILETYPE = 254,
}

internal static class TiffConstants
{
    // Compression
    public const int COMPRESSION_CCITT_T6 = 4;  // CCITT Group 4 fax
    public const int COMPRESSION_LZW = 5;
    public const int COMPRESSION_DEFLATE = 32946;

    // Photometric
    public const int PHOTOMETRIC_MINISWHITE = 0;
    public const int PHOTOMETRIC_MINISBLACK = 1;
    public const int PHOTOMETRIC_RGB = 2;

    // FillOrder
    public const int FILLORDER_MSB2LSB = 1;
    public const int FILLORDER_LSB2MSB = 2;

    // PlanarConfig
    public const int PLANARCONFIG_CONTIG = 1;

    // ResolutionUnit
    public const int RESUNIT_INCH = 2;

    // SubFileType
    public const int FILETYPE_PAGE = 2;
}

#endregion

#region LibTiff Wrapper

/// <summary>
/// Lightweight wrapper for writing multi-page TIFF files via native libtiff.
/// Each instance wraps a single TIFF file handle — not thread-safe per instance,
/// but multiple instances can be used concurrently (one per document).
/// </summary>
internal sealed class TiffWriter : IDisposable
{
    private IntPtr _tiff;
    private int _pageIndex;
    private bool _disposed;

    // Pin the delegates so they don't get GC'd while libtiff holds them
    private static readonly LibTiffNative.TIFFErrorHandler s_errorHandler = OnTiffError;
    private static readonly LibTiffNative.TIFFErrorHandler s_warningHandler = OnTiffWarning;
    private static int s_handlersInstalled;

    public TiffWriter(string outputPath)
    {
        InstallHandlers();

        _tiff = LibTiffNative.TIFFOpen(outputPath, "w");
        if (_tiff == IntPtr.Zero)
            throw new IOException($"libtiff: failed to open '{outputPath}' for writing.");
    }

    /// <summary>
    /// Writes a 1-bit (bilevel) page from packed scanline data.
    /// </summary>
    /// <param name="data">Packed 1-bit pixel data, MSB first, one row = ceil(width/8) bytes.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="dpiX">Horizontal resolution.</param>
    /// <param name="dpiY">Vertical resolution.</param>
    /// <param name="totalPages">Total number of pages (for PAGENUMBER tag). Pass 0 if unknown.</param>
    public void WriteBilevelPage(byte[] data, int width, int height,
        float dpiX = 200f, float dpiY = 200f, int totalPages = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int bytesPerRow = (width + 7) / 8;

        SetPageFields(width, height, dpiX, dpiY, totalPages,
            bitsPerSample: 1,
            samplesPerPixel: 1,
            compression: TiffConstants.COMPRESSION_CCITT_T6,
            photometric: TiffConstants.PHOTOMETRIC_MINISWHITE);

        // Write scanline by scanline
        byte[] rowBuf = new byte[bytesPerRow];
        for (int row = 0; row < height; row++)
        {
            Buffer.BlockCopy(data, row * bytesPerRow, rowBuf, 0, bytesPerRow);
            int result = LibTiffNative.TIFFWriteScanline(_tiff, rowBuf, row, 0);
            if (result < 0)
                throw new IOException($"libtiff: TIFFWriteScanline failed at row {row}.");
        }

        FinalizePage();
    }

    /// <summary>
    /// Writes a grayscale (8-bit) page with LZW compression.
    /// Useful if you want to keep grayscale fidelity instead of thresholding.
    /// </summary>
    public void WriteGrayscalePage(byte[] data, int width, int height,
        float dpiX = 200f, float dpiY = 200f, int totalPages = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        SetPageFields(width, height, dpiX, dpiY, totalPages,
            bitsPerSample: 8,
            samplesPerPixel: 1,
            compression: TiffConstants.COMPRESSION_LZW,
            photometric: TiffConstants.PHOTOMETRIC_MINISBLACK);

        byte[] rowBuf = new byte[width];
        for (int row = 0; row < height; row++)
        {
            Buffer.BlockCopy(data, row * width, rowBuf, 0, width);
            int result = LibTiffNative.TIFFWriteScanline(_tiff, rowBuf, row, 0);
            if (result < 0)
                throw new IOException($"libtiff: TIFFWriteScanline failed at row {row}.");
        }

        FinalizePage();
    }

    private void SetPageFields(int width, int height, float dpiX, float dpiY,
        int totalPages, int bitsPerSample, int samplesPerPixel,
        int compression, int photometric)
    {
        var t = _tiff;

        LibTiffNative.TIFFSetField(t, TiffTag.SUBFILETYPE, TiffConstants.FILETYPE_PAGE);
        LibTiffNative.TIFFSetField(t, TiffTag.IMAGEWIDTH, width);
        LibTiffNative.TIFFSetField(t, TiffTag.IMAGELENGTH, height);
        LibTiffNative.TIFFSetField(t, TiffTag.BITSPERSAMPLE, (short)bitsPerSample);
        LibTiffNative.TIFFSetField(t, TiffTag.SAMPLESPERPIXEL, (short)samplesPerPixel);
        LibTiffNative.TIFFSetField(t, TiffTag.COMPRESSION, compression);
        LibTiffNative.TIFFSetField(t, TiffTag.PHOTOMETRIC, photometric);
        LibTiffNative.TIFFSetField(t, TiffTag.FILLORDER, TiffConstants.FILLORDER_MSB2LSB);
        LibTiffNative.TIFFSetField(t, TiffTag.PLANARCONFIG, TiffConstants.PLANARCONFIG_CONTIG);
        LibTiffNative.TIFFSetField(t, TiffTag.XRESOLUTION, dpiX);
        LibTiffNative.TIFFSetField(t, TiffTag.YRESOLUTION, dpiY);
        LibTiffNative.TIFFSetField(t, TiffTag.RESOLUTIONUNIT, TiffConstants.RESUNIT_INCH);
        LibTiffNative.TIFFSetField(t, TiffTag.ROWSPERSTRIP, height); // one strip per page

        if (totalPages > 0)
        {
            // PAGENUMBER takes two shorts: current page (0-based) and total pages
            // libtiff expects these as two separate ushort args via varargs
            // For P/Invoke, we need a specific overload or pack them
            // Simplest: skip PAGENUMBER if it causes issues, it's optional
        }
    }

    private void FinalizePage()
    {
        int result = LibTiffNative.TIFFWriteDirectory(_tiff);
        if (result == 0)
            throw new IOException("libtiff: TIFFWriteDirectory failed.");
        _pageIndex++;
    }

    private static void InstallHandlers()
    {
        if (Interlocked.CompareExchange(ref s_handlersInstalled, 1, 0) == 0)
        {
            LibTiffNative.TIFFSetErrorHandler(s_errorHandler);
            LibTiffNative.TIFFSetWarningHandler(s_warningHandler);
        }
    }

    private static void OnTiffError(string module, string fmt, IntPtr args)
    {
        // In production, log this via your logging framework
        System.Diagnostics.Debug.WriteLine($"[libtiff ERROR] {module}: {fmt}");
    }

    private static void OnTiffWarning(string module, string fmt, IntPtr args)
    {
        System.Diagnostics.Debug.WriteLine($"[libtiff WARN] {module}: {fmt}");
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_tiff != IntPtr.Zero)
            {
                LibTiffNative.TIFFClose(_tiff);
                _tiff = IntPtr.Zero;
            }
        }
    }
}

#endregion

#region Pixel Conversion Utilities

internal static class PixelConverter
{
    /// <summary>
    /// Converts BGRA (PDFium's native format) to 1-bit packed bilevel using a simple threshold.
    /// Output is MSB-first packed bytes, one row = ceil(width/8) bytes.
    /// </summary>
    /// <param name="bgra">Source BGRA pixel buffer (4 bytes per pixel).</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="stride">Source stride in bytes (may include padding).</param>
    /// <param name="threshold">Luminance threshold 0-255. Pixels below = black (1), above = white (0).
    /// Note: PHOTOMETRIC_MINISWHITE means 0=white, 1=black.</param>
    /// <returns>Packed 1-bit data suitable for TiffWriter.WriteBilevelPage.</returns>
    public static byte[] BgraToPackedBilevel(IntPtr bgra, int width, int height,
        int stride, byte threshold = 128)
    {
        int packedStride = (width + 7) / 8;
        byte[] output = new byte[packedStride * height];

        unsafe
        {
            byte* src = (byte*)bgra;

            for (int y = 0; y < height; y++)
            {
                byte* row = src + (y * stride);
                int outOffset = y * packedStride;

                for (int x = 0; x < width; x++)
                {
                    int px = x * 4;
                    byte b = row[px];
                    byte g = row[px + 1];
                    byte r = row[px + 2];

                    // ITU-R BT.601 luminance
                    int luminance = (r * 299 + g * 587 + b * 114) / 1000;

                    // MINISWHITE: 0 = white, 1 = black
                    if (luminance < threshold)
                    {
                        int byteIndex = outOffset + (x >> 3);
                        int bitIndex = 7 - (x & 7); // MSB first
                        output[byteIndex] |= (byte)(1 << bitIndex);
                    }
                }
            }
        }

        return output;
    }

    /// <summary>
    /// Converts BGRA to grayscale (1 byte per pixel).
    /// </summary>
    public static byte[] BgraToGrayscale(IntPtr bgra, int width, int height, int stride)
    {
        byte[] output = new byte[width * height];

        unsafe
        {
            byte* src = (byte*)bgra;

            for (int y = 0; y < height; y++)
            {
                byte* row = src + (y * stride);
                int outOffset = y * width;

                for (int x = 0; x < width; x++)
                {
                    int px = x * 4;
                    byte b = row[px];
                    byte g = row[px + 1];
                    byte r = row[px + 2];

                    output[outOffset + x] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
                }
            }
        }

        return output;
    }
}

#endregion

#region Full Pipeline: PDF → Multi-page TIFF

/// <summary>
/// Converts a PDF to a multi-page TIFF using PDFium for rendering and libtiff for encoding.
/// 
/// Usage:
///   var converter = new PdfToTiffConverter(dpi: 200);
///   converter.Convert("input.pdf", "output.tif");
/// </summary>
public sealed class PdfToTiffConverter
{
    private readonly int _dpi;
    private readonly byte _threshold;

    public PdfToTiffConverter(int dpi = 200, byte threshold = 128)
    {
        _dpi = dpi;
        _threshold = threshold;
    }

    /// <summary>
    /// Converts all pages of a PDF to a single multi-page TIFF with CCITT G4 compression.
    /// </summary>
    public void Convert(string pdfPath, string tiffPath)
    {
        // ---------------------------------------------------------------
        // Replace these calls with your actual Malweka.PdfiumSdk API.
        // The key contract is:
        //   1. Load document → get page count
        //   2. For each page → get dimensions, render to BGRA bitmap
        //   3. You receive an IntPtr to the pixel buffer + stride
        // ---------------------------------------------------------------

        IntPtr document = PdfiumNative.FPDF_LoadDocument(pdfPath, null);
        if (document == IntPtr.Zero)
            throw new FileNotFoundException($"PDFium failed to open: {pdfPath}");

        try
        {
            int pageCount = PdfiumNative.FPDF_GetPageCount(document);

            using var writer = new TiffWriter(tiffPath);

            for (int i = 0; i < pageCount; i++)
            {
                IntPtr page = PdfiumNative.FPDF_LoadPage(document, i);
                if (page == IntPtr.Zero)
                    throw new InvalidOperationException($"Failed to load page {i}.");

                try
                {
                    // Get page size in points (1 point = 1/72 inch)
                    double widthPts = PdfiumNative.FPDF_GetPageWidth(page);
                    double heightPts = PdfiumNative.FPDF_GetPageHeight(page);

                    // Convert to pixels at target DPI
                    int widthPx = (int)(widthPts * _dpi / 72.0);
                    int heightPx = (int)(heightPts * _dpi / 72.0);
                    int stride = widthPx * 4; // BGRA = 4 bytes per pixel

                    // Create bitmap and render
                    IntPtr bitmap = PdfiumNative.FPDFBitmap_Create(widthPx, heightPx, 0);
                    if (bitmap == IntPtr.Zero)
                        throw new OutOfMemoryException($"FPDFBitmap_Create failed for page {i}.");

                    try
                    {
                        // Fill with white background
                        PdfiumNative.FPDFBitmap_FillRect(bitmap, 0, 0, widthPx, heightPx,
                            0xFFFFFFFF); // white in ARGB

                        // Render page to bitmap
                        PdfiumNative.FPDF_RenderPageBitmap(
                            bitmap, page,
                            0, 0, widthPx, heightPx,
                            0,     // rotation: 0 = normal
                            0x10   // FPDF_PRINTING flag for print-quality rendering
                        );

                        IntPtr buffer = PdfiumNative.FPDFBitmap_GetBuffer(bitmap);
                        int bitmapStride = PdfiumNative.FPDFBitmap_GetStride(bitmap);

                        // Convert BGRA → packed 1-bit bilevel
                        byte[] bilevelData = PixelConverter.BgraToPackedBilevel(
                            buffer, widthPx, heightPx, bitmapStride, _threshold);

                        // Write to TIFF
                        writer.WriteBilevelPage(bilevelData, widthPx, heightPx,
                            _dpi, _dpi, pageCount);
                    }
                    finally
                    {
                        PdfiumNative.FPDFBitmap_Destroy(bitmap);
                    }
                }
                finally
                {
                    PdfiumNative.FPDF_ClosePage(page);
                }
            }
        }
        finally
        {
            PdfiumNative.FPDF_CloseDocument(document);
        }
    }
}

#endregion

#region PDFium P/Invoke (stub — replace with your Malweka.PdfiumSdk)

/// <summary>
/// Minimal PDFium P/Invoke declarations. Replace with your actual wrapper.
/// </summary>
internal static class PdfiumNative
{
    private const string Pdfium = "pdfium";

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_InitLibrary();

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_DestroyLibrary();

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr FPDF_LoadDocument(string filePath, string? password);

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_CloseDocument(IntPtr document);

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_GetPageCount(IntPtr document);

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_ClosePage(IntPtr page);

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    public static extern double FPDF_GetPageWidth(IntPtr page);

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    public static extern double FPDF_GetPageHeight(IntPtr page);

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFBitmap_FillRect(IntPtr bitmap,
        int left, int top, int width, int height, uint color);

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page,
        int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFBitmap_GetStride(IntPtr bitmap);

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFBitmap_Destroy(IntPtr bitmap);
}

#endregion
