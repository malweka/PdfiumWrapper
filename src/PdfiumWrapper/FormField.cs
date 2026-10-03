namespace PdfiumWrapper;

public class FormField
{
    public string? Name { get; set; }
    public FormFieldType Type { get; set; }

    /// <summary>
    /// The field's current value. Check boxes and radio buttons: "true" or "false", from the widget's
    /// checked state whatever its export value is. List boxes: the first selected value.
    /// </summary>
    public string? Value { get; set; }
    public int PageIndex { get; set; }
    public bool IsRequired { get; set; }
    public bool IsReadOnly { get; set; }
    public List<string> Options { get; set; } = new List<string>();
}