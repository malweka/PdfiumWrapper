namespace PdfiumWrapper;

/// <summary>
/// Represents a shading object in a PDF page
/// </summary>
/// <remarks>
/// Shading objects represent smooth color gradients in PDF documents.
/// Note: PDFium has limited APIs for creating shading objects directly.
/// </remarks>
public class PdfShadingObject : PdfPageObject
{
    internal PdfShadingObject(IntPtr handle, IntPtr documentHandle) 
        : base(handle, documentHandle)
    {
    }

    // Note: PDFium doesn't provide direct APIs for creating shading objects
    // Shading objects are typically read from existing PDFs rather than created programmatically
}

