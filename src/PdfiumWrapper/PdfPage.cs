using System.Drawing;
using System.Runtime.InteropServices;

namespace PdfiumWrapper;

/// <summary>
/// Represents a single page in a PDF document
/// </summary>
public class PdfPage : IDisposable
{
    private IntPtr _page;
    private readonly PdfDocument _owner;
    private bool _disposed;
    private readonly object _attachedObjectsLock = new();
    private HashSet<PdfPageObject>? _attachedObjects;

    internal PdfPage(PdfDocument owner, int pageIndex)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        PageIndex = pageIndex;
        _page = PDFium.FPDF_LoadPage(owner.Document, pageIndex);
        if (_page == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Failed to load page {pageIndex}. Error: {PDFium.FPDF_GetLastError()}");
        }

        try
        {
            _owner.RegisterPage(this);
        }
        catch
        {
            PDFium.FPDF_ClosePage(_page);
            _page = IntPtr.Zero;
            throw;
        }
    }

    public int PageIndex { get; }

    public double Width
    {
        get
        {
            ThrowIfDisposed();
            return PDFium.FPDF_GetPageWidth(_page);
        }
    }

    public double Height
    {
        get
        {
            ThrowIfDisposed();
            return PDFium.FPDF_GetPageHeight(_page);
        }
    }

    internal IntPtr Handle => _page;
    internal IntPtr DocumentHandle => _owner.Document;
    internal bool IsDisposedForChildObjects => _disposed || _owner.IsDisposed;

    /// <summary>
    /// Renders the page to a native PDFium bitmap handle.
    /// The caller MUST call PDFium.FPDFBitmap_Destroy on the returned handle.
    /// Use FPDFBitmap_GetBuffer/GetStride to read the BGRA pixel data directly.
    /// </summary>
    internal IntPtr RenderToBitmapHandle(int width, int height, int flags = 0)
    {
        ThrowIfDisposed();

        var bitmap = PDFium.FPDFBitmap_Create(width, height, 0);
        if (bitmap == IntPtr.Zero)
            throw new OutOfMemoryException("Failed to create bitmap");

        PDFium.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
        PDFium.FPDF_RenderPageBitmap(bitmap, _page, 0, 0, width, height, 0, flags);
        return bitmap;
    }

    public byte[] RenderToBytes(int width, int height, int flags = 0)
    {
        ThrowIfDisposed();

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
        ThrowIfDisposed();

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
            ThrowIfDisposed();

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
        ThrowIfDisposed();

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
        ThrowIfDisposed();

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
        ThrowIfDisposed();

        var textObj = PdfTextObject.Create(_owner.Document, font, fontSize);
        textObj.Text = text;

        // Position the text object
        textObj.SetMatrix(1, 0, 0, 1, x, y);

        // Insert into page
        PDFium.FPDFPage_InsertObject(_page, textObj.Handle);
        RegisterAttachedObject(textObj);

        return textObj;
    }

    /// <summary>
    /// Add an image object to the page
    /// </summary>
    public PdfImageObject AddImage(byte[] imageBytes, float x, float y, float width, float height)
    {
        ThrowIfDisposed();

        var imageObj = PdfImageObject.Create(_owner.Document);
        imageObj.SetImage(imageBytes, _page);
        imageObj.SetPositionAndSize(x, y, width, height);

        // Insert into page
        PDFium.FPDFPage_InsertObject(_page, imageObj.Handle);
        RegisterAttachedObject(imageObj);

        return imageObj;
    }

    /// <summary>
    /// Add a path object to the page
    /// </summary>
    public PdfPathObject AddPath()
    {
        ThrowIfDisposed();

        var pathObj = PdfPathObject.Create(_owner.Document);

        // Insert into page
        PDFium.FPDFPage_InsertObject(_page, pathObj.Handle);
        RegisterAttachedObject(pathObj);

        return pathObj;
    }

    /// <summary>
    /// Add a rectangle to the page
    /// </summary>
    public PdfPathObject AddRectangle(float x, float y, float width, float height, Color? fillColor = null, Color? strokeColor = null)
    {
        ThrowIfDisposed();

        var rectObj = PdfPathObject.CreateRectangle(_owner.Document, x, y, width, height);

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
        RegisterAttachedObject(rectObj);

        return rectObj;
    }

    /// <summary>
    /// Remove a page object from the page
    /// </summary>
    public bool RemoveObject(PdfPageObject pageObject)
    {
        ThrowIfDisposed();

        if (pageObject == null)
            throw new ArgumentNullException(nameof(pageObject));

        var result = PDFium.FPDFPage_RemoveObject(_page, pageObject.Handle);
        if (result)
        {
            pageObject.DetachFromPage();
            UnregisterAttachedObject(pageObject);
        }
        return result;
    }

    /// <summary>
    /// Generate the page content stream (required after adding/modifying objects)
    /// </summary>
    public void GenerateContent()
    {
        ThrowIfDisposed();

        if (!PDFium.FPDFPage_GenerateContent(_page))
            throw new InvalidOperationException("Failed to generate page content");
    }

    /// <summary>
    /// Get the number of objects on the page
    /// </summary>
    public int ObjectCount
    {
        get
        {
            ThrowIfDisposed();
            return PDFium.FPDFPage_CountObjects(_page);
        }
    }

    /// <summary>
    /// Get a page object by index
    /// </summary>
    public IntPtr GetObject(int index)
    {
        ThrowIfDisposed();

        if (index < 0 || index >= ObjectCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return PDFium.FPDFPage_GetObject(_page, index);
    }

    #endregion

    internal void UnregisterAttachedObject(PdfPageObject pageObject)
    {
        lock (_attachedObjectsLock)
        {
            _attachedObjects?.Remove(pageObject);
        }
    }

    private void RegisterAttachedObject(PdfPageObject pageObject)
    {
        pageObject.AttachToPage(this);

        lock (_attachedObjectsLock)
        {
            _attachedObjects ??= new HashSet<PdfPageObject>();
            _attachedObjects.Add(pageObject);
        }
    }

    private PdfPageObject[] DetachAttachedObjects()
    {
        lock (_attachedObjectsLock)
        {
            if (_attachedObjects == null || _attachedObjects.Count == 0)
                return Array.Empty<PdfPageObject>();

            var pageObjects = _attachedObjects.ToArray();
            _attachedObjects.Clear();
            _attachedObjects = null;
            return pageObjects;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_owner.IsDisposed)
            throw new ObjectDisposedException(nameof(PdfDocument), "The owning document has been disposed.");
    }

    /// <summary>
    /// Releases all resources used by the PdfPage.
    /// </summary>
    public void Dispose()
    {
        DisposeInternal(suppressFinalize: true);
    }

    internal void DisposeFromOwner()
    {
        DisposeInternal(suppressFinalize: false);
    }

    private void DisposeInternal(bool suppressFinalize)
    {
        Dispose(true);
        if (suppressFinalize)
        {
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// Releases the unmanaged resources and optionally releases managed resources.
    /// </summary>
    /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;

            foreach (var pageObject in DetachAttachedObjects())
            {
                pageObject.InvalidateFromPageDisposal();
            }

            // Always release native handle
            if (_page != IntPtr.Zero)
            {
                PDFium.FPDF_ClosePage(_page);
                _page = IntPtr.Zero;
            }

            _owner.UnregisterPage(this);
        }
    }

    /// <summary>
    /// Destructor to ensure native resources are released if Dispose is not called.
    /// </summary>
    ~PdfPage()
    {
        Dispose(false);
    }
}
