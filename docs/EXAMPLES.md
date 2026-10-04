# Examples

This document provides code examples for common scenarios using PdfiumWrapper.

## Table of Contents

- [Loading Documents and Handling Errors](#loading-documents-and-handling-errors)
- [PDF Creation](#pdf-creation)
- [PDF Rendering](#pdf-rendering)
- [Cancellation](#cancellation)
- [PDF Merging](#pdf-merging)
- [Form Filling](#form-filling)
- [Text Extraction](#text-extraction)
- [Metadata](#metadata)
- [Document Properties](#document-properties)
- [Bookmarks](#bookmarks)
- [Attachments](#attachments)
- [Thumbnails and Embedded Images](#thumbnails-and-embedded-images)
- [Advanced Scenarios](#advanced-scenarios)

---

## Loading Documents and Handling Errors

### Open a File, Stream or Byte Array

```csharp
using PdfiumWrapper;

using var fromFile = new PdfDocument("document.pdf");
using var protectedFile = new PdfDocument("secure.pdf", password: "secret123");

// The stream is read to its end during construction; the document does not keep it
using var input = File.OpenRead("document.pdf");
using var fromStream = new PdfDocument(input);

// The array is used in place and pinned: do not modify it until the document is disposed
byte[] data = File.ReadAllBytes("document.pdf");
using var fromBytes = new PdfDocument(data);
```

### Tell a Wrong Password from a Broken File

When PDFium cannot open a document, the constructor throws `PdfiumException`. It derives from `InvalidOperationException`, and its `ErrorCode` says why:

| `PdfiumErrorCode` | Meaning |
|-------------------|---------|
| `File` | The file was not found or could not be opened |
| `Format` | The input is not a PDF or is corrupted |
| `Password` | The document is encrypted and the password is missing or wrong |
| `Security` | The document uses a security handler PDFium does not support |
| `Page` | A page was not found or its content is broken |
| `Unknown` | PDFium gave no specific reason |

```csharp
try
{
    using var document = new PdfDocument("upload.pdf");
    Console.WriteLine($"{document.PageCount} pages");
}
catch (PdfiumException ex) when (ex.ErrorCode == PdfiumErrorCode.Password)
{
    Console.WriteLine("The PDF is password protected");
}
catch (PdfiumException ex)
{
    Console.WriteLine($"Cannot open the PDF ({ex.ErrorCode}): {ex.Message}");
}
```

### Retry with a Password

```csharp
static PdfDocument OpenWithPasswords(string path, IEnumerable<string> candidates)
{
    try
    {
        return new PdfDocument(path);
    }
    catch (PdfiumException ex) when (ex.ErrorCode == PdfiumErrorCode.Password)
    {
        // Encrypted: try the passwords below
    }

    foreach (var password in candidates)
    {
        try
        {
            return new PdfDocument(path, password);
        }
        catch (PdfiumException ex) when (ex.ErrorCode == PdfiumErrorCode.Password)
        {
            // Wrong password: try the next one
        }
    }

    throw new UnauthorizedAccessException($"No known password opens {path}.");
}
```

`PdfMerger` constructors and the `PdfMerger` methods that take a file path or bytes load a document too, so they throw `PdfiumException` the same way. Other failures use the usual .NET exceptions:

- `ArgumentException` and `ArgumentOutOfRangeException` for invalid arguments, such as a page index out of range or a DPI that is zero or negative.
- `InvalidOperationException` for a render that would exceed the pixel limit, a failed save, or a failed page import.
- `ObjectDisposedException` for a disposed object.

---

## PDF Creation

Creating documents and adding text, images and shapes is covered in the [PDF Editing Guide](PDF-EDITING.md):

- [Simple document](PDF-EDITING.md#example-1-simple-document)
- [Document with image](PDF-EDITING.md#example-2-document-with-image)
- [Multi-page document](PDF-EDITING.md#example-3-multi-page-document)
- [Shapes and graphics](PDF-EDITING.md#example-4-shapes-and-graphics)
- [Invoice layout](PDF-EDITING.md#example-5-invoice-layout)
- [Adding content to an existing PDF](PDF-EDITING.md#editing-existing-documents)
- [Reading and removing page objects](PDF-EDITING.md#reading-page-objects)

---

## PDF Rendering

### Defaults and Rules

- **DPI** defaults to 300 for PNG and JPEG output and to 200 for TIFF. It must be positive; zero or a negative value throws `ArgumentOutOfRangeException`.
- **JPEG quality** defaults to 90 everywhere. PNG output ignores the `quality` argument.
- **Render limit.** Each rendered page is limited to 268,435,456 pixels by default (for example 16,384 x 16,384). A larger page throws `InvalidOperationException`; lower the DPI, or raise the limit with the `PdfiumWrapper.MaxRenderPixels` AppContext data key (see [Troubleshooting](TROUBLESHOOTING.md)).
- **File names.** Directory exports write `{prefix}_{n:D3}.png` or `.jpg` (`page_001.png`, ...), creating the directory if needed.
- **Output files** are opened by .NET, so any path .NET accepts works, including non-ASCII paths on Windows.
- **Annotations** are drawn by all document-level render methods.

### Convert All Pages to PNG Images

```csharp
using PdfiumWrapper;

using var document = new PdfDocument("document.pdf");

document.SaveAsPngs("output_folder", fileNamePrefix: "page", dpi: 300);
// Creates: output_folder/page_001.png, output_folder/page_002.png, ...
```

### Convert to JPEG with Quality Control

```csharp
using var document = new PdfDocument("document.pdf");

// 85% quality at 200 DPI (default quality is 90)
document.SaveAsJpegs("output_folder", fileNamePrefix: "scan", quality: 85, dpi: 200);
```

### Choose the Format at Run Time

`SaveAsImages` writes PNG or JPEG. TIFF is a multi-page format with its own method, `SaveAsTiff`; passing `ImageFormat.Tiff` to `SaveAsImages` throws `ArgumentOutOfRangeException`.

```csharp
using var document = new PdfDocument("document.pdf");

ImageFormat format = ImageFormat.Jpeg; // or ImageFormat.Png
document.SaveAsImages(
    outputDirectory: "output_folder",
    fileNamePrefix: "page",
    format: format,
    quality: 90,      // JPEG only; ignored for PNG
    dpiWidth: 150,
    dpiHeight: 150
);
```

### Stream Images as Byte Arrays

```csharp
using var document = new PdfDocument("document.pdf");

// One page at a time: only one page's bytes are in memory at any point
int pageNumber = 0;
foreach (var bytes in document.StreamImageBytes(ImageFormat.Png, dpi: 150))
{
    File.WriteAllBytes($"page_{++pageNumber}.png", bytes);
}

// Async version
pageNumber = 0;
await foreach (var bytes in document.StreamImageBytesAsync(ImageFormat.Jpeg, quality: 85, dpi: 200))
{
    await File.WriteAllBytesAsync($"page_{++pageNumber}.jpg", bytes);
}
```

### Save as Multi-Page TIFF

TIFF pages are rendered in 8-bit gray, then written as bilevel or grayscale.

```csharp
using var document = new PdfDocument("document.pdf");

// Bilevel CCITT G4 (default): ideal for scanned documents
document.SaveAsTiff("output.tiff", dpi: 200);

// Grayscale LZW
document.SaveAsTiff("output_gray.tiff", dpi: 200, colorMode: TiffColorMode.Grayscale);

// Write to a stream; it must be writable and seekable
using var stream = new MemoryStream();
document.SaveAsTiff(stream, dpi: 200);
```

If writing a TIFF file fails, the partly written file is deleted.

### Get Raw Bitmaps for Custom Processing

`RenderPages` returns every page at once (a US Letter page at 300 DPI is about 32 MiB), so use it for short documents or low DPI. The pixels are BGRx: blue, green, red, then a fourth byte that is not alpha.

```csharp
using PdfiumWrapper;

using var document = new PdfDocument("document.pdf");
RawBitmap[] bitmaps = document.RenderPages(dpi: 150);

foreach (var bitmap in bitmaps)
{
    byte[] pixels = bitmap.Pixels;
    int width = bitmap.Width;
    int height = bitmap.Height;
    int stride = bitmap.Stride;   // bytes per row

    for (int y = 0; y < height; y++)
    {
        for (int x = 0; x < width; x++)
        {
            int offset = y * stride + x * 4;
            byte b = pixels[offset];
            byte g = pixels[offset + 1];
            byte r = pixels[offset + 2];
            // pixels[offset + 3] is unused
        }
    }
}
// No disposal needed: RawBitmap is a record holding a managed array
```

### Async Rendering

The async methods render on the thread pool and wait for the native gate without blocking a thread, so a UI stays responsive. Your own `await` still resumes on your context as usual.

```csharp
using var document = new PdfDocument("large_document.pdf");

RawBitmap[] bitmaps = await document.RenderPagesAsync(dpi: 150);

// Or write files directly
await document.SaveAsImagesAsync("output_folder", "page", ImageFormat.Png, dpi: 300);
```

The delegate passed to `ProcessAllPagesAsync` runs on a thread-pool thread, so it must not touch UI objects.

### Render a Single Page

`PdfPage.RenderToBytes(width, height, flags)` renders one page into a bitmap of exactly that size, stretching the page if the aspect ratio differs. It returns BGRx pixels, rows of `width * 4` bytes. With the default `flags` of 0 annotations are not drawn; pass `PDFium.FPDF_ANNOT` to draw them as the document-level methods do.

```csharp
using var document = new PdfDocument("document.pdf");
using var page = document.GetPage(0); // First page

// Keep the aspect ratio: size the bitmap from the page size in points
const double dpi = 150;
int width = (int)Math.Round(page.Width / 72.0 * dpi);
int height = (int)Math.Round(page.Height / 72.0 * dpi);

byte[] pixels = page.RenderToBytes(width, height, PDFium.FPDF_ANNOT);
```

### Different DPI for Width and Height

```csharp
using var document = new PdfDocument("document.pdf");

// Non-uniform DPI (rare use case)
var bitmaps = document.RenderPages(dpiWidth: 300, dpiHeight: 150);
```

---

## Cancellation

Every async method of `PdfDocument` can be cancelled.

- **Task-returning methods** take an optional `CancellationToken` as their last parameter: `RenderPagesAsync`, `SaveAsPngsAsync`, `SaveAsJpegsAsync`, `SaveAsImagesAsync`, `SaveAsTiffAsync` and `ProcessAllPagesAsync`.
- **Streaming methods** (`StreamImageBytesAsync`, `StreamJpegBytesAsync`) return `IAsyncEnumerable<byte[]>`. Pass the token with `WithCancellation`.

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
var ct = cts.Token;

using var document = new PdfDocument("document.pdf");

await document.SaveAsPngsAsync("output_folder", "page", dpi: 150, cancellationToken: ct);
await document.SaveAsTiffAsync("output.tiff", dpi: 200, cancellationToken: ct);
RawBitmap[] bitmaps = await document.RenderPagesAsync(dpi: 150, cancellationToken: ct);
string[] texts = await document.ProcessAllPagesAsync(page => page.ExtractText(), ct);

int pageNumber = 0;
await foreach (var bytes in document.StreamImageBytesAsync(ImageFormat.Jpeg, dpi: 200).WithCancellation(ct))
{
    await File.WriteAllBytesAsync($"page_{++pageNumber}.jpg", bytes, ct);
}
```

The token is checked before each page and while waiting for the native gate, and cancellation throws `OperationCanceledException`. A page that is already rendering finishes first, because a native render cannot be interrupted. What a cancelled export leaves behind:

| Method | After cancellation |
|--------|--------------------|
| `SaveAsTiffAsync(path, ...)` | The partly written file is deleted |
| `SaveAsTiffAsync(stream, ...)` | The stream holds an incomplete TIFF |
| `SaveAsPngsAsync`, `SaveAsJpegsAsync`, `SaveAsImagesAsync` | Pages written before the cancellation are kept |

The synchronous methods take no token.

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

A wrong password throws `PdfiumException` with `ErrorCode == PdfiumErrorCode.Password`.

### Extract Specific Pages

```csharp
using var merger = new PdfMerger();
using var source = new PdfDocument("large_document.pdf");

// Pages 1, 3 and 5-10 (1-based page numbers)
merger.AppendPages(source, "1,3,5-10");
merger.Save("selected_pages.pdf");
```

### Extract Pages by Index

```csharp
using var merger = new PdfMerger();
using var source = new PdfDocument("document.pdf");

// First, fifth and last pages (0-based indices)
int lastPageIndex = source.PageCount - 1;
merger.AppendPages(source, new[] { 0, 4, lastPageIndex });
merger.Save("extracted.pdf");
```

### Insert Pages at Specific Position

```csharp
using var merger = new PdfMerger("main_document.pdf");
using var coverPage = new PdfDocument("cover.pdf");
using var appendix = new PdfDocument("appendix.pdf");

// Insert the cover at the beginning
merger.InsertDocument(coverPage, insertAtIndex: 0);

// Append adds at the end
merger.AppendDocument(appendix);

merger.Save("complete_document.pdf");
```

### Delete Pages

```csharp
using var merger = new PdfMerger("document.pdf");

// Delete a single page (0-based index)
merger.DeletePage(0);

// Delete several pages; the indices refer to the document before the call
merger.DeletePages(new[] { 1, 3, 5 });

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

await File.WriteAllBytesAsync("merged.pdf", mergedPdf);
```

### Copy Viewer Preferences

```csharp
using var merger = new PdfMerger();
using var sourceWithPrefs = new PdfDocument("source_with_zoom.pdf");

merger.AppendDocument("document.pdf");
merger.CopyViewerPreferences(sourceWithPrefs); // zoom, layout settings

merger.Save("with_preferences.pdf");
```

---

## Form Filling

`GetForm()` returns `null` when the document has no form fields. The form belongs to its document: use it while the document is open. It is disposed with the document, and `using` disposes it earlier.

### List All Form Fields

```csharp
using var document = new PdfDocument("form.pdf");
using var form = document.GetForm();

if (form == null)
{
    Console.WriteLine("This PDF has no form fields");
    return;
}

foreach (var field in form.GetAllFormFields())
{
    Console.WriteLine($"Field: {field.Name}");
    Console.WriteLine($"  Type: {field.Type}");
    Console.WriteLine($"  Value: {field.Value}");
    Console.WriteLine($"  Page: {field.PageIndex + 1}");
    Console.WriteLine($"  Required: {field.IsRequired}");
    Console.WriteLine($"  ReadOnly: {field.IsReadOnly}");

    if (field.Options.Count > 0)
    {
        Console.WriteLine($"  Options: {string.Join(", ", field.Options)}");
    }
}
```

`form.GetFormFieldsOnPage(pageIndex)` lists the fields of one page.

### Fill Text Fields

```csharp
using var document = new PdfDocument("application_form.pdf");
using var form = document.GetForm();

if (form != null)
{
    form.SetFormFieldValue("FirstName", "John");
    form.SetFormFieldValue("LastName", "Doe");
    form.SetFormFieldValue("Email", "john.doe@example.com");
    form.SetFormFieldValue("Phone", "555-123-4567");
    form.SetFormFieldValue("Address", "123 Main Street\nAnytown, USA 12345");

    document.Save("filled_application.pdf");
}
```

### Work with Checkboxes

```csharp
using var document = new PdfDocument("consent_form.pdf");
using var form = document.GetForm();

if (form != null)
{
    form.SetFormFieldChecked("AgreeToTerms", true);
    form.SetFormFieldChecked("ReceiveNewsletter", false);

    bool hasAgreed = form.GetFormFieldChecked("AgreeToTerms");
    Console.WriteLine($"User agreed to terms: {hasAgreed}");

    document.Save("signed_consent.pdf");
}
```

### Work with Dropdowns (Combo Boxes)

```csharp
using var document = new PdfDocument("registration.pdf");
using var form = document.GetForm();

if (form != null)
{
    form.SetFormFieldValue("Country", "United States");
    form.SetFormFieldValue("State", "California");

    document.Save("completed_registration.pdf");
}
```

### Work with List Boxes

```csharp
using var document = new PdfDocument("preferences.pdf");
using var form = document.GetForm();

if (form != null)
{
    // Single selection
    form.SetListBoxSelection("PrimaryLanguage", "English");

    // Several selections in a multi-select list box
    form.SetListBoxSelections("Skills", new[] { "C#", "JavaScript", "SQL", "Azure" });

    document.Save("preferences_filled.pdf");
}
```

### Fill Form from Dictionary

A field name that does not exist throws `ArgumentException`.

```csharp
using var document = new PdfDocument("form.pdf");
using var form = document.GetForm();

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

    document.Save("filled_form.pdf");
}
```

### Fill Form from JSON

```csharp
using System.Text.Json;

string json = await File.ReadAllTextAsync("form_data.json");
var data = JsonSerializer.Deserialize<FormData>(json);

using var document = new PdfDocument("form.pdf");
using var form = document.GetForm();

if (form != null && data != null)
{
    form.SetFormFieldValue("FullName", data.FullName);
    form.SetFormFieldValue("Email", data.Email);
    form.SetFormFieldValue("Phone", data.Phone);
    form.SetFormFieldChecked("AgreeToTerms", data.AgreeToTerms);
    form.SetFormFieldValue("Country", data.Country);

    document.Save("completed.pdf");
}

public record FormData(
    string FullName,
    string Email,
    string Phone,
    bool AgreeToTerms,
    string Country
);
```

---

## Text Extraction

### Extract Text from All Pages

`ProcessAllPages` loads, processes and disposes one page at a time:

```csharp
using var document = new PdfDocument("document.pdf");

string[] pageTexts = document.ProcessAllPages(page => page.ExtractText());

for (int i = 0; i < pageTexts.Length; i++)
{
    Console.WriteLine($"=== Page {i + 1} ===");
    Console.WriteLine(pageTexts[i]);
}

// Async: waits for the native gate without blocking a thread
string[] texts = await document.ProcessAllPagesAsync(page => page.ExtractText());
```

### Extract Text from a Specific Page

```csharp
using var document = new PdfDocument("document.pdf");

// Page 5 (0-based index 4)
using var page = document.GetPage(4);
Console.WriteLine(page.ExtractText());
```

### Search for Text in a PDF

```csharp
using var document = new PdfDocument("document.pdf");

string searchTerm = "important";
string[] pageTexts = document.ProcessAllPages(page => page.ExtractText());

for (int i = 0; i < pageTexts.Length; i++)
{
    string text = pageTexts[i];
    int index = text.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase);
    if (index < 0)
        continue;

    int start = Math.Max(0, index - 50);
    int length = Math.Min(text.Length - start, 100 + searchTerm.Length);
    Console.WriteLine($"Page {i + 1}: ...{text.Substring(start, length).Trim()}...");
}
```

### Export Text to a File

```csharp
using var document = new PdfDocument("document.pdf");
using var writer = new StreamWriter("extracted_text.txt");

document.ProcessAllPages(page =>
{
    writer.WriteLine($"--- Page {page.PageIndex + 1} ---");
    writer.WriteLine(page.ExtractText());
    writer.WriteLine();
});
```

---

## Metadata

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
Console.WriteLine($"PDF Version: {metadata.PdfVersionString}");   // "1.7"; metadata.PdfVersion is 17

if (metadata.CreationDateTime.HasValue)
    Console.WriteLine($"Created: {metadata.CreationDateTime.Value:yyyy-MM-dd HH:mm:ss}");

if (metadata.ModificationDateTime.HasValue)
    Console.WriteLine($"Modified: {metadata.ModificationDateTime.Value:yyyy-MM-dd HH:mm:ss}");
```

Missing entries are empty strings. `CreationDate` and `ModificationDate` hold the raw PDF date strings. `CreationDateTime` and `ModificationDateTime` parse them: the result is in UTC when the date carries a time zone, and `DateTimeKind.Unspecified` when it does not.

### Get Metadata as a Dictionary

```csharp
using var document = new PdfDocument("document.pdf");
Dictionary<string, string> allMetadata = document.Metadata.GetAllMetadata();

foreach (var (key, value) in allMetadata)
{
    if (!string.IsNullOrEmpty(value))
        Console.WriteLine($"{key}: {value}");
}
```

The keys are `Title`, `Author`, `Subject`, `Keywords`, `Creator`, `Producer`, `CreationDate`, `ModificationDate`, `Trapped` and `FileVersion`. For a custom Info dictionary entry, use `document.Metadata.GetMetadataString("MyKey")`.

### Writing Metadata

Metadata is read-only. PDFium has no function to write the Info dictionary, so `PdfMetadata` has no setters (the 1.x setters always failed with `EntryPointNotFoundException`). To change metadata, edit the saved PDF with a library that writes PDF objects.

---

## Document Properties

### Check Permissions

`Permissions` is a `[Flags]` enum, `PdfPermissions`. An unencrypted document reports every permission.

```csharp
using var document = new PdfDocument("sample.pdf");
PdfPermissions permissions = document.Permissions;

if (permissions.HasFlag(PdfPermissions.Print))
    Console.WriteLine("Printing is allowed");

if (permissions.HasFlag(PdfPermissions.ModifyContents))
    Console.WriteLine("Modifying is allowed");

// HasFlag with several flags is true only when all of them are set
if (permissions.HasFlag(PdfPermissions.Print | PdfPermissions.CopyContents))
    Console.WriteLine("Can print and copy");
```

| Flag | Allows |
|------|--------|
| `Print` | Printing |
| `PrintHighQuality` | Printing at full quality |
| `ModifyContents` | Changing the document's contents |
| `CopyContents` | Copying or extracting text and graphics |
| `ExtractForAccessibility` | Extracting text and graphics for accessibility |
| `ModifyAnnotations` | Adding or changing annotations and form fields |
| `FillForms` | Filling in form fields |
| `AssembleDocument` | Inserting, rotating or deleting pages, creating bookmarks and thumbnails |

`(uint)document.Permissions` gives the raw permission bits.

### Get the Document ID

`DocumentId` is the original file identifier from the trailer's `/ID` entry, as uppercase hex. It is 32 characters for the usual 16-byte ID. It is `null` when the file has none.

```csharp
using var document = new PdfDocument("sample.pdf");

string? docId = document.DocumentId;
Console.WriteLine(docId != null ? $"Document ID: {docId}" : "No document ID");
```

### Get Page Labels

Page labels are the page numbers a viewer shows, for example "i", "ii" for front matter and "1", "2" for the body. `GetPageLabel` returns `null` for a page without a label, and throws `ArgumentOutOfRangeException` for an index outside the document.

```csharp
using var document = new PdfDocument("book.pdf");

string? first = document.GetPageLabel(0);
Console.WriteLine($"First page label: {first ?? "(none)"}");

string?[] labels = document.GetAllPageLabels();
for (int i = 0; i < labels.Length; i++)
{
    Console.WriteLine($"Page index {i}: {labels[i] ?? "(none)"}");
}
```

Labels can use any of the PDF numbering styles, with an optional prefix:

- Decimal: 1, 2, 3, ...
- Roman numerals: i, ii, iii, ... or I, II, III, ...
- Letters: a, b, c, ... or A, B, C, ...
- Prefixed: "A-1", "A-2", "Appendix-1", ...

```
Page index 0: "i"     (front matter)
Page index 1: "ii"
Page index 2: "1"     (body)
Page index 3: "2"
Page index 4: "A-1"   (appendix)
```

### Find a Page by Its Label

```csharp
using var document = new PdfDocument("book.pdf");

int index = Array.IndexOf(document.GetAllPageLabels(), "A-1");
if (index >= 0)
{
    using var page = document.GetPage(index);
    Console.WriteLine(page.ExtractText());
}
```

---

## Bookmarks

`PageIndex` is the 0-based target page, or `null` when a bookmark has no destination or it cannot be resolved.

### Read Bookmark Hierarchy

```csharp
using var document = new PdfDocument("document.pdf");
List<PdfBookmark> bookmarks = document.Bookmarks.GetAllBookmarks();

void PrintBookmarks(List<PdfBookmark> items, int level = 0)
{
    string indent = new string(' ', level * 2);

    foreach (var bookmark in items)
    {
        string target = bookmark.PageIndex is int index ? $"page {index + 1}" : "no target";
        Console.WriteLine($"{indent}{bookmark.Title} -> {target}");

        if (bookmark.Children.Count > 0)
        {
            PrintBookmarks(bookmark.Children, level + 1);
        }
    }
}

PrintBookmarks(bookmarks);
```

### Generate a Table of Contents

```csharp
using System.Text;

using var document = new PdfDocument("book.pdf");
var bookmarks = document.Bookmarks.GetAllBookmarks();

var toc = new StringBuilder();
toc.AppendLine("# Table of Contents");
toc.AppendLine();

void AddToToc(List<PdfBookmark> items, int level)
{
    foreach (var bookmark in items)
    {
        string prefix = new string('#', level + 1);
        string target = bookmark.PageIndex is int index ? $" (Page {index + 1})" : "";
        toc.AppendLine($"{prefix} {bookmark.Title}{target}");

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

Attachment names come from the PDF and may contain paths such as `..\..\file.exe`. Never use a name as a path as it is.

### List Attachments

```csharp
using var document = new PdfDocument("document_with_attachments.pdf");
var attachments = document.Attachments;

Console.WriteLine($"Found {attachments.Count} attachment(s)");

foreach (var attachment in attachments.GetAllAttachments())
{
    Console.WriteLine($"  {attachment.Name ?? "(unnamed)"} ({attachment.Size:N0} bytes)");
}
```

### Extract All Attachments

`ExtractAll` is the safe way to write attachments to disk:

- Each file is named after the last path component of its attachment name, with characters the file system rejects replaced, so nothing is written outside the directory.
- An attachment without a usable name is written as `attachment_N`.
- Names that collide get `_2`, `_3`, ... before the extension.

```csharp
using var document = new PdfDocument("document.pdf");

if (document.Attachments.Count > 0)
{
    document.Attachments.ExtractAll("extracted_attachments");
}
```

### Extract a Single Attachment

`Name` and `Data` are nullable. Sanitize the name before writing:

```csharp
using var document = new PdfDocument("document.pdf");

PdfAttachment? attachment = document.Attachments.Count > 0 ? document.Attachments.GetAttachment(0) : null;
if (attachment?.Data is byte[] data)
{
    Directory.CreateDirectory("extracted");
    string path = Path.Combine("extracted", SafeFileName(attachment.Name));
    await File.WriteAllBytesAsync(path, data);
    Console.WriteLine($"Extracted: {path}");
}

static string SafeFileName(string? name)
{
    // Keep only the last path component, whichever separator the PDF used
    string fileName = Path.GetFileName((name ?? string.Empty).Replace('\\', '/'));
    foreach (char c in Path.GetInvalidFileNameChars())
        fileName = fileName.Replace(c, '_');
    return fileName is "" or "." or ".." ? "attachment.bin" : fileName;
}
```

---

## Thumbnails and Embedded Images

### Embedded Page Thumbnails

Some PDFs store a small preview image per page. `GetEmbeddedThumbnail` returns it as BGRA pixels, or `null` when the page has none or it cannot be decoded:

```csharp
using var document = new PdfDocument("document.pdf");
using var page = document.GetPage(0);

if (page.HasEmbeddedThumbnail)
{
    RawBitmap? thumbnail = page.GetEmbeddedThumbnail();
    if (thumbnail != null)
        Console.WriteLine($"Thumbnail: {thumbnail.Width} x {thumbnail.Height}");
}
```

`GetEmbeddedThumbnailBytes()` and `GetEmbeddedThumbnailSize()` are obsolete; `GetEmbeddedThumbnail()` returns the pixels, size and stride from one decode.

When there is no embedded thumbnail, render the first page at a low DPI instead:

```csharp
using var document = new PdfDocument("document.pdf");

// The stream is lazy: First() renders only the first page
byte[] preview = document.StreamImageBytes(ImageFormat.Jpeg, quality: 75, dpi: 36).First();
```

### Images on a Page

`PdfImageObject.GetBitmap()` returns an image's own pixels as `RawBitmap?`. See [Reading Page Objects](PDF-EDITING.md#reading-page-objects).

---

## Advanced Scenarios

### Batch PDF Processing

```csharp
public static async Task BatchConvertPdfsToImagesAsync(
    string inputFolder, string outputFolder, int dpi = 150, CancellationToken ct = default)
{
    foreach (var pdfFile in Directory.GetFiles(inputFolder, "*.pdf"))
    {
        string fileName = Path.GetFileNameWithoutExtension(pdfFile);
        string pdfOutputFolder = Path.Combine(outputFolder, fileName);

        try
        {
            using var document = new PdfDocument(pdfFile);
            await document.SaveAsPngsAsync(pdfOutputFolder, "page", dpi, ct);
            Console.WriteLine($"{fileName}: converted {document.PageCount} pages");
        }
        catch (PdfiumException ex)
        {
            Console.WriteLine($"{fileName}: cannot open ({ex.ErrorCode})");
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"{fileName}: {ex.Message}");   // for example the render limit
        }
    }
}
```

For many files at once, see [High-Throughput Processing](HIGH-THROUGHPUT-PROCESSING.md).

### PDF Processing Pipeline

```csharp
public class PdfProcessor
{
    public ProcessingResult Process(Stream pdfStream)
    {
        using var document = new PdfDocument(pdfStream);

        var result = new ProcessingResult
        {
            PageCount = document.PageCount,
            Metadata = document.Metadata.GetAllMetadata(),
            FullText = string.Join(Environment.NewLine, document.ProcessAllPages(page => page.ExtractText())),

            // Thumbnail of the first page only: the stream is lazy
            ThumbnailBytes = document.StreamImageBytes(ImageFormat.Jpeg, quality: 75, dpi: 72).First(),

            Bookmarks = document.Bookmarks.GetAllBookmarks()
                .Select(b => b.Title ?? string.Empty)
                .ToList()
        };

        using var form = document.GetForm();
        if (form != null)
        {
            result.FormFields = form.GetAllFormFields()
                .Select(f => new FormFieldInfo(f.Name, f.Type.ToString(), f.Value))
                .ToList();
        }

        return result;
    }
}

public class ProcessingResult
{
    public int PageCount { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
    public string FullText { get; set; } = string.Empty;
    public byte[] ThumbnailBytes { get; set; } = Array.Empty<byte>();
    public List<FormFieldInfo> FormFields { get; set; } = new();
    public List<string> Bookmarks { get; set; } = new();
}

public record FormFieldInfo(string? Name, string Type, string? Value);
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
        await file.CopyToAsync(stream, HttpContext.RequestAborted);
        stream.Position = 0;

        try
        {
            using var document = new PdfDocument(stream);
            using var form = document.GetForm();

            return Ok(new
            {
                FileName = file.FileName,
                PageCount = document.PageCount,
                Metadata = document.Metadata.GetAllMetadata(),
                HasForm = form != null,
                AttachmentCount = document.Attachments.Count,
                BookmarkCount = document.Bookmarks.GetAllBookmarks().Count
            });
        }
        catch (PdfiumException ex) when (ex.ErrorCode == PdfiumErrorCode.Password)
        {
            return BadRequest("The PDF is password protected");
        }
        catch (PdfiumException ex)
        {
            return BadRequest($"Invalid PDF ({ex.ErrorCode})");
        }
    }

    [HttpPost("thumbnail")]
    public async Task<IActionResult> GetThumbnail(IFormFile file, [FromQuery] int dpi = 72)
    {
        if (file == null || file.Length == 0)
            return BadRequest("No file provided");

        dpi = Math.Clamp(dpi, 18, 300);

        using var stream = new MemoryStream();
        await file.CopyToAsync(stream, HttpContext.RequestAborted);
        stream.Position = 0;

        try
        {
            using var document = new PdfDocument(stream);

            // First page only; stops rendering if the client disconnects
            await foreach (var jpeg in document.StreamImageBytesAsync(ImageFormat.Jpeg, quality: 80, dpi: dpi)
                               .WithCancellation(HttpContext.RequestAborted))
            {
                return File(jpeg, "image/jpeg");
            }

            return NoContent();
        }
        catch (PdfiumException ex)
        {
            return BadRequest($"Invalid PDF ({ex.ErrorCode})");
        }
    }
}
```

The controller needs `using Microsoft.AspNetCore.Mvc;`. For untrusted uploads at volume, see [Isolate Untrusted PDFs in Worker Processes](#isolate-untrusted-pdfs-in-worker-processes).

### Generate a PDF Report from Multiple Sources

```csharp
public byte[] GenerateReport(string coverPagePath, string[] contentPaths, string appendixPath)
{
    using var merger = new PdfMerger();

    merger.AppendDocument(coverPagePath);

    foreach (var contentPath in contentPaths)
    {
        merger.AppendDocument(contentPath);
    }

    if (File.Exists(appendixPath))
    {
        merger.AppendDocument(appendixPath);
    }

    // Take the viewer preferences (zoom, layout) from the cover document
    using var coverDoc = new PdfDocument(coverPagePath);
    merger.CopyViewerPreferences(coverDoc);

    return merger.ToBytes();
}
```

### Isolate Untrusted PDFs in Worker Processes

A damaged or hostile PDF can make PDFium abort the process; no `catch` can stop that. The optional `PdfiumWrapper.Processing` package runs the same operations in worker processes it manages. Each document's outcome is a status on the result instead of an exception, and a worker that dies is replaced.

```csharp
using PdfiumWrapper.Processing;

using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
await using var pool = await PdfProcessingPool.CreateAsync();

PdfJobResult<ImageFiles> result = await pool.ConvertToPngAsync(
    PdfInput.FromFile("upload.pdf"), "output_folder", dpi: 150, ct: cts.Token);

if (result.IsSuccess)
{
    ImageFiles images = result.Value!;
    Console.WriteLine($"{images.PageCount} pages: {string.Join(", ", images.Files)}");
}
else
{
    Console.WriteLine($"{result.Status}: {result.Error}");   // Failed, TimedOut, Cancelled or WorkerCrashed
}
```

A cancelled single job reports `PdfJobStatus.Cancelled`. Cancelling the token of a batch (the `IEnumerable<PdfInput>` overloads) throws `OperationCanceledException`. See [High-Throughput Processing](HIGH-THROUGHPUT-PROCESSING.md#worker-pool) for options, sizing and events.
