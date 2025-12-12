# Copilot Instructions for Malweka.PdfiumSdk

## Project Overview

This is a .NET 8 wrapper around Google's PDFium native library for PDF manipulation. The architecture consists of high-level C# classes that interact with low-level P/Invoke bindings to native PDFium libraries.

**Key architectural layers:**
- **High-level API**: `PdfDocument`, `PdfPage`, `PdfForm`, `PdfMerger` - user-facing classes with .NET idioms
- **P/Invoke layer**: `PDFium.cs` + partial classes (`PDFium.Edit.cs`, `PDFium.FormFill.cs`, etc.) - native interop using `LibraryImport`
- **Native binaries**: Platform-specific PDFium libraries in `src/libs/{win-x64,osx-x64,osx-arm64}/`

## Critical Thread Safety Requirements

⚠️ **PDFium is NOT thread-safe.** This is the #1 source of bugs and crashes.

- Never share `PdfDocument`, `PdfPage`, or `PdfForm` instances across threads
- Async methods (e.g., `ConvertToBitmapsAsync`) use `Task.Yield()` for UI responsiveness but process sequentially
- Each thread/request must create its own document instance
- See `docs/BEST-PRACTICES.md` for safe patterns (document-per-thread, sequential processing)

**Code pattern to follow:**
```csharp
// ✅ SAFE: Each parallel operation gets its own instance
await Parallel.ForEachAsync(files, async (file, ct) => {
    using var doc = new PdfDocument(file);
    // Process...
});

// ❌ UNSAFE: Sharing document across threads
var doc = new PdfDocument("file.pdf");
Parallel.For(0, doc.PageCount, i => doc.GetPage(i)); // CRASH!
```

## Resource Management

All PDF objects implement `IDisposable` and **must** be disposed:
- `PdfDocument`, `PdfPage`, `PdfForm`, `PdfMerger`, `PdfPageObject` (and subclasses)
- Always use `using` declarations or statements
- Pages obtained via `GetPage()` must be disposed; internal page access handles disposal automatically

```csharp
using var doc = new PdfDocument("file.pdf");
using var page = doc.GetPage(0);
// Automatic cleanup on scope exit
```

## Page Editing Workflow

When adding or modifying page content, you **must** call `GenerateContent()` before saving:

```csharp
using var doc = new PdfDocument();
using var page = doc.AddPage(612, 792);

page.AddText("Hello World", 100, 700);
page.AddRectangle(100, 500, 200, 100, Color.Blue, Color.Black);

page.GenerateContent();  // REQUIRED - finalizes page objects
doc.Save("output.pdf");
```

This pattern applies to all page object additions: text, images, paths, rectangles.

## P/Invoke Architecture

The `PDFium` class is split into partial classes by functionality:
- `PDFium.cs` - Core document/page/bitmap operations + native library loading
- `PDFium.Edit.cs` - Page editing (fpdf_edit.h)
- `PDFium.FormFill.cs` - Form field operations
- `PDFium.Metadata.cs` - Document metadata
- `PDFium.Annot.cs` - Annotations/attachments
- `PDFium.Ppo.cs` - Page import/merge operations

**Native library loading strategy:**
- Custom `DllImportResolver` handles cross-platform loading
- Runtime checks `RuntimeInformation.IsOSPlatform` and `ProcessArchitecture`
- Binaries stored in `src/libs/{platform}/` and copied to `runtimes/{platform}/native/` during build
- Fallback to standard NativeLibrary.TryLoad if custom paths fail

## Testing Conventions

- Uses xUnit with custom `[Collection("PDF Tests")]` attribute for test isolation
- Test PDFs in `Docs/` subdirectory, copied via `.csproj` `<None Update="Docs\*.pdf">`
- `Bootstrapper.cs` uses `[ModuleInitializer]` to clean/create `TestOutput/` directory
- All tests implement `IDisposable` for cleanup of temp directories created during tests
- Use `CreateTempDirectory()` helper pattern for file output tests

**Run tests:**
```bash
dotnet test src/Malweka.PdfiumSdk.Tests/Malweka.PdfiumSdk.Tests.csproj
```

## Build Configuration

- Target: .NET 8 (`net8.0`)
- `AllowUnsafeBlocks=true` in main project (required for pointer operations)
- Single dependency: `SkiaSharp` (for image rendering/conversion)
- Platform-specific native libraries defined in `.csproj` with `<Content Include>` + `CopyToOutputDirectory`
- Linux x64 support commented out but available in codebase

## Common Patterns

**Loading PDFs (multiple input types):**
```csharp
using var doc = new PdfDocument("path.pdf", password: "optional");
using var doc = new PdfDocument(byteArray, password: "optional");
using var doc = new PdfDocument(stream, password: "optional");
```

**Rendering to images:**
- `SaveAsPngs()`, `SaveAsJpegs()`, `SaveAsWebps()` - save all pages to directory
- `ConvertToBitmaps()` / `ConvertToBitmapsAsync()` - get SKBitmap array
- `ConvertToImageBytes()` - get byte arrays in specified format
- DPI parameter controls resolution (e.g., `dpi: 300` for high quality)

**Form filling:**
```csharp
var form = doc.GetForm();
form.SetFormFieldValue("FieldName", "value");
form.SetFormFieldChecked("CheckboxName", true);
doc.Save("filled.pdf");
```

**PDF merging:**
```csharp
using var merger = new PdfMerger();
merger.AppendDocument("doc1.pdf");
merger.AppendPages("doc2.pdf", new[] { 0, 2, 5 }); // specific pages
merger.Save("merged.pdf");
```

## Project-Specific Conventions

- Use `IntPtr` for PDFium handles (document, page, form, etc.)
- Error checking: Check for `IntPtr.Zero` after PDFium calls, throw `InvalidOperationException` with `PDFium.FPDF_GetLastError()`
- Coordinate system: PDF uses bottom-left origin (see `docs/PDF-EDITING.md` for details)
- Lazy initialization pattern for metadata/bookmarks/attachments (see `PdfDocument.Metadata`, `PdfDocument.Bookmarks`)
- Standard page sizes in points: US Letter = 612x792, A4 = 595x842

## Documentation Structure

- `README.md` - Quick start examples and feature overview
- `docs/API-REFERENCE.md` - Comprehensive API documentation
- `docs/PDF-EDITING.md` - Creating/editing PDFs, page objects
- `docs/BEST-PRACTICES.md` - Thread safety, ASP.NET Core, performance
- `docs/EXAMPLES.md` - Detailed code examples
- `docs/TROUBLESHOOTING.md` - Common issues and solutions

When implementing new features, follow existing patterns in these areas and update relevant documentation. Don't write docs out of /docs folder, just update current documentations README and /docs files as needed. Don't create sample code, create unit tests that test the new features instead.
