using System.Drawing;

namespace PdfiumWrapper;

/// <summary>
/// Represents a text object in a PDF page
/// </summary>
public class PdfTextObject : PdfPageObject
{
    internal PdfTextObject(IntPtr handle, IntPtr documentHandle)
        : base(handle, documentHandle)
    {
    }

    /// <summary>
    /// Create a new text object
    /// </summary>
    internal static PdfTextObject Create(IntPtr documentHandle, string fontName = "Helvetica", float fontSize = 12)
    {
        using var _ = PdfiumRuntime.Enter();

        var font = PDFium.FPDFText_LoadStandardFont(documentHandle, fontName);
        if (font == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to load font: {fontName}");

        try
        {
            var handle = PDFium.FPDFPageObj_CreateTextObj(documentHandle, font, fontSize);
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException("Failed to create text object");

            return new PdfTextObject(handle, documentHandle);
        }
        finally
        {
            // The text object keeps its own reference to the font. Holding ours until the object
            // was disposed leaked the font whenever the page or document was disposed first.
            PDFium.FPDFFont_Close(font);
        }
    }

    /// <summary>
    /// Set the text content
    /// </summary>
    public string Text
    {
        set
        {
            using var _ = PdfiumRuntime.Enter();
            ThrowIfDisposed();
            if (!PDFium.FPDFText_SetText(Handle, value))
                throw new InvalidOperationException("Failed to set text");
        }
    }

    /// <summary>
    /// Get or set the font size, in points. The font itself is chosen when the text is added
    /// (<see cref="PdfPage.AddText"/>); PDFium cannot change it afterwards.
    /// </summary>
    public float FontSize
    {
        get
        {
            using var _ = PdfiumRuntime.Enter();
            ThrowIfDisposed();
            if (!PDFium.FPDFTextObj_GetFontSize(Handle, out float size))
                throw new InvalidOperationException("Failed to get font size");
            return size;
        }
        set
        {
            using var _ = PdfiumRuntime.Enter();
            ThrowIfDisposed();
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            if (!PDFium.FPDFTextObj_SetFontSize(Handle, value))
                throw new InvalidOperationException("Failed to set font size");
        }
    }

    /// <summary>
    /// Set the fill color of the text
    /// </summary>
    public Color Color
    {
        set
        {
            using var _ = PdfiumRuntime.Enter();
            ThrowIfDisposed();
            PDFium.FPDFPageObj_SetFillColor(Handle, value.R, value.G, value.B, value.A);
        }
    }

    /// <summary>
    /// Set the stroke color of the text
    /// </summary>
    public Color StrokeColor
    {
        set
        {
            using var _ = PdfiumRuntime.Enter();
            ThrowIfDisposed();
            PDFium.FPDFPageObj_SetStrokeColor(Handle, value.R, value.G, value.B, value.A);
        }
    }
}
