using System.Runtime.InteropServices;

namespace Malweka.PdfiumSdk;

/// <summary>
/// PDF bookmarks (table of contents)
/// </summary>
public class PdfBookmarks
{
    private IntPtr _document;

    internal PdfBookmarks(IntPtr document)
    {
        _document = document;
    }

    /// <summary>
    /// Get all top-level bookmarks
    /// </summary>
    public List<PdfBookmark> GetAllBookmarks()
    {
        var bookmarks = new List<PdfBookmark>();
        var firstBookmark = PDFium.FPDFBookmark_GetFirstChild(_document, IntPtr.Zero);

        if (firstBookmark != IntPtr.Zero)
        {
            TraverseBookmarks(firstBookmark, bookmarks);
        }

        return bookmarks;
    }

    private void TraverseBookmarks(IntPtr bookmarkHandle, List<PdfBookmark> bookmarkList)
    {
        while (bookmarkHandle != IntPtr.Zero)
        {
            var bookmark = ExtractBookmark(bookmarkHandle);

            // Get children
            var firstChild = PDFium.FPDFBookmark_GetFirstChild(_document, bookmarkHandle);
            if (firstChild != IntPtr.Zero)
            {
                TraverseBookmarks(firstChild, bookmark.Children);
            }

            bookmarkList.Add(bookmark);

            // Move to next sibling
            bookmarkHandle = PDFium.FPDFBookmark_GetNextSibling(_document, bookmarkHandle);
        }
    }

    private PdfBookmark ExtractBookmark(IntPtr bookmarkHandle)
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
        var dest = PDFium.FPDFBookmark_GetDest(_document, bookmarkHandle);
        if (dest != IntPtr.Zero)
        {
            bookmark.PageIndex = (int)PDFium.FPDFDest_GetDestPageIndex(_document, dest);
        }
        else
        {
            // Try to get from action
            var action = PDFium.FPDFBookmark_GetAction(bookmarkHandle);
            if (action != IntPtr.Zero)
            {
                dest = PDFium.FPDFAction_GetDest(_document, action);
                if (dest != IntPtr.Zero)
                {
                    bookmark.PageIndex = (int)PDFium.FPDFDest_GetDestPageIndex(_document, dest);
                }
            }
        }

        // Get child count
        bookmark.ChildCount = PDFium.FPDFBookmark_GetCount(bookmarkHandle);

        return bookmark;
    }
}