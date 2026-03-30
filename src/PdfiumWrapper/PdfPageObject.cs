
namespace PdfiumWrapper;

/// <summary>
/// Base class for PDF page objects
/// </summary>
public abstract class PdfPageObject : IDisposable
{
    private bool _disposed;
    private PdfPage? _ownerPage;
    protected internal IntPtr Handle { get; private set; }
    protected internal IntPtr DocumentHandle { get; private set; }
    internal bool IsAttachedToPage { get; set; }

    protected PdfPageObject(IntPtr handle, IntPtr documentHandle)
    {
        Handle = handle;
        DocumentHandle = documentHandle;
        IsAttachedToPage = false;
    }

    /// <summary>
    /// Get the type of this page object
    /// </summary>
    public int ObjectType
    {
        get
        {
            ThrowIfDisposed();
            return PDFium.FPDFPageObj_GetType(Handle);
        }
    }

    /// <summary>
    /// Get the bounds of this page object
    /// </summary>
    public (float left, float bottom, float right, float top) GetBounds()
    {
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
            ThrowIfDisposed();
            return PDFium.FPDFPageObj_HasTransparency(Handle);
        }
    }

    /// <summary>
    /// Transform this page object with a matrix
    /// </summary>
    public void Transform(double a, double b, double c, double d, double e, double f)
    {
        ThrowIfDisposed();
        PDFium.FPDFPageObj_Transform(Handle, a, b, c, d, e, f);
    }

    /// <summary>
    /// Set the matrix of this page object
    /// </summary>
    public void SetMatrix(double a, double b, double c, double d, double e, double f)
    {
        ThrowIfDisposed();
        var matrix = new PDFium.Matrix { A = (float)a, B = (float)b, C = (float)c, D = (float)d, E = (float)e, F = (float)f };
        PDFium.FPDFPageObj_SetMatrix(Handle, ref matrix);
    }

    /// <summary>
    /// Get the matrix of this page object
    /// </summary>
    public (double a, double b, double c, double d, double e, double f) GetMatrix()
    {
        ThrowIfDisposed();
        PDFium.FPDFPageObj_GetMatrix(Handle, out double a, out double b, out double c, out double d, out double e, out double f);
        return (a, b, c, d, e, f);
    }

    protected void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(GetType().Name);
        if (_ownerPage?.IsDisposedForChildObjects == true)
            throw new ObjectDisposedException(GetType().Name);
    }

    internal void AttachToPage(PdfPage ownerPage)
    {
        _ownerPage = ownerPage;
        IsAttachedToPage = true;
    }

    internal void DetachFromPage()
    {
        _ownerPage = null;
        IsAttachedToPage = false;
    }

    internal void ReleasePageTracking()
    {
        _ownerPage?.UnregisterAttachedObject(this);
        _ownerPage = null;
    }

    internal void InvalidateFromPageDisposal()
    {
        _ownerPage = null;
        Handle = IntPtr.Zero;
        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            ReleasePageTracking();

            // Only destroy if not attached to a page
            if (Handle != IntPtr.Zero && !IsAttachedToPage)
            {
                PDFium.FPDFPageObj_Destroy(Handle);
                Handle = IntPtr.Zero;
            }

            _disposed = true;
        }
    }

    ~PdfPageObject()
    {
        Dispose(false);
    }
}
