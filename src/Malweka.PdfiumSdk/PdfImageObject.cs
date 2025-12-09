namespace Malweka.PdfiumSdk;

/// <summary>
/// Represents an image object in a PDF page
/// </summary>
public class PdfImageObject : PdfPageObject
{
    internal PdfImageObject(IntPtr handle, IntPtr documentHandle) 
        : base(handle, documentHandle)
    {
    }

    /// <summary>
    /// Create a new image object
    /// </summary>
    public static PdfImageObject Create(IntPtr documentHandle)
    {
        var handle = PDFium.FPDFPageObj_NewImageObj(documentHandle);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create image object");

        return new PdfImageObject(handle, documentHandle);
    }

    /// <summary>
    /// Set the image from a bitmap
    /// </summary>
    public void SetBitmap(IntPtr bitmap, IntPtr page)
    {
        ThrowIfDisposed();
        // Note: pages parameter should be an array of page handles, but we'll use a single page
        var pageHandle = System.Runtime.InteropServices.Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            System.Runtime.InteropServices.Marshal.WriteIntPtr(pageHandle, page);
            if (!PDFium.FPDFImageObj_SetBitmap(pageHandle, 1, Handle, bitmap))
                throw new InvalidOperationException("Failed to set image bitmap");
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(pageHandle);
        }
    }

    /// <summary>
    /// Set the image from image bytes (PNG, JPEG, etc.)
    /// </summary>
    public void SetImage(byte[] imageBytes, IntPtr page)
    {
        ThrowIfDisposed();
        
        // Create a bitmap from the image bytes
        var bitmap = CreateBitmapFromBytes(imageBytes);
        if (bitmap == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create bitmap from image bytes");

        try
        {
            SetBitmap(bitmap, page);
        }
        finally
        {
            PDFium.FPDFBitmap_Destroy(bitmap);
        }
    }

    /// <summary>
    /// Set the position and size of the image
    /// </summary>
    public void SetPositionAndSize(float x, float y, float width, float height)
    {
        ThrowIfDisposed();
        // Use matrix transformation: [width, 0, 0, height, x, y]
        if (!PDFium.FPDFImageObj_SetMatrix(Handle, width, 0, 0, height, x, y))
            throw new InvalidOperationException("Failed to set image matrix");
    }

    /// <summary>
    /// Get the bitmap from the image object
    /// </summary>
    public IntPtr GetBitmap()
    {
        ThrowIfDisposed();
        return PDFium.FPDFImageObj_GetBitmap(Handle);
    }

    /// <summary>
    /// Get the rendered bitmap from the image object
    /// </summary>
    public IntPtr GetRenderedBitmap(IntPtr page)
    {
        ThrowIfDisposed();
        return PDFium.FPDFImageObj_GetRenderedBitmap(DocumentHandle, page, Handle);
    }

    private IntPtr CreateBitmapFromBytes(byte[] imageBytes)
    {
        // For simplicity, we'll use SkiaSharp to decode the image
        // In a real implementation, you might want to handle this differently
        using var skImage = SkiaSharp.SKImage.FromEncodedData(imageBytes);
        if (skImage == null)
            return IntPtr.Zero;

        using var skBitmap = SkiaSharp.SKBitmap.FromImage(skImage);
        var width = skBitmap.Width;
        var height = skBitmap.Height;

        // Create PDFium bitmap
        var bitmap = PDFium.FPDFBitmap_Create(width, height, 1); // 1 = has alpha
        if (bitmap == IntPtr.Zero)
            return IntPtr.Zero;

        // Get buffer and copy pixels
        var buffer = PDFium.FPDFBitmap_GetBuffer(bitmap);
        var stride = PDFium.FPDFBitmap_GetStride(bitmap);

        // Copy pixels (convert from RGBA to BGRA if needed)
        var pixels = skBitmap.GetPixels();
        
        unsafe
        {
            byte* src = (byte*)pixels.ToPointer();
            byte* dst = (byte*)buffer.ToPointer();

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int srcOffset = (y * width + x) * 4;
                    int dstOffset = y * stride + x * 4;

                    // Convert RGBA to BGRA
                    dst[dstOffset + 0] = src[srcOffset + 2]; // B
                    dst[dstOffset + 1] = src[srcOffset + 1]; // G
                    dst[dstOffset + 2] = src[srcOffset + 0]; // R
                    dst[dstOffset + 3] = src[srcOffset + 3]; // A
                }
            }
        }

        return bitmap;
    }
}

