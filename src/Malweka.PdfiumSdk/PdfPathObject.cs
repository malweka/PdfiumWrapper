using System.Drawing;

namespace Malweka.PdfiumSdk;

/// <summary>
/// Represents a path object (shapes, lines) in a PDF page
/// </summary>
public class PdfPathObject : PdfPageObject
{
    internal PdfPathObject(IntPtr handle, IntPtr documentHandle) 
        : base(handle, documentHandle)
    {
    }

    /// <summary>
    /// Create a new path object starting at the specified position
    /// </summary>
    public static PdfPathObject Create(IntPtr documentHandle, float x = 0, float y = 0)
    {
        var handle = PDFium.FPDFPageObj_CreateNewPath(x, y);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create path object");

        return new PdfPathObject(handle, documentHandle);
    }

    /// <summary>
    /// Create a new rectangle path object
    /// </summary>
    public static PdfPathObject CreateRectangle(IntPtr documentHandle, float x, float y, float width, float height)
    {
        var handle = PDFium.FPDFPageObj_CreateNewRect(x, y, width, height);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create rectangle object");

        return new PdfPathObject(handle, documentHandle);
    }

    /// <summary>
    /// Move the path's current point to the specified position
    /// </summary>
    public PdfPathObject MoveTo(float x, float y)
    {
        ThrowIfDisposed();
        if (!PDFium.FPDFPath_MoveTo(Handle, x, y))
            throw new InvalidOperationException("Failed to move path");
        return this;
    }

    /// <summary>
    /// Add a line from the current point to the specified position
    /// </summary>
    public PdfPathObject LineTo(float x, float y)
    {
        ThrowIfDisposed();
        if (!PDFium.FPDFPath_LineTo(Handle, x, y))
            throw new InvalidOperationException("Failed to add line to path");
        return this;
    }

    /// <summary>
    /// Add a cubic Bézier curve to the path
    /// </summary>
    public PdfPathObject BezierTo(float x1, float y1, float x2, float y2, float x3, float y3)
    {
        ThrowIfDisposed();
        if (!PDFium.FPDFPath_BezierTo(Handle, x1, y1, x2, y2, x3, y3))
            throw new InvalidOperationException("Failed to add bezier curve to path");
        return this;
    }

    /// <summary>
    /// Close the current path
    /// </summary>
    public PdfPathObject Close()
    {
        ThrowIfDisposed();
        if (!PDFium.FPDFPath_Close(Handle))
            throw new InvalidOperationException("Failed to close path");
        return this;
    }

    /// <summary>
    /// Set the fill color of the path
    /// </summary>
    public Color FillColor
    {
        set
        {
            ThrowIfDisposed();
            if (!PDFium.FPDFPageObj_SetFillColor(Handle, value.R, value.G, value.B, value.A))
                throw new InvalidOperationException("Failed to set fill color");
        }
    }

    /// <summary>
    /// Set the stroke color of the path
    /// </summary>
    public Color StrokeColor
    {
        set
        {
            ThrowIfDisposed();
            if (!PDFium.FPDFPageObj_SetStrokeColor(Handle, value.R, value.G, value.B, value.A))
                throw new InvalidOperationException("Failed to set stroke color");
        }
    }

    /// <summary>
    /// Set the stroke width
    /// </summary>
    public float StrokeWidth
    {
        get
        {
            ThrowIfDisposed();
            PDFium.FPDFPageObj_GetStrokeWidth(Handle, out float width);
            return width;
        }
        set
        {
            ThrowIfDisposed();
            if (!PDFium.FPDFPageObj_SetStrokeWidth(Handle, value))
                throw new InvalidOperationException("Failed to set stroke width");
        }
    }

    /// <summary>
    /// Set the drawing mode (fill and/or stroke)
    /// </summary>
    public void SetDrawMode(PdfPathFillMode fillMode, bool stroke)
    {
        ThrowIfDisposed();
        if (!PDFium.FPDFPath_SetDrawMode(Handle, (int)fillMode, stroke ? 1 : 0))
            throw new InvalidOperationException("Failed to set draw mode");
    }

    /// <summary>
    /// Set the line join style
    /// </summary>
    public PdfLineJoinStyle LineJoin
    {
        set
        {
            ThrowIfDisposed();
            if (!PDFium.FPDFPageObj_SetLineJoin(Handle, (int)value))
                throw new InvalidOperationException("Failed to set line join");
        }
    }

    /// <summary>
    /// Set the line cap style
    /// </summary>
    public PdfLineCapStyle LineCap
    {
        set
        {
            ThrowIfDisposed();
            if (!PDFium.FPDFPageObj_SetLineCap(Handle, (int)value))
                throw new InvalidOperationException("Failed to set line cap");
        }
    }

    /// <summary>
    /// Get the number of segments in the path
    /// </summary>
    public int SegmentCount
    {
        get
        {
            ThrowIfDisposed();
            return PDFium.FPDFPath_CountSegments(Handle);
        }
    }
}

/// <summary>
/// Fill mode for paths
/// </summary>
public enum PdfPathFillMode
{
    None = PDFium.FPDF_FILLMODE_NONE,
    Alternate = PDFium.FPDF_FILLMODE_ALTERNATE,
    Winding = PDFium.FPDF_FILLMODE_WINDING
}

/// <summary>
/// Line join style
/// </summary>
public enum PdfLineJoinStyle
{
    Miter = PDFium.FPDF_LINEJOIN_MITER,
    Round = PDFium.FPDF_LINEJOIN_ROUND,
    Bevel = PDFium.FPDF_LINEJOIN_BEVEL
}

/// <summary>
/// Line cap style
/// </summary>
public enum PdfLineCapStyle
{
    Butt = PDFium.FPDF_LINECAP_BUTT,
    Round = PDFium.FPDF_LINECAP_ROUND,
    Square = PDFium.FPDF_LINECAP_SQUARE
}

