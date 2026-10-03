using System.Runtime.ExceptionServices;
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

    // For Stream-based writing: the callbacks reach this writer through a GCHandle, and the
    // delegates are kept alive while libtiff holds their function pointers.
    private Stream? _stream;
    private ExceptionDispatchInfo? _streamFailure;
    private GCHandle _selfHandle;
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
    private static bool s_handlersInstalled;

    /// <summary>
    /// Creates a TiffWriter that writes to a Stream via TIFFClientOpen.
    /// The stream must be writable and seekable. File output goes through a managed
    /// <see cref="FileStream"/> as well: libtiff's TIFFOpen reads the path in the ANSI code page
    /// on Windows, so non-ASCII paths would break.
    /// </summary>
    public TiffWriter(Stream stream)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        if (!stream.CanWrite) throw new ArgumentException("Stream must be writable.", nameof(stream));
        if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));

        // The callbacks get this writer back from the clientdata pointer
        _stream = stream;
        _selfHandle = GCHandle.Alloc(this);
        var clientdata = GCHandle.ToIntPtr(_selfHandle);

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
            _selfHandle.Free();
            ThrowStreamFailureOr("libtiff: failed to open stream for writing via TIFFClientOpen.");
        }
    }

    #region Stream I/O Callbacks

    // libtiff calls these from native code. An exception must not unwind through native frames
    // (on Linux and macOS that terminates the process), so each callback catches, records the
    // first failure, and returns libtiff's error value. The libtiff call that fails as a result
    // rethrows the recorded exception (ThrowStreamFailureOr).

    private static TiffWriter Writer(IntPtr clientdata) => (TiffWriter)GCHandle.FromIntPtr(clientdata).Target!;

    private void Fail(Exception ex) => _streamFailure ??= ExceptionDispatchInfo.Capture(ex);

    private static nint StreamReadProc(IntPtr clientdata, IntPtr data, nint size)
    {
        var writer = Writer(clientdata);
        try
        {
            var buffer = new byte[(int)size];
            int bytesRead = writer._stream!.Read(buffer, 0, (int)size);
            Marshal.Copy(buffer, 0, data, bytesRead);
            return bytesRead;
        }
        catch (Exception ex)
        {
            writer.Fail(ex);
            return -1;
        }
    }

    private static nint StreamWriteProc(IntPtr clientdata, IntPtr data, nint size)
    {
        var writer = Writer(clientdata);
        try
        {
            var buffer = new byte[(int)size];
            Marshal.Copy(data, buffer, 0, (int)size);
            writer._stream!.Write(buffer, 0, (int)size);
            return size;
        }
        catch (Exception ex)
        {
            writer.Fail(ex);
            return -1;
        }
    }

    private static ulong StreamSeekProc(IntPtr clientdata, ulong offset, int whence)
    {
        var writer = Writer(clientdata);
        try
        {
            var origin = whence switch
            {
                0 => SeekOrigin.Begin,
                1 => SeekOrigin.Current,
                2 => SeekOrigin.End,
                _ => SeekOrigin.Begin
            };
            return (ulong)writer._stream!.Seek((long)offset, origin);
        }
        catch (Exception ex)
        {
            writer.Fail(ex);
            return ulong.MaxValue; // (toff_t)-1
        }
    }

    private static int StreamCloseProc(IntPtr clientdata)
    {
        // Don't close the stream — the caller owns it
        return 0;
    }

    private static ulong StreamSizeProc(IntPtr clientdata)
    {
        var writer = Writer(clientdata);
        try
        {
            return (ulong)writer._stream!.Length;
        }
        catch (Exception ex)
        {
            writer.Fail(ex);
            return 0;
        }
    }

    /// <summary>
    /// Throws the exception a stream callback recorded, which is why libtiff failed, or an
    /// <see cref="IOException"/> with <paramref name="message"/> when there is none.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void ThrowStreamFailureOr(string message)
    {
        _streamFailure?.Throw();
        throw new IOException(message);
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
                        ThrowStreamFailureOr($"libtiff: TIFFWriteScanline failed at row {row}.");
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
            ThrowStreamFailureOr("libtiff: TIFFWriteDirectory failed.");
        _pageIndex++;
    }

    /// <summary>
    /// Installs libtiff's process-global error handlers. Called once by <see cref="PdfiumRuntime"/>
    /// during native initialization, under the gate, so it cannot race with another writer.
    /// </summary>
    internal static void EnsureHandlersInstalled()
    {
        if (s_handlersInstalled)
            return;

        LibTiff.TIFFSetErrorHandler(s_errorHandlerPtr);
        LibTiff.TIFFSetWarningHandler(s_warningHandlerPtr);
        s_handlersInstalled = true;
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
            if (_selfHandle.IsAllocated)
            {
                _selfHandle.Free();
            }
        }
    }
}
