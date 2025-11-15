namespace Malweka.PdfiumSdk;

public class FormField
{
    public string Name { get; set; }
    public FormFieldType Type { get; set; }
    public string Value { get; set; }
    public int PageIndex { get; set; }
    public bool IsRequired { get; set; }
    public bool IsReadOnly { get; set; }
    public List<string> Options { get; set; } = new List<string>();
}