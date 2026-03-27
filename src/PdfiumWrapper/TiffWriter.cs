using System.Runtime.InteropServices;

namespace PdfiumWrapper;

/// <summary>
/// Writes multi-page TIFF files via native libtiff.
/// Not thread-safe per instance, but multiple instances can run concurrently (one per document).
/// </summary>
internal sealed class TiffWriter : IDisposable
{
    private IntPtr _tiff;
    private int _pageIndex;
    private bool _disposed;

    // For Stream-based writing: prevent GC of the stream and callback delegates
    private GCHandle _streamHandle;
    private LibTiff.TIFFReadWriteProc? _readDelegate;
    private LibTiff.TIFFReadWriteProc? _writeDelegate;
    private LibTiff.TIFFSeekProc? _seekDelegate;
    private LibTiff.TIFFCloseProc? _closeDelegate;
    private LibTiff.TIFFSizeProc? _sizeDelegate;

    // Pin delegates so they survive GC while libtiff holds the function pointers
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TiffMessageHandler(IntPtr module, IntPtr fmt, IntPtr args);

    private static readonly TiffMessageHandler s_errorHandler = OnTiffError;
    private static readonly TiffMessageHandler s_warningHandler = OnTiffWarning;
    private static readonly IntPtr s_errorHandlerPtr = Marshal.GetFunctionPointerForDelegate(s_errorHandler);
    private static readonly IntPtr s_warningHandlerPtr = Marshal.GetFunctionPointerForDelegate(s_warningHandler);
    private static int s_handlersInstalled;

    public TiffWriter(string outputPath)
    {
        InstallHandlers();

        _tiff = LibTiff.TIFFOpen(outputPath, "w");
        if (_tiff == IntPtr.Zero)
            throw new IOException($"libtiff: failed to open '{outputPath}' for writing.");
    }

    /// <summary>
    /// Creates a TiffWriter that writes to a Stream via TIFFClientOpen.
    /// The stream must be writable and seekable.
    /// </summary>
    public TiffWriter(Stream stream)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        if (!stream.CanWrite) throw new ArgumentException("Stream must be writable.", nameof(stream));
        if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));

        InstallHandlers();

        // Pin the stream so we can retrieve it from the IntPtr clientdata in callbacks
        _streamHandle = GCHandle.Alloc(stream);
        var clientdata = GCHandle.ToIntPtr(_streamHandle);

        // Create and pin callback delegates (prevent GC while libtiff holds the pointers)
        _readDelegate = StreamReadProc;
        _writeDelegate = StreamWriteProc;
        _seekDelegate = StreamSeekProc;
        _closeDelegate = StreamCloseProc;
        _sizeDelegate = StreamSizeProc;

        _tiff = LibTiff.TIFFClientOpen(
            "stream", "w", clientdata,
            Marshal.GetFunctionPointerForDelegate(_readDelegate),
            Marshal.GetFunctionPointerForDelegate(_writeDelegate),
            Marshal.GetFunctionPointerForDelegate(_seekDelegate),
            Marshal.GetFunctionPointerForDelegate(_closeDelegate),
            Marshal.GetFunctionPointerForDelegate(_sizeDelegate),
            IntPtr.Zero, IntPtr.Zero);

        if (_tiff == IntPtr.Zero)
        {
            _streamHandle.Free();
            throw new IOException("libtiff: failed to open stream for writing via TIFFClientOpen.");
        }
    }

    #region Stream I/O Callbacks

    private static nint StreamReadProc(IntPtr clientdata, IntPtr data, nint size)
    {
        var stream = (Stream)GCHandle.FromIntPtr(clientdata).Target!;
        var buffer = new byte[(int)size];
        int bytesRead = stream.Read(buffer, 0, (int)size);
        Marshal.Copy(buffer, 0, data, bytesRead);
        return bytesRead;
    }

    private static nint StreamWriteProc(IntPtr clientdata, IntPtr data, nint size)
    {
        var stream = (Stream)GCHandle.FromIntPtr(clientdata).Target!;
        var buffer = new byte[(int)size];
        Marshal.Copy(data, buffer, 0, (int)size);
        stream.Write(buffer, 0, (int)size);
        return size;
    }

    private static ulong StreamSeekProc(IntPtr clientdata, ulong offset, int whence)
    {
        var stream = (Stream)GCHandle.FromIntPtr(clientdata).Target!;
        var origin = whence switch
        {
            0 => SeekOrigin.Begin,
            1 => SeekOrigin.Current,
            2 => SeekOrigin.End,
            _ => SeekOrigin.Begin
        };
        return (ulong)stream.Seek((long)offset, origin);
    }

    private static int StreamCloseProc(IntPtr clientdata)
    {
        // Don't close the stream — the caller owns it
        return 0;
    }

    private static ulong StreamSizeProc(IntPtr clientdata)
    {
        var stream = (Stream)GCHandle.FromIntPtr(clientdata).Target!;
        return (ulong)stream.Length;
    }

    #endregion

    /// <summary>
    /// Writes a 1-bit bilevel page with CCITT G4 compression.
    /// Data must be packed 1-bit MSB-first, one row = ceil(width/8) bytes.
    /// </summary>
    public void WriteBilevelPage(byte[] data, int width, int height,
        float dpiX, float dpiY, int totalPages)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int bytesPerRow = (width + 7) / 8;

        SetPageFields(width, height, dpiX, dpiY, totalPages,
            bitsPerSample: 1, samplesPerPixel: 1,
            compression: LibTiff.COMPRESSION_CCITT_T6,
            photometric: LibTiff.PHOTOMETRIC_MINISWHITE);

        WriteScanlines(data, height, bytesPerRow);
        FinalizePage();
    }

    /// <summary>
    /// Writes an 8-bit grayscale page with LZW compression.
    /// Data is 1 byte per pixel, row-major, one row = width bytes.
    /// </summary>
    public void WriteGrayscalePage(byte[] data, int width, int height,
        float dpiX, float dpiY, int totalPages)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        SetPageFields(width, height, dpiX, dpiY, totalPages,
            bitsPerSample: 8, samplesPerPixel: 1,
            compression: LibTiff.COMPRESSION_LZW,
            photometric: LibTiff.PHOTOMETRIC_MINISBLACK);

        WriteScanlines(data, height, width);
        FinalizePage();
    }

    /// <summary>
    /// Pin data once, write all scanlines via pointer offset — zero per-row allocation.
    /// </summary>
    private void WriteScanlines(byte[] data, int height, int bytesPerRow)
    {
        unsafe
        {
            fixed (byte* ptr = data)
            {
                for (int row = 0; row < height; row++)
                {
                    var rowPtr = (IntPtr)(ptr + row * bytesPerRow);
                    if (LibTiff.TIFFWriteScanline(_tiff, rowPtr, row, 0) < 0)
                        throw new IOException($"libtiff: TIFFWriteScanline failed at row {row}.");
                }
            }
        }
    }

    private void SetPageFields(int width, int height, float dpiX, float dpiY,
        int totalPages, int bitsPerSample, int samplesPerPixel,
        int compression, int photometric)
    {
        var t = _tiff;

        CheckField("SUBFILETYPE", LibTiff.TIFFSetFieldInt(t, LibTiff.TIFFTAG_SUBFILETYPE, LibTiff.FILETYPE_PAGE));
        CheckField("IMAGEWIDTH", LibTiff.TIFFSetFieldInt(t, LibTiff.TIFFTAG_IMAGEWIDTH, width));
        CheckField("IMAGELENGTH", LibTiff.TIFFSetFieldInt(t, LibTiff.TIFFTAG_IMAGELENGTH, height));
        CheckField("BITSPERSAMPLE", LibTiff.TIFFSetFieldInt(t, LibTiff.TIFFTAG_BITSPERSAMPLE, bitsPerSample));
        CheckField("SAMPLESPERPIXEL", LibTiff.TIFFSetFieldInt(t, LibTiff.TIFFTAG_SAMPLESPERPIXEL, samplesPerPixel));
        CheckField("COMPRESSION", LibTiff.TIFFSetFieldInt(t, LibTiff.TIFFTAG_COMPRESSION, compression));
        CheckField("PHOTOMETRIC", LibTiff.TIFFSetFieldInt(t, LibTiff.TIFFTAG_PHOTOMETRIC, photometric));
        CheckField("FILLORDER", LibTiff.TIFFSetFieldInt(t, LibTiff.TIFFTAG_FILLORDER, LibTiff.FILLORDER_MSB2LSB));
        CheckField("PLANARCONFIG", LibTiff.TIFFSetFieldInt(t, LibTiff.TIFFTAG_PLANARCONFIG, LibTiff.PLANARCONFIG_CONTIG));
        CheckField("XRESOLUTION", LibTiff.TIFFSetFieldDouble(t, LibTiff.TIFFTAG_XRESOLUTION, dpiX));
        CheckField("YRESOLUTION", LibTiff.TIFFSetFieldDouble(t, LibTiff.TIFFTAG_YRESOLUTION, dpiY));
        CheckField("RESOLUTIONUNIT", LibTiff.TIFFSetFieldInt(t, LibTiff.TIFFTAG_RESOLUTIONUNIT, LibTiff.RESUNIT_INCH));
        CheckField("ROWSPERSTRIP", LibTiff.TIFFSetFieldInt(t, LibTiff.TIFFTAG_ROWSPERSTRIP, height));
    }

    private static void CheckField(string name, int result)
    {
        if (result == 0)
            throw new IOException($"libtiff: TIFFSetField({name}) failed.");
    }

    private void FinalizePage()
    {
        if (LibTiff.TIFFWriteDirectory(_tiff) == 0)
            throw new IOException("libtiff: TIFFWriteDirectory failed.");
        _pageIndex++;
    }

    private static void InstallHandlers()
    {
        if (Interlocked.CompareExchange(ref s_handlersInstalled, 1, 0) == 0)
        {
            LibTiff.TIFFSetErrorHandler(s_errorHandlerPtr);
            LibTiff.TIFFSetWarningHandler(s_warningHandlerPtr);
        }
    }

    private static void OnTiffError(IntPtr module, IntPtr fmt, IntPtr args)
    {
        System.Diagnostics.Debug.WriteLine("[libtiff ERROR]");
    }

    private static void OnTiffWarning(IntPtr module, IntPtr fmt, IntPtr args)
    {
        System.Diagnostics.Debug.WriteLine("[libtiff WARN]");
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_tiff != IntPtr.Zero)
            {
                LibTiff.TIFFClose(_tiff);
                _tiff = IntPtr.Zero;
            }
            if (_streamHandle.IsAllocated)
            {
                _streamHandle.Free();
            }
        }
    }
}
