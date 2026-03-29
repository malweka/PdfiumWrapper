namespace PdfiumWrapper;

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
        int width, height;
        byte[] bgraPixels;

        if (IsJpeg(imageBytes))
        {
            using var decoder = new JpegDecoder();
            var (pixels, info) = decoder.Decode(imageBytes, LibTurboJpeg.TJPixelFormat.BGRA);
            width = info.Width;
            height = info.Height;
            bgraPixels = pixels;
        }
        else if (IsPng(imageBytes))
        {
            unsafe
            {
                fixed (byte* ptr = imageBytes)
                {
                    int result = LibPdfiumPng.pdfium_png_decode_from_memory(
                        (IntPtr)ptr,
                        (nuint)imageBytes.Length,
                        LibPdfiumPng.PngPixelFormat.BGRA,
                        out var outData,
                        out width,
                        out height,
                        out var outStride);

                    if (result != 0)
                    {
                        var errPtr = LibPdfiumPng.pdfium_png_get_error();
                        var msg = errPtr != IntPtr.Zero
                            ? System.Runtime.InteropServices.Marshal.PtrToStringAnsi(errPtr)
                            : "unknown error";
                        throw new InvalidOperationException($"PNG decode failed: {msg}");
                    }

                    try
                    {
                        bgraPixels = new byte[outStride * height];
                        System.Runtime.InteropServices.Marshal.Copy(outData, bgraPixels, 0, bgraPixels.Length);
                    }
                    finally
                    {
                        LibPdfiumPng.pdfium_png_free(outData);
                    }
                }
            }
        }
        else
        {
            throw new NotSupportedException("Image format not supported. Only JPEG and PNG are supported.");
        }

        // Create PDFium bitmap and copy decoded BGRA pixels
        var bitmap = PDFium.FPDFBitmap_Create(width, height, 1);
        if (bitmap == IntPtr.Zero)
            return IntPtr.Zero;

        var buffer = PDFium.FPDFBitmap_GetBuffer(bitmap);
        var stride = PDFium.FPDFBitmap_GetStride(bitmap);

        // Copy row by row to account for stride differences
        int srcStride = width * 4;
        unsafe
        {
            byte* dst = (byte*)buffer.ToPointer();
            fixed (byte* src = bgraPixels)
            {
                for (int y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(
                        src + y * srcStride,
                        dst + y * stride,
                        stride,
                        srcStride);
                }
            }
        }

        return bitmap;
    }

    private static bool IsJpeg(byte[] data)
        => data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8;

    private static bool IsPng(byte[] data)
        => data.Length >= 8
           && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
           && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A;
}

