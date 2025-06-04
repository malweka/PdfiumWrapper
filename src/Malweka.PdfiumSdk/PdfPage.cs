using System.Runtime.InteropServices;

namespace Malweka.PdfiumSdk;

/// <summary>
/// Represents a single page in a PDF document
/// </summary>
public class PdfPage : IDisposable
{
    private IntPtr _page;
    private bool _disposed;

    internal PdfPage(IntPtr document, int pageIndex)
    {
        _page = PDFium.FPDF_LoadPage(document, pageIndex);
        if (_page == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Failed to load page {pageIndex}. Error: {PDFium.FPDF_GetLastError()}");
        }
    }

    public double Width => PDFium.FPDF_GetPageWidth(_page);
    public double Height => PDFium.FPDF_GetPageHeight(_page);

    public byte[] RenderToBytes(int width, int height, int flags = 0)
    {
        var bitmap = PDFium.FPDFBitmap_Create(width, height, 0);
        if (bitmap == IntPtr.Zero)
            throw new OutOfMemoryException("Failed to create bitmap");

        try
        {
            // Fill with white background
            PDFium.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);

            // Render page to bitmap
            PDFium.FPDF_RenderPageBitmap(bitmap, _page, 0, 0, width, height, 0, flags);

            // Get bitmap data
            var buffer = PDFium.FPDFBitmap_GetBuffer(bitmap);
            var stride = PDFium.FPDFBitmap_GetStride(bitmap);
            var size = stride * height;

            var result = new byte[size];
            Marshal.Copy(buffer, result, 0, size);

            return result;
        }
        finally
        {
            PDFium.FPDFBitmap_Destroy(bitmap);
        }
    }

    public string ExtractText()
    {
        var textPage = PDFium.FPDFText_LoadPage(_page);
        if (textPage == IntPtr.Zero)
            return string.Empty;

        try
        {
            var charCount = PDFium.FPDFText_CountChars(textPage);
            if (charCount <= 0)
                return string.Empty;

            var buffer = Marshal.AllocHGlobal((charCount + 1) * 2); // UTF-16
            try
            {
                var actualCount = PDFium.FPDFText_GetText(textPage, 0, charCount, buffer);
                if (actualCount > 0)
                {
                    return Marshal.PtrToStringUni(buffer, actualCount - 1); // -1 to exclude null terminator
                }
                return string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            PDFium.FPDFText_ClosePage(textPage);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_page != IntPtr.Zero)
            {
                PDFium.FPDF_ClosePage(_page);
                _page = IntPtr.Zero;
            }
            _disposed = true;
        }
    }
}