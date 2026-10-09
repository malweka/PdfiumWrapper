# PDFium Concurrency and Burst Throughput Plan

Status: Release 1 (Phases 0 to 4 and 8) and Release 2 (the worker pool, `PdfiumWrapper.Processing`) implemented and measured on 2026-10-02. The 10,000-job qualification, 30-minute soak and self-contained publish passed on 2026-10-08 (`plan-pool-qualification.md`). Open: macOS beyond a smoke run, and isolating the coordinator's own CPU share. See "Implementation record" below for what was done in Release 1, where the code departs from the reference design in section 4, and the measured results.

This document is written so that an agent can implement it without further design decisions. Section 3 contains the binding design rules. Section 4 contains reference code for every new component. Phases 0 to 8 are the ordered checklist. Where this document and existing code disagree, this document wins; where this document is silent, follow existing patterns in `AGENTS.md`.

## Implementation record (2026-10-02)

### Departures from the reference design in section 4

Section 4 is kept as written for history. The shipped code differs in these places, each for a defect found while checking the reference code against the real code or while testing:

| Area | Reference design | Shipped | Why |
|---|---|---|---|
| Async admission (4.1) | `EnterAsync` is an `async` method that records the owner thread after `await WaitAsync` | `EnterAsync` returns a custom awaitable; ownership is claimed in the awaiter's `GetResult()` | `GetResult()` runs on the thread that resumes the caller. The async-method version records the thread that completed the wait; if the task completes before the caller attaches its continuation, the caller resumes on a different thread, a nested `Enter()` self-deadlocks, and the recorded thread can later reenter without holding the semaphore. |
| Async admission and `SynchronizationContext` | not addressed | the awaiter never resumes on a captured context, and `PdfDocument`'s internal awaits use `ConfigureAwait(false)` | The semaphore is handed to an async waiter before its continuation runs. Posted to a UI thread that is blocked in a synchronous `Enter()`, it would never run. Covered by `AsyncAdmission_DoesNotResumeOnTheCallersSynchronizationContext`. |
| Drain (4.1) | walk kinds 0 to 4, dequeue until each queue is empty | snapshot queue counts in reverse kind order, drain only those counts | The finalizer thread enqueues while the drain runs. With live queues, a document enqueued during the drain is closed before its pages, which arrived after the page queue was passed. With the batched `finalizer-drain` scenario the live-queue drain aborted the process with an access violation in 5 of 5 runs; the snapshot drain passed 8 of 8. |
| Deferred release kinds (4.1, 4.4) | five kinds | seven: `PinnedBuffer` and `NativeMemory` added, plus a temp-file queue | The finalizer in 4.4 unpinned the document's source bytes immediately although `FPDF_CloseDocument` runs later in the drain. Source bytes, form-info memory and spool files are now released after the document closes. |
| Forms (4.4) | one `_form` field | the document tracks a set of forms; `FPDF_FORMFILLINFO` lives in native memory | `GetForm()` returns a new caller-owned form per call. PDFium keeps a pointer to the info structure; the old code passed `ref` to a field of a movable managed object and pinned only a boxed copy. |
| Detached page objects | finalizer enqueues the handle | the document also tracks objects removed from its pages | So they are destroyed before the document closes, by `Dispose` or by the document's finalizer. |
| Spooling (4.5) | `using var spool` in the constructor; temp file deleted in the finalizer | the document owns the spool file; deleted after close, or queued for deletion by the finalizer; deletion never throws | PDFium opens files without `FILE_SHARE_DELETE` on Windows, so the file cannot be deleted while the document is open, and a throwing delete on the finalizer thread would end the process. |
| `SpooledInput` (4.5) | zero-copy only when the `MemoryStream` segment covers the whole array; position ignored | honors segment offset and stream position, advances the stream to its end, buffers non-seekable streams up to the threshold before spilling | Matches what the constructors did before. |
| Save API (4.5) | `Save(Stream, SaveFlags)`, `SaveAsync(Stream)` | existing `Save(string, uint)`, `SaveToStream(Stream, uint)`, `PdfMerger.Save`/`ToBytes` kept | The names in 4.5 did not exist. The synchronous API is preserved. |
| Operation pattern (4.3) | argument validation, then `Enter()`, then disposed check | `Enter()`, disposed check, then argument validation | Existing tests require `ObjectDisposedException` to win over `ArgumentNullException` on a disposed object. |
| Bootstrap lock (4.1) | an object stored in `AppContext` | `lock (AppDomain.CurrentDomain)` | Two load contexts racing on first use could each read back a different lock object. |
| `Scope` (4.1) | unused `_outer` field | `_active` flag; `default(Scope).Dispose()` is a no-op | |
| `Exit()` (4.1) | `Debug.Assert` on the owner | throws `InvalidOperationException` in all builds | A scope disposed on the wrong thread would silently corrupt the gate. |
| Gate wait detection | elapsed time greater than zero | `Wait(0)` first, then a blocking wait | Exact count of acquisitions that had to wait. |
| Diagnostics (4.7) | counters and events | also records each deferred release (kind, handle), wait and hold tick totals | Needed to check release order per object graph and to report wait/hold shares. |
| Phase 0 `init-race` test | asserts exit 0 before the gate exists | the scenario was added in Phase 0; the assertion became a test in Phase 3 | Before the gate the scenario aborts with an access violation (8 of 8 runs), which is the defect being fixed. |
| Phase 1 tag | tag, then add the small-document benchmark | benchmark classes and runner added first, then the tag | So the tag can rerun every baseline measurement. |
| `ConcurrentCallersBenchmark` (Phase 4) | caller `i` converts `_inputs[i % n]` to `Stream.Null` | every caller converts the whole corpus; TIFF goes to a `MemoryStream` | Equal work per caller makes pages/sec comparable across caller counts; libtiff needs a seekable stream. |
| Shutdown tests | in the test project | host scenario `shutdown` | Handles leaked by other tests make `LiveHandleCount == 0` unreliable in the shared test process. |

Also fixed, found by the gate-coverage test: `FPDFPageObj_GetMatrix` was declared with six `out double` parameters; the native function takes one `FS_MATRIX*`. `PdfPageObject.GetMatrix()` corrupted the stack.

### Results and decision

Full tables are in `benchmark.md` ("Concurrency Benchmarks"). Machine: Intel Core i7-13700F, 24 logical processors, 31.7 GB, NVMe SSD, Windows 11, .NET runtime 8.0.31, PDFium 150.0.7869.0.

Correctness:

- 219 tests pass on win-x64 and on linux-x64 (.NET 8 SDK container). macOS was not run; it is an open platform check.
- Before the gate, the `init-race` scenario aborted with an access violation in 8 of 8 runs and a merger-first cold start in 3 of 3. Both pass now, with one native initialization.
- Concurrent callers (2, 4, 8, 16 threads; async tasks) match a sequential oracle page for page; the independent detector saw at most one native call in flight, always inside a gate hold.
- Crash probe: 25 damaged inputs on each platform, none aborted the process.

Cost of the gate:

- One uncontended entry and exit: 28 ns.
- One-page load/count/close: median +0.3%, P95 +0.6%. Load/render/close: median +0.5%, P95 +0.4%. Limits were 5% and 10%.
- Existing suite: conversion within 1.1%; merge between -5.9% and +4.4% (buffered save).
- Cold start, document first: about 5 ms slower (libtiff is loaded at initialization).
- Sequential batch of 200 jobs: 154.3 s gated, 153.5 s before.

Capacity:

- One process, mixed corpus at 200 DPI: 1.30 docs/sec with one caller, 1.62 docs/sec (20.1 pages/sec) from 4 callers up to 24. The gate is held 99.8% of the time at 8 callers. In-process parallelism is worth 1.25x on this mix (1.11x TIFF, 1.09x JPEG, 1.45x PNG).
- Async admission keeps the thread pool free: heartbeat p99 1.3 ms against 2.5 s when the synchronous API is called from pool threads (192 concurrent conversions, pool pinned to 24 threads). Throughput is the same.
- Stream type does not change gate hold time. Abandoning 5% of documents to the finalizer costs nothing measurable.
- Several processes: 3.13 docs/sec with 2, 5.75 with 4, 9.05 with 8, 11.07 with 16.

Decision gate (R13): `max_W R_inproc(W) = 1.62` docs/sec, so the usable in-process rate is `0.8 x 1.62 = 1.30` docs/sec. The rule builds the pool only if that is below `N / T` and the consumer cannot add replicas, or the consumer needs isolation from native aborts. `N`, `T` and the replica question are still open inputs, and the crash-probe table is empty. The pool was not built. The measurements do say that a burst of thousands of documents in a short window is beyond one process on this hardware and needs several processes; replicas of the consumer's service are the first route (plan 3.3), the pool the second.

Review follow-up (pull request 15, 2026-10-02). Four findings, all confirmed and fixed:

- Burst runner deleted whatever directory `--out` named, even with `--keep-output true`. Each run now writes into its own new child directory and removes only that.
- Burst runner's mix used a fixed stride of 37 and collapsed to one format when the weights summed to a multiple of 37 (`png:18,jpeg:19` gave 74 PNG and no JPEG). The stride is now chosen coprime with the total. The recorded runs used a total of 100, for which the job sequence is unchanged.
- `PdfImageObject.GetBitmap()` and `GetRenderedBitmap()` returned a caller-owned native bitmap that callers could no longer destroy once the raw imports became internal. They now return managed BGRA pixels (`RawBitmap?`); the native bitmap is leased, counted, copied outside the gate and destroyed.
- `StreamImageBytesAsync` and `StreamJpegBytesAsync` called the synchronous page-count check and so blocked on the gate. The factory now does managed validation only; the page count is awaited when enumeration starts. The starvation scenario gained a `stream` mode.

A second review pass found that weights near `int.MaxValue` overflowed the stride search and produced a stride of zero. Mix totals above 10,000 are now rejected as an argument error, the search uses 64-bit arithmetic, and the stride (coprime with the total, close to 0.37 of it, 37 for a total of 100) is reported. `PdfImageObject.SetBitmap` and `SetImage` were made `internal` at the owner's request: they take native handles that public callers cannot obtain.

237 tests pass on win-x64 and linux-x64 after these changes.

Left open:

- `N`, `T`, the real document mix, and whether the consumer can run replicas. These decide Phases 5 to 7.
- macOS test run.
- The starvation bound (heartbeat p99 under 100 ms) is far above the measured 1.3 ms and could be tightened.
- `benchmark.db` was not updated; the tables are in `benchmark.md` only.

---

## 1. Objective

Process thousands of documents within a short, explicit completion window, with correct output, bounded memory, and recovery from native failures. Measure completion of the entire burst including queueing and output writes. A fast single-document benchmark is insufficient.

Ship correctness first as its own release (Release 1). Obtain parallelism second (Release 2), and only in the form measurements justify: in-process overlap of gated rendering with ungated encoding, replica scale-out of the consumer's service, or an opt-in process pool.

## 2. Evidence and constraints

- [x] Audit wrapper/documentation threading claims and inspect upstream native source.
- Bundled Windows DLL is PDFium `150.0.7869.0`, V8/XFA disabled. Upstream checkout `_native_build/pdfium-source-7869`, commit `80fccd7553e5cff9cea6549bc0db2ea93ea6cb2e`.
- PDFium shares mutable font caches, font faces, character maps, and non-atomic reference counts across documents. Its [public contract](https://pdfium.googlesource.com/pdfium/+/refs/heads/main/public/fpdfview.h) requires at most one native call in flight per process, across all documents.
- The wrapper has 192 `LibraryImport` declarations across six `PDFium.*.cs` partials, all `public`. There is no shared gate. Four finalizers call PDFium directly: `PdfDocument.cs:1340`, `PdfPage.cs:467`, `PdfMerger.cs:456`, `PdfPageObject.cs:141`.
- Initialization (`FPDF_InitLibrary`) runs only in the `PdfDocument` static constructor (`PdfDocument.cs:27`). `PdfMerger` constructors call PDFium without it.
- `NativeLibraryResolver.EnsureRegistered()` (`NativeLibraryResolver.cs:15`) uses `Interlocked.CompareExchange`; a thread that loses the race can reach a P/Invoke before `SetDllImportResolver` completes.
- `StreamDocumentLoader` (`PdfHelpers.cs:40`) reads from the caller's stream lazily during later page operations. `PdfStreamFileWriter` (`PdfHelpers.cs:170`) writes to the caller's stream synchronously inside `FPDF_SaveAsCopy`.
- `PdfForm` sets no callbacks in `FPDF_FORMFILLINFO` (version 2, no function pointers). There is no PDFium-to-managed callback other than the two stream callbacks above.
- `docs/HIGH-THROUGHPUT-PROCESSING.md:272` to `:300` recommends `Parallel.ForEachAsync` with `MaxDegreeOfParallelism = Environment.ProcessorCount` across documents. This is the unsafe pattern.
- `PdfPageDeletionExample` (`src/PdfiumWrapper.Tests/PdfPageDeletionExample.cs:8`) has no `[Collection("PDF Tests")]`, so xUnit may run it in parallel with the PDF collection today.
- Encoders are already independent of PDFium: `TiffWriter`/`PixelConverter` read a native BGRA pointer, `JpegEncoder` wraps a per-instance turbojpeg handle, `PngEncoder` is stateless. libtiff's global error handlers are installed once (`TiffWriter.cs:221`).
- Preserve the synchronous API and resource-ownership rules. Different documents may be used concurrently after the fix; concurrent use of one document, page, form, or merger remains unsupported.
- Preserve the benchmark shape in `plan-performance-benchmark.md`. Add new benchmark classes; do not change `PdfMergeBenchmark.MergeTwoDocuments()` or the other existing benchmarks.

## 3. Binding design rules

### 3.1 Release 1: correctness (Phases 0 to 4)

| # | Rule | Reason |
|---|------|--------|
| R1 | One process-wide gate, entered once per public operation. Every public method on `PdfDocument`, `PdfPage`, `PdfForm`, `PdfMerger`, `PdfMetadata`, `PdfBookmarks`, `PdfAttachments`, `PdfAttachment`, `PdfBookmark`, and every `PdfPageObject` subtype that touches PDFium does `using var _ = PdfiumRuntime.Enter();` as its first statement after argument validation. The disposed check, every native call, and `FPDF_GetLastError` happen inside that scope. | Per-call gating does not protect multi-call sequences and gives wrong `GetLastError`. |
| R2 | Gate primitive is `SemaphoreSlim(1,1)` plus an owner-thread id and depth counter for reentrancy. Sync code uses `Enter()`. Async public methods use `await PdfiumRuntime.EnterAsync(ct)` and then run their native batch synchronously to completion. | Async waiters do not block thread-pool threads; one primitive serves both paths. |
| R3 | A gated scope is never held across `await`, `yield return`, or a call into a user delegate. | `SemaphoreSlim` ownership is tracked by thread; continuations change threads; user code may block or call back. |
| R4 | The gate object, the init flag, the pending-release queues, and the diagnostic counters live in `AppContext` data under fixed string keys and are BCL types only. | Two copies of the assembly in different `AssemblyLoadContext`s must share one gate over the single loaded `pdfium` module. |
| R5 | Finalizers never call PDFium. They enqueue `(kind, handle)` onto the pending-release queues. Drain happens at the start of every outermost gated scope, in kind order: page objects, text pages, forms, pages, documents. | Finalizer thread never waits on the gate; no lock-order problem; dependency order preserved. |
| R6 | A `PdfDocument` finalizer enqueues its live pages' handles (and its form handle, if any) before its own, and zeroes those handles in the page objects. A `PdfPage` finalizer enqueues only its own handle if still non-zero. | Both finalizers run on the one finalizer thread, so this is race-free, and a page can never be closed after its document. |
| R7 | No user I/O inside the gate. `PdfDocument(Stream)` and `PdfMerger(Stream)` spool the stream fully before `Enter()`. Stream saves write to a pooled buffer inside the gate and copy to the caller's stream after exit. `StreamDocumentLoader` lazy reads are removed. | A slow or network stream must not stall the whole process. |
| R8 | Render inside the gate, encode and write outside it. `RenderToBitmapLease` returns a `BitmapLease` (handle, buffer pointer, stride, size). Encoders consume the lease with no gate held. `BitmapLease.Dispose()` reenters the gate to destroy the bitmap. | PDFium does not touch a bitmap after rendering returns. Concurrent callers overlap one caller's render with others' encoding. |
| R9 | The 192 `LibraryImport` declarations become `internal`. Major version bump. Public raw access, if ever needed, goes through `PdfiumRuntime.Enter()` plus a future `PdfiumWrapper.Interop` namespace that asserts the gate is held. | Public ungated imports are an unsafe bypass. **Approved by the project owner on 2026-09-30.** Any PDFium function a downstream consumer needs is wrapped properly in 2.0 rather than exposed raw. |
| R10 | `PdfiumRuntime` owns initialization. Resolver registration is per assembly copy via a static constructor. `FPDF_InitLibrary` runs once per process under the gate guarded by the shared init flag. `FPDF_DestroyLibrary` is only reachable through `PdfiumRuntime.Shutdown()`, which throws if live handles or pending releases exist. | No first-caller race; no destruction under live objects. |
| R11 | Lock order: gate outermost; the existing per-object locks (`_pagesLock`, `_attachedObjectsLock`, `_disposeLock`, `_streamLock`) only inside a gated scope; finalizers take none of them. | Deadlock freedom by construction. |
| R12 | Diagnostics are off unless `AppContext` switch `PdfiumWrapper.Diagnostics` is `true`. When on, the runtime records gate wait/hold intervals and the wrapper records native intervals around the hot call sites listed in 4.7. | Tests and instrumented benchmarks need evidence; production runs must not pay for it. |

### 3.2 Release 2: parallelism (Phases 5 to 8, conditional)

| # | Rule | Reason |
|---|------|--------|
| R13 | ~~The process pool is built only if the Phase 4 decision gate says so.~~ **Superseded 2026-10-02:** the owner decided to build the pool after the 1,000-request measurement showed one process at 2.2 requests/s against 10.9 for 8 processes, and because the developer otherwise has to write the orchestration (bounded loops, replicas or a hand-built process pool) themselves. | A single-process application has no other way past the per-process ceiling. |
| R14 | Crash isolation is a justification of its own. PDFium can abort on malformed input; in one process that kills the service. In the pool it costs one job attempt. | Holds even when parallelism adds nothing. |
| R15 | The pool ships as one opt-in package, `PdfiumWrapper.Processing`. The core package never launches processes. The worker is not a deployable of its own: by default the consumer's executable is re-launched as the worker (`PdfWorkerHost.TryRun()` first in `Main`); a dedicated worker executable is optional (`WorkerPath`). | Small consumers keep the simple API; no per-RID worker to publish, and the natives are already in the consumer's output. |
| R16 | Worker count is dynamic between `MinWorkers` and `MaxWorkers`: up by one when all workers are busy and jobs wait longer than `ScaleUpAfter` (500 ms); down by one when a worker idles longer than `IdleTimeout` (60 s). `MinWorkers` are pre-warmed. `MaxWorkers` defaults to half the logical processors. Workers are replaced on crash, timeout or a memory limit, never recycled on a schedule. | Scale up fast, down slowly, no thrash; a worker is one core, so past the fast cores more workers add memory and little throughput; scheduled recycling hides leaks. |
| R17 | Only file paths, options and small results cross the process boundary, as length-prefixed JSON frames. Never pixels, handles, delegates or shell commands. Large text results go through a temp file. | Keeps IPC cheap and the attack surface small. |
| R18 | A job failure never fails the pool. Crash, hang, malformed frame or memory limit: the worker is killed or retired and replaced; the job is reported with a status and retried up to `MaxAttempts`; other jobs are unaffected. `SubmitAsync` throws only for argument errors and after disposal. | The pool exists to contain native failures. |

### 3.3 Approach table

| Approach | Role |
|---|---|
| Operation-level gate in one process | Required. Ships alone in Release 1. |
| Gated render plus ungated encode/output | Release 1. First source of in-process parallel capacity. |
| Replica scale-out of the consumer's service | First recommendation for consumers with an existing queue. Needs only the published single-lane capacity. |
| Persistent worker pool, one job per worker, dynamic size (PdfiumWrapper.Processing) | Release 2, approved 2026-10-02. Section 4.9 and Phases 5 to 7. |
| Single dedicated native thread | Fallback if the semaphore gate measures badly in Phase 4. |
| Separate DLL copies or load contexts | Excluded. Native state isolation is not established. R4 applies instead. |
| Fork PDFium for internal thread safety | Excluded. |

No production option disables synchronization. No per-document lock replaces the process-wide gate. No unbounded task-per-document dispatch is introduced.

---

## 4. Reference implementation

Code below is the target shape. Implementers may rename private members but must keep public names, the `AppContext` keys, the kind ordering, and the behavior described in comments.

### 4.1 `src/PdfiumWrapper/PdfiumRuntime.cs`

```csharp
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace PdfiumWrapper;

/// <summary>
/// Process-wide coordination for the native PDFium library. PDFium permits one native call
/// in flight per process. Every wrapper operation enters this gate once; nested wrapper calls reenter.
/// </summary>
public static class PdfiumRuntime
{
    // AppContext keys. Values are BCL types only so that copies of this assembly in different
    // AssemblyLoadContexts share the same instances (R4).
    private const string GateKey      = "PdfiumWrapper.NativeGate";       // SemaphoreSlim
    private const string OwnerKey     = "PdfiumWrapper.GateOwner";        // int[1]  managed thread id of holder, 0 = none
    private const string DepthKey     = "PdfiumWrapper.GateDepth";        // int[1]  reentrancy depth, touched only by holder
    private const string InitKey      = "PdfiumWrapper.NativeInitialized";// int[1]  0/1
    private const string PendingKey   = "PdfiumWrapper.PendingReleases";  // ConcurrentQueue<nint>[5], index = NativeHandleKind
    private const string LiveKey      = "PdfiumWrapper.LiveHandles";      // long[1] open native handles (diagnostic + Shutdown guard)

    private static readonly SemaphoreSlim s_gate   = Shared(GateKey,    static () => new SemaphoreSlim(1, 1));
    private static readonly int[] s_owner          = Shared(OwnerKey,   static () => new int[1]);
    private static readonly int[] s_depth          = Shared(DepthKey,   static () => new int[1]);
    private static readonly int[] s_initialized    = Shared(InitKey,    static () => new int[1]);
    private static readonly long[] s_live          = Shared(LiveKey,    static () => new long[1]);
    private static readonly ConcurrentQueue<nint>[] s_pending =
        Shared(PendingKey, static () => Enumerable.Range(0, 5).Select(_ => new ConcurrentQueue<nint>()).ToArray());

    static PdfiumRuntime()
    {
        // Per-assembly-copy: each copy must register its own DllImportResolver (R10).
        RuntimeHelpers.RunClassConstructor(typeof(NativeLibraryResolver).TypeHandle);
    }

    private static T Shared<T>(string key, Func<T> create) where T : class
    {
        // AppContext.SetData/GetData is thread-safe. Create-then-publish with a global lock on the
        // AppContext key string is not possible (strings may not be interned across ALCs), so use
        // a BCL object stored under a bootstrap key as the lock.
        lock (BootstrapLock)
        {
            if (AppContext.GetData(key) is T existing) return existing;
            var created = create();
            AppContext.SetData(key, created);
            return created;
        }
    }

    private static object BootstrapLock
    {
        get
        {
            // The very first access races between ALC copies; AppContext.SetData is atomic per key,
            // so losing copies read back the winner's object on the next GetData.
            if (AppContext.GetData("PdfiumWrapper.Bootstrap") is object o) return o;
            var candidate = new object();
            AppContext.SetData("PdfiumWrapper.Bootstrap", candidate);
            return AppContext.GetData("PdfiumWrapper.Bootstrap")!;
        }
    }

    /// <summary>True when the current thread holds the gate. Used by debug assertions and Interop guards.</summary>
    public static bool IsHeldByCurrentThread => Volatile.Read(ref s_owner[0]) == Environment.CurrentManagedThreadId;

    /// <summary>
    /// Enter the native gate synchronously. Reentrant on the same thread. Drains pending releases
    /// and performs one-time library initialization on the outermost entry.
    /// Dispose the returned scope to exit. Never hold it across await/yield/user delegates (R3).
    /// </summary>
    public static Scope Enter()
    {
        int me = Environment.CurrentManagedThreadId;
        if (Volatile.Read(ref s_owner[0]) == me)
        {
            s_depth[0]++;
            return new Scope(outer: false);
        }

        long waitStart = PdfiumDiagnostics.Enabled ? Stopwatch.GetTimestamp() : 0;
        s_gate.Wait();
        OnAcquired(me, waitStart);
        return new Scope(outer: true);
    }

    /// <summary>
    /// Async admission for async public methods. The returned scope must be used synchronously
    /// (no await while it is alive) and disposed on the same thread that continued from the await.
    /// </summary>
    internal static async ValueTask<Scope> EnterAsync(CancellationToken cancellationToken = default)
    {
        int me = Environment.CurrentManagedThreadId;
        if (Volatile.Read(ref s_owner[0]) == me)
        {
            s_depth[0]++;
            return new Scope(outer: false);
        }

        long waitStart = PdfiumDiagnostics.Enabled ? Stopwatch.GetTimestamp() : 0;
        await s_gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        OnAcquired(Environment.CurrentManagedThreadId, waitStart); // thread after resumption
        return new Scope(outer: true);
    }

    private static void OnAcquired(int threadId, long waitStart)
    {
        Volatile.Write(ref s_owner[0], threadId);
        s_depth[0] = 1;
        if (PdfiumDiagnostics.Enabled) PdfiumDiagnostics.RecordGateAcquired(threadId, waitStart);

        if (s_initialized[0] == 0)
        {
            PDFium.FPDF_InitLibrary();          // idempotent in PDFium, but we still run it once
            TiffWriter.EnsureHandlersInstalled();// moved here from first TIFF use
            s_initialized[0] = 1;
            if (PdfiumDiagnostics.Enabled) PdfiumDiagnostics.RecordInit();
        }

        DrainPendingReleasesCore();
    }

    private static void Exit()
    {
        Debug.Assert(IsHeldByCurrentThread, "Scope disposed on a thread that does not hold the gate.");
        if (--s_depth[0] == 0)
        {
            if (PdfiumDiagnostics.Enabled) PdfiumDiagnostics.RecordGateReleased(Environment.CurrentManagedThreadId);
            Volatile.Write(ref s_owner[0], 0);
            s_gate.Release();
        }
    }

    /// <summary>Scope token. Struct to avoid an allocation per operation. Dispose exactly once.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly bool _outer;
        internal Scope(bool outer) => _outer = outer;
        public void Dispose() => Exit();
    }

    // ---- Pending releases (R5, R6) ----

    internal static void EnqueueRelease(NativeHandleKind kind, nint handle)
    {
        if (handle == 0) return;
        s_pending[(int)kind].Enqueue(handle);
        if (PdfiumDiagnostics.Enabled) PdfiumDiagnostics.RecordEnqueued(kind);
    }

    /// <summary>Close native handles abandoned by finalized wrapper objects. Also runs automatically on every outermost Enter().</summary>
    public static void ReleasePending()
    {
        using var _ = Enter(); // OnAcquired drains
    }

    internal static int PendingCount => s_pending.Sum(q => q.Count);

    private static void DrainPendingReleasesCore()
    {
        // Kind order is dependency order: PageObject(0) < TextPage(1) < Form(2) < Page(3) < Document(4).
        for (int kind = 0; kind < s_pending.Length; kind++)
        {
            while (s_pending[kind].TryDequeue(out var h))
            {
                switch ((NativeHandleKind)kind)
                {
                    case NativeHandleKind.PageObject: PDFium.FPDFPageObj_Destroy(h); break;
                    case NativeHandleKind.TextPage:   PDFium.FPDFText_ClosePage(h); break;
                    case NativeHandleKind.Form:       PDFium.FPDFDOC_ExitFormFillEnvironment(h); break;
                    case NativeHandleKind.Page:       PDFium.FPDF_ClosePage(h); break;
                    case NativeHandleKind.Document:   PDFium.FPDF_CloseDocument(h); break;
                }
                HandleClosed();
                if (PdfiumDiagnostics.Enabled) PdfiumDiagnostics.RecordDrained((NativeHandleKind)kind);
            }
        }
    }

    // ---- Live handle accounting (R10, diagnostics) ----
    internal static void HandleOpened() => Interlocked.Increment(ref s_live[0]);
    internal static void HandleClosed() => Interlocked.Decrement(ref s_live[0]);
    public static long LiveHandleCount => Interlocked.Read(ref s_live[0]);

    /// <summary>
    /// Destroy the native library. Throws if any wrapper object is still alive or releases are pending.
    /// Intended for tests and controlled host shutdown only. After this call the runtime re-initializes on next Enter().
    /// </summary>
    public static void Shutdown()
    {
        using var _ = Enter();
        if (LiveHandleCount != 0 || PendingCount != 0)
            throw new InvalidOperationException($"Cannot shut down PDFium: {LiveHandleCount} live handles, {PendingCount} pending releases.");
        PDFium.FPDF_DestroyLibrary();
        s_initialized[0] = 0;
    }
}

/// <summary>Order is dependency order for deferred release. Do not reorder.</summary>
internal enum NativeHandleKind { PageObject = 0, TextPage = 1, Form = 2, Page = 3, Document = 4 }
```

Notes for the implementer:

- `Enter()` on the fast path is one volatile read, one `SemaphoreSlim.Wait()` (lock-free when uncontended), two writes. Phase 4 verifies this against the baseline.
- `OnAcquired` runs the drain while holding the gate, so drained closes are serialized with everything else.
- `TiffWriter.EnsureHandlersInstalled()` replaces the compare-exchange at `TiffWriter.cs:221`; it is now only ever called under the gate.
- Every place that obtains a native handle (`FPDF_LoadDocument`, `FPDF_CreateNewDocument`, `FPDF_LoadPage`, `FPDFPage_New`, `FPDFText_LoadPage`, `FPDFDOC_InitFormFillEnvironment`, page-object creation) calls `HandleOpened()`; every close calls `HandleClosed()`.

### 4.2 `NativeLibraryResolver` barrier (Phase 0)

Replace the compare-exchange with a static constructor. Callers keep calling `EnsureRegistered()`; it now only forces the type initializer.

```csharp
internal static class NativeLibraryResolver
{
    static NativeLibraryResolver()
    {
        // CLR guarantees no thread observes the type before this completes.
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
    }

    internal static void EnsureRegistered()
        => RuntimeHelpers.RunClassConstructor(typeof(NativeLibraryResolver).TypeHandle);

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        /* existing body unchanged */
    }
}
```

### 4.3 Operation pattern in wrapper types (R1, R3)

Sync operation:

```csharp
public int GetPageCount()   // example shape; apply to every public PDFium-touching member
{
    using var _ = PdfiumRuntime.Enter();
    ThrowIfDisposed();                                  // inside the gate: disposal is also gated
    return PDFium.FPDF_GetPageCount(Document);
}
```

Load with error retrieval in the same scope:

```csharp
public PdfDocument(string filePath, string? password = null)
{
    ArgumentException.ThrowIfNullOrEmpty(filePath);
    using var _ = PdfiumRuntime.Enter();
    Document = PDFium.FPDF_LoadDocument(filePath, password);
    if (Document == IntPtr.Zero)
        throw new InvalidOperationException($"Failed to load PDF document. Error: {PDFium.FPDF_GetLastError()}");
    PdfiumRuntime.HandleOpened();
}
```

Async operation, per page, never across the await:

```csharp
public async Task SaveAsTiffAsync(Stream output, int dpiWidth, int dpiHeight, TiffColorMode colorMode, CancellationToken ct = default)
{
    ArgumentNullException.ThrowIfNull(output);
    int pageCount;
    using (await PdfiumRuntime.EnterAsync(ct).ConfigureAwait(false)) { ThrowIfDisposed(); pageCount = PageCount; }

    using var tiff = TiffWriter.Create(output, ...);            // libtiff, no gate needed
    for (int i = 0; i < pageCount; i++)
    {
        ct.ThrowIfCancellationRequested();
        BitmapLease lease;
        using (await PdfiumRuntime.EnterAsync(ct).ConfigureAwait(false))
        {
            using var page = GetPageCore(i);                      // Core variants assume the gate is held
            lease = page.RenderToBitmapLeaseCore(widthPx, heightPx, PDFium.FPDF_PRINTING | PDFium.FPDF_ANNOT);
        }
        using (lease)                                             // gate NOT held during convert/encode/write
        {
            PixelConverter.Convert(lease, colorMode, scratch);
            tiff.WriteScanlines(scratch, ...);
        }                                                         // lease.Dispose() re-enters the gate to destroy the bitmap
        await Task.Yield();
    }
}
```

Rule for `Core` methods: a method named `XxxCore` assumes `PdfiumRuntime.IsHeldByCurrentThread` and asserts it in debug builds. Public methods enter the gate and call `Core`. This avoids repeated reentry inside loops and keeps the gate boundary visible.

`ProcessAllPages` hands the page to the user delegate with no gate held; each `PdfPage` member the delegate calls enters on its own:

```csharp
public void ProcessAllPages(Action<PdfPage> action)
{
    ArgumentNullException.ThrowIfNull(action);
    int count;
    using (PdfiumRuntime.Enter()) { ThrowIfDisposed(); count = PageCount; }
    for (int i = 0; i < count; i++)
    {
        using var page = GetPage(i);   // gated internally
        action(page);                  // user code, no gate held
    }
}
```

### 4.4 Finalizers and deferred release (R5, R6)

```csharp
// PdfPage
~PdfPage()
{
    // Never touch _owner or PDFium here. The document finalizer may already have zeroed _page.
    var h = Interlocked.Exchange(ref _page, IntPtr.Zero);
    PdfiumRuntime.EnqueueRelease(NativeHandleKind.Page, h);
}

// PdfDocument
~PdfDocument()
{
    // Runs on the one finalizer thread, sequentially with page finalizers, so touching page fields is safe.
    if (_activePages is not null)
        foreach (var page in _activePages)
            PdfiumRuntime.EnqueueRelease(NativeHandleKind.Page, Interlocked.Exchange(ref page._page, IntPtr.Zero));
    if (_form is not null)
        PdfiumRuntime.EnqueueRelease(NativeHandleKind.Form, Interlocked.Exchange(ref _form._formHandle, IntPtr.Zero));
    PdfiumRuntime.EnqueueRelease(NativeHandleKind.Document, Interlocked.Exchange(ref Document, IntPtr.Zero));
    ReleasePinnedMemoryDocument();   // managed GCHandle only
}

// PdfMerger: same as PdfDocument for _document and any held source documents.
// PdfPageObject (detached only): EnqueueRelease(NativeHandleKind.PageObject, handle).
```

`Dispose(true)` paths keep calling PDFium directly, inside `using var _ = PdfiumRuntime.Enter();`, in the existing order (pages, form, document). `Dispose(bool)` must branch: `disposing == true` closes natively; `disposing == false` is the finalizer path above.

### 4.5 Stream spooling and buffered save (R7)

```csharp
internal sealed class SpooledInput : IDisposable
{
    public static long MemoryThreshold = 64L * 1024 * 1024;     // bytes; AppContext data "PdfiumWrapper.SpoolThreshold" overrides

    public byte[]? Buffer { get; private set; }     // set when spooled to memory
    public string? TempPath { get; private set; }   // set when spooled to a temp file

    public static SpooledInput From(Stream s)
    {
        // Entirely outside the gate.
        if (s is MemoryStream ms && ms.TryGetBuffer(out var seg) && seg.Offset == 0 && seg.Count == seg.Array!.Length)
            return new SpooledInput { Buffer = seg.Array };
        if (s.CanSeek && s.Length - s.Position <= MemoryThreshold)
        {
            var buf = new byte[s.Length - s.Position];
            s.ReadExactly(buf);
            return new SpooledInput { Buffer = buf };
        }
        var path = Path.Combine(Path.GetTempPath(), "pdfium-" + Guid.NewGuid().ToString("N") + ".pdf");
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.SequentialScan))
            s.CopyTo(fs);
        return new SpooledInput { TempPath = path };
    }

    public void Dispose() { if (TempPath is not null) File.Delete(TempPath); }
}
```

`PdfDocument(Stream)` and `PdfMerger(Stream)`: `using var spool = SpooledInput.From(stream);` then, inside the gate, `FPDF_LoadMemDocument` over a pinned `Buffer` (existing `_documentBytes` path) or `FPDF_LoadDocument(TempPath)`. The temp file is deleted on document dispose or finalizer. Remove `StreamDocumentLoader`.

Buffered save:

```csharp
internal sealed class PooledFileWriter : IDisposable
{
    // FPDF_FILEWRITE.WriteBlock callback appends into an ArrayPool-backed growable buffer.
    // No user stream is touched while the gate is held.
    private byte[] _buf = ArrayPool<byte>.Shared.Rent(1 << 20);
    private int _len;
    public PDFium.FPDF_FILEWRITE GetFileWriteStruct() { /* existing GCHandle/delegate plumbing */ }
    private int WriteBlock(IntPtr pThis, IntPtr data, CULong size)
    {
        int n = checked((int)size.Value);
        if (_len + n > _buf.Length) Grow(_len + n);
        unsafe { new ReadOnlySpan<byte>((void*)data, n).CopyTo(_buf.AsSpan(_len)); }
        _len += n;
        return 1;
    }
    public (byte[] Buffer, int Length) Detach() { var r = (_buf, _len); _buf = Array.Empty<byte>(); return r; }
    public void Dispose() { if (_buf.Length > 0) ArrayPool<byte>.Shared.Return(_buf); /* free GCHandles */ }
}

public void Save(Stream output, SaveFlags flags = SaveFlags.None)
{
    ArgumentNullException.ThrowIfNull(output);
    byte[] buf; int len;
    using (PdfiumRuntime.Enter())
    {
        ThrowIfDisposed();
        using var writer = new PooledFileWriter();
        var fw = writer.GetFileWriteStruct();
        if (!PDFium.FPDF_SaveAsCopy(Document, ref fw, (int)flags))
            throw new InvalidOperationException($"Failed to save PDF document. PDFium error code: {PDFium.FPDF_GetLastError()}");
        (buf, len) = writer.Detach();
    }
    try { output.Write(buf, 0, len); }
    finally { ArrayPool<byte>.Shared.Return(buf); }
}
```

Async save: identical, with `await output.WriteAsync(buf.AsMemory(0, len), ct)` outside the gate.

### 4.6 `BitmapLease` and render/encode split (R8)

```csharp
/// <summary>A rendered PDFium bitmap whose pixel buffer may be read without holding the native gate.</summary>
internal sealed class BitmapLease : IDisposable
{
    public IntPtr Handle { get; private set; }
    public IntPtr Buffer { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }

    internal BitmapLease(IntPtr handle, IntPtr buffer, int width, int height, int stride)
    { Handle = handle; Buffer = buffer; Width = width; Height = height; Stride = stride; }

    public void Dispose()
    {
        var h = Handle;
        if (h == IntPtr.Zero) return;
        Handle = IntPtr.Zero;
        using var _ = PdfiumRuntime.Enter();
        PDFium.FPDFBitmap_Destroy(h);
    }
}

// PdfPage
internal BitmapLease RenderToBitmapLease(int width, int height, int flags = 0)
{
    using var _ = PdfiumRuntime.Enter();
    return RenderToBitmapLeaseCore(width, height, flags);
}

internal BitmapLease RenderToBitmapLeaseCore(int width, int height, int flags)
{
    Debug.Assert(PdfiumRuntime.IsHeldByCurrentThread);
    ThrowIfDisposed();
    var bitmap = PDFium.FPDFBitmap_Create(width, height, 0);
    if (bitmap == IntPtr.Zero) throw new OutOfMemoryException("Failed to create bitmap");
    try
    {
        PDFium.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
        using (PdfiumDiagnostics.NativeInterval(NativeOp.Render))
            PDFium.FPDF_RenderPageBitmap(bitmap, _page, 0, 0, width, height, 0, flags);
        return new BitmapLease(bitmap, PDFium.FPDFBitmap_GetBuffer(bitmap), width, height, PDFium.FPDFBitmap_GetStride(bitmap));
    }
    catch { PDFium.FPDFBitmap_Destroy(bitmap); throw; }
}
```

Apply to: `SaveAsTiff*` (both file and stream), `SaveAsPngs`, `SaveAsJpegs`, `SaveAsImages`, `StreamImageBytes*`, `RenderPages*`, `RenderToBytes`, thumbnail paths. `RenderToBitmapHandle` is deleted. `PixelConverter`, `TiffWriter`, `JpegEncoder.Encode`, `PngEncoder.Encode` get overloads taking `(IntPtr buffer, int stride, int width, int height)` if they do not already.

### 4.7 `PdfiumDiagnostics` (R12)

```csharp
internal static class PdfiumDiagnostics
{
    public static readonly bool Enabled =
        AppContext.TryGetSwitch("PdfiumWrapper.Diagnostics", out var on) && on;

    // Shared via AppContext (R4): "PdfiumWrapper.DiagCounters" -> long[16], "PdfiumWrapper.DiagEvents" -> ConcurrentQueue<(long,long,int,int)>
    // Counters: [0] gateEntries, [1] gateWaits (acquisitions that blocked), [2] activeHolders, [3] maxActiveHolders,
    //           [4] activeNative, [5] maxActiveNative, [6] initCount, [7] enqueued, [8] drained
    // Events: (startTicks, endTicks, threadId, kind) where kind: 0=GateWait, 1=GateHold, 100+NativeOp

    public static IDisposable NativeInterval(NativeOp op) { /* increments activeNative, updates max, records event on dispose */ }
    public static void RecordGateAcquired(int threadId, long waitStart) { /* gateEntries++, gateWaits++ if waitStart != 0 and elapsed > 0; activeHolders++ and max */ }
    public static void RecordGateReleased(int threadId) { /* close hold event, activeHolders-- */ }
    public static void RecordInit() { /* initCount++ */ }
    public static void RecordEnqueued(NativeHandleKind k) { /* enqueued++ */ }
    public static void RecordDrained(NativeHandleKind k) { /* drained++ */ }
    public static DiagnosticsSnapshot Snapshot() { /* copy counters and events */ }
    public static void Reset() { /* zero counters, clear events */ }
}

internal enum NativeOp { LoadDocument = 0, LoadPage = 1, Render = 2, Text = 3, Save = 4, Import = 5, FormFill = 6, Close = 7 }
```

Instrument `NativeInterval` around: `FPDF_LoadDocument`/`FPDF_LoadMemDocument`, `FPDF_LoadPage`, `FPDF_RenderPageBitmap` (+ `FPDF_FFLDraw`), `FPDFText_GetText`, `FPDF_SaveAsCopy`, `FPDF_ImportPages*`, `FORM_*` field writes, `FPDF_CloseDocument`. `maxActiveNative` is the independent detector used by Phase 3 tests: it must read 1 under contention. The test project enables the switch in `Bootstrapper.Initialize()` via `AppContext.SetSwitch("PdfiumWrapper.Diagnostics", true)` before any PDFium type is touched. `DiagnosticsSnapshot` and `Reset()` are exposed to tests through `InternalsVisibleTo`.

### 4.8 Test host `src/PdfiumWrapper.Tests.Host`

Console project, `IsPackable=false`, referenced by the test project with `ReferenceOutputAssembly=false` and copied to test output. Invocation: `PdfiumWrapper.Tests.Host <scenario> [key=value ...]`. Writes one JSON object to stdout and exits 0 on success, 2 on scenario failure, 3 on bad arguments. Any native abort surfaces as a different non-zero code or signal, which the parent test records.

```csharp
return args[0] switch
{
    "init-race"       => Scenarios.InitRace(threads: Arg("threads", 8), input: Arg("input")),
    "starvation"      => Scenarios.Starvation(callers: Arg("callers", 8 * Environment.ProcessorCount), input: Arg("input"), heartbeatBoundMs: Arg("bound", 100)),
    "finalizer-drain" => Scenarios.FinalizerDrain(graphs: Arg("graphs", 200), workers: Arg("workers", 4), input: Arg("input")),
    "alc-shared-gate" => Scenarios.AlcSharedGate(input: Arg("input")),
    "crash-probe"     => Scenarios.CrashProbe(input: Arg("input")),   // opens, renders, extracts text, saves; parent inspects exit code
    "cold-start"      => Scenarios.ColdStart(first: Arg("first", "document"), input: Arg("input")), // "document" | "merger" | "tiff"
    _ => 3
};
```

Parent-side helper in the test project:

```csharp
internal static HostResult RunHost(string scenario, TimeSpan timeout, params string[] kv)
{
    var psi = new ProcessStartInfo(HostPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    psi.ArgumentList.Add(scenario); foreach (var a in kv) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
    if (!p.WaitForExit((int)timeout.TotalMilliseconds)) { p.Kill(entireProcessTree: true); throw new TimeoutException(scenario); }
    return new HostResult(p.ExitCode, stdout.Result, stderr.Result);
}
```

---

### 4.9 Release 2: `PdfProcessingPool` (package `PdfiumWrapper.Processing`)

The developer-facing API. Everything below runs in the consumer's process; the PDF work runs in worker processes it starts and manages.

```csharp
using PdfiumWrapper.Processing;

await using var pool = new PdfProcessingPool(new PdfPoolOptions
{
    MinWorkers = 1,                                   // kept alive and warm
    MaxWorkers = PdfPoolOptions.DefaultMaxWorkers,    // Environment.ProcessorCount / 2, at least 1
    JobsPerWorker = 2,                                // a worker encodes one document while PDFium renders another
    ScaleUpAfter = TimeSpan.FromMilliseconds(500),    // all workers busy and jobs waiting for this long: add one
    IdleTimeout = TimeSpan.FromSeconds(60),           // a worker idle for this long is stopped, down to MinWorkers
    JobTimeout = TimeSpan.FromMinutes(2),             // per attempt; the worker is killed and replaced
    MaxAttempts = 2,                                  // a job whose worker crashed or timed out runs again on a fresh worker
    QueueCapacity = 1_000,                            // SubmitAsync waits when this many jobs are queued (backpressure)
    MaxWorkerMemoryBytes = null,                      // optional: retire a worker whose working set exceeds this, after its current job
    WorkerPath = null,                                // null: re-launch this executable as the worker (see PdfWorkerHost)
});

// Typed operations. Each is one job on one worker.
PdfJobResult<int>            pages = await pool.GetPageCountAsync("in.pdf", ct);
PdfJobResult<ImageFiles>     png   = await pool.ConvertToPngAsync("in.pdf", "out/in", dpi: 150, ct: ct);
PdfJobResult<ImageFiles>     jpeg  = await pool.ConvertToJpegAsync("in.pdf", "out/in", quality: 90, dpi: 300, ct: ct);
PdfJobResult<TiffFile>       tiff  = await pool.ConvertToTiffAsync("in.pdf", "out/in.tiff", dpi: 200, TiffColorMode.Bilevel, ct: ct);
PdfJobResult<string[]>       text  = await pool.ExtractTextAsync("in.pdf", ct);

// Batches: results in completion order, as they finish; the enumerable completes when all have.
await foreach (var r in pool.ConvertToPngAsync(files, outputRoot, dpi: 150, ct: ct))
    Console.WriteLine($"{r.Input}: {r.Status} {r.Value?.Files.Count} files in {r.Timings.Processing.TotalMilliseconds} ms");

// Inputs may also be byte[] or Stream; the pool spools them to a temp file for the worker.

// Observability
pool.Workers; pool.BusyWorkers; pool.QueuedJobs;
pool.Statistics;                      // jobs succeeded/failed/timed out/retried, worker starts/stops/crashes, scale-ups/downs
pool.Events += (s, e) => log.Information("{Kind} worker={Pid} job={JobId} {Detail}", e.Kind, e.WorkerPid, e.JobId, e.Detail);
```

```csharp
public sealed record PdfJobResult<T>(
    string Input,                     // the path or the name given with a byte[]/Stream input
    PdfJobStatus Status,              // Succeeded, Failed, TimedOut, Cancelled, WorkerCrashed
    T? Value,                         // set when Succeeded
    string? Error,                    // exception type and message from the worker, or the pool's reason
    int Attempts,
    int WorkerPid,
    PdfJobTimings Timings);           // Queued (submit to dispatch), Processing (dispatch to result), Total

public sealed record ImageFiles(int PageCount, IReadOnlyList<string> Files);
public sealed record TiffFile(int PageCount, string Path, long Bytes);

public enum PdfPoolEventKind
{
    WorkerStarting, WorkerReady, WorkerStopped, WorkerCrashed, WorkerRetiredForMemory,
    ScaledUp, ScaledDown, JobDispatched, JobCompleted, JobFailed, JobTimedOut, JobRetried, JobCancelled, QueueFull
}
```

Implementation notes (2026-10-02): built as specified, with these additions found while testing. Retries travel through their own unbounded channel so a retry queued after a crash is dispatched at once (the first version waited only on the job channel and left retries until the next submission). The dispatcher takes a job out of its queue only once a worker is free, so `QueueCapacity` is exact. Batch output directories get `-2`, `-3`, ... for repeated document names, because two workers writing the same files at once fail on Windows. `PdfProcessingPool.CreateAsync` is the preferred entry point so start-up failures surface. `JobsPerWorker` (default 2) was added after the first Phase 7 run: with one job per worker the warm pool of 8 reached 9.15 requests/s against 10.87 for 8 independent processes with 2 threads each (16% behind, outside the 10% acceptance), because a worker could not overlap encoding with rendering. Slots are counted per worker and the sizer scales on free slots. `PdfWorkerHost.DiagnosticsVariable` turns on wrapper diagnostics in a worker for the overlap test.

Review follow-up (pull request 18, 2026-10-02). Five findings, all reproduced and fixed: batch output names are reserved against originals as well as generated names (`report`, `report`, `report-2` got two paths and the third job overwrote the second's TIFF); a batch bounds the documents it has spooled, queued, running or unread to `QueueCapacity + MaxWorkers x JobsPerWorker` (it used to start every job at once and spool every input); cancellation while waiting for a queue slot, or while the input is spooled, returns a `Cancelled` result instead of throwing; an image job that fails part-way removes the pages it wrote, and the coordinator removes what a killed or crashed worker left behind; a worker whose start is cancelled by the caller is killed and `CreateAsync` fails with the cancellation (it used to return a pool with a live, untracked child), and `DisposeAsync` waits for starts in progress. The worker's exit is now acted on only after its stdout has been drained, so a progress or result frame written just before death is not lost. Test hooks gained `BeforeHello` and `AfterPage` (`slow-start:<ms>`, `crash-after-page-N:<match>`).

Second pass (2026-10-03), two further findings: the batch kept a continuation per job until the whole batch finished, so memory still grew with batch size (outstanding jobs are now counted, and the last to finish completes the results); and the coordinator's cleanup deleted `page_001` for any job that ended without success, including one cancelled before submission or one whose input was missing, so an ordinary failure could destroy earlier output. The fix establishes ownership instead of guessing: image jobs stage every page as `<final>.<jobId>.tmp`, send a `Progress` frame with `CommittingPages` once all are staged, then move them into place. The coordinator cleans up only for an attempt whose worker crashed or was killed, and only that job's `.tmp` files plus, when the worker had reported committing, the final names it had claimed. A job that never reached a worker removes nothing. Covered by `CleanupNeverTouchesOutputTheJobDidNotWrite`.

A job never fails the pool: a worker that crashes or hangs is killed and replaced, its job is reported as `WorkerCrashed`/`TimedOut` and retried up to `MaxAttempts`, and every other job proceeds. Disposing the pool stops accepting jobs, cancels queued ones (`Cancelled`), lets in-flight ones finish up to `JobTimeout`, then sends `Shutdown` to every worker.

**Worker hosting.** The worker is not a separate executable to deploy. By default the pool re-launches the consumer's own executable with the environment variable `PDFIUMWRAPPER_WORKER=1`, and the consumer makes this the first statement of `Main`:

```csharp
public static async Task<int> Main(string[] args)
{
    if (PdfWorkerHost.TryRun())      // true: this process is a worker; it has already run its loop and should exit
        return 0;

    // normal application startup
}
```

The relaunched process has the consumer's assemblies and native libraries available (they are in its own output directory, framework-dependent or self-contained), so there is nothing to publish per RID. The pool handles the `dotnet app.dll` case by launching `dotnet` with the entry assembly path. `WorkerPath` points at a dedicated worker executable instead, for hosts whose `Main` cannot be changed (the test project uses `PdfiumWrapper.Tests.Host` this way).

**Sizing policy.** On a 250 ms timer the coordinator reads `QueuedJobs` and `BusyWorkers`:

- Scale up by one worker when `QueuedJobs > 0` and `BusyWorkers == Workers` for at least `ScaleUpAfter`, until `MaxWorkers`. A burst therefore reaches `MaxWorkers` in `(MaxWorkers - MinWorkers) x ~0.5 s`; with the default 500 ms a single stray job never starts a process.
- Scale down by one worker when a worker has been idle for `IdleTimeout`, until `MinWorkers`. The timeout is long on purpose: an idle worker costs memory and nothing else, and a service with bursts every few minutes keeps them warm.
- Replace, don't recycle: a worker runs until it crashes, times out, exceeds `MaxWorkerMemoryBytes`, or is scaled down. No "restart after N jobs": that would hide leaks instead of exposing them.
- `MinWorkers` are started and pre-warmed (one small built-in page rendered) when the pool is constructed, so the first request never pays a cold start.

Measured basis (this machine, 1,000 PNG requests, `ai/tmp/throughput-comparison.md` for the full run): a worker reaches its first render about 75 to 85 ms after process start, uses 117 to 142 MB, and is CPU-bound on rendering, so one worker is one core. Throughput against worker count for this scenario, 2 request threads per process, PdfiumWrapper 2.0.0:

| Processes | Requests/sec | Pages/sec | Peak memory, all processes |
|---|---|---|---|
| 1 (4 threads) | 2.21 | 27.4 | 159 MB |
| 4 | 7.19 | 89.1 | 492 MB |
| 8 | 10.87 | 134.8 | 949 MB |
| 12 | 12.28 | 152.3 | 1,423 MB |
| 16 | 13.06 | 161.9 | 1,880 MB |

Past the fast cores each worker adds little, which is why `MaxWorkers` defaults to half the logical processors rather than all of them.

**Protocol** (coordinator to worker over the worker's stdin/stdout): length-prefixed (4-byte little-endian) UTF-8 JSON frames, 16 MiB maximum; the worker's stderr is drained to `Events`. Frames: `Hello {protocolVersion, pdfiumVersion, pid}` (worker, once), `Job {id, kind, input, output, options}`, `Progress {id, pagesDone}`, `Cancel {id}`, `Result {id, status, value, error, timings}`, `Shutdown`. `input` and `output` are file paths; text results above 4 MiB are written to a temp file the result points at. No delegates, handles, pixels or shell commands cross the boundary. A worker that receives a malformed frame reports it and continues; a coordinator that receives one kills that worker.

**Rules for the worker loop:** one job at a time, synchronous core APIs, `PdfiumRuntime` initialized once at start; between pages it checks for `Cancel`; on any exception it writes `Result{Failed}` and continues; on `Shutdown` or a closed stdin it exits 0. It never writes to stdout except protocol frames.

---

## Phase 0 — Immediate fixes that do not wait on design

- [x] Correct `docs/HIGH-THROUGHPUT-PROCESSING.md` (sections at `:49`, `:262` to `:460`, `:728`, summary table `:820`), `docs/BEST-PRACTICES.md:30` and `:53`, `README.md:152`, `AGENTS.md` Thread Safety, `docs/API-REFERENCE.md:44`, `docs/TROUBLESHOOTING.md:415`. Replace with: PDFium allows one native call per process at a time across all documents; until Release 1 ships, callers must serialize all PdfiumWrapper use in a process (one `SemaphoreSlim(1,1)` around every operation); file copies, byte arrays, and one document per thread do not help. Remove forced-GC batch advice and the `GC.GetTotalMemory` native-memory claim.
- [x] Add `[Collection("PDF Tests")]` to `PdfPageDeletionExample`. Add `TestProjectHygieneTests.AllPdfiumTestClassesShareTheCollection`: reflect over the test assembly, select classes whose methods reference `PdfiumWrapper` types or whose source files `using PdfiumWrapper`, and assert each has `CollectionAttribute("PDF Tests")`.
- [x] Replace `NativeLibraryResolver.EnsureRegistered()` with the static-constructor barrier from 4.2.
- [x] Create `PdfiumWrapper.Tests.Host` with the `init-race` scenario: barrier, N threads, each thread's first native use chosen round-robin from `new PdfDocument(file)`, `new PdfMerger()`, `new PdfMerger(file)`, `TiffWriter` on a rendered page. Report exceptions and `initCount`. Test asserts exit 0 and no exceptions. (`initCount == 1` is asserted after Phase 2 adds diagnostics.)
- [x] Run `dotnet test`; all existing tests pass.

Exit: public guidance no longer recommends unsafe parallelism; PDF tests cannot run in parallel by accident; first-use registration cannot race.

## Phase 1 — Sequential baseline (trimmed)

- [x] Record machine: OS/RID, CPU model and count, RAM, storage type, .NET SDK/runtime, PDFium version and DLL SHA-256. Deployment targets stay open inputs.
- [x] Tag the repository `bench-baseline-pre-gate`. Run the existing BenchmarkDotNet suite in Release and commit the CSV to `benchmark.md` per `plan-performance-benchmark.md`.
- [x] Add `SmallDocumentBenchmark.cs`: `[Benchmark] LoadCountClose()` = open `doc-1-page.pdf`, read `PageCount`, dispose; `[Benchmark] LoadRender72Close()` = same plus one 72 DPI `RenderToBytes`. This is where gate overhead is proportionally largest and is the Phase 4 regression reference.
- [x] Add the `cold-start` host scenario: report milliseconds from process start to first completed 72 DPI render for `first=document`, `first=merger`, `first=tiff`. Record three runs each.
- [x] Add the burst runner skeleton (Phase 4 spec) with `--n`, `--t`, `--callers 1`, `--mix`, `--input`, `--out`, `--report`. Run `--callers 1` for the sequential batch baseline. Required rate is `N / T_seconds`; engineering target includes 25% headroom. Do not state an achievable rate until measured.

Exit: reproducible sequential, small-document, cold-start, and single-caller batch baselines exist and are recorded in `benchmark.md`.

## Phase 2 — `PdfiumRuntime`: gate, lifecycle, callbacks, encode-outside-gate

Work in this order; run the suite after each step.

- [x] Add `PdfiumRuntime.cs`, `NativeHandleKind`, and `PdfiumDiagnostics.cs` per 4.1 and 4.7. Remove the `PdfDocument` static constructor. Move `TiffWriter` handler installation to `EnsureHandlersInstalled()` called from `OnAcquired`.
- [x] Change all 192 `LibraryImport` declarations from `public` to `internal` (R9). Bump `Version` to `2.0.0` in `PdfiumWrapper.csproj` and `PdfiumWrapper.runtime.csproj`. Fix compile errors in tests that used raw imports by routing them through wrapper APIs or `InternalsVisibleTo`.
- [x] Gate every public PDFium-touching member of `PdfDocument`, `PdfPage`, `PdfForm`, `PdfMerger`, `PdfMetadata`, `PdfBookmarks`, `PdfBookmark`, `PdfAttachments`, `PdfAttachment`, `PdfPageObject`, `PdfTextObject`, `PdfImageObject`, `PdfPathObject`, `PdfShadingObject`, `PdfFormObject` per 4.3. Introduce `Core` variants where loops would otherwise reenter per iteration. Put `ThrowIfDisposed()` inside the scope.
- [x] Add `GateCoverageTests.EveryPublicPdfiumMemberEntersTheGate`: for each public instance method and property getter on the types above (excluding `Dispose`, `Equals`, `GetHashCode`, `ToString`, and members marked `[NoNativeCall]`), invoke it on a live fixture with reasonable arguments via reflection and assert `gateEntries` increased. Add `[NoNativeCall]` (internal attribute) to the few members that are pure managed (e.g. `RawBitmap` accessors) so the test can skip them deliberately rather than silently.
- [x] Convert async methods to `EnterAsync` per page/operation per 4.3. Verify by code search that no `await` or `yield return` occurs lexically inside a `using (… Enter…)` block; add a unit test that scans the source files for that pattern as a guard.
- [x] Replace the four finalizers per 4.4. Make `PdfDocument` hold a reference to its `PdfForm` (if `GetForm()` creates one) so the finalizer can enqueue the form handle first. Make `PdfPage._page` and `PdfForm._formHandle` `internal` fields for the owner's finalizer.
- [x] Add `HandleOpened()`/`HandleClosed()` at every native open/close site.
- [x] Add `SpooledInput` and switch `PdfDocument(Stream)` and `PdfMerger(Stream)` to it; delete `StreamDocumentLoader`. Add `PooledFileWriter` and switch `Save(Stream)`/`SaveAsync(Stream)` and the `PdfMerger` stream save to it; delete `PdfStreamFileWriter`.
- [x] Add `BitmapLease` and `RenderToBitmapLease(Core)`; convert every render-then-encode path per 4.6; delete `RenderToBitmapHandle`.
- [x] Enforce R11: add `Debug.Assert(PdfiumRuntime.IsHeldByCurrentThread)` at each `lock (_pagesLock)`, `lock (_attachedObjectsLock)`, `lock (_disposeLock)`, `lock (_streamLock)` site.
- [x] Add `PdfiumRuntime.Shutdown()` tests: throws with a live document; succeeds after disposal; a new document works after shutdown.
- [x] Update XML docs on affected public members: "Thread safety: operations on different objects may run concurrently; the wrapper serializes native work. Do not use one object from two threads at once."

Exit: every public operation enters the same gate, finalizers never touch PDFium, user I/O never runs inside the gate, encoding runs outside the gate, no public import bypass remains, all existing tests pass.

## Phase 3 — Tests for concurrency and for each identified pitfall

All in `src/PdfiumWrapper.Tests/Concurrency/`. Every test has a bounded timeout. Tests that change process-global state run in the host.

- [x] **Serialization and correctness** (`PdfiumConcurrencyTests.cs`):

```csharp
[Theory, InlineData(2), InlineData(4), InlineData(8), InlineData(16)]
public void ConcurrentCallers_AreSerialized_AndMatchSequentialOracle(int callers)
{
    PdfiumDiagnostics.Reset();
    var inputs = new[] { "Docs/contract.pdf", "Docs/presentation.pdf", "Docs/fw2.pdf", "Docs/doc-3-pages-with-comments.pdf" };
    var oracle = inputs.ToDictionary(f => f, f => RenderHashesSequential(f, dpi: 72));   // SHA-256 per page over BGRA bytes

    using var start = new Barrier(callers);
    var errors = new ConcurrentBag<Exception>();
    var threads = Enumerable.Range(0, callers).Select(i => new Thread(() =>
    {
        try
        {
            start.SignalAndWait();
            var file = inputs[i % inputs.Length];
            using var doc = new PdfDocument(file);
            var hashes = doc.RenderPages(72).Select(Hash).ToArray();
            Assert.Equal(oracle[file], hashes);
            using var ms = new MemoryStream();
            doc.SaveAsTiff(ms, 100, TiffColorMode.Bilevel);
            Assert.True(ms.Length > 0);
        }
        catch (Exception ex) { errors.Add(ex); }
    }) { IsBackground = true }).ToList();

    threads.ForEach(t => t.Start());
    Assert.All(threads, t => Assert.True(t.Join(TimeSpan.FromMinutes(2)), "caller timed out"));
    Assert.Empty(errors);

    var s = PdfiumDiagnostics.Snapshot();
    Assert.True(s.GateWaits > 0, "no contention observed; threads did not overlap");
    Assert.Equal(1, s.MaxActiveHolders);
    Assert.Equal(1, s.MaxActiveNative);
    Assert.True(s.DistinctHolderThreads >= 2);
}
```

  Variants: text extraction compared to oracle strings; metadata/bookmarks; form field read/write on `fw2.pdf` copies; merge of two inputs then reopen and count pages; same file for all callers; `PdfMerger` first in a fresh host.

- [x] **Encode overlap**: 4 threads, `SaveAsTiff` to `MemoryStream` at 200 DPI. From `Snapshot().Events`, assert there exists a native `Render` interval of thread A and a gate-free window of thread B (gap between B's hold intervals while B is still running) that overlap in time. Assert every `FPDFBitmap_Destroy` happens inside a hold interval (checked by making `BitmapLease.Dispose` record a `Close` native interval).

- [x] **Thread-pool starvation** (host scenario `starvation`): `ThreadPool.SetMinThreads(pc, pc); ThreadPool.SetMaxThreads(pc, pc)` where `pc = Environment.ProcessorCount` (the runtime rejects smaller maxima). Start `8 * pc` concurrent `SaveAsTiffAsync(MemoryStream)` tasks. Concurrently run a heartbeat: every 50 ms, `Stopwatch` around `await Task.Run(() => { })`, record milliseconds. After all tasks complete, report heartbeat p50/p99 and total time. Test asserts exit 0, all tasks completed, and p99 under the bound (initially 100 ms; tune after first run and record the value). Run a second time with `mode=sync` where the tasks call the sync `SaveAsTiff` inside `Task.Run`, report the same numbers, and assert only completion; the two reports go into `benchmark.md` as the documented difference.

- [x] **Callback isolation**: `ThrottledStream` wraps a `MemoryStream` with `Thread.Sleep(50)` per `Read`/`Write` and records `(start, end)` of each call. Thread A: `new PdfMerger(throttled)` then `Save(throttledOut)`. Thread B: 50 sequential `RenderToBytes` at 72 DPI on `doc-1-page.pdf`, recording per-call latency. Assert: no throttled-stream call interval overlaps any gate-hold interval; B's p95 latency is below 3x B's solo p95 (measured in the same test before starting A).

- [x] **Deferred release** (host scenario `finalizer-drain`): `workers` threads run steady render jobs. Main thread creates `graphs` documents with two open pages each and a detached page object, drops all references, then `GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();`, then runs one `PdfiumRuntime.ReleasePending()`. Report `enqueued`, `drained`, `PendingCount`, `LiveHandleCount`, and whether any event with the finalizer thread's id is a `GateWait` or `GateHold`. Test asserts drained == enqueued, `PendingCount == 0`, `LiveHandleCount == 0` after workers finish and dispose, and zero finalizer-thread gate events. Also assert from the event order that for each graph the page `Close` events precede the document `Close` event.

- [x] **Shared gate across load contexts** (host scenario `alc-shared-gate`): load `PdfiumWrapper.dll` into `new AssemblyLoadContext("second")` via `LoadFromAssemblyPath`. Through reflection on the second copy, construct a `PdfDocument` and call `RenderPages(72)` on 4 threads while 4 threads use the primary copy. Report `ReferenceEquals(AppContext.GetData("PdfiumWrapper.NativeGate"), gateSeenByCopy2)` (copy 2 exposes it via `PdfiumRuntime` reflection), and `MaxActiveNative` from the shared counters. Assert true and 1.

- [x] **Initialization race**: Phase 0 scenario plus `initCount == 1`.

- [x] **Failure paths**: thread A loops over invalid file, wrong password, disposed document use, and a `Save` to a stream whose `Write` throws; thread B renders. Assert A gets the specific exception types, B completes, and after A finishes `PdfiumRuntime.Enter()` succeeds immediately (gate was released on every failure).

- [x] **Crash characterization** (host scenario `crash-probe`): test code generates malformed inputs from fixtures (truncate at 25/50/75%, flip 1% of bytes with a fixed seed, zero the xref) into `TestOutput/malformed/`. For each, run the host and record exit code. Test asserts only that the parent survived and writes the table to `TestOutput/crash-probe.md`. Inputs that abort the host are listed in `docs/TROUBLESHOOTING.md` as process-fatal in Release 1 and feed the R13 decision.

- [x] **Detector validation**: `PdfiumDiagnostics.NativeInterval` is called from a test on two threads simultaneously without entering the gate; assert `MaxActiveNative == 2`. This proves the detector would catch a real bypass.

- [x] Rename `MultipleDocuments_ShouldWorkConcurrently` (`PdfDocumentTests.cs:1300`) to `MultipleDocuments_CanCoexistSequentially`; keep its assertions.

Exit: all pitfall tests pass on win-x64 and linux-x64; concurrent callers produce oracle-equal output; stress and cleanup runs finish without crashes or deadlocks.

## Phase 4 — Benchmarks and the parallelism decision

New classes in `src/PdfiumWrapper.Benchmarks`, registered in `Program.cs` after the existing four. Diagnostics off for timing runs; one separate instrumented run reports wait/hold shares.

- [x] `GateOverheadBenchmark.cs`: the two `SmallDocumentBenchmark` operations. Compare against the `bench-baseline-pre-gate` tag run. Acceptance: median within 5%, p95 within 10%. Report absolute nanoseconds per `Enter()` from a micro-benchmark `[Benchmark] EnterExit()` that does `using var _ = PdfiumRuntime.Enter();` on an initialized runtime.

- [x] `ConcurrentCallersBenchmark.cs`:

```csharp
[MemoryDiagnoser]
public class ConcurrentCallersBenchmark
{
    [Params(1, 2, 4, 8)] public int Callers;
    [Params("tiff", "png", "jpeg")] public string Format;
    private string[] _inputs = null!;

    [GlobalSetup] public void Setup() { _inputs = BenchmarkBase.CorpusFiles(); using var warm = new PdfDocument(_inputs[0]); warm.RenderToBytes(72); }

    [Benchmark]
    public void ConvertBatch()
    {
        using var start = new Barrier(Callers);
        var threads = Enumerable.Range(0, Callers).Select(i => new Thread(() =>
        {
            start.SignalAndWait();
            using var doc = new PdfDocument(_inputs[i % _inputs.Length]);
            using var sink = Stream.Null;
            switch (Format)
            {
                case "tiff": doc.SaveAsTiff(sink, 200, TiffColorMode.Bilevel); break;
                case "png":  foreach (var _ in doc.StreamImageBytes(ImageFormat.Png, 100, 150)) { } break;
                case "jpeg": foreach (var _ in doc.StreamImageBytes(ImageFormat.Jpeg, 85, 150)) { } break;
            }
        })).ToList();
        threads.ForEach(t => t.Start()); threads.ForEach(t => t.Join());
    }
}
```

  Derived metrics written to `benchmark.md`: pages/sec = `Callers * pagesPerDoc / meanSeconds`; speedup = pages/sec at W over pages/sec at 1; from the instrumented run, gate-wait share and hold share of wall time.

- [x] Burst runner (`Program.cs` branch when `args[0] == "burst"`; otherwise existing BenchmarkDotNet behavior):

```text
dotnet run -c Release --project src/PdfiumWrapper.Benchmarks -- burst \
  --n 1000 --t 60 --callers 4 --mix tiff:50,png:30,jpeg:20 --dpi 200 \
  --input src/PdfiumWrapper.Tests/Docs --out TestOutput/burst --report burst.json \
  [--mode sync|async|async-starved] [--prewarm true|false] [--abandon-fraction 0.05]
```

  Report JSON fields: `n`, `t`, `callers`, `mode`, `totalSeconds`, `lastJobCompletedAt`, `metDeadline`, `jobs.success`, `jobs.expectedFailure`, `jobs.unexpectedFailure`, `latencyMs.{p50,p95,p99,max}` for queue, processing, and end-to-end, `pagesPerSec`, `docsPerSec`, `peakWorkingSetMB`, `steadyWorkingSetMB`, `cpuSeconds`, `outputBytes`, `queueDepthSamples[]`, `gateWaitShare` (instrumented run only), `heartbeatP99Ms` (async-starved mode), `pendingDrainedPerOp` (abandon mode).

- [x] `AsyncAdmissionBenchmark`: burst runner `--mode async` and `--mode async-starved` (constrained pool as in the starvation test) at callers 8, 16, 32; compare with `--mode sync`. Record heartbeat p99 and throughput.
- [x] `StreamCallbackBenchmark.cs`: `Save` to file, to `MemoryStream`, and to a 5 ms-per-write throttled stream; `PdfMerger` load from the same three. Instrumented run reports gate hold per operation; acceptance: hold time independent of the stream type within noise.
- [x] `FinalizerDrainBenchmark`: burst runner `--abandon-fraction 0.05` versus `0`; report throughput delta and `pendingDrainedPerOp`.
- [x] **Decision gate (R13).** Record `R_inproc(W)` for `W` in 1..ProcessorCount from `ConcurrentCallersBenchmark` on the mixed corpus. Build Release 2 only if `0.8 * max_W R_inproc(W) < N / T` and the consumer cannot add replicas, or the consumer requires isolation from native aborts (crash-probe table non-empty and consumer confirms the requirement). Otherwise write the measured single-lane and in-process capacities into `docs/HIGH-THROUGHPUT-PROCESSING.md` as sizing guidance and skip to Phase 8.

Exit: regression criteria verified, in-process scaling measured and recorded, documented go/no-go for the process pool.

## Phase 5 — `PdfiumWrapper.Processing`: worker pool (Release 2)

> Revised 2026-10-02. The owner asked for the pool after the 1,000-request throughput measurement (one process: 2.2 requests/s; 8 processes: 10.9; 16: 13.1). The design is in section 4.9; the rows below are the build order. The earlier "conditional" status and the separate per-RID worker package are withdrawn.

Build order; run the full suite after each step.

- [x] **Project and packaging.** Add `src/PdfiumWrapper.Processing` (library, `PackageId` `PdfiumWrapper.Processing`, references `PdfiumWrapper`; `InternalsVisibleTo` the test project). Add it to the solution and the PR/release workflows. The core package is unchanged and never launches processes (R15).
- [x] **Protocol.** `Frames.cs`: the record types in 4.9, `System.Text.Json` source-generated; `FrameReader`/`FrameWriter` over a `Stream` with the 4-byte prefix, 16 MiB limit, and a unit test for a truncated prefix, an oversize frame and an unknown kind.
- [x] **Worker.** `PdfWorkerHost.TryRun()`: returns false unless `PDFIUMWRAPPER_WORKER=1`; otherwise sends `Hello`, pre-warms (renders an embedded one-page PDF at 36 DPI), then loops per 4.9. Job execution for the five kinds (`PageCount`, `ConvertToPng`, `ConvertToJpeg`, `ConvertToTiff`, `ExtractText`) by calling the core APIs; output files written as `<name>.<jobId>.tmp` and renamed on success; partial outputs deleted on failure or cancel.
- [x] **Worker launch.** `WorkerLauncher`: `WorkerPath` when set; otherwise `Environment.ProcessPath` plus, when that is the `dotnet` host, the entry assembly path; working directory the consumer's base directory; stdin/stdout redirected, stderr drained to events; the `Hello` frame expected within `WorkerStartTimeout` (default 30 s) or the process is killed and the start reported as failed.
- [x] **Coordinator core.** `PdfProcessingPool`: bounded job queue (`Channel`, `QueueCapacity`), worker table, FIFO dispatch to idle workers, result routing to the job's `TaskCompletionSource`, `JobTimeout` per attempt, `MaxAttempts`, cancellation (queued: removed; in flight: `Cancel` frame, then kill after a 2 s grace), `DisposeAsync` semantics from 4.9, `Statistics` and `Events`.
- [x] **Sizing.** `Sizer`: the 250 ms timer and the scale-up/scale-down rules in 4.9, `MinWorkers` pre-warmed at construction, `MaxWorkerMemoryBytes` check after each job (working set read from the `Process`). Every decision raises an event.
- [x] **Typed API.** The operations and batch overloads in 4.9, `byte[]`/`Stream` inputs spooled through `SpooledInput` to a temp file owned by the job and deleted with it.
- [x] **Test host.** `PdfiumWrapper.Tests.Host` gains a `worker` scenario that calls `PdfWorkerHost.TryRun()`; the tests set `WorkerPath` to it. Add scenarios `worker-crash` (aborts after `Hello` on a given job id, via `Environment.FailFast`), `worker-hang` (never answers a given job id) and `worker-garbage` (writes a malformed frame).
- [x] Validate under a service account and paths containing spaces on win-x64 and linux-x64.

Exit: jobs run in independent PIDs through a typed API; a crashed or hung worker costs one job attempt, never the pool; admission is bounded; worker count follows load between `MinWorkers` and `MaxWorkers`.

## Phase 6 — Verify parallel native execution, recovery and sizing (Release 2)

All in `src/PdfiumWrapper.Tests/Processing/`, every test with a bounded timeout, workers from the test host.

- [x] **Correctness oracle.** 200 PNG and TIFF jobs over the five documents through a pool of 4; outputs byte-identical to the single-process results of the same calls (same DPI, same code path), page counts equal, no path collisions, every job `Succeeded` with `Attempts == 1`.
- [x] **Cross-PID overlap.** Workers started with `PdfiumWrapper.Diagnostics` on report their `Render` intervals in `Result.timings` as UTC ticks (calibrated by a ping at `Hello`). Assert at least one pair of overlapping render intervals from different PIDs, and `MaxActiveNative == 1` inside every worker.
- [x] **Backpressure.** `QueueCapacity = 8`, submit 64 jobs from one task: `SubmitAsync` must await (the 9th submission is still pending 200 ms later while workers are blocked by a `worker-hang` document), then every job reaches a final status; the coordinator's working set stays bounded.
- [x] **Fault injection.** Kill a busy worker (`WorkerCrashed`, retried once, then `Succeeded`); `worker-hang` with `JobTimeout = 1 s` (`TimedOut`, worker replaced, other jobs unaffected); `worker-garbage` (worker killed, job retried); invalid `WorkerPath` (constructor throws `PdfPoolException` naming the path); cancel a queued job and an in-flight job (`Cancelled`, no partial output); `DisposeAsync` with jobs running (in-flight finish or time out, queued are `Cancelled`, all worker processes gone within 5 s).
- [x] **Crash isolation.** Run the Phase 3 crash-probe corpus through the pool; the pool survives every input and reports `Failed` or `WorkerCrashed` per job, never an exception from `SubmitAsync`.
- [x] **Sizing.** `MinWorkers = 1`, `MaxWorkers = 4`, `ScaleUpAfter = 200 ms`, `IdleTimeout = 2 s`: submit 40 jobs at once; assert `Workers` reaches 4 within 3 s (`ScaledUp` events), all jobs complete, then `Workers` returns to 1 within `IdleTimeout + 3 s` (`ScaledDown` events). Then one job at a time with 1 s gaps: assert no scale-up. `MinWorkers == MaxWorkers`: assert no sizing events at all. `MaxWorkerMemoryBytes` set below a worker's steady working set: assert `WorkerRetiredForMemory` and the next job still succeeds.
- [x] **Hosting.** Done through the comparison project's `--engine pool` (`WorkerPath = null`, apphost re-launch, win-x64) and the test project (`WorkerPath` to a `.dll` through `dotnet`, win-x64 and linux-x64). **Not run:** a self-contained publish. A worker that never says `Hello` is killed after `WorkerStartTimeout` (`Worker.StartAsync`).
- [x] **Qualification job (not in PR CI).** Done 2026-10-08, locally (win-x64 native, linux-x64 in Docker), self-contained publish: all jobs correct and cleaned up; Linux worker memory fixed (`LinuxAllocator`). Results in `plan-pool-qualification.md`. 10,000-job burst and a 30-minute soak with `MinWorkers = 2`, `MaxWorkers = 8`, on win-x64 and linux-x64; smoke only on macOS. Sample each worker's working set per 1,000 jobs; investigate growth above 10% after warm-up rather than recycling workers.

Exit: cross-PID overlap demonstrated, results identical to in-process output, recovery and bounds validated, sizing behaves as specified.

## Phase 7 — Tune against the measurements (Release 2)

- [x] Add `--engine pool` to the comparison project's `throughput` runner: the same 1,000-request scenario through `PdfProcessingPool` with `MaxWorkers` 4, 8, 12, 16 and `MinWorkers` 1 (cold) and `= MaxWorkers` (warm). Reports the throughput fields plus `poolScaleUpSeconds` and the pool statistics. (Not done: a `pool` mode in the burst runner and `ipcMsPerJob`; the per-job `Timings` in results cover the latter.)
- [x] **Acceptance.** Warm pool at 8: 10.07 requests/s against 10.86 for 8 independent processes, 7% behind (limit 10%). Cold pool reached 8 workers in 4.5 s (limit 5 s) and finished within 3% of warm (limit 15%). Coordinator CPU: the harness process measured 0.12 cores at 8 workers and 0.13 at 16, including its own submitter threads and a process-enumerating memory sampler; the pool's own share was not isolated, so the 5% criterion is not confirmed, only bounded by 13%.
- [x] Set `DefaultMaxWorkers` from the measurement (half the logical processors, which is 12 here, gave 12.3 requests/s against 13.1 for 16 at twice the memory); document the rule and when to raise it.
- [x] Encode in the worker; never ship raw BGRA over IPC. Page-range splitting across workers: not built; no document in the scenario needed it.
- [x] Record the pool's numbers (PdfiumWrapper's only) in `benchmark.md` and the sizing guidance in `docs/HIGH-THROUGHPUT-PROCESSING.md`; replace the "no built-in batch or worker-pool API" note with the pool.

Primary acceptance remains all `N` jobs within `T` with correct outputs and 25% headroom on the deployment hardware.

## Phase 8 — Documentation and release qualification

- [x] Update `README.md`, `AGENTS.md`, XML remarks, `docs/API-REFERENCE.md`, `docs/BEST-PRACTICES.md`, `docs/HIGH-THROUGHPUT-PROCESSING.md`, `docs/TROUBLESHOOTING.md`: gate semantics; what concurrent submission does and does not provide; finalizer behavior and `ReleasePending()`; stream spooling and the threshold; encode overlap; measured single-lane and in-process capacity tables; replica scale-out guidance.
- [x] Document the pool (Release 2): `PdfProcessingPool` and `PdfWorkerHost` in `docs/API-REFERENCE.md`; a "Worker pool" section in `docs/HIGH-THROUGHPUT-PROCESSING.md` replacing the "no built-in batch or worker-pool API" note, with the sizing policy and the measured pool numbers; `README.md` package list; `AGENTS.md` project layout and the worker rules.
- [x] Document the 2.0 break: raw `PDFium` imports are internal; `PdfiumRuntime.Enter()`, `ReleasePending()`, `Shutdown()`, `IsHeldByCurrentThread`.
- [x] Pin and report PDFium version and DLL checksums in `benchmark.md` and release notes. Recorded in `benchmark.md`; the repository has no release-notes file, so the 2.0 break is described in `README.md` ("Upgrading to 2.0").
- [x] Run the full suite plus Phase 3 tests on all supported RIDs; recheck native resolution. Record results and open platform limits in `/docs` and `ai/current-state.md`. Done for win-x64 and linux-x64 (219 tests each). **Not run on osx-x64 or osx-arm64**: no macOS machine was available; recorded as an open platform check.

## First implementation step

Release 1 is done. Next: Phase 5 in its listed order (project, protocol, worker, launcher, coordinator, sizer, typed API, test host scenarios), then Phase 6 tests, then the Phase 7 measurements, then the Phase 8 pool documentation. Build on a branch from `main`; the gray TIFF rendering change (`feature/tiff-gray-render`) should merge first.

## Open inputs

- Exact `N`, `T`, page-count distribution, dominant operations/formats/DPI, input and output sizes, production hardware and storage. The pool's defaults (section 4.9) were chosen from this machine's measurements and the owner's scenario (1,000 requests, page count plus PNG); confirm or change them on the deployment hardware.
- Whether the consumer needs results in memory (today: page count and text come back inline, images as files on disk).
- Second-wave operations for the pool: merge, form filling, bookmarks and attachments. The first version covers page count, PNG, JPEG, TIFF and text.
