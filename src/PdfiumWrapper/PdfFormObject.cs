using System.Runtime.InteropServices;

namespace PdfiumWrapper;

/// <summary>
/// Represents a form XObject in a PDF page
/// </summary>
public class PdfFormObject : PdfPageObject
{
    internal PdfFormObject(IntPtr handle, IntPtr documentHandle) 
        : base(handle, documentHandle)
    {
    }

    /// <summary>
    /// Get the number of sub-objects in this form object
    /// </summary>
    public int ObjectCount
    {
        get
        {
            using var _ = PdfiumRuntime.Enter();
            ThrowIfDisposed();
            return PDFium.FPDFFormObj_CountObjects(Handle);
        }
    }

    /// <summary>
    /// Get a sub-object from this form object by index, wrapped in the type that matches its kind.
    /// The form object owns it: disposing the wrapper does not delete it, and the wrapper is
    /// unusable once this form object is disposed.
    /// </summary>
    public PdfPageObject GetObject(int index)
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();
        if (index < 0 || index >= PDFium.FPDFFormObj_CountObjects(Handle))
            throw new ArgumentOutOfRangeException(nameof(index));

        var handle = PDFium.FPDFFormObj_GetObject(Handle, new CULong((uint)index));
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to get sub-object {index} of the form object.");

        var wrapper = WrapExisting(handle, DocumentHandle);
        wrapper.AttachToForm(this);
        return wrapper;
    }
}

