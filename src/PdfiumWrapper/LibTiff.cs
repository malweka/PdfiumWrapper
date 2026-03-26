using System.Runtime.InteropServices;

namespace PdfiumWrapper;

/// <summary>
/// P/Invoke bindings for libtiff using modern LibraryImport.
/// Covers multi-page TIFF writing with CCITT G4, LZW, and Deflate compression.
/// </summary>
public static partial class LibTiff
{
    private const string LibraryName = "tiff";

    static LibTiff()
    {
        NativeLibraryResolver.EnsureRegistered();
    }

    #region Lifecycle

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr TIFFOpen(string filename, string mode);

    /// <summary>
    /// Opens a TIFF with custom I/O callbacks (e.g. for writing to a Stream).
    /// </summary>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr TIFFClientOpen(
        string filename, string mode, IntPtr clientdata,
        IntPtr readproc, IntPtr writeproc, IntPtr seekproc,
        IntPtr closeproc, IntPtr sizeproc,
        IntPtr mapproc, IntPtr unmapproc);

    [LibraryImport(LibraryName)]
    public static partial void TIFFClose(IntPtr tiff);

    #endregion

    #region I/O Callback Delegates

    // Callback signatures for TIFFClientOpen.
    // thandle_t = IntPtr, tmsize_t = nint, toff_t = ulong.

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate nint TIFFReadWriteProc(IntPtr clientdata, IntPtr data, nint size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate ulong TIFFSeekProc(IntPtr clientdata, ulong offset, int whence);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int TIFFCloseProc(IntPtr clientdata);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate ulong TIFFSizeProc(IntPtr clientdata);

    #endregion

    #region Field Setters

    // TIFFSetField is variadic in C (uint32_t tag, ...).
    // LibraryImport does not support variadic functions directly, so we provide
    // typed overloads via EntryPoint aliasing for each value type we need.
    //
    // NOTE: On ARM64, the variadic calling convention differs from standard.
    // If issues arise on ARM64 macOS, these overloads may need to fall back to
    // DllImport with CallingConvention.Cdecl which handles variadic correctly.

    [LibraryImport(LibraryName, EntryPoint = "TIFFSetField")]
    public static partial int TIFFSetFieldInt(IntPtr tiff, uint tag, int value);

    [LibraryImport(LibraryName, EntryPoint = "TIFFSetField")]
    public static partial int TIFFSetFieldShort(IntPtr tiff, uint tag, short value);

    [LibraryImport(LibraryName, EntryPoint = "TIFFSetField")]
    public static partial int TIFFSetFieldFloat(IntPtr tiff, uint tag, float value);

    [LibraryImport(LibraryName, EntryPoint = "TIFFSetField")]
    public static partial int TIFFSetFieldDouble(IntPtr tiff, uint tag, double value);

    #endregion

    #region Writing

    [LibraryImport(LibraryName)]
    public static partial int TIFFWriteScanline(IntPtr tiff, IntPtr buf, int row, ushort sample);

    [LibraryImport(LibraryName)]
    public static partial int TIFFWriteDirectory(IntPtr tiff);

    [LibraryImport(LibraryName)]
    public static partial int TIFFFlush(IntPtr tiff);

    #endregion

    #region Error Handling

    [LibraryImport(LibraryName)]
    public static partial IntPtr TIFFSetErrorHandler(IntPtr handler);

    [LibraryImport(LibraryName)]
    public static partial IntPtr TIFFSetWarningHandler(IntPtr handler);

    #endregion

    #region Tag Constants

    public const uint TIFFTAG_SUBFILETYPE = 254;
    public const uint TIFFTAG_IMAGEWIDTH = 256;
    public const uint TIFFTAG_IMAGELENGTH = 257;
    public const uint TIFFTAG_BITSPERSAMPLE = 258;
    public const uint TIFFTAG_COMPRESSION = 259;
    public const uint TIFFTAG_PHOTOMETRIC = 262;
    public const uint TIFFTAG_FILLORDER = 266;
    public const uint TIFFTAG_SAMPLESPERPIXEL = 277;
    public const uint TIFFTAG_ROWSPERSTRIP = 278;
    public const uint TIFFTAG_XRESOLUTION = 282;
    public const uint TIFFTAG_YRESOLUTION = 283;
    public const uint TIFFTAG_PLANARCONFIG = 284;
    public const uint TIFFTAG_RESOLUTIONUNIT = 296;

    #endregion

    #region Value Constants

    // Compression schemes
    public const int COMPRESSION_CCITT_T6 = 4;
    public const int COMPRESSION_LZW = 5;
    public const int COMPRESSION_DEFLATE = 32946;

    // Photometric interpretation
    public const int PHOTOMETRIC_MINISWHITE = 0;
    public const int PHOTOMETRIC_MINISBLACK = 1;
    public const int PHOTOMETRIC_RGB = 2;

    // Fill order
    public const int FILLORDER_MSB2LSB = 1;

    // Planar configuration
    public const int PLANARCONFIG_CONTIG = 1;

    // Resolution unit
    public const int RESUNIT_INCH = 2;

    // SubFileType
    public const int FILETYPE_PAGE = 2;

    #endregion
}
