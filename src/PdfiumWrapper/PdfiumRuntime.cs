using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PdfiumWrapper;

/// <summary>
/// Process-wide coordination for the native PDFium library. PDFium permits one native call in
/// flight per process, across all documents. Every wrapper operation enters this gate once;
/// nested wrapper calls on the same thread reenter.
/// </summary>
/// <remarks>
/// Thread safety: operations on different objects may run concurrently; the wrapper serializes
/// native work. Do not use one object from two threads at once.
/// </remarks>
public static class PdfiumRuntime
{
    // AppContext keys. Values are BCL types only so that copies of this assembly in different
    // AssemblyLoadContexts share the same instances.
    private const string GateKey = "PdfiumWrapper.NativeGate";                 // SemaphoreSlim
    private const string OwnerKey = "PdfiumWrapper.GateOwner";                 // int[1]  managed thread id of holder, 0 = none
    private const string DepthKey = "PdfiumWrapper.GateDepth";                 // int[1]  reentrancy depth, touched only by holder
    private const string InitKey = "PdfiumWrapper.NativeInitialized";          // int[1]  0/1
    private const string PendingKey = "PdfiumWrapper.PendingReleases";         // ConcurrentQueue<nint>[], index = NativeHandleKind
    private const string PendingCountKey = "PdfiumWrapper.PendingReleaseCount"; // int[1]  fast-path hint for the drain
    private const string PendingFilesKey = "PdfiumWrapper.PendingTempFiles";   // ConcurrentQueue<string>
    private const string LiveKey = "PdfiumWrapper.LiveHandles";                // long[1] open native handles

    private const int KindCount = 7;

    private static readonly SemaphoreSlim s_gate = SharedState.GetOrCreate(GateKey, static () => new SemaphoreSlim(1, 1));
    private static readonly int[] s_owner = SharedState.GetOrCreate(OwnerKey, static () => new int[1]);
    private static readonly int[] s_depth = SharedState.GetOrCreate(DepthKey, static () => new int[1]);
    private static readonly int[] s_initialized = SharedState.GetOrCreate(InitKey, static () => new int[1]);
    private static readonly long[] s_live = SharedState.GetOrCreate(LiveKey, static () => new long[1]);
    private static readonly int[] s_pendingCount = SharedState.GetOrCreate(PendingCountKey, static () => new int[1]);
    private static readonly ConcurrentQueue<nint>[] s_pending = SharedState.GetOrCreate(PendingKey,
        static () => Enumerable.Range(0, KindCount).Select(_ => new ConcurrentQueue<nint>()).ToArray());
    private static readonly ConcurrentQueue<string> s_pendingFiles =
        SharedState.GetOrCreate(PendingFilesKey, static () => new ConcurrentQueue<string>());

    static PdfiumRuntime()
    {
        // Per assembly copy: each copy must register its own DllImportResolver.
        NativeLibraryResolver.EnsureRegistered();
    }

    /// <summary>True when the current thread holds the gate.</summary>
    public static bool IsHeldByCurrentThread
        => Volatile.Read(ref s_owner[0]) == Environment.CurrentManagedThreadId;

    /// <summary>
    /// Enter the native gate. Reentrant on the same thread. The outermost entry initializes the
    /// native library on first use and closes native handles abandoned by finalized wrapper objects.
    /// Dispose the returned scope exactly once, on the same thread, to exit. Never hold it across
    /// <c>await</c>, <c>yield return</c>, or a call into code that may block.
    /// </summary>
    public static Scope Enter()
    {
        if (PdfiumDiagnostics.Enabled)
            PdfiumDiagnostics.RecordEntry();

        int me = Environment.CurrentManagedThreadId;
        if (Volatile.Read(ref s_owner[0]) == me)
        {
            s_depth[0]++;
            return new Scope(active: true);
        }

        long waitStart = 0;
        bool waited = false;
        if (!s_gate.Wait(0))
        {
            waited = true;
            if (PdfiumDiagnostics.Enabled)
                waitStart = Stopwatch.GetTimestamp();
            s_gate.Wait();
        }

        Acquire(me, waitStart, waited);
        return new Scope(active: true);
    }

    /// <summary>
    /// Asynchronous admission for async wrapper methods. Await the result and use the scope
    /// synchronously: no <c>await</c> while it is alive.
    /// </summary>
    internal static GateAwaitable EnterAsync(CancellationToken cancellationToken = default)
    {
        if (PdfiumDiagnostics.Enabled)
            PdfiumDiagnostics.RecordEntry();

        if (IsHeldByCurrentThread)
        {
            s_depth[0]++;
            return default;
        }

        var wait = s_gate.WaitAsync(cancellationToken);
        bool waited = !wait.IsCompletedSuccessfully;
        long waitStart = waited && PdfiumDiagnostics.Enabled ? Stopwatch.GetTimestamp() : 0;
        return new GateAwaitable(wait, waitStart, waited);
    }

    /// <summary>
    /// Record ownership on <paramref name="threadId"/>, which must be the thread that will run the
    /// gated code. The semaphore is already held.
    /// </summary>
    private static void Acquire(int threadId, long waitStart, bool waited)
    {
        Volatile.Write(ref s_owner[0], threadId);
        s_depth[0] = 1;

        try
        {
            if (PdfiumDiagnostics.Enabled)
                PdfiumDiagnostics.RecordGateAcquired(threadId, waitStart, waited);

            if (s_initialized[0] == 0)
                Initialize();

            if (Volatile.Read(ref s_pendingCount[0]) != 0)
                DrainPendingReleasesCore();
        }
        catch
        {
            s_depth[0] = 1;
            Exit();
            throw;
        }
    }

    private static void Initialize()
    {
        PDFium.FPDF_InitLibrary();
        try
        {
            // libtiff's error handlers are process-global; install them once, here, under the gate.
            TiffWriter.EnsureHandlersInstalled();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // libtiff is optional until a TIFF is written; the missing library surfaces there.
        }

        s_initialized[0] = 1;
        if (PdfiumDiagnostics.Enabled)
            PdfiumDiagnostics.RecordInit();
    }

    private static void Exit()
    {
        if (!IsHeldByCurrentThread)
            throw new InvalidOperationException("A PdfiumRuntime scope was disposed on a thread that does not hold the gate.");

        if (--s_depth[0] == 0)
        {
            if (PdfiumDiagnostics.Enabled)
                PdfiumDiagnostics.RecordGateReleased(Environment.CurrentManagedThreadId);
            Volatile.Write(ref s_owner[0], 0);
            s_gate.Release();
        }
    }

    /// <summary>Scope token returned by <see cref="Enter"/>. Dispose exactly once.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly bool _active;

        internal Scope(bool active) => _active = active;

        /// <summary>Exit the gate.</summary>
        public void Dispose()
        {
            if (_active)
                Exit();
        }
    }

    /// <summary>Awaitable returned by <see cref="EnterAsync"/>.</summary>
    internal readonly struct GateAwaitable
    {
        private readonly Task? _wait;
        private readonly long _waitStart;
        private readonly bool _waited;

        internal GateAwaitable(Task wait, long waitStart, bool waited)
        {
            _wait = wait;
            _waitStart = waitStart;
            _waited = waited;
        }

        public GateAwaiter GetAwaiter() => new(_wait, _waitStart, _waited);
    }

    /// <summary>
    /// Claims gate ownership in <see cref="GetResult"/>, which the compiler calls on the thread that
    /// resumes the awaiting method. Recording the owner anywhere earlier would name the thread that
    /// completed the wait, which is not necessarily the thread that runs the gated code.
    /// </summary>
    internal readonly struct GateAwaiter : ICriticalNotifyCompletion
    {
        private readonly Task? _wait;
        private readonly long _waitStart;
        private readonly bool _waited;

        internal GateAwaiter(Task? wait, long waitStart, bool waited)
        {
            _wait = wait;
            _waitStart = waitStart;
            _waited = waited;
        }

        // A null task is the reentrant case: the caller already holds the gate.
        public bool IsCompleted => _wait is null || _wait.IsCompleted;

        // Never resume on a captured SynchronizationContext. The semaphore is handed to the waiter
        // before its continuation runs; if that continuation were posted to a UI thread that is
        // itself blocked in a synchronous Enter(), neither could ever proceed.
        public void OnCompleted(Action continuation)
            => _wait!.ConfigureAwait(false).GetAwaiter().OnCompleted(continuation);

        public void UnsafeOnCompleted(Action continuation)
            => _wait!.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(continuation);

        public Scope GetResult()
        {
            if (_wait is null)
                return new Scope(active: true);

            _wait.GetAwaiter().GetResult(); // throws if the wait was cancelled; the gate is not held then
            Acquire(Environment.CurrentManagedThreadId, _waitStart, _waited);
            return new Scope(active: true);
        }
    }

    // ---- Deferred release of handles abandoned to finalizers ----

    /// <summary>
    /// Queue a native handle for release. Called from finalizers, which never call PDFium and never
    /// wait on the gate. A finalizer must enqueue in ascending <see cref="NativeHandleKind"/> order.
    /// </summary>
    internal static void EnqueueRelease(NativeHandleKind kind, nint handle)
    {
        if (handle == 0)
            return;

        s_pending[(int)kind].Enqueue(handle);
        Interlocked.Increment(ref s_pendingCount[0]);
        if (PdfiumDiagnostics.Enabled)
            PdfiumDiagnostics.RecordEnqueued(kind);
    }

    /// <summary>Queue a spool file for deletion after its document has been closed.</summary>
    internal static void EnqueueTempFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        s_pendingFiles.Enqueue(path);
        Interlocked.Increment(ref s_pendingCount[0]);
    }

    /// <summary>
    /// Close native handles abandoned by finalized wrapper objects. This also happens automatically
    /// on every outermost <see cref="Enter"/>.
    /// </summary>
    public static void ReleasePending()
    {
        using var _ = Enter();
        DrainPendingReleasesCore();
    }

    internal static int PendingCount => s_pending.Sum(q => q.Count) + s_pendingFiles.Count;

    private static void DrainPendingReleasesCore()
    {
        // The finalizer thread may be enqueuing while this runs. Take the counts in reverse kind
        // order and drain only those counts: an item in the snapshot was enqueued before its
        // queue was counted, everything it depends on being released first was enqueued earlier
        // still, and lower kinds are counted later, so those are in the snapshot too. Draining
        // the live queues instead could pick up a document whose pages arrived after the page
        // queue had been passed.
        Span<int> counts = stackalloc int[KindCount];
        int files = s_pendingFiles.Count;
        for (int kind = KindCount - 1; kind >= 0; kind--)
            counts[kind] = s_pending[kind].Count;

        for (int kind = 0; kind < KindCount; kind++)
        {
            for (int n = counts[kind]; n > 0 && s_pending[kind].TryDequeue(out var handle); n--)
            {
                Interlocked.Decrement(ref s_pendingCount[0]);
                Release((NativeHandleKind)kind, handle);
                if (PdfiumDiagnostics.Enabled)
                    PdfiumDiagnostics.RecordDrained((NativeHandleKind)kind, handle);
            }
        }

        for (; files > 0 && s_pendingFiles.TryDequeue(out var path); files--)
        {
            Interlocked.Decrement(ref s_pendingCount[0]);
            SpooledInput.TryDelete(path);
        }
    }

    private static void Release(NativeHandleKind kind, nint handle)
    {
        switch (kind)
        {
            case NativeHandleKind.PageObject:
                PDFium.FPDFPageObj_Destroy(handle);
                HandleClosed();
                break;
            case NativeHandleKind.TextPage:
                PDFium.FPDFText_ClosePage(handle);
                HandleClosed();
                break;
            case NativeHandleKind.Form:
                PDFium.FPDFDOC_ExitFormFillEnvironment(handle);
                HandleClosed();
                break;
            case NativeHandleKind.Page:
                using (PdfiumDiagnostics.NativeInterval(NativeOp.Close))
                    PDFium.FPDF_ClosePage(handle);
                HandleClosed();
                break;
            case NativeHandleKind.Document:
                using (PdfiumDiagnostics.NativeInterval(NativeOp.Close))
                    PDFium.FPDF_CloseDocument(handle);
                HandleClosed();
                break;
            case NativeHandleKind.PinnedBuffer:
                GCHandle.FromIntPtr(handle).Free();
                break;
            case NativeHandleKind.NativeMemory:
                Marshal.FreeHGlobal(handle);
                break;
        }
    }

    // ---- Live handle accounting ----

    internal static void HandleOpened() => Interlocked.Increment(ref s_live[0]);

    internal static void HandleClosed() => Interlocked.Decrement(ref s_live[0]);

    /// <summary>
    /// Native handles currently owned by wrapper objects: documents, pages, form environments,
    /// detached page objects and bitmaps being encoded. Includes handles waiting for deferred release.
    /// </summary>
    public static long LiveHandleCount => Interlocked.Read(ref s_live[0]);

    /// <summary>
    /// Destroy the native library. Throws if any wrapper object is still alive or releases are
    /// pending. Intended for tests and controlled host shutdown only. The next <see cref="Enter"/>
    /// initializes the library again.
    /// </summary>
    public static void Shutdown()
    {
        using var _ = Enter();
        DrainPendingReleasesCore();

        if (s_depth[0] != 1)
            throw new InvalidOperationException("Cannot shut down PDFium from inside another wrapper operation.");
        if (LiveHandleCount != 0 || PendingCount != 0)
            throw new InvalidOperationException(
                $"Cannot shut down PDFium: {LiveHandleCount} live handles, {PendingCount} pending releases.");

        PDFium.FPDF_DestroyLibrary();
        s_initialized[0] = 0;
    }

    [Conditional("DEBUG")]
    internal static void AssertHeld()
        => Debug.Assert(IsHeldByCurrentThread, "This code path requires the PDFium gate to be held by the current thread.");
}

/// <summary>
/// Order is dependency order for deferred release: everything of a lower kind is released before
/// anything of a higher kind. Do not reorder.
/// </summary>
internal enum NativeHandleKind
{
    PageObject = 0,
    TextPage = 1,
    Form = 2,
    Page = 3,
    Document = 4,

    /// <summary>A pinned <see cref="GCHandle"/> keeping a document's backing bytes alive. Freed after the document closes.</summary>
    PinnedBuffer = 5,

    /// <summary><c>AllocHGlobal</c> memory a closed native object pointed at.</summary>
    NativeMemory = 6,
}
