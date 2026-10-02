using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace PdfiumWrapper.Tests.Host;

internal static partial class Scenarios
{
    /// <summary>
    /// Many async conversions on a thread pool pinned to the processor count, with a heartbeat that
    /// measures how long a trivial work item waits for a pool thread. In <c>mode=sync</c> the same
    /// work calls the synchronous API from pool threads, for comparison.
    /// </summary>
    public static int Starvation(int callers, string input, int heartbeatBoundMs, string mode)
    {
        if (mode is not ("async" or "sync"))
            throw new HostArgumentException($"mode must be async or sync; got '{mode}'");

        int pc = Environment.ProcessorCount;
        ThreadPool.GetMinThreads(out _, out int minIo);
        ThreadPool.GetMaxThreads(out _, out int maxIo);
        ThreadPool.SetMinThreads(pc, minIo);
        ThreadPool.SetMaxThreads(pc, maxIo);

        // Constructors are synchronous; open everything first so the run measures admission of the
        // conversions themselves.
        var documents = Enumerable.Range(0, callers).Select(_ => new PdfDocument(input)).ToArray();

        var heartbeatMs = new List<double>();
        using var stop = new CancellationTokenSource();
        var heartbeat = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                long start = Stopwatch.GetTimestamp();
                Task.Run(static () => { }).Wait();
                heartbeatMs.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                stop.Token.WaitHandle.WaitOne(50);
            }
        }) { IsBackground = true, Name = "heartbeat" };

        long t0 = Stopwatch.GetTimestamp();
        heartbeat.Start();

        var tasks = documents.Select(doc => mode == "async"
            ? Task.Run(async () =>
            {
                using var tiff = new MemoryStream();
                await doc.SaveAsTiffAsync(tiff, 100).ConfigureAwait(false);
                return tiff.Length;
            })
            : Task.Run(() =>
            {
                using var tiff = new MemoryStream();
                doc.SaveAsTiff(tiff, 100);
                return tiff.Length;
            })).ToArray();

        bool finished;
        string[] errors = Array.Empty<string>();
        try
        {
            finished = Task.WaitAll(tasks, TimeSpan.FromMinutes(5));
        }
        catch (AggregateException ex)
        {
            finished = true;
            errors = Describe(ex.Flatten().InnerExceptions);
        }

        double totalMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        stop.Cancel();
        heartbeat.Join();

        foreach (var doc in documents)
            doc.Dispose();

        int completed = tasks.Count(t => t.IsCompletedSuccessfully && t.Result > 0);
        var sorted = heartbeatMs.OrderBy(x => x).ToArray();
        double p50 = Percentile(sorted, 0.50);
        double p99 = Percentile(sorted, 0.99);

        // The bound applies to async admission only; the sync run exists to document the difference.
        bool ok = finished && completed == callers && errors.Length == 0
                  && (mode == "sync" || p99 <= heartbeatBoundMs);

        return Report(ok, new Dictionary<string, object?>
        {
            ["scenario"] = "starvation",
            ["mode"] = mode,
            ["processorCount"] = pc,
            ["callers"] = callers,
            ["completed"] = completed,
            ["totalMs"] = Math.Round(totalMs, 1),
            ["heartbeatSamples"] = sorted.Length,
            ["heartbeatP50Ms"] = Math.Round(p50, 3),
            ["heartbeatP99Ms"] = Math.Round(p99, 3),
            ["heartbeatMaxMs"] = Math.Round(sorted.Length == 0 ? 0 : sorted[^1], 3),
            ["heartbeatBoundMs"] = heartbeatBoundMs,
            ["exceptions"] = errors,
        });
    }

    /// <summary>
    /// Abandons whole object graphs to the finalizer while worker threads keep the gate busy, then
    /// checks that every handle was released by a gated drain, in dependency order, and that the
    /// finalizer thread never waited on or held the gate.
    /// </summary>
    public static int FinalizerDrain(int graphs, int workers, string input)
    {
        int finalizerThreadId = FinalizerThreadProbe.Capture();

        var errors = new ConcurrentBag<Exception>();
        using var stop = new CancellationTokenSource();
        var workerThreads = Enumerable.Range(0, workers).Select(i => new Thread(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var doc = new PdfDocument(input);
                    using var page = doc.GetPage(0);
                    _ = page.RenderToBytes(100, 130);
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }) { IsBackground = true, Name = $"render-{i}" }).ToList();
        workerThreads.ForEach(t => t.Start());

        var graphHandles = BuildGraphs(graphs, input);

        // Abandon the graphs a batch at a time. Workers keep entering the gate, so the drain of one
        // batch is still closing documents when the finalizer thread starts enqueuing the next:
        // exactly the interleaving in which a drain over the live queues would close a document
        // ahead of its pages.
        for (int batch = 0; batch < s_batches!.Length; batch++)
        {
            s_batches[batch] = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Thread.SpinWait(20_000);
        }

        for (int attempt = 0; attempt < 5; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            PdfiumRuntime.ReleasePending();
        }

        stop.Cancel();
        bool joined = workerThreads.All(t => t.Join(TimeSpan.FromMinutes(1)));
        PdfiumRuntime.ReleasePending();

        var snapshot = PdfiumDiagnostics.Snapshot();
        var releases = PdfiumDiagnostics.DrainedReleases();

        // Position of each handle's release, by kind. Handles are unique while all graphs are alive,
        // and nothing was released before the graphs were dropped.
        var releaseIndex = new Dictionary<(NativeHandleKind, nint), int>();
        for (int i = 0; i < releases.Length; i++)
            releaseIndex.TryAdd(releases[i], i);

        int orderViolations = 0;
        int missing = 0;
        foreach (var graph in graphHandles)
        {
            if (!releaseIndex.TryGetValue((NativeHandleKind.Document, graph.Document), out int documentAt))
            {
                missing++;
                continue;
            }

            foreach (var (kind, handle) in graph.Dependents)
            {
                if (!releaseIndex.TryGetValue((kind, handle), out int dependentAt))
                    missing++;
                else if (dependentAt > documentAt)
                    orderViolations++;
            }
        }

        int finalizerGateEvents = snapshot.Events.Count(e =>
            e.ThreadId == finalizerThreadId && e.Kind is DiagnosticsEvent.GateWait or DiagnosticsEvent.GateHold);

        bool ok = joined && errors.IsEmpty
                  && snapshot.Enqueued > 0
                  && snapshot.Drained == snapshot.Enqueued
                  && PdfiumRuntime.PendingCount == 0
                  && PdfiumRuntime.LiveHandleCount == 0
                  && finalizerGateEvents == 0
                  && orderViolations == 0
                  && missing == 0;

        return Report(ok, new Dictionary<string, object?>
        {
            ["scenario"] = "finalizer-drain",
            ["graphs"] = graphs,
            ["workers"] = workers,
            ["enqueued"] = snapshot.Enqueued,
            ["drained"] = snapshot.Drained,
            ["pendingCount"] = PdfiumRuntime.PendingCount,
            ["liveHandleCount"] = PdfiumRuntime.LiveHandleCount,
            ["finalizerThreadId"] = finalizerThreadId,
            ["finalizerGateEvents"] = finalizerGateEvents,
            ["orderViolations"] = orderViolations,
            ["missingReleases"] = missing,
            ["maxActiveNative"] = snapshot.MaxActiveNative,
            ["exceptions"] = Describe(errors),
        });
    }

    private sealed record GraphHandles(nint Document, (NativeHandleKind Kind, nint Handle)[] Dependents);

    private const int GraphsPerBatch = 25;

    // The only references to the graphs. Clearing a slot abandons that batch to the finalizer.
    private static List<object>?[]? s_batches;

    /// <summary>
    /// Each graph: a document, two open pages, a form, and a page object removed from its page.
    /// All graphs stay alive until every one exists, so their handles are distinct.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static GraphHandles[] BuildGraphs(int graphs, string input)
    {
        var handles = new GraphHandles[graphs];
        s_batches = new List<object>?[(graphs + GraphsPerBatch - 1) / GraphsPerBatch];

        for (int i = 0; i < graphs; i++)
        {
            var keepAlive = s_batches[i / GraphsPerBatch] ??= new List<object>();

            var doc = new PdfDocument(input);
            var first = doc.GetPage(0);
            var second = doc.GetPage(doc.PageCount > 1 ? 1 : 0);
            var form = doc.GetForm();
            var rectangle = first.AddRectangle(10, 10, 20, 20);
            first.RemoveObject(rectangle);

            var dependents = new List<(NativeHandleKind, nint)>
            {
                (NativeHandleKind.Page, first.Handle),
                (NativeHandleKind.Page, second.Handle),
                (NativeHandleKind.PageObject, rectangle.Handle),
            };
            if (form != null)
                dependents.Add((NativeHandleKind.Form, form._formHandle));

            handles[i] = new GraphHandles(doc.Document, dependents.ToArray());
            keepAlive.Add(doc);
            keepAlive.Add(first);
            keepAlive.Add(second);
            keepAlive.Add(rectangle);
            if (form != null)
                keepAlive.Add(form);
        }

        PdfiumDiagnostics.Reset();
        return handles;
    }

    private sealed class FinalizerThreadProbe
    {
        private static int s_threadId;

        ~FinalizerThreadProbe() => Volatile.Write(ref s_threadId, Environment.CurrentManagedThreadId);

        public static int Capture()
        {
            Allocate();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return Volatile.Read(ref s_threadId);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Allocate() => _ = new FinalizerThreadProbe();
    }

    /// <summary>
    /// Loads a second copy of the wrapper assembly into another AssemblyLoadContext and drives both
    /// copies from several threads. Both must use one gate over the single native module.
    /// </summary>
    public static int AlcSharedGate(string input)
    {
        var primaryAssembly = typeof(PdfDocument).Assembly;
        var context = new AssemblyLoadContext("second");
        var secondAssembly = context.LoadFromAssemblyPath(primaryAssembly.Location);

        var secondDocumentType = secondAssembly.GetType("PdfiumWrapper.PdfDocument", throwOnError: true)!;
        var secondRuntimeType = secondAssembly.GetType("PdfiumWrapper.PdfiumRuntime", throwOnError: true)!;
        var renderPages = secondDocumentType.GetMethod("RenderPages", new[] { typeof(int) })!;
        bool typesDistinct = secondDocumentType != typeof(PdfDocument);

        PdfiumDiagnostics.Reset();
        var errors = new ConcurrentBag<Exception>();
        using var start = new Barrier(8);

        var threads = Enumerable.Range(0, 8).Select(i => new Thread(() =>
        {
            try
            {
                start.SignalAndWait();
                for (int round = 0; round < 3; round++)
                {
                    if (i % 2 == 0)
                    {
                        using var doc = new PdfDocument(input);
                        _ = doc.RenderPages(72);
                    }
                    else
                    {
                        var doc = Activator.CreateInstance(secondDocumentType, input, null)!;
                        try
                        {
                            renderPages.Invoke(doc, new object[] { 72 });
                        }
                        finally
                        {
                            ((IDisposable)doc).Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex is TargetInvocationException { InnerException: { } inner } ? inner : ex);
            }
        }) { IsBackground = true }).ToList();

        threads.ForEach(t => t.Start());
        bool joined = threads.All(t => t.Join(TimeSpan.FromMinutes(2)));

        const BindingFlags privateStatic = BindingFlags.NonPublic | BindingFlags.Static;
        object? gateSeenByPrimary = typeof(PdfiumRuntime).GetField("s_gate", privateStatic)!.GetValue(null);
        object? gateSeenBySecond = secondRuntimeType.GetField("s_gate", privateStatic)!.GetValue(null);
        object? gateInAppContext = AppContext.GetData("PdfiumWrapper.NativeGate");
        bool sharedGate = gateInAppContext != null
                          && ReferenceEquals(gateInAppContext, gateSeenByPrimary)
                          && ReferenceEquals(gateInAppContext, gateSeenBySecond);

        var snapshot = PdfiumDiagnostics.Snapshot();
        bool ok = joined && errors.IsEmpty && typesDistinct && sharedGate
                  && snapshot.MaxActiveNative == 1 && snapshot.MaxActiveHolders == 1
                  && snapshot.InitCount == 1 && snapshot.GateWaits > 0;

        return Report(ok, new Dictionary<string, object?>
        {
            ["scenario"] = "alc-shared-gate",
            ["typesDistinct"] = typesDistinct,
            ["sharedGate"] = sharedGate,
            ["maxActiveNative"] = snapshot.MaxActiveNative,
            ["maxActiveHolders"] = snapshot.MaxActiveHolders,
            ["gateWaits"] = snapshot.GateWaits,
            ["initCount"] = snapshot.InitCount,
            ["joined"] = joined,
            ["exceptions"] = Describe(errors),
        });
    }

    /// <summary>
    /// Opens, renders, extracts text and saves one input. A managed failure is reported and exits
    /// with the failure code; a native abort ends the process with whatever code the OS assigns,
    /// which is what the parent test records.
    /// </summary>
    public static int CrashProbe(string input)
    {
        WindowsErrorMode.SuppressFaultDialogs();

        string stage = "open";
        try
        {
            using var doc = new PdfDocument(input);
            stage = "count";
            int pages = doc.PageCount;

            stage = "render";
            for (int i = 0; i < Math.Min(pages, 3); i++)
            {
                using var page = doc.GetPage(i);
                _ = page.RenderToBytes(Math.Max(1, (int)page.Width), Math.Max(1, (int)page.Height));
                stage = "text";
                _ = page.ExtractText();
                stage = "render";
            }

            stage = "save";
            using var output = new MemoryStream();
            doc.SaveToStream(output);

            return Report(true, new Dictionary<string, object?>
            {
                ["scenario"] = "crash-probe",
                ["pages"] = pages,
                ["savedBytes"] = output.Length,
            });
        }
        catch (Exception ex)
        {
            return Report(false, new Dictionary<string, object?>
            {
                ["scenario"] = "crash-probe",
                ["stage"] = stage,
                ["exception"] = $"{ex.GetType().Name}: {ex.Message}",
            });
        }
    }

    /// <summary>
    /// <see cref="PdfiumRuntime.Shutdown"/> refuses while a document is alive, succeeds once it is
    /// disposed, and the library initializes again on next use.
    /// </summary>
    public static int Shutdown(string input)
    {
        bool threwWhileLive;
        using (var live = new PdfDocument(input))
        {
            try
            {
                PdfiumRuntime.Shutdown();
                threwWhileLive = false;
            }
            catch (InvalidOperationException)
            {
                threwWhileLive = true;
            }

            // The refused shutdown must leave the library usable.
            _ = live.PageCount;
        }

        string? shutdownError = null;
        try
        {
            PdfiumRuntime.Shutdown();
        }
        catch (Exception ex)
        {
            shutdownError = $"{ex.GetType().Name}: {ex.Message}";
        }

        int pagesAfter;
        int renderedBytes;
        using (var doc = new PdfDocument(input))
        using (var page = doc.GetPage(0))
        {
            pagesAfter = doc.PageCount;
            renderedBytes = page.RenderToBytes(100, 130).Length;
        }

        long initCount = InitCount();
        bool ok = threwWhileLive && shutdownError == null && pagesAfter > 0 && renderedBytes > 0
                  && initCount == 2 && PdfiumRuntime.LiveHandleCount == 0;

        return Report(ok, new Dictionary<string, object?>
        {
            ["scenario"] = "shutdown",
            ["threwWhileLive"] = threwWhileLive,
            ["shutdownError"] = shutdownError,
            ["pagesAfter"] = pagesAfter,
            ["renderedBytes"] = renderedBytes,
            ["initCount"] = initCount,
            ["liveHandleCount"] = PdfiumRuntime.LiveHandleCount,
        });
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0)
            return 0;
        int rank = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }
}
