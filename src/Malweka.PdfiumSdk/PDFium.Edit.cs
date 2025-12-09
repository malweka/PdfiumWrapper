using System.Runtime.InteropServices;

namespace Malweka.PdfiumSdk;

/// <summary>
/// PDFium Edit APIs from fpdf_edit.h
/// For creating and editing PDF page content
/// </summary>
public static partial class PDFium
{
    #region Page Creation and Manipulation

    /// <summary>
    /// Create a new page in the document
    /// </summary>
    /// <param name="document">Document handle</param>
    /// <param name="page_index">Index where to insert the page (0-based)</param>
    /// <param name="width">Page width in points</param>
    /// <param name="height">Page height in points</param>
    /// <returns>Page handle, or null on failure</returns>
    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFPage_New(IntPtr document, int page_index, double width, double height);

    /// <summary>
    /// Delete a page from the document
    /// </summary>
    /// <param name="document">Document handle</param>
    /// <param name="page_index">0-based page index to delete</param>
    [LibraryImport(LibraryName)]
    public static partial void FPDFPage_Delete(IntPtr document, int page_index);

    /// <summary>
    /// Set the page rotation
    /// </summary>
    /// <param name="page">Page handle</param>
    /// <param name="rotate">Rotation in degrees (0, 90, 180, 270)</param>
    [LibraryImport(LibraryName)]
    public static partial void FPDFPage_SetRotation(IntPtr page, int rotate);

    /// <summary>
    /// Get the page rotation
    /// </summary>
    /// <param name="page">Page handle</param>
    /// <returns>Rotation in degrees (0, 90, 180, 270)</returns>
    [LibraryImport(LibraryName)]
    public static partial int FPDFPage_GetRotation(IntPtr page);

    /// <summary>
    /// Get the number of page objects in a page
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int FPDFPage_CountObjects(IntPtr page);

    /// <summary>
    /// Get a page object by index
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFPage_GetObject(IntPtr page, int index);

    /// <summary>
    /// Insert page object into page
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial void FPDFPage_InsertObject(IntPtr page, IntPtr page_obj);

    /// <summary>
    /// Remove page object from page
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPage_RemoveObject(IntPtr page, IntPtr page_obj);

    /// <summary>
    /// Generate content for page (must be called after modifying page objects)
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPage_GenerateContent(IntPtr page);

    /// <summary>
    /// Set media box for a page
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPage_SetMediaBox(IntPtr page, float left, float bottom, float right, float top);

    /// <summary>
    /// Set crop box for a page
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPage_SetCropBox(IntPtr page, float left, float bottom, float right, float top);

    /// <summary>
    /// Get media box for a page
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPage_GetMediaBox(IntPtr page, out float left, out float bottom, out float right, out float top);

    /// <summary>
    /// Get crop box for a page
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPage_GetCropBox(IntPtr page, out float left, out float bottom, out float right, out float top);

    /// <summary>
    /// Transform the page content with a matrix
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPage_TransFormWithClip(IntPtr page, ref Matrix matrix, ref RectF clipRect);

    #endregion

    #region Page Object Creation

    /// <summary>
    /// Create a new path object
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFPageObj_CreateNewPath(float x, float y);

    /// <summary>
    /// Create a new rectangle object
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFPageObj_CreateNewRect(float x, float y, float w, float h);

    /// <summary>
    /// Create a new text object
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFPageObj_CreateTextObj(IntPtr document, IntPtr font, float font_size);

    /// <summary>
    /// Create a new image object
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFPageObj_NewImageObj(IntPtr document);

    #endregion

    #region Page Object Properties

    /// <summary>
    /// Get the type of a page object
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int FPDFPageObj_GetType(IntPtr page_obj);

    /// <summary>
    /// Destroy a page object (must be removed from page first)
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial void FPDFPageObj_Destroy(IntPtr page_obj);

    /// <summary>
    /// Check if the page object has transparency
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPageObj_HasTransparency(IntPtr page_obj);

    /// <summary>
    /// Get the bounds of a page object
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPageObj_GetBounds(IntPtr page_obj, out float left, out float bottom, out float right, out float top);

    /// <summary>
    /// Get the matrix of a page object
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPageObj_GetMatrix(IntPtr page_obj, out double a, out double b, out double c, out double d, out double e, out double f);

    /// <summary>
    /// Set the matrix of a page object
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPageObj_SetMatrix(IntPtr page_obj, ref Matrix matrix);

    /// <summary>
    /// Transform page object with matrix
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial void FPDFPageObj_Transform(IntPtr page_obj, double a, double b, double c, double d, double e, double f);

    #endregion

    #region Page Object Colors

    /// <summary>
    /// Set the stroke color of a page object
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPageObj_SetStrokeColor(IntPtr page_obj, uint R, uint G, uint B, uint A);

    /// <summary>
    /// Get the stroke color of a page object
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPageObj_GetStrokeColor(IntPtr page_obj, out uint R, out uint G, out uint B, out uint A);

    /// <summary>
    /// Set the fill color of a page object
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPageObj_SetFillColor(IntPtr page_obj, uint R, uint G, uint B, uint A);

    /// <summary>
    /// Get the fill color of a page object
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPageObj_GetFillColor(IntPtr page_obj, out uint R, out uint G, out uint B, out uint A);

    /// <summary>
    /// Get the stroke width of a page object
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPageObj_GetStrokeWidth(IntPtr page_obj, out float width);

    /// <summary>
    /// Set the stroke width of a page object
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPageObj_SetStrokeWidth(IntPtr page_obj, float width);

    /// <summary>
    /// Get the line join style of a page object
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPageObj_GetLineJoin(IntPtr page_obj, out int line_join);

    /// <summary>
    /// Set the line join style of a page object
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPageObj_SetLineJoin(IntPtr page_obj, int line_join);

    /// <summary>
    /// Get the line cap style of a page object
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPageObj_GetLineCap(IntPtr page_obj, out int line_cap);

    /// <summary>
    /// Set the line cap style of a page object
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPageObj_SetLineCap(IntPtr page_obj, int line_cap);

    #endregion

    #region Path Operations

    /// <summary>
    /// Move the path's current point
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPath_MoveTo(IntPtr path, float x, float y);

    /// <summary>
    /// Add a line segment to the path
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPath_LineTo(IntPtr path, float x, float y);

    /// <summary>
    /// Add a cubic Bézier curve to the path
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPath_BezierTo(IntPtr path, float x1, float y1, float x2, float y2, float x3, float y3);

    /// <summary>
    /// Close the current path
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPath_Close(IntPtr path);

    /// <summary>
    /// Set the drawing mode of a path
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPath_SetDrawMode(IntPtr path, int fillmode, int stroke);

    /// <summary>
    /// Get the number of segments in a path
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int FPDFPath_CountSegments(IntPtr path);

    /// <summary>
    /// Get a segment from a path
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFPath_GetPathSegment(IntPtr path, int index);

    /// <summary>
    /// Get the type of a path segment
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int FPDFPathSegment_GetType(IntPtr segment);

    /// <summary>
    /// Get the coordinates of a path segment
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPathSegment_GetPoint(IntPtr segment, out float x, out float y);

    /// <summary>
    /// Check if a path segment closes the path
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPathSegment_GetClose(IntPtr segment);

    #endregion

    #region Text Operations

    /// <summary>
    /// Set the text for a text object
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFText_SetText(IntPtr text_object, [MarshalAs(UnmanagedType.LPWStr)] string text);

    /// <summary>
    /// Set the position of a text object
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFText_SetCharcodes(IntPtr text_object, IntPtr charcodes, ulong count);

    /// <summary>
    /// Load a standard font
    /// </summary>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr FPDFText_LoadStandardFont(IntPtr document, string font);

    /// <summary>
    /// Load a Type1 font from data
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFText_LoadFont(IntPtr document, IntPtr data, uint size, int font_type, [MarshalAs(UnmanagedType.Bool)] bool cid);

    /// <summary>
    /// Close a font
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial void FPDFFont_Close(IntPtr font);

    /// <summary>
    /// Get the font name
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial ulong FPDFFont_GetFontName(IntPtr font, IntPtr buffer, ulong length);

    /// <summary>
    /// Get the flags of a font
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int FPDFFont_GetFlags(IntPtr font);

    /// <summary>
    /// Get the weight of a font
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int FPDFFont_GetWeight(IntPtr font);

    /// <summary>
    /// Get the italic angle of a font
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFFont_GetItalicAngle(IntPtr font, out int angle);

    /// <summary>
    /// Get the ascent of a font
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFFont_GetAscent(IntPtr font, float font_size, out float ascent);

    /// <summary>
    /// Get the descent of a font
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFFont_GetDescent(IntPtr font, float font_size, out float descent);

    /// <summary>
    /// Get the width of a glyph
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFFont_GetGlyphWidth(IntPtr font, uint glyph, float font_size, out float width);

    /// <summary>
    /// Get the glyph path
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFFont_GetGlyphPath(IntPtr font, uint glyph, float font_size);

    /// <summary>
    /// Get the font data
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFFont_GetFontData(IntPtr font, IntPtr buffer, ulong buflen, out ulong out_buflen);

    /// <summary>
    /// Check if a font is embedded
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int FPDFFont_GetIsEmbedded(IntPtr font);

    #endregion

    #region Image Operations

    /// <summary>
    /// Load an image from a JPEG buffer
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFImageObj_LoadJpegFile(IntPtr pages, int count, IntPtr image_object, IntPtr fileaccess);

    /// <summary>
    /// Load an image from a JPEG buffer (inline)
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFImageObj_LoadJpegFileInline(IntPtr pages, int count, IntPtr image_object, IntPtr fileaccess);

    /// <summary>
    /// Set the matrix of an image object
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFImageObj_SetMatrix(IntPtr image_object, double a, double b, double c, double d, double e, double f);

    /// <summary>
    /// Set bitmap for an image object
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFImageObj_SetBitmap(IntPtr pages, int count, IntPtr image_object, IntPtr bitmap);

    /// <summary>
    /// Get the bitmap from an image object
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFImageObj_GetBitmap(IntPtr image_object);

    /// <summary>
    /// Get the rendered bitmap of an image object
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFImageObj_GetRenderedBitmap(IntPtr document, IntPtr page, IntPtr image_object);

    /// <summary>
    /// Get the image filter count
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int FPDFImageObj_GetImageFilterCount(IntPtr image_object);

    /// <summary>
    /// Get an image filter name
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial ulong FPDFImageObj_GetImageFilter(IntPtr image_object, int index, IntPtr buffer, ulong buflen);

    /// <summary>
    /// Get image metadata
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFImageObj_GetImageMetadata(IntPtr image_object, IntPtr page, out FPDF_IMAGEOBJ_METADATA metadata);

    #endregion

    #region Form Objects

    /// <summary>
    /// Get the number of sub-objects in a form object
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int FPDFFormObj_CountObjects(IntPtr form_object);

    /// <summary>
    /// Get a sub-object from a form object
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFFormObj_GetObject(IntPtr form_object, ulong index);

    #endregion

    #region Constants

    // Object types
    public const int FPDF_PAGEOBJ_UNKNOWN = 0;
    public const int FPDF_PAGEOBJ_TEXT = 1;
    public const int FPDF_PAGEOBJ_PATH = 2;
    public const int FPDF_PAGEOBJ_IMAGE = 3;
    public const int FPDF_PAGEOBJ_SHADING = 4;
    public const int FPDF_PAGEOBJ_FORM = 5;

    // Path segment types
    public const int FPDF_SEGMENT_UNKNOWN = -1;
    public const int FPDF_SEGMENT_LINETO = 0;
    public const int FPDF_SEGMENT_BEZIERTO = 1;
    public const int FPDF_SEGMENT_MOVETO = 2;

    // Fill modes
    public const int FPDF_FILLMODE_NONE = 0;
    public const int FPDF_FILLMODE_ALTERNATE = 1;
    public const int FPDF_FILLMODE_WINDING = 2;

    // Line join styles
    public const int FPDF_LINEJOIN_MITER = 0;
    public const int FPDF_LINEJOIN_ROUND = 1;
    public const int FPDF_LINEJOIN_BEVEL = 2;

    // Line cap styles
    public const int FPDF_LINECAP_BUTT = 0;
    public const int FPDF_LINECAP_ROUND = 1;
    public const int FPDF_LINECAP_SQUARE = 2;

    // Font types
    public const int FPDF_FONT_TYPE1 = 1;
    public const int FPDF_FONT_TRUETYPE = 2;

    #endregion

    #region Structures

    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_IMAGEOBJ_METADATA
    {
        public uint width;
        public uint height;
        public uint horizontal_dpi;
        public uint vertical_dpi;
        public uint bits_per_pixel;
        public int colorspace;
        public int marked_content_id;
    }

    #endregion
}

