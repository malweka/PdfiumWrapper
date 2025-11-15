namespace Malweka.PdfiumSdk;

/// <summary>
/// PDF bookmark/outline information
/// </summary>
public class PdfBookmark
{
    public string Title { get; set; }
    public int PageIndex { get; set; }
    public int ChildCount { get; set; }
    public List<PdfBookmark> Children { get; set; } = new List<PdfBookmark>();
}