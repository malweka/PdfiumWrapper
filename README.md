# Malweka.PdfiumSdk

A modern, high-level .NET 8 wrapper for PDFium that makes PDF manipulation easy and intuitive. This library provides a clean C# API for working with PDF documents, including creation, rendering, merging, form filling, metadata management, and more.

## Features

- **PDF Creation** — Create new PDF documents from scratch with text, images, and shapes
- **Page Editing** — Add text objects, images, paths, and rectangles to pages
- **PDF Rendering** — Convert PDF pages to images (PNG, JPEG, WebP, etc.) with customizable DPI
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
dotnet add package Malweka.PdfiumSdk
```

## Requirements

- .NET 8.0 or later
- SkiaSharp (automatically installed as dependency)
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

### Create a New PDF Document

```csharp
using Malweka.PdfiumSdk;
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

## Platform Support

This library includes native PDFium binaries for:

- Windows (x64)
- macOS (x64 and ARM64)
- Linux (x64)

## Thread Safety Warning

⚠️ **Important:** PDFium is not thread-safe. Do not access the same `PdfDocument`, `PdfForm`, or `PdfPage` instance from multiple threads concurrently. See [Best Practices](docs/BEST-PRACTICES.md) for guidance on multi-threaded scenarios.

## License

This project is licensed under the MIT License.

## Credits

- [PDFium](https://pdfium.googlesource.com/pdfium/) — Google's open-source PDF rendering engine
- [SkiaSharp](https://github.com/mono/SkiaSharp) — Cross-platform 2D graphics library

## Support

For issues, questions, or contributions, please visit the [GitHub repository](https://github.com/malweka/PdfiumWrapper).
