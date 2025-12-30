using System.Drawing;

namespace PdfiumWrapper;

/// <summary>
/// Represents a text object in a PDF page
/// </summary>
public class PdfTextObject : PdfPageObject
{
    private IntPtr _font;
    private float _fontSize;

    internal PdfTextObject(IntPtr handle, IntPtr documentHandle, IntPtr font, float fontSize) 
        : base(handle, documentHandle)
    {
        _font = font;
        _fontSize = fontSize;
    }

    /// <summary>
    /// Create a new text object
    /// </summary>
    public static PdfTextObject Create(IntPtr documentHandle, string fontName = "Helvetica", float fontSize = 12)
    {
        var font = PDFium.FPDFText_LoadStandardFont(documentHandle, fontName);
        if (font == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to load font: {fontName}");

        var handle = PDFium.FPDFPageObj_CreateTextObj(documentHandle, font, fontSize);
        if (handle == IntPtr.Zero)
        {
            PDFium.FPDFFont_Close(font);
            throw new InvalidOperationException("Failed to create text object");
        }

        return new PdfTextObject(handle, documentHandle, font, fontSize);
    }

    /// <summary>
    /// Set or get the text content
    /// </summary>
    public string Text
    {
        set
        {
            ThrowIfDisposed();
            if (!PDFium.FPDFText_SetText(Handle, value))
                throw new InvalidOperationException("Failed to set text");
        }
    }

    /// <summary>
    /// Set or get the font name (changes require recreating the text object)
    /// </summary>
    public string Font
    {
        set
        {
            ThrowIfDisposed();
            // Close old font
            if (_font != IntPtr.Zero)
                PDFium.FPDFFont_Close(_font);

            // Load new font
            _font = PDFium.FPDFText_LoadStandardFont(DocumentHandle, value);
            if (_font == IntPtr.Zero)
                throw new InvalidOperationException($"Failed to load font: {value}");
        }
    }

    /// <summary>
    /// Set or get the font size
    /// </summary>
    public float FontSize
    {
        get => _fontSize;
        set
        {
            ThrowIfDisposed();
            _fontSize = value;
            // Note: PDFium doesn't have a direct API to change font size after creation
            // The font size is set during object creation
        }
    }

    /// <summary>
    /// Set the fill color of the text
    /// </summary>
    public Color Color
    {
        set
        {
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
            ThrowIfDisposed();
            PDFium.FPDFPageObj_SetStrokeColor(Handle, value.R, value.G, value.B, value.A);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_font != IntPtr.Zero)
            {
                PDFium.FPDFFont_Close(_font);
                _font = IntPtr.Zero;
            }
        }
        base.Dispose(disposing);
    }
}

