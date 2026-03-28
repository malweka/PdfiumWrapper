using System.Runtime.InteropServices;

namespace PdfiumWrapper;

/// <summary>
/// JPEG encoder using native libjpeg-turbo via the TurboJPEG API.
/// Accepts raw pixel buffers (e.g. BGRA from PDFium) and compresses to JPEG.
/// Not thread-safe per instance — create one per thread or use pooling.
/// </summary>
internal sealed class JpegEncoder : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    public JpegEncoder()
    {
        _handle = LibTurboJpeg.tjInitCompress();
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("libjpeg-turbo: tjInitCompress() failed.");
    }

    /// <summary>
    /// Encode a raw pixel buffer to JPEG bytes.
    /// </summary>
    /// <param name="pixelBuffer">Pointer to the source pixel data (e.g. from FPDFBitmap_GetBuffer).</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="stride">Bytes per row. 0 = tightly packed.</param>
    /// <param name="pixelFormat">Pixel format of the source buffer. Default is BGRA (PDFium's native format).</param>
    /// <param name="quality">JPEG quality 1-100.</param>
    /// <param name="subsampling">Chroma subsampling. Default is 4:2:0.</param>
    /// <param name="flags">Encoding flags.</param>
    /// <returns>Compressed JPEG data.</returns>
    public byte[] Encode(IntPtr pixelBuffer, int width, int height, int stride,
        LibTurboJpeg.TJPixelFormat pixelFormat = LibTurboJpeg.TJPixelFormat.BGRA,
        int quality = 85,
        LibTurboJpeg.TJSubsampling subsampling = LibTurboJpeg.TJSubsampling.Samp420,
        LibTurboJpeg.TJFlag flags = LibTurboJpeg.TJFlag.None)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        IntPtr jpegBuf = IntPtr.Zero;
        nuint jpegSize = 0;

        try
        {
            int result = LibTurboJpeg.tjCompress2(
                _handle,
                pixelBuffer,
                width,
                stride,
                height,
                (int)pixelFormat,
                ref jpegBuf,
                ref jpegSize,
                (int)subsampling,
                quality,
                (int)flags);

            if (result != 0)
                ThrowTurboJpegError("tjCompress2");

            var managed = new byte[(int)jpegSize];
            Marshal.Copy(jpegBuf, managed, 0, (int)jpegSize);
            return managed;
        }
        finally
        {
            if (jpegBuf != IntPtr.Zero)
                LibTurboJpeg.tjFree(jpegBuf);
        }
    }

    /// <summary>
    /// Encode a raw pixel buffer and write directly to a file.
    /// </summary>
    public void EncodeToFile(IntPtr pixelBuffer, int width, int height, int stride,
        string outputPath,
        LibTurboJpeg.TJPixelFormat pixelFormat = LibTurboJpeg.TJPixelFormat.BGRA,
        int quality = 85,
        LibTurboJpeg.TJSubsampling subsampling = LibTurboJpeg.TJSubsampling.Samp420,
        LibTurboJpeg.TJFlag flags = LibTurboJpeg.TJFlag.None)
    {
        var data = Encode(pixelBuffer, width, height, stride, pixelFormat, quality, subsampling, flags);
        File.WriteAllBytes(outputPath, data);
    }

    /// <summary>
    /// Encode a raw pixel buffer and write to a stream.
    /// </summary>
    public void EncodeToStream(IntPtr pixelBuffer, int width, int height, int stride,
        Stream output,
        LibTurboJpeg.TJPixelFormat pixelFormat = LibTurboJpeg.TJPixelFormat.BGRA,
        int quality = 85,
        LibTurboJpeg.TJSubsampling subsampling = LibTurboJpeg.TJSubsampling.Samp420,
        LibTurboJpeg.TJFlag flags = LibTurboJpeg.TJFlag.None)
    {
        var data = Encode(pixelBuffer, width, height, stride, pixelFormat, quality, subsampling, flags);
        output.Write(data, 0, data.Length);
    }

    private void ThrowTurboJpegError(string function)
    {
        var msgPtr = LibTurboJpeg.tjGetErrorStr2(_handle);
        var msg = msgPtr != IntPtr.Zero ? Marshal.PtrToStringAnsi(msgPtr) : "unknown error";
        throw new InvalidOperationException($"libjpeg-turbo: {function} failed — {msg}");
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_handle != IntPtr.Zero)
            {
                LibTurboJpeg.tjDestroy(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }
}
