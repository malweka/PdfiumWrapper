# Changelog

All notable changes to PdfiumWrapper are listed here. Versions follow [Semantic Versioning](https://semver.org/).

## 2.0.1 (unreleased)

- **Smaller Linux workers.** On Linux the worker pool starts each worker with `MALLOC_ARENA_MAX=2`, unless the application's environment already sets it, and a worker idle for a second returns its free memory (`malloc_trim`). Without this, glibc kept the memory of the largest pages a worker had rendered. In a 10,000-job burst, idle workers went from 316-400 MB to about 100 MB and peaks from about 400 MB to about 190 MB, at the same throughput. `PdfPoolOptions.WorkerEnvironment` overrides the value. The docs explain how to set it for in-process services and containers, and when to add `MALLOC_MMAP_THRESHOLD_=131072` (smaller still, about 9% slower).
- **The runtime packages have a package readme.** It says to reference `PdfiumWrapper` instead, and lists the native libraries and their versions.

## 2.0.0 (2026-10-08)

`PdfiumWrapper.Processing` is not published at 2.0.0 yet; the core and the four runtime packages are.

2.0 makes the library safe to use from many threads, adds an optional worker pool for high volumes, and hardens the native boundary against hostile input. It is a breaking release.

Version 1.0.0 was never published to NuGet. "Changed" and "removed" below are relative to the 1.0.0 source (commit `9021729`, .NET 8).

### Breaking changes

**Platform and packaging**

- **.NET 10 is required.** Every 2.0 package targets `net10.0` only.
- **Native binaries ship in four runtime packages.** These are `PdfiumWrapper.runtime.win-x64`, `linux-x64`, `osx-x64` and `osx-arm64`, and `PdfiumWrapper` depends on all four at exactly its own version.
  - A portable build (no `RuntimeIdentifier`) gets every platform under `runtimes/<rid>/native` and loads the matching one.
  - A RID-specific build or publish copies only its own platform.
  - 1.0 mapped the platforms through `runtime.json`, which left portable apps with no native libraries.
- **Upgraded native libraries:** PDFium chromium/8076, libtiff 4.7.2, libjpeg-turbo 3.2.0, libpng 1.6.59 and zlib-ng 2.3.3.
- **macOS renders text like Windows and Linux.** Glyphs are drawn by PDFium instead of CoreGraphics (`FPDF_NO_NATIVETEXT`), so they are lighter than in 1.0.

**API**

- **No public native interop.** The raw imports on `PDFium`, its interop structs, `LibTiff`, `LibTurboJpeg`, `PdfHelpers` (including the `Stream.ReadStreamToBytes()` extension) and `JpegInfo` are internal. `PDFium` stays public for its constants (`PDFium.FPDF_ANNOT`, ...). No public member takes or returns a native pointer.
- **Page objects are typed.**
  - `PdfPage.GetObject` and `PdfFormObject.GetObject` return `PdfTextObject`, `PdfPathObject`, ... instead of an `IntPtr`.
  - The `Create(IntPtr, ...)` and `CreateRectangle(IntPtr, ...)` factories are internal; use `PdfPage.AddText`, `AddImage`, `AddPath` and `AddRectangle`.
  - The `PdfTextObject.Font` setter is removed: it never changed the font. Choose the font in `AddText`.
- **`PdfImageObject` works in managed pixels.**
  - `GetBitmap()` and `GetRenderedBitmap(PdfPage)` return BGRA pixels (`RawBitmap?`) instead of native handles.
  - `SetBitmap` and `SetImage` are internal.
- **`PdfMetadata` is read-only.** The setters and `Set*`/`ClearAllMetadata` methods are removed. They called a function PDFium does not export and always threw `EntryPointNotFoundException`.
- **Typed load errors.** A document that fails to load throws `PdfiumException` (derived from `InvalidOperationException`) with an `ErrorCode`: `Password`, `Format`, `File`, `Security`, `Page` or `Unknown`. Code that parsed the old message text must switch to `ErrorCode`.
- **Async signatures changed.** The async render and save methods take an optional `CancellationToken`, checked before each page, so callers must recompile. Cancel `StreamImageBytesAsync` and `StreamJpegBytesAsync` with `.WithCancellation(token)`.
- **`PdfPage.RemoveObject` is stricter.** It throws `ObjectDisposedException` for a disposed wrapper, and returns `false` unless the wrapper is the one the page tracks for that object.
- **`PdfBookmark.PageIndex` is `int?`.** It is `null` when the bookmark has no destination or its target is not a page of this document; 1.0 reported 0 (page 1) or -1.
- **Thumbnails return one `RawBitmap?`.** `PdfPage.GetEmbeddedThumbnail()` returns BGRA pixels with their size and stride. `GetEmbeddedThumbnailBytes()` and `GetEmbeddedThumbnailSize()` are obsolete.

**Behaviour**

- **Stream inputs are copied.** `new PdfDocument(Stream)` and `new PdfMerger(Stream)` read the stream to its end during construction, `MemoryStream` included; inputs above 64 MB are spooled to a temporary file. The `byte[]` constructors still use the array in place.
- **Disposal cascades.** A document owns its pages, the forms from `GetForm()` and the page objects removed from its pages; disposing the document disposes them.
- **Render size is capped.**
  - A render larger than 268,435,456 pixels throws `InvalidOperationException` before any native allocation. The `PdfiumWrapper.MaxRenderPixels` `AppContext` data key changes the cap.
  - A DPI, width or height of zero or less throws `ArgumentOutOfRangeException`.
- **Default JPEG quality is 90 everywhere.** `StreamImageBytes`, `StreamImageBytesAsync` and `SaveAsImages` defaulted to 100.
- **TIFF pages render in 8-bit gray.** This is faster and uses a quarter of the memory. TIFF files are not pixel-identical to 1.0: about 1-2% of pixels differ, at glyph edges.
- **Async methods stay off the caller's context.** They run page work on the thread pool and never post to the caller's `SynchronizationContext`, so a `ProcessAllPagesAsync` delegate runs on a thread-pool thread.

### Added

- **Thread safety.** PDFium allows one native call per process at a time. Every operation now enters one process-wide gate, so different documents and mergers can be used from different threads without a lock of your own. Async methods wait for the gate without blocking a thread.
- **`PdfiumRuntime`:** `Enter()`, `ReleasePending()`, `Shutdown()`, `IsHeldByCurrentThread` and `LiveHandleCount`.
- **New package `PdfiumWrapper.Processing`.** It is a worker pool that runs page counts, PNG, JPEG and TIFF conversion and text extraction in a dynamically sized set of worker processes, so a native crash costs one job, not the service.
  - **Statuses, not exceptions:** every job ends with a status (`Succeeded`, `Failed`, `TimedOut`, `Cancelled`, `WorkerCrashed`) and is retried within limits.
  - **Batches:** batch methods take any number of inputs with bounded memory.
  - **Process cleanup:** workers die with the host.
  - **Version pinning:** the package depends on exactly the same `PdfiumWrapper` version.
- **New async methods:** `SaveAsPngsAsync` and `SaveAsImagesAsync(Stream[], ...)`.

### Fixed

- **Hostile input:**
  - a crafted JPEG passed to `AddImage` could overflow the decode buffer;
  - a crafted page box or a very high DPI could force multi-GiB native allocations.
- **Attachment names:** `PdfAttachments.ExtractAll` wrote to a path built from the attachment name, so a name with `..\` or an absolute path could write outside the output directory.
- **`MemoryStream` aliasing:** a `MemoryStream` was loaded in place, so reusing the stream after construction corrupted the open document.
- **Unthreaded use:** using two documents from two threads at the same time could crash the process or corrupt output.
- **Process crash on stream errors:** an exception from a caller's stream during `SaveAsTiff(Stream)` unwound through native code and could terminate the process.
- **Non-ASCII paths on Windows:** PNG and TIFF output to non-ASCII directories or file names failed. Every output file is now opened by .NET.
- **UI deadlocks:** async methods could deadlock a UI thread that blocked on them (`.Wait()`, `.Result`).
- **Native leaks:**
  - one font reference per undisposed text object;
  - an object removed through a disposed wrapper.
- **Form check boxes and list boxes:**
  - `SetFormFieldChecked` (and `SetFormFieldValue` on a check box or radio button) did not change the box. It wrote the value as text and left the appearance state alone. It now toggles the field as a click in a viewer does, and throws when PDFium refuses (read-only field, unchecking a radio button).
  - `SetListBoxSelections` wrote one comma-joined string. It now makes a real multi-selection, and accepts values containing commas.
- **PDF dates:** `CreationDateTime` and `ModificationDateTime` returned `null` for valid short dates such as `D:20231215`.
- **Text size:** `PdfTextObject.FontSize` had no effect; it now changes the size.
- **Native signatures:** several imports did not match the PDFium headers. `PdfPageObject.GetMatrix()` could crash. Every import is now checked against the shipped binaries.


## 1.0.0

Not published.
