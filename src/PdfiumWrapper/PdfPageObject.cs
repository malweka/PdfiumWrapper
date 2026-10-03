
namespace PdfiumWrapper;

/// <summary>
/// Base class for PDF page objects
/// </summary>
/// <remarks>
/// Thread safety: operations on different objects may run concurrently; the wrapper serializes
/// native work. Do not use one object from two threads at once.
/// </remarks>
public abstract class PdfPageObject : IDisposable
{
    private bool _disposed;
    private PdfPage? _ownerPage;
    private PdfDocument? _ownerDocument;
    private PdfFormObject? _parentForm;
    private IntPtr _handle;

    internal IntPtr Handle => _handle;
    internal IntPtr DocumentHandle { get; private set; }
    internal bool IsAttachedToPage { get; set; }

    /// <summary>
    /// Wraps a native page object the caller owns (not attached to a page). The native gate must be held.
    /// </summary>
    private protected PdfPageObject(IntPtr handle, IntPtr documentHandle)
    {
        _handle = handle;
        DocumentHandle = documentHandle;
        IsAttachedToPage = false;
        if (handle != IntPtr.Zero)
            PdfiumRuntime.HandleOpened();
    }

    /// <summary>
    /// Wraps an existing native object that a page or form object owns, in the wrapper type that
    /// matches its kind. The caller attaches the result. The native gate must be held.
    /// </summary>
    internal static PdfPageObject WrapExisting(IntPtr handle, IntPtr documentHandle)
    {
        PdfiumRuntime.AssertHeld();
        return PDFium.FPDFPageObj_GetType(handle) switch
        {
            PDFium.FPDF_PAGEOBJ_TEXT => new PdfTextObject(handle, documentHandle),
            PDFium.FPDF_PAGEOBJ_PATH => new PdfPathObject(handle, documentHandle),
            PDFium.FPDF_PAGEOBJ_IMAGE => new PdfImageObject(handle, documentHandle),
            PDFium.FPDF_PAGEOBJ_SHADING => new PdfShadingObject(handle, documentHandle),
            PDFium.FPDF_PAGEOBJ_FORM => new PdfFormObject(handle, documentHandle),
            var type => throw new InvalidOperationException($"PDFium reported an unknown page object type {type}."),
        };
    }

    /// <summary>
    /// Get the type of this page object
    /// </summary>
    public int ObjectType
    {
        get
        {
            using var _ = PdfiumRuntime.Enter();
            ThrowIfDisposed();
            return PDFium.FPDFPageObj_GetType(Handle);
        }
    }

    /// <summary>
    /// Get the bounds of this page object
    /// </summary>
    public (float left, float bottom, float right, float top) GetBounds()
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();
        PDFium.FPDFPageObj_GetBounds(Handle, out float left, out float bottom, out float right, out float top);
        return (left, bottom, right, top);
    }

    /// <summary>
    /// Check if this page object has transparency
    /// </summary>
    public bool HasTransparency
    {
        get
        {
            using var _ = PdfiumRuntime.Enter();
            ThrowIfDisposed();
            return PDFium.FPDFPageObj_HasTransparency(Handle);
        }
    }

    /// <summary>
    /// Transform this page object with a matrix
    /// </summary>
    public void Transform(double a, double b, double c, double d, double e, double f)
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();
        PDFium.FPDFPageObj_Transform(Handle, a, b, c, d, e, f);
    }

    /// <summary>
    /// Set the matrix of this page object
    /// </summary>
    public void SetMatrix(double a, double b, double c, double d, double e, double f)
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();
        var matrix = new PDFium.Matrix { A = (float)a, B = (float)b, C = (float)c, D = (float)d, E = (float)e, F = (float)f };
        PDFium.FPDFPageObj_SetMatrix(Handle, ref matrix);
    }

    /// <summary>
    /// Get the matrix of this page object
    /// </summary>
    public (double a, double b, double c, double d, double e, double f) GetMatrix()
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();
        // FPDFPageObj_GetMatrix fills one FS_MATRIX (six floats), not six separate values.
        PDFium.FPDFPageObj_GetMatrix(Handle, out PDFium.Matrix matrix);
        return (matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F);
    }

    /// <summary>True once this object, or whatever owns its native object, is disposed.</summary>
    internal bool IsDisposedForChildObjects =>
        _disposed
        || _ownerPage?.IsDisposedForChildObjects == true
        || _ownerDocument?.IsDisposed == true
        || _parentForm?.IsDisposedForChildObjects == true;

    protected void ThrowIfDisposed()
    {
        if (IsDisposedForChildObjects)
            throw new ObjectDisposedException(GetType().Name);
    }

    /// <summary>The page now owns the native object. The native gate must be held.</summary>
    internal void AttachToPage(PdfPage ownerPage)
    {
        PdfiumRuntime.AssertHeld();
        _ownerDocument?.UnregisterDetachedObject(this);
        _ownerDocument = null;
        _ownerPage = ownerPage;
        if (!IsAttachedToPage && _handle != IntPtr.Zero)
            PdfiumRuntime.HandleClosed();
        IsAttachedToPage = true;
    }

    /// <summary>
    /// The native object belongs to <paramref name="parent"/>'s form XObject and lives as long as
    /// it does; this wrapper never destroys it. The native gate must be held.
    /// </summary>
    internal void AttachToForm(PdfFormObject parent)
    {
        PdfiumRuntime.AssertHeld();
        _parentForm = parent;
        if (!IsAttachedToPage && _handle != IntPtr.Zero)
            PdfiumRuntime.HandleClosed();
        IsAttachedToPage = true;
    }

    /// <summary>
    /// The caller owns the native object again after removal from its page. The document tracks it
    /// so it is destroyed before the document closes. The native gate must be held.
    /// </summary>
    internal void DetachFromPage(PdfDocument ownerDocument)
    {
        PdfiumRuntime.AssertHeld();
        _ownerPage = null;
        if (IsAttachedToPage && _handle != IntPtr.Zero)
            PdfiumRuntime.HandleOpened();
        IsAttachedToPage = false;
        _ownerDocument = ownerDocument;
        ownerDocument.RegisterDetachedObject(this);
    }

    internal void ReleasePageTracking()
    {
        _ownerPage?.UnregisterAttachedObject(this);
        _ownerPage = null;
    }

    /// <summary>
    /// PDFium freed the native object itself (<c>FPDFPage_InsertObject</c> does when it fails),
    /// so the wrapper must not destroy it again. The native gate must be held.
    /// </summary>
    internal void InvalidateFreedByPdfium()
    {
        PdfiumRuntime.AssertHeld();
        ReleasePageTracking();
        _ownerDocument?.UnregisterDetachedObject(this);
        _ownerDocument = null;
        if (!IsAttachedToPage && _handle != IntPtr.Zero)
            PdfiumRuntime.HandleClosed();
        _handle = IntPtr.Zero;
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>The owning page closed and took the native object with it.</summary>
    internal void InvalidateFromPageDisposal()
    {
        _ownerPage = null;
        _handle = IntPtr.Zero;
        _disposed = true;
    }

    /// <summary>
    /// Called by the owning document's finalizer: hand over the native handle of a detached object
    /// so the document can queue it for release ahead of itself. No locks, no PDFium.
    /// </summary>
    internal IntPtr TakeHandleForOwnerFinalizer()
        => IsAttachedToPage ? IntPtr.Zero : Interlocked.Exchange(ref _handle, IntPtr.Zero);

    /// <summary>The owning document is being disposed. The native gate must be held.</summary>
    internal void DisposeFromOwner()
    {
        PdfiumRuntime.AssertHeld();
        _ownerDocument = null;
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        using (PdfiumRuntime.Enter())
        {
            Dispose(true);
        }

        GC.SuppressFinalize(this);
    }

    /// <param name="disposing">
    /// true when called from <see cref="Dispose()"/>: a detached object is destroyed now, under the native gate.
    /// false when called from the finalizer: the handle is queued and destroyed by the next gated operation.
    /// </param>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            // Finalizer thread: never call PDFium, never take a lock.
            // An attached object belongs to its page and is released with it.
            PdfiumRuntime.EnqueueRelease(NativeHandleKind.PageObject, TakeHandleForOwnerFinalizer());
            return;
        }

        if (_disposed)
            return;

        using var _ = PdfiumRuntime.Enter();
        ReleasePageTracking();

        // Only destroy if not attached to a page
        if (_handle != IntPtr.Zero && !IsAttachedToPage)
        {
            PDFium.FPDFPageObj_Destroy(_handle);
            PdfiumRuntime.HandleClosed();
            _handle = IntPtr.Zero;
        }

        _ownerDocument?.UnregisterDetachedObject(this);
        _ownerDocument = null;
        _disposed = true;
    }

    ~PdfPageObject()
    {
        Dispose(false);
    }
}
