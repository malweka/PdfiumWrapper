# Agent Instructions for PdfiumWrapper

# Agent Instructions

## Startup Instructions

Before starting work:

1. Read this file: `AGENTS.md` and `README.md`.
2. Read `/ai/current-state.md`.
3. Check `/ai/plans` for any active plan related to the task.


## Project State Tracking

Agents must keep `/ai/current-state.md` updated.

Update `/ai/current-state.md`:

- Before stopping work.
- After completing a meaningful task.
- When switching to a different feature or task.
- When new blockers or open questions are discovered.

The file must always include:

- Current focus.
- What was completed.
- What is in progress.
- The next recommended step.
- Blockers or open questions.
- Recently changed files.

## Plan File Rules

When creating a new implementation plan, save it under:

```text
/ai/plans/
```

Plan filenames must use this format:

```text
plan-<feature>.md
```

Examples:

```text
plan-authentication.md
plan-document-upload.md
plan-admin-dashboard.md
plan-ef-core-migrations.md
```

Use lowercase words separated by hyphens.

Do not create generic names such as:

```text
plan.md
implementation.md
tasks.md
```

## Working Rules

When executing a plan:

1. Read the related `plan-<feature>.md` file.
2. Work through the checklist in order unless there is a good reason not to.
3. Check off completed items.
4. Update `/ai/current-state.md` before stopping.
5. Add notes about any decisions, blockers, or files changed.

## Diagram Instructions

When creating, generating, drawing, designing, or exporting diagrams, flowcharts, architecture diagrams, ER diagrams, sequence diagrams, class diagrams, network diagrams, mockups, wireframes, or UI sketches, use the `drawio` skill.

## Before Stopping Work

Before ending a session, always update `/ai/current-state.md` with:

1. What was completed.
2. What is still in progress.
3. The exact next step.
4. Any blockers or open questions.
5. Files recently changed.

## Project Overview

PdfiumWrapper is a .NET 10 library wrapping Google's PDFium for PDF manipulation and native libtiff for TIFF export. It targets high-throughput document processing services handling thousands of files.

**Version:** 2.0.0 (the raw `PDFium.*` imports are `internal`; all native work goes through `PdfiumRuntime`)
**Target framework:** `net10.0` with `AllowUnsafeBlocks=true`
**Dependencies:** Native PDFium, libtiff + tiff_shim (TIFF), libjpeg-turbo (JPEG), pdfium_png (PNG; statically links libpng + zlib-ng)

## Architecture

```
Worker pool (PdfiumWrapper.Processing: PdfProcessingPool, PdfWorkerHost; separate package, optional)
       |
High-level API (PdfDocument, PdfPage, PdfForm, PdfMerger, TiffWriter, PngEncoder, JpegEncoder/Decoder)
       |
Native coordination (PdfiumRuntime: process-wide gate, init, deferred release; BitmapLease; SpooledInput; PooledFileWriter)
       |
P/Invoke layer (PDFium.cs partials, LibTiff.cs, LibTurboJpeg.cs, LibPdfiumPng.cs, NativeLibraryResolver.cs)
       |
Native binaries (src/libs/{rid}/ — pdfium, libtiff, tiff_shim, libturbojpeg, pdfium_png)
```

### Native Library Loading

`NativeLibraryResolver.cs` is the single shared `DllImportResolver` for the assembly. Registration happens in its static constructor, so no thread can reach a P/Invoke before the resolver is in place. Native interop classes and `PdfiumRuntime` call `EnsureRegistered()`, which only forces that static constructor. It resolves:
- `pdfium` — PDF rendering engine
- `tiff` — libtiff for TIFF I/O
- `tiff_shim` — non-variadic wrappers for `TIFFSetField`
- `turbojpeg` — libjpeg-turbo for JPEG encoding/decoding
- `pdfium_png` — C shim wrapping libpng + zlib-ng for PNG encoding/decoding

Resolution order: `libs/{rid}/{file}` -> `runtimes/{rid}/native/{file}` -> system fallback.

`PdfiumRuntime` owns native initialization: the first gate entry calls `FPDF_InitLibrary` and installs libtiff's error handlers. Any wrapper type can be the first one used in a process. `FPDF_DestroyLibrary` is reachable only through `PdfiumRuntime.Shutdown()`.

### Why tiff_shim Exists

`TIFFSetField` is a variadic C function. .NET P/Invoke (both `DllImport` and `LibraryImport`) cannot correctly call variadic functions — on ARM64 the ABI passes variadic args differently from fixed params, and on x64 values are silently corrupted. The shim (`src/native/tiff_shim.c`) wraps the variadic call in non-variadic C functions so the compiler handles it correctly. See `docs/BUILDING-NATIVE-LIBS.md` for build instructions.

### Image Pipelines

All image output uses native libraries directly — no managed image dependencies.

Every pipeline renders inside the native gate and returns a `BitmapLease` (bitmap handle, buffer pointer, size, stride). Conversion, encoding and output read the lease's buffer with the gate free; disposing the lease reenters the gate to destroy the bitmap.

**TIFF:**
```
PdfPage.RenderToBitmapLeaseCore(gray: true) → BitmapLease (native 8-bit gray buffer)   [inside the gate]
    → PixelConverter (unsafe pointer math, no managed copy)                    [outside the gate]
        → TiffWriter (pinned write, zero per-row allocation)                   [outside the gate]
```
TIFF output is bilevel or grayscale, so its pages are rendered straight into an 8-bit gray PDFium bitmap (`FPDFBitmap_Gray`): a quarter of the memory of BGRA and one byte per pixel to threshold or copy. PDFium anti-aliases text with plain grayscale smoothing at that depth and with LCD-style smoothing at 32 bits, so TIFF glyph edges differ slightly from the PNG/JPEG render of the same page. `PixelConverter.cs` reads directly from the native IntPtr. `TiffWriter.cs` pins the output array once and writes all scanlines via pointer offsets. Stream-based TIFF output uses `TIFFClientOpen` with GCHandle-pinned callback delegates.

**JPEG:**
```
BitmapLease (native BGRA buffer) → JpegEncoder (libjpeg-turbo, accepts BGRA natively)
```
`JpegEncoder` wraps a `tjInitCompress` handle. Not thread-safe per instance. `JpegDecoder` handles decoding for `PdfImageObject.SetImage()`.

**PNG:**
```
BitmapLease (native BGRA buffer) → PngEncoder (pdfium_png shim, uses png_set_bgr() internally)
```
`PngEncoder` is stateless/static. The C shim (`src/native/pdfium_png.c`) handles setjmp/longjmp error recovery, BGRA↔RGBA conversion via `png_set_bgr()`, and memory I/O. Both libpng and zlib-ng (SIMD-accelerated) are statically linked into the shim binary.

### Why pdfium_png Shim Exists

libpng uses `setjmp`/`longjmp` for error handling, which corrupts .NET's managed stack. The C shim contains the `setjmp` scope in native code and returns integer error codes. It also uses `png_set_bgr()` for zero-copy BGRA handling, and defaults to `PNG_FILTER_SUB` for fast encoding. See `docs/BUILDING-NATIVE-LIBS.md` for the 3-step build (zlib-ng → libpng → shim).

### RawBitmap

`RawBitmap` is a lightweight record (`byte[] Pixels, int Width, int Height, int Stride`) returned by `RenderPages()` / `RenderPagesAsync()`. It gives callers raw BGRA pixel data they can use with any framework. Not disposable — the `byte[]` is a managed array.

## Critical Rules

### Thread Safety and the Native Gate

PDFium allows one native call per process at a time, across all documents (shared font caches, non-atomic reference counts). The wrapper enforces this with one process-wide reentrant gate, `PdfiumRuntime`. Different objects may be used from different threads; one `PdfDocument`, `PdfPage`, `PdfForm`, `PdfMerger` or page object must not be used from two threads at once. Async methods wait for the gate without blocking a thread and process pages sequentially.

Rules for any code you add or change:

- Every public member that touches PDFium starts with `using var _ = PdfiumRuntime.Enter();`, then the disposed check, then argument validation and the native calls. The disposed check, every native call and `FPDF_GetLastError()` all happen inside that one scope.
- A method named `XxxCore` assumes the gate is held and calls `PdfiumRuntime.AssertHeld()`. Public methods enter the gate and call the `Core` method; use `Core` methods inside loops instead of reentering per iteration.
- Never hold a scope across `await`, `yield return`, or a call into a user delegate. Collect what you need inside the scope, leave it, then continue.
- Async methods use `using (await PdfiumRuntime.EnterAsync()) { ... }` with no `await` inside the block. Do not call synchronous gated methods (`Dispose()`, `GetPage()`) from async paths where they would block on the gate; use the async helpers (`DisposeAsync` on `BitmapLease`, `GetPageAsync`/`DisposePageAsync` in `PdfDocument`).
- A non-async method that returns a `Task` or `IAsyncEnumerable` must not call `Enter()`: do managed validation there (disposed check, argument checks) and await the gate inside the async body. `StreamImageBytesAsync` checks the page count when enumeration starts for this reason.
- Never hand a caller-owned native handle out of the public API (the raw destroy functions are internal). Copy to managed memory, or wrap it in a tracked disposable. `PdfImageObject.GetBitmap()` returns a `RawBitmap` for this reason.
- Awaits inside the library use `ConfigureAwait(false)` (`EnterAsync()` already never resumes on a captured context). The gate is handed to an async waiter before its continuation runs; posted to a UI thread that is blocked in a synchronous call, that continuation would never run.
- Finalizers never call PDFium, never take a lock and never wait on the gate. They only call `PdfiumRuntime.EnqueueRelease(kind, handle)` in ascending `NativeHandleKind` order (page objects, forms, pages, document, then pinned buffers and native memory). A document's finalizer enqueues its pages, forms and detached page objects itself so none can be closed after the document.
- No user I/O inside the gate. Read caller streams before entering (`SpooledInput`); serialize saves into a pooled buffer inside the gate (`PooledFileWriter`) and write to the caller's stream after leaving it.
- Render inside the gate, encode outside it: return a `BitmapLease` from the gated scope and convert/encode/write from its buffer with the gate free.
- Call `PdfiumRuntime.HandleOpened()` / `HandleClosed()` wherever a long-lived native handle is opened or closed (documents, pages, form environments, detached page objects, bitmap leases).
- Lock order: the gate is outermost. The per-object locks (`_pagesLock`, `_attachedObjectsLock`, `_disposeLock`) are taken only inside a gated scope.
- Shared runtime state lives in `AppContext` data as BCL types (`SharedState.GetOrCreate`) so copies of the assembly in different `AssemblyLoadContext`s share one gate.
- A public member that is pure managed code is marked `[NoNativeCall]` (the attribute also applies to a whole type).

`GateCoverageTests` enforces the first and last rules by reflection over the public surface, and scans the library source for `await` / `yield return` inside a gated scope.

### Resource Management

All PDF and TIFF objects implement `IDisposable`. Always use `using`. Pages from `GetPage()` must be disposed by the caller. `ProcessAllPages()` handles disposal automatically and does not hold the gate while the caller's delegate runs.

A document owns its pages, the forms returned by `GetForm()` (a new form per call), and page objects removed from its pages with `RemoveObject`; disposing the document disposes them. An object dropped without `Dispose()` has its handles queued by its finalizer and closed by the next gated operation or `PdfiumRuntime.ReleasePending()`.

`PdfMerger` wraps a private `PdfDocument` (`_target`), built by the matching `PdfDocument` constructor. That document owns the native handle, the pinned input buffer or spool file, saving, disposal and the finalizer; the merger has no finalizer or load code of its own. Make document lifecycle changes in `PdfDocument` only.

### Page Editing Workflow

After adding/modifying page objects, `page.GenerateContent()` MUST be called before saving. Without it, changes are not written to the content stream.

### P/Invoke Conventions

- Use `LibraryImport` (source-generated) for all non-variadic native functions
- Use `DllImport` only when `LibraryImport` cannot handle the signature (currently: none — the shim eliminated this need)
- PDFium imports are `internal static partial` (never `public`: a public raw import bypasses the gate). The `PDFium` class stays public for constants and structs only
- Check `IntPtr.Zero` after native calls and throw `InvalidOperationException` with `PDFium.FPDF_GetLastError()`, read in the same gated scope as the failing call
- Check each signature against the PDFium header. A struct pointer parameter (for example `FS_MATRIX*`) is one `ref`/`out` struct, not separate scalar parameters
- Memory that PDFium keeps a pointer to after the call returns (for example `FPDF_FORMFILLINFO`) must be native memory or pinned for the whole lifetime, and released only after the owning native object is closed
- The `PDFium` class is split into partial files by domain: `PDFium.cs` (core), `PDFium.Edit.cs`, `PDFium.FormFill.cs`, `PDFium.Metadata.cs`, `PDFium.Annot.cs`, `PDFium.Ppo.cs`

### Worker Pool (`src/PdfiumWrapper.Processing`)

Separate package `PdfiumWrapper.Processing`; the core package never launches processes. `PdfProcessingPool` keeps a dynamically sized set of worker processes (between `MinWorkers` and `MaxWorkers`), hands each job to an idle worker over stdin/stdout as length-prefixed JSON frames (`Protocol/Frames.cs`, `Protocol/FrameStream.cs`), and reports every outcome as a `PdfJobResult<T>` status rather than an exception. Workers are copies of the consumer's own executable (`PdfWorkerHost.TryRun()` first in `Main`) or a dedicated executable (`WorkerPath`); the test project uses `PdfiumWrapper.Tests.Host` as its worker.

Rules for pool code:

- Only file paths, options and small results cross the process boundary. Never pixels, handles or delegates. Large text goes through a temp file.
- A worker writes nothing to stdout except protocol frames; diagnostics go to stderr, which the pool forwards as events.
- A job failure never fails the pool: crash, hang, malformed frame and memory limit all end with the worker replaced and the job given a status (`WorkerCrashed`, `TimedOut`, `Failed`) and retried up to `MaxAttempts`.
- The dispatcher takes a job out of its queue only once a worker has a free slot, so `QueueCapacity` is exact. Retries go through their own unbounded channel, ahead of new jobs.
- A worker runs `JobsPerWorker` jobs at once (default 2) so encoding overlaps rendering inside the worker, as it does for concurrent callers in one process. Slots are tracked per worker (`Worker.Slots`, `InUse`, `Active`); the sizer counts free slots, not idle workers.
- Output files are written as `<name>.<jobId>.tmp` and renamed on success (image jobs: all pages staged first, then all renamed).
- Sizing decisions happen on one 250 ms timer; every decision raises an event. Workers are replaced, never recycled on a schedule.
- Image jobs stage every page as `<final>.<jobId>.tmp` and move them to their final names only once all are staged, after a `Progress` frame with `CommittingPages` set. A job that does not succeed leaves no output and never deletes a file it did not write: the worker removes its staged and moved files on a managed failure or an observed cancel; the coordinator (`RemovePartialOutput`) runs only for an attempt whose worker crashed or was killed, and removes that job's `.tmp` files plus, if the worker had reported committing, the final names it had claimed. Jobs that never reached a worker remove nothing.
- A batch (`IEnumerable<PdfInput>`) bounds documents in any stage, spooled through unread, to `QueueCapacity + MaxWorkers x JobsPerWorker`, and keeps no per-job task: outstanding jobs are counted. Batch output names are reserved against originals and generated names alike.
- Cancellation is always a status, never an exception: before spooling, while spooling, while waiting for a queue slot, while queued, or in flight.
- A worker whose start is cancelled or times out is killed inside `Worker.StartAsync`; the caller's cancellation propagates out of `CreateAsync`. A worker's exit is acted on only after its stdout is drained.
- Fault injection for tests lives in `PdfiumWrapper.Tests.Host/WorkerFaults.cs` (`PDFIUMWRAPPER_TEST_FAULT=crash|hang|garbage:<input substring>`, `crash-after-page-N:<input substring>`, `slow-start:<ms>` with `PDFIUMWRAPPER_TEST_PIDFILE`), passed through `PdfPoolOptions.WorkerEnvironment`.

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

**Runtime:**
- `PdfiumRuntime.Enter()` — enter the gate (public; returns a disposable `Scope`, reentrant per thread)
- `PdfiumRuntime.ReleasePending()` — close handles queued by finalizers
- `PdfiumRuntime.Shutdown()` — destroy the native library; throws while handles are alive
- `PdfiumRuntime.IsHeldByCurrentThread`, `PdfiumRuntime.LiveHandleCount`
- `PdfiumRuntime.EnterAsync()`, `AssertHeld()`, `EnqueueRelease()`, `HandleOpened()` / `HandleClosed()` — internal
- `PdfiumDiagnostics` — internal counters and intervals, enabled by the `PdfiumWrapper.Diagnostics` `AppContext` switch before first use

## Performance Considerations

- Native work is serialized process-wide; only conversion, encoding and output overlap between callers. More throughput than one process gives comes from more processes, not more threads
- `RenderToBitmapLease()` exposes the native pixel buffer — encoders read it directly, avoiding the managed `byte[]` copy that `RenderToBytes()` makes
- `RenderPages()` copies each lease into a managed `byte[]` with a single `Marshal.Copy`, outside the gate
- Stream inputs are spooled before the gate (in memory up to 64 MB, then a temp file; `PdfiumWrapper.SpoolThreshold` overrides); PDF saves are buffered in a pooled array and written after the gate is released
- `PixelConverter` uses pre-scaled threshold comparison to avoid per-pixel division in bilevel conversion
- PNG encoding uses zlib-ng (SIMD: NEON/AVX2) + `PNG_FILTER_SUB` for ~40% faster than SkiaSharp
- JPEG encoding uses libjpeg-turbo (SIMD) for ~2x faster than SkiaSharp
- Document-level renders add `FPDF_NO_NATIVETEXT` so macOS draws text with PDFium's own rasterizer instead of CoreGraphics (heavier glyphs), matching Windows/Linux and the gray TIFF render
- For TIFF: render flags include `FPDF_PRINTING | FPDF_ANNOT` (vs just `FPDF_ANNOT` for other formats), and pages are rendered into an 8-bit gray bitmap instead of BGRA (26-39% faster on text documents)
- `StreamImageBytes` uses eager validation + private core pattern to throw immediately on bad input while deferring iteration

## Testing

- xUnit. Every class that uses the wrapper must carry `[Collection("PDF Tests")]`; `TestProjectHygieneTests` fails the run if one does not
- Test PDFs in `src/PdfiumWrapper.Tests/Docs/`
- `Bootstrapper.cs` uses `[ModuleInitializer]` to set up `TestOutput/` and to enable the `PdfiumWrapper.Diagnostics` switch
- Tests implement `IDisposable` and use `CreateTempDirectory()` for file output
- `src/PdfiumWrapper.Tests/Concurrency/` — gate coverage (`GateCoverageTests`), concurrent callers against a sequential oracle with an independent detector (`PdfiumConcurrencyTests`), and child-process scenarios (`PdfiumHostTests`)
- `src/PdfiumWrapper.Tests.Host` — console host for tests that change process-global state, need a fresh process, or may abort natively (init race, cold start, thread-pool starvation, deferred release, shared gate across load contexts, shutdown, crash probe). Launched through `HostRunner`. It is also the worker executable for the pool tests (`PdfWorkerHost.TryRun()` at the top of its `Main`)
- `src/PdfiumWrapper.Tests/Processing/` — protocol framing, pool behaviour (correctness against in-process output, crash/hang/garbage workers, cancellation, backpressure, disposal), sizing policy, cross-process render overlap, damaged inputs
- A new public PDFium-touching member needs no test registration: `GateCoverageTests` discovers it. If it takes an argument type the fixture does not know, add it to `GateCoverageTests.Fixture.Argument`
- Run: `dotnet test src/PdfiumWrapper.Tests/PdfiumWrapper.Tests.csproj`

## Build

```bash
# Build
dotnet build src/PdfiumWrapper/PdfiumWrapper.csproj

# Test
dotnet test src/PdfiumWrapper.Tests/PdfiumWrapper.Tests.csproj

# Build/download native libraries on macOS/Linux
bash src/native/build-natives.sh --target host --clean
bash src/native/build-natives.sh --target osx-arm64 --clean
bash src/native/build-natives.sh --target osx-x64 --clean
bash src/native/build-natives.sh --target linux-x64 --clean

# Native libs must exist in src/libs/{rid}/ — see docs/BUILDING-NATIVE-LIBS.md
```

For Windows x64 native binaries, run `src\native\build-natives.cmd --clean`.

The `.csproj` auto-detects the platform RID and includes native binaries with `Exists()` conditions — missing binaries don't break the build, only runtime calls that need them.

## When Making Changes

- Prefer editing existing files over creating new ones
- Don't write docs outside of `README.md` and `/docs` — update existing files
- Don't create sample code files — write unit tests instead
- Update relevant documentation when adding or changing public API
- Follow existing patterns for disposal, error handling, and P/Invoke signatures
- Follow the gate rules under "Thread Safety and the Native Gate" for every member that touches PDFium
- Run the test suite after changes: all 274+ tests should pass (win-x64 and linux-x64)
- Coordinate system: PDF uses bottom-left origin (see `docs/PDF-EDITING.md`)
- Standard page sizes in points: US Letter = 612x792, A4 = 595x842
