using System.Runtime.InteropServices;

namespace PdfiumWrapper;

/// <summary>
/// PNG encoder using the native pdfium_png shim (libpng + zlib-ng underneath).
/// Stateless — all methods are static and thread-safe.
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

    /// <summary>
    /// Encode a raw pixel buffer to a PNG file.
    /// </summary>
    public static void EncodeToFile(IntPtr pixelBuffer, int width, int height, int stride,
        string outputPath,
        LibPdfiumPng.PngPixelFormat format = LibPdfiumPng.PngPixelFormat.BGRA,
        int compressionLevel = 6,
        int filterFlags = 0)
    {
        int rc = LibPdfiumPng.pdfium_png_encode_to_file(
            outputPath, pixelBuffer, width, height, stride,
            format, compressionLevel, filterFlags);

        if (rc != 0)
            ThrowPngError("pdfium_png_encode_to_file");
    }

    /// <summary>
    /// Encode a raw pixel buffer and write to a stream.
    /// </summary>
    public static void EncodeToStream(IntPtr pixelBuffer, int width, int height, int stride,
        Stream output,
        LibPdfiumPng.PngPixelFormat format = LibPdfiumPng.PngPixelFormat.BGRA,
        int compressionLevel = 6,
        int filterFlags = 0)
    {
        var data = Encode(pixelBuffer, width, height, stride, format, compressionLevel, filterFlags);
        output.Write(data, 0, data.Length);
    }

    private static void ThrowPngError(string function)
    {
        var msgPtr = LibPdfiumPng.pdfium_png_get_error();
        var msg = msgPtr != IntPtr.Zero ? Marshal.PtrToStringAnsi(msgPtr) : "unknown error";
        throw new InvalidOperationException($"libpng: {function} failed — {msg}");
    }
}
