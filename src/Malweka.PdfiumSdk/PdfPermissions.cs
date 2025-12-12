namespace Malweka.PdfiumSdk;

/// <summary>
/// PDF document permissions/restrictions flags
/// </summary>
[Flags]
public enum PdfPermissions
{
    /// <summary>
    /// No permissions granted
    /// </summary>
    None = 0,
    
    /// <summary>
    /// Printing is allowed (bit 3)
    /// </summary>
    Print = 1 << 2,
    
    /// <summary>
    /// Modifying the contents of the document is allowed (bit 4)
    /// </summary>
    ModifyContents = 1 << 3,
    
    /// <summary>
    /// Copying or extracting text and graphics is allowed (bit 5)
    /// </summary>
    CopyContents = 1 << 4,
    
    /// <summary>
    /// Adding or modifying text annotations and interactive form fields is allowed (bit 6)
    /// </summary>
    ModifyAnnotations = 1 << 5,
    
    /// <summary>
    /// Filling in forms is allowed (bit 9)
    /// </summary>
    FillForms = 1 << 8,
    
    /// <summary>
    /// Extracting text and graphics for accessibility purposes is allowed (bit 10)
    /// </summary>
    ExtractForAccessibility = 1 << 9,
    
    /// <summary>
    /// Assembling the document (inserting, rotating, or deleting pages and creating bookmarks or thumbnail images) is allowed (bit 11)
    /// </summary>
    AssembleDocument = 1 << 10,
    
    /// <summary>
    /// Printing at high quality is allowed (bit 12)
    /// </summary>
    PrintHighQuality = 1 << 11
}

