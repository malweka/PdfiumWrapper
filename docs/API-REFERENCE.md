# API Reference

This document provides complete API documentation for all public classes in PdfiumWrapper.

## Table of Contents

- [PdfDocument](#pdfdocument)
- [PdfPage](#pdfpage)
- [Page Object Classes](#page-object-classes)
  - [PdfPageObject](#pdfpageobject)
  - [PdfTextObject](#pdftextobject)
  - [PdfImageObject](#pdfimageobject)
  - [PdfPathObject](#pdfpathobject)
- [PdfForm](#pdfform)
- [FormField](#formfield)
- [FormFieldType](#formfieldtype)
- [PdfMerger](#pdfmerger)
- [PdfMetadata](#pdfmetadata)
- [PdfBookmarks](#pdfbookmarks)
- [PdfBookmark](#pdfbookmark)
- [PdfAttachments](#pdfattachments)
- [PdfAttachment](#pdfattachment)

---

## PdfDocument

The main entry point for working with PDF documents. Provides access to pages, metadata, bookmarks, attachments, forms, and rendering capabilities.

### Namespace

```csharp
namespace PdfiumWrapper;
```

### Declaration

```csharp
public class PdfDocument : IDisposable
```

### Thread Safety

This class is **NOT** thread-safe. Do not access the same `PdfDocument` instance from multiple threads concurrently. Each instance should be used from a single thread at a time, or external synchronization must be provided.

### Constructors

#### PdfDocument()

Creates a new empty PDF document for adding pages and content.

```csharp
using var document = new PdfDocument();
using var page = document.AddPage();
// Add content...
page.GenerateContent();
document.Save("new_document.pdf");
```

#### PdfDocument(string filePath, string password = null)

Loads a PDF document from a file path.

```csharp
using var document = new PdfDocument("sample.pdf");
using var secureDoc = new PdfDocument("encrypted.pdf", password: "secret");
```

**Parameters:**
- `filePath` — Path to the PDF file
- `password` — Optional password for encrypted PDFs

**Exceptions:**
- `InvalidOperationException` — If the document fails to load

#### PdfDocument(byte[] data, string password = null)

Loads a PDF document from a byte array.

```csharp
byte[] pdfBytes = File.ReadAllBytes("sample.pdf");
using var document = new PdfDocument(pdfBytes);
```

**Parameters:**
- `data` — PDF file contents as byte array
- `password` — Optional password for encrypted PDFs

#### PdfDocument(Stream pdfStream, string password = null)

Loads a PDF document from a stream.

```csharp
using var stream = File.OpenRead("sample.pdf");
using var document = new PdfDocument(stream);
```

**Parameters:**
- `pdfStream` — Stream containing PDF data
- `password` — Optional password for encrypted PDFs

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `PageCount` | `int` | Number of pages in the document |
| `Permissions` | `uint` | Document permission flags (see PDF specification) |
| `Metadata` | `PdfMetadata` | Access to document metadata |
| `Bookmarks` | `PdfBookmarks` | Access to document bookmarks/outlines |
| `Attachments` | `PdfAttachments` | Access to embedded file attachments |

### Methods

#### AddPage(double width = 612, double height = 792, int? index = null)

Adds a new page to the document. Used when creating new PDFs.

```csharp
// Add page with default US Letter size (612 x 792 points)
using var page = document.AddPage();

// Add page with custom dimensions (A4)
using var page = document.AddPage(width: 595, height: 842);

// Insert page at specific position
using var page = document.AddPage(width: 612, height: 792, index: 0);
```

**Parameters:**
- `width` — Page width in points (default: 612 = US Letter)
- `height` — Page height in points (default: 792 = US Letter)
- `index` — Optional insertion index (null = append at end)

**Returns:** `PdfPage` instance (must be disposed)

**Common Page Sizes:**
| Size | Width | Height |
|------|-------|--------|
| US Letter | 612 | 792 |
| A4 | 595 | 842 |
| Legal | 612 | 1008 |

#### GetPage(int pageIndex)

Returns a `PdfPage` object for the specified page.

```csharp
using var page = document.GetPage(0); // First page (0-indexed)
string text = page.ExtractText();
```

**Parameters:**
- `pageIndex` — Zero-based page index

**Returns:** `PdfPage` instance (must be disposed)

**Exceptions:**
- `ArgumentOutOfRangeException` — If pageIndex is out of bounds

#### GetAllPages()

Returns all pages as an array. **Important:** Each page must be disposed individually.

```csharp
var pages = document.GetAllPages();
try
{
    foreach (var page in pages)
    {
        Console.WriteLine(page.ExtractText());
    }
}
finally
{
    foreach (var page in pages)
        page.Dispose();
}
```

**Returns:** Array of `PdfPage` instances

#### GetPageSize(int pageIndex)

Gets the dimensions of a specific page in points (1/72 inch).

```csharp
var (width, height) = document.GetPageSize(0);
Console.WriteLine($"Page 1: {width} x {height} points");
```

**Returns:** Tuple of (width, height) in points

#### GetAllPageSizes()

Gets dimensions for all pages.

```csharp
var sizes = document.GetAllPageSizes();
for (int i = 0; i < sizes.Length; i++)
{
    Console.WriteLine($"Page {i + 1}: {sizes[i].width} x {sizes[i].height}");
}
```

**Returns:** Array of (width, height) tuples

#### GetForm()

Returns the PDF form if the document contains form fields, or `null` if no form fields exist.

```csharp
var form = document.GetForm();
if (form != null)
{
    var fields = form.GetAllFormFields();
    // ... work with form fields
    form.Dispose();
}
```

**Returns:** `PdfForm` instance or `null`

#### RenderPages(int dpi = 300)

Renders all pages to `RawBitmap` records containing raw pixel data.

```csharp
RawBitmap[] bitmaps = document.RenderPages(dpi: 150);

// Process bitmaps — RawBitmap is a lightweight record, no disposal needed
foreach (var bitmap in bitmaps)
{
    Console.WriteLine($"{bitmap.Width}x{bitmap.Height}, stride={bitmap.Stride}");
    byte[] pixels = bitmap.Pixels; // Raw BGRA pixel data
}
```

**Parameters:**
- `dpi` — Resolution in dots per inch (default: 300)

**Returns:** Array of `RawBitmap` records (with `Pixels`, `Width`, `Height`, `Stride` properties). Not disposable.

#### RenderPages(int dpiWidth, int dpiHeight)

Renders all pages to bitmaps with different horizontal and vertical DPI.

#### RenderPagesAsync(int dpi = 300)

Async version that yields between pages for UI responsiveness.

```csharp
RawBitmap[] bitmaps = await document.RenderPagesAsync(dpi: 300);
```

**Note:** This method processes pages sequentially and uses `Task.Yield()` for responsiveness, not parallelism.

#### StreamImageBytes(ImageFormat format, int quality = 100, int dpi = 300)

Streams encoded image bytes one page at a time. Only one page's data is in memory at any point.

```csharp
foreach (var bytes in document.StreamImageBytes(ImageFormat.Png, quality: 100, dpi: 300))
{
    File.WriteAllBytes($"page.png", bytes);
    // Previous page's bytes are eligible for GC
}
```

**Parameters:**
- `format` — Image format (`ImageFormat.Png`, `ImageFormat.Jpeg`, `ImageFormat.Tiff`)
- `quality` — Quality for lossy formats (1-100)
- `dpi` — Resolution

**Returns:** `IEnumerable<byte[]>` — one byte array per page

#### StreamImageBytesAsync(ImageFormat format, int quality = 100, int dpi = 300)

Async streaming version. Uses `Task.Yield()` between pages for UI responsiveness.

```csharp
await foreach (var bytes in document.StreamImageBytesAsync(ImageFormat.Jpeg, 85, 200))
{
    await File.WriteAllBytesAsync($"page.jpg", bytes);
}
```

**Returns:** `IAsyncEnumerable<byte[]>` — one byte array per page

#### SaveAsTiff(string outputPath, int dpi = 200, TiffColorMode colorMode = TiffColorMode.Bilevel, byte threshold = 128)

Saves all pages as a single multi-page TIFF file using a direct PDFium-to-libtiff pipeline.

```csharp
// Bilevel (1-bit CCITT G4) — ideal for scanned documents
document.SaveAsTiff("output.tiff", dpi: 200);

// Grayscale (8-bit LZW)
document.SaveAsTiff("output.tiff", dpi: 200, colorMode: TiffColorMode.Grayscale);

// Separate horizontal/vertical DPI
document.SaveAsTiff("output.tiff", dpiWidth: 200, dpiHeight: 300);
```

**Parameters:**
- `outputPath` — Path to the output .tiff file
- `dpi` — Resolution in dots per inch (default: 200)
- `colorMode` — `TiffColorMode.Bilevel` (1-bit CCITT G4) or `TiffColorMode.Grayscale` (8-bit LZW). Default: Bilevel
- `threshold` — Luminance threshold 0-255 for bilevel mode. Ignored for grayscale. Default: 128

#### SaveAsTiff(Stream output, int dpi = 200, TiffColorMode colorMode = TiffColorMode.Bilevel, byte threshold = 128)

Saves all pages as a multi-page TIFF to a writable, seekable stream.

```csharp
using var stream = new MemoryStream();
document.SaveAsTiff(stream, dpi: 200);
```

#### SaveAsTiffAsync(...)

Async versions of both file and stream overloads. Uses `Task.Yield()` between pages for responsiveness.

```csharp
await document.SaveAsTiffAsync("output.tiff", dpi: 200);
await document.SaveAsTiffAsync(stream, dpi: 200, colorMode: TiffColorMode.Grayscale);
```

#### SaveAsPngs(string outputDirectory, string fileNamePrefix = "page", int dpi = 300)

Saves all pages as PNG files.

```csharp
document.SaveAsPngs("output", fileNamePrefix: "invoice", dpi: 300);
// Creates: output/invoice_001.png, output/invoice_002.png, etc.
```

#### SaveAsJpegs(string outputDirectory, string fileNamePrefix = "page", int quality = 90, int dpi = 300)

Saves all pages as JPEG files.

```csharp
document.SaveAsJpegs("output", fileNamePrefix: "page", quality: 85, dpi: 200);
```

#### SaveAsImages(string outputDirectory, string fileNamePrefix, ImageFormat format, int quality, int dpiWidth, int dpiHeight)

Saves all pages in the specified image format.

```csharp
document.SaveAsImages("output", "page", ImageFormat.Png, quality: 100, dpiWidth: 300, dpiHeight: 300);
```

**Note:** Supported formats are `ImageFormat.Png`, `ImageFormat.Jpeg`, and `ImageFormat.Tiff`.

#### SaveAsImagesAsync(...)

Async version of `SaveAsImages`.

#### SaveAsImages(Stream[] outputStreams, ImageFormat format, int quality, int dpiWidth, int dpiHeight)

Saves pages to provided streams.

```csharp
var streams = new Stream[document.PageCount];
for (int i = 0; i < streams.Length; i++)
    streams[i] = new MemoryStream();

document.SaveAsImages(streams, ImageFormat.Png, 100, 300, 300);
```

#### Save(string filePath, uint flags = 0)

Saves the PDF document to a file.

```csharp
document.Save("modified.pdf");
```

**Parameters:**
- `filePath` — Output file path
- `flags` — Save flags (0 for standard save)

#### SaveToStream(Stream stream, uint flags = 0)

Saves the PDF document to a stream.

```csharp
using var memoryStream = new MemoryStream();
document.SaveToStream(memoryStream);
byte[] pdfBytes = memoryStream.ToArray();
```

---

## PdfPage

Represents a single page in a PDF document. Provides rendering, text extraction, and page editing capabilities.

### Declaration

```csharp
public class PdfPage : IDisposable
```

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `PageIndex` | `int` | Zero-based index of this page |
| `Width` | `double` | Page width in points |
| `Height` | `double` | Page height in points |
| `HasEmbeddedThumbnail` | `bool` | Whether the page has an embedded thumbnail |

### Page Editing Methods

#### AddText(string text, float x, float y, string font = "Helvetica", float fontSize = 12)

Adds a text object to the page.

```csharp
var text = page.AddText("Hello World", x: 100, y: 700);
text.Font = "Helvetica-Bold";
text.FontSize = 24;
text.Color = Color.Black;
```

**Parameters:**
- `text` — The text content
- `x` — X position in points (from left)
- `y` — Y position in points (from bottom)
- `font` — Font name (default: "Helvetica")
- `fontSize` — Font size in points (default: 12)

**Returns:** `PdfTextObject` instance

#### AddImage(byte[] imageBytes, float x, float y, float width, float height)

Adds an image object to the page.

```csharp
var imageBytes = File.ReadAllBytes("logo.png");
var image = page.AddImage(imageBytes, x: 100, y: 500, width: 200, height: 100);
```

**Parameters:**
- `imageBytes` — Image data (PNG, JPEG, etc.)
- `x` — X position of bottom-left corner
- `y` — Y position of bottom-left corner
- `width` — Display width in points
- `height` — Display height in points

**Returns:** `PdfImageObject` instance

#### AddRectangle(float x, float y, float width, float height, Color? fillColor, Color? strokeColor)

Adds a rectangle to the page.

```csharp
var rect = page.AddRectangle(
    x: 100, y: 400, 
    width: 200, height: 100,
    fillColor: Color.LightBlue,
    strokeColor: Color.Black
);
rect.StrokeWidth = 2;
```

**Parameters:**
- `x` — X position of bottom-left corner
- `y` — Y position of bottom-left corner
- `width` — Rectangle width in points
- `height` — Rectangle height in points
- `fillColor` — Fill color (null for no fill)
- `strokeColor` — Stroke color (null for no stroke)

**Returns:** `PdfPathObject` instance

#### AddPath()

Creates a new empty path object for custom shapes.

```csharp
var path = page.AddPath();
path.MoveTo(100, 300);
path.LineTo(200, 300);
path.LineTo(150, 200);
path.Close();
path.FillColor = Color.Red;
path.StrokeColor = Color.Black;
path.SetDrawMode(PdfPathFillMode.Winding, stroke: true);
```

**Returns:** `PdfPathObject` instance

#### GenerateContent()

Generates the page content stream. **Must be called after adding or modifying page objects, before saving.**

```csharp
page.AddText("Hello", 100, 700);
page.AddRectangle(100, 500, 200, 100, Color.Blue, null);
page.GenerateContent();  // Required!
document.Save("output.pdf");
```

### Text Extraction Methods

#### ExtractText()

Extracts all text content from the page.

```csharp
using var page = document.GetPage(0);
string text = page.ExtractText();
Console.WriteLine(text);
```

**Returns:** Text content of the page

### Rendering Methods

#### RenderToBytes(int width, int height, int flags = 0)

Renders the page to raw BGRA pixel data.

```csharp
using var page = document.GetPage(0);
byte[] pixels = page.RenderToBytes(1920, 1080);
// pixels contains BGRA data at 4 bytes per pixel
```

**Parameters:**
- `width` — Output width in pixels
- `height` — Output height in pixels
- `flags` — Render flags (e.g., `PDFium.FPDF_ANNOT` to include annotations)

**Returns:** BGRA byte array

#### GetEmbeddedThumbnailBytes()

Gets the embedded thumbnail as raw BGRA bytes.

```csharp
if (page.HasEmbeddedThumbnail)
{
    byte[] thumbnail = page.GetEmbeddedThumbnailBytes();
}
```

**Returns:** BGRA byte array or `null` if no thumbnail exists

#### GetEmbeddedThumbnailSize()

Gets the dimensions of the embedded thumbnail.

```csharp
var size = page.GetEmbeddedThumbnailSize();
if (size.HasValue)
{
    Console.WriteLine($"Thumbnail: {size.Value.width} x {size.Value.height}");
}
```

**Returns:** Tuple of (width, height) or `null`

---

## Page Object Classes

Base classes for objects that can be added to PDF pages.

### PdfPageObject

Abstract base class for all page objects.

```csharp
public abstract class PdfPageObject : IDisposable
```

#### Methods

| Method | Description |
|--------|-------------|
| `GetBounds()` | Returns the bounding rectangle of the object |
| `GetMatrix()` | Returns the transformation matrix |
| `SetMatrix(matrix)` | Sets the transformation matrix |
| `Transform(a, b, c, d, e, f)` | Applies a transformation |
| `HasTransparency` | Returns whether the object has transparency |

---

### PdfTextObject

Represents text content on a PDF page.

```csharp
public class PdfTextObject : PdfPageObject
```

#### Properties

| Property | Type | Description |
|----------|------|-------------|
| `Font` | `string` | Font name (e.g., "Helvetica", "Times-Roman") |
| `FontSize` | `float` | Font size in points |
| `Color` | `Color` | Text color |

#### Example

```csharp
var text = page.AddText("Hello World", x: 100, y: 700);
text.Font = "Helvetica-Bold";
text.FontSize = 24;
text.Color = Color.DarkBlue;
```

#### Standard Fonts

| Font Family | Variants |
|-------------|----------|
| Helvetica | Helvetica, Helvetica-Bold, Helvetica-Oblique, Helvetica-BoldOblique |
| Times | Times-Roman, Times-Bold, Times-Italic, Times-BoldItalic |
| Courier | Courier, Courier-Bold, Courier-Oblique, Courier-BoldOblique |

---

### PdfImageObject

Represents an image on a PDF page.

```csharp
public class PdfImageObject : PdfPageObject
```

#### Creation

Images are created via `page.AddImage()`:

```csharp
var imageBytes = File.ReadAllBytes("photo.png");
var image = page.AddImage(imageBytes, x: 100, y: 500, width: 200, height: 150);
```

#### Supported Formats

Images are decoded using native libraries (libjpeg-turbo and libpng):
- PNG
- JPEG

---

### PdfPathObject

Represents vector paths and shapes on a PDF page.

```csharp
public class PdfPathObject : PdfPageObject
```

#### Properties

| Property | Type | Description |
|----------|------|-------------|
| `FillColor` | `Color` | Fill color for the path |
| `StrokeColor` | `Color` | Stroke (outline) color |
| `StrokeWidth` | `float` | Stroke width in points |

#### Path Drawing Methods

| Method | Description |
|--------|-------------|
| `MoveTo(x, y)` | Move to point (starts new subpath) |
| `LineTo(x, y)` | Draw line to point |
| `BezierTo(x1, y1, x2, y2, x3, y3)` | Draw cubic Bézier curve |
| `Close()` | Close current subpath |
| `SetDrawMode(fillMode, stroke)` | Set fill and stroke behavior |

#### PdfPathFillMode

| Value | Description |
|-------|-------------|
| `None` | No fill |
| `Alternate` | Alternate (even-odd) fill rule |
| `Winding` | Winding (non-zero) fill rule |

#### Example: Triangle

```csharp
var triangle = page.AddPath();
triangle.MoveTo(150, 300);   // Top
triangle.LineTo(100, 200);   // Bottom-left
triangle.LineTo(200, 200);   // Bottom-right
triangle.Close();

triangle.FillColor = Color.Red;
triangle.StrokeColor = Color.DarkRed;
triangle.StrokeWidth = 2;
triangle.SetDrawMode(PdfPathFillMode.Winding, stroke: true);
```

#### Example: Fluent API

```csharp
var path = page.AddPath();
path.MoveTo(100, 300)
    .LineTo(200, 300)
    .LineTo(150, 200)
    .Close();
```

---

## PdfForm

Provides access to PDF form fields and allows reading and modifying form data.

### Declaration

```csharp
public class PdfForm : IDisposable
```

### Thread Safety

This class is **NOT** thread-safe. Do not access the same `PdfForm` instance from multiple threads concurrently.

### Methods

#### GetAllFormFields()

Returns all form fields in the document.

```csharp
var form = document.GetForm();
if (form != null)
{
    FormField[] fields = form.GetAllFormFields();
    foreach (var field in fields)
    {
        Console.WriteLine($"{field.Name}: {field.Type} = {field.Value}");
    }
}
```

**Returns:** Array of `FormField` objects

#### GetFormFieldsOnPage(int pageIndex)

Returns form fields on a specific page.

```csharp
FormField[] pageFields = form.GetFormFieldsOnPage(0);
```

#### GetFormFieldValue(string fieldName)

Gets the current value of a form field.

```csharp
string name = form.GetFormFieldValue("FullName");
```

**Exceptions:**
- `ArgumentException` — If field not found

#### SetFormFieldValue(string fieldName, string value)

Sets the value of a form field.

```csharp
form.SetFormFieldValue("FullName", "John Doe");
form.SetFormFieldValue("Email", "john@example.com");
```

**Supported field types:**
- Text fields — Sets the text value
- Checkboxes/Radio buttons — Use "true"/"false", "1"/"0", or "yes"/"no"
- Combo boxes — Sets the selected value
- List boxes — Sets the selected value

**Exceptions:**
- `ArgumentException` — If field not found
- `NotSupportedException` — If field type doesn't support setting values

#### GetFormFieldChecked(string fieldName)

Gets whether a checkbox or radio button is checked.

```csharp
bool agreed = form.GetFormFieldChecked("AgreeToTerms");
```

#### SetFormFieldChecked(string fieldName, bool isChecked)

Sets the checked state of a checkbox or radio button.

```csharp
form.SetFormFieldChecked("AgreeToTerms", true);
form.SetFormFieldChecked("OptOut", false);
```

#### SetListBoxSelection(string fieldName, string selectedValue)

Sets the selected value for a list box.

```csharp
form.SetListBoxSelection("Country", "United States");
```

#### SetListBoxSelections(string fieldName, string[] selectedValues)

Sets multiple selections for a multi-select list box.

```csharp
form.SetListBoxSelections("Interests", new[] { "Music", "Sports", "Reading" });
```

---

## FormField

Represents a single form field with its properties and current value.

### Declaration

```csharp
public class FormField
```

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `Name` | `string` | Field name/identifier |
| `Type` | `FormFieldType` | Type of form field |
| `Value` | `string` | Current value |
| `PageIndex` | `int` | Page where field appears (0-indexed) |
| `IsRequired` | `bool` | Whether the field is required |
| `IsReadOnly` | `bool` | Whether the field is read-only |
| `Options` | `List<string>` | Available options for combo/list boxes |

---

## FormFieldType

Enumeration of form field types.

```csharp
public enum FormFieldType
{
    Unknown = 0,
    PushButton = 1,
    CheckBox = 2,
    RadioButton = 3,
    ComboBox = 4,
    ListBox = 5,
    TextField = 6,
    Signature = 7,
    XFA = 8,
    XFACheckBox = 9,
    XFAComboBox = 10,
    XFAImageField = 11,
    XFAListBox = 12,
    XFAPushButton = 13,
    XFASignature = 14,
    XFATextField = 15
}
```

---

## PdfMerger

High-level class for merging and manipulating PDF documents.

### Declaration

```csharp
public class PdfMerger : IDisposable
```

### Constructors

#### PdfMerger()

Creates a new empty PDF document for merging.

```csharp
using var merger = new PdfMerger();
```

#### PdfMerger(string filePath, string password = null)

Starts with an existing PDF document.

```csharp
using var merger = new PdfMerger("existing.pdf");
```

#### PdfMerger(byte[] data, string password = null)

Starts with an existing PDF from byte array.

#### PdfMerger(Stream pdfStream, string password = null)

Starts with an existing PDF from stream.

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `PageCount` | `int` | Current number of pages |

### Methods

#### AppendDocument(PdfDocument sourceDoc)

Appends all pages from another PDF document.

```csharp
using var source = new PdfDocument("source.pdf");
merger.AppendDocument(source);
```

#### AppendDocument(string filePath, string password = null)

Appends all pages from a PDF file.

```csharp
merger.AppendDocument("document1.pdf");
merger.AppendDocument("document2.pdf");
```

#### AppendDocument(byte[] pdfData, string password = null)

Appends all pages from PDF bytes.

#### AppendPages(PdfDocument sourceDoc, string pageRange)

Appends specific pages using a page range string.

```csharp
using var source = new PdfDocument("source.pdf");
merger.AppendPages(source, "1,3,5-7");  // Pages 1, 3, 5, 6, 7 (1-based)
merger.AppendPages(source, null);        // All pages
```

**Page Range Format:**
- Individual pages: `"1,3,5"`
- Ranges: `"1-5"`
- Combined: `"1,3,5-7,10"`
- All pages: `null`

**Note:** Page numbers in the range string are 1-based.

#### AppendPages(PdfDocument sourceDoc, int[] pageIndices)

Appends specific pages by zero-based index.

```csharp
using var source = new PdfDocument("source.pdf");
merger.AppendPages(source, new[] { 0, 2, 4 }); // First, third, fifth pages
```

#### InsertDocument(PdfDocument sourceDoc, int insertAtIndex)

Inserts all pages from a document at a specific position.

```csharp
using var source = new PdfDocument("insert.pdf");
merger.InsertDocument(source, insertAtIndex: 2); // Insert at position 2
```

#### InsertPages(PdfDocument sourceDoc, string pageRange, int insertAtIndex)

Inserts specific pages at a position.

```csharp
merger.InsertPages(source, "1-3", insertAtIndex: 0); // Insert at beginning
```

#### InsertPages(PdfDocument sourceDoc, int[] pageIndices, int insertAtIndex)

Inserts specific pages by index at a position.

#### DeletePage(int pageIndex)

Deletes a single page.

```csharp
merger.DeletePage(0); // Delete first page
```

**Parameters:**
- `pageIndex` — Zero-based page index

#### DeletePages(int[] pageIndices)

Deletes multiple pages. Indices are automatically sorted in descending order to avoid index shifting issues.

```csharp
merger.DeletePages(new[] { 1, 3, 5 }); // Delete pages at indices 1, 3, 5
```

#### Save(string outputPath, uint flags = 0)

Saves the merged document to a file.

```csharp
merger.Save("merged.pdf");
```

#### Save(Stream outputStream, uint flags = 0)

Saves the merged document to a stream.

#### ToBytes(uint flags = 0)

Returns the merged document as a byte array.

```csharp
byte[] pdfBytes = merger.ToBytes();
```

#### CopyViewerPreferences(PdfDocument sourceDoc)

Copies viewer preferences (zoom, layout, etc.) from a source document.

```csharp
using var source = new PdfDocument("source.pdf");
merger.CopyViewerPreferences(source);
```

---

## PdfMetadata

Provides access to PDF document metadata.

### Declaration

```csharp
public class PdfMetadata
```

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `Title` | `string` | Document title |
| `Author` | `string` | Document author |
| `Subject` | `string` | Document subject |
| `Keywords` | `string` | Document keywords |
| `Creator` | `string` | Application that created the original document |
| `Producer` | `string` | Application that produced the PDF |
| `CreationDate` | `string` | Raw creation date string |
| `ModificationDate` | `string` | Raw modification date string |
| `Trapped` | `string` | Trapped status |
| `FileVersion` | `int` | PDF version as integer (e.g., 17 for PDF 1.7) |
| `FileVersionString` | `string` | PDF version as string (e.g., "1.7") |
| `CreationDateTime` | `DateTime?` | Parsed creation date |
| `ModificationDateTime` | `DateTime?` | Parsed modification date |

### Methods

#### GetMetadataString(string tag)

Gets a metadata value by tag name.

```csharp
string customField = document.Metadata.GetMetadataString("CustomField");
```

#### SetMetadataString(string tag, string value)

Sets a metadata value by tag name.

```csharp
document.Metadata.SetMetadataString("CustomField", "Custom Value");
```

#### SetCreationDateTime(DateTime dateTime)

Sets the creation date from a DateTime.

```csharp
document.Metadata.SetCreationDateTime(DateTime.Now);
```

#### SetModificationDateTime(DateTime dateTime)

Sets the modification date from a DateTime.

```csharp
document.Metadata.SetModificationDateTime(DateTime.UtcNow);
```

#### SetAllMetadata(...)

Sets multiple metadata fields at once.

```csharp
document.Metadata.SetAllMetadata(
    title: "Annual Report 2024",
    author: "Finance Department",
    subject: "Q4 Financial Results",
    keywords: "finance, quarterly, 2024"
);
```

#### ClearAllMetadata()

Clears all metadata fields.

```csharp
document.Metadata.ClearAllMetadata();
```

#### GetAllMetadata()

Returns all metadata as a dictionary.

```csharp
Dictionary<string, string> metadata = document.Metadata.GetAllMetadata();
foreach (var kvp in metadata)
{
    Console.WriteLine($"{kvp.Key}: {kvp.Value}");
}
```

---

## PdfBookmarks

Provides access to PDF bookmarks/outlines.

### Declaration

```csharp
public class PdfBookmarks
```

### Methods

#### GetAllBookmarks()

Returns all bookmarks as a hierarchical list.

```csharp
List<PdfBookmark> bookmarks = document.Bookmarks.GetAllBookmarks();

void PrintBookmarks(List<PdfBookmark> bookmarks, int indent = 0)
{
    foreach (var bookmark in bookmarks)
    {
        Console.WriteLine($"{new string(' ', indent * 2)}{bookmark.Title} -> Page {bookmark.PageIndex + 1}");
        PrintBookmarks(bookmark.Children, indent + 1);
    }
}

PrintBookmarks(bookmarks);
```

---

## PdfBookmark

Represents a single bookmark/outline entry.

### Declaration

```csharp
public class PdfBookmark
```

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `Title` | `string` | Bookmark title/label |
| `PageIndex` | `int` | Target page (0-indexed) |
| `ChildCount` | `int` | Number of child bookmarks |
| `Children` | `List<PdfBookmark>` | Child bookmark entries |

---

## PdfAttachments

Provides access to embedded file attachments.

### Declaration

```csharp
public class PdfAttachments
```

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `Count` | `int` | Number of attachments |

### Methods

#### GetAllAttachments()

Returns all attachments.

```csharp
List<PdfAttachment> attachments = document.Attachments.GetAllAttachments();
foreach (var attachment in attachments)
{
    Console.WriteLine($"{attachment.Name}: {attachment.Size} bytes");
}
```

#### GetAttachment(int index)

Gets a specific attachment by index.

```csharp
var attachment = document.Attachments.GetAttachment(0);
if (attachment != null)
{
    File.WriteAllBytes(attachment.Name, attachment.Data);
}
```

#### ExtractAll(string outputDirectory)

Extracts all attachments to a directory.

```csharp
document.Attachments.ExtractAll("extracted_files");
```

---

## PdfAttachment

Represents an embedded file attachment.

### Declaration

```csharp
public class PdfAttachment
```

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `Name` | `string` | File name |
| `Size` | `long` | File size in bytes |
| `Data` | `byte[]` | File contents |

