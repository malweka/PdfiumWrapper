namespace PdfiumWrapper;

/// <summary>
/// PDF bookmark/outline information
/// </summary>
[NoNativeCall]
public class PdfBookmark
{
    public string? Title { get; set; }
    public int PageIndex { get; set; }
    public int ChildCount { get; set; }
    public List<PdfBookmark> Children { get; set; } = new List<PdfBookmark>();
}