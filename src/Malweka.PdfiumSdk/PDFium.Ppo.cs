using System.Runtime.InteropServices;

namespace Malweka.PdfiumSdk;

/// <summary>
/// PDFium Page Organizer APIs from fpdf_ppo.h
/// For importing pages from one document to another (merging PDFs)
/// </summary>
public static partial class PDFium
{
    #region Page Import/Merge

    /// <summary>
    /// Import pages from src_doc to dest_doc
    /// </summary>
    /// <param name="dest_doc">Destination document</param>
    /// <param name="src_doc">Source document</param>
    /// <param name="pagerange">Page range string (e.g., "1,3,5-7" or null for all pages)</param>
    /// <param name="index">Index at which to insert pages in destination (0-based)</param>
    /// <returns>True on success, false on failure</returns>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDF_ImportPages(IntPtr dest_doc, IntPtr src_doc, string? pagerange, int index);

    /// <summary>
    /// Import pages from src_doc to dest_doc using an array of page indices
    /// </summary>
    /// <param name="dest_doc">Destination document</param>
    /// <param name="src_doc">Source document</param>
    /// <param name="page_indices">Array of 0-based page indices to import</param>
    /// <param name="length">Number of pages in the array</param>
    /// <param name="index">Index at which to insert pages in destination (0-based)</param>
    /// <returns>True on success, false on failure</returns>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDF_ImportPagesByIndex(IntPtr dest_doc, IntPtr src_doc,
        int[] page_indices, ulong length, int index);

    /// <summary>
    /// Copy the viewer preferences from src_doc to dest_doc
    /// </summary>
    /// <param name="dest_doc">Destination document</param>
    /// <param name="src_doc">Source document</param>
    /// <returns>True on success, false on failure</returns>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDF_CopyViewerPreferences(IntPtr dest_doc, IntPtr src_doc);

    #endregion

    #region Document Creation and Saving

    /// <summary>
    /// Create a new empty PDF document
    /// </summary>
    /// <returns>Handle to the new document, or null on failure</returns>
    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDF_CreateNewDocument();

    /// <summary>
    /// Delete a page from the document
    /// </summary>
    /// <param name="document">Document handle</param>
    /// <param name="page_index">0-based page index to delete</param>
    [LibraryImport(LibraryName)]
    public static partial void FPDFPage_Delete(IntPtr document, int page_index);

    #endregion

    #region Document Saving

    /// <summary>
    /// Save the document to a file path
    /// </summary>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDF_SaveAsCopy(IntPtr document, ref FPDF_FILEWRITE fileWrite, uint flags);

    /// <summary>
    /// Save the document with specific version
    /// </summary>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDF_SaveWithVersion(IntPtr document, ref FPDF_FILEWRITE fileWrite,
        uint flags, int fileVersion);

    #endregion

    #region File Write Structure

    /// <summary>
    /// Structure for custom file writing
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_FILEWRITE
    {
        /// <summary>
        /// Version number, currently must be 1
        /// </summary>
        public int version;

        /// <summary>
        /// Function pointer: int WriteBlock(FPDF_FILEWRITE* pThis, const void* pData, unsigned long size)
        /// Should return non-zero on success
        /// </summary>
        public IntPtr WriteBlock;
    }

    #endregion

    #region Save Flags

    /// <summary>
    /// Incremental save (only changes are saved)
    /// </summary>
    public const uint FPDF_INCREMENTAL = 1;

    /// <summary>
    /// Don't generate an object stream
    /// </summary>
    public const uint FPDF_NO_INCREMENTAL = 2;

    /// <summary>
    /// Remove security/encryption
    /// </summary>
    public const uint FPDF_REMOVE_SECURITY = 3;

    #endregion

    #region PDF Version Constants

    public const int PDF_VERSION_10 = 10; // PDF 1.0
    public const int PDF_VERSION_11 = 11; // PDF 1.1
    public const int PDF_VERSION_12 = 12; // PDF 1.2
    public const int PDF_VERSION_13 = 13; // PDF 1.3
    public const int PDF_VERSION_14 = 14; // PDF 1.4
    public const int PDF_VERSION_15 = 15; // PDF 1.5
    public const int PDF_VERSION_16 = 16; // PDF 1.6
    public const int PDF_VERSION_17 = 17; // PDF 1.7

    #endregion
}