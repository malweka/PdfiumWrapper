using System.Drawing;
using System.Runtime.InteropServices;

namespace Malweka.PdfiumSdk;

/// <summary>
/// Represents a single page in a PDF document
/// </summary>
public class PdfPage : IDisposable
{
    private IntPtr _page;
    private IntPtr _document;
    private bool _disposed;

    internal PdfPage(IntPtr document, int pageIndex)
    {
        _document = document;
        PageIndex = pageIndex;
        _page = PDFium.FPDF_LoadPage(document, pageIndex);
        if (_page == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Failed to load page {pageIndex}. Error: {PDFium.FPDF_GetLastError()}");
        }
    }

    public int PageIndex { get; }

    public double Width => PDFium.FPDF_GetPageWidth(_page);

    public double Height => PDFium.FPDF_GetPageHeight(_page);

    internal IntPtr Handle => _page;
    internal IntPtr DocumentHandle => _document;

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

    /// <summary>
    /// Check if this page has an embedded thumbnail
    /// </summary>
    public bool HasEmbeddedThumbnail
    {
        get
        {
            var thumbnail = PDFium.FPDFPage_GetThumbnailAsBitmap(_page);
            if (thumbnail != IntPtr.Zero)
            {
                PDFium.FPDFBitmap_Destroy(thumbnail);
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Get the embedded thumbnail as raw BGRA bytes (if it exists)
    /// Returns null if no embedded thumbnail exists
    /// </summary>
    public byte[] GetEmbeddedThumbnailBytes()
    {
        var thumbnail = PDFium.FPDFPage_GetThumbnailAsBitmap(_page);
        if (thumbnail == IntPtr.Zero)
            return null;

        try
        {
            var width = PDFium.FPDFBitmap_GetWidth(thumbnail);
            var height = PDFium.FPDFBitmap_GetHeight(thumbnail);
            var stride = PDFium.FPDFBitmap_GetStride(thumbnail);
            var buffer = PDFium.FPDFBitmap_GetBuffer(thumbnail);

            var size = stride * height;
            var result = new byte[size];
            Marshal.Copy(buffer, result, 0, size);

            return result;
        }
        finally
        {
            PDFium.FPDFBitmap_Destroy(thumbnail);
        }
    }

    /// <summary>
    /// Get the embedded thumbnail dimensions (if it exists)
    /// Returns null if no embedded thumbnail exists
    /// </summary>
    public (int width, int height)? GetEmbeddedThumbnailSize()
    {
        var thumbnail = PDFium.FPDFPage_GetThumbnailAsBitmap(_page);
        if (thumbnail == IntPtr.Zero)
            return null;

        try
        {
            var width = PDFium.FPDFBitmap_GetWidth(thumbnail);
            var height = PDFium.FPDFBitmap_GetHeight(thumbnail);
            return (width, height);
        }
        finally
        {
            PDFium.FPDFBitmap_Destroy(thumbnail);
        }
    }

    #region Page Editing

    /// <summary>
    /// Add a text object to the page
    /// </summary>
    public PdfTextObject AddText(string text, float x, float y, string font = "Helvetica", float fontSize = 12)
    {
        var textObj = PdfTextObject.Create(_document, font, fontSize);
        textObj.Text = text;
        
        // Position the text object
        textObj.SetMatrix(1, 0, 0, 1, x, y);
        
        // Insert into page
        PDFium.FPDFPage_InsertObject(_page, textObj.Handle);
        textObj.IsAttachedToPage = true;
        
        return textObj;
    }

    /// <summary>
    /// Add an image object to the page
    /// </summary>
    public PdfImageObject AddImage(byte[] imageBytes, float x, float y, float width, float height)
    {
        var imageObj = PdfImageObject.Create(_document);
        imageObj.SetImage(imageBytes, _page);
        imageObj.SetPositionAndSize(x, y, width, height);
        
        // Insert into page
        PDFium.FPDFPage_InsertObject(_page, imageObj.Handle);
        imageObj.IsAttachedToPage = true;
        
        return imageObj;
    }

    /// <summary>
    /// Add a path object to the page
    /// </summary>
    public PdfPathObject AddPath()
    {
        var pathObj = PdfPathObject.Create(_document);
        
        // Insert into page
        PDFium.FPDFPage_InsertObject(_page, pathObj.Handle);
        pathObj.IsAttachedToPage = true;
        
        return pathObj;
    }

    /// <summary>
    /// Add a rectangle to the page
    /// </summary>
    public PdfPathObject AddRectangle(float x, float y, float width, float height, Color? fillColor = null, Color? strokeColor = null)
    {
        var rectObj = PdfPathObject.CreateRectangle(_document, x, y, width, height);
        
        if (fillColor.HasValue)
        {
            rectObj.FillColor = fillColor.Value;
            rectObj.SetDrawMode(PdfPathFillMode.Winding, strokeColor.HasValue);
        }
        else if (strokeColor.HasValue)
        {
            rectObj.SetDrawMode(PdfPathFillMode.None, true);
        }
        
        if (strokeColor.HasValue)
        {
            rectObj.StrokeColor = strokeColor.Value;
        }
        
        // Insert into page
        PDFium.FPDFPage_InsertObject(_page, rectObj.Handle);
        rectObj.IsAttachedToPage = true;
        
        return rectObj;
    }

    /// <summary>
    /// Remove a page object from the page
    /// </summary>
    public bool RemoveObject(PdfPageObject pageObject)
    {
        if (pageObject == null)
            throw new ArgumentNullException(nameof(pageObject));

        var result = PDFium.FPDFPage_RemoveObject(_page, pageObject.Handle);
        if (result)
        {
            pageObject.IsAttachedToPage = false;
        }
        return result;
    }

    /// <summary>
    /// Generate the page content stream (required after adding/modifying objects)
    /// </summary>
    public void GenerateContent()
    {
        if (!PDFium.FPDFPage_GenerateContent(_page))
            throw new InvalidOperationException("Failed to generate page content");
    }

    /// <summary>
    /// Get the number of objects on the page
    /// </summary>
    public int ObjectCount => PDFium.FPDFPage_CountObjects(_page);

    /// <summary>
    /// Get a page object by index
    /// </summary>
    public IntPtr GetObject(int index)
    {
        if (index < 0 || index >= ObjectCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return PDFium.FPDFPage_GetObject(_page, index);
    }

    #endregion

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