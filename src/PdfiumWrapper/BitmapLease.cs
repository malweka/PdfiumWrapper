using System.Runtime.InteropServices;

namespace PdfiumWrapper;

/// <summary>
/// A rendered PDFium bitmap whose pixel buffer may be read without holding the native gate.
/// PDFium does not touch a bitmap after rendering returns, so encoding and output run with the
/// gate free and overlap other callers' rendering. Disposal reenters the gate to destroy the bitmap.
/// </summary>
internal sealed class BitmapLease : IDisposable, IAsyncDisposable
{
    public IntPtr Handle { get; private set; }
    public IntPtr Buffer { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }

    /// <summary>Takes ownership of <paramref name="handle"/>. The gate must be held.</summary>
    internal BitmapLease(IntPtr handle, IntPtr buffer, int width, int height, int stride)
    {
        Handle = handle;
        Buffer = buffer;
        Width = width;
        Height = height;
        Stride = stride;
        PdfiumRuntime.HandleOpened();
    }

    /// <summary>Copy the BGRA pixels into a managed array. No gate needed.</summary>
    public byte[] ToArray()
    {
        var pixels = new byte[Stride * Height];
        Marshal.Copy(Buffer, pixels, 0, pixels.Length);
        return pixels;
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero)
            return;

        using var _ = PdfiumRuntime.Enter();
        DestroyCore();
    }

    /// <summary>Async methods use this so a busy gate does not block a thread-pool thread.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Handle == IntPtr.Zero)
            return;

        using (await PdfiumRuntime.EnterAsync())
        {
            DestroyCore();
        }
    }

    private void DestroyCore()
    {
        var handle = Handle;
        if (handle == IntPtr.Zero)
            return;

        Handle = IntPtr.Zero;
        using (PdfiumDiagnostics.NativeInterval(NativeOp.Close))
            PDFium.FPDFBitmap_Destroy(handle);
        PdfiumRuntime.HandleClosed();
    }
}
