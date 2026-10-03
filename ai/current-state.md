# Current State

## Current focus

.NET 10 and dependency upgrade (2026-10-03), branch `feature/net10-upgrade` from `main` at `4497749`, uncommitted. Every project targets `net10.0`; win-x64 and linux-x64 natives are rebuilt at the new versions; 274 tests pass on win-x64 and on linux-x64 (.NET 10 SDK container). **macOS natives (osx-arm64, osx-x64) are still the old versions** and must be rebuilt on a Mac before a release: `bash src/native/build-natives.sh --target osx-arm64` and `--target osx-x64`, then run the tests.

### Earlier focus (2026-10-03, before the upgrade)

Follow-up verification of the local, uncommitted PR #18 fixes is complete (2026-10-03). GitHub and local HEAD remain `7080c0377551a0f4f572f3ba8ba1a259c2099ba8`; the fixes are in the working tree. All 273 Windows tests pass, including the six added regression tests, and the comparison project builds. Two findings remain: Critical deletion of pre-existing image output by cleanup for a job that wrote nothing (`PdfProcessingPool.cs:568`), and Moderate retention of every completed batch continuation despite the new admission bound (`PdfProcessingPool.Operations.cs:184`). Verdict remains request changes. No implementation edits were made during this verification.

PR #18 architecture review is complete at head `7080c0377551a0f4f572f3ba8ba1a259c2099ba8` (merge base `5930c8606bac014c81ee0b6f8b7da4bc4bd12d3f`). Verdict: request changes for five reproduced findings: batch output-name collisions, unbounded batch submissions/spooling, cancellation while waiting for admission throwing instead of returning a status, incomplete output cleanup after failure, and cancellation during worker startup leaking a child and returning a pool. No implementation fixes were requested or made.

Release 2 (worker pool) is implemented and measured on branch `feature/worker-pool` (from `main` after PRs 16 and 17): `src/PdfiumWrapper.Processing` (`PdfProcessingPool`, `PdfWorkerHost`, protocol, sizing, `JobsPerWorker`), 29 tests in `src/PdfiumWrapper.Tests/Processing/`, `PdfiumWrapper.Tests.Host` as the test worker with fault injection, `--engine pool` in the comparison project's throughput runner, documentation, and the Phase 7 numbers in `benchmark.md`. 267 tests pass on win-x64 and linux-x64.

Phase 7 result: a warm pool of 8 does 10.07 requests/s against 10.86 for 8 independent processes (within the 10% acceptance); cold start to 8 in 4.5 s. The first version, one job per worker, was 16% behind; `JobsPerWorker = 2` (a worker encodes one document while PDFium renders another) recovered it.

Open after Release 2: the 10,000-job qualification and 30-minute soak (Phase 6, not in PR CI), a self-contained-publish hosting run, macOS, isolating the coordinator's own CPU (the harness measured 0.12 cores including its own submitters and sampler), a `pool` mode in the burst runner, and the second-wave operations (merge, forms, bookmarks, attachments).

### Earlier focus (2026-10-02, before the pool)

Release 2 of `ai/plans/plan-pdfium-concurrency.md` (the worker pool, package `PdfiumWrapper.Processing`) was approved by the owner on 2026-10-02 and the plan was revised: section 4.9 holds the API, hosting model, sizing policy and protocol; Phases 5 to 7 are the build order, tests and acceptance. Implementation has not started. Also on the working tree, uncommitted, on branch `feature/tiff-gray-render`: gray TIFF rendering, the comparison harness, and documentation notes.

### Earlier focus

Follow-up verification of PR #15 commit `f3b15a1130567dc00f604c61b5aab9bc98a9e242` is complete (2026-10-02). The four original reproductions are fixed; one additional accepted-weight overflow edge case remains in `BurstRunner.InterleavingStride`. Independently ran 228 passing tests on Windows and in Linux Docker; exact-commit CI is green, including 228 Linux tests and all four platform build/package jobs. No implementation fixes were made during verification.

Release 1 of `ai/plans/plan-pdfium-concurrency.md` is implemented on `feature/pdfium-concurrency-plan` (version 2.0.0); Release 2 remains deferred pending the owner's capacity/deployment inputs. The global `ai-pr-review` skill is installed and available.

## Completed

### Latest task: resolve the compiler warnings (2026-10-03, same branch)

- The build had 28 distinct warnings (9 library, 19 tests); it now has none on win-x64 and linux-x64. 275 tests pass on both (one new).
- Three were real defects: `PdfAttachments.ExtractAll` threw on an attachment with no name or no contents, and wrote outside the output directory for names such as `../x`, `..\x` or an absolute path (attachment names come from the document). It now keeps the last path component, replaces invalid file-name characters (":" would open an NTFS alternate data stream), falls back to `attachment_N`, and writes empty attachments as empty files. `PdfForm.FindFormField` threw on a field with no name. New test `ExtractAll_WritesEveryAttachmentInsideTheOutputDirectory` (PdfDocumentTests) with internal imports `FPDFDoc_AddAttachment` and `FPDFAttachment_SetFile` (length as `CULong`). `docs/API-REFERENCE.md` describes the naming.
- Annotation-only: `PdfMetadata.SetAllMetadata` parameters are `string?`; tests assert `GetForm()` is not null before use, cast `(string?)null`, suppress CS0618 around the deliberate `GetAllPages()` disposed check, and `FailingOperations_AlwaysReleaseTheGate` awaits its probe instead of blocking (xUnit1031).
- Found, not changed: the attachment/metadata imports declare C `unsigned long` as `ulong` (8 bytes) although it is 4 bytes on Windows (32 such `ulong`s across the PDFium partials; `out ulong` relies on the local starting at zero). `FPDFAttachment_HasKey` passes its key as UTF-16 where PDFium takes a byte string (`FPDF_BYTESTRING`).

### Earlier task: upgrade to .NET 10, native and NuGet dependencies (2026-10-03)

- .NET: `net8.0` to `net10.0` in all seven projects; CI (`pr-build.yml`, `release.yml`) uses `10.0.x`; README, AGENTS.md, package description and the TROUBLESHOOTING Dockerfile say .NET 10. Measured figures in `benchmark.md` and `docs/HIGH-THROUGHPUT-PROCESSING.md` still name .NET 8.0.31 because that is what they were measured on.
- NuGet: removed `Microsoft.SourceLink.GitHub` 8.0.0 from the core and Processing projects (Source Link is built into the SDK; the package pulled in `Microsoft.Build.Tasks.Git` 8.0.0, which has advisory GHSA-23fw-v26w-5fgq). Verified a packed nupkg still carries the repository commit and the snupkg PDB maps to raw.githubusercontent.com. Tests: Magick.NET-Q16-AnyCPU 14.9.1 to 14.17.2 (clears all its NU190x advisories), Microsoft.NET.Test.Sdk 17.8.0 to 18.10.1, xunit 2.5.3 to 2.9.3 (still v2), xunit.runner.visualstudio 2.5.3 to 4.0.0, coverlet.collector 6.0.0 to 10.1.0. BenchmarkDotNet 0.14.0 to 0.15.8 (both benchmark projects). Aspose.PDF in the comparison project left at 25.9.0: a newer version may be outside the license's subscription date. `dotnet list package --vulnerable --include-transitive` is now clean.
- Natives: PDFium chromium/7869 to 8076 (156.0.8076.0), libtiff 4.7.1 to 4.7.2, libjpeg-turbo 3.1.4.1 to 3.2.0, zlib-ng 2.2.4 to 2.3.3, libpng 1.6.56 to 1.6.59. PDFium public headers 7869 to 8076 are additive only (new functions, `FPDF_LIBRARY_CONFIG` versions 6 and 7; the wrapper calls `FPDF_InitLibrary`). Windows built with NASM (`WITH_SIMD = 1`); Linux built in Docker (ubuntu:22.04, `WITH_SIMD = 1`); `libtiff.so` still needs only system `libz.so.1`, as before. `libtiff_shim.so` rebuilt byte-identical.
- Build scripts: PDFium pinned to 8076 instead of `latest`; the extracted PDFium archive is cached per version (`_native_build/pdfium-<version>-<rid>`), so a version change downloads again and `latest` always does (before, an existing `pdfium-win-x64` folder kept the old build silently). `build-natives.sh` now accepts a bare build number like the Windows script (`chromium/` prefix optional). `docs/BUILDING-NATIVE-LIBS.md` version table includes PDFium.
- Running `build-natives.sh --target linux-x64` from Git Bash on Windows needs `MSYS2_ARG_CONV_EXCL="<repo path>:;/src"` so the docker `-v`/`-w` arguments are not path-converted while host `curl` still is; `MSYS_NO_PATHCONV=1` breaks host curl.
- Verified: build 0 errors, same compiler/analyzer warnings as net8.0; 274/274 tests on win-x64 with the old natives, 274/274 with the new ones; 274/274 on linux-x64 in `mcr.microsoft.com/dotnet/sdk:10.0` with the new natives; comparison project builds.
- Not done: macOS natives (needs a Mac), commit, PR. A `git stash` entry (`stash@{0}`) duplicating these csproj edits was left from a warning comparison and can be dropped.

### Latest task: verify the PR #18 review fixes (2026-10-03)

- Read the updated repository rules, README, state, concurrency plan and ai-pr-review skill; reviewed the local follow-up changes in the coordinator, operations, worker, pending-job model, fault hooks and regression tests against `7080c03`. Unrelated documentation edits were excluded. Confirmed via GitHub that PR #18 still points to `7080c03`; its green CI applies to the original commit, not the uncommitted fixes.
- Independently ran `dotnet test src/PdfiumWrapper.Tests/PdfiumWrapper.Tests.csproj --no-restore --verbosity quiet`: 273 passed, 0 failed, Windows (2 m 24 s). Existing test dependency vulnerability warnings remain. The comparison project builds with 0 warnings/errors.
- The original collision, unbounded spooling, admission cancellation, managed/crashed output cleanup and cancelled-start reproductions are covered by the six added regression tests, which pass. The new semaphore bounds active/spooled/unread batch work, and cancelled creation kills the child and propagates cancellation.
- Isolated xUnit cases outside the repository confirmed a cleanup regression: a pre-cancelled byte-input PNG job returns Cancelled with Attempts=0 but deletes an existing `page_001.png`; a job failing to open a missing input also deletes that file. `Finish` calls `RemovePartialOutput` on every non-success, and its `PagesDone + 1` loop guesses ownership even when no file was written.
- A separate reflection-backed xUnit case confirmed that after reading 50 batch results with a bound of 3, `pending` still retained 53 completed continuation tasks. Active work is bounded, but task history grows with the total batch size. Recommend a bounded set of outstanding tasks or fixed producer/consumer tasks rather than retaining all completions.
- An additional early-disposal probe observed a batch submitter remaining pending after both iterator and pool disposal. It was not reported as a separate finding without stronger lifecycle/retention evidence; no implementation fix was attempted.
- Verdict: request changes for the output-deletion regression and incomplete task-memory bound. No Linux/macOS run, self-contained publish, qualification burst, soak or benchmark rerun was performed. Updated only this required state file; preserved all implementation and documentation changes.

### Latest task: fix the five findings of the PR 18 review (2026-10-02)

All five reproduced by reading the code and fixed on `feature/worker-pool`, with a test each (35 pool tests, 273 in all, pass on win-x64; linux-x64 not rerun):

- Batch output names collided (`report`, `report`, `report-2` gave two paths): `OutputsFor` now reserves every name handed out.
- Batches started every job at once and spooled every input: `Batch` bounds documents in any stage to `QueueCapacity + MaxWorkers x JobsPerWorker`, released as the caller reads results.
- Cancellation while waiting for a queue slot threw: `SubmitAsync` and the spool in `RunAsync` return a `Cancelled` result through `Finish`, so counters and events agree.
- Failed image jobs left pages and a `.tmp`: the worker removes written pages on any exception; the coordinator tracks `Progress` frames and removes a dead worker's pages and temp files (`RemovePartialOutput`) before a retry and on the final failure. `Worker` acts on the process exit only after its stdout is drained.
- Cancelling `CreateAsync` leaked the child: `Worker.StartAsync` kills it on any cancellation and rethrows the caller's; `StartWorkerAsync` swallows only the shutdown token; `DisposeAsync` waits for starts in progress.
- Test hooks: `BeforeHello`, `AfterPage`; faults `slow-start:<ms>` (writes its pid to `PDFIUMWRAPPER_TEST_PIDFILE`) and `crash-after-page-N:<match>`.

Second review pass (2026-10-03), two findings, both fixed (36 pool tests, 274 in all, pass on win-x64):

- The batch kept every job's continuation in a list until the whole batch finished. Outstanding jobs are now counted; the last to finish completes the results channel.
- `RemovePartialOutput` ran for every unsuccessful job and deleted `page_001` even when the job never ran (pre-cancelled, missing input). Image jobs now stage all pages as `<final>.<jobId>.tmp`, report `CommittingPages` in a `Progress` frame, then move them into place; the coordinator cleans up only after a crash or kill, and only the job's own `.tmp` files plus the final names it had claimed once committing. `PendingJob.CommittingPages`, `ProgressPayload.CommittingPages`. Test `CleanupNeverTouchesOutputTheJobDidNotWrite`.

Not committed. Linux run, qualification burst and soak still open.

### Earlier task: review PR #18 (2026-10-02)

- Applied the global `ai-pr-review` skill; read AGENTS.md, README.md, the state records and the concurrency plan (Release 2 section 4.9 and Phases 5 to 8). GitHub PR #18 and local HEAD both resolve to `7080c0377551a0f4f572f3ba8ba1a259c2099ba8`; merge base is `5930c8606bac014c81ee0b6f8b7da4bc4bd12d3f`. Reviewed only that 33-file diff and relevant core callers; excluded existing local documentation/state edits.
- Independently ran `dotnet test src/PdfiumWrapper.Tests/PdfiumWrapper.Tests.csproj --no-restore`: 267 passed, 0 failed (Windows, 2 m 5 s). Existing Magick.NET vulnerability and compiler/analyzer warnings remain outside this PR's scope. Built the opt-in comparison project with `--no-restore`: success, 0 warnings/errors. Exact-head GitHub CI reports success for Linux tests and four platform core build/package jobs.
- Ran five isolated xUnit reproductions from a temporary project outside the repository against the reviewed assemblies. All five confirmed defects: three successful TIFF jobs named `report.pdf`, `report.pdf`, `report-2.pdf` produced two distinct output paths; a blocked worker with QueueCapacity=2 retained over 90 batch-spooled inputs (94 submitted / 93 reported queued at the sample); cancelling a capacity-waiting submission threw OperationCanceledException; failure to rename page 2 left page 1 and a `.tmp` file; cancellation while awaiting a worker Hello returned a pool and left its child alive after DisposeAsync. The deliberately orphaned proof child was killed by the test.
- Verdict: request changes (one Critical output-loss finding, four Moderate correctness/architecture findings). No code fixes, fetches, pushes, merges, PR comments, or implementation-plan changes were made. Only this required state record was edited in the repository.
- Verification limits: no independent Linux/macOS runtime run, self-contained publish, 10,000-job qualification, 30-minute soak, or benchmark rerun. The PR explicitly records the latter qualification/hosting gaps as deferred.

### Latest task: document the in-process and pool paths side by side (2026-10-02)

- Added "Two Paths: In-Process or Worker Pool" to `docs/HIGH-THROUGHPUT-PROCESSING.md`, after Core Principles and in the table of contents: the two packages and their dependency direction (the pool is optional; the core never starts a process), the same folder-to-PNG job written both ways, a feature table (rendering, crash isolation, memory, errors, operations, deployment), when to use which, and a call-mapping table for moving between them.
- Documentation only. No code or tests changed; figures are the ones already recorded in the file and `benchmark.md`.

### Earlier task: version comparison and engine comparison (2026-10-02, after the merge)

- Pull request 15 was merged into `main` (merge commit `df0ef31`). The feature branch still exists.
- `benchmark.db` now holds two runs from this machine: `pre-gate-1.0.0` (the `bench-baseline-pre-gate` tag) and `gated-2.0.0` (merged code). Conversion is within about 3% of 1.0.0; merge is up to 5.6% slower on the larger documents. `benchmark.db` is modified and not committed.
- New opt-in project `src/PdfiumWrapper.Benchmarks.Comparison` (not in `PdfiumWrapper.sln`): PdfiumWrapper against other engines for page count, TIFF, PNG, JPEG, merge and text. Ghostscript is located on `PATH` or through `GHOSTSCRIPT_EXE`; Aspose.PDF needs `ASPOSE_PDF_LICENSE` and is skipped without it. A `check` mode verifies the engines produce equivalent output.
- **Owner's rule: results for other engines are not published.** The comparison report and its CSV files are in `ai/tmp/`, which is ignored by git. Only PdfiumWrapper's own numbers go into tracked files. License files are never copied into this repository; `*.lic` is ignored as a guard.
- One finding relevant to the wrapper itself: bilevel TIFF renders a 32-bit page and then thresholds it.

### Experiment: render TIFF pages into an 8-bit gray bitmap (2026-10-02)

- Prototype only, in `src/PdfiumWrapper.Benchmarks/GrayscaleTiffExperiment.cs` (`dotnet run -c Release -- grayscale [dpi]`, plus `--dump <dir>` for image crops). The library is unchanged. Not committed.
- Method: `FPDFBitmap_CreateEx` with `FPDFBitmap_Gray`, then threshold or copy one byte per pixel. Whole document to one TIFF at 200 DPI, median of 7 runs, pinned to the performance cores.
- Result, bilevel TIFF: 28% to 39% faster on the four text documents (for example contract.pdf 158.6 ms to 108.7 ms); no gain on presentation.pdf (3,092 ms to 3,019 ms), whose time is almost all page rendering and that is not faster in gray.
- Result, grayscale TIFF: 26% to 32% faster on the four text documents and files 23% to 26% smaller; 5% faster on presentation.pdf.
- The bitmap is a quarter of the size (US Letter at 200 DPI: 3.7 MB instead of 15 MB), and the time saved is inside the native gate.
- The output is not identical. PDFium anti-aliases text differently by bitmap depth (`cfx_renderdevice.cpp`: below 16 bits per pixel it uses normal grayscale anti-aliasing, at 32 bits LCD-mode anti-aliasing normalized to gray). About 0.6% to 1.9% of pixels differ after thresholding on text documents, at glyph edges; on contract.pdf page 1 the gray render has about 2% fewer black pixels. Crops of both were inspected and are equally legible.
- The `FPDF_GRAYSCALE` render flag on a BGRA bitmap gives near-identical output to today's and no speedup.
- Measurement note: on this hybrid CPU an unpinned run showed the same render taking either about 2.9 s or about 6.0 s depending on the core it landed on. Pin stopwatch experiments to the performance cores.
- **Adopted** at the owner's request (same day), on branch `feature/tiff-gray-render` (from `main`, not committed yet). `PdfPage.RenderToBitmapLeaseCore(..., gray: true)` creates the bitmap with `FPDFBitmap_CreateEx(FPDFBitmap_Gray)`; `BitmapLease.IsGray`; `PixelConverter.GrayToPackedBilevel` / `GrayToGrayscale`; `PdfDocument` uses the gray lease for every TIFF path. The prototype file was removed. New test `SaveAsTiff_Content_ShouldMatchTheBgraRender` decodes the TIFF with Magick.NET and compares its ink with the BGRA render. 239 tests pass on win-x64 and linux-x64. `PdfToTiffBenchmark`: 27% to 39% faster on the four text documents, 2% on the presentation; recorded in `benchmark.md` and in `benchmark.db` as `gray-tiff-2.0.0`.

### Throughput comparison harness (2026-10-02)

- `src/PdfiumWrapper.Benchmarks.Comparison` gained a `throughput` command (`ThroughputRunner.cs`): N requests arrive at once, each reads the page count and converts every page to PNG. One engine and one deployment shape per run: `--engine pdfium|ghostscript|aspose --n 1000 --concurrency C [--processes P] --dpi 150`. Multi-process runs spawn copies of the program that start together. Reports go to `ai/tmp/throughput/` (private).
- Sweep run 2026-10-02: 1,000 requests (12,400 pages, PNG at 150 DPI), 16 deployment shapes across the three engines, all completed without failures. Report: `ai/tmp/throughput-comparison.md` (private; no figures for other engines belong in tracked files). PdfiumWrapper's own figures: one process 1.49 requests/s with one request thread and 2.21 with four (gate-bound, 1.5 cores); 4 processes 7.19, 8 processes 10.87, 16 processes 13.06 requests/s (19.6 cores, 1.9 GB). The deciding factor for throughput is running several processes, which supports replicas or the Release 2 process pool.
- Correction: `presentation.pdf` has 37 pages, not the 30 its benchmark label says (the corpus is 62 pages, not 55). The label is kept so `benchmark.db` history still joins; `BenchmarkBase.CorpusPages()` now counts real pages, and the `ConcurrentCallersBenchmark` pages/sec table in `benchmark.md` was recomputed (speedups unchanged).
- The comparison project's BenchmarkDotNet artifacts now go to `ai/tmp/comparison-artifacts`. Reason: a run from the repository root left other engines' CSV files in the shared artifacts folder and they were imported into `benchmark.db` by mistake; that run was deleted, the database vacuumed, and its bytes checked. **Only import from a folder that holds PdfiumWrapper's own CSV files.**


### Latest task: verify PR #15 review fixes at f3b15a1 (2026-10-02)

- Read startup files, the global `ai-pr-review` skill, concurrency plan updates, and the 18-file follow-up diff against `ecb22a5`. Confirmed PR head and local HEAD are `f3b15a1130567dc00f604c61b5aab9bc98a9e242`; the working tree was clean when verification started.
- Confirmed the regression tests exercise output preservation across kept/cleaned runs, exact 36/38 allocation for `png:18,jpeg:19`, bitmap cleanup/accounting, managed validation timing, factory return under contention, and constrained-pool async streaming. Managed bitmap returns make caller-owned native-bitmap shutdown checks unnecessary; native leases remain counted during the internal copy.
- Independently reran the full suite: 228 passed on Windows; 228 passed in a Linux .NET 8 SDK Docker container with a read-only repository mount and isolated build outputs. Existing compiler/analyzer warnings remain.
- Verified GitHub Actions run `37012904355` for the exact commit completed successfully: Linux build/tests (228 passed), plus build/package jobs for win-x64, osx-x64, osx-arm64 and linux-x64. macOS native runtime tests were not executed. Run: https://github.com/malweka/PdfiumWrapper/actions/runs/37012904355.
- Temporary pixel probes checked exact BGRA bytes and cleanup for Gray, BGR, BGRx and BGRA source buffers (including row padding), and confirmed public `GetBitmap()` pixels exactly match the rendered PNG source.
- Confirmed `InterleavingStride(100) == 37`, so the recorded default-mix job sequence is unchanged; performance benchmarks were not rerun.
- Found a new overflow boundary at `BurstRunner.cs:406`: accepted weights `png:1073741823,jpeg:1073741823` total 2,147,483,646; `37 + totalWeight` overflows, the search never runs and `InterleavingStride` returns zero. A direct compiled probe confirmed zero; every job then uses slot zero. Fix with a widened search bound or reject out-of-range weight totals, and test positive/coprime stride at the boundary.
- Temporary probes/reports are under `%TEMP%/pdfium-pr15-f3b15a1-1544d7a0fcb04cdcad1a88d0c3845d10/`; Windows test log is `%TEMP%/pdfium-pr15-f3b15a1-tests.log`. Only this state record was edited by the verification agent. Unrelated working-tree edits to README and image setter visibility appeared during verification and were excluded from the commit review.

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
- Added `BurstRunnerTests` (runs the benchmark executable as a child process), tests for async stream admission and validation timing, a `stream` mode in the starvation scenario, and an image-bitmap test.
- Second pass: burst runner rejects mix totals above 10,000 (weights near `int.MaxValue` overflowed the stride search and gave a stride of zero); the stride search uses 64-bit arithmetic and the report lists `mixTotalWeight` and `mixStride`. `PdfImageObject.SetBitmap` and `SetImage` are `internal`.
- 237 tests pass on win-x64 and linux-x64.

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

- .NET 10 upgrade on `feature/net10-upgrade`: done for win-x64 and linux-x64, uncommitted. macOS natives not rebuilt.

- Follow-up review is finished. The two remaining findings await implementation by the owner; no code corrections were requested in this verification turn.

- PR #18 review finished; implementation corrections have not been requested. Older status notes below are historical.

- Nothing active. Verification is complete; the stride overflow edge case awaits correction. The four original review reproductions are resolved.

## Next recommended step

- On a Mac: `bash src/native/build-natives.sh --target osx-arm64` and `--target osx-x64` (versions pinned in the script), run the tests on osx-arm64, check the osx-x64 dylibs with `file`/`otool -L`. Then commit the upgrade on `feature/net10-upgrade`, open a PR and confirm CI on the .NET 10 SDK. Bump the package version (2.0.0 has not been released) if the TFM change should be called out.

- Correct artifact ownership in `RemovePartialOutput` (`PdfProcessingPool.cs:568`) so undispatched/rejected jobs cannot delete existing files, and remove completed task history from `Batch` (`PdfProcessingPool.Operations.cs:184`). Add preservation and task-retention regressions, rerun tests, then commit/push the fixes and verify CI for that exact commit. Earlier recommendations below are historical.

- Address the five PR #18 findings at `PdfProcessingPool.Operations.cs:152/181`, `PdfProcessingPool.cs:141`, `PdfWorkerHost.cs:249`, and `Worker.cs:74` / `PdfProcessingPool.cs:574`; add regression coverage for each, then rerun the suite and request a follow-up review. Prior recommendations below are retained as history.

0. Commit the current working tree on `feature/tiff-gray-render` (gray TIFF rendering, test, docs, `benchmark.db`, `.gitignore`, the comparison harness without results, the plan revision), open a PR, merge. Then start Release 2 on a new branch from `main`, Phase 5 in its listed order.

Earlier list (items 1 and 2 still apply; item 3 is done):

0. Correct the stride-search bound at `BurstRunner.cs:406` for large accepted weight totals (widen arithmetic or validate/reject the boundary), add a regression asserting a positive coprime stride, and run the relevant benchmark tests. The original four regressions and full suite already pass; exact-commit CI is green.
1. Project owner supplies `N` (documents per burst), `T` (window), the real document mix, and whether the consuming service can run several replicas behind its queue. With those, apply rule R13: if replicas are possible, size them from `benchmark.md` and stop; if not, build the process pool (plan Phases 5 to 7).
2. Run the test suite on macOS (osx-x64, osx-arm64); it has only been run on win-x64 and linux-x64.
3. Decide whether to commit `src/PdfiumWrapper.Benchmarks.Comparison` (code only, no results), the `.gitignore` entries and the updated `benchmark.db`. 2.0.0 is merged but not released: the release workflow takes its version from a `release/<version>` branch.

## Blockers or open questions

- macOS natives are still PDFium 7869 / libtiff 4.7.1 / libjpeg-turbo 3.1.4.1 / zlib-ng 2.2.4 / libpng 1.6.56 until rebuilt on a Mac; a release from this branch before that would ship mixed versions.
- Dropping `net8.0` means .NET 8 and 9 consumers can no longer use the package (both reach end of support on 2026-11-10). Multi-targeting `net8.0;net10.0` is the alternative if that matters.

- The local PR #18 fixes still have one Critical output-loss regression and one Moderate batch-memory issue. GitHub CI has not evaluated these uncommitted changes. The review itself is complete.

- PR #18 should not merge until the reproduced output-loss, batch-admission, cleanup, and cancellation defects are corrected. The review itself has no remaining blocker.

- Original four PR #15 findings are resolved at `f3b15a1`; a new large-weight stride-search overflow remains (`BurstRunner.cs:406`). No implementation fixes were requested during verification.
- The Release 2 decision is open for the reason above. The measurements say a burst of thousands of documents in a short window is beyond one process on the test hardware (usable rate about 1.3 docs/sec per process on the mixed corpus), so some multi-process arrangement is needed; which one is the owner's call.
- macOS not verified.
- Cold start with a document first is about 5 ms slower than before: initialization now loads libtiff to install its error handlers. Moving that to the first TIFF write would recover it.
- Merge benchmark moved between -5.9% and +4.4% by document (buffered save). Peak memory during a save now includes the whole output.
- `benchmark.db` was not updated with the new runs; the tables are in `benchmark.md` only.
- The starvation test bound (heartbeat p99 under 100 ms) is loose against the measured 1.3 ms.
- `PdfImageObject.SetBitmap(IntPtr, IntPtr)` and `SetImage(byte[], IntPtr)` are now `internal` (they take native handles public callers cannot obtain); `PdfPage.AddImage` is the public path. The static `Create(IntPtr documentHandle, ...)` factories on `PdfImageObject`, `PdfTextObject` and `PdfPathObject` are still public and have the same limitation; not changed.
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

- Warning cleanup (2026-10-03): `src/PdfiumWrapper/PdfAttachments.cs`, `PdfForm.cs`, `PdfMetadata.cs`, `PDFium.Metadata.cs`; `src/PdfiumWrapper.Tests/PdfDocumentTests.cs`, `PdfFormTests.cs`, `PdfMergerTests.cs`, `LifetimeCoordinationTests.cs`, `Concurrency/PdfiumConcurrencyTests.cs`; `docs/API-REFERENCE.md`.
- .NET 10 upgrade (2026-10-03): all seven `*.csproj`; `.github/workflows/pr-build.yml`, `release.yml`; `src/native/build-natives.cmd`, `build-natives.sh`; `src/libs/win-x64/*` (5), `src/libs/linux-x64/*` (4; `libtiff_shim.so` unchanged); `README.md`, `AGENTS.md`, `docs/BUILDING-NATIVE-LIBS.md`, `docs/TROUBLESHOOTING.md`, `ai/current-state.md`.

- Follow-up verification (2026-10-03): `ai/current-state.md` only. All pre-existing code and documentation edits were preserved. Temporary xUnit probes are under `%TEMP%/PdfiumPr18Followup_f8bfa9bdd893413980dd7dcdc936ed1f/`.

- PR #18 review (2026-10-02): `ai/current-state.md` only. Preserved the pre-existing local change to `docs/HIGH-THROUGHPUT-PROCESSING.md` and earlier state notes. Temporary reproduction tests were outside the repository.

- Review-fix session (2026-10-02): `src/PdfiumWrapper.Processing/PdfProcessingPool.cs`, `PdfProcessingPool.Operations.cs`, `PdfWorkerHost.cs`, `Worker.cs`, `PendingJob.cs`; `src/PdfiumWrapper.Tests.Host/WorkerFaults.cs`; `src/PdfiumWrapper.Tests/Processing/PdfProcessingPoolTests.cs`; `AGENTS.md`, `ai/plans/plan-pdfium-concurrency.md`, `ai/current-state.md`.
- Documentation session (2026-10-02): `docs/HIGH-THROUGHPUT-PROCESSING.md` (new section), `ai/current-state.md`.
- Current verification session: `ai/current-state.md` only. Other working-tree edits were preserved.
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
