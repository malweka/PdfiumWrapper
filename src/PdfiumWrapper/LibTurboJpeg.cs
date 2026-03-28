using System.Runtime.InteropServices;

namespace PdfiumWrapper;

/// <summary>
/// P/Invoke bindings for libjpeg-turbo's TurboJPEG API using modern LibraryImport.
/// Provides SIMD-accelerated JPEG encoding and decoding of raw pixel buffers.
/// </summary>
public static partial class LibTurboJpeg
{
    private const string LibraryName = "turbojpeg";

    static LibTurboJpeg()
    {
        NativeLibraryResolver.EnsureRegistered();
    }

    #region Lifecycle

    /// <summary>
    /// Create a compressor instance. Returns an opaque handle.
    /// Must be destroyed with <see cref="tjDestroy"/> when done.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial IntPtr tjInitCompress();

    /// <summary>
    /// Create a decompressor instance. Returns an opaque handle.
    /// Must be destroyed with <see cref="tjDestroy"/> when done.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial IntPtr tjInitDecompress();

    /// <summary>
    /// Destroy a compressor or decompressor instance.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int tjDestroy(IntPtr handle);

    #endregion

    #region Compression

    /// <summary>
    /// Compress a raw pixel buffer to JPEG.
    /// TurboJPEG allocates the output buffer; caller must free with <see cref="tjFree"/>.
    /// </summary>
    /// <param name="handle">Compressor handle from <see cref="tjInitCompress"/>.</param>
    /// <param name="srcBuf">Source pixel buffer.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="pitch">Bytes per row (stride). 0 = tightly packed.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="pixelFormat">Pixel format (see <see cref="TJPixelFormat"/>).</param>
    /// <param name="jpegBuf">OUTPUT — pointer to JPEG buffer allocated by TurboJPEG.</param>
    /// <param name="jpegSize">OUTPUT — size of the compressed JPEG data.</param>
    /// <param name="jpegSubsamp">Chroma subsampling (see <see cref="TJSubsampling"/>).</param>
    /// <param name="jpegQual">JPEG quality 1-100.</param>
    /// <param name="flags">Encoding flags (see <see cref="TJFlag"/>).</param>
    /// <returns>0 on success, -1 on error.</returns>
    [LibraryImport(LibraryName)]
    public static partial int tjCompress2(
        IntPtr handle,
        IntPtr srcBuf,
        int width,
        int pitch,
        int height,
        int pixelFormat,
        ref IntPtr jpegBuf,
        ref nuint jpegSize,
        int jpegSubsamp,
        int jpegQual,
        int flags);

    /// <summary>
    /// Free a buffer allocated by TurboJPEG.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial void tjFree(IntPtr buffer);

    #endregion

    #region Decompression

    /// <summary>
    /// Read JPEG header to get dimensions and subsampling without decompressing.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int tjDecompressHeader3(
        IntPtr handle,
        IntPtr jpegBuf,
        nuint jpegSize,
        out int width,
        out int height,
        out int jpegSubsamp,
        out int jpegColorspace);

    /// <summary>
    /// Decompress a JPEG image to a raw pixel buffer.
    /// </summary>
    /// <param name="handle">Decompressor handle from <see cref="tjInitDecompress"/>.</param>
    /// <param name="jpegBuf">Pointer to JPEG data.</param>
    /// <param name="jpegSize">Size of JPEG data.</param>
    /// <param name="dstBuf">Pre-allocated destination buffer.</param>
    /// <param name="width">Desired output width (0 = use JPEG width).</param>
    /// <param name="pitch">Bytes per row in destination (0 = tightly packed).</param>
    /// <param name="height">Desired output height (0 = use JPEG height).</param>
    /// <param name="pixelFormat">Desired output pixel format.</param>
    /// <param name="flags">Decoding flags.</param>
    /// <returns>0 on success, -1 on error.</returns>
    [LibraryImport(LibraryName)]
    public static partial int tjDecompress2(
        IntPtr handle,
        IntPtr jpegBuf,
        nuint jpegSize,
        IntPtr dstBuf,
        int width,
        int pitch,
        int height,
        int pixelFormat,
        int flags);

    #endregion

    #region Error Handling

    /// <summary>
    /// Get the last error message. Returns a pointer to a null-terminated string. Do NOT free.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial IntPtr tjGetErrorStr2(IntPtr handle);

    /// <summary>
    /// Get the last error code.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int tjGetErrorCode(IntPtr handle);

    #endregion

    #region Utility

    /// <summary>
    /// Calculate the maximum buffer size needed for a JPEG image with the given dimensions and subsampling.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial nuint tjBufSize(int width, int height, int jpegSubsamp);

    #endregion

    #region Enums

    /// <summary>
    /// Pixel formats for TurboJPEG. Must match TJPF_* constants from turbojpeg.h.
    /// </summary>
    public enum TJPixelFormat : int
    {
        RGB = 0,
        BGR = 1,
        RGBX = 2,
        BGRX = 3,
        XBGR = 4,
        XRGB = 5,
        Gray = 6,
        RGBA = 7,
        BGRA = 8,
        ABGR = 9,
        ARGB = 10,
        CMYK = 11,
    }

    /// <summary>
    /// Chroma subsampling options.
    /// </summary>
    public enum TJSubsampling : int
    {
        Samp444 = 0,
        Samp422 = 1,
        Samp420 = 2,
        Gray = 3,
        Samp440 = 4,
        Samp411 = 5,
    }

    /// <summary>
    /// Encoding/decoding flags.
    /// </summary>
    [Flags]
    public enum TJFlag : int
    {
        None = 0,
        BottomUp = 2,
        FastUpsample = 256,
        NoRealloc = 1024,
        FastDCT = 2048,
        AccurateDCT = 4096,
        Progressive = 16384,
    }

    #endregion
}
