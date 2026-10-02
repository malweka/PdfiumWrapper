# Current State

## Current focus

Implementation-readiness check of `ai/plans/plan-pdfium-concurrency.md` against the code (2026-10-02). The plan is implementable, but section 4 reference code has defects that must be corrected during Phase 2 (listed under Blockers or open questions). No library code, tests, or benchmarks were changed or run. The plan file itself was not edited.

## Completed

### Latest task: implementation-readiness check (2026-10-02)

- Confirmed against code: 192 PDFium `LibraryImport`s (17 + 32 + 32 + 71 + 6 + 34); finalizers at `PdfDocument.cs:1340`, `PdfPage.cs:467`, `PdfMerger.cs:456`, `PdfPageObject.cs:141`; init only in `PdfDocument` static constructor (`:27`); resolver compare-exchange (`NativeLibraryResolver.cs:15`); `TiffWriter.cs:221`; `InternalsVisibleTo` exists for `PdfiumWrapper.Tests` only; no test or benchmark uses raw `PDFium.*` imports.
- Confirmed environment: .NET SDK 10.0.401, no git tags yet, Docker Desktop present (linux-x64 test runs possible locally), PR CI runs tests on `ubuntu-latest` only.
- Found reference-code defects and plan/code mismatches; see Blockers or open questions.

### Latest task: plan review and rewrite (2026-09-30)

- Reviewed the previous plan against the code. Confirmed: four finalizers call PDFium directly; init only in the `PdfDocument` static constructor; `NativeLibraryResolver` compare-exchange race; `StreamDocumentLoader` lazy user-stream reads; `PdfStreamFileWriter` writes inside `FPDF_SaveAsCopy`; `PdfPageDeletionExample` lacks the xUnit collection attribute; `docs/HIGH-THROUGHPUT-PROCESSING.md` still recommends `Parallel.ForEachAsync` across documents.
- Restructured the plan into Release 1 (correctness, Phases 0 to 4) and Release 2 (parallelism, Phases 5 to 8, conditional on a measured decision gate in Phase 4).
- Changed the gate design from per-import locking to one operation-level reentrant gate (`SemaphoreSlim(1,1)` plus owner-thread id and depth), shared across `AssemblyLoadContext`s via `AppContext` data with BCL-only types.
- Replaced finalizer native calls with a kind-ordered pending-release queue drained on every outermost gate entry; document finalizer enqueues its pages and form before itself.
- Added: stream spooling before the gate (`SpooledInput`), buffered save with copy-out after the gate (`PooledFileWriter`), render/encode split (`BitmapLease`), diagnostics with a `MaxActiveNative` detector, a `PdfiumWrapper.Tests.Host` with scenarios `init-race`, `starvation`, `finalizer-drain`, `alc-shared-gate`, `crash-probe`, `cold-start`.
- Added a test for each identified pitfall (thread-pool starvation, callback stall, deferred release ordering, shared gate across load contexts, init race, failure-path gate release, crash characterization, detector validation) and benchmarks `GateOverheadBenchmark`, `ConcurrentCallersBenchmark`, `StreamCallbackBenchmark`, a burst runner with `--mode async-starved` and `--abandon-fraction`, plus the Release 2 go/no-go rule.
- Rule R9 (192 raw `PDFium` imports become internal, version 2.0) was approved by the project owner on 2026-09-30.
- Phase 0 (doc correction, test collection attribute, resolver barrier, test host) is defined as the immediate first step and does not depend on the rest of the design.

### Previous clarification: small document workloads

- Made the small-workload compatibility boundary explicit in `ai/plans/plan-pdfium-concurrency.md`: core-only consumers keep existing sync/async APIs without automatic worker launches, queues, or IPC.
- Added one/two-document cold-start and warmed regression benchmarks, including tiny PDFs, and a core-only consumer deployment check.
- Explained that sequential callers pay synchronization overhead, which remains unmeasured; concurrent direct callers have native work serialized for correctness and may experience increased elapsed time compared with prior unsupported parallel execution.
- Retained process-pool throughput testing for high-volume consumers. This clarification changed only the plan and state file; no code or tests were changed or run.

### Previous task: concurrency remedy and burst-performance plan

- Read startup files and the existing performance plan; confirmed 192 PDFium imports across six partial files, current project/test/benchmark structure, and native initialization/ownership paths.
- Saved a concrete seven-phase checklist in `ai/plans/plan-pdfium-concurrency.md`: workload/baseline, complete native-call/lifecycle coordination, real concurrent-caller tests, persistent process workers, native-overlap/recovery tests, deadline-based tuning, and documentation/release qualification.
- Incorporated the user's additional requirement to complete thousands of jobs in a small time window. The plan measures full-batch completion, queue latency, prewarming/cold startup, output writes, admission backpressure, memory, and feasible worker capacity using `N / T` and measured pool scaling.
- Chose a small shared gate per process plus independent persistent worker processes as the proposed design. The plan measures uncontended gate overhead, keeps independent codec work outside native scopes, and avoids unsafe parallel access, unbounded tasks, and per-document process launches.
- Defined tests with barriers and multiple actual threads, per-process native-boundary tracing across all imports, correctness oracles, fresh-process/cache and finalizer cases, and overlapping native rendering intervals across distinct worker PIDs. Included worker failure/cancellation/retry tests, a 10,000-job qualification burst, and a 30-minute soak.
- Preserved the existing merge benchmark shape and proposed separate concurrency/burst measurements. Added provisional measured-performance targets, with the actual completion deadline as the primary criterion.
- Asked optional workload/hardware/throughput questions. The user emphasized thousands of documents in a short window without yet supplying exact `N`, `T`, workload mix, or deployment capacity; the plan keeps those inputs configurable and open.
- Planning only: no production code or tests changed, and no concurrency or performance tests were executed in this turn.

### Previous task: inspect native PDFium source before making changes

- Identified the bundled Windows DLL as PDFium `150.0.7869.0`; read `_native_build/pdfium-win-x64/VERSION` and `args.gn`. SHA-256 of the bundled DLL matches the cached download: `E472F04B57181946CA1E52FA34086079599D9B733164E7AB05179B03F4D3DDCC`.
- Confirmed this build disables V8 and XFA, so the shared-state findings do not depend on either feature.
- Cloned the official `chromium/7869` branch into the ignored `_native_build/pdfium-source-7869` directory. Inspected commit `80fccd7553e5cff9cea6549bc0db2ea93ea6cb2e` (2026-05-29).
- Cloned the producer's `chromium/7869` release tag into the ignored `_native_build/pdfium-binaries-7869` directory, at commit `9c061bee16814c28de781335b13d5dbd3a089b39`. Reviewed checkout/patch scripts and applicable Windows/shared-library/public-header patches; they do not add synchronization to the inspected font/render/cache code.
- Verified concrete shared-state hazards in the native source:
  - `core/fxge/cfx_gemodule.cpp:14` defines the shared graphics-module singleton, which owns the shared font manager.
  - `core/fxge/cfx_fontmgr.h:114` declares ordinary shared `std::map` font/face/glyph caches. `cfx_fontmgr.cpp:128`, `:136`, and `:163` perform unsynchronized lookup/insertion. Different documents and even different font faces can reach the same map.
  - `core/fxge/cfx_fontmapper.cpp:522` and `:938` reuse font faces across documents. `cfx_face.cpp:717` changes face transforms, loads glyphs into the same mutable face, and reads the glyph slot; these operations have no enclosing mutex.
  - `core/fxcrt/retain_ptr.h:175` implements shared-object retain/release with a non-atomic `uintptr_t` reference count, so prewarming caches does not establish safe parallel use.
  - `core/fpdfapi/font/cpdf_fontglobals.h:28` explicitly identifies a per-process singleton. `cpdf_fontglobals.cpp:115` and `:130` lazily mutate shared CMap/CID-to-Unicode caches; `:86` erases from a shared document-to-stock-font map.
  - Traced document close through `fpdfsdk/fpdf_view.cpp:855`, `CPDF_Document::StockFontClearer::~StockFontClearer()` at `cpdf_document.cpp:770`, and `cpdf_docpagedata.cpp:208` into global stock-font cleanup. Serialization must include cleanup/finalizers.
- The cached binary's own shipped `include/fpdfview.h:11` has the same all-APIs threading contract as the checked-out source. The native source independently supports the earlier conclusion: separate documents do not isolate PDFium state; callers must serialize calls into the shared library.
- This was source inspection, not a rebuilt-binary comparison or a race-detector/stress-test run. Version/branch/build configuration and applicable producer patches were checked; no exact build dependency reconstruction was performed. No throughput measurements or worker-count guarantees were established.

### Previous task: wrapper and documentation concurrency audit

- Read `AGENTS.md`, `README.md`, this state file, and the related performance plan; reviewed threading guidance throughout `/docs`, wrapper/native code, tests, and benchmarks.
- Verified PDFium's official contract in `https://pdfium.googlesource.com/pdfium/+/refs/heads/main/public/fpdfview.h`: calls must come from one thread or be serialized so only one PDFium call runs at a time, including calls involving different documents.
- Found unsafe or incomplete guidance in `README.md:152`, `AGENTS.md:159`, `docs/API-REFERENCE.md:44`, `docs/BEST-PRACTICES.md:30` and `:53`, `docs/HIGH-THROUGHPUT-PROCESSING.md:270`, and `docs/TROUBLESHOOTING.md:415`. One document per thread/request does not provide library-wide synchronization. File copies and byte-array loading do not fix the native shared-state problem.
- Confirmed there is no shared PDFium gate. `PDFium.cs` and its partials expose direct public native imports. Document/page operations, merging, save, and native cleanup call them without library-wide synchronization.
- Confirmed existing locks protect per-document page tracking, per-page object tracking, stream positioning, or per-form disposal. Resolver registration and interlocked counters do not serialize PDFium execution.
- Confirmed async APIs use sequential loops with `Task.Yield()`. This provides neither cross-document synchronization nor parallel native processing; continuations can change threads. Some stream enumerators retain a page across a yield to their consumer.
- Confirmed finalizers for `PdfDocument`, `PdfPage`, `PdfMerger`, and detached `PdfPageObject` call PDFium directly, so finalization can overlap foreground native calls even when an application serializes its explicit operations.
- Found an additional lifecycle issue by inspection: library initialization is in the `PdfDocument` static constructor, while `PdfMerger` constructors directly invoke PDFium without ensuring that constructor has run. This was not reproduced in a fresh process during the audit.
- Confirmed `MultipleDocuments_ShouldWorkConcurrently` (`PdfDocumentTests.cs:1300`) is sequential and does not verify multithreading. PDF test classes share one xUnit collection; current benchmarks measure individual synchronous operations rather than a concurrent service workload.
- Reviewed throughput implications: recommend a persistent bounded pool of separate worker processes, one active PDFium job per process, plus internal native-call serialization that also covers finalizers. Codec work may run separately once pixel-buffer ownership is safe; current JPEG/PNG/TIFF paths run inline and use native bitmap pointers.
- Identified misleading performance guidance: forced blocking GC as a default batch strategy, managed-only `GC.GetTotalMemory` as a native-memory monitor, and collecting all raw page buffers for large workloads. A US Letter BGRA page at 300 DPI occupies about 32 MiB before additional allocations.
- No library code, public docs, tests, or benchmarks were changed or executed for this static audit. Only this required state file was updated.

### Previous task: Windows native build validation

- Read `AGENTS.md`, `README.md`, `ai/current-state.md`, and `ai/plans/plan-native-build-script-windows.md`.
- Validated the Windows native build script on the current machine only, per request; no VM was used.
- Deleted existing `src/libs/win-x64/*.dll` outputs before validation so the script had to regenerate them.
- Fixed `src/native/build-natives.cmd` after local validation exposed two current-machine issues:
  - Added a PowerShell `Expand-Archive` fallback when `unzip` is not on PATH.
  - Shortened the internal libjpeg-turbo batch subroutine label after `cmd.exe` failed to resolve the longer label on this machine.
  - Improved NASM detection to find `%LOCALAPPDATA%\bin\NASM\nasm.exe` and pass it to CMake.
- Ran `src\native\build-natives.cmd --clean`; it completed successfully and regenerated all five Windows x64 DLLs.
- Verified every regenerated `src/libs/win-x64/*.dll` reports `8664 machine (x64)` with `dumpbin /headers`.
- Verified `pdfium_png.dll` exports only the `pdfium_png_*` shim API and has no runtime dependency on `libpng16*.dll` or `zlib*.dll`.
- Verified libjpeg-turbo built with NASM/SIMD on this machine; CMake reported `SIMD extensions: x86_64 (WITH_SIMD = 1)`.
- Ran `$env:LIBPNG_VERSION='1.6.56'; src\native\build-natives.cmd --only pdfium_png`; it completed successfully.
- Ran `dotnet test src\PdfiumWrapper.Tests\PdfiumWrapper.Tests.csproj`: 186 passed, 0 failed.
- Deleted the replaced `src/native/build_win_x64.bat`.
- Updated `docs/BUILDING-NATIVE-LIBS.md` and `AGENTS.md` to point at `src/native/build-natives.cmd`.
- Marked the relevant Windows plan Phase 5 and cleanup items complete.

## In progress

- No implementation work is currently in progress. Audits and the requested plan are complete; implementation/test checkboxes remain open.

## Next recommended step

When implementation is requested, read `ai/plans/plan-pdfium-concurrency.md` sections 3 and 4 first, then execute Phase 0 (doc correction, `[Collection]` on `PdfPageDeletionExample`, `NativeLibraryResolver` static-constructor barrier, `PdfiumWrapper.Tests.Host` with `init-race`). Tag `bench-baseline-pre-gate` and run Phase 1 before touching `PdfiumRuntime`. Rule R9 is approved; Phase 2 may proceed directly after Phase 1.

## Blockers or open questions

Findings from the 2026-10-02 readiness check (corrections to apply in Phase 2; not yet written into the plan):

- **`EnterAsync` records the wrong owner thread (plan 4.1).** `OnAcquired` runs inside the async method on the thread that completed `WaitAsync`. If the returned `ValueTask` completes before the caller registers its continuation, the caller resumes on a different thread while `s_owner` names a now-free pool thread. Result: nested `Enter()` self-deadlocks, and that pool thread can later "reenter" without holding the semaphore. Fix: claim ownership on the consuming thread (custom awaiter whose `GetResult()` sets owner, runs init and drain).
- **Drain can close a document before its pages (plan 4.1/4.4).** The drain walks kinds 0 to 4 while the finalizer thread enqueues page then document; a page enqueued after the drain passed kind 3 is skipped while its document is picked up in kind 4. Fix: snapshot queue counts in reverse kind order (4 to 0) before draining, and drain only those counts.
- **Backing memory released before the deferred close (plan 4.4/4.5).** The `PdfDocument` finalizer calls `ReleasePinnedMemoryDocument()` immediately, but `FPDF_CloseDocument` runs later in the drain. Same for `PdfForm`: `FPDFDOC_InitFormFillEnvironment` is passed `ref _formInfo` (an object field; the pinned `GCHandle` at `PdfForm.cs:42` pins a boxed copy), so a deferred `ExitFormFillEnvironment` would run after the `PdfForm` is collected. Fix: keep-alive handles released by the drain after the document close; allocate `FPDF_FORMFILLINFO` in native memory.
- **Spool temp file cannot be deleted while open on Windows.** PDFium opens with `FILE_SHARE_READ | FILE_SHARE_WRITE` only (`cfx_fileaccess_windows.cpp:32`). Plan 4.5 also shows `using var spool` in the constructor, which contradicts "deleted on document dispose or finalizer". Needs a deferred-delete path that never throws on the finalizer thread.
- **Plan/code mismatches.** No `_form` field: `GetForm()` (`PdfDocument.cs:1256`) returns a new caller-owned `PdfForm` per call and `PdfForm` has no finalizer, so the document must track a set of forms. Plan names `Save(Stream, SaveFlags)`/`SaveAsync(Stream)`; actual API is `Save(string, uint)` and `SaveToStream(Stream, uint)`, no async save. `SaveAsTiffAsync` has a `threshold` parameter and no `CancellationToken`. `PdfMerger` holds no source documents. `SpooledInput` ignores `MemoryStream.Position`/segment offset and does not advance the stream to its end, both of which current constructors do.
- **Existing tests that will need rewriting.** `PdfMergerTests.cs:173` and `:256` assert on `StreamDocumentLoader` through reflection.
- **Smaller gaps to settle while implementing.** `InternalsVisibleTo` for `PdfiumWrapper.Tests.Host` and `PdfiumWrapper.Benchmarks`; `LiveHandleCount` accounting when a page object is attached to or removed from a page; `BootstrapLock` can hand two load contexts different lock objects on first use; `Shutdown()` tests belong in the host because leaked handles from other tests make `LiveHandleCount == 0` unreliable in the shared test process; tag `bench-baseline-pre-gate` after adding the Phase 1 benchmark classes so the baseline can be rerun.
- **Needed from the owner.** Go-ahead to commit and tag locally (Phase 1 requires both); `N` and `T` before the Phase 4 decision gate.

Earlier notes:

- No blocker to the requested plan. The current wrapper still does not enforce PDFium's threading contract; no remedy has been implemented yet.
- The starvation test bound (heartbeat p99 under 100 ms) and the callback-isolation bound (3x solo p95) are initial guesses to be tuned after the first run and recorded in `benchmark.md`.
- Exact burst size/window, operation mix, target hardware/output storage, and durable-admission integration remain open. These refine acceptance targets and sizing, not the established native coordination requirement.
- Worker count and achievable documents/pages per second require workload measurements; no performance guarantee was established.
- The repository's existing threading guidance, including the supplied `AGENTS.md`, requires correction before implementing a concurrency design based on it.
- Prior native-build validation notes:
- VM validation was intentionally skipped by request.
- No no-NASM rebuild was performed because the current machine has NASM installed at `%LOCALAPPDATA%\bin\NASM\nasm.exe`.
- `dotnet test` passes but emits existing NuGet vulnerability warnings for `Magick.NET-Q16-AnyCPU` 14.9.1 and existing nullable/obsolete warnings.
- Shell commands required escalation because the Windows sandbox shell failed with `windows sandbox: spawn setup refresh`.

## Recently changed files

- `ai/current-state.md`
- `ai/plans/plan-pdfium-concurrency.md` (rewritten 2026-09-30)
- `_native_build/pdfium-source-7869/` (ignored upstream source checkout)
- `_native_build/pdfium-binaries-7869/` (ignored producer release checkout)

Previous native-build session files (unchanged by this audit):

- `ai/plans/plan-native-build-script-windows.md`
- `AGENTS.md`
- `docs/BUILDING-NATIVE-LIBS.md`
- `src/native/build-natives.cmd`
- `src/native/build_win_x64.bat` (deleted)
- `src/libs/win-x64/pdfium.dll`
- `src/libs/win-x64/pdfium_png.dll`
- `src/libs/win-x64/tiff.dll`
- `src/libs/win-x64/tiff_shim.dll`
- `src/libs/win-x64/turbojpeg.dll`
