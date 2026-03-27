# Examples

This document provides detailed code examples for common scenarios using PdfiumWrapper.

## Table of Contents

- [PDF Creation](#pdf-creation)
- [PDF Rendering](#pdf-rendering)
- [PDF Merging](#pdf-merging)
- [Form Filling](#form-filling)
- [Text Extraction](#text-extraction)
- [Metadata Operations](#metadata-operations)
- [Bookmarks](#bookmarks)
- [Attachments](#attachments)
- [Advanced Scenarios](#advanced-scenarios)

---

## PDF Creation

### Create a Simple PDF

```csharp
using PdfiumWrapper;
using System.Drawing;

using var document = new PdfDocument();
using var page = document.AddPage(width: 612, height: 792); // US Letter

// Add title
var title = page.AddText("Hello World", x: 100, y: 700);
title.Font = "Helvetica";
title.FontSize = 24;
title.Color = Color.Black;

// Add body text
var body = page.AddText("This is a sample PDF.", x: 100, y: 650);
body.Font = "Helvetica";
body.FontSize = 12;
body.Color = Color.Gray;

page.GenerateContent();
document.Save("hello_world.pdf");
```

### Create PDF with Different Page Sizes

```csharp
using var document = new PdfDocument();

// US Letter (default)
using var letterPage = document.AddPage();

// A4 Portrait
using var a4Page = document.AddPage(width: 595, height: 842);

// A4 Landscape  
using var a4Landscape = document.AddPage(width: 842, height: 595);

// Custom size
using var customPage = document.AddPage(width: 400, height: 600);

// Add content to each page...
letterPage.AddText("US Letter", 100, 700);
letterPage.GenerateContent();

a4Page.AddText("A4 Portrait", 100, 800);
a4Page.GenerateContent();

a4Landscape.AddText("A4 Landscape", 100, 550);
a4Landscape.GenerateContent();

customPage.AddText("Custom Size", 100, 550);
customPage.GenerateContent();

document.Save("multiple_sizes.pdf");
```

### Create PDF with Images

```csharp
using var document = new PdfDocument();
using var page = document.AddPage();

// Add title
var title = page.AddText("Document with Image", x: 100, y: 700);
title.Font = "Helvetica-Bold";
title.FontSize = 18;
title.Color = Color.Black;

// Add image from file
var imageBytes = File.ReadAllBytes("logo.png");
var image = page.AddImage(imageBytes, x: 100, y: 500, width: 200, height: 100);

// Add caption below image
var caption = page.AddText("Figure 1: Company Logo", x: 100, y: 480);
caption.Font = "Helvetica-Oblique";
caption.FontSize = 10;
caption.Color = Color.Gray;

page.GenerateContent();
document.Save("with_image.pdf");
```

### Create PDF with Shapes

```csharp
using var document = new PdfDocument();
using var page = document.AddPage();

// Title
var title = page.AddText("Shapes Demo", x: 250, y: 750);
title.Font = "Helvetica-Bold";
title.FontSize = 20;
title.Color = Color.Black;

// Filled rectangle
var rect = page.AddRectangle(
    x: 100, y: 600, 
    width: 150, height: 80,
    fillColor: Color.LightBlue, 
    strokeColor: Color.Blue
);
rect.StrokeWidth = 2;

// Rectangle outline only (border)
var border = page.AddRectangle(
    x: 300, y: 600,
    width: 150, height: 80,
    fillColor: null,
    strokeColor: Color.Red
);
border.StrokeWidth = 3;

// Triangle using path
var triangle = page.AddPath();
triangle.MoveTo(175, 500);   // Top point
triangle.LineTo(100, 400);   // Bottom-left
triangle.LineTo(250, 400);   // Bottom-right
triangle.Close();
triangle.FillColor = Color.LightGreen;
triangle.StrokeColor = Color.DarkGreen;
triangle.StrokeWidth = 2;
triangle.SetDrawMode(PdfPathFillMode.Winding, stroke: true);

page.GenerateContent();
document.Save("shapes.pdf");
```

### Create Multi-Page PDF

```csharp
using var document = new PdfDocument();

for (int i = 0; i < 5; i++)
{
    using var page = document.AddPage();
    
    // Page header
    var header = page.AddText($"Page {i + 1} of 5", x: 250, y: 750);
    header.Font = "Helvetica-Bold";
    header.FontSize = 18;
    header.Color = Color.DarkBlue;
    
    // Page border
    var border = page.AddRectangle(
        x: 50, y: 50, 
        width: 512, height: 692,
        fillColor: null, 
        strokeColor: Color.LightGray
    );
    
    // Page content
    var content = page.AddText(
        $"This is the content of page {i + 1}.", 
        x: 100, y: 600
    );
    content.Font = "Times-Roman";
    content.FontSize = 12;
    content.Color = Color.Black;
    
    // Footer
    var footer = page.AddText(
        "Generated with PdfiumWrapper", 
        x: 200, y: 30
    );
    footer.Font = "Helvetica";
    footer.FontSize = 8;
    footer.Color = Color.Gray;
    
    page.GenerateContent();
}

document.Save("multipage.pdf");
```

### Create PDF Invoice Template

```csharp
using var document = new PdfDocument();
using var page = document.AddPage();

float pageWidth = 612;
float pageHeight = 792;
float margin = 50;

// Company header
var companyName = page.AddText("ACME Corporation", margin, pageHeight - 50);
companyName.Font = "Helvetica-Bold";
companyName.FontSize = 24;
companyName.Color = Color.DarkBlue;

var tagline = page.AddText("Quality Products Since 1990", margin, pageHeight - 75);
tagline.Font = "Helvetica";
tagline.FontSize = 10;
tagline.Color = Color.Gray;

// Invoice title
var invoiceTitle = page.AddText("INVOICE", pageWidth - 150, pageHeight - 50);
invoiceTitle.Font = "Helvetica-Bold";
invoiceTitle.FontSize = 28;
invoiceTitle.Color = Color.Black;

// Invoice details
var invoiceNum = page.AddText("Invoice #: INV-2024-001", pageWidth - 200, pageHeight - 100);
invoiceNum.Font = "Helvetica";
invoiceNum.FontSize = 10;
invoiceNum.Color = Color.Black;

var invoiceDate = page.AddText("Date: January 15, 2024", pageWidth - 200, pageHeight - 115);
invoiceDate.Font = "Helvetica";
invoiceDate.FontSize = 10;
invoiceDate.Color = Color.Black;

// Horizontal line
var line = page.AddRectangle(margin, pageHeight - 140, pageWidth - 2 * margin, 1, Color.Gray, null);

// Bill To section
var billTo = page.AddText("Bill To:", margin, pageHeight - 170);
billTo.Font = "Helvetica-Bold";
billTo.FontSize = 12;
billTo.Color = Color.Black;

var customerName = page.AddText("John Smith", margin, pageHeight - 190);
var customerAddr = page.AddText("123 Main Street", margin, pageHeight - 205);
var customerCity = page.AddText("Anytown, ST 12345", margin, pageHeight - 220);

// Table header background
var tableHeader = page.AddRectangle(margin, pageHeight - 280, pageWidth - 2 * margin, 25, Color.LightGray, null);

// Table headers
var descHeader = page.AddText("Description", margin + 10, pageHeight - 270);
descHeader.Font = "Helvetica-Bold";
descHeader.FontSize = 10;

var qtyHeader = page.AddText("Qty", 350, pageHeight - 270);
qtyHeader.Font = "Helvetica-Bold";
qtyHeader.FontSize = 10;

var priceHeader = page.AddText("Price", 420, pageHeight - 270);
priceHeader.Font = "Helvetica-Bold";
priceHeader.FontSize = 10;

var totalHeader = page.AddText("Total", 500, pageHeight - 270);
totalHeader.Font = "Helvetica-Bold";
totalHeader.FontSize = 10;

// Table row
var item1 = page.AddText("Widget Pro", margin + 10, pageHeight - 300);
var qty1 = page.AddText("5", 350, pageHeight - 300);
var price1 = page.AddText("$99.99", 420, pageHeight - 300);
var total1 = page.AddText("$499.95", 500, pageHeight - 300);

// Subtotal
var subtotalLabel = page.AddText("Subtotal:", 420, pageHeight - 350);
var subtotalValue = page.AddText("$499.95", 500, pageHeight - 350);

var taxLabel = page.AddText("Tax (8%):", 420, pageHeight - 370);
var taxValue = page.AddText("$40.00", 500, pageHeight - 370);

var grandTotalLabel = page.AddText("Total:", 420, pageHeight - 400);
grandTotalLabel.Font = "Helvetica-Bold";
grandTotalLabel.FontSize = 14;
var grandTotalValue = page.AddText("$539.95", 500, pageHeight - 400);
grandTotalValue.Font = "Helvetica-Bold";
grandTotalValue.FontSize = 14;

// Footer
var thankYou = page.AddText("Thank you for your business!", margin, 80);
thankYou.Font = "Helvetica-Oblique";
thankYou.FontSize = 12;
thankYou.Color = Color.Gray;

page.GenerateContent();
document.Save("invoice.pdf");
```

---

## PDF Rendering

### Convert All Pages to PNG Images

```csharp
using PdfiumWrapper;

using var document = new PdfDocument("document.pdf");

// Simple: Save all pages as PNG at 300 DPI
document.SaveAsPngs("output_folder", fileNamePrefix: "page", dpi: 300);
// Creates: output_folder/page_001.png, output_folder/page_002.png, etc.
```

### Convert to JPEG with Quality Control

```csharp
using var document = new PdfDocument("document.pdf");

// JPEG with 85% quality at 200 DPI (good balance of size and quality)
document.SaveAsJpegs("output_folder", fileNamePrefix: "scan", quality: 85, dpi: 200);
```

### Convert to WebP Format

```csharp
using PdfiumWrapper;
using SkiaSharp;

using var document = new PdfDocument("document.pdf");

document.SaveAsImages(
    outputDirectory: "output_folder",
    fileNamePrefix: "page",
    format: SKEncodedImageFormat.Webp,
    quality: 80,
    dpiWidth: 150,
    dpiHeight: 150
);
```

### Stream Images as Byte Arrays

```csharp
using var document = new PdfDocument("document.pdf");

// Stream pages one at a time — only one page's bytes in memory at any point
int i = 0;
foreach (var bytes in document.StreamImageBytes(SKEncodedImageFormat.Png, quality: 100, dpi: 150))
{
    File.WriteAllBytes($"page_{++i}.png", bytes);
}

// Async version
await foreach (var bytes in document.StreamImageBytesAsync(SKEncodedImageFormat.Jpeg, quality: 85, dpi: 200))
{
    await File.WriteAllBytesAsync($"page_{++i}.jpg", bytes);
}
```

### Save as Multi-Page TIFF

```csharp
using var document = new PdfDocument("document.pdf");

// Bilevel CCITT G4 (default) — ideal for scanned documents
document.SaveAsTiff("output.tiff", dpi: 200);

// Grayscale LZW
document.SaveAsTiff("output.tiff", dpi: 200, colorMode: TiffColorMode.Grayscale);

// Write to a stream
using var stream = new MemoryStream();
document.SaveAsTiff(stream, dpi: 200);
```

### Get SkiaSharp Bitmaps for Custom Processing

```csharp
using PdfiumWrapper;
using SkiaSharp;

using var document = new PdfDocument("document.pdf");
SKBitmap[] bitmaps = document.ConvertToBitmaps(dpi: 300);

try
{
    foreach (var bitmap in bitmaps)
    {
        // Apply custom image processing
        using var surface = SKSurface.Create(new SKImageInfo(bitmap.Width, bitmap.Height));
        var canvas = surface.Canvas;
        
        // Draw original
        canvas.DrawBitmap(bitmap, 0, 0);
        
        // Add watermark
        using var paint = new SKPaint
        {
            Color = SKColors.Red.WithAlpha(128),
            TextSize = 48,
            IsAntialias = true
        };
        canvas.DrawText("CONFIDENTIAL", 50, 100, paint);
        
        // Save result
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        // ... save or use data
    }
}
finally
{
    foreach (var bitmap in bitmaps)
        bitmap.Dispose();
}
```

### Async Rendering for UI Applications

```csharp
using var document = new PdfDocument("large_document.pdf");

// Async version keeps UI responsive
SKBitmap[] bitmaps = await document.ConvertToBitmapsAsync(dpi: 150);

// Or save directly to files asynchronously
await document.SaveAsImagesAsync(
    "output_folder",
    "page",
    SKEncodedImageFormat.Png,
    quality: 100,
    dpiWidth: 300,
    dpiHeight: 300
);
```

### Render Single Page

```csharp
using var document = new PdfDocument("document.pdf");
using var page = document.GetPage(0); // First page

// Get raw BGRA bytes
byte[] pixels = page.RenderToBytes(width: 1920, height: 1080);

// Or calculate dimensions based on DPI
double dpi = 150;
int width = (int)(page.Width / 72.0 * dpi);
int height = (int)(page.Height / 72.0 * dpi);
byte[] highResPixels = page.RenderToBytes(width, height);
```

### Different DPI for Width and Height

```csharp
using var document = new PdfDocument("document.pdf");

// Non-uniform DPI (rare use case)
var bitmaps = document.ConvertToBitmaps(dpiWidth: 300, dpiHeight: 150);
```

---

## PDF Merging

### Merge Multiple PDF Files

```csharp
using PdfiumWrapper;

using var merger = new PdfMerger();

merger.AppendDocument("chapter1.pdf");
merger.AppendDocument("chapter2.pdf");
merger.AppendDocument("chapter3.pdf");

Console.WriteLine($"Total pages: {merger.PageCount}");
merger.Save("complete_book.pdf");
```

### Merge with Password-Protected PDFs

```csharp
using var merger = new PdfMerger();

merger.AppendDocument("public.pdf");
merger.AppendDocument("secure.pdf", password: "secret123");

merger.Save("combined.pdf");
```

### Extract Specific Pages

```csharp
using var merger = new PdfMerger();
using var source = new PdfDocument("large_document.pdf");

// Extract pages 1, 3, 5-10 (1-based page numbers)
merger.AppendPages(source, "1,3,5-10");
merger.Save("selected_pages.pdf");
```

### Extract Pages by Index

```csharp
using var merger = new PdfMerger();
using var source = new PdfDocument("document.pdf");

// Extract first, fifth, and last pages (0-based indices)
int lastPageIndex = source.PageCount - 1;
merger.AppendPages(source, new[] { 0, 4, lastPageIndex });
merger.Save("extracted.pdf");
```

### Insert Pages at Specific Position

```csharp
using var merger = new PdfMerger("main_document.pdf");
using var coverPage = new PdfDocument("cover.pdf");
using var appendix = new PdfDocument("appendix.pdf");

// Insert cover at the beginning
merger.InsertDocument(coverPage, insertAtIndex: 0);

// Appendix goes at the end (already default behavior for Append)
merger.AppendDocument(appendix);

merger.Save("complete_document.pdf");
```

### Delete Pages

```csharp
using var merger = new PdfMerger("document.pdf");

// Delete single page (0-based index)
merger.DeletePage(0); // Remove first page

// Delete multiple pages
merger.DeletePages(new[] { 1, 3, 5 }); // Indices handled in correct order

merger.Save("trimmed.pdf");
```

### Split PDF into Individual Pages

```csharp
using var source = new PdfDocument("multi_page.pdf");

for (int i = 0; i < source.PageCount; i++)
{
    using var merger = new PdfMerger();
    merger.AppendPages(source, new[] { i });
    merger.Save($"page_{i + 1:D3}.pdf");
}
```

### Merge and Get as Byte Array

```csharp
using var merger = new PdfMerger();
merger.AppendDocument("doc1.pdf");
merger.AppendDocument("doc2.pdf");

byte[] mergedPdf = merger.ToBytes();

// Use directly or save
await File.WriteAllBytesAsync("merged.pdf", mergedPdf);
```

### Copy Viewer Preferences

```csharp
using var merger = new PdfMerger();
using var sourceWithPrefs = new PdfDocument("source_with_zoom.pdf");

merger.AppendDocument("document.pdf");
merger.CopyViewerPreferences(sourceWithPrefs); // Copy zoom, layout settings

merger.Save("with_preferences.pdf");
```

---

## Form Filling

### List All Form Fields

```csharp
using var document = new PdfDocument("form.pdf");
var form = document.GetForm();

if (form == null)
{
    Console.WriteLine("This PDF has no form fields");
    return;
}

FormField[] fields = form.GetAllFormFields();

foreach (var field in fields)
{
    Console.WriteLine($"Field: {field.Name}");
    Console.WriteLine($"  Type: {field.Type}");
    Console.WriteLine($"  Value: {field.Value}");
    Console.WriteLine($"  Page: {field.PageIndex + 1}");
    Console.WriteLine($"  Required: {field.IsRequired}");
    Console.WriteLine($"  ReadOnly: {field.IsReadOnly}");
    
    if (field.Options?.Count > 0)
    {
        Console.WriteLine($"  Options: {string.Join(", ", field.Options)}");
    }
    Console.WriteLine();
}

form.Dispose();
```

### Fill Text Fields

```csharp
using var document = new PdfDocument("application_form.pdf");
var form = document.GetForm();

if (form != null)
{
    form.SetFormFieldValue("FirstName", "John");
    form.SetFormFieldValue("LastName", "Doe");
    form.SetFormFieldValue("Email", "john.doe@example.com");
    form.SetFormFieldValue("Phone", "555-123-4567");
    form.SetFormFieldValue("Address", "123 Main Street\nAnytown, USA 12345");
    
    form.Dispose();
    document.Save("filled_application.pdf");
}
```

### Work with Checkboxes

```csharp
using var document = new PdfDocument("consent_form.pdf");
var form = document.GetForm();

if (form != null)
{
    // Check boxes
    form.SetFormFieldChecked("AgreeToTerms", true);
    form.SetFormFieldChecked("ReceiveNewsletter", false);
    form.SetFormFieldChecked("ShareData", false);
    
    // Read checkbox state
    bool hasAgreed = form.GetFormFieldChecked("AgreeToTerms");
    Console.WriteLine($"User agreed to terms: {hasAgreed}");
    
    form.Dispose();
    document.Save("signed_consent.pdf");
}
```

### Work with Dropdown/ComboBox

```csharp
using var document = new PdfDocument("registration.pdf");
var form = document.GetForm();

if (form != null)
{
    // Set dropdown selection
    form.SetFormFieldValue("Country", "United States");
    form.SetFormFieldValue("State", "California");
    
    // For combo boxes, you can also type custom values
    form.SetFormFieldValue("Occupation", "Software Engineer");
    
    form.Dispose();
    document.Save("completed_registration.pdf");
}
```

### Work with List Boxes

```csharp
using var document = new PdfDocument("preferences.pdf");
var form = document.GetForm();

if (form != null)
{
    // Single selection
    form.SetListBoxSelection("PrimaryLanguage", "English");
    
    // Multiple selections (for multi-select list boxes)
    form.SetListBoxSelections("Skills", new[] 
    { 
        "C#", 
        "JavaScript", 
        "SQL",
        "Azure"
    });
    
    form.Dispose();
    document.Save("preferences_filled.pdf");
}
```

### Fill Form from Dictionary

```csharp
using var document = new PdfDocument("form.pdf");
var form = document.GetForm();

if (form != null)
{
    var formData = new Dictionary<string, string>
    {
        ["FullName"] = "Jane Smith",
        ["Email"] = "jane@example.com",
        ["Department"] = "Engineering",
        ["StartDate"] = "2024-01-15"
    };
    
    foreach (var (fieldName, value) in formData)
    {
        try
        {
            form.SetFormFieldValue(fieldName, value);
        }
        catch (ArgumentException)
        {
            Console.WriteLine($"Warning: Field '{fieldName}' not found");
        }
    }
    
    form.Dispose();
    document.Save("filled_form.pdf");
}
```

### Fill Form from JSON

```csharp
using System.Text.Json;

public record FormData(
    string FullName,
    string Email,
    string Phone,
    bool AgreeToTerms,
    string Country
);

// Load JSON
string json = await File.ReadAllTextAsync("form_data.json");
var data = JsonSerializer.Deserialize<FormData>(json);

using var document = new PdfDocument("form.pdf");
var form = document.GetForm();

if (form != null && data != null)
{
    form.SetFormFieldValue("FullName", data.FullName);
    form.SetFormFieldValue("Email", data.Email);
    form.SetFormFieldValue("Phone", data.Phone);
    form.SetFormFieldChecked("AgreeToTerms", data.AgreeToTerms);
    form.SetFormFieldValue("Country", data.Country);
    
    form.Dispose();
    document.Save("completed.pdf");
}
```

---

## Text Extraction

### Extract Text from All Pages

```csharp
using var document = new PdfDocument("document.pdf");

var allText = new StringBuilder();

for (int i = 0; i < document.PageCount; i++)
{
    using var page = document.GetPage(i);
    string pageText = page.ExtractText();
    
    allText.AppendLine($"=== Page {i + 1} ===");
    allText.AppendLine(pageText);
    allText.AppendLine();
}

Console.WriteLine(allText.ToString());
```

### Extract Text from Specific Page

```csharp
using var document = new PdfDocument("document.pdf");

// Extract from page 5 (0-indexed)
using var page = document.GetPage(4);
string text = page.ExtractText();

Console.WriteLine(text);
```

### Search for Text in PDF

```csharp
using var document = new PdfDocument("document.pdf");

string searchTerm = "important";
var results = new List<(int PageNumber, string Context)>();

for (int i = 0; i < document.PageCount; i++)
{
    using var page = document.GetPage(i);
    string text = page.ExtractText();
    
    if (text.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
    {
        // Get context around the match
        int index = text.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase);
        int start = Math.Max(0, index - 50);
        int length = Math.Min(text.Length - start, 100 + searchTerm.Length);
        string context = text.Substring(start, length);
        
        results.Add((i + 1, context.Trim()));
    }
}

foreach (var (pageNum, context) in results)
{
    Console.WriteLine($"Page {pageNum}: ...{context}...");
}
```

### Export Text to File

```csharp
using var document = new PdfDocument("document.pdf");
using var writer = new StreamWriter("extracted_text.txt");

for (int i = 0; i < document.PageCount; i++)
{
    using var page = document.GetPage(i);
    await writer.WriteLineAsync($"--- Page {i + 1} ---");
    await writer.WriteLineAsync(page.ExtractText());
    await writer.WriteLineAsync();
}
```

---

## Metadata Operations

### Read All Metadata

```csharp
using var document = new PdfDocument("document.pdf");
var metadata = document.Metadata;

Console.WriteLine($"Title: {metadata.Title}");
Console.WriteLine($"Author: {metadata.Author}");
Console.WriteLine($"Subject: {metadata.Subject}");
Console.WriteLine($"Keywords: {metadata.Keywords}");
Console.WriteLine($"Creator: {metadata.Creator}");
Console.WriteLine($"Producer: {metadata.Producer}");
Console.WriteLine($"PDF Version: {metadata.FileVersionString}");

if (metadata.CreationDateTime.HasValue)
    Console.WriteLine($"Created: {metadata.CreationDateTime.Value:yyyy-MM-dd HH:mm:ss}");
    
if (metadata.ModificationDateTime.HasValue)
    Console.WriteLine($"Modified: {metadata.ModificationDateTime.Value:yyyy-MM-dd HH:mm:ss}");
```

### Get Metadata as Dictionary

```csharp
using var document = new PdfDocument("document.pdf");
Dictionary<string, string> allMetadata = document.Metadata.GetAllMetadata();

foreach (var (key, value) in allMetadata)
{
    if (!string.IsNullOrEmpty(value))
        Console.WriteLine($"{key}: {value}");
}
```

### Set Metadata

```csharp
using var document = new PdfDocument("document.pdf");
var metadata = document.Metadata;

metadata.Title = "Annual Report 2024";
metadata.Author = "Finance Department";
metadata.Subject = "Q4 Financial Results";
metadata.Keywords = "finance, quarterly, 2024, annual report";
metadata.Creator = "My Application";

document.Save("document_with_metadata.pdf");
```

### Set All Metadata at Once

```csharp
using var document = new PdfDocument("document.pdf");

document.Metadata.SetAllMetadata(
    title: "Project Documentation",
    author: "Development Team",
    subject: "Technical Specification",
    keywords: "api, documentation, v2.0",
    creator: "DocGenerator v1.0",
    producer: "PdfiumWrapper"
);

document.Save("documented.pdf");
```

### Update Modification Date

```csharp
using var document = new PdfDocument("document.pdf");

document.Metadata.SetModificationDateTime(DateTime.UtcNow);
document.Save("document.pdf");
```

### Clear All Metadata (Privacy)

```csharp
using var document = new PdfDocument("document.pdf");

document.Metadata.ClearAllMetadata();
document.Save("clean_document.pdf");
```

---

## Bookmarks

### Read Bookmark Hierarchy

```csharp
using var document = new PdfDocument("document.pdf");
List<PdfBookmark> bookmarks = document.Bookmarks.GetAllBookmarks();

void PrintBookmarks(List<PdfBookmark> bookmarks, int level = 0)
{
    string indent = new string(' ', level * 2);
    
    foreach (var bookmark in bookmarks)
    {
        Console.WriteLine($"{indent}{bookmark.Title} -> Page {bookmark.PageIndex + 1}");
        
        if (bookmark.Children.Count > 0)
        {
            PrintBookmarks(bookmark.Children, level + 1);
        }
    }
}

PrintBookmarks(bookmarks);
```

### Generate Table of Contents

```csharp
using var document = new PdfDocument("book.pdf");
var bookmarks = document.Bookmarks.GetAllBookmarks();

var toc = new StringBuilder();
toc.AppendLine("# Table of Contents");
toc.AppendLine();

void AddToToc(List<PdfBookmark> bookmarks, int level)
{
    foreach (var bookmark in bookmarks)
    {
        string prefix = new string('#', level + 1);
        toc.AppendLine($"{prefix} {bookmark.Title} (Page {bookmark.PageIndex + 1})");
        
        if (bookmark.Children.Count > 0)
        {
            AddToToc(bookmark.Children, level + 1);
        }
    }
}

AddToToc(bookmarks, 1);
await File.WriteAllTextAsync("toc.md", toc.ToString());
```

---

## Attachments

### List Attachments

```csharp
using var document = new PdfDocument("document_with_attachments.pdf");
var attachments = document.Attachments;

Console.WriteLine($"Found {attachments.Count} attachment(s)");

foreach (var attachment in attachments.GetAllAttachments())
{
    Console.WriteLine($"  {attachment.Name} ({attachment.Size:N0} bytes)");
}
```

### Extract Single Attachment

```csharp
using var document = new PdfDocument("document.pdf");

if (document.Attachments.Count > 0)
{
    var attachment = document.Attachments.GetAttachment(0);
    
    if (attachment != null)
    {
        await File.WriteAllBytesAsync(attachment.Name, attachment.Data);
        Console.WriteLine($"Extracted: {attachment.Name}");
    }
}
```

### Extract All Attachments

```csharp
using var document = new PdfDocument("document.pdf");

if (document.Attachments.Count > 0)
{
    string outputDir = "extracted_attachments";
    document.Attachments.ExtractAll(outputDir);
    
    Console.WriteLine($"Extracted {document.Attachments.Count} files to {outputDir}");
}
```

---

## Advanced Scenarios

### Batch PDF Processing

```csharp
public static async Task BatchConvertPdfsToImages(string inputFolder, string outputFolder, int dpi = 150)
{
    var pdfFiles = Directory.GetFiles(inputFolder, "*.pdf");
    
    foreach (var pdfFile in pdfFiles)
    {
        string fileName = Path.GetFileNameWithoutExtension(pdfFile);
        string pdfOutputFolder = Path.Combine(outputFolder, fileName);
        
        Console.WriteLine($"Processing: {fileName}");
        
        try
        {
            using var document = new PdfDocument(pdfFile);
            await document.SaveAsImagesAsync(
                pdfOutputFolder,
                "page",
                SKEncodedImageFormat.Png,
                100,
                dpi,
                dpi
            );
            
            Console.WriteLine($"  Converted {document.PageCount} pages");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Error: {ex.Message}");
        }
    }
}
```

### PDF Processing Pipeline

```csharp
public class PdfProcessor
{
    public async Task<ProcessingResult> ProcessAsync(Stream pdfStream)
    {
        using var document = new PdfDocument(pdfStream);
        
        var result = new ProcessingResult
        {
            PageCount = document.PageCount,
            Metadata = document.Metadata.GetAllMetadata()
        };
        
        // Extract text from all pages
        var textBuilder = new StringBuilder();
        for (int i = 0; i < document.PageCount; i++)
        {
            using var page = document.GetPage(i);
            textBuilder.AppendLine(page.ExtractText());
        }
        result.FullText = textBuilder.ToString();
        
        // Generate thumbnail of first page
        result.ThumbnailBytes = document.StreamImageBytes(
            SKEncodedImageFormat.Jpeg,
            quality: 75,
            dpi: 72
        ).FirstOrDefault();
        
        // Check for forms
        var form = document.GetForm();
        if (form != null)
        {
            result.FormFields = form.GetAllFormFields()
                .Select(f => new FormFieldInfo(f.Name, f.Type.ToString(), f.Value))
                .ToList();
            form.Dispose();
        }
        
        // Get bookmarks
        result.Bookmarks = document.Bookmarks.GetAllBookmarks()
            .Select(b => b.Title)
            .ToList();
        
        return result;
    }
}

public record ProcessingResult
{
    public int PageCount { get; set; }
    public Dictionary<string, string> Metadata { get; set; }
    public string FullText { get; set; }
    public byte[] ThumbnailBytes { get; set; }
    public List<FormFieldInfo> FormFields { get; set; }
    public List<string> Bookmarks { get; set; }
}

public record FormFieldInfo(string Name, string Type, string Value);
```

### ASP.NET Core File Upload and Processing

```csharp
[ApiController]
[Route("api/pdf")]
public class PdfApiController : ControllerBase
{
    [HttpPost("analyze")]
    public async Task<IActionResult> AnalyzePdf(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest("No file provided");
        
        using var stream = new MemoryStream();
        await file.CopyToAsync(stream);
        stream.Position = 0;
        
        try
        {
            using var document = new PdfDocument(stream);
            
            return Ok(new
            {
                FileName = file.FileName,
                PageCount = document.PageCount,
                Metadata = document.Metadata.GetAllMetadata(),
                HasForm = document.GetForm() != null,
                AttachmentCount = document.Attachments.Count,
                BookmarkCount = document.Bookmarks.GetAllBookmarks().Count
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest($"Invalid PDF: {ex.Message}");
        }
    }
    
    [HttpPost("thumbnail")]
    public async Task<IActionResult> GetThumbnail(IFormFile file, [FromQuery] int dpi = 72)
    {
        if (file == null)
            return BadRequest("No file provided");
        
        using var stream = new MemoryStream();
        await file.CopyToAsync(stream);
        stream.Position = 0;
        
        using var document = new PdfDocument(stream);
        var thumbnail = document.StreamImageBytes(SKEncodedImageFormat.Jpeg, 80, dpi).First();

        return File(thumbnail, "image/jpeg");
    }
}
```

### Generate PDF Report from Multiple Sources

```csharp
public async Task<byte[]> GenerateReportAsync(
    string coverPagePath,
    string[] contentPaths,
    string appendixPath)
{
    using var merger = new PdfMerger();
    
    // Add cover page
    merger.AppendDocument(coverPagePath);
    
    // Add all content pages
    foreach (var contentPath in contentPaths)
    {
        merger.AppendDocument(contentPath);
    }
    
    // Add appendix
    if (File.Exists(appendixPath))
    {
        merger.AppendDocument(appendixPath);
    }
    
    // Set metadata
    using var coverDoc = new PdfDocument(coverPagePath);
    merger.CopyViewerPreferences(coverDoc);
    
    return merger.ToBytes();
}
```

