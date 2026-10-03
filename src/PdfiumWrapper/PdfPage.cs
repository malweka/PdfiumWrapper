using System.Buffers;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace PdfiumWrapper;

/// <summary>
/// Represents a single page in a PDF document
/// </summary>
/// <remarks>
/// Thread safety: operations on different objects may run concurrently; the wrapper serializes
/// native work. Do not use one object from two threads at once.
/// </remarks>
public class PdfPage : IDisposable
{
    // Internal so the owning document's finalizer can hand the handle to the deferred-release queue.
    internal IntPtr _page;
    private readonly PdfDocument _owner;
    private bool _disposed;
    private readonly object _attachedObjectsLock = new();
    // Keyed by native handle so GetObject finds an existing wrapper without a scan.
    private Dictionary<IntPtr, PdfPageObject>? _attachedObjects;

    /// <summary>The native gate must be held.</summary>
    internal PdfPage(PdfDocument owner, int pageIndex)
    {
        PdfiumRuntime.AssertHeld();

        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        PageIndex = pageIndex;
        using (PdfiumDiagnostics.NativeInterval(NativeOp.LoadPage))
            _page = PDFium.FPDF_LoadPage(owner.Document, pageIndex);
        if (_page == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Failed to load page index {pageIndex}.");
        }

        PdfiumRuntime.HandleOpened();

        try
        {
            _owner.RegisterPage(this);
        }
        catch
        {
            PDFium.FPDF_ClosePage(_page);
            PdfiumRuntime.HandleClosed();
            _page = IntPtr.Zero;
            throw;
        }
    }

    [NoNativeCall]
    public int PageIndex { get; }

    public double Width
    {
        get
        {
            using var _ = PdfiumRuntime.Enter();
            return WidthCore;
        }
    }

    public double Height
    {
        get
        {
            using var _ = PdfiumRuntime.Enter();
            return HeightCore;
        }
    }

    internal double WidthCore
    {
        get
        {
            PdfiumRuntime.AssertHeld();
            ThrowIfDisposed();
            return PDFium.FPDF_GetPageWidth(_page);
        }
    }

    internal double HeightCore
    {
        get
        {
            PdfiumRuntime.AssertHeld();
            ThrowIfDisposed();
            return PDFium.FPDF_GetPageHeight(_page);
        }
    }

    internal IntPtr Handle => _page;
    internal IntPtr DocumentHandle => _owner.Document;
    internal PdfDocument Owner => _owner;
    internal bool IsDisposedForChildObjects => _disposed || _owner.IsDisposed;

    /// <summary>
    /// Renders the page to a native PDFium bitmap. Rendering runs inside the gate; the returned
    /// lease's pixel buffer may then be read with the gate free.
    /// </summary>
    internal BitmapLease RenderToBitmapLease(int width, int height, int flags = 0)
    {
        using var _ = PdfiumRuntime.Enter();
        return RenderToBitmapLeaseCore(width, height, flags);
    }

    /// <summary>The native gate must be held.</summary>
    /// <param name="gray">
    /// Render into an 8-bit gray bitmap instead of 32-bit BGRA. Used for TIFF output, which is
    /// bilevel or grayscale anyway: a quarter of the memory and less work to convert. PDFium
    /// anti-aliases text with plain grayscale smoothing at this depth, where the 32-bit path uses
    /// LCD-style smoothing reduced to gray, so glyph edges differ slightly between the two.
    /// </param>
    internal BitmapLease RenderToBitmapLeaseCore(int width, int height, int flags, bool gray = false)
    {
        PdfiumRuntime.AssertHeld();
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        RenderLimits.Check(width, height, PageIndex);

        var bitmap = gray
            ? PDFium.FPDFBitmap_CreateEx(width, height, PDFium.FPDFBitmap_Gray, IntPtr.Zero, 0)
            : PDFium.FPDFBitmap_Create(width, height, 0);
        if (bitmap == IntPtr.Zero)
            throw new InvalidOperationException(
                $"PDFium could not create a {width} x {height} bitmap to render page index {PageIndex}.");

        try
        {
            // Fill with white background
            PDFium.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, new CULong(0xFFFFFFFF));

            using (PdfiumDiagnostics.NativeInterval(NativeOp.Render))
                PDFium.FPDF_RenderPageBitmap(bitmap, _page, 0, 0, width, height, 0, flags);

            // FPDFBitmap_Create with alpha 0 makes a BGRx bitmap.
            return new BitmapLease(bitmap, PDFium.FPDFBitmap_GetBuffer(bitmap), width, height,
                PDFium.FPDFBitmap_GetStride(bitmap), gray ? PDFium.FPDFBitmap_Gray : PDFium.FPDFBitmap_BGRx);
        }
        catch
        {
            PDFium.FPDFBitmap_Destroy(bitmap);
            throw;
        }
    }

    /// <summary>
    /// Renders the page into a <paramref name="width"/> x <paramref name="height"/> BGRx bitmap and
    /// returns its pixels, rows of <c>width * 4</c> bytes.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException">
    /// The bitmap would exceed the render pixel limit (268,435,456 pixels by default; see the
    /// <c>PdfiumWrapper.MaxRenderPixels</c> AppContext data key), or PDFium could not allocate it.
    /// </exception>
    public byte[] RenderToBytes(int width, int height, int flags = 0)
    {
        // Render inside the gate, copy the pixels out with the gate free.
        using var lease = RenderToBitmapLease(width, height, flags);
        return lease.ToArray();
    }

    public string ExtractText()
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();

        var textPage = PDFium.FPDFText_LoadPage(_page);
        if (textPage == IntPtr.Zero)
            return string.Empty;

        try
        {
            var charCount = PDFium.FPDFText_CountChars(textPage);
            if (charCount <= 0)
                return string.Empty;

            var buffer = ArrayPool<char>.Shared.Rent(charCount + 1);
            try
            {
                int actualCount;
                unsafe
                {
                    fixed (char* bufferPtr = buffer)
                    {
                        using (PdfiumDiagnostics.NativeInterval(NativeOp.Text))
                            actualCount = PDFium.FPDFText_GetText(textPage, 0, charCount, (IntPtr)bufferPtr);
                    }
                }

                if (actualCount > 0)
                {
                    return new string(buffer, 0, actualCount - 1);
                }

                return string.Empty;
            }
            finally
            {
                ArrayPool<char>.Shared.Return(buffer);
            }
        }
        finally
        {
            PDFium.FPDFText_ClosePage(textPage);
        }
    }

    /// <summary>
    /// Whether the page has an embedded thumbnail (a <c>/Thumb</c> image with data).
    /// </summary>
    /// <remarks>
    /// This only measures the thumbnail's stream as stored; it neither decompresses nor decodes it,
    /// so a crafted thumbnail cannot make the check expensive. A thumbnail that is present but
    /// damaged reports true here while <see cref="GetEmbeddedThumbnail"/> returns null.
    /// </remarks>
    public bool HasEmbeddedThumbnail
    {
        get
        {
            using var _ = PdfiumRuntime.Enter();
            ThrowIfDisposed();

            return PDFium.FPDFPage_GetRawThumbnailData(_page, IntPtr.Zero, default).Value != 0;
        }
    }

    /// <summary>
    /// Get the page's embedded thumbnail as BGRA pixels, with its size and stride, in one decode.
    /// </summary>
    /// <remarks>
    /// Gray and RGB thumbnails are expanded to BGRA with full opacity, as
    /// <see cref="PdfImageObject.GetBitmap"/> does. The native bitmap is copied and released
    /// before this returns.
    /// </remarks>
    /// <returns>The thumbnail, or null if the page has none or it cannot be decoded.</returns>
    public RawBitmap? GetEmbeddedThumbnail()
    {
        BitmapLease? lease;
        using (PdfiumRuntime.Enter())
        {
            ThrowIfDisposed();
            lease = BitmapLease.Adopt(PDFium.FPDFPage_GetThumbnailAsBitmap(_page));
        }

        // Converted with the gate free; disposing the lease reenters it to destroy the bitmap.
        return BitmapLease.ToBgraAndRelease(lease);
    }

    /// <summary>
    /// Get the embedded thumbnail as BGRA bytes, rows of <c>width * 4</c> bytes.
    /// Returns null if no embedded thumbnail exists.
    /// </summary>
    [Obsolete("Use GetEmbeddedThumbnail(), which returns the BGRA pixels together with their size and stride in one decode.")]
    public byte[]? GetEmbeddedThumbnailBytes()
    {
        return GetEmbeddedThumbnail()?.Pixels;
    }

    /// <summary>
    /// Get the embedded thumbnail dimensions (if it exists)
    /// Returns null if no embedded thumbnail exists
    /// </summary>
    [Obsolete("Use GetEmbeddedThumbnail(), which returns the BGRA pixels together with their size and stride in one decode.")]
    public (int width, int height)? GetEmbeddedThumbnailSize()
    {
        using var _ = PdfiumRuntime.Enter();
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
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();

        var textObj = PdfTextObject.Create(_owner.Document, font, fontSize);
        try
        {
            textObj.Text = text;

            // Position the text object
            textObj.SetMatrix(1, 0, 0, 1, x, y);
        }
        catch
        {
            // Not inserted yet, so nothing else owns it
            textObj.Dispose();
            throw;
        }

        InsertObject(textObj);

        return textObj;
    }

    /// <summary>
    /// Add an image object to the page
    /// </summary>
    public PdfImageObject AddImage(byte[] imageBytes, float x, float y, float width, float height)
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();

        var imageObj = PdfImageObject.Create(_owner.Document);
        try
        {
            imageObj.SetImage(imageBytes, _page);
            imageObj.SetPositionAndSize(x, y, width, height);
        }
        catch
        {
            // Not inserted yet, so nothing else owns it: an undecodable image must not leak the object.
            imageObj.Dispose();
            throw;
        }

        InsertObject(imageObj);

        return imageObj;
    }

    /// <summary>
    /// Add a path object to the page
    /// </summary>
    public PdfPathObject AddPath()
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();

        var pathObj = PdfPathObject.Create(_owner.Document);

        InsertObject(pathObj);

        return pathObj;
    }

    /// <summary>
    /// Add a rectangle to the page
    /// </summary>
    public PdfPathObject AddRectangle(float x, float y, float width, float height, Color? fillColor = null, Color? strokeColor = null)
    {
        using var _ = PdfiumRuntime.Enter();
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

        InsertObject(rectObj);

        return rectObj;
    }

    /// <summary>
    /// Remove a page object from the page. The caller owns the object afterwards: dispose it, or
    /// the document disposes it when the document is disposed.
    /// </summary>
    /// <returns>
    /// False when <paramref name="pageObject"/> is not on this page: an object of another page, a
    /// sub-object of a form object, or one already removed.
    /// </returns>
    /// <exception cref="ObjectDisposedException"><paramref name="pageObject"/> is disposed.</exception>
    public bool RemoveObject(PdfPageObject pageObject)
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();

        if (pageObject == null)
            throw new ArgumentNullException(nameof(pageObject));

        // A disposed wrapper still holds the pointer. Removing through it handed the object to a
        // wrapper whose Dispose does nothing, so the object was never destroyed.
        if (pageObject.IsDisposedForChildObjects)
            throw new ObjectDisposedException(pageObject.GetType().Name);

        // Only the wrapper this page tracks for the object may remove it. Any other wrapper would
        // leave the tracked one in place, pointing at an object the page no longer owns.
        lock (_attachedObjectsLock)
        {
            if (_attachedObjects == null || !_attachedObjects.TryGetValue(pageObject.Handle, out var tracked)
                || !ReferenceEquals(tracked, pageObject))
            {
                return false;
            }
        }

        var result = PDFium.FPDFPage_RemoveObject(_page, pageObject.Handle);
        if (result)
        {
            // The caller owns the native object again. The document tracks it so it is destroyed
            // before the document closes, whether by Dispose or by the finalizer.
            UnregisterAttachedObject(pageObject);
            pageObject.DetachFromPage(_owner);
        }
        return result;
    }

    /// <summary>
    /// Generate the page content stream (required after adding/modifying objects)
    /// </summary>
    public void GenerateContent()
    {
        using var _ = PdfiumRuntime.Enter();
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
            using var _ = PdfiumRuntime.Enter();
            ThrowIfDisposed();
            return PDFium.FPDFPage_CountObjects(_page);
        }
    }

    /// <summary>
    /// Get a page object by index, wrapped in the type that matches its kind
    /// (<see cref="PdfTextObject"/>, <see cref="PdfPathObject"/>, <see cref="PdfImageObject"/>,
    /// <see cref="PdfShadingObject"/> or <see cref="PdfFormObject"/>). The page owns the object:
    /// disposing the wrapper does not delete it, and the wrapper is unusable once the page is disposed.
    /// The same object returns the same wrapper while that wrapper is not disposed.
    /// </summary>
    public PdfPageObject GetObject(int index)
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();

        if (index < 0 || index >= PDFium.FPDFPage_CountObjects(_page))
            throw new ArgumentOutOfRangeException(nameof(index));

        var handle = PDFium.FPDFPage_GetObject(_page, index);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to get object {index} of page index {PageIndex}.");

        // One wrapper per native object: two wrappers would let RemoveObject on one and Dispose
        // destroy the object under the other.
        lock (_attachedObjectsLock)
        {
            if (_attachedObjects != null && _attachedObjects.TryGetValue(handle, out var existing))
                return existing;
        }

        var wrapper = PdfPageObject.WrapExisting(handle, _owner.Document);
        RegisterAttachedObject(wrapper);
        return wrapper;
    }

    #endregion

    internal void UnregisterAttachedObject(PdfPageObject pageObject)
    {
        PdfiumRuntime.AssertHeld();
        lock (_attachedObjectsLock)
        {
            if (_attachedObjects != null && _attachedObjects.TryGetValue(pageObject.Handle, out var tracked)
                && ReferenceEquals(tracked, pageObject))
            {
                _attachedObjects.Remove(pageObject.Handle);
            }
        }
    }

    private void InsertObject(PdfPageObject pageObject)
    {
        // On failure PDFium frees the object, so the wrapper must not destroy it again.
        if (!PDFium.FPDFPage_InsertObject(_page, pageObject.Handle))
        {
            pageObject.InvalidateFreedByPdfium();
            throw new InvalidOperationException($"Failed to insert the {pageObject.GetType().Name} into page index {PageIndex}.");
        }

        RegisterAttachedObject(pageObject);
    }

    private void RegisterAttachedObject(PdfPageObject pageObject)
    {
        PdfiumRuntime.AssertHeld();
        pageObject.AttachToPage(this);

        lock (_attachedObjectsLock)
        {
            _attachedObjects ??= new Dictionary<IntPtr, PdfPageObject>();
            bool added = _attachedObjects.TryAdd(pageObject.Handle, pageObject);
            Debug.Assert(added, "One wrapper per native object: GetObject returns the tracked one.");
        }
    }

    private PdfPageObject[] DetachAttachedObjects()
    {
        PdfiumRuntime.AssertHeld();
        lock (_attachedObjectsLock)
        {
            if (_attachedObjects == null || _attachedObjects.Count == 0)
                return Array.Empty<PdfPageObject>();

            var pageObjects = _attachedObjects.Values.ToArray();
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
        using (PdfiumRuntime.Enter())
        {
            Dispose(true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Dispose from code that already holds the native gate.</summary>
    internal void DisposeCore()
    {
        PdfiumRuntime.AssertHeld();
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the unmanaged resources and optionally releases managed resources.
    /// </summary>
    /// <param name="disposing">
    /// true when called from <see cref="Dispose()"/>: the page is closed now, under the native gate.
    /// false when called from the finalizer: the handle is queued and closed by the next gated operation.
    /// </param>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            // Finalizer thread: never call PDFium, never take a lock, never touch the owner.
            // The owning document's finalizer may already have taken the handle.
            PdfiumRuntime.EnqueueRelease(NativeHandleKind.Page, Interlocked.Exchange(ref _page, IntPtr.Zero));
            return;
        }

        if (_disposed)
            return;

        using var _ = PdfiumRuntime.Enter();
        _disposed = true;

        foreach (var pageObject in DetachAttachedObjects())
        {
            pageObject.InvalidateFromPageDisposal();
        }

        if (_page != IntPtr.Zero)
        {
            using (PdfiumDiagnostics.NativeInterval(NativeOp.Close))
                PDFium.FPDF_ClosePage(_page);
            PdfiumRuntime.HandleClosed();
            _page = IntPtr.Zero;
        }

        _owner.UnregisterPage(this);
    }

    /// <summary>
    /// Queues the native handle for deferred release if Dispose was not called.
    /// </summary>
    ~PdfPage()
    {
        Dispose(false);
    }
}
