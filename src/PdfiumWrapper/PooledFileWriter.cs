using System.Buffers;
using System.Runtime.InteropServices;

namespace PdfiumWrapper;

/// <summary>
/// <c>FPDF_FILEWRITE</c> target that appends into an <see cref="ArrayPool{T}"/>-backed buffer.
/// PDFium writes into it while the gate is held; the caller copies the result to its own stream
/// after leaving the gate, so no user I/O ever runs inside the gate.
/// </summary>
internal sealed class PooledFileWriter : IDisposable
{
    private const int InitialCapacity = 256 * 1024;

    private readonly WriteBlockDelegate _writeDelegate;
    private GCHandle _delegateHandle;
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(InitialCapacity);
    private int _length;
    private bool _failed;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WriteBlockDelegate(IntPtr pThis, IntPtr data, CULong size);

    public PooledFileWriter()
    {
        _writeDelegate = WriteBlock;
        _delegateHandle = GCHandle.Alloc(_writeDelegate);
    }

    /// <summary>
    /// Serialize <paramref name="document"/> into a pooled buffer. The gate must be held.
    /// On success the caller owns <paramref name="buffer"/> and must return it to
    /// <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    public static bool TrySave(IntPtr document, uint flags, out byte[] buffer, out int length)
    {
        PdfiumRuntime.AssertHeld();

        using var writer = new PooledFileWriter();
        var fileWrite = writer.GetFileWriteStruct();

        bool success;
        using (PdfiumDiagnostics.NativeInterval(NativeOp.Save))
            success = PDFium.FPDF_SaveAsCopy(document, ref fileWrite, new CULong(flags));

        if (!success || writer._failed)
        {
            buffer = Array.Empty<byte>();
            length = 0;
            return false;
        }

        (buffer, length) = writer.Detach();
        return true;
    }

    private PDFium.FPDF_FILEWRITE GetFileWriteStruct()
    {
        return new PDFium.FPDF_FILEWRITE
        {
            version = 1,
            WriteBlock = Marshal.GetFunctionPointerForDelegate(_writeDelegate)
        };
    }

    private int WriteBlock(IntPtr pThis, IntPtr data, CULong size)
    {
        try
        {
            int count = checked((int)size.Value);
            int required = checked(_length + count);
            if (required > _buffer.Length)
                Grow(required);

            unsafe
            {
                new ReadOnlySpan<byte>(data.ToPointer(), count).CopyTo(_buffer.AsSpan(_length));
            }

            _length = required;
            return 1;
        }
        catch
        {
            // An exception must not unwind through native frames.
            _failed = true;
            return 0;
        }
    }

    private void Grow(int required)
    {
        int capacity = (int)Math.Min(Math.Max((long)_buffer.Length * 2, required), Array.MaxLength);
        var larger = ArrayPool<byte>.Shared.Rent(capacity);
        _buffer.AsSpan(0, _length).CopyTo(larger);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = larger;
    }

    private (byte[] Buffer, int Length) Detach()
    {
        var result = (_buffer, _length);
        _buffer = Array.Empty<byte>();
        _length = 0;
        return result;
    }

    public void Dispose()
    {
        if (_buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = Array.Empty<byte>();
        }

        if (_delegateHandle.IsAllocated)
            _delegateHandle.Free();
    }
}
