namespace PdfiumWrapper;

/// <summary>
/// PDF attachment information
/// </summary>
[NoNativeCall]
public class PdfAttachment
{
    public string? Name { get; set; }
    public long Size { get; set; }
    public byte[]? Data { get; set; }
}