# PdfiumWrapper

<img src="icon.png" alt="PdfiumWrapper Icon" width="64" height="64" align="left" />

A modern, high-level .NET 10 wrapper for PDFium that makes PDF manipulation easy and intuitive. This library provides a clean C# API for working with PDF documents, including creation, rendering, merging, form filling, metadata management, and more.

## Features

- **PDF Creation** — Create new PDF documents from scratch with text, images, and shapes
- **Page Editing** — Add text objects, images, paths, and rectangles to pages
- **PDF Rendering** — Convert PDF pages to images (PNG, JPEG) or raw pixel buffers with customizable DPI
- **TIFF Export** — High-performance multi-page TIFF output (bilevel CCITT G4 or grayscale LZW) via native libtiff
- **PDF Merging** — Combine multiple PDFs or extract specific pages
- **Form Filling** — Read and write PDF form fields (text fields, checkboxes, dropdowns, radio buttons)
- **Metadata Management** — Read and modify PDF metadata (title, author, keywords, etc.)
- **Bookmarks** — Access PDF bookmarks/outlines with full hierarchy
- **Attachments** — Extract and manage embedded file attachments
- **Page Management** — Extract text, get page dimensions, render individual pages
- **Async Support** — Async/await patterns for UI responsiveness
- **Password-Protected PDFs** — Open and work with encrypted documents
- **Save Support** — Save modified documents back to file or stream

## Installation

```bash
dotnet add package PdfiumWrapper
```

For high volumes, the optional worker pool runs conversions in parallel worker processes with crash isolation and dynamic sizing:

```bash
dotnet add package PdfiumWrapper.Processing
```

See [High-Throughput Processing](docs/HIGH-THROUGHPUT-PROCESSING.md#worker-pool).

## Requirements

- .NET 10.0 or later
- Platform-specific PDFium binaries (included in the package)
- Platform-specific native libraries (included): libtiff + tiff_shim (TIFF), libjpeg-turbo (JPEG), pdfium_png (PNG)

## Quick Start

### Load a PDF Document

```csharp
using PdfiumWrapper;

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

### Create a New PDF Document

```csharp
using PdfiumWrapper;
using System.Drawing;

// Create a new empty document
using var document = new PdfDocument();

// Add a page (US Letter size)
using var page = document.AddPage(width: 612, height: 792);

// Add text
var text = page.AddText("Hello World", x: 100, y: 700);
text.Font = "Helvetica";
text.FontSize = 24;
text.Color = Color.Black;

// Add a rectangle
var rect = page.AddRectangle(x: 100, y: 500, width: 200, height: 100,
    fillColor: Color.LightBlue, strokeColor: Color.Black);

// Generate content and save
page.GenerateContent();
document.Save("created.pdf");
```

### Convert PDF to Images

```csharp
using var document = new PdfDocument("document.pdf");

// Save all pages as PNG at 300 DPI
document.SaveAsPngs("output_folder", fileNamePrefix: "page", dpi: 300);

// Save as JPEG with quality setting
document.SaveAsJpegs("output_folder", fileNamePrefix: "page", quality: 90, dpi: 200);

// Save as multi-page TIFF (bilevel CCITT G4 — ideal for scanned documents)
document.SaveAsTiff("output.tiff", dpi: 200);

// Save TIFF to a stream
using var stream = new MemoryStream();
document.SaveAsTiff(stream, dpi: 200, colorMode: TiffColorMode.Grayscale);
```

### Fill a PDF Form

```csharp
using var document = new PdfDocument("form.pdf");
var form = document.GetForm();

if (form != null)
{
    form.SetFormFieldValue("FullName", "John Doe");
    form.SetFormFieldValue("Email", "john@example.com");
    form.SetFormFieldChecked("AgreeToTerms", true);
    
    document.Save("filled_form.pdf");
}
```

### Merge PDF Documents

```csharp
using var merger = new PdfMerger();
merger.AppendDocument("document1.pdf");
merger.AppendDocument("document2.pdf");
merger.Save("merged.pdf");
```

## Documentation

| Document | Description |
|----------|-------------|
| [API Reference](docs/API-REFERENCE.md) | Complete API documentation for all classes |
| [PDF Editing Guide](docs/PDF-EDITING.md) | Creating PDFs, adding text, images, and shapes |
| [Best Practices](docs/BEST-PRACTICES.md) | Thread safety, ASP.NET Core guidance, performance tips |
| [Examples](docs/EXAMPLES.md) | Detailed code examples for common scenarios |
| [Troubleshooting](docs/TROUBLESHOOTING.md) | Common issues and solutions |
| [High-Throughput Processing](docs/HIGH-THROUGHPUT-PROCESSING.md) | Batch processing, parallelism, memory management |
| [Building Native Libraries](docs/BUILDING-NATIVE-LIBS.md) | How to compile native libraries (libtiff, libjpeg-turbo, libpng, zlib-ng) for each platform |

## Platform Support

This library includes native binaries for:

- Windows (x64)
- macOS (x64 and ARM64)
- Linux (x64)

Native libraries bundled: **PDFium** (PDF rendering), **libtiff** + **tiff_shim** (TIFF export), **libjpeg-turbo** (JPEG encoding/decoding), **pdfium_png** (PNG encoding/decoding, statically links libpng + zlib-ng). See [Building Native Libraries](docs/BUILDING-NATIVE-LIBS.md) for compilation instructions.

## Thread Safety

PDFium allows one native call per process at a time, across all documents. PdfiumWrapper 2.0 enforces this itself: every operation enters one process-wide gate (`PdfiumRuntime`), so you do not need your own lock around the library.

- **Safe:** using different objects (documents, mergers) from different threads at the same time. Native work (loading, rendering, text, forms, saving) takes turns; image encoding and output writes overlap.
- **Not supported:** using one `PdfDocument`, `PdfPage`, `PdfForm`, `PdfMerger`, or page object from two threads at once.
- In async services, prefer the async methods (`SaveAsTiffAsync`, `StreamImageBytesAsync`, ...): they wait for the gate without blocking a thread.

See [Best Practices](docs/BEST-PRACTICES.md) and [High-Throughput Processing](docs/HIGH-THROUGHPUT-PROCESSING.md) for multi-threaded scenarios and sizing.

## Upgrading to 2.0

- The raw native imports on the `PDFium` class (for example `PDFium.FPDF_LoadDocument`) are now `internal`. The class stays public for its constants and structs (`PDFium.FPDF_ANNOT`, `PDFium.FPDF_INCREMENTAL`, ...). There is no supported raw-call path in 2.0; use the wrapper types.
- New `PdfiumRuntime` class: `Enter()`, `ReleasePending()`, `Shutdown()`, `IsHeldByCurrentThread`, `LiveHandleCount`. See the [API Reference](docs/API-REFERENCE.md#pdfiumruntime).
- `new PdfDocument(Stream)` and `new PdfMerger(Stream)` read the stream to its end during construction, copying a `MemoryStream`'s bytes as well; the stream can be closed, reset or reused immediately afterwards. The `byte[]` constructors still use the array in place: do not modify it while the document is open.
- Saving to a stream writes after the PDF has been serialized in memory. An exception thrown by the destination stream (for example `IOException`) now reaches the caller unchanged.
- A document owns the forms returned by `GetForm()` and the page objects removed from its pages; disposing the document disposes them.
- `PdfImageObject.GetBitmap()` and `GetRenderedBitmap()` return managed BGRA pixels (`RawBitmap?`) instead of a native bitmap handle. `GetRenderedBitmap` takes a `PdfPage` instead of a page handle. `PdfImageObject.SetBitmap` and `SetImage`, which took native handles, are `internal`; add images with `PdfPage.AddImage`.
- `StreamImageBytesAsync` and `StreamJpegBytesAsync` return without waiting for the native gate; an empty document is reported when enumeration starts rather than by the call.
- TIFF output (`SaveAsTiff`, `SaveAsTiffAsync`) renders pages in 8-bit gray instead of 32-bit color. It is faster and uses a quarter of the memory per page. Text is anti-aliased slightly differently at that depth, so TIFF files are not pixel-identical to those from 1.x: on text pages roughly 1-2% of pixels differ, at glyph edges.

## License

This project is licensed under the MIT License.

## Credits

- [PDFium](https://pdfium.googlesource.com/pdfium/) — Google's open-source PDF rendering engine
- [libtiff](https://libtiff.gitlab.io/libtiff/) — TIFF image library
- [libjpeg-turbo](https://libjpeg-turbo.org/) — SIMD-accelerated JPEG encoding/decoding
- [libpng](http://www.libpng.org/) — PNG reference library
- [zlib-ng](https://github.com/zlib-ng/zlib-ng) — SIMD-accelerated compression (embedded in pdfium_png)

## Support

For issues, questions, or contributions, please visit the [GitHub repository](https://github.com/malweka/PdfiumWrapper).
