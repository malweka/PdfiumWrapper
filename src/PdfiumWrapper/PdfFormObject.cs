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
            ThrowIfDisposed();
            return PDFium.FPDFFormObj_CountObjects(Handle);
        }
    }

    /// <summary>
    /// Get a sub-object from this form object by index
    /// </summary>
    public IntPtr GetObject(int index)
    {
        ThrowIfDisposed();
        if (index < 0 || index >= ObjectCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return PDFium.FPDFFormObj_GetObject(Handle, (ulong)index);
    }
}

