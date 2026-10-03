using System.Runtime.InteropServices;

namespace PdfiumWrapper;

/// <summary>
/// JPEG decoder using native libjpeg-turbo via the TurboJPEG API.
/// Decodes JPEG data to raw pixel buffers.
/// Not thread-safe per instance — create one per thread or use pooling.
/// </summary>
internal sealed class JpegDecoder : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    public JpegDecoder()
    {
        _handle = LibTurboJpeg.tjInitDecompress();
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("libjpeg-turbo: tjInitDecompress() failed.");
    }

    /// <summary>
    /// Read JPEG header to get dimensions and subsampling without decompressing.
    /// </summary>
    public JpegInfo ReadHeader(byte[] jpegData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        unsafe
        {
            fixed (byte* ptr = jpegData)
            {
                int result = LibTurboJpeg.tjDecompressHeader3(
                    _handle,
                    (IntPtr)ptr,
                    (nuint)jpegData.Length,
                    out int width,
                    out int height,
                    out int subsampling,
                    out int colorspace);

                if (result != 0)
                    ThrowTurboJpegError("tjDecompressHeader3");

                return new JpegInfo(width, height, (LibTurboJpeg.TJSubsampling)subsampling, colorspace);
            }
        }
    }

    /// <summary>
    /// Read JPEG header from a stream.
    /// </summary>
    public JpegInfo ReadHeader(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ReadHeader(ms.ToArray());
    }

    /// <summary>
    /// Decode JPEG data to a raw pixel buffer.
    /// </summary>
    /// <param name="jpegData">JPEG file bytes.</param>
    /// <param name="outputFormat">Desired output pixel format. Default is BGRA.</param>
    /// <returns>Raw pixel data and image info.</returns>
    public (byte[] pixels, JpegInfo info) Decode(byte[] jpegData,
        LibTurboJpeg.TJPixelFormat outputFormat = LibTurboJpeg.TJPixelFormat.BGRA)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var info = ReadHeader(jpegData);
        int pixelSize = GetPixelSize(outputFormat);
        // The dimensions come from the file. Size the buffer in 64-bit arithmetic: a header claiming
        // 32768 x 32769 wraps to 128 KB in int, and the decoder would write 4 GB into it.
        long pitch = (long)info.Width * pixelSize;
        long size = pitch * info.Height;
        if (info.Width <= 0 || info.Height <= 0 || size > Array.MaxLength)
            throw new InvalidDataException(
                $"JPEG dimensions {info.Width} x {info.Height} are not supported: the decoded image would need {size:N0} bytes.");
        // Below that, the bitmap PDFium gets is held to the render limit, so check it before allocating
        // as well: otherwise a few hundred bytes of JPEG could still allocate up to 2 GB here first.
        RenderLimits.Check(info.Width, info.Height, -1);
        var destBuffer = new byte[size];

        unsafe
        {
            fixed (byte* srcPtr = jpegData)
            fixed (byte* dstPtr = destBuffer)
            {
                int result = LibTurboJpeg.tjDecompress2(
                    _handle,
                    (IntPtr)srcPtr,
                    (nuint)jpegData.Length,
                    (IntPtr)dstPtr,
                    info.Width,
                    (int)pitch,
                    info.Height,
                    (int)outputFormat,
                    0);

                if (result != 0)
                    ThrowTurboJpegError("tjDecompress2");
            }
        }

        return (destBuffer, info);
    }

    private static int GetPixelSize(LibTurboJpeg.TJPixelFormat format) => format switch
    {
        LibTurboJpeg.TJPixelFormat.RGB => 3,
        LibTurboJpeg.TJPixelFormat.BGR => 3,
        LibTurboJpeg.TJPixelFormat.RGBX => 4,
        LibTurboJpeg.TJPixelFormat.BGRX => 4,
        LibTurboJpeg.TJPixelFormat.XBGR => 4,
        LibTurboJpeg.TJPixelFormat.XRGB => 4,
        LibTurboJpeg.TJPixelFormat.Gray => 1,
        LibTurboJpeg.TJPixelFormat.RGBA => 4,
        LibTurboJpeg.TJPixelFormat.BGRA => 4,
        LibTurboJpeg.TJPixelFormat.ABGR => 4,
        LibTurboJpeg.TJPixelFormat.ARGB => 4,
        LibTurboJpeg.TJPixelFormat.CMYK => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

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

/// <summary>
/// JPEG image metadata returned by header parsing.
/// </summary>
public record JpegInfo(int Width, int Height, LibTurboJpeg.TJSubsampling Subsampling, int Colorspace);
