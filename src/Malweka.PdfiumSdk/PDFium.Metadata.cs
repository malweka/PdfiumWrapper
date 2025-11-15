using System.Runtime.InteropServices;

namespace Malweka.PdfiumSdk;

/// <summary>
/// PDFium Metadata and Document Properties APIs from fpdf_doc.h and fpdf_ext.h
/// </summary>
public static partial class PDFium
{
    #region Document Metadata (Info Dictionary)

    /// <summary>
    /// Get document metadata value by tag
    /// </summary>
    /// <param name="document">Document handle</param>
    /// <param name="tag">Metadata tag (e.g., "Title", "Author", "Subject", "Keywords", "Creator", "Producer", "CreationDate", "ModDate")</param>
    /// <param name="buffer">Buffer to receive the value (UTF-16LE)</param>
    /// <param name="buflen">Length of buffer in bytes</param>
    /// <returns>Number of bytes in the value, including the terminating null character</returns>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial ulong FPDF_GetMetaText(IntPtr document, string tag, IntPtr buffer, ulong buflen);

    #endregion

    #region File Version

    /// <summary>
    /// Get the file version of the PDF document
    /// </summary>
    /// <param name="doc">Document handle</param>
    /// <param name="fileVersion">Pointer to receive file version (e.g., 14 for PDF 1.4, 17 for PDF 1.7)</param>
    /// <returns>True on success, false on failure</returns>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDF_GetFileVersion(IntPtr doc, out int fileVersion);

    #endregion

    #region Page Labels

    /// <summary>
    /// Get the label for a page
    /// </summary>
    /// <param name="document">Document handle</param>
    /// <param name="page_index">0-based page index</param>
    /// <param name="buffer">Buffer to receive the label (UTF-16LE)</param>
    /// <param name="buflen">Length of buffer in bytes</param>
    /// <returns>Number of bytes in the label, including the terminating null character</returns>
    [LibraryImport(LibraryName)]
    public static partial ulong FPDF_GetPageLabel(IntPtr document, int page_index, IntPtr buffer, ulong buflen);

    #endregion

    #region Bookmarks/Outlines

    /// <summary>
    /// Get the first child bookmark of the document root
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFBookmark_GetFirstChild(IntPtr document, IntPtr bookmark);

    /// <summary>
    /// Get the next sibling bookmark
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFBookmark_GetNextSibling(IntPtr document, IntPtr bookmark);

    /// <summary>
    /// Get bookmark title
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial ulong FPDFBookmark_GetTitle(IntPtr bookmark, IntPtr buffer, ulong buflen);

    /// <summary>
    /// Get the number of children of a bookmark
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int FPDFBookmark_GetCount(IntPtr bookmark);

    /// <summary>
    /// Find bookmark by title
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFBookmark_Find(IntPtr document, IntPtr title);

    /// <summary>
    /// Get the destination associated with a bookmark
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFBookmark_GetDest(IntPtr document, IntPtr bookmark);

    /// <summary>
    /// Get the action associated with a bookmark
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFBookmark_GetAction(IntPtr bookmark);

    #endregion

    #region Actions

    /// <summary>
    /// Get action type
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial ulong FPDFAction_GetType(IntPtr action);

    /// <summary>
    /// Get destination associated with an action
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFAction_GetDest(IntPtr document, IntPtr action);

    /// <summary>
    /// Get file path from a remote goto action
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial ulong FPDFAction_GetFilePath(IntPtr action, IntPtr buffer, ulong buflen);

    /// <summary>
    /// Get URI from a URI action
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial ulong FPDFAction_GetURIPath(IntPtr document, IntPtr action, IntPtr buffer, ulong buflen);

    #endregion

    #region Destinations

    /// <summary>
    /// Get page index from a destination
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial ulong FPDFDest_GetDestPageIndex(IntPtr document, IntPtr dest);

    /// <summary>
    /// Get location coordinates from a destination
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFDest_GetLocationInPage(IntPtr dest,
        out int hasXVal, out int hasYVal, out int hasZoomVal,
        out float x, out float y, out float zoom);

    #endregion

    #region Named Destinations

    /// <summary>
    /// Get the count of named destinations
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial ulong FPDF_CountNamedDests(IntPtr document);

    /// <summary>
    /// Get a named destination by index
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDF_GetNamedDestByName(IntPtr document,
        [MarshalAs(UnmanagedType.LPStr)] string name);

    /// <summary>
    /// Get a named destination by index
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDF_GetNamedDest(IntPtr document, int index, IntPtr buffer, out long buflen);

    #endregion

    #region Attachments (Embedded Files)

    /// <summary>
    /// Get the number of embedded files (attachments)
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int FPDFDoc_GetAttachmentCount(IntPtr document);

    /// <summary>
    /// Get attachment by index
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFDoc_GetAttachment(IntPtr document, int index);

    /// <summary>
    /// Get attachment name
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial ulong FPDFAttachment_GetName(IntPtr attachment, IntPtr buffer, ulong buflen);

    /// <summary>
    /// Check if attachment has a key
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFAttachment_HasKey(IntPtr attachment,
        [MarshalAs(UnmanagedType.LPWStr)] string key);

    /// <summary>
    /// Get attachment file size
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFAttachment_GetFile(IntPtr attachment, IntPtr buffer, ulong buflen, out ulong out_buflen);

    /// <summary>
    /// Get attachment string value
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial ulong FPDFAttachment_GetStringValue(IntPtr attachment,
        [MarshalAs(UnmanagedType.LPWStr)] string key, IntPtr buffer, ulong buflen);

    #endregion

    #region JavaScript

    /// <summary>
    /// Get the number of JavaScript actions
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int FPDFDoc_GetJavaScriptActionCount(IntPtr document);

    /// <summary>
    /// Get JavaScript action by index
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFDoc_GetJavaScriptAction(IntPtr document, int index);

    /// <summary>
    /// Get JavaScript action name
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial ulong FPDFDoc_GetJavaScriptActionName(IntPtr document, int index, IntPtr buffer, ulong buflen);

    #endregion

    #region XFA (XML Forms Architecture)

    /// <summary>
    /// Load XFA packet data
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDF_LoadXFA(IntPtr document);

    #endregion

    #region Signature Information

    /// <summary>
    /// Get the number of signatures in the document
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int FPDF_GetSignatureCount(IntPtr document);

    /// <summary>
    /// Get signature object by index
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDF_GetSignatureObject(IntPtr document, int index);

    #endregion

    #region Metadata Tag Constants

    // Standard metadata tags
    public const string METADATA_TITLE = "Title";
    public const string METADATA_AUTHOR = "Author";
    public const string METADATA_SUBJECT = "Subject";
    public const string METADATA_KEYWORDS = "Keywords";
    public const string METADATA_CREATOR = "Creator";
    public const string METADATA_PRODUCER = "Producer";
    public const string METADATA_CREATION_DATE = "CreationDate";
    public const string METADATA_MOD_DATE = "ModDate";
    public const string METADATA_TRAPPED = "Trapped";

    #endregion

    #region Action Type Constants

    public const ulong PDFACTION_UNSUPPORTED = 0;
    public const ulong PDFACTION_GOTO = 1;
    public const ulong PDFACTION_REMOTEGOTO = 2;
    public const ulong PDFACTION_URI = 3;
    public const ulong PDFACTION_LAUNCH = 4;
    public const ulong PDFACTION_EMBEDDEDGOTO = 5;

    #endregion

    #region Metadata Setting

    /// <summary>
    /// Set document metadata value by tag
    /// </summary>
    /// <param name="document">Document handle</param>
    /// <param name="tag">Metadata tag (e.g., "Title", "Author", "Subject", "Keywords", "Creator", "Producer", "CreationDate", "ModDate")</param>
    /// <param name="value">Value to set (UTF-16LE string)</param>
    /// <returns>True on success, false on failure</returns>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDF_SetMetaText(IntPtr document, string tag,
        [MarshalAs(UnmanagedType.LPWStr)] string value);

    #endregion

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

    #endregion

    #region Page Object Creation

    /// <summary>
    /// Create a new text object
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFPageObj_CreateNewPath(float x, float y);

    /// <summary>
    /// Create a new rectangle object
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFPageObj_CreateNewRect(float x, float y, float w, float h);

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

    #endregion

    #region Page Object Properties

    /// <summary>
    /// Destroy a page object (must be removed from page first)
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial void FPDFPageObj_Destroy(IntPtr page_obj);

    /// <summary>
    /// Transform page object with matrix
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial void FPDFPageObj_Transform(IntPtr page_obj, double a, double b, double c, double d, double e, double f);

    #endregion

    #region Thumbnail

    /// <summary>
    /// Get the thumbnail image of a page
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFPage_GetThumbnailAsBitmap(IntPtr page);

    #endregion
}