# Malweka.PdfiumSdk

A modern, high-level .NET 8 wrapper for PDFium that makes PDF manipulation easy and intuitive. This library provides a clean C# API for working with PDF documents, including rendering, merging, form filling, metadata management, and more.

## Features

- ?? **PDF Rendering** - Convert PDF pages to images (PNG, JPEG, WebP, etc.) with customizable DPI
- ?? **PDF Merging** - Combine multiple PDFs or specific pages
- ?? **Form Filling** - Read and write PDF form fields (text fields, checkboxes, dropdowns)
- ?? **Metadata Management** - Read and modify PDF metadata (title, author, keywords, etc.)
- ?? **Bookmarks** - Access and manipulate PDF bookmarks/outlines
- ?? **Attachments** - Extract and manage embedded file attachments
- ?? **Page Management** - Extract text, get page dimensions, and manage thumbnails
- ?? **Async Support** - Async/await patterns for better performance
- ?? **Password-Protected PDFs** - Open and work with encrypted PDFs

## Installation

```bash
dotnet add package Malweka.PdfiumSdk
```

## Requirements

- .NET 8.0 or later
- SkiaSharp (automatically installed)
- Platform-specific PDFium binaries (included in the package)

## Quick Start

### Load a PDF Document

```csharp
using Malweka.PdfiumSdk;

// Load from file
using var document = new PdfDocument("sample.pdf");

// Load from byte array
byte[] pdfBytes = File.ReadAllBytes("sample.pdf");
using var document = new PdfDocument(pdfBytes);

// Load from stream
using var stream = File.OpenRead("sample.pdf");
using var document = new PdfDocument(stream);

// Load password-protected PDF
using var document = new PdfDocument("secure.pdf", password: "secret");
```

## Common Use Cases

### 1. Convert PDF to Images

```csharp
using Malweka.PdfiumSdk;
using SkiaSharp;

// Convert all pages to PNG images at 300 DPI
using var document = new PdfDocument("document.pdf");
document.SaveAsPngs("output_folder", fileNamePrefix: "page", dpi: 300);

// Convert to JPEG with quality setting
document.SaveAsJpegs("output_folder", fileNamePrefix: "page", quality: 90, dpi: 200);

// Convert to SKBitmap objects for further processing
SKBitmap[] bitmaps = document.ConvertToBitmaps(dpi: 150);
foreach (var bitmap in bitmaps)
{
    // Process bitmap...
  bitmap.Dispose();
}

// Async conversion for better performance
SKBitmap[] bitmaps = await document.ConvertToBitmapsAsync(dpi: 300);
```

### 2. Merge PDF Documents

```csharp
using Malweka.PdfiumSdk;

// Create a new merged document
using var merger = new PdfMerger();

// Append entire documents
merger.AppendDocument("document1.pdf");
merger.AppendDocument("document2.pdf");

// Append specific pages (1-based page numbers)
using var sourceDoc = new PdfDocument("source.pdf");
merger.AppendPages(sourceDoc, "1,3,5-7"); // Pages 1, 3, 5, 6, 7

// Append specific pages by index (0-based)
merger.AppendPages(sourceDoc, new[] { 0, 2, 4 }); // First, third, and fifth pages

// Insert pages at specific position
merger.InsertDocument(sourceDoc, insertAtIndex: 2);

// Delete pages
merger.DeletePage(0); // Delete first page
merger.DeletePages(new[] { 1, 3, 5 }); // Delete multiple pages

// Save the merged document
merger.Save("merged.pdf");

// Or get as byte array
byte[] mergedPdf = merger.ToBytes();
```

### 3. Read and Modify Metadata

```csharp
using Malweka.PdfiumSdk;

using var document = new PdfDocument("document.pdf");

// Read metadata
var metadata = document.Metadata;
Console.WriteLine($"Title: {metadata.Title}");
Console.WriteLine($"Author: {metadata.Author}");
Console.WriteLine($"Subject: {metadata.Subject}");
Console.WriteLine($"Keywords: {metadata.Keywords}");
Console.WriteLine($"PDF Version: {metadata.FileVersionString}");
Console.WriteLine($"Created: {metadata.CreationDateTime}");
Console.WriteLine($"Modified: {metadata.ModificationDateTime}");

// Get all metadata as dictionary
Dictionary<string, string> allMetadata = metadata.GetAllMetadata();

// Modify metadata
metadata.Title = "Updated Title";
metadata.Author = "John Doe";
metadata.Subject = "Important Document";
metadata.Keywords = "pdf, document, sample";

// Set multiple fields at once
metadata.SetAllMetadata(
    title: "My Document",
    author: "Jane Smith",
    subject: "Annual Report",
    keywords: "finance, 2024"
);

// Set dates
metadata.SetCreationDateTime(DateTime.Now);
metadata.SetModificationDateTime(DateTime.UtcNow);

// Clear all metadata
metadata.ClearAllMetadata();
```

### 4. Work with PDF Forms

```csharp
using Malweka.PdfiumSdk;

using var document = new PdfDocument("form.pdf");

// Get the form (returns null if no form fields exist)
var form = document.GetForm();
if (form != null)
{
    // Get all form fields
    FormField[] allFields = form.GetAllFormFields();
    
  foreach (var field in allFields)
    {
        Console.WriteLine($"Field: {field.Name}");
        Console.WriteLine($"Type: {field.Type}");
        Console.WriteLine($"Value: {field.Value}");
        Console.WriteLine($"Required: {field.IsRequired}");
        Console.WriteLine($"ReadOnly: {field.IsReadOnly}");
    }
    
    // Set text field value
  form.SetFormFieldValue("FullName", "John Doe");
 form.SetFormFieldValue("Email", "john@example.com");
    
    // Check/uncheck checkboxes
    form.SetFormFieldChecked("AgreeToTerms", true);
    
    // Get checkbox state
    bool isChecked = form.GetFormFieldChecked("AgreeToTerms");
    
    // Set dropdown/listbox selection
    form.SetListBoxSelection("Country", "United States");
    
    // Set multiple selections (for multi-select listboxes)
    form.SetListBoxSelections("Interests", new[] { "Music", "Sports", "Reading" });
}
```

### 5. Extract Text from Pages

```csharp
using Malweka.PdfiumSdk;

using var document = new PdfDocument("document.pdf");

// Extract text from a specific page
using var page = document.GetPage(0);
string text = page.ExtractText();
Console.WriteLine(text);

// Extract text from all pages
for (int i = 0; i < document.PageCount; i++)
{
    using var page = document.GetPage(i);
    string pageText = page.ExtractText();
    Console.WriteLine($"Page {i + 1}:\n{pageText}\n");
}
```

### 6. Work with Bookmarks

```csharp
using Malweka.PdfiumSdk;

using var document = new PdfDocument("document.pdf");
var bookmarks = document.Bookmarks;

// Get all bookmarks
PdfBookmark[] allBookmarks = bookmarks.GetAllBookmarks();

foreach (var bookmark in allBookmarks)
{
    Console.WriteLine($"Title: {bookmark.Title}");
    Console.WriteLine($"Level: {bookmark.Level}");
 Console.WriteLine($"Page Index: {bookmark.PageIndex}");
}

// Get top-level bookmarks only
PdfBookmark[] topLevel = bookmarks.GetTopLevelBookmarks();
```

### 7. Manage Attachments

```csharp
using Malweka.PdfiumSdk;

using var document = new PdfDocument("document.pdf");
var attachments = document.Attachments;

// Check if document has attachments
if (attachments.Count > 0)
{
    // List all attachments
    foreach (var attachment in attachments.GetAllAttachments())
    {
     Console.WriteLine($"Name: {attachment.Name}");
  Console.WriteLine($"Size: {attachment.Size} bytes");
    }
    
    // Get attachment by index
    var firstAttachment = attachments.GetAttachment(0);
    Console.WriteLine($"First attachment: {firstAttachment.Name}");
    
    // Save attachment to file
    attachments.SaveAttachmentToFile(0, "output.dat");
    
    // Get attachment content
    byte[] content = attachments.GetAttachmentContent(0);
}
```

### 8. Get Page Information

```csharp
using Malweka.PdfiumSdk;

using var document = new PdfDocument("document.pdf");

// Get page count
int pageCount = document.PageCount;
Console.WriteLine($"Total pages: {pageCount}");

// Get page dimensions (in points, 1/72 inch)
var (width, height) = document.GetPageSize(0);
Console.WriteLine($"Page 1: {width} x {height} points");

// Get all page sizes
var allSizes = document.GetAllPageSizes();
for (int i = 0; i < allSizes.Length; i++)
{
    Console.WriteLine($"Page {i + 1}: {allSizes[i].width} x {allSizes[i].height} points");
}

// Work with individual page
using var page = document.GetPage(0);
Console.WriteLine($"Width: {page.Width} points");
Console.WriteLine($"Height: {page.Height} points");

// Check for embedded thumbnail
if (page.HasEmbeddedThumbnail)
{
    byte[] thumbnailBytes = page.GetEmbeddedThumbnailBytes();
    var (thumbWidth, thumbHeight) = page.GetEmbeddedThumbnailSize().Value;
}
```

### 9. Render Specific Page to Image

```csharp
using Malweka.PdfiumSdk;

using var document = new PdfDocument("document.pdf");
using var page = document.GetPage(0);

// Render to raw BGRA bytes
int width = 1920;
int height = 1080;
byte[] imageBytes = page.RenderToBytes(width, height);

// Use with SkiaSharp for conversion
// (handled internally by PdfDocument.ConvertToBitmaps)
```

### 10. Check PDF Permissions

```csharp
using Malweka.PdfiumSdk;

using var document = new PdfDocument("document.pdf");

// Get permission flags
uint permissions = document.Permissions;
Console.WriteLine($"Permissions: {permissions}");

// Check specific permissions (based on PDF specification)
bool canPrint = (permissions & 0x4) != 0;
bool canModify = (permissions & 0x8) != 0;
bool canCopy = (permissions & 0x10) != 0;
bool canAnnotate = (permissions & 0x20) != 0;
```

## Advanced Examples

### Batch Convert PDFs to Images with Progress Reporting

```csharp
using Malweka.PdfiumSdk;
using SkiaSharp;

string[] pdfFiles = Directory.GetFiles("input_folder", "*.pdf");

foreach (var pdfFile in pdfFiles)
{
    Console.WriteLine($"Processing: {pdfFile}");
    
    using var document = new PdfDocument(pdfFile);
    string outputFolder = Path.Combine("output", Path.GetFileNameWithoutExtension(pdfFile));
 Directory.CreateDirectory(outputFolder);
    
    // Convert with async for better performance
    await document.SaveAsImagesAsync(
    outputFolder,
fileNamePrefix: "page",
        format: SKEncodedImageFormat.Png,
        quality: 100,
        dpiWidth: 300,
     dpiHeight: 300
    );
    
    Console.WriteLine($"Saved {document.PageCount} pages to {outputFolder}");
}
```

### Create a PDF Merger Utility

```csharp
using Malweka.PdfiumSdk;

public class PdfMergerUtility
{
    public static void MergeMultiplePdfs(string[] inputFiles, string outputFile)
    {
        using var merger = new PdfMerger();
        
        foreach (var file in inputFiles)
        {
    Console.WriteLine($"Adding: {file}");
         merger.AppendDocument(file);
        }
        
        merger.Save(outputFile);
        Console.WriteLine($"Merged {inputFiles.Length} files into {outputFile}");
      Console.WriteLine($"Total pages: {merger.PageCount}");
    }
    
    public static void ExtractPages(string inputFile, string outputFile, int[] pageIndices)
    {
        using var merger = new PdfMerger();
    using var sourceDoc = new PdfDocument(inputFile);
        
        merger.AppendPages(sourceDoc, pageIndices);
      merger.Save(outputFile);
   
   Console.WriteLine($"Extracted {pageIndices.Length} pages to {outputFile}");
    }
}

// Usage
PdfMergerUtility.MergeMultiplePdfs(
    new[] { "doc1.pdf", "doc2.pdf", "doc3.pdf" },
    "merged_output.pdf"
);

PdfMergerUtility.ExtractPages(
    "large_document.pdf",
    "selected_pages.pdf",
    new[] { 0, 2, 4, 6, 8 } // Extract pages 1, 3, 5, 7, 9
);
```

### Fill PDF Form from JSON

```csharp
using Malweka.PdfiumSdk;
using System.Text.Json;

public class FormData
{
    public string FullName { get; set; }
    public string Email { get; set; }
    public string Phone { get; set; }
    public bool AgreeToTerms { get; set; }
    public string Country { get; set; }
}

public static void FillFormFromJson(string pdfPath, string jsonPath, string outputPath)
{
    // Read form data
    string jsonContent = File.ReadAllText(jsonPath);
    var formData = JsonSerializer.Deserialize<FormData>(jsonContent);
    
    // Load PDF and fill form
    using var document = new PdfDocument(pdfPath);
    var form = document.GetForm();
  
    if (form != null)
    {
        form.SetFormFieldValue("FullName", formData.FullName);
        form.SetFormFieldValue("Email", formData.Email);
        form.SetFormFieldValue("Phone", formData.Phone);
form.SetFormFieldChecked("AgreeToTerms", formData.AgreeToTerms);
   form.SetListBoxSelection("Country", formData.Country);
 
        // Save the filled form
        // Note: In this example, you would need to save the document
        // PDFium typically requires re-rendering or flattening
        Console.WriteLine("Form filled successfully");
    }
}
```

## API Overview

### Main Classes

- **`PdfDocument`** - Main class for working with PDF documents
- **`PdfPage`** - Represents a single PDF page
- **`PdfMerger`** - Merge and manipulate multiple PDF documents
- **`PdfMetadata`** - Read/write PDF metadata
- **`PdfBookmarks`** - Access PDF bookmarks/outlines
- **`PdfAttachments`** - Manage embedded file attachments
- **`PdfForm`** - Work with PDF form fields
- **`FormField`** - Represents a single form field

## Platform Support

This library includes native PDFium binaries for:

- Windows (x64)
- macOS (x64 and ARM64)
- Linux (x64) - *can be enabled in project file*

## Performance Tips

1. **Use async methods** when processing multiple pages
2. **Dispose objects properly** - Use `using` statements
3. **Batch operations** - Process multiple files in parallel when possible
4. **Choose appropriate DPI** - Higher DPI = larger files and slower processing
5. **Reuse PdfDocument instances** when accessing multiple properties

## Thread Safety

?? **Important:** PDFium is not fully thread-safe. When using async operations, the library uses internal locking (`_pdfiumLock`) to ensure thread safety. If you're doing your own multi-threading, be cautious about concurrent access to the same document.

## License

This project is licensed under the MIT License.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## Credits

This library is a wrapper around [PDFium](https://pdfium.googlesource.com/pdfium/), Google's open-source PDF rendering engine.

Uses [SkiaSharp](https://github.com/mono/SkiaSharp) for image processing.

## Support

For issues, questions, or contributions, please visit the [GitHub repository](https://github.com/malweka/PdfiumWrapper).
