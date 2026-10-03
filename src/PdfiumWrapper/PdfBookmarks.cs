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

        // Get destination page
        var dest = PDFium.FPDFBookmark_GetDest(document, bookmarkHandle);
        if (dest != IntPtr.Zero)
        {
            bookmark.PageIndex = PDFium.FPDFDest_GetDestPageIndex(document, dest);
        }
        else
        {
            // Try to get from action
            var action = PDFium.FPDFBookmark_GetAction(bookmarkHandle);
            if (action != IntPtr.Zero)
            {
                dest = PDFium.FPDFAction_GetDest(document, action);
                if (dest != IntPtr.Zero)
                {
                    bookmark.PageIndex = PDFium.FPDFDest_GetDestPageIndex(document, dest);
                }
            }
        }

        // Get child count
        bookmark.ChildCount = PDFium.FPDFBookmark_GetCount(bookmarkHandle);

        return bookmark;
    }
}
