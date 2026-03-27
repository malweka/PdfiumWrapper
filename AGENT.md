# Agent Instructions for PdfiumWrapper

## Project Overview

PdfiumWrapper is a .NET 8 library wrapping Google's PDFium for PDF manipulation and native libtiff for TIFF export. It targets high-throughput document processing services handling thousands of files.

**Target framework:** `net8.0` with `AllowUnsafeBlocks=true`
**Dependencies:** SkiaSharp (image rendering), native PDFium, native libtiff + tiff_shim

## Architecture

```
High-level API (PdfDocument, PdfPage, PdfForm, PdfMerger, TiffWriter)
       |
P/Invoke layer (PDFium.cs partials, LibTiff.cs, NativeLibraryResolver.cs)
       |
Native binaries (src/libs/{rid}/ — pdfium, libtiff, tiff_shim)
```

### Native Library Loading

`NativeLibraryResolver.cs` is the single shared `DllImportResolver` for the assembly. Both `PDFium` and `LibTiff` register with it via `EnsureRegistered()` (idempotent, uses `Interlocked`). It resolves:
- `pdfium` — PDF rendering engine
- `tiff` — libtiff for TIFF I/O
- `tiff_shim` — non-variadic wrappers for `TIFFSetField`

Resolution order: `libs/{rid}/{file}` -> `runtimes/{rid}/native/{file}` -> system fallback.

### Why tiff_shim Exists

`TIFFSetField` is a variadic C function. .NET P/Invoke (both `DllImport` and `LibraryImport`) cannot correctly call variadic functions — on ARM64 the ABI passes variadic args differently from fixed params, and on x64 values are silently corrupted. The shim (`src/native/tiff_shim.c`) wraps the variadic call in non-variadic C functions so the compiler handles it correctly. See `docs/BUILDING-NATIVE-LIBS.md` for build instructions.

### TIFF Pipeline

The `SaveAsTiff` path bypasses SkiaSharp entirely for maximum throughput:

```
PdfPage.RenderToBitmapHandle() → native BGRA buffer (IntPtr)
    → PixelConverter (unsafe pointer math, no managed copy)
        → TiffWriter (pinned write, zero per-row allocation)
```

`PixelConverter.cs` reads directly from the native IntPtr. `TiffWriter.cs` pins the output array once and writes all scanlines via pointer offsets. Stream-based TIFF output uses `TIFFClientOpen` with GCHandle-pinned callback delegates.

## Critical Rules

### Thread Safety

PDFium is NOT thread-safe. Never share `PdfDocument`, `PdfPage`, or `PdfForm` across threads. Async methods use `Task.Yield()` for responsiveness, not parallelism. Safe pattern: one `PdfDocument` per thread/request.

### Resource Management

All PDF and TIFF objects implement `IDisposable`. Always use `using`. Pages from `GetPage()` must be disposed by the caller. `ProcessAllPages()` handles disposal automatically.

### Page Editing Workflow

After adding/modifying page objects, `page.GenerateContent()` MUST be called before saving. Without it, changes are not written to the content stream.

### P/Invoke Conventions

- Use `LibraryImport` (source-generated) for all non-variadic native functions
- Use `DllImport` only when `LibraryImport` cannot handle the signature (currently: none — the shim eliminated this need)
- Check `IntPtr.Zero` after native calls and throw `InvalidOperationException` with `PDFium.FPDF_GetLastError()`
- The `PDFium` class is split into partial files by domain: `PDFium.cs` (core), `PDFium.Edit.cs`, `PDFium.FormFill.cs`, `PDFium.Metadata.cs`, `PDFium.Annot.cs`, `PDFium.Ppo.cs`

## Key APIs

**Image output (streaming, memory-efficient):**
- `StreamImageBytes()` / `StreamImageBytesAsync()` — `IEnumerable<byte[]>` / `IAsyncEnumerable<byte[]>`, one page at a time
- `SaveAsTiff()` / `SaveAsTiffAsync()` — multi-page TIFF to file or stream, bilevel (CCITT G4) or grayscale (LZW)
- `SaveAsPngs()`, `SaveAsJpegs()`, `SaveAsImages()` — save to directory or streams
- `ConvertToBitmaps()` — returns `SKBitmap[]` (caller must dispose each)

**PDF operations:**
- `PdfDocument` — load from file/bytes/stream, create new, save to file/stream
- `PdfPage` — render, extract text, add objects (text, image, path, rectangle)
- `PdfForm` — read/write form fields
- `PdfMerger` — combine PDFs, extract pages
- `PdfMetadata`, `PdfBookmarks`, `PdfAttachments` — lazy-loaded via properties

## Performance Considerations

- `RenderToBitmapHandle()` returns a native pointer — avoids the managed `byte[]` allocation in `RenderToBytes()`
- `RenderPageToSkBitmap()` uses `Buffer.MemoryCopy` for a single native-to-native copy (no intermediate managed array)
- `PixelConverter` uses pre-scaled threshold comparison to avoid per-pixel division in bilevel conversion
- For TIFF: render flags include `FPDF_PRINTING | FPDF_ANNOT` (vs just `FPDF_ANNOT` for other formats)
- `StreamImageBytes` uses eager validation + private core pattern to throw immediately on bad input while deferring iteration

## Testing

- xUnit with `[Collection("PDF Tests")]` for isolation
- Test PDFs in `src/PdfiumWrapper.Tests/Docs/`
- `Bootstrapper.cs` uses `[ModuleInitializer]` to set up `TestOutput/`
- Tests implement `IDisposable` and use `CreateTempDirectory()` for file output
- Run: `dotnet test src/PdfiumWrapper.Tests/PdfiumWrapper.Tests.csproj`

## Build

```bash
# Build
dotnet build src/PdfiumWrapper/PdfiumWrapper.csproj

# Test
dotnet test src/PdfiumWrapper.Tests/PdfiumWrapper.Tests.csproj

# Native libs must exist in src/libs/{rid}/ — see docs/BUILDING-NATIVE-LIBS.md
```

The `.csproj` auto-detects the platform RID and includes native binaries with `Exists()` conditions — missing binaries don't break the build, only runtime calls that need them.

## When Making Changes

- Prefer editing existing files over creating new ones
- Don't write docs outside of `README.md` and `/docs` — update existing files
- Don't create sample code files — write unit tests instead
- Update relevant documentation when adding or changing public API
- Follow existing patterns for disposal, error handling, and P/Invoke signatures
- Run the test suite after changes: all 154+ tests should pass
- Coordinate system: PDF uses bottom-left origin (see `docs/PDF-EDITING.md`)
- Standard page sizes in points: US Letter = 612x792, A4 = 595x842
