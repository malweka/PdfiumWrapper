using System.Runtime.InteropServices;

namespace PdfiumWrapper;

/// <summary>
/// PNG encoder using the native pdfium_png shim (libpng + zlib-ng underneath).
/// Stateless — all methods are static and thread-safe. Encodes to memory only; callers write
/// files in managed code, because the shim's fopen reads paths in the ANSI code page on Windows.
/// </summary>
internal static class PngEncoder
{
    /// <summary>
    /// Encode a managed byte array to PNG bytes.
    /// </summary>
    public static byte[] Encode(byte[] pixels, int width, int height, int stride,
        LibPdfiumPng.PngPixelFormat format = LibPdfiumPng.PngPixelFormat.BGRA,
        int compressionLevel = 6,
        int filterFlags = 0)
    {
        unsafe
        {
            fixed (byte* ptr = pixels)
            {
                return Encode((IntPtr)ptr, width, height, stride, format, compressionLevel, filterFlags);
            }
        }
    }

    /// <summary>
    /// Encode a raw pixel buffer to PNG bytes.
    /// </summary>
    public static byte[] Encode(IntPtr pixelBuffer, int width, int height, int stride,
        LibPdfiumPng.PngPixelFormat format = LibPdfiumPng.PngPixelFormat.BGRA,
        int compressionLevel = 6,
        int filterFlags = 0)
    {
        int rc = LibPdfiumPng.pdfium_png_encode_to_memory(
            pixelBuffer, width, height, stride,
            format, compressionLevel, filterFlags,
            out IntPtr outData, out nuint outSize);

        if (rc != 0)
            ThrowPngError("pdfium_png_encode_to_memory");

        try
        {
            var managed = new byte[(int)outSize];
            Marshal.Copy(outData, managed, 0, (int)outSize);
            return managed;
        }
        finally
        {
            LibPdfiumPng.pdfium_png_free(outData);
        }
    }

    private static void ThrowPngError(string function)
    {
        var msgPtr = LibPdfiumPng.pdfium_png_get_error();
        var msg = msgPtr != IntPtr.Zero ? Marshal.PtrToStringAnsi(msgPtr) : "unknown error";
        throw new InvalidOperationException($"libpng: {function} failed — {msg}");
    }
}
