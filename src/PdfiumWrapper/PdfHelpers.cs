using System.Runtime.InteropServices;

namespace PdfiumWrapper;

public static class PdfHelpers
{
    private const int SaveFileBufferSize = 128 * 1024;

    public static byte[] ReadStreamToBytes(this Stream stream)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        if (stream is MemoryStream ms)
        {
            return ms.ToArray();
        }

        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    public static FileStream OpenWriteFileStream(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        return new FileStream(
            filePath,
            new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.Create,
                Share = FileShare.None,
                BufferSize = SaveFileBufferSize
            });
    }
}

internal sealed class StreamDocumentLoader : IDisposable
{
    private readonly Stream _stream;
    private readonly long _startOffset;
    private readonly long _length;
    private readonly object _streamLock = new();
    private readonly GetBlockDelegate _getBlockDelegate;
    private GCHandle _selfHandle;
    private GCHandle _delegateHandle;
    private bool _disposed;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetBlockDelegate(IntPtr param, CULong position, IntPtr buffer, CULong size);

    public StreamDocumentLoader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanRead)
            throw new ArgumentException("Stream must be readable.", nameof(stream));
        if (!stream.CanSeek)
            throw new ArgumentException("Stream must be seekable.", nameof(stream));

        _stream = stream;
        _startOffset = stream.Position;
        _length = checked(stream.Length - _startOffset);
        _getBlockDelegate = GetBlock;
        _selfHandle = GCHandle.Alloc(this);
        _delegateHandle = GCHandle.Alloc(_getBlockDelegate);
    }

    public PDFium.FPDF_FILEACCESS GetFileAccessStruct()
    {
        return new PDFium.FPDF_FILEACCESS
        {
            m_FileLen = ToCULong(_length),
            m_GetBlock = Marshal.GetFunctionPointerForDelegate(_getBlockDelegate),
            m_Param = GCHandle.ToIntPtr(_selfHandle)
        };
    }

    private static int GetBlock(IntPtr param, CULong position, IntPtr buffer, CULong size)
    {
        var loaderHandle = GCHandle.FromIntPtr(param);
        if (loaderHandle.Target is not StreamDocumentLoader loader)
        {
            return 0;
        }

        return loader.ReadBlock(position.Value, buffer, size.Value);
    }

    private int ReadBlock(nuint position, IntPtr buffer, nuint size)
    {
        try
        {
            int bytesToRead = checked((int)size);
            long relativePosition = checked((long)position);

            if (relativePosition < 0 || relativePosition > _length)
            {
                return 0;
            }

            if (bytesToRead > _length - relativePosition)
            {
                return 0;
            }

            lock (_streamLock)
            {
                _stream.Position = _startOffset + relativePosition;

                unsafe
                {
                    var destination = new Span<byte>(buffer.ToPointer(), bytesToRead);
                    int totalRead = 0;

                    while (totalRead < bytesToRead)
                    {
                        int read = _stream.Read(destination[totalRead..]);
                        if (read == 0)
                        {
                            return 0;
                        }

                        totalRead += read;
                    }
                }
            }

            return 1;
        }
        catch
        {
            return 0;
        }
    }

    private static CULong ToCULong(long value)
    {
        checked
        {
            return OperatingSystem.IsWindows()
                ? new CULong((uint)value)
                : new CULong((nuint)value);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_delegateHandle.IsAllocated)
        {
            _delegateHandle.Free();
        }

        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }

        _disposed = true;
    }
}

internal sealed class PdfStreamFileWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly WriteBlockDelegate _writeDelegate;
    private GCHandle _delegateHandle;
    private bool _disposed;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WriteBlockDelegate(IntPtr pThis, IntPtr data, CULong size);

    public PdfStreamFileWriter(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _writeDelegate = WriteBlock;
        _delegateHandle = GCHandle.Alloc(_writeDelegate);
    }

    public PDFium.FPDF_FILEWRITE GetFileWriteStruct()
    {
        return new PDFium.FPDF_FILEWRITE
        {
            version = 1,
            WriteBlock = Marshal.GetFunctionPointerForDelegate(_writeDelegate)
        };
    }

    private int WriteBlock(IntPtr pThis, IntPtr pData, CULong size)
    {
        try
        {
            int bytesToWrite = checked((int)size.Value);
            unsafe
            {
                var span = new ReadOnlySpan<byte>(pData.ToPointer(), bytesToWrite);
                _stream.Write(span);
            }

            return 1;
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_delegateHandle.IsAllocated)
        {
            _delegateHandle.Free();
        }

        _disposed = true;
    }
}
