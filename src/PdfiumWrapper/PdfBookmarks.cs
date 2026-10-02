using System.Runtime.InteropServices;

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

        // Get title
        ulong titleLength = PDFium.FPDFBookmark_GetTitle(bookmarkHandle, IntPtr.Zero, 0);
        if (titleLength > 0)
        {
            var titleBuffer = Marshal.AllocHGlobal((int)titleLength);
            try
            {
                PDFium.FPDFBookmark_GetTitle(bookmarkHandle, titleBuffer, titleLength);
                bookmark.Title = Marshal.PtrToStringUni(titleBuffer) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(titleBuffer);
            }
        }

        // Get destination page
        var dest = PDFium.FPDFBookmark_GetDest(document, bookmarkHandle);
        if (dest != IntPtr.Zero)
        {
            bookmark.PageIndex = (int)PDFium.FPDFDest_GetDestPageIndex(document, dest);
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
                    bookmark.PageIndex = (int)PDFium.FPDFDest_GetDestPageIndex(document, dest);
                }
            }
        }

        // Get child count
        bookmark.ChildCount = PDFium.FPDFBookmark_GetCount(bookmarkHandle);

        return bookmark;
    }
}
