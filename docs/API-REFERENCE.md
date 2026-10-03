# API Reference

This document describes the public API of PdfiumWrapper (namespace `PdfiumWrapper`) and of the worker pool package PdfiumWrapper.Processing (namespace `PdfiumWrapper.Processing`). The breaking changes of 2.0 are listed in the [changelog](../CHANGELOG.md); the "Changed in 2.0" notes below explain the ones that affect a member.

The samples use `System.Drawing.Color` for colors (`using System.Drawing;`).

## Table of Contents

- [PdfDocument](#pdfdocument)
- [PdfPage](#pdfpage)
- [Page Object Classes](#page-object-classes)
  - [PdfPageObject](#pdfpageobject)
  - [PdfTextObject](#pdftextobject)
  - [PdfImageObject](#pdfimageobject)
  - [PdfPathObject](#pdfpathobject)
  - [PdfShadingObject](#pdfshadingobject)
  - [PdfFormObject](#pdfformobject)
- [PdfForm](#pdfform)
- [FormField](#formfield)
- [FormFieldType](#formfieldtype)
- [PdfMerger](#pdfmerger)
- [PdfMetadata](#pdfmetadata)
- [PdfBookmarks](#pdfbookmarks)
- [PdfBookmark](#pdfbookmark)
- [PdfAttachments](#pdfattachments)
- [PdfAttachment](#pdfattachment)
- [Supporting Types](#supporting-types)
  - [RawBitmap](#rawbitmap)
  - [ImageFormat](#imageformat)
  - [TiffColorMode](#tiffcolormode)
  - [PdfPermissions](#pdfpermissions)
  - [PdfiumException and PdfiumErrorCode](#pdfiumexception-and-pdfiumerrorcode)
- [PdfiumRuntime](#pdfiumruntime)
- [The PDFium Class in 2.0](#the-pdfium-class-in-20)
- [PdfProcessingPool (PdfiumWrapper.Processing)](#pdfprocessingpool-pdfiumwrapperprocessing)
  - [Hosting the workers](#hosting-the-workers)
  - [Construction](#construction)
  - [Operations](#operations)
  - [PdfInput](#pdfinput)
  - [Pool Behavior](#pool-behavior)
  - [PdfPoolOptions](#pdfpooloptions)
  - [PdfJobResult](#pdfjobresult)
  - [PdfWorkerHost](#pdfworkerhost)
  - [Events and Statistics](#events-and-statistics)
  - [PdfPoolException](#pdfpoolexception)

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
page.AddText("Hello", 100, 700);
page.GenerateContent();
document.Save("new_document.pdf");
```

**Exceptions:**
- `InvalidOperationException` — If PDFium cannot create the document

#### PdfDocument(string filePath, string? password = null)

Loads a PDF document from a file path.

```csharp
using var document = new PdfDocument("sample.pdf");
using var secureDoc = new PdfDocument("encrypted.pdf", password: "secret");
```

**Parameters:**
- `filePath` — Path to the PDF file
- `password` — Optional password for encrypted PDFs

**Exceptions:**
- `PdfiumException` — If the document fails to load. It derives from `InvalidOperationException`; its `ErrorCode` says why (see [Load errors](#load-errors)).

#### PdfDocument(byte[] data, string? password = null)

Loads a PDF document from a byte array.

```csharp
byte[] pdfBytes = File.ReadAllBytes("sample.pdf");
using var document = new PdfDocument(pdfBytes);
```

The array is used in place, without a copy: it is pinned, and PDFium reads pages from it for as long as the document is open. Do not modify, reuse or return the array to a pool until the document is disposed; pass a copy (or use the `Stream` constructor) if you need the array back sooner.

**Parameters:**
- `data` — PDF file contents as byte array
- `password` — Optional password for encrypted PDFs

**Exceptions:**
- `ArgumentNullException` — If `data` is null
- `PdfiumException` — If the document fails to load

#### PdfDocument(Stream pdfStream, string? password = null)

Loads a PDF document from a stream.

```csharp
using var stream = File.OpenRead("sample.pdf");
using var document = new PdfDocument(stream);
```

The stream is read from its current position to its end during construction, before any native work starts, and is left positioned at its end. The document does not use the stream afterwards, so it can be closed, reset or reused immediately.

- Inputs of up to 64 MB are copied into a buffer the document owns (rented from `ArrayPool<byte>.Shared` when the stream is seekable, and returned after the document is closed). This includes a `MemoryStream`: its own buffer is never used in place, so overwriting it after construction cannot change the document.
- Larger inputs are copied to a temporary file that is deleted when the document is disposed.

The threshold can be changed with `AppContext.SetData("PdfiumWrapper.SpoolThreshold", bytes)` before loading. The value is a number of bytes, as `long`, `int` or a numeric string; any other value means the default. Unlike `PdfiumWrapper.MaxRenderPixels`, zero and negative values are used as given (every stream input is then spooled to a file).

**Parameters:**
- `pdfStream` — Readable stream containing PDF data. It does not need to be seekable.
- `password` — Optional password for encrypted PDFs

**Exceptions:**
- `ArgumentNullException` — If `pdfStream` is null
- `ArgumentException` — If the stream is not readable
- `PdfiumException` — If the document fails to load

#### Load errors

Every constructor that loads a document (path, byte array, stream, and the same `PdfMerger` constructors) throws `PdfiumException` when PDFium rejects the input. So do the `PdfMerger` methods that load a source document themselves: `AppendDocument(string, ...)`, `AppendDocument(byte[], ...)` and the `AppendPages(string filePath, ...)` overloads. `ErrorCode` is a `PdfiumErrorCode`, the value of `FPDF_GetLastError` read right after the failing load:

| `ErrorCode` | Meaning |
|-------------|---------|
| `File` | The file was not found or could not be opened |
| `Format` | The input is not a PDF or is corrupted |
| `Password` | The document is encrypted and the password is missing or wrong |
| `Security` | The document uses a security handler PDFium does not support |
| `Page` | A page was not found or its content is broken |
| `Unknown` | PDFium gave no specific reason |

```csharp
try
{
    using var document = new PdfDocument(bytes, password);
}
catch (PdfiumException ex) when (ex.ErrorCode == PdfiumErrorCode.Password)
{
    // Ask for the password again
}
```

See [PdfiumException and PdfiumErrorCode](#pdfiumexception-and-pdfiumerrorcode) for the declarations.

**Changed in 2.0:** load failures used to be plain `InvalidOperationException` with the code in the message. `PdfiumException` derives from `InvalidOperationException`, so existing `catch` blocks still match. Failures of calls that do not set `FPDF_GetLastError` (importing pages, creating or loading a page, saving) no longer append a stale code to their message.

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `PageCount` | `int` | Number of pages in the document |
| `Permissions` | `PdfPermissions` | Document permission flags (see [PdfPermissions](#pdfpermissions)) |
| `DocumentId` | `string?` | The original file identifier from the trailer `/ID`, as uppercase hex (32 characters for the usual 16-byte ID), or null. **Changed in 2.0:** the hex no longer ends in `00` from PDFium's terminator. |
| `Metadata` | `PdfMetadata` | Access to document metadata |
| `Bookmarks` | `PdfBookmarks` | Access to document bookmarks/outlines |
| `Attachments` | `PdfAttachments` | Access to embedded file attachments |

### Methods

#### AddPage(int width = 612, int height = 792, int index = -1)

Adds a new page to the document. Used when creating new PDFs.

```csharp
// Add page with default US Letter size (612 x 792 points)
using var letter = document.AddPage();

// Add page with custom dimensions (A4)
using var a4 = document.AddPage(width: 595, height: 842);

// Insert page at a specific position
using var first = document.AddPage(width: 612, height: 792, index: 0);
```

**Parameters:**
- `width` — Page width in whole points (default: 612 = US Letter)
- `height` — Page height in whole points (default: 792 = US Letter)
- `index` — Zero-based insertion index (default: -1 = append at end)

**Returns:** `PdfPage` instance (must be disposed)

**Exceptions:**
- `InvalidOperationException` — If PDFium cannot create the page

**Common Page Sizes:**
| Size | Width | Height |
|------|-------|--------|
| US Letter | 612 | 792 |
| A4 | 595 | 842 |
| Legal | 612 | 1008 |

#### DeletePage(int pageIndex) / DeletePage(PdfPage page)

Deletes a page from the document. The `PdfPage` overload deletes the page at `page.PageIndex`.

```csharp
document.DeletePage(0); // Delete the first page
```

The `PageIndex` of `PdfPage` objects that are already loaded is not updated, so dispose loaded pages after deleting a page before them.

**Exceptions:**
- `ArgumentOutOfRangeException` — If `pageIndex` is out of bounds
- `ArgumentNullException` — If `page` is null

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

**Obsolete.** Returns all pages as an array, every page loaded at once. Dispose each page; an undisposed page is released by its finalizer or with the document. Prefer [`ProcessAllPages`](#processallpages), which loads and disposes one page at a time.

```csharp
#pragma warning disable CS0618 // GetAllPages is obsolete
var pages = document.GetAllPages();
#pragma warning restore CS0618
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

#### ProcessAllPages

```csharp
public TResult[] ProcessAllPages<TResult>(Func<PdfPage, TResult> processor)
public void ProcessAllPages(Action<PdfPage> action)
```

Loads each page in turn, passes it to the delegate and disposes it before loading the next, so only one page is loaded at a time. The native gate is not held while the delegate runs; each page member it calls enters the gate on its own. An empty document returns an empty array.

```csharp
string[] texts = document.ProcessAllPages(page => page.ExtractText());

document.ProcessAllPages(page => Console.WriteLine($"Page {page.PageIndex}: {page.Width} x {page.Height}"));
```

The page is disposed when the delegate returns, so do not keep it or its page objects.

**Exceptions:**
- `ArgumentNullException` — If the delegate is null

#### ProcessAllPagesAsync

```csharp
public Task<TResult[]> ProcessAllPagesAsync<TResult>(Func<PdfPage, TResult> processor, CancellationToken cancellationToken = default)
public Task ProcessAllPagesAsync(Action<PdfPage> action, CancellationToken cancellationToken = default)
```

Async version of `ProcessAllPages`. Pages are loaded and disposed while waiting for the gate without blocking a thread. The delegate runs on a thread-pool thread, never on the caller's synchronization context, so it must not touch UI objects. The token is checked before each page (see [Async methods and cancellation](#async-methods-and-cancellation)).

```csharp
string[] texts = await document.ProcessAllPagesAsync(page => page.ExtractText(), cancellationToken);
```

#### GetPageSize(int pageIndex)

Gets the dimensions of a specific page in points (1/72 inch).

```csharp
var (width, height) = document.GetPageSize(0);
Console.WriteLine($"Page 1: {width} x {height} points");
```

**Returns:** `(double width, double height)` in points

**Exceptions:**
- `ArgumentOutOfRangeException` — If pageIndex is out of bounds

#### GetAllPageSizes()

Gets dimensions for all pages.

```csharp
var sizes = document.GetAllPageSizes();
for (int i = 0; i < sizes.Length; i++)
{
    Console.WriteLine($"Page {i + 1}: {sizes[i].width} x {sizes[i].height}");
}
```

**Returns:** `(double width, double height)[]`

#### GetPageLabel(int pageIndex) / GetAllPageLabels()

Gets the page label (the `/PageLabels` number tree entry, such as `"iii"` or `"A-1"`) of one page, or of every page.

```csharp
string? label = document.GetPageLabel(0);
string?[] labels = document.GetAllPageLabels();
```

**Returns:** `string?` (null when the page has no label); `string?[]` with one entry per page

**Exceptions:**
- `ArgumentOutOfRangeException` — If pageIndex is out of bounds

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

#### Async methods and cancellation

Every async method on `PdfDocument` (`RenderPagesAsync`, `StreamImageBytesAsync`, `StreamJpegBytesAsync`, `SaveAsTiffAsync`, `SaveAsPngsAsync`, `SaveAsJpegsAsync`, `SaveAsImagesAsync`, `ProcessAllPagesAsync`) processes pages sequentially on thread-pool threads, and waiting for the native gate does not block a thread. They never post work to the caller's `SynchronizationContext`, so a UI thread is not used for rendering, and a caller that blocks on the task (`.Wait()`, `.Result`) does not deadlock. The synchronous methods, including constructors, block the calling thread while they wait. Neither form renders one document's pages in parallel.

**Cancellation:** every async method takes an optional `CancellationToken` as its last parameter, except `StreamImageBytesAsync` and `StreamJpegBytesAsync`, which take it through `WithCancellation`. The token is checked before each page and while waiting for the native gate, and a cancelled call throws `OperationCanceledException`. A page that is rendering finishes first. PNG and JPEG files are written with `File.WriteAllBytesAsync` and the token; pages written before the cancellation are kept. libtiff writes synchronously, so TIFF output checks the token between pages only; a cancelled `SaveAsTiffAsync(path, ...)` deletes the partial file, and a cancelled `SaveAsTiffAsync(stream, ...)` leaves an incomplete TIFF in the stream.

```csharp
await foreach (var bytes in document.StreamImageBytesAsync(ImageFormat.Png).WithCancellation(cancellationToken))
{
    // ...
}
```

#### RenderPages(int dpi = 300) / RenderPages(int dpiWidth, int dpiHeight)

Renders all pages to `RawBitmap` records containing raw pixel data. The second overload takes separate horizontal and vertical DPI.

```csharp
RawBitmap[] bitmaps = document.RenderPages(dpi: 150);

// RawBitmap is a lightweight record, no disposal needed
foreach (var bitmap in bitmaps)
{
    Console.WriteLine($"{bitmap.Width}x{bitmap.Height}, stride={bitmap.Stride}");
    byte[] pixels = bitmap.Pixels; // BGRx: 4 bytes per pixel, the 4th byte is padding
}
```

**Parameters:**
- `dpi` — Resolution in dots per inch (default: 300)

**Returns:** Array of `RawBitmap` records (see [RawBitmap](#rawbitmap)). The pixels are PDFium's BGRx layout: the fourth byte of each pixel is padding, not alpha. Rows are `Stride` bytes apart. Not disposable.

**Exceptions:**
- `ArgumentOutOfRangeException` — If a DPI is zero or less
- `InvalidOperationException` — If the document has no pages, or a page exceeds the [render size limit](#render-size-limit-and-dpi-validation)

**Memory:** every page's pixels are held at once until the call returns (a US Letter page at 300 DPI is about 33 MB), so a long document needs one bitmap per page in memory. For large documents use `StreamImageBytes`, the `SaveAs*` methods, or render one page at a time.

#### RenderPagesAsync

```csharp
public Task<RawBitmap[]> RenderPagesAsync(int dpi = 300, CancellationToken cancellationToken = default)
public Task<RawBitmap[]> RenderPagesAsync(int dpiWidth, int dpiHeight, CancellationToken cancellationToken = default)
```

Async version of `RenderPages`. Waits for the native gate without blocking a thread.

```csharp
RawBitmap[] bitmaps = await document.RenderPagesAsync(dpi: 300, cancellationToken: cancellationToken);
```

#### Render size limit and DPI validation

Every render is checked before PDFium allocates its bitmap. This applies to the document-level methods (`RenderPages`, `StreamImageBytes`, `StreamJpegBytes`, `SaveAsPngs`, `SaveAsJpegs`, `SaveAsImages`, `SaveAsTiff` and their async versions), to `PdfPage.RenderToBytes`, and to the bitmap `PdfPage.AddImage` creates from a decoded image:

- A DPI of zero or less throws `ArgumentOutOfRangeException`, before any page is rendered or any output is created. So does a `RenderToBytes` width or height of zero or less.
- A page's pixel size is its size in points / 72 × DPI. It comes from the file's page box, so a crafted PDF can ask for a huge bitmap. A render larger than **268,435,456 pixels** (2^28, 1 GiB as BGRA, for example 16,384 × 16,384) throws `InvalidOperationException`. The message names the page index, the pixel size and the setting below. A US Letter page at 1,200 DPI is about 134 million pixels, well inside the limit.
- If PDFium still cannot allocate a bitmap, the error is an `InvalidOperationException`, not an `OutOfMemoryException`.
- A page smaller than one pixel at the chosen DPI renders as one pixel.

To change the limit, set the `PdfiumWrapper.MaxRenderPixels` `AppContext` data key (a number of pixels, as `long`, `int` or a numeric string) before rendering:

```csharp
AppContext.SetData("PdfiumWrapper.MaxRenderPixels", 512L * 1024 * 1024); // 2^29 pixels
```

or in the project file:

```xml
<ItemGroup>
  <RuntimeHostConfigurationOption Include="PdfiumWrapper.MaxRenderPixels" Value="536870912" />
</ItemGroup>
```

A value that is missing, zero, negative or not a number means the default. The limit is per bitmap. Concurrent callers each hold one bitmap while they encode, and `RenderPages` holds one per page.

#### StreamImageBytes

```csharp
public IEnumerable<byte[]> StreamImageBytes(ImageFormat format, int quality = 90, int dpi = 300)
public IEnumerable<byte[]> StreamImageBytes(ImageFormat format, int quality, int dpiWidth, int dpiHeight)
```

Streams encoded image bytes one page at a time. Only one page's data is in memory at any point. The arguments and the page count are checked when the method is called; pages are rendered as the enumeration advances.

```csharp
int pageNumber = 0;
foreach (var bytes in document.StreamImageBytes(ImageFormat.Png, dpi: 300))
{
    File.WriteAllBytes($"page_{++pageNumber}.png", bytes);
    // Previous page's bytes are eligible for GC
}
```

**Parameters:**
- `format` — `ImageFormat.Png` or `ImageFormat.Jpeg`. `ImageFormat.Tiff` throws `ArgumentOutOfRangeException`; use `SaveAsTiff`
- `quality` — JPEG quality, 1-100 (default 90). Ignored for PNG
- `dpi` — Resolution

**Returns:** `IEnumerable<byte[]>` — one byte array per page

**Exceptions:**
- `ArgumentOutOfRangeException` — For `ImageFormat.Tiff` or a DPI of zero or less
- `InvalidOperationException` — If the document has no pages

**JPEG quality default:** every JPEG entry point uses quality **90** when the caller passes none: `StreamImageBytes`, `StreamImageBytesAsync`, `StreamJpegBytes`, `StreamJpegBytesAsync`, `SaveAsJpegs`, `SaveAsJpegsAsync`, `SaveAsImages`, `SaveAsImagesAsync`, and the worker pool's `ConvertToJpegAsync`. A page therefore produces the same bytes whichever of these you call. **Changed in 2.0:** `StreamImageBytes`, `StreamImageBytesAsync` and `SaveAsImages` used to default to 100.

#### StreamImageBytesAsync

```csharp
public IAsyncEnumerable<byte[]> StreamImageBytesAsync(ImageFormat format, int quality = 90, int dpi = 300)
public IAsyncEnumerable<byte[]> StreamImageBytesAsync(ImageFormat format, int quality, int dpiWidth, int dpiHeight)
```

Async streaming version. Waits for the native gate without blocking a thread. Each page is rendered and encoded on the thread pool, not on the thread that resumes the enumeration. Cancel with `WithCancellation`.

The call itself returns at once and never waits for the gate. It throws immediately if the document is disposed, the format cannot be streamed (`ImageFormat.Tiff`), or a DPI is not positive. An empty document is reported (`InvalidOperationException`) when enumeration starts, because reading the page count is native work. The same holds for `StreamJpegBytesAsync`.

```csharp
int pageNumber = 0;
await foreach (var bytes in document.StreamImageBytesAsync(ImageFormat.Jpeg, 85, 200))
{
    await File.WriteAllBytesAsync($"page_{++pageNumber}.jpg", bytes);
}
```

**Returns:** `IAsyncEnumerable<byte[]>` — one byte array per page

#### StreamJpegBytes / StreamJpegBytesAsync

```csharp
public IEnumerable<byte[]> StreamJpegBytes(int quality = 90, int dpi = 300)
public IEnumerable<byte[]> StreamJpegBytes(int quality, int dpiWidth, int dpiHeight)
public IAsyncEnumerable<byte[]> StreamJpegBytesAsync(int quality = 90, int dpi = 300)
public IAsyncEnumerable<byte[]> StreamJpegBytesAsync(int quality, int dpiWidth, int dpiHeight)
```

Shorthand for `StreamImageBytes(ImageFormat.Jpeg, ...)` and `StreamImageBytesAsync(ImageFormat.Jpeg, ...)`, with the same validation, memory use and cancellation (`WithCancellation` for the async version).

```csharp
foreach (var jpeg in document.StreamJpegBytes(quality: 85, dpi: 150))
{
    // one JPEG per page
}
```

#### SaveAsTiff (file)

```csharp
public void SaveAsTiff(string outputPath, int dpi = 200, TiffColorMode colorMode = TiffColorMode.Bilevel, byte threshold = 128)
public void SaveAsTiff(string outputPath, int dpiWidth, int dpiHeight, TiffColorMode colorMode = TiffColorMode.Bilevel, byte threshold = 128)
```

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

The file is opened as a managed `FileStream` and written through libtiff's stream interface. Any path .NET accepts therefore works, including non-ASCII directories and names on Windows. If the export fails (for example, a page over the render size limit), the partly written file is deleted.

**Exceptions:**
- `ArgumentException` — If `outputPath` is null, empty or white space
- `ArgumentOutOfRangeException` — If a DPI is zero or less
- `InvalidOperationException` — If the document has no pages, or a page exceeds the render size limit
- `IOException` — If libtiff fails to write

#### SaveAsTiff (stream)

```csharp
public void SaveAsTiff(Stream output, int dpi = 200, TiffColorMode colorMode = TiffColorMode.Bilevel, byte threshold = 128)
public void SaveAsTiff(Stream output, int dpiWidth, int dpiHeight, TiffColorMode colorMode = TiffColorMode.Bilevel, byte threshold = 128)
```

Saves all pages as a multi-page TIFF to a writable, seekable stream. libtiff reads back what it has written while it finishes the file, so use a stream that can also be read (a `MemoryStream`, or a `FileStream` opened with `FileAccess.ReadWrite`). The stream is left open.

```csharp
using var stream = new MemoryStream();
document.SaveAsTiff(stream, dpi: 200);
```

**Exceptions:**
- `ArgumentNullException` — If `output` is null
- `ArgumentException` — If the stream is not writable or not seekable
- An exception thrown by the stream (for example `IOException`) propagates unchanged. Other libtiff failures throw `IOException`.

#### SaveAsTiffAsync

```csharp
public Task SaveAsTiffAsync(string outputPath, int dpi = 200, TiffColorMode colorMode = TiffColorMode.Bilevel, byte threshold = 128, CancellationToken cancellationToken = default)
public Task SaveAsTiffAsync(string outputPath, int dpiWidth, int dpiHeight, TiffColorMode colorMode = TiffColorMode.Bilevel, byte threshold = 128, CancellationToken cancellationToken = default)
public Task SaveAsTiffAsync(Stream output, int dpi = 200, TiffColorMode colorMode = TiffColorMode.Bilevel, byte threshold = 128, CancellationToken cancellationToken = default)
public Task SaveAsTiffAsync(Stream output, int dpiWidth, int dpiHeight, TiffColorMode colorMode = TiffColorMode.Bilevel, byte threshold = 128, CancellationToken cancellationToken = default)
```

Async versions of both file and stream overloads. They wait for the native gate without blocking a thread and run on the thread pool. libtiff writes synchronously, so the stream is written with synchronous calls.

```csharp
await document.SaveAsTiffAsync("output.tiff", dpi: 200);
await document.SaveAsTiffAsync(stream, dpi: 200, colorMode: TiffColorMode.Grayscale);
```

#### Page file names

The directory methods (`SaveAsPngs`, `SaveAsJpegs`, `SaveAsImages` and their async versions) name page *n* (1-based) `{fileNamePrefix}_{n:D3}.png` or `.jpg`: `page_001.png`, `page_002.png`, …, `page_1000.png`. They create the directory if needed, and replace files that already have those names. Every argument (directory, format, DPI, page count) is checked before the directory is created. All output files are opened by .NET, so non-ASCII directories and prefixes (`Rechnung_März`, `café`) are written exactly as given on every platform. The worker pool's image jobs use the same names.

**Exceptions** (all directory methods):
- `ArgumentException` — If `outputDirectory` is null, empty or white space
- `ArgumentOutOfRangeException` — For `ImageFormat.Tiff` or a DPI of zero or less
- `InvalidOperationException` — If the document has no pages, or a page exceeds the render size limit

#### SaveAsPngs / SaveAsPngsAsync

```csharp
public void SaveAsPngs(string outputDirectory, string fileNamePrefix = "page", int dpi = 300)
public Task SaveAsPngsAsync(string outputDirectory, string fileNamePrefix = "page", int dpi = 300, CancellationToken cancellationToken = default)
```

Saves all pages as PNG files.

```csharp
document.SaveAsPngs("output", fileNamePrefix: "invoice", dpi: 300);
// Creates: output/invoice_001.png, output/invoice_002.png, etc.

await document.SaveAsPngsAsync("output", fileNamePrefix: "invoice", dpi: 300);
```

#### SaveAsJpegs / SaveAsJpegsAsync

```csharp
public void SaveAsJpegs(string outputDirectory, string fileNamePrefix = "page", int quality = 90, int dpi = 300)
public void SaveAsJpegs(string outputDirectory, string fileNamePrefix, int quality, int dpiWidth, int dpiHeight)
public Task SaveAsJpegsAsync(string outputDirectory, string fileNamePrefix = "page", int quality = 90, int dpi = 300, CancellationToken cancellationToken = default)
public Task SaveAsJpegsAsync(string outputDirectory, string fileNamePrefix, int quality, int dpiWidth, int dpiHeight, CancellationToken cancellationToken = default)
```

Saves all pages as JPEG files.

```csharp
document.SaveAsJpegs("output", fileNamePrefix: "page", quality: 85, dpi: 200);
```

#### SaveAsImages / SaveAsImagesAsync (directory)

```csharp
public void SaveAsImages(string outputDirectory, string fileNamePrefix, ImageFormat format, int quality = 90, int dpi = 300)
public void SaveAsImages(string outputDirectory, string fileNamePrefix, ImageFormat format, int quality, int dpiWidth, int dpiHeight)
public Task SaveAsImagesAsync(string outputDirectory, string fileNamePrefix, ImageFormat format, int quality = 90, int dpi = 300, CancellationToken cancellationToken = default)
public Task SaveAsImagesAsync(string outputDirectory, string fileNamePrefix, ImageFormat format, int quality, int dpiWidth, int dpiHeight, CancellationToken cancellationToken = default)
```

Saves all pages as PNG or JPEG files.

```csharp
document.SaveAsImages("output", "page", ImageFormat.Png, dpi: 300);
document.SaveAsImages("output", "page", ImageFormat.Jpeg, quality: 80, dpiWidth: 300, dpiHeight: 300);
await document.SaveAsImagesAsync("output", "page", ImageFormat.Jpeg, quality: 85, dpi: 200);
```

**Note:** Supported formats are `ImageFormat.Png` and `ImageFormat.Jpeg`. `ImageFormat.Tiff` throws `ArgumentOutOfRangeException` before the directory is created; use `SaveAsTiff` for TIFF output. `quality` is ignored for PNG.

#### SaveAsImages / SaveAsImagesAsync (streams)

```csharp
public void SaveAsImages(Stream[] outputStreams, ImageFormat format, int quality, int dpiWidth, int dpiHeight)
public Task SaveAsImagesAsync(Stream[] outputStreams, ImageFormat format, int quality, int dpiWidth, int dpiHeight, CancellationToken cancellationToken = default)
```

Saves page *i* to `outputStreams[i]`. There must be exactly one non-null stream per page. An empty document throws `InvalidOperationException`, as the other image methods do. The streams are written with the native gate free and are left open. The async version writes each page with `Stream.WriteAsync` and the token.

```csharp
var streams = new Stream[document.PageCount];
for (int i = 0; i < streams.Length; i++)
    streams[i] = new MemoryStream();

document.SaveAsImages(streams, ImageFormat.Png, 90, 300, 300);
await document.SaveAsImagesAsync(streams, ImageFormat.Jpeg, 90, 200, 200);
```

**Exceptions:**
- `ArgumentNullException` — If `outputStreams` is null
- `ArgumentException` — If the number of streams differs from the page count, or a stream is null

#### Save(string filePath, uint flags = 0)

Saves the PDF document to a file.

```csharp
document.Save("modified.pdf");
```

**Parameters:**
- `filePath` — Output file path
- `flags` — Save flags (0 for standard save, `PDFium.FPDF_INCREMENTAL` for an incremental save)

The document is serialized into a pooled in-memory buffer first and written to the file afterwards, so peak memory includes the full output size.

**Exceptions:**
- `ArgumentException` — If `filePath` is null, empty or white space
- `InvalidOperationException` — If PDFium fails to serialize the document

#### SaveToStream(Stream stream, uint flags = 0)

Saves the PDF document to a stream.

```csharp
using var memoryStream = new MemoryStream();
document.SaveToStream(memoryStream);
byte[] pdfBytes = memoryStream.ToArray();
```

The document is serialized into a pooled in-memory buffer first and written to `stream` in one call afterwards. A slow stream therefore does not hold up other PDF work, and peak memory includes the full output size.

**Exceptions:**
- `ArgumentNullException` — If `stream` is null
- `InvalidOperationException` — If PDFium fails to serialize the document
- Any exception thrown by `stream` (for example `IOException`) propagates unchanged. Before 2.0 a failing stream was reported as `InvalidOperationException`.

#### Dispose()

Closes the document, together with its pages, forms and detached page objects. A document dropped without `Dispose()` is released by its finalizer: its native handles are queued and closed by the next gated operation (see [PdfiumRuntime](#pdfiumruntime)). Derived classes override `protected virtual void Dispose(bool disposing)`.

---

## PdfPage

Represents a single page in a PDF document. Provides rendering, text extraction, and page editing capabilities.

### Declaration

```csharp
public class PdfPage : IDisposable
```

A page belongs to its document. It cannot be used after it or its document is disposed (`ObjectDisposedException`). One page must not be used from two threads at once.

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `PageIndex` | `int` | Zero-based index of this page, fixed when the page is loaded |
| `Width` | `double` | Page width in points |
| `Height` | `double` | Page height in points |
| `ObjectCount` | `int` | Number of page objects on the page (see [GetObject](#objectcount-and-getobjectint-index)) |
| `HasEmbeddedThumbnail` | `bool` | Whether the page has an embedded thumbnail (checks the stream without decoding it; see `GetEmbeddedThumbnail()`) |

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
- `font` — Standard font name (default: "Helvetica"; see [Standard Fonts](#standard-fonts))
- `fontSize` — Font size in points (default: 12)

**Returns:** `PdfTextObject` instance, owned by the page

**Exceptions:**
- `InvalidOperationException` — If the font cannot be loaded or the object cannot be created or inserted

#### AddImage(byte[] imageBytes, float x, float y, float width, float height)

Adds an image object to the page.

```csharp
var imageBytes = File.ReadAllBytes("logo.png");
var image = page.AddImage(imageBytes, x: 100, y: 500, width: 200, height: 100);
```

**Parameters:**
- `imageBytes` — PNG or JPEG data, recognized by its signature
- `x` — X position of bottom-left corner
- `y` — Y position of bottom-left corner
- `width` — Display width in points
- `height` — Display height in points

**Returns:** `PdfImageObject` instance, owned by the page

**Exceptions:**
- `NotSupportedException` — If the data is neither PNG nor JPEG
- `InvalidDataException` — If a JPEG header gives dimensions too large to decode into an array
- `InvalidOperationException` — If the decoded image exceeds the [render size limit](#render-size-limit-and-dpi-validation), the image cannot be decoded, or the object cannot be created or inserted

#### AddRectangle(float x, float y, float width, float height, Color? fillColor = null, Color? strokeColor = null)

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

**Returns:** `PdfPathObject` instance, owned by the page

#### AddPath()

Creates a new empty path object on the page for custom shapes.

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

**Returns:** `PdfPathObject` instance, owned by the page

#### RemoveObject(PdfPageObject pageObject)

Takes an object off the page. The caller owns the object afterwards: dispose it, or the document disposes it when the document is disposed. Call `GenerateContent()` afterwards, as after any change.

```csharp
var stamp = page.AddText("DRAFT", 100, 700);
// ...
if (page.RemoveObject(stamp))
    stamp.Dispose();
page.GenerateContent();
```

`pageObject` must be the live wrapper this page returned for the object (from an `Add*` method or `GetObject`).

**Returns:** `true` when the object was removed; `false` when it is not on this page (another page's object, a sub-object of a [PdfFormObject](#pdfformobject), or one already removed)

**Exceptions:**
- `ArgumentNullException` — If `pageObject` is null
- `ObjectDisposedException` — If `pageObject` is disposed

#### GenerateContent()

Generates the page content stream. **Must be called after adding, removing or modifying page objects, before saving.**

```csharp
page.AddText("Hello", 100, 700);
page.AddRectangle(100, 500, 200, 100, Color.Blue, null);
page.GenerateContent();  // Required!
document.Save("output.pdf");
```

**Exceptions:**
- `InvalidOperationException` — If PDFium fails to generate the content

#### ObjectCount and GetObject(int index)

`ObjectCount` is the number of objects on the page. `GetObject` returns the object at `index` wrapped in the class that matches its kind: `PdfTextObject`, `PdfPathObject`, `PdfImageObject`, `PdfShadingObject` or `PdfFormObject` (a form XObject, whose own `ObjectCount` and `GetObject` read its sub-objects).

```csharp
for (int i = 0; i < page.ObjectCount; i++)
{
    var obj = page.GetObject(i);
    Console.WriteLine($"{obj.GetType().Name}: {obj.GetBounds()}");
}
```

The page owns these objects: disposing a wrapper does not delete the object, and every wrapper becomes unusable (`ObjectDisposedException`) when the page is disposed. While a wrapper is not disposed, the page returns that same wrapper for the same object, including the wrappers returned by the `Add*` methods. Pass a wrapper to `RemoveObject` to take the object off the page.

**Exceptions:**
- `ArgumentOutOfRangeException` — If `index` is out of bounds

**Changed in 2.0:** `GetObject` returned a native `IntPtr`, and `PdfTextObject.Create`, `PdfImageObject.Create`, `PdfPathObject.Create` and `CreateRectangle` took a document handle. The factories are internal; use the `Add*` methods.

### Text Extraction Methods

#### ExtractText()

Extracts all text content from the page.

```csharp
using var page = document.GetPage(0);
string text = page.ExtractText();
Console.WriteLine(text);
```

**Returns:** Text content of the page, or an empty string when it has none

### Rendering Methods

#### RenderToBytes(int width, int height, int flags = 0)

Renders the page, stretched to `width` × `height` pixels, to raw pixel data.

```csharp
using var page = document.GetPage(0);
byte[] pixels = page.RenderToBytes(1920, 1080, PDFium.FPDF_ANNOT);
// BGRx, 4 bytes per pixel (the 4th byte is padding), rows of width * 4 bytes
```

**Parameters:**
- `width` — Output width in pixels, greater than zero
- `height` — Output height in pixels, greater than zero
- `flags` — Render flags (e.g., `PDFium.FPDF_ANNOT` to include annotations). The default, 0, does not draw annotations; the document-level render methods always do.

**Returns:** BGRx byte array

**Throws:** `ArgumentOutOfRangeException` for a width or height of zero or less. `InvalidOperationException` when `width × height` exceeds the render size limit (268,435,456 pixels by default; see [Render size limit and DPI validation](#render-size-limit-and-dpi-validation)), or when PDFium cannot create the bitmap.

#### GetEmbeddedThumbnail()

Gets the page's embedded thumbnail (its `/Thumb` image) as BGRA pixels, with its size and stride, in one decode.

```csharp
RawBitmap? thumbnail = page.GetEmbeddedThumbnail();
if (thumbnail != null)
{
    Console.WriteLine($"Thumbnail: {thumbnail.Width} x {thumbnail.Height}");
    byte[] bgra = thumbnail.Pixels; // Stride == Width * 4
}
```

Gray and RGB thumbnails are expanded to BGRA with full opacity, as `PdfImageObject.GetBitmap()` does. The native bitmap is copied and released before the method returns.

**Returns:** `RawBitmap`, or `null` if the page has no thumbnail or it cannot be decoded

`HasEmbeddedThumbnail` only measures the thumbnail stream and does not decode the image. A page whose thumbnail is present but damaged reports `true` there, while `GetEmbeddedThumbnail()` returns `null`. To get the pixels, call `GetEmbeddedThumbnail()` directly and check for `null`; there is no need to check `HasEmbeddedThumbnail` first.

#### GetEmbeddedThumbnailBytes() / GetEmbeddedThumbnailSize() (obsolete)

**Obsolete:** use `GetEmbeddedThumbnail()`, which returns the pixels and the size from a single decode. `GetEmbeddedThumbnailBytes()` now returns the same BGRA pixels as `GetEmbeddedThumbnail().Pixels`; before 2.0 it returned PDFium's raw format, often 3 bytes per pixel. `GetEmbeddedThumbnailSize()` returns `(int width, int height)?`. Both return `null` when there is no thumbnail.

#### Dispose()

Closes the page. Its page object wrappers become unusable. A page dropped without `Dispose()` is released by its finalizer or with its document.

---

## Page Object Classes

The objects on a PDF page: text, images, paths, shadings and form XObjects. New objects are created with the `PdfPage.Add*` methods; existing ones are read with `PdfPage.GetObject`.

### PdfPageObject

Abstract base class for all page objects. Its constructor is `private protected`, so it cannot be derived from outside the library.

```csharp
public abstract class PdfPageObject : IDisposable
```

#### Properties

| Property | Type | Description |
|----------|------|-------------|
| `ObjectType` | `int` | PDFium's object type: one of the `PDFium.FPDF_PAGEOBJ_*` constants (`TEXT` = 1, `PATH` = 2, `IMAGE` = 3, `SHADING` = 4, `FORM` = 5) |
| `HasTransparency` | `bool` | Whether the object has transparency |

#### Methods

| Method | Description |
|--------|-------------|
| `GetBounds()` | Returns the bounding box as `(float left, float bottom, float right, float top)` in page points |
| `GetMatrix()` | Returns the transformation matrix as `(double a, double b, double c, double d, double e, double f)` |
| `SetMatrix(double a, double b, double c, double d, double e, double f)` | Sets the transformation matrix. PDFium stores it as floats |
| `Transform(double a, double b, double c, double d, double e, double f)` | Multiplies the object's matrix by the given one |
| `Dispose()` | See ownership below |

**Note:** Before 2.0, `GetMatrix()` used a wrong native signature and could crash the process. Fixed in 2.0.

An object added to a page belongs to that page: disposing its wrapper does not delete it. An object removed with `PdfPage.RemoveObject` belongs to the caller again and is tracked by the document: dispose it, or the document disposes it when the document is disposed. `RemoveObject` takes the live wrapper of an object on that page: it throws `ObjectDisposedException` for a disposed wrapper and returns `false` for an object that is not on the page (another page's, a form object's sub-object, or one already removed).

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
| `FontSize` | `float` | Font size in points (get and set). Setting a negative size throws `ArgumentOutOfRangeException` |
| `Color` | `Color` | Fill color (set only) |
| `StrokeColor` | `Color` | Stroke color (set only) |

The font is chosen when the text is added (`PdfPage.AddText(text, x, y, font, fontSize)`); PDFium cannot change a text object's font afterwards.

**Changed in 2.0:** the `Font` setter is removed; it never applied the font. `FontSize` now reads and writes the size stored in the object.

#### Example

```csharp
var text = page.AddText("Hello World", x: 100, y: 700, font: "Helvetica-Bold", fontSize: 24);
text.Color = Color.DarkBlue;
text.FontSize = 28;
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

Images are decoded using native libraries (libjpeg-turbo and libpng). Only these formats are accepted; anything else throws `NotSupportedException`:
- PNG
- JPEG

#### Positioning

| Method | Description |
|--------|-------------|
| `SetPositionAndSize(float x, float y, float width, float height)` | Places the image with its bottom-left corner at (`x`, `y`) and scales it to `width` × `height` points. Replaces the image's matrix |

#### Reading Pixels

| Method | Description |
|--------|-------------|
| `GetBitmap()` | The image's own pixels, without its mask or transformation, as a `RawBitmap?` (BGRA). Returns `null` if the image has no bitmap |
| `GetRenderedBitmap(PdfPage? page = null)` | The image as it appears on the page, with mask and transformation applied, as a `RawBitmap?` (BGRA). Pass the page the image is on for better color handling. Returns `null` if the image cannot be rendered |

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
| `FillColor` | `Color` | Fill color for the path (set only) |
| `StrokeColor` | `Color` | Stroke (outline) color (set only) |
| `StrokeWidth` | `float` | Stroke width in points (get and set) |
| `LineJoin` | `PdfLineJoinStyle` | Line join style (set only) |
| `LineCap` | `PdfLineCapStyle` | Line cap style (set only) |
| `SegmentCount` | `int` | Number of segments in the path |

#### Path Drawing Methods

The drawing methods return the path itself, so calls can be chained.

| Method | Description |
|--------|-------------|
| `MoveTo(float x, float y)` | Move to point (starts new subpath) |
| `LineTo(float x, float y)` | Draw line to point |
| `BezierTo(float x1, float y1, float x2, float y2, float x3, float y3)` | Draw cubic Bézier curve |
| `Close()` | Close current subpath |
| `SetDrawMode(PdfPathFillMode fillMode, bool stroke)` | Set fill and stroke behavior (returns `void`) |

#### PdfPathFillMode

| Value | Description |
|-------|-------------|
| `None` | No fill |
| `Alternate` | Alternate (even-odd) fill rule |
| `Winding` | Winding (non-zero) fill rule |

#### PdfLineJoinStyle and PdfLineCapStyle

| Enum | Values |
|------|--------|
| `PdfLineJoinStyle` | `Miter`, `Round`, `Bevel` |
| `PdfLineCapStyle` | `Butt`, `Round`, `Square` |

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
triangle.LineJoin = PdfLineJoinStyle.Round;
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

### PdfShadingObject

A shading (smooth color gradient) read from an existing page. It has no members beyond [PdfPageObject](#pdfpageobject); PDFium cannot create shading objects, so they are only returned by `GetObject`.

```csharp
public class PdfShadingObject : PdfPageObject
```

---

### PdfFormObject

A form XObject on a page: a group of objects that is drawn as one. Returned by `GetObject`.

```csharp
public class PdfFormObject : PdfPageObject
```

| Member | Description |
|--------|-------------|
| `int ObjectCount` | Number of sub-objects |
| `PdfPageObject GetObject(int index)` | The sub-object at `index`, wrapped in the class that matches its kind. Throws `ArgumentOutOfRangeException` for an index out of bounds |

```csharp
for (int i = 0; i < page.ObjectCount; i++)
{
    if (page.GetObject(i) is PdfFormObject form)
    {
        for (int j = 0; j < form.ObjectCount; j++)
            Console.WriteLine($"  {form.GetObject(j).GetType().Name}");
    }
}
```

The form object owns its sub-objects: disposing a sub-object wrapper does not delete it, and the wrapper is unusable once the form object is disposed. Unlike `PdfPage.GetObject`, each call returns a new wrapper. A sub-object cannot be removed with `PdfPage.RemoveObject`.

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
string? name = form.GetFormFieldValue("FullName");
```

**Returns:** `string?`

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

Gets whether a checkbox or radio button is checked, from PDFium's checked state (`FPDFAnnot_IsChecked`), whatever the field's export value is ("On", "Yes", "1", ...). Reads the first widget with this name; for a radio button group that is its first button.

```csharp
bool agreed = form.GetFormFieldChecked("AgreeToTerms");
```

**Exceptions:**
- `ArgumentException` — If field not found
- `InvalidOperationException` — If the field is not a checkbox or radio button

#### SetFormFieldChecked(string fieldName, bool isChecked)

Checks or unchecks a checkbox or radio button as a click in a viewer would: the field's appearance state (`/AS`) and value (`/V`) become its export value or `Off`. Acts on the first widget with this name.

```csharp
form.SetFormFieldChecked("AgreeToTerms", true);
form.SetFormFieldChecked("OptOut", false);
```

**Exceptions:**
- `ArgumentException` — If field not found
- `NotSupportedException` — If the field type does not take a value
- `InvalidOperationException` — If PDFium did not change the state: a read-only field, or unchecking a radio button (check another button of its group instead)

**Changed in 2.0:** in 1.0 this wrote `/V` as text and never changed `/AS`, so the box did not change.

#### SetListBoxSelection(string fieldName, string selectedValue)

Sets the selected value for a list box.

```csharp
form.SetListBoxSelection("Country", "United States");
```

#### SetListBoxSelections(string fieldName, string[] selectedValues)

Selects exactly the given options of a list box and clears the others. Values are matched (ordinal, case-sensitive) against the option labels in `FormField.Options` and may contain commas. PDFium's form filler (`FORM_SetIndexSelected`) writes the field as a viewer does: `/I` holds the selected indexes and `/V` an array of the selected values. An empty array clears the selection.

```csharp
form.SetListBoxSelections("Interests", new[] { "Music", "Sports", "Reading" });
```

**Exceptions:**
- `ArgumentNullException` — If `selectedValues` is null
- `ArgumentException` — If field not found, a value is not one of the options, or more than one value is given for a list box without the multi-select flag
- `InvalidOperationException` — If the field is not a list box, or PDFium did not apply the selection (for example a read-only field)

#### Dispose()

Exits the form environment. The document also disposes its forms when it is disposed.

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
| `Value` | `string` | Current value. Checkboxes and radio buttons: `"true"` or `"false"` (checked state, whatever the export value). List boxes: the first selected value |
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

#### PdfMerger(string filePath, string? password = null)

Starts with an existing PDF document.

```csharp
using var merger = new PdfMerger("existing.pdf");
```

#### PdfMerger(byte[] data, string? password = null)

Starts with an existing PDF from byte array. As with [`PdfDocument(byte[])`](#pdfdocumentbyte-data-string-password--null), the array is used in place and must not be modified until the merger is disposed.

#### PdfMerger(Stream pdfStream, string? password = null)

Starts with an existing PDF from stream.

The stream is read from its current position to its end during construction and is left positioned at its end, with the same copy and temporary-file rules as [`PdfDocument(Stream)`](#pdfdocumentstream-pdfstream-string-password--null). The merger does not use the stream afterwards, so it can be closed, reset or reused immediately. (Before 2.0 a seekable stream had to stay open for the lifetime of the merger.)

Each constructor builds the merger on a private `PdfDocument` loaded through the matching `PdfDocument` constructor, so loading, error messages (`PdfiumException`, see [Load errors](#load-errors)), saving and disposal behave exactly as they do for `PdfDocument`. A merger dropped without `Dispose()` is released by that document's finalizer.

### Thread Safety

One `PdfMerger` instance must not be used from two threads at once. Different mergers and documents may be used from different threads at the same time; the wrapper serializes the native work.

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `PageCount` | `int` | Current number of pages |

### Methods

**Exceptions** (all import, insert and delete methods):
- `ArgumentNullException` — If a source document or index array is null
- `ArgumentOutOfRangeException` — If `insertAtIndex` is not between 0 and `PageCount`, or a deleted page index is out of bounds
- `InvalidOperationException` — If PDFium fails to import the pages (for example, a page range or index that does not exist in the source)
- `PdfiumException` — If an overload that takes a file path or bytes cannot load the source document
- `ObjectDisposedException` — If the merger or the source document is disposed

#### AppendDocument(PdfDocument sourceDoc)

Appends all pages from another PDF document.

```csharp
using var source = new PdfDocument("source.pdf");
merger.AppendDocument(source);
```

#### AppendDocument(string filePath, string? password = null)

Appends all pages from a PDF file.

```csharp
merger.AppendDocument("document1.pdf");
merger.AppendDocument("document2.pdf");
```

#### AppendDocument(byte[] pdfData, string? password = null)

Appends all pages from PDF bytes.

#### AppendPages(PdfDocument sourceDoc, string? pageRange)

Appends specific pages using a page range string.

```csharp
using var source = new PdfDocument("source.pdf");
merger.AppendPages(source, "1,3,5-7");    // Pages 1, 3, 5, 6, 7 (1-based)
merger.AppendPages(source, (string?)null); // All pages
```

**Page Range Format:**
- Individual pages: `"1,3,5"`
- Ranges: `"1-5"`
- Combined: `"1,3,5-7,10"`
- All pages: `null`, typed as `string?` (a bare `null` is ambiguous with the `int[]` overload)

**Note:** Page numbers in the range string are 1-based.

#### AppendPages(PdfDocument sourceDoc, int[] pageIndices)

Appends specific pages by zero-based index.

```csharp
using var source = new PdfDocument("source.pdf");
merger.AppendPages(source, new[] { 0, 2, 4 }); // First, third, fifth pages
```

#### AppendPages(string filePath, string? pageRange, string? password = null) / AppendPages(string filePath, int[] pageIndices, string? password = null)

Load a PDF file, append the given pages and close the file again.

```csharp
merger.AppendPages("source.pdf", "2-4");
merger.AppendPages("encrypted.pdf", new[] { 0 }, password: "secret");
```

#### InsertDocument(PdfDocument sourceDoc, int insertAtIndex)

Inserts all pages from a document at a specific position.

```csharp
using var source = new PdfDocument("insert.pdf");
merger.InsertDocument(source, insertAtIndex: 2); // Insert at position 2
```

#### InsertPages(PdfDocument sourceDoc, string? pageRange, int insertAtIndex)

Inserts specific pages at a position.

```csharp
using var source = new PdfDocument("insert.pdf");
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

Deletes multiple pages. Indices are sorted in descending order first, so deleting one page does not shift the indices of the others. Pass each index once: a repeated index deletes a second page.

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

**Exceptions:**
- `InvalidOperationException` — If PDFium fails to copy the preferences

#### Dispose()

Closes the merged document.

---

## PdfMetadata

Reads PDF document metadata (the Info dictionary). Metadata is read-only: PDFium has no function to write it.

### Declaration

```csharp
public class PdfMetadata
```

**Changed in 2.0:** the property setters, `SetMetadataString`, `SetCreationDateTime`, `SetModificationDateTime`, `SetAllMetadata` and `ClearAllMetadata` are removed. They called `FPDF_SetMetaText`, which PDFium does not export, so every call threw `EntryPointNotFoundException`.

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `Title` | `string` | Document title |
| `Author` | `string` | Document author |
| `Subject` | `string` | Document subject |
| `Keywords` | `string` | Document keywords |
| `Creator` | `string` | Application that created the original document |
| `Producer` | `string` | Application that produced the PDF |
| `CreationDate` | `string` | Raw creation date string (`D:YYYYMMDDHHmmSSOHH'mm'`) |
| `ModificationDate` | `string` | Raw modification date string |
| `Trapped` | `string` | Trapped status |
| `PdfVersion` | `int` | PDF version as integer (e.g., 17 for PDF 1.7), or 0 when unknown (for example a new document) |
| `PdfVersionString` | `string` | PDF version as string (e.g., "1.7"), or `"Unknown"` |
| `CreationDateTime` | `DateTime?` | Parsed creation date, or null when missing or unparseable |
| `ModificationDateTime` | `DateTime?` | Parsed modification date, or null when missing or unparseable |

The string properties return an empty string, never null, when the entry is missing. A parsed date with a time zone offset (or `Z`) is converted to UTC (`DateTimeKind.Utc`); one without is returned as `DateTimeKind.Unspecified`.

### Methods

#### GetMetadataString(string tag)

Gets a metadata value by tag name, or an empty string when it is missing. The `PDFium.METADATA_*` constants name the standard tags.

```csharp
string customField = document.Metadata.GetMetadataString("CustomField");
```

#### GetAllMetadata()

Returns the standard metadata as a dictionary with the keys `Title`, `Author`, `Subject`, `Keywords`, `Creator`, `Producer`, `CreationDate`, `ModificationDate`, `Trapped` and `FileVersion` (the value of `PdfVersionString`). Custom entries are not included; read them with `GetMetadataString`.

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

Returns the top-level bookmarks, each with its `Children`.

```csharp
List<PdfBookmark> bookmarks = document.Bookmarks.GetAllBookmarks();

void PrintBookmarks(List<PdfBookmark> bookmarks, int indent = 0)
{
    foreach (var bookmark in bookmarks)
    {
        string target = bookmark.PageIndex is int page ? $"page {page + 1}" : "no page";
        Console.WriteLine($"{new string(' ', indent * 2)}{bookmark.Title} -> {target}");
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
| `PageIndex` | `int?` | Target page (0-indexed); `null` when the bookmark has no destination or GoTo action, or its destination does not resolve to a page of this document |
| `ChildCount` | `int` | The outline item's `/Count`: the number of descendants shown when it is open, negative when it is closed, 0 without children. Use `Children.Count` for the number of direct children |
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

The methods below read attachment data from the document. An attachment whose size is larger than an array can hold throws `InvalidDataException`.

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

Gets a specific attachment by index, or `null` when there is none at that index.

```csharp
var attachment = document.Attachments.GetAttachment(0);
if (attachment?.Data != null)
{
    // The name comes from the document: keep only its file name part
    string fileName = Path.GetFileName(attachment.Name ?? "attachment");
    File.WriteAllBytes(Path.Combine("extracted_files", fileName), attachment.Data);
}
```

An attachment name can contain a path. To write files safely, prefer `ExtractAll`, which sanitizes the names.

#### ExtractAll(string outputDirectory)

Extracts all attachments to a directory, creating it if needed. Attachment names come from the document, so each file is named after the last path component of its attachment name (`/` and `\` separate components everywhere; on macOS `:` does too, because PDFium returns `/` in a name as `:` there), with characters the file system rejects replaced by `_`; nothing is written outside `outputDirectory`. Trailing dots and spaces are removed (Windows drops them), and a Windows device name such as `CON`, `NUL.txt` or `COM1.log` gets a leading `_`, on every platform. An attachment with no usable name (empty, `.`, `..`) is written as `attachment_N`, numbered from 1 by its position in `GetAllAttachments()`. Attachments whose names are then equal, ignoring case, are not overwritten: later ones get `_2`, `_3` before the extension (`report.txt`, `report_2.txt`). An attachment without data is written as an empty file.

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
| `Name` | `string?` | File name as stored in the document (may contain a path) |
| `Size` | `long` | File size in bytes |
| `Data` | `byte[]?` | File contents; null when the attachment is empty or cannot be read |

---

## Supporting Types

### RawBitmap

```csharp
public record RawBitmap(byte[] Pixels, int Width, int Height, int Stride);
```

Pixel data with 4 bytes per pixel, rows `Stride` bytes apart. Not disposable: `Pixels` is a managed array.

- **Page renders** (`RenderPages`, `RenderPagesAsync`) are PDFium's BGRx layout: the fourth byte of each pixel is padding, not alpha, and its value must not be relied on. `Stride` is the row size PDFium allocated.
- **Converted bitmaps** (`PdfPage.GetEmbeddedThumbnail`, `PdfImageObject.GetBitmap`, `PdfImageObject.GetRenderedBitmap`) are real BGRA and tightly packed (`Stride == Width * 4`). The fourth byte is the source's alpha when it has one; gray, BGR and BGRx sources get full opacity (255).

### ImageFormat

```csharp
public enum ImageFormat { Png, Jpeg, Tiff }
```

The format for `StreamImageBytes` and `SaveAsImages`. Those methods accept `Png` and `Jpeg`; TIFF output goes through `SaveAsTiff`.

### TiffColorMode

```csharp
public enum TiffColorMode { Bilevel, Grayscale }
```

| Value | Output |
|-------|--------|
| `Bilevel` | 1-bit black and white, CCITT Group 4 compression. A luminance threshold decides each pixel |
| `Grayscale` | 8-bit gray, LZW compression |

### PdfPermissions

```csharp
[Flags]
public enum PdfPermissions : uint
```

The permission bits of an encrypted document (`PdfDocument.Permissions`), as defined by the PDF specification.

| Value | Bit |
|-------|-----|
| `None` | 0 |
| `Print` | 1 << 2 |
| `ModifyContents` | 1 << 3 |
| `CopyContents` | 1 << 4 |
| `ModifyAnnotations` | 1 << 5 |
| `FillForms` | 1 << 8 |
| `ExtractForAccessibility` | 1 << 9 |
| `AssembleDocument` | 1 << 10 |
| `PrintHighQuality` | 1 << 11 |

```csharp
bool canPrint = document.Permissions.HasFlag(PdfPermissions.Print);
```

### PdfiumException and PdfiumErrorCode

```csharp
public sealed class PdfiumException : InvalidOperationException
{
    public PdfiumException(string message, PdfiumErrorCode errorCode);
    public PdfiumErrorCode ErrorCode { get; }
}

public enum PdfiumErrorCode
{
    Unknown = 1,
    File = 2,
    Format = 3,
    Password = 4,
    Security = 5,
    Page = 6,
}
```

Thrown when a document fails to load; the values are PDFium's `FPDF_ERR_*` codes. See [Load errors](#load-errors) for their meaning and an example. The message ends with a description of the code and its number.

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
| `static Scope Enter()` | Enters the gate and returns a disposable `PdfiumRuntime.Scope` (a `readonly struct` whose `Dispose()` exits the gate). Reentrant on the same thread. Dispose the scope exactly once, on the same thread. |
| `static void ReleasePending()` | Closes native handles left behind by finalized wrapper objects. This also happens on every outermost gate entry. |
| `static void Shutdown()` | Destroys the native library. Throws `InvalidOperationException` if any wrapper object is alive, releases are pending, or the calling thread is inside a scope it entered. The library initializes again on next use. Intended for tests and controlled host shutdown. |
| `static bool IsHeldByCurrentThread` | `true` when the calling thread holds the gate. |
| `static long LiveHandleCount` | Number of native handles currently owned by wrapper objects (documents, pages, form environments, detached page objects, bitmaps being encoded), including handles waiting for deferred release. |

#### Enter()

Groups several wrapper calls into one uninterrupted native sequence. Wrapper members called inside the scope reenter the gate without waiting.

```csharp
using (PdfiumRuntime.Enter())
{
    // No other thread runs PDFium work between these calls
    var title = document.Metadata.Title;
    var author = document.Metadata.Author;
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

**Breaking change:** the raw native imports on the `PDFium` class (functions such as `PDFium.FPDF_LoadDocument` or `PDFium.FPDF_RenderPageBitmap`) are `internal` in 2.0, and so are the `LibTiff` and `LibTurboJpeg` import classes. A raw call bypassed the gate and was unsafe next to any other use of the library. There is no supported raw-call path in 2.0; functionality that is needed is exposed through the wrapper types.

The `PDFium` class itself stays public for its constants, for example:

- `PDFium.FPDF_ANNOT`, `PDFium.FPDF_PRINTING`, `PDFium.FPDF_GRAYSCALE` and the other render flags for `PdfPage.RenderToBytes`
- `PDFium.FPDF_INCREMENTAL`, `PDFium.FPDF_NO_INCREMENTAL`, `PDFium.FPDF_REMOVE_SECURITY` (save flags for `Save`, `SaveToStream` and `PdfMerger.Save`)
- `PDFium.FPDF_PAGEOBJ_*` (values of `PdfPageObject.ObjectType`)
- `PDFium.METADATA_*` (tags for `PdfMetadata.GetMetadataString`)
- `PDFium.FPDF_FORMFIELD_*` and `PDFium.FPDF_ANNOT_*` (form field and annotation type codes)

Its interop structs (`FPDF_FILEWRITE`, `FPDF_FORMFILLINFO` and the others) are internal, like the functions that take them. No public member of the library takes or returns a native pointer.

---

## PdfProcessingPool (PdfiumWrapper.Processing)

Package `PdfiumWrapper.Processing`, namespace `PdfiumWrapper.Processing`. Runs PDF operations in a dynamically sized set of worker processes. Each worker has its own PDFium, so workers render in parallel, and a native failure in one costs that job rather than the process that owns the pool. Use it when one process is not enough (see [High-Throughput Processing](HIGH-THROUGHPUT-PROCESSING.md#worker-pool)); for a handful of documents the core library alone is simpler.

**Packages:** `PdfiumWrapper.Processing` depends on exactly the `PdfiumWrapper` version it was built with (for example `[2.0.0]`), and `PdfiumWrapper` depends on the four `PdfiumWrapper.runtime.<rid>` packages (win-x64, linux-x64, osx-x64, osx-arm64) at its own exact version, so a portable build gets every platform's native libraries under `runtimes/<rid>/native`.

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
    Console.WriteLine($"{result.Value!.PageCount} pages, {result.Value.Files.Count} files");
else
    Console.WriteLine($"{result.Status}: {result.Error}");
```

### Hosting the workers

By default the pool starts **copies of your own executable** as workers. Make this the first statement of `Main`:

```csharp
public static async Task<int> Main(string[] args)
{
    if (PdfWorkerHost.TryRun())        // this process was started as a worker and has run its loop
        return Environment.ExitCode;   // keep the exit code the worker set

    await using var pool = await PdfProcessingPool.CreateAsync();
    // normal application start-up
    return 0;
}
```

Nothing has to be published per platform: the worker is your application, with your native libraries already in its output directory. Framework-dependent (`dotnet app.dll`) and self-contained deployments both work. Set `PdfPoolOptions.WorkerPath` to use a dedicated worker executable instead, for hosts whose `Main` cannot be changed.

### Construction

| Member | Description |
|--------|-------------|
| `static Task<PdfProcessingPool> CreateAsync(PdfPoolOptions? options = null, CancellationToken ct = default)` | Creates the pool and starts `MinWorkers` workers, waiting until they are ready. Throws `PdfPoolException` if a worker cannot start. Preferred |
| `PdfProcessingPool(PdfPoolOptions? options = null)` | Creates the pool; `MinWorkers` start in the background and the first jobs wait for them |

Both validate the options first (see [PdfPoolOptions](#pdfpooloptions)). The pool works on a copy of the options, so changing them afterwards has no effect.

### Operations

Each call is one job on one worker. Inputs are [`PdfInput`](#pdfinput) values; a `string` path converts implicitly.

| Method | Returns |
|--------|---------|
| `GetPageCountAsync(PdfInput input, CancellationToken ct = default)` | `Task<PdfJobResult<int>>` |
| `ConvertToPngAsync(PdfInput input, string outputDirectory, int dpi = 300, string fileNamePrefix = "page", CancellationToken ct = default)` | `Task<PdfJobResult<ImageFiles>>`: one `{prefix}_{page:D3}.png` per page |
| `ConvertToJpegAsync(PdfInput input, string outputDirectory, int quality = 90, int dpi = 300, string fileNamePrefix = "page", CancellationToken ct = default)` | `Task<PdfJobResult<ImageFiles>>` |
| `ConvertToTiffAsync(PdfInput input, string outputPath, int dpi = 200, TiffColorMode colorMode = TiffColorMode.Bilevel, byte threshold = 128, CancellationToken ct = default)` | `Task<PdfJobResult<TiffFile>>`: one multi-page file |
| `ExtractTextAsync(PdfInput input, CancellationToken ct = default)` | `Task<PdfJobResult<string[]>>`: one string per page |

A job's problems (a bad document, a crashed worker, a timeout, cancellation) are reported in the result's `Status`, not thrown. The calls throw only for invalid arguments (`ArgumentException` for a missing output path or an empty `PdfInput`) and `ObjectDisposedException` after the pool is disposed.

Batch overloads take `IEnumerable<PdfInput>` and return `IAsyncEnumerable<PdfJobResult<T>>`, yielding results **in completion order** as they finish:

```csharp
IAsyncEnumerable<PdfJobResult<int>> GetPageCountAsync(IEnumerable<PdfInput> inputs, CancellationToken ct = default)
IAsyncEnumerable<PdfJobResult<ImageFiles>> ConvertToPngAsync(IEnumerable<PdfInput> inputs, string outputRoot, int dpi = 300, string fileNamePrefix = "page", CancellationToken ct = default)
IAsyncEnumerable<PdfJobResult<ImageFiles>> ConvertToJpegAsync(IEnumerable<PdfInput> inputs, string outputRoot, int quality = 90, int dpi = 300, string fileNamePrefix = "page", CancellationToken ct = default)
IAsyncEnumerable<PdfJobResult<TiffFile>> ConvertToTiffAsync(IEnumerable<PdfInput> inputs, string outputRoot, int dpi = 200, TiffColorMode colorMode = TiffColorMode.Bilevel, byte threshold = 128, CancellationToken ct = default)
IAsyncEnumerable<PdfJobResult<string[]>> ExtractTextAsync(IEnumerable<PdfInput> inputs, CancellationToken ct = default)
```

Image and TIFF batches write each document to `{outputRoot}/{document name without extension}` (`.tiff` appended for TIFF); a name that repeats within the batch gets `-2`, `-3`, ... appended so documents never overwrite each other. Names are compared as the file system resolves them, ignoring case, so `a.pdf` and `A.pdf` never share a directory. Characters a file name cannot hold become `_`, and a name that is empty, `.` or `..`, or ends in a dot or a space, becomes `document`; every output lies directly under `outputRoot`.

Leaving a batch early (`break`, an exception, or disposing the enumerator) cancels the jobs it has already submitted, which end as `Cancelled`, and submits no more. Cancelling the batch's `ct` is different: the enumeration itself throws `OperationCanceledException`, by design, after cancelling the jobs in the same way.

```csharp
IEnumerable<PdfInput> files = Directory.GetFiles("in", "*.pdf").Select(path => (PdfInput)path);
await foreach (var r in pool.ConvertToPngAsync(files, "out", dpi: 150, ct: ct))
    Console.WriteLine($"{r.Input}: {r.Status} in {r.Timings.Total.TotalMilliseconds:F0} ms");
```

Output files are written to a temporary name and renamed when complete, so a crashed or cancelled job leaves no half-written file.

### PdfInput

```csharp
public readonly struct PdfInput
```

| Member | Description |
|--------|-------------|
| `static PdfInput FromFile(string path, string? password = null)` | The worker reads the file directly |
| `static PdfInput FromBytes(byte[] bytes, string name, string? password = null)` | Written to a temporary file for the job; `name` is what the result reports |
| `static PdfInput FromStream(Stream stream, string name, string? password = null)` | Read to its end during submission; the stream may be closed afterwards |
| `implicit operator PdfInput(string path)` | Same as `FromFile(path)` |
| `string? Path`, `byte[]? Bytes`, `Stream? Stream` | The source; exactly one is set |
| `string Name` | What `PdfJobResult<T>.Input` reports: the path, or the given name |
| `string? Password` | The password, if any |

The factories throw `ArgumentNullException` for a null argument. A `default(PdfInput)` has no source and is rejected with `ArgumentException` when submitted.

### Pool Behavior

- **Backpressure:** at most `QueueCapacity` jobs wait for a worker; a submission beyond that awaits a slot (and raises `QueueFull`). No exception, no unbounded growth. A job cancelled while waiting for a slot ends as `Cancelled`.
- **Failures never fail the pool.** A worker that crashes (for example a native abort on a damaged PDF), hangs past `JobTimeout` or sends malformed data is killed and replaced. Its job is reported as `WorkerCrashed` or `TimedOut` and retried on a fresh worker up to `MaxAttempts`. A job the document itself rejects (bad file, wrong password) is `Failed` and not retried. Other jobs are unaffected.
- **Neighbours on a worker are not charged.** With `JobsPerWorker` above 1, a worker killed for one job's timeout or cancellation takes its other jobs down too; they run again without using an attempt. When a worker running several jobs crashes, which one caused it is unknown: each runs again alone on a worker without using an attempt, and only the one that crashes again is charged. A job gets at most `MaxAttempts` such free runs.
- **Workers that cannot start.** Once `MaxConsecutiveStartFailures` starts in a row have failed and no worker is alive, the jobs waiting for a worker end as `Failed` with the start error instead of waiting forever. A job submitted later waits for one more start attempt; the first successful start resets the count.
- **Memory retirement** (`MaxWorkerMemoryBytes`): the worker takes no new job, finishes the ones it is running (each within its `JobTimeout`), then shuts down and is replaced.
- **Cancellation:** a cancelled job that has not started is dropped; one in flight is asked to stop between pages and its partial output removed. Either way the result is `Cancelled`.
- **`DisposeAsync`:** stops accepting jobs, cancels queued ones, lets in-flight ones finish (up to `JobTimeout`; any still running then end as `Cancelled`), then stops every worker and deletes the pool's temp directory. Submitting afterwards throws `ObjectDisposedException`.
- **Workers never outlive the application.** If the process that owns the pool ends without disposing it (a crash, a kill), its workers end too. On Windows each worker is put in a job object that Windows kills with the owning process; this is best effort (a host that forbids nested job objects, for example, leaves the worker out). On every platform a worker whose standard input closes asks its jobs to stop and exits; if a job does not stop within 5 seconds (hung in native code, for example), the worker exits anyway with exit code 5.
- **Temporary files** (spooled byte and stream inputs, large text results) go in a directory the pool creates for itself, `pdfium-pool-{pid}-{guid}` under `TempDirectory`, and deletes on `DisposeAsync`.
- **Large text results** (above 4 MB as UTF-16, about two million characters) travel through a file in that directory. The pool reads such a file only from there, deletes it once read, and deletes it as well when nobody will read it (the job was cancelled, timed out or its worker died).

### Pool Properties

| Property | Description |
|----------|-------------|
| `int Workers` | Workers alive, including ones still starting |
| `int BusyWorkers` | Workers with at least one job in flight |
| `int RunningJobs` | Jobs in flight across all workers |
| `int QueuedJobs` | Jobs waiting for a worker |
| `PdfPoolStatistics Statistics` | Counters since creation (see [Events and Statistics](#events-and-statistics)) |
| `event EventHandler<PdfPoolEvent>? Events` | Worker and job events (see [Events and Statistics](#events-and-statistics)) |

### PdfPoolOptions

```csharp
public sealed class PdfPoolOptions
```

Validated when the pool is created: a value outside its range throws `ArgumentOutOfRangeException`, and a `WorkerPath` that does not exist throws `PdfPoolException`.

| Property | Type | Default | Valid | Description |
|----------|------|---------|-------|-------------|
| `MinWorkers` | `int` | 1 | ≥ 0, ≤ `MaxWorkers` | Workers kept alive and warm. Started when the pool is created. 0 starts workers only when jobs arrive |
| `MaxWorkers` | `int` | `DefaultMaxWorkers` (half the logical processors, at least 1) | ≥ 1 | Upper bound on worker processes, including a worker retired for memory that is still finishing its jobs (its replacement starts when it exits). A worker is one core, so beyond the fast cores extra workers add memory and little throughput |
| `JobsPerWorker` | `int` | 2 | 1–16 | Jobs a worker runs at once. PDFium serializes rendering inside a process, so a second job lets one document encode and write while the other renders. More mostly adds memory (one rendered page per job in flight) |
| `ScaleUpAfter` | `TimeSpan` | 500 ms | ≥ 0 | A worker is added when every slot of every worker has been busy and jobs have been waiting for this long. A burst reaches `MaxWorkers` in seconds; a single stray job never starts a process |
| `IdleTimeout` | `TimeSpan` | 60 s | > 0 | A worker idle for this long is stopped, down to `MinWorkers` |
| `JobTimeout` | `TimeSpan` | 2 min | > 0, ≤ `MaxTimeout` | Per attempt. On expiry the worker is killed and replaced. There is no infinite timeout |
| `MaxAttempts` | `int` | 2 | ≥ 1 | Attempts for a job whose worker crashed or timed out. Only the job that ended the attempt is charged (see [Pool Behavior](#pool-behavior)) |
| `QueueCapacity` | `int` | 1,000 | ≥ 1 | Jobs that may wait. Submissions beyond this wait for a slot |
| `MaxWorkerMemoryBytes` | `long?` | null | > 0 when set | When set, a worker whose working set exceeds this after a job is retired: it finishes its running jobs, then is replaced |
| `WorkerStartTimeout` | `TimeSpan` | 30 s | > 0, ≤ `MaxTimeout` | Time a new worker gets to report ready |
| `MaxConsecutiveStartFailures` | `int` | 3 | ≥ 1 | Worker starts that may fail in a row, with no worker alive, before the jobs waiting for a worker end as `Failed` with the start error |
| `WorkerPath` | `string?` | null | existing file | Executable (or `.dll`, run through `dotnet`) to start as a worker. Null re-launches this process |
| `WorkerArguments` | `IReadOnlyList<string>` | empty | | Arguments for `WorkerPath`. Ignored when it is null |
| `WorkerEnvironment` | `IDictionary<string, string>` (get only) | empty | | Extra environment variables for workers; add entries to it |
| `TempDirectory` | `string?` | system temp | | Parent of the pool's own temp directory (see [Pool Behavior](#pool-behavior)) |

Static members:

| Member | Description |
|--------|-------------|
| `static int DefaultMaxWorkers` | Half the logical processors, at least 1 |
| `static TimeSpan MaxTimeout` | The longest accepted `JobTimeout` or `WorkerStartTimeout`: `uint.MaxValue - 1` milliseconds (about 49.7 days), the limit of .NET timers |

Fixed size is `MinWorkers == MaxWorkers`.

### PdfJobResult

```csharp
public sealed record PdfJobResult<T>(string Input, PdfJobStatus Status, T? Value, string? Error,
    int Attempts, int WorkerPid, PdfJobTimings Timings)
{
    public bool IsSuccess { get; }   // Status == Succeeded
}

public sealed record PdfJobTimings(TimeSpan Queued, TimeSpan Processing, TimeSpan Total);
public sealed record ImageFiles(int PageCount, IReadOnlyList<string> Files, long TotalBytes);
public sealed record TiffFile(int PageCount, string Path, long Bytes);
```

| `PdfJobStatus` | Meaning |
|---|---|
| `Succeeded` | `Value` is set |
| `Failed` | The document or request was rejected (bad file, wrong password, missing output path). Not retried. Also the status when no worker could be started (`MaxConsecutiveStartFailures`) or the pool's dispatcher stopped on an unexpected error; `Error` says which |
| `TimedOut` | The last attempt exceeded `JobTimeout` |
| `Cancelled` | Cancelled by the caller, or the pool was disposed while the job waited or ran |
| `WorkerCrashed` | The worker process died during the last attempt |

`Error` is the worker's exception type and message, or the pool's reason, when the job did not succeed. `WorkerPid` is the worker that produced the result, or 0.

`Attempts` counts the attempts charged to the job: 1 unless its own worker crashed or it timed out, and 0 for a job that never reached a worker. A run ended only because a neighbour on the same worker brought it down is not counted.

`PdfJobTimings`: `Queued` runs from submission to the dispatch of the last attempt, `Processing` from that dispatch to the result, and `Total` from submission to the final result, all attempts included. `ImageFiles.Files` lists one file per page, in page order.

### PdfWorkerHost

```csharp
public static class PdfWorkerHost
{
    public const string EnvironmentVariable = "PDFIUMWRAPPER_WORKER";   // "1" in a worker process
    public const string DiagnosticsVariable = "PDFIUMWRAPPER_DIAGNOSTICS"; // "1" turns on wrapper diagnostics in a worker (tests, measurements)
    public static bool TryRun();
}
```

`TryRun()` returns `false` immediately in a normal process. In a worker it runs the job loop until the pool shuts it down, or until its standard input closes, and then returns `true`; the process should exit. When the loop ends with an error, `TryRun()` sets `Environment.ExitCode` first, so return `Environment.ExitCode` from `Main` (as in [Hosting the workers](#hosting-the-workers)) rather than 0. When standard input closes and the jobs do not stop within 5 seconds, the worker ends the process itself with exit code 5 and `TryRun()` does not return.

A worker writes protocol frames to its standard output, so nothing else in the process may write there; calling `TryRun()` first in `Main` guarantees that.

### Events and Statistics

```csharp
pool.Events += (sender, e) => logger.LogInformation("{Kind} worker={Pid} job={JobId} {Detail}", e.Kind, e.WorkerPid, e.JobId, e.Detail);
```

```csharp
public sealed class PdfPoolEvent : EventArgs
{
    public PdfPoolEventKind Kind { get; }
    public int WorkerPid { get; }            // the worker involved, or 0
    public long JobId { get; }               // the job involved, or 0
    public string? Detail { get; }
    public DateTimeOffset Timestamp { get; } // UTC, when the event was raised
}
```

`ToString()` returns `"{Kind} worker={WorkerPid} job={JobId} {Detail}"`.

`PdfPoolEventKind`: `WorkerStarting`, `WorkerReady`, `WorkerStopped`, `WorkerCrashed`, `WorkerStartFailed`, `WorkerRetiredForMemory`, `ScaledUp`, `ScaledDown`, `JobDispatched`, `JobCompleted`, `JobFailed`, `JobTimedOut`, `JobRetried`, `JobCancelled`, `QueueFull`, `WorkerMessage`. A worker's standard error arrives as `WorkerMessage` events, one per line, with the line as the detail; a line longer than 4,096 characters is cut there and ends with ` [truncated]`. Malformed protocol data from a worker is reported the same way. Handlers run on pool threads and must be quick and must not throw.

`Statistics` returns a snapshot of the counters since the pool was created:

```csharp
public sealed record PdfPoolStatistics(
    long JobsSubmitted, long JobsSucceeded, long JobsFailed, long JobsTimedOut,
    long JobsCancelled, long JobsCrashed, long JobsRetried,
    long WorkersStarted, long WorkersStopped, long WorkersCrashed, long WorkersRetiredForMemory,
    long ScaleUps, long ScaleDowns);
```

### PdfPoolException

```csharp
public sealed class PdfPoolException : Exception
{
    public PdfPoolException(string message);
    public PdfPoolException(string message, Exception? inner);
}
```

Raised for pool-level problems: a `WorkerPath` that does not exist, or a worker that cannot start during `CreateAsync`. Job failures are never thrown; they are reported in `PdfJobResult<T>.Status`.
