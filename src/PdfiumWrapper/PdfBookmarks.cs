namespace PdfiumWrapper;

/// <summary>
/// PDF bookmarks (table of contents)
/// </summary>
public class PdfBookmarks
{
    private readonly PdfDocument _owner;

    internal PdfBookmarks(PdfDocument owner)
    {
        _owner = owner;
    }

    /// <summary>
    /// Get all top-level bookmarks
    /// </summary>
    public List<PdfBookmark> GetAllBookmarks()
    {
        using var _ = PdfiumRuntime.Enter();
        _owner.ThrowIfDisposed();
        var document = _owner.Document;

        var bookmarks = new List<PdfBookmark>();
        var firstBookmark = PDFium.FPDFBookmark_GetFirstChild(document, IntPtr.Zero);

        if (firstBookmark != IntPtr.Zero)
        {
            TraverseBookmarks(document, firstBookmark, bookmarks);
        }

        return bookmarks;
    }

    private static void TraverseBookmarks(IntPtr document, IntPtr bookmarkHandle, List<PdfBookmark> bookmarkList)
    {
        while (bookmarkHandle != IntPtr.Zero)
        {
            var bookmark = ExtractBookmark(document, bookmarkHandle);

            // Get children
            var firstChild = PDFium.FPDFBookmark_GetFirstChild(document, bookmarkHandle);
            if (firstChild != IntPtr.Zero)
            {
                TraverseBookmarks(document, firstChild, bookmark.Children);
            }

            bookmarkList.Add(bookmark);

            // Move to next sibling
            bookmarkHandle = PDFium.FPDFBookmark_GetNextSibling(document, bookmarkHandle);
        }
    }

    private static PdfBookmark ExtractBookmark(IntPtr document, IntPtr bookmarkHandle)
    {
        var bookmark = new PdfBookmark();

        var title = NativeText.ReadUtf16(bookmarkHandle, static (handle, buffer, length) => PDFium.FPDFBookmark_GetTitle(handle, buffer, length));
        if (title != null)
            bookmark.Title = title;

        // Get destination page. FPDFBookmark_GetDest falls back to the destination of the
        // bookmark's action, whatever its type: a remote or embedded GoTo names a page of another
        // document, so only a GoTo action (or none) can target a page here.
        var action = PDFium.FPDFBookmark_GetAction(bookmarkHandle);
        bool targetsThisDocument = action == IntPtr.Zero || PDFium.FPDFAction_GetType(action).Value == PDFium.PDFACTION_GOTO;
        var dest = targetsThisDocument ? PDFium.FPDFBookmark_GetDest(document, bookmarkHandle) : IntPtr.Zero;

        // No destination stays null, and so does one PDFium cannot resolve (-1) or one that names
        // a page number past the end, so page 0 is never confused with "no target".
        if (dest != IntPtr.Zero)
        {
            int pageIndex = PDFium.FPDFDest_GetDestPageIndex(document, dest);
            if (pageIndex >= 0 && pageIndex < PDFium.FPDF_GetPageCount(document))
                bookmark.PageIndex = pageIndex;
        }

        // Get child count
        bookmark.ChildCount = PDFium.FPDFBookmark_GetCount(bookmarkHandle);

        return bookmark;
    }
}
