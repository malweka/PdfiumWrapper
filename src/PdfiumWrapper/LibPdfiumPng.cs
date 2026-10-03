using System.Runtime.InteropServices;

namespace PdfiumWrapper;

/// <summary>
/// P/Invoke bindings for the pdfium_png C shim.
/// The shim wraps libpng and handles setjmp/longjmp internally,
/// exposing a simple error-code API safe for .NET P/Invoke.
/// </summary>
internal static partial class LibPdfiumPng
{
    private const string LibraryName = "pdfium_png";

    static LibPdfiumPng()
    {
        NativeLibraryResolver.EnsureRegistered();
    }

    public enum PngPixelFormat : int
    {
        RGB = 0,
        RGBA = 1,
        BGRA = 2,
        Gray = 3,
        GrayAlpha = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PngInfo
    {
        public int Width;
        public int Height;
        public int BitDepth;
        public int ColorType;
        public int Channels;
    }

    /// <summary>
    /// PNG filter flags — pass to encode functions to control filter strategy.
    /// Use <see cref="PngFilterSub"/> for fast encoding or <see cref="PngAllFilters"/> for best compression.
    /// Pass 0 to use the default (SUB).
    /// </summary>
    public const int PngFilterNone  = 0x08;
    public const int PngFilterSub   = 0x10;
    public const int PngFilterUp    = 0x20;
    public const int PngFilterAvg   = 0x40;
    public const int PngFilterPaeth = 0x80;
    public const int PngAllFilters  = 0xF8;

    // The shim's file-path functions (pdfium_png_encode_to_file, _decode_from_file, _read_header)
    // are not imported: on Windows their fopen reads the char* path in the ANSI code page, so
    // non-ASCII paths break. Files are opened in managed code and the in-memory functions used.

    [LibraryImport(LibraryName)]
    public static partial int pdfium_png_encode_to_memory(
        IntPtr pixelData,
        int width,
        int height,
        int stride,
        PngPixelFormat format,
        int compressionLevel,
        int filterFlags,
        out IntPtr outData,
        out nuint outSize);

    [LibraryImport(LibraryName)]
    public static partial int pdfium_png_read_header_from_memory(
        IntPtr pngData,
        nuint pngSize,
        out PngInfo info);

    [LibraryImport(LibraryName)]
    public static partial int pdfium_png_decode_from_memory(
        IntPtr pngData,
        nuint pngSize,
        PngPixelFormat outputFormat,
        out IntPtr outData,
        out int outWidth,
        out int outHeight,
        out int outStride);

    [LibraryImport(LibraryName)]
    public static partial void pdfium_png_free(IntPtr data);

    [LibraryImport(LibraryName)]
    public static partial IntPtr pdfium_png_get_error();
}
