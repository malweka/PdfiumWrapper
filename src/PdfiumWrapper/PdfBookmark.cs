namespace PdfiumWrapper;

/// <summary>
/// PDF bookmark/outline information
/// </summary>
[NoNativeCall]
public class PdfBookmark
{
    public string? Title { get; set; }

    /// <summary>
    /// The 0-based index of the page the bookmark opens, from its destination or its GoTo action.
    /// Null when the bookmark has neither, or when PDFium cannot resolve the destination to a page
    /// of this document (for example a remote GoTo action, or a named destination that is not defined).
    /// </summary>
    public int? PageIndex { get; set; }
    public int ChildCount { get; set; }
    public List<PdfBookmark> Children { get; set; } = new List<PdfBookmark>();
}