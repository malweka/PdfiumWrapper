# PdfiumWrapper

<img src="https://raw.githubusercontent.com/malweka/PdfiumWrapper/main/icon.png" alt="PdfiumWrapper Icon" width="64" height="64" align="left" />

A modern, high-level .NET 10 wrapper for PDFium that makes PDF manipulation easy and intuitive. This library provides a clean C# API for working with PDF documents, including creation, rendering, merging, form filling, metadata, bookmarks, attachments, and more.

## Features

- **PDF Creation** — Create new PDF documents from scratch with text, images, and shapes
- **Page Editing** — Add text objects, images, paths, and rectangles to pages
- **PDF Rendering** — Convert PDF pages to images (PNG, JPEG) or raw BGRA pixel buffers with customizable DPI; render sizes are capped (2^28 pixels by default) so a hostile page box cannot exhaust memory
- **TIFF Export** — High-performance multi-page TIFF output (bilevel CCITT G4 or grayscale LZW) via native libtiff
- **PDF Merging** — Combine multiple PDFs or extract specific pages
- **Form Filling** — Read and write PDF form fields (text fields, checkboxes, dropdowns, radio buttons)
- **Metadata** — Read PDF metadata (title, author, keywords, dates, PDF version, document ID, permissions); metadata is read-only
- **Bookmarks** — Read PDF bookmarks/outlines with full hierarchy
- **Attachments** — List and extract embedded file attachments (read-only)
- **Page Management** — Extract text, get page sizes and page labels, read embedded thumbnails, add and delete pages
- **Async Support** — Async methods that never block on the native gate or post to the caller's synchronization context, with `CancellationToken` support
- **Thread Safety** — Different documents can be used from different threads; the library serializes native calls itself
- **Typed Load Errors** — A document that fails to load throws `PdfiumException` with an `ErrorCode` (wrong password, corrupt file, ...)
- **Password-Protected PDFs** — Open and work with encrypted documents
- **Save Support** — Save modified documents back to file or stream
- **Worker Pool** (separate package `PdfiumWrapper.Processing`) — Run conversions in parallel worker processes, so a native crash costs one job and not the service

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

- .NET 10.0 or later (2.0 targets `net10.0` only)
- Windows x64, Linux x64, or macOS x64/ARM64
- On Windows, the Visual C++ 2015-2022 x64 runtime (`tiff.dll` and `pdfium_png.dll` link against it; it is present on most machines)

The native libraries (PDFium, libtiff + tiff_shim, libjpeg-turbo, pdfium_png) ship in four runtime packages, `PdfiumWrapper.runtime.win-x64`, `linux-x64`, `osx-x64` and `osx-arm64`. `PdfiumWrapper` depends on all four, so `dotnet add package PdfiumWrapper` is all you need:

- **Portable build** (no `RuntimeIdentifier`): the output gets every platform under `runtimes/<rid>/native`, and the matching one is loaded at run time.
- **RID-specific build or publish** (for example `dotnet publish -r linux-x64`): only that platform's binaries are copied, which gives a smaller output.

## Quick Start

### Load a PDF Document

```csharp
using PdfiumWrapper;

// Load from file
using var fromFile = new PdfDocument("sample.pdf");

// Load from byte array (used in place: do not modify the array while the document is open)
byte[] pdfBytes = File.ReadAllBytes("sample.pdf");
using var fromBytes = new PdfDocument(pdfBytes);

// Load from stream (read to its end during construction; the stream can be closed afterwards)
using (var stream = File.OpenRead("sample.pdf"))
using (var fromStream = new PdfDocument(stream))
{
    Console.WriteLine(fromStream.PageCount);
}

// Load a password-protected PDF; a failed load throws PdfiumException
try
{
    using var secured = new PdfDocument("secure.pdf", password: "secret");
}
catch (PdfiumException ex) when (ex.ErrorCode == PdfiumErrorCode.Password)
{
    Console.WriteLine("Wrong or missing password");
}
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
var text = page.AddText("Hello World", x: 100, y: 700, font: "Helvetica", fontSize: 24);
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

// Save as JPEG with quality setting (default quality is 90)
document.SaveAsJpegs("output_folder", fileNamePrefix: "page", quality: 85, dpi: 200);

// Save as multi-page TIFF (bilevel CCITT G4 — ideal for scanned documents)
document.SaveAsTiff("output.tiff", dpi: 200);

// Save TIFF to a stream
using var stream = new MemoryStream();
document.SaveAsTiff(stream, dpi: 200, colorMode: TiffColorMode.Grayscale);

// Async, with cancellation (checked before each page)
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
await document.SaveAsPngsAsync("output_folder", "page", dpi: 150, cancellationToken: cts.Token);
```

Files are named `{prefix}_{n:D3}.{ext}` (`page_001.png`, ...). Each render is capped at 268,435,456 pixels; the `PdfiumWrapper.MaxRenderPixels` `AppContext` data key changes the cap (see [Render size limit](docs/API-REFERENCE.md#render-size-limit-and-dpi-validation)).

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
| [High-Throughput Processing](docs/HIGH-THROUGHPUT-PROCESSING.md) | Batch processing, parallelism, memory management, the worker pool |
| [Building Native Libraries](docs/BUILDING-NATIVE-LIBS.md) | How to compile native libraries (libtiff, libjpeg-turbo, libpng, zlib-ng) for each platform |
| [Changelog](CHANGELOG.md) | Release notes and breaking changes |

## Platform Support

This library includes native binaries for:

- Windows (x64)
- macOS (x64 and ARM64)
- Linux (x64)

Native libraries bundled: **PDFium** chromium/8076 (PDF rendering), **libtiff** 4.7.2 + **tiff_shim** (TIFF export), **libjpeg-turbo** 3.2.0 (JPEG encoding/decoding), **pdfium_png** (PNG encoding/decoding, statically links libpng 1.6.59 + zlib-ng 2.3.3). See [Building Native Libraries](docs/BUILDING-NATIVE-LIBS.md) for compilation instructions.

On Linux (glibc), a long-running process that renders many pages keeps freed page memory unless `MALLOC_ARENA_MAX=2` is set before it starts. The worker pool sets it for its workers and trims idle workers; for a service that renders in-process, set it on the service or its container image. See [Memory on Linux](docs/HIGH-THROUGHPUT-PROCESSING.md#memory-on-linux).

## Thread Safety

PDFium allows one native call per process at a time, across all documents. PdfiumWrapper 2.0 enforces this itself: every operation enters one process-wide gate (`PdfiumRuntime`), so you do not need your own lock around the library.

- **Safe:** using different objects (documents, mergers) from different threads at the same time. Native work (loading, rendering, text, forms, saving) takes turns; image encoding and output writes overlap.
- **Not supported:** using one `PdfDocument`, `PdfPage`, `PdfForm`, `PdfMerger`, or page object from two threads at once.
- In async services, prefer the async methods (`SaveAsTiffAsync`, `StreamImageBytesAsync`, ...): they wait for the gate without blocking a thread, and never resume on the caller's synchronization context.

See [Best Practices](docs/BEST-PRACTICES.md) and [High-Throughput Processing](docs/HIGH-THROUGHPUT-PROCESSING.md) for multi-threaded scenarios and sizing.

## Upgrading to 2.0

2.0 targets .NET 10 and has breaking changes. The main ones:

- the raw `PDFium` imports and the interop classes are internal;
- `GetObject` returns typed page objects;
- load failures throw `PdfiumException` with an `ErrorCode`;
- `PdfMetadata` is read-only;
- async methods take a `CancellationToken`;
- renders are capped in size;
- the default JPEG quality is 90;
- TIFF pages render in 8-bit gray.

See [CHANGELOG.md](CHANGELOG.md) for the full list.

## License

This project is licensed under the MIT License.

The native libraries in the runtime packages contain third-party software (PDFium and the libraries it bundles, libtiff, libjpeg-turbo, libpng and zlib-ng) under their own permissive licenses. Their license texts are in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt), which is also included in the packages.

## Credits

- [PDFium](https://pdfium.googlesource.com/pdfium/) — Google's open-source PDF rendering engine
- [libtiff](https://libtiff.gitlab.io/libtiff/) — TIFF image library
- [libjpeg-turbo](https://libjpeg-turbo.org/) — SIMD-accelerated JPEG encoding/decoding
- [libpng](http://www.libpng.org/) — PNG reference library
- [zlib-ng](https://github.com/zlib-ng/zlib-ng) — SIMD-accelerated compression (embedded in pdfium_png)

## Support

For issues, questions, or contributions, please visit the [GitHub repository](https://github.com/malweka/PdfiumWrapper).
