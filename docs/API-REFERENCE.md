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
- [PdfiumRuntime](#pdfiumruntime)
- [The PDFium Class in 2.0](#the-pdfium-class-in-20)
- [PdfProcessingPool (PdfiumWrapper.Processing)](#pdfprocessingpool-pdfiumwrapperprocessing)
  - [PdfPoolOptions](#pdfpooloptions)
  - [PdfJobResult](#pdfjobresult)
  - [PdfWorkerHost](#pdfworkerhost)
  - [Events and Statistics](#events-and-statistics)

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

Different `PdfDocument` instances may be used from different threads at the same time. PDFium allows one native call per process at a time, so the wrapper serializes native work (loading, rendering, text, saving) across all documents through one process-wide gate (see [PdfiumRuntime](#pdfiumruntime)); image encoding and output writes run outside it and overlap.

One instance must not be used from two threads at once. If an instance is shared, the caller must synchronize access to it.

A document owns its pages, the forms returned by `GetForm()`, and the page objects removed from its pages with `RemoveObject`. Disposing the document disposes them.

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

The array is used in place, without a copy: it is pinned, and PDFium reads pages from it for as long as the document is open. Do not modify, reuse or return the array to a pool until the document is disposed; pass a copy (or use the `Stream` constructor) if you need the array back sooner.

**Parameters:**
- `data` — PDF file contents as byte array
- `password` — Optional password for encrypted PDFs

#### PdfDocument(Stream pdfStream, string password = null)

Loads a PDF document from a stream.

```csharp
using var stream = File.OpenRead("sample.pdf");
using var document = new PdfDocument(stream);
```

The stream is read from its current position to its end during construction, before any native work starts, and is left positioned at its end. The document does not use the stream afterwards, so it can be closed, reset or reused immediately.

- Inputs of up to 64 MB are copied into a buffer the document owns (rented from `ArrayPool<byte>.Shared` when the stream is seekable, and returned after the document is closed). This includes a `MemoryStream`: its own buffer is never used in place, so overwriting it after construction cannot change the document.
- Larger inputs are copied to a temporary file that is deleted when the document is disposed.

The threshold can be changed with `AppContext.SetData("PdfiumWrapper.SpoolThreshold", bytes)` (a `long`) before loading.

**Parameters:**
- `pdfStream` — Readable stream containing PDF data. It does not need to be seekable.
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

Each call returns a new `PdfForm`. The form belongs to the document: dispose it when done, or let the document dispose it. A form cannot be used after its document is disposed.

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

Async version. Waits for the native gate without blocking a thread and yields between pages.

```csharp
RawBitmap[] bitmaps = await document.RenderPagesAsync(dpi: 300);
```

**Note:** This applies to every async method on `PdfDocument` (`RenderPagesAsync`, `StreamImageBytesAsync`, `SaveAsTiffAsync`, `SaveAsJpegsAsync`, `SaveAsImagesAsync`, `ProcessAllPagesAsync`): pages are processed sequentially, `Task.Yield()` is used between pages, and waiting for the gate does not block a thread. The synchronous methods, including constructors, block the calling thread while they wait. Neither form renders one document's pages in parallel.

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

Async streaming version. Waits for the native gate without blocking a thread and uses `Task.Yield()` between pages.

The call itself returns at once and never waits for the gate. It throws immediately if the document is disposed or the format cannot be streamed (`ImageFormat.Tiff`). An empty document is reported (`InvalidOperationException`) when enumeration starts, because reading the page count is native work. The same holds for `StreamJpegBytesAsync`.

```csharp
await foreach (var bytes in document.StreamImageBytesAsync(ImageFormat.Jpeg, 85, 200))
{
    await File.WriteAllBytesAsync($"page.jpg", bytes);
}
```

**Returns:** `IAsyncEnumerable<byte[]>` — one byte array per page

#### SaveAsTiff(string outputPath, int dpi = 200, TiffColorMode colorMode = TiffColorMode.Bilevel, byte threshold = 128)

Saves all pages as a single multi-page TIFF file using a direct PDFium-to-libtiff pipeline.

Pages are rendered in 8-bit gray, since the output is bilevel or grayscale either way. **Changed in 2.0:** 1.x rendered 32-bit color and converted it. PDFium smooths text slightly differently at the two depths, so the files are not pixel-identical to 1.x output (roughly 1-2% of pixels on a text page, at glyph edges).

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

Async versions of both file and stream overloads. They wait for the native gate without blocking a thread and use `Task.Yield()` between pages.

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
- `flags` — Save flags (0 for standard save, `PDFium.FPDF_INCREMENTAL` for an incremental save)

The document is serialized into a pooled in-memory buffer first and written to the file afterwards, so peak memory includes the full output size.

#### SaveToStream(Stream stream, uint flags = 0)

Saves the PDF document to a stream.

```csharp
using var memoryStream = new MemoryStream();
document.SaveToStream(memoryStream);
byte[] pdfBytes = memoryStream.ToArray();
```

The document is serialized into a pooled in-memory buffer first and written to `stream` in one call afterwards. A slow stream therefore does not hold up other PDF work, and peak memory includes the full output size.

**Exceptions:**
- `InvalidOperationException` — If PDFium fails to serialize the document
- Any exception thrown by `stream` (for example `IOException`) propagates unchanged. Before 2.0 a failing stream was reported as `InvalidOperationException`.

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
var text = page.AddText("Hello World", x: 100, y: 700, font: "Helvetica-Bold", fontSize: 24);
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
| `GetMatrix()` | Returns the transformation matrix as `(a, b, c, d, e, f)` |
| `SetMatrix(a, b, c, d, e, f)` | Sets the transformation matrix |
| `Transform(a, b, c, d, e, f)` | Applies a transformation |
| `HasTransparency` | Returns whether the object has transparency |

**Note:** Before 2.0, `GetMatrix()` used a wrong native signature and could crash the process. Fixed in 2.0.

An object added to a page belongs to that page. An object removed with `PdfPage.RemoveObject` belongs to the caller again and is tracked by the document: dispose it, or the document disposes it when the document is disposed.

---

### PdfTextObject

Represents text content on a PDF page.

```csharp
public class PdfTextObject : PdfPageObject
```

#### Properties

| Property | Type | Description |
|----------|------|-------------|
| `Text` | `string` | Text content (set only) |
| `FontSize` | `float` | Font size in points (get and set) |
| `Color` | `Color` | Fill color (set only) |
| `StrokeColor` | `Color` | Stroke color (set only) |

The font is chosen when the text is added (`PdfPage.AddText(text, x, y, font, fontSize)`); PDFium cannot change a text object's font afterwards.

#### Example

```csharp
var text = page.AddText("Hello World", x: 100, y: 700, font: "Helvetica-Bold", fontSize: 24);
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

#### Reading Pixels

| Method | Description |
|--------|-------------|
| `GetBitmap()` | The image's own pixels, without its mask or transformation, as a `RawBitmap` (BGRA). Returns `null` if the image has no bitmap |
| `GetRenderedBitmap(PdfPage? page = null)` | The image as it appears on the page, with mask and transformation applied, as a `RawBitmap` (BGRA). Pass the page the image is on for better color handling |

Both return managed pixels. The native bitmap PDFium produces is copied and destroyed by the wrapper, so there is nothing to release. Grayscale and BGR images are expanded to BGRA with full opacity.

```csharp
RawBitmap? pixels = image.GetBitmap();
if (pixels != null)
    Console.WriteLine($"{pixels.Width}x{pixels.Height}, {pixels.Pixels.Length} bytes");
```

**Changed in 2.0:** these methods returned a native bitmap handle (`IntPtr`) that the caller had to destroy with `PDFium.FPDFBitmap_Destroy`. That function is no longer public, so they return managed pixels instead.

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

One `PdfForm` instance must not be used from two threads at once. Forms of different documents may be used from different threads at the same time; the wrapper serializes the native work.

A form belongs to the document that created it and is disposed with that document. Using a form after its document is disposed throws `ObjectDisposedException`.

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

Starts with an existing PDF from byte array. As with [`PdfDocument(byte[])`](#pdfdocumentbyte-data-string-password--null), the array is used in place and must not be modified until the merger is disposed.

#### PdfMerger(Stream pdfStream, string password = null)

Starts with an existing PDF from stream.

The stream is read from its current position to its end during construction and is left positioned at its end, with the same copy and temporary-file rules as [`PdfDocument(Stream)`](#pdfdocumentstream-pdfstream-string-password--null). The merger does not use the stream afterwards, so it can be closed, reset or reused immediately. (Before 2.0 a seekable stream had to stay open for the lifetime of the merger.)

Each constructor builds the merger on a private `PdfDocument` loaded through the matching `PdfDocument` constructor, so loading, error messages, saving and disposal behave exactly as they do for `PdfDocument`. A merger dropped without `Dispose()` is released by that document's finalizer.

### Thread Safety

One `PdfMerger` instance must not be used from two threads at once. Different mergers and documents may be used from different threads at the same time; the wrapper serializes the native work.

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

Both `Save` overloads serialize the document into a pooled in-memory buffer first and write to the file or stream afterwards, so peak memory includes the full output size. An exception thrown by `outputStream` (for example `IOException`) propagates unchanged; `InvalidOperationException` is thrown when PDFium fails to serialize the document.

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

Extracts all attachments to a directory, creating it if needed. Attachment names come from the document, so each file is named after the last path component of its attachment name (`/` and `\` separate components everywhere; on macOS `:` does too, because PDFium returns `/` in a name as `:` there), with characters the file system rejects replaced by `_`; nothing is written outside `outputDirectory`. Trailing dots and spaces are removed (Windows drops them), and a Windows device name such as `CON`, `NUL.txt` or `COM1.log` gets a leading `_`, on every platform. An attachment with no usable name (empty, `.`, `..`) is written as `attachment_N`, numbered from 1 by its position. Attachments whose names are then equal, ignoring case, are not overwritten: later ones get `_2`, `_3` before the extension (`report.txt`, `report_2.txt`).

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

---

## PdfiumRuntime

Process-wide coordination for the native PDFium library. PDFium allows one native call per process at a time, across all documents. Every wrapper operation enters this gate once, so callers do not need their own lock around PdfiumWrapper. Most applications never call this class directly.

### Declaration

```csharp
public static class PdfiumRuntime
```

### Behavior

- The first gate entry in a process initializes the native library. Any wrapper type can be the first one used.
- Finalizers of undisposed wrapper objects never call PDFium. They queue the native handles, and the next outermost gate entry on any thread closes them.
- Copies of the assembly loaded into different `AssemblyLoadContext`s share one gate.
- `AppContext.SetSwitch("PdfiumWrapper.Diagnostics", true)`, set before the first use of the library, enables internal counters used by the test suite and benchmarks. It is off by default.

### Members

| Member | Description |
|--------|-------------|
| `Enter()` | Enters the gate and returns a disposable `PdfiumRuntime.Scope`. Reentrant on the same thread. Dispose the scope exactly once, on the same thread. |
| `ReleasePending()` | Closes native handles left behind by finalized wrapper objects. This also happens on every outermost gate entry. |
| `Shutdown()` | Destroys the native library. Throws `InvalidOperationException` if any wrapper object is alive or releases are pending. The library initializes again on next use. Intended for tests and controlled host shutdown. |
| `IsHeldByCurrentThread` | `true` when the calling thread holds the gate. |
| `LiveHandleCount` | Number of native handles currently owned by wrapper objects (documents, pages, form environments, detached page objects, bitmaps being encoded), including handles waiting for deferred release. |

#### Enter()

Groups several wrapper calls into one uninterrupted native sequence. Wrapper members called inside the scope reenter the gate without waiting.

```csharp
using (PdfiumRuntime.Enter())
{
    // No other thread runs PDFium work between these calls
    document.Metadata.Title = "Report";
    document.Metadata.Author = "Finance";
}
```

Never hold a scope across `await`, `yield return`, or a call that may block (I/O, locks, user callbacks). While it is held, every other PdfiumWrapper caller in the process waits.

#### ReleasePending()

```csharp
// After dropping objects without Dispose(), once the GC has finalized them
PdfiumRuntime.ReleasePending();
```

#### Shutdown()

```csharp
document.Dispose();
PdfiumRuntime.Shutdown(); // throws if anything is still alive
```

---

## The PDFium Class in 2.0

**Breaking change:** the raw native imports on the `PDFium` class (192 functions such as `PDFium.FPDF_LoadDocument` or `PDFium.FPDF_RenderPageBitmap`) are `internal` in 2.0. A raw call bypassed the gate and was unsafe next to any other use of the library. There is no supported raw-call path in 2.0; functionality that is needed is exposed through the wrapper types.

The `PDFium` class itself stays public for its constants and structs, for example:

- `PDFium.FPDF_ANNOT`, `PDFium.FPDF_PRINTING` and the other render flags for `PdfPage.RenderToBytes`
- `PDFium.FPDF_INCREMENTAL` and the other save flags for `Save` / `SaveToStream`

---

## PdfProcessingPool (PdfiumWrapper.Processing)

Package `PdfiumWrapper.Processing`, namespace `PdfiumWrapper.Processing`. Runs PDF operations in a dynamically sized set of worker processes. Each worker has its own PDFium, so workers render in parallel, and a native failure in one costs that job rather than the process that owns the pool. Use it when one process is not enough (see [High-Throughput Processing](HIGH-THROUGHPUT-PROCESSING.md#worker-pool)); for a handful of documents the core library alone is simpler.

```csharp
public sealed class PdfProcessingPool : IAsyncDisposable
```

Create one pool per application and keep it for the application's lifetime. All members are thread-safe.

```csharp
using PdfiumWrapper.Processing;

await using var pool = await PdfProcessingPool.CreateAsync(new PdfPoolOptions
{
    MinWorkers = 2,
    MaxWorkers = 8,
});

var result = await pool.ConvertToPngAsync("invoice.pdf", "out/invoice", dpi: 150);
if (result.IsSuccess)
    Console.WriteLine($"{result.Value.PageCount} pages, {result.Value.Files.Count} files");
else
    Console.WriteLine($"{result.Status}: {result.Error}");
```

#### Hosting the workers

By default the pool starts **copies of your own executable** as workers. Make this the first statement of `Main`:

```csharp
public static async Task<int> Main(string[] args)
{
    if (PdfWorkerHost.TryRun())   // this process was started as a worker: it has run its loop and should exit
        return 0;

    // normal application start-up
}
```

Nothing has to be published per platform: the worker is your application, with your native libraries already in its output directory. Framework-dependent (`dotnet app.dll`) and self-contained deployments both work. Set `PdfPoolOptions.WorkerPath` to use a dedicated worker executable instead, for hosts whose `Main` cannot be changed.

#### Construction

| Member | Description |
|--------|-------------|
| `static Task<PdfProcessingPool> CreateAsync(PdfPoolOptions? options = null, CancellationToken ct = default)` | Creates the pool and starts `MinWorkers` workers, waiting until they are ready. Throws `PdfPoolException` if a worker cannot start. Preferred |
| `PdfProcessingPool(PdfPoolOptions? options = null)` | Creates the pool; `MinWorkers` start in the background and the first jobs wait for them |

#### Operations

Each call is one job on one worker. Inputs are `PdfInput` values; a `string` path converts implicitly.

| Method | Returns |
|--------|---------|
| `GetPageCountAsync(PdfInput input, CancellationToken ct = default)` | `Task<PdfJobResult<int>>` |
| `ConvertToPngAsync(PdfInput input, string outputDirectory, int dpi = 300, string fileNamePrefix = "page", CancellationToken ct = default)` | `Task<PdfJobResult<ImageFiles>>`: one `{prefix}_{page:D3}.png` per page |
| `ConvertToJpegAsync(PdfInput input, string outputDirectory, int quality = 90, int dpi = 300, string fileNamePrefix = "page", CancellationToken ct = default)` | `Task<PdfJobResult<ImageFiles>>` |
| `ConvertToTiffAsync(PdfInput input, string outputPath, int dpi = 200, TiffColorMode colorMode = TiffColorMode.Bilevel, byte threshold = 128, CancellationToken ct = default)` | `Task<PdfJobResult<TiffFile>>`: one multi-page file |
| `ExtractTextAsync(PdfInput input, CancellationToken ct = default)` | `Task<PdfJobResult<string[]>>`: one string per page |

Batch overloads take `IEnumerable<PdfInput>` and return `IAsyncEnumerable<PdfJobResult<T>>`, yielding results **in completion order** as they finish. Image and TIFF batches take an `outputRoot` and write each document to `{outputRoot}/{document name without extension}` (`.tiff` appended for TIFF); a name that repeats within the batch gets `-2`, `-3`, ... appended so documents never overwrite each other.

```csharp
await foreach (var r in pool.ConvertToPngAsync(files, "out", dpi: 150, ct: ct))
    Console.WriteLine($"{r.Input}: {r.Status} in {r.Timings.Total.TotalMilliseconds:F0} ms");
```

`PdfInput`:

| Member | Description |
|--------|-------------|
| `PdfInput.FromFile(string path, string? password = null)` | The worker reads the file directly |
| `PdfInput.FromBytes(byte[] bytes, string name, string? password = null)` | Written to a temporary file for the job; `name` is what the result reports |
| `PdfInput.FromStream(Stream stream, string name, string? password = null)` | Read to its end during submission; the stream may be closed afterwards |

Output files are written to a temporary name and renamed when complete, so a crashed or cancelled job leaves no half-written file.

#### Behavior

- **Backpressure:** at most `QueueCapacity` jobs wait for a worker; a submission beyond that awaits a slot. No exception, no unbounded growth.
- **Failures never fail the pool.** A worker that crashes (for example a native abort on a damaged PDF), hangs past `JobTimeout` or sends malformed data is killed and replaced. Its job is reported as `WorkerCrashed` or `TimedOut` and retried on a fresh worker up to `MaxAttempts`. A job the document itself rejects (bad file, wrong password) is `Failed` and not retried. Other jobs are unaffected.
- **Cancellation:** a cancelled job that has not started is dropped; one in flight is asked to stop between pages and its partial output removed. Either way the result is `Cancelled`.
- **`DisposeAsync`:** stops accepting jobs, cancels queued ones, lets in-flight ones finish (up to `JobTimeout`), then stops every worker. Submitting afterwards throws `ObjectDisposedException`.

#### Properties

| Property | Description |
|----------|-------------|
| `int Workers` | Workers alive, including ones still starting |
| `int BusyWorkers` | Workers with at least one job in flight |
| `int RunningJobs` | Jobs in flight across all workers |
| `int QueuedJobs` | Jobs waiting for a worker |
| `PdfPoolStatistics Statistics` | Counters since creation (see below) |

### PdfPoolOptions

Validated when the pool is created (`ArgumentOutOfRangeException`, or `PdfPoolException` for a missing `WorkerPath`).

| Property | Default | Description |
|----------|---------|-------------|
| `MinWorkers` | 1 | Workers kept alive and warm. Started when the pool is created |
| `MaxWorkers` | `DefaultMaxWorkers` (half the logical processors, at least 1) | Upper bound. A worker is one core, so beyond the fast cores extra workers add memory and little throughput |
| `JobsPerWorker` | 2 | Jobs a worker runs at once. PDFium serializes rendering inside a process, so a second job lets one document encode and write while the other renders. More mostly adds memory (one rendered page per job in flight) |
| `ScaleUpAfter` | 500 ms | A worker is added when every slot of every worker has been busy and jobs have been waiting for this long. A burst reaches `MaxWorkers` in seconds; a single stray job never starts a process |
| `IdleTimeout` | 60 s | A worker idle for this long is stopped, down to `MinWorkers` |
| `JobTimeout` | 2 min | Per attempt. On expiry the worker is killed and replaced |
| `MaxAttempts` | 2 | Attempts for a job whose worker crashed or timed out |
| `QueueCapacity` | 1,000 | Jobs that may wait. Submissions beyond this wait for a slot |
| `MaxWorkerMemoryBytes` | null | When set, a worker whose working set exceeds this after a job is retired and replaced |
| `WorkerStartTimeout` | 30 s | Time a new worker gets to report ready |
| `WorkerPath` | null | Executable (or `.dll`, run through `dotnet`) to start as a worker. Null re-launches this process |
| `WorkerArguments` | empty | Arguments for `WorkerPath` |
| `WorkerEnvironment` | empty | Extra environment variables for workers |
| `TempDirectory` | system temp | Where spooled inputs and large text results go |

Fixed size is `MinWorkers == MaxWorkers`.

### PdfJobResult

```csharp
public sealed record PdfJobResult<T>(string Input, PdfJobStatus Status, T? Value, string? Error,
    int Attempts, int WorkerPid, PdfJobTimings Timings)
{
    public bool IsSuccess { get; }   // Status == Succeeded
}
```

| `PdfJobStatus` | Meaning |
|---|---|
| `Succeeded` | `Value` is set |
| `Failed` | The document or request was rejected (bad file, wrong password, missing output path). Not retried |
| `TimedOut` | The last attempt exceeded `JobTimeout` |
| `Cancelled` | Cancelled by the caller, or the pool was disposed while the job waited |
| `WorkerCrashed` | The worker process died during the last attempt |

`PdfJobTimings` has `Queued` (submission to dispatch), `Processing` (dispatch to result, last attempt) and `Total`. `ImageFiles` is `(int PageCount, IReadOnlyList<string> Files, long TotalBytes)`; `TiffFile` is `(int PageCount, string Path, long Bytes)`.

### PdfWorkerHost

```csharp
public static class PdfWorkerHost
{
    public const string EnvironmentVariable = "PDFIUMWRAPPER_WORKER";   // "1" in a worker process
    public const string DiagnosticsVariable = "PDFIUMWRAPPER_DIAGNOSTICS"; // "1" turns on wrapper diagnostics in a worker (tests, measurements)
    public static bool TryRun();
}
```

`TryRun()` returns `false` immediately in a normal process. In a worker it runs the job loop until the pool shuts it down and then returns `true`; the process should exit. A worker writes protocol frames to its standard output, so nothing else in the process may write there; calling `TryRun()` first in `Main` guarantees that.

### Events and Statistics

```csharp
pool.Events += (sender, e) => logger.LogInformation("{Kind} worker={Pid} job={JobId} {Detail}", e.Kind, e.WorkerPid, e.JobId, e.Detail);
```

`PdfPoolEventKind`: `WorkerStarting`, `WorkerReady`, `WorkerStopped`, `WorkerCrashed`, `WorkerStartFailed`, `WorkerRetiredForMemory`, `ScaledUp`, `ScaledDown`, `JobDispatched`, `JobCompleted`, `JobFailed`, `JobTimedOut`, `JobRetried`, `JobCancelled`, `QueueFull`. A worker's standard error arrives as `WorkerStopped` events whose detail starts with `stderr:`. Handlers run on pool threads and must be quick and must not throw.

`PdfPoolStatistics` counts jobs submitted, succeeded, failed, timed out, cancelled, crashed and retried, and workers started, stopped, crashed and retired for memory, plus scale-ups and scale-downs.
