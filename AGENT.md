# Agent Instructions for PdfiumWrapper

## Project Overview

PdfiumWrapper is a .NET 8 library wrapping Google's PDFium for PDF manipulation and native libtiff for TIFF export. It targets high-throughput document processing services handling thousands of files.

**Target framework:** `net8.0` with `AllowUnsafeBlocks=true`
**Dependencies:** Native PDFium, libtiff + tiff_shim (TIFF), libjpeg-turbo (JPEG), pdfium_png (PNG; statically links libpng + zlib-ng)

## Architecture

```
High-level API (PdfDocument, PdfPage, PdfForm, PdfMerger, TiffWriter, PngEncoder, JpegEncoder/Decoder)
       |
P/Invoke layer (PDFium.cs partials, LibTiff.cs, LibTurboJpeg.cs, LibPdfiumPng.cs, NativeLibraryResolver.cs)
       |
Native binaries (src/libs/{rid}/ — pdfium, libtiff, tiff_shim, libturbojpeg, pdfium_png)
```

### Native Library Loading

`NativeLibraryResolver.cs` is the single shared `DllImportResolver` for the assembly. All native interop classes register with it via `EnsureRegistered()` (idempotent, uses `Interlocked`). It resolves:
- `pdfium` — PDF rendering engine
- `tiff` — libtiff for TIFF I/O
- `tiff_shim` — non-variadic wrappers for `TIFFSetField`
- `turbojpeg` — libjpeg-turbo for JPEG encoding/decoding
- `pdfium_png` — C shim wrapping libpng + zlib-ng for PNG encoding/decoding

Resolution order: `libs/{rid}/{file}` -> `runtimes/{rid}/native/{file}` -> system fallback.

### Why tiff_shim Exists

`TIFFSetField` is a variadic C function. .NET P/Invoke (both `DllImport` and `LibraryImport`) cannot correctly call variadic functions — on ARM64 the ABI passes variadic args differently from fixed params, and on x64 values are silently corrupted. The shim (`src/native/tiff_shim.c`) wraps the variadic call in non-variadic C functions so the compiler handles it correctly. See `docs/BUILDING-NATIVE-LIBS.md` for build instructions.

### Image Pipelines

All image output uses native libraries directly — no managed image dependencies.

**TIFF:**
```
PdfPage.RenderToBitmapHandle() → native BGRA buffer (IntPtr)
    → PixelConverter (unsafe pointer math, no managed copy)
        → TiffWriter (pinned write, zero per-row allocation)
```
`PixelConverter.cs` reads directly from the native IntPtr. `TiffWriter.cs` pins the output array once and writes all scanlines via pointer offsets. Stream-based TIFF output uses `TIFFClientOpen` with GCHandle-pinned callback delegates.

**JPEG:**
```
RenderPageToRawBitmap() → BGRA byte[] → JpegEncoder (libjpeg-turbo, accepts BGRA natively)
```
`JpegEncoder` wraps a `tjInitCompress` handle. Not thread-safe per instance. `JpegDecoder` handles decoding for `PdfImageObject.SetImage()`.

**PNG:**
```
RenderPageToRawBitmap() → BGRA byte[] → PngEncoder (pdfium_png shim, uses png_set_bgr() internally)
```
`PngEncoder` is stateless/static. The C shim (`src/native/pdfium_png.c`) handles setjmp/longjmp error recovery, BGRA↔RGBA conversion via `png_set_bgr()`, and memory I/O. Both libpng and zlib-ng (SIMD-accelerated) are statically linked into the shim binary.

### Why pdfium_png Shim Exists

libpng uses `setjmp`/`longjmp` for error handling, which corrupts .NET's managed stack. The C shim contains the `setjmp` scope in native code and returns integer error codes. It also uses `png_set_bgr()` for zero-copy BGRA handling, and defaults to `PNG_FILTER_SUB` for fast encoding. See `docs/BUILDING-NATIVE-LIBS.md` for the 3-step build (zlib-ng → libpng → shim).

### RawBitmap

`RawBitmap` is a lightweight record (`byte[] Pixels, int Width, int Height, int Stride`) returned by `RenderPages()` / `RenderPagesAsync()`. It gives callers raw BGRA pixel data they can use with any framework. Not disposable — the `byte[]` is a managed array.

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
- `RenderPages()` / `RenderPagesAsync()` — returns `RawBitmap[]` (BGRA pixel data, no disposal needed)

**PDF operations:**
- `PdfDocument` — load from file/bytes/stream, create new, save to file/stream
- `PdfPage` — render, extract text, add objects (text, image, path, rectangle)
- `PdfForm` — read/write form fields
- `PdfMerger` — combine PDFs, extract pages
- `PdfMetadata`, `PdfBookmarks`, `PdfAttachments` — lazy-loaded via properties

## Performance Considerations

- `RenderToBitmapHandle()` returns a native pointer — avoids the managed `byte[]` allocation in `RenderToBytes()`
- `RenderPageToRawBitmap()` uses `Marshal.Copy` for a single native-to-managed copy
- `PixelConverter` uses pre-scaled threshold comparison to avoid per-pixel division in bilevel conversion
- PNG encoding uses zlib-ng (SIMD: NEON/AVX2) + `PNG_FILTER_SUB` for ~40% faster than SkiaSharp
- JPEG encoding uses libjpeg-turbo (SIMD) for ~2x faster than SkiaSharp
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
