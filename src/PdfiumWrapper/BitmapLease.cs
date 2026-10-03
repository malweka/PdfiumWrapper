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

    /// <summary>The PDFium pixel format (<c>PDFium.FPDFBitmap_*</c>).</summary>
    public int Format { get; }

    /// <summary>True for an 8-bit gray bitmap (one byte per pixel); false for 32-bit BGRx/BGRA.</summary>
    public bool IsGray => Format == PDFium.FPDFBitmap_Gray;

    /// <summary>Takes ownership of <paramref name="handle"/>. The gate must be held.</summary>
    internal BitmapLease(IntPtr handle, IntPtr buffer, int width, int height, int stride, int format)
    {
        Handle = handle;
        Buffer = buffer;
        Width = width;
        Height = height;
        Stride = stride;
        Format = format;
        PdfiumRuntime.HandleOpened();
    }

    /// <summary>
    /// Takes ownership of a caller-owned bitmap that PDFium handed out (a thumbnail, an image
    /// object's pixels), so it is counted as a live handle and destroyed under the gate.
    /// The native gate must be held.
    /// </summary>
    /// <returns>The lease, or null when <paramref name="bitmap"/> is null.</returns>
    internal static BitmapLease? Adopt(IntPtr bitmap)
    {
        PdfiumRuntime.AssertHeld();

        if (bitmap == IntPtr.Zero)
            return null;

        return new BitmapLease(bitmap, PDFium.FPDFBitmap_GetBuffer(bitmap),
            PDFium.FPDFBitmap_GetWidth(bitmap), PDFium.FPDFBitmap_GetHeight(bitmap),
            PDFium.FPDFBitmap_GetStride(bitmap), PDFium.FPDFBitmap_GetFormat(bitmap));
    }

    /// <summary>
    /// Copies the pixels out as BGRA with the gate free, then destroys the native bitmap.
    /// Returns null for a null lease.
    /// </summary>
    internal static RawBitmap? ToBgraAndRelease(BitmapLease? lease)
    {
        if (lease == null)
            return null;

        using (lease)
        {
            return lease.ToBgraBitmap();
        }
    }

    /// <summary>Copy the pixels into a managed array as they are, stride included. No gate needed.</summary>
    public byte[] ToArray()
    {
        var pixels = new byte[ManagedLength(Stride, Height)];
        Marshal.Copy(Buffer, pixels, 0, pixels.Length);
        return pixels;
    }

    /// <summary>
    /// Copy the pixels into a tightly packed BGRA <see cref="RawBitmap"/>. Gray and BGR bitmaps are
    /// expanded, and formats without alpha get full opacity. No gate needed.
    /// </summary>
    public RawBitmap ToBgraBitmap()
    {
        int sourceBytesPerPixel = Format switch
        {
            PDFium.FPDFBitmap_Gray => 1,
            PDFium.FPDFBitmap_BGR => 3,
            PDFium.FPDFBitmap_BGRx or PDFium.FPDFBitmap_BGRA => 4,
            _ => throw new NotSupportedException($"Unsupported PDFium bitmap format {Format}."),
        };

        int width = Width;
        int height = Height;
        int stride = checked(width * 4);
        var pixels = new byte[ManagedLength(stride, height)];
        bool hasAlpha = Format == PDFium.FPDFBitmap_BGRA;

        unsafe
        {
            byte* source = (byte*)Buffer;
            fixed (byte* destination = pixels)
            {
                for (int y = 0; y < height; y++)
                {
                    byte* from = source + (long)y * Stride;
                    byte* to = destination + (long)y * stride;

                    for (int x = 0; x < width; x++, from += sourceBytesPerPixel, to += 4)
                    {
                        if (sourceBytesPerPixel == 1)
                        {
                            to[0] = to[1] = to[2] = from[0];
                        }
                        else
                        {
                            to[0] = from[0];
                            to[1] = from[1];
                            to[2] = from[2];
                        }

                        // Only BGRA carries alpha; the other formats are opaque.
                        to[3] = hasAlpha ? from[3] : (byte)255;
                    }
                }
            }
        }

        return new RawBitmap(pixels, width, height, stride);
    }

    /// <summary>Bytes in <paramref name="rows"/> rows of <paramref name="stride"/>, computed without wrapping.</summary>
    private static int ManagedLength(int stride, int rows)
    {
        long length = (long)stride * rows;
        if (length > Array.MaxLength)
            throw new InvalidOperationException(
                $"A {stride}-byte x {rows}-row bitmap ({length:N0} bytes) is too large for a managed array.");
        return (int)length;
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
