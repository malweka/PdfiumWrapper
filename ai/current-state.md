# Current State

## Current focus

Global installation of `ai-pr-review` is complete: copied PonaFlow's skill to `C:/Users/hamsm/.codex/skills/ai-pr-review` and verified matching SHA-256 hashes. It will be available across Codex projects on the next turn.

PR #15 architecture review is complete (2026-10-02), at head `ecb22a546a403ccb05bded8d7180edf08272276a`, against merge base `9021729ca8e6cfa420a15648dfddb2e779820187`. Verdict: request changes for four reproduced findings below. Release 1 of `ai/plans/plan-pdfium-concurrency.md` is implemented on `feature/pdfium-concurrency-plan` (version 2.0.0); Release 2 remains deferred pending the owner's capacity/deployment inputs. Review only: no implementation or test sources were changed.

## Completed

### Latest task: install ai-pr-review globally (2026-10-02)

- Copied the complete `C:/Dev/emh/PonaFlow/.agents/skills/ai-pr-review` directory to `C:/Users/hamsm/.codex/skills/ai-pr-review`, following the skill-installer destination convention. `CODEX_HOME` was unset; the global destination did not already exist.
- Verified the installed file count and every file's SHA-256 hash against the source. PonaFlow's source copy was preserved. No blockers; no library code changed or tests rerun for this file-copy task. Use `$ai-pr-review pr <number>` from any project on the next turn.

### Latest task: review PR #15 (2026-10-02)

- Applied the explicitly requested `ai-pr-review` skill from `C:/Dev/emh/PonaFlow/.agents/skills/ai-pr-review/SKILL.md`; read repository instructions, README, current state, concurrency plan, authenticated PR metadata, diff and relevant native source/callers/tests. Local HEAD matches the PR head; initial working tree was clean.
- Ran `dotnet test src/PdfiumWrapper.Tests/PdfiumWrapper.Tests.csproj --no-restore --verbosity quiet`: 219 passed, zero failures, on win-x64. Existing NuGet vulnerability warnings for the unchanged Magick.NET test dependency remain. Built the benchmark project successfully with `--no-restore` (zero warnings/errors). Linux/macOS and performance tables were not rerun.
- Reproduced synchronous admission in `PdfDocument.StreamImageBytesAsync`: its factory call did not return during a 300 ms gate hold, and returned after the gate was released. It calls synchronous `RequirePages()` before returning the async iterator.
- Reproduced missing bitmap ownership in `PdfImageObject.GetBitmap`: returned a nonzero caller-owned bitmap, live-handle delta was zero, no public bitmap release method exists, and `LiveHandleCount` was zero after disposing the document/page while the bitmap remained alive. Confirmed both bitmap getter ownership contracts against the bundled PDFium header. The probe cleaned up its bitmap through reflection under the gate.
- Reproduced destructive benchmark output handling using only a newly created temporary fixture: `BurstRunner.Run` deleted a pre-existing sentinel in its `--out` directory even with `--keep-output true`.
- Reproduced incorrect benchmark format allocation: 74 jobs with `--mix png:18,jpeg:19` produced 74 PNGs and zero JPEGs (expected 36/38); fixed stride 37 collapses all slots when total weight is 37.
- Probe sources, binaries, fixtures and reports were created only under `%TEMP%/pdfium-pr15-review-5912bf2272b4459bb753acaeea4d3e77/`. No PR comment, fetch, push, merge or implementation fix was performed. Only this required state record was modified in the repository.

### Latest task: implement the concurrency plan, Release 1 (2026-10-02)

- Phase 0: `NativeLibraryResolver` static-constructor barrier; `PdfPageDeletionExample` in the `PDF Tests` collection plus `TestProjectHygieneTests`; `PdfiumWrapper.Tests.Host` and `HostRunner`. The `init-race` scenario aborted the pre-gate code in 8 of 8 runs.
- Phase 1: tag `bench-baseline-pre-gate` (commit `f2478f6`); `SmallDocumentBenchmark`, burst runner, `cold-start` scenario; baselines recorded in `benchmark.md`.
- Phase 2: `PdfiumRuntime` (reentrant gate, init, deferred release, `Shutdown`), `PdfiumDiagnostics`, `SharedState`, `SpooledInput`, `PooledFileWriter`, `BitmapLease`. Every public PDFium-touching member of the wrapper types enters the gate. Finalizers only enqueue. The 192 raw imports are `internal`. `StreamDocumentLoader` and `PdfStreamFileWriter` are deleted.
- Phase 3: `src/PdfiumWrapper.Tests/Concurrency/` (`GateCoverageTests`, `PdfiumConcurrencyTests`, `PdfiumHostTests`) and host scenarios `init-race`, `cold-start`, `starvation`, `finalizer-drain`, `alc-shared-gate`, `shutdown`, `crash-probe`. 219 tests passed on win-x64 and linux-x64 (Docker, .NET 8 SDK image) at that point; 228 after the review follow-up.
- Phase 4: `GateOverheadBenchmark`, `ConcurrentCallersBenchmark`, `StreamCallbackBenchmark`, instrumented burst runs, replica scale-out runs. Results and the decision are in `benchmark.md` and in the plan's "Implementation record".
- Phase 8: `README.md`, `AGENTS.md`, `docs/API-REFERENCE.md`, `docs/BEST-PRACTICES.md`, `docs/HIGH-THROUGHPUT-PROCESSING.md`, `docs/TROUBLESHOOTING.md` rewritten for the gate, with measured capacity and a sizing rule.
- The shipped code departs from the plan's section 4 reference code in several places (async admission, drain ordering, release of backing memory, spool files, forms, save API names). Each is listed with its reason in the plan's "Implementation record".
- Found and fixed along the way: `FPDFPageObj_GetMatrix` had the wrong native signature (`PdfPageObject.GetMatrix()` corrupted the stack); `FPDF_FORMFILLINFO` was passed from a movable managed object.

Key measurements (one machine: i7-13700F, 24 logical processors, win-x64):

- Gate cost: 28 ns per uncontended entry; one-page load/render within 0.5% of the pre-gate code; sequential batch unchanged (154.3 s against 153.5 s).
- One process: 1.30 docs/sec sequential, 1.62 docs/sec (20.1 pages/sec) with 4 or more callers on the mixed corpus at 200 DPI. The gate is held 99.8% of the time; rendering dominates and is serialized. In-process parallelism is worth 1.25x.
- Several processes: 3.13 docs/sec with 2, 5.75 with 4, 9.05 with 8, 11.07 with 16.
- Async API keeps the pool free (heartbeat p99 1.3 ms against 2.5 s with the sync API on pool threads).
- Crash probe: 25 damaged inputs per platform, no process abort.

### Review follow-up on pull request 15 (2026-10-02)

- Pull request: https://github.com/malweka/PdfiumWrapper/pull/15 (`feature/pdfium-concurrency-plan` into `main`).
- Fixed four review findings: the burst runner no longer deletes the `--out` directory (run-owned child directory); its format mix is exact for any weights; `PdfImageObject.GetBitmap()` / `GetRenderedBitmap()` return managed BGRA pixels instead of an unreleasable native handle; `StreamImageBytesAsync` / `StreamJpegBytesAsync` no longer wait synchronously for the gate.
- Added `BurstRunnerTests` (runs the benchmark executable as a child process), tests for async stream admission and validation timing, a `stream` mode in the starvation scenario, and an image-bitmap test. 228 tests pass on win-x64 and linux-x64.

### Previous task: implementation-readiness check (2026-10-02)

- Confirmed the plan's evidence against the code and found defects in its reference code. All of them were corrected during implementation; see the plan's "Implementation record".
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

- Nothing active. The global skill installation and PR #15 review are complete; the four PR findings await an implementation request.

## Next recommended step

0. Address PR #15 findings: isolate benchmark outputs in a fresh run-owned child directory; replace raw image-bitmap returns with managed pixels or tracked disposable ownership; move async streaming's native page-count validation to async admission; fix weighted format scheduling for totals sharing a factor with 37. Add regressions for these paths and rerun the full suite before merge.
1. Project owner supplies `N` (documents per burst), `T` (window), the real document mix, and whether the consuming service can run several replicas behind its queue. With those, apply rule R13: if replicas are possible, size them from `benchmark.md` and stop; if not, build the process pool (plan Phases 5 to 7).
2. Run the test suite on macOS (osx-x64, osx-arm64); it has only been run on win-x64 and linux-x64.
3. Review and merge `feature/pdfium-concurrency-plan`. It is a major version (2.0.0): raw `PDFium.*` imports are no longer public.

## Blockers or open questions

- PR #15 should not merge before its reproduced data-loss issue (`BurstRunner.cs:77-78`) and bitmap/async/format-scheduling defects are addressed. No fixes were authorized during the review.
- The Release 2 decision is open for the reason above. The measurements say a burst of thousands of documents in a short window is beyond one process on the test hardware (usable rate about 1.3 docs/sec per process on the mixed corpus), so some multi-process arrangement is needed; which one is the owner's call.
- macOS not verified.
- Cold start with a document first is about 5 ms slower than before: initialization now loads libtiff to install its error handlers. Moving that to the first TIFF write would recover it.
- Merge benchmark moved between -5.9% and +4.4% by document (buffered save). Peak memory during a save now includes the whole output.
- `benchmark.db` was not updated with the new runs; the tables are in `benchmark.md` only.
- The starvation test bound (heartbeat p99 under 100 ms) is loose against the measured 1.3 ms.
- `PdfImageObject.SetBitmap(IntPtr, IntPtr)` and `SetImage(byte[], IntPtr)` are still public but take native handles that public callers can no longer obtain; `PdfPage.AddImage` is the usable path. Not changed.
- `docs/DOCUMENT_PROPERTIES_IMPLEMENTATION.md` still shows pre-gate implementation snippets with raw `PDFium.FPDF_*` calls. `.github/copilot-instructions.md` points at `AGENT.md`; the file is `AGENTS.md`.
- Other raw import signatures were not audited. The gate-coverage test exercised every public member once and found one wrong signature; imports not reachable from the public API are unchecked.
- Benchmarks on this machine: run a baseline worktree from the same volume and kind of directory as the repository. A worktree under `%TEMP%` made file opens about 60 µs slower.

Earlier notes:Earlier notes:

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

- Global skill installation session: `C:/Users/hamsm/.codex/skills/ai-pr-review/SKILL.md` (outside the repository), `ai/current-state.md`.
- Current review session: `ai/current-state.md` only.

Previous implementation session:

- `src/PdfiumWrapper/`: new `PdfiumRuntime.cs`, `PdfiumDiagnostics.cs`, `SharedState.cs`, `SpooledInput.cs`, `PooledFileWriter.cs`, `BitmapLease.cs`; rewritten `PdfDocument.cs`, `PdfPage.cs`, `PdfMerger.cs`, `PdfForm.cs`, `PdfPageObject.cs` and subtypes, `PdfMetadata.cs`, `PdfBookmarks.cs`, `PdfAttachments.cs`, `PdfHelpers.cs`, `TiffWriter.cs`, `NativeLibraryResolver.cs`; `PDFium*.cs` imports made internal; version 2.0.0 in both `.csproj` files
- `src/PdfiumWrapper.Tests/`: `Concurrency/*`, `HostRunner.cs`, `TestProjectHygieneTests.cs`, `Bootstrapper.cs`, `PdfMergerTests.cs`, `PdfPageEditingTests.cs`, `PdfDocumentTests.cs`
- `src/PdfiumWrapper.Tests.Host/` (new project)
- `src/PdfiumWrapper.Benchmarks/`: `BurstRunner.cs`, `BurstDiagnostics.cs`, `SmallDocumentBenchmark.cs`, `GateOverheadBenchmark.cs`, `ConcurrentCallersBenchmark.cs`, `StreamCallbackBenchmark.cs`, `Program.cs`, `BenchmarkBase.cs`
- `README.md`, `AGENTS.md`, `benchmark.md`, `docs/API-REFERENCE.md`, `docs/BEST-PRACTICES.md`, `docs/HIGH-THROUGHPUT-PROCESSING.md`, `docs/TROUBLESHOOTING.md`
- `ai/plans/plan-pdfium-concurrency.md`, `ai/current-state.md`

Earlier sessions:
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
