using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

namespace PdfiumWrapper.Tests.Concurrency;

/// <summary>
/// Real threads, released together, each working on its own documents. The wrapper must serialize
/// the native work and every caller must get the same output as a sequential run.
/// </summary>
[Collection(PdfTestCollection.Name)]
public class PdfiumConcurrencyTests
{
    private static readonly TimeSpan CallerTimeout = TimeSpan.FromMinutes(2);

    private static readonly string[] Inputs =
    {
        "Docs/contract.pdf", "Docs/presentation.pdf", "Docs/fw2.pdf", "Docs/doc-3-pages-with-comments.pdf",
    };

    private static string Hash(RawBitmap bitmap) => Convert.ToHexString(SHA256.HashData(bitmap.Pixels));

    private static string[] RenderHashesSequential(string file, int dpi)
    {
        using var doc = new PdfDocument(file);
        return doc.RenderPages(dpi).Select(Hash).ToArray();
    }

    /// <summary>
    /// Runs <paramref name="work"/> on <paramref name="callers"/> threads released by one barrier
    /// and returns the diagnostics for exactly that run.
    /// </summary>
    private static DiagnosticsSnapshot RunConcurrently(int callers, Action<int> work)
    {
        PdfiumDiagnostics.Reset();

        using var start = new Barrier(callers);
        var errors = new ConcurrentBag<Exception>();
        var threads = Enumerable.Range(0, callers).Select(i => new Thread(() =>
        {
            try
            {
                start.SignalAndWait();
                work(i);
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }) { IsBackground = true }).ToList();

        threads.ForEach(t => t.Start());
        Assert.All(threads, t => Assert.True(t.Join(CallerTimeout), "caller timed out"));
        Assert.True(errors.IsEmpty, string.Join(Environment.NewLine, errors.Select(e => e.ToString())));

        return PdfiumDiagnostics.Snapshot();
    }

    /// <summary>The independent detector: at most one native call in flight, always inside a gate hold.</summary>
    private static void AssertSerialized(DiagnosticsSnapshot snapshot)
    {
        Assert.True(snapshot.GateWaits > 0, "no contention observed; threads did not overlap");
        Assert.Equal(1, snapshot.MaxActiveHolders);
        Assert.Equal(1, snapshot.MaxActiveNative);
        Assert.True(snapshot.DistinctHolderThreads >= 2, "only one thread ever held the gate");
        Assert.Equal(0, snapshot.EventsDropped);
        AssertNativeCallsInsideHolds(snapshot);
    }

    private static void AssertNativeCallsInsideHolds(DiagnosticsSnapshot snapshot)
    {
        var holdsByThread = snapshot.Events
            .Where(e => e.Kind == DiagnosticsEvent.GateHold)
            .GroupBy(e => e.ThreadId)
            .ToDictionary(g => g.Key, g => g.ToArray());

        foreach (var native in snapshot.Events.Where(e => e.IsNative))
        {
            bool inside = holdsByThread.TryGetValue(native.ThreadId, out var holds)
                          && holds.Any(h => h.StartTicks <= native.StartTicks && native.EndTicks <= h.EndTicks);
            Assert.True(inside, $"{native.NativeOp} on thread {native.ThreadId} ran outside a gate hold");
        }
    }

    [Theory, InlineData(2), InlineData(4), InlineData(8), InlineData(16)]
    public void ConcurrentCallers_AreSerialized_AndMatchSequentialOracle(int callers)
    {
        var oracle = Inputs.ToDictionary(f => f, f => RenderHashesSequential(f, dpi: 72));

        var snapshot = RunConcurrently(callers, i =>
        {
            var file = Inputs[i % Inputs.Length];
            using var doc = new PdfDocument(file);
            var hashes = doc.RenderPages(72).Select(Hash).ToArray();
            Assert.Equal(oracle[file], hashes);

            using var tiff = new MemoryStream();
            doc.SaveAsTiff(tiff, 100, TiffColorMode.Bilevel);
            Assert.True(tiff.Length > 0);
        });

        AssertSerialized(snapshot);
    }

    [Fact]
    public void ConcurrentCallers_OnTheSameFile_MatchSequentialOracle()
    {
        const string file = "Docs/contract.pdf";
        var oracle = RenderHashesSequential(file, dpi: 72);

        var snapshot = RunConcurrently(8, _ =>
        {
            using var doc = new PdfDocument(file);
            Assert.Equal(oracle, doc.RenderPages(72).Select(Hash).ToArray());
        });

        AssertSerialized(snapshot);
    }

    [Fact]
    public void ConcurrentTextExtraction_MatchesSequentialOracle()
    {
        var oracle = Inputs.ToDictionary(f => f, f =>
        {
            using var doc = new PdfDocument(f);
            return doc.ProcessAllPages(page => page.ExtractText());
        });

        var snapshot = RunConcurrently(8, i =>
        {
            var file = Inputs[i % Inputs.Length];
            for (int round = 0; round < 3; round++)
            {
                using var doc = new PdfDocument(file);
                Assert.Equal(oracle[file], doc.ProcessAllPages(page => page.ExtractText()));
            }
        });

        AssertSerialized(snapshot);
    }

    [Fact]
    public void ConcurrentMetadataBookmarksAndPageInfo_MatchSequentialOracle()
    {
        var oracle = Inputs.ToDictionary(f => f, DescribeDocument);

        var snapshot = RunConcurrently(8, i =>
        {
            var file = Inputs[i % Inputs.Length];
            for (int round = 0; round < 20; round++)
                Assert.Equal(oracle[file], DescribeDocument(file));
        });

        AssertSerialized(snapshot);
    }

    private static string DescribeDocument(string file)
    {
        using var doc = new PdfDocument(file);
        var metadata = doc.Metadata.GetAllMetadata().OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}");
        var bookmarks = doc.Bookmarks.GetAllBookmarks().Select(b => $"{b.Title}@{b.PageIndex}/{b.ChildCount}");
        var sizes = doc.GetAllPageSizes().Select(s => $"{s.width:F2}x{s.height:F2}");
        var labels = doc.GetAllPageLabels();
        return string.Join("|", metadata.Concat(bookmarks).Concat(sizes).Concat(labels!)
            .Append($"pages={doc.PageCount};attachments={doc.Attachments.Count};permissions={doc.Permissions};id={doc.DocumentId}"));
    }

    [Fact]
    public void ConcurrentFormFill_OnSeparateCopies_KeepsEachCallersValues()
    {
        var template = File.ReadAllBytes("Docs/fw2.pdf");
        string[] textFields =
        {
            W2FieldMapping.CopyA["employee_first_name"],
            W2FieldMapping.CopyA["employee_last_name"],
        };

        var snapshot = RunConcurrently(6, i =>
        {
            // Written values become readable after a save and reload, as in the form tests.
            using var saved = new MemoryStream();
            using (var doc = new PdfDocument((byte[])template.Clone()))
            using (var form = doc.GetForm()!)
            {
                foreach (var field in textFields)
                    form.SetFormFieldValue(field, $"caller-{i}-{field.Length}");
                doc.SaveToStream(saved);
            }

            using var reopened = new PdfDocument(saved.ToArray());
            using var reopenedForm = reopened.GetForm()!;
            foreach (var field in textFields)
                Assert.Equal($"caller-{i}-{field.Length}", reopenedForm.GetFormFieldValue(field));
        });

        AssertSerialized(snapshot);
    }

    [Fact]
    public void ConcurrentMerge_ProducesDocumentsWithTheExpectedPageCount()
    {
        const string first = "Docs/doc-3-pages-with-comments.pdf";
        const string second = "Docs/contract.pdf";
        int expectedPages;
        using (var a = new PdfDocument(first))
        using (var b = new PdfDocument(second))
            expectedPages = a.PageCount + b.PageCount;

        var snapshot = RunConcurrently(8, i =>
        {
            byte[] merged;
            using (var merger = i % 2 == 0 ? new PdfMerger(first) : new PdfMerger(File.ReadAllBytes(first)))
            {
                merger.AppendDocument(second);
                merged = merger.ToBytes();
            }

            using var reopened = new PdfDocument(merged);
            Assert.Equal(expectedPages, reopened.PageCount);
        });

        AssertSerialized(snapshot);
    }

    /// <summary>
    /// The point of rendering inside the gate and encoding outside it: one caller's native render
    /// overlaps a window in which another caller is running with the gate free.
    /// </summary>
    [Fact]
    public void EncodingRunsOutsideTheGate_AndOverlapsAnotherCallersRender()
    {
        var snapshot = RunConcurrently(4, _ =>
        {
            using var doc = new PdfDocument("Docs/contract.pdf");
            using var tiff = new MemoryStream();
            doc.SaveAsTiff(tiff, 200, TiffColorMode.Bilevel);
            Assert.True(tiff.Length > 0);
        });

        AssertSerialized(snapshot);

        var renders = snapshot.Events.Where(e => e.IsNative && e.NativeOp == NativeOp.Render).ToArray();
        Assert.NotEmpty(renders);

        // Gate-free windows of a thread: the gaps between its consecutive holds while it is still working.
        var freeWindows = snapshot.Events
            .Where(e => e.Kind == DiagnosticsEvent.GateHold)
            .GroupBy(e => e.ThreadId)
            .SelectMany(g =>
            {
                var holds = g.OrderBy(h => h.StartTicks).ToArray();
                return holds.Zip(holds.Skip(1), (current, next) =>
                    new DiagnosticsEvent(current.EndTicks, next.StartTicks, g.Key, DiagnosticsEvent.GateHold));
            })
            .ToArray();

        bool overlap = renders.Any(render => freeWindows.Any(window =>
            window.ThreadId != render.ThreadId && window.Overlaps(render)));
        Assert.True(overlap, "no render overlapped another caller's gate-free (encoding) window");

        // Every bitmap destroy (and every other close) happened under the gate.
        Assert.Contains(snapshot.Events, e => e.IsNative && e.NativeOp == NativeOp.Close);
    }

    /// <summary>
    /// A slow user stream must not hold the gate: its reads and writes happen outside every gate
    /// hold of the calling thread, and another caller's latency is unaffected.
    /// </summary>
    [Fact]
    public void SlowUserStreams_NeverRunInsideTheGate()
    {
        const string small = "Docs/doc-1-page.pdf";

        double[] RenderLatencies(int count)
        {
            var latencies = new double[count];
            using var doc = new PdfDocument(small);
            using var page = doc.GetPage(0);
            for (int i = 0; i < count; i++)
            {
                long start = Stopwatch.GetTimestamp();
                _ = page.RenderToBytes(612, 792);
                latencies[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            return latencies;
        }

        double soloP95 = Percentile(RenderLatencies(50), 0.95);

        var input = new ThrottledStream(new MemoryStream(File.ReadAllBytes(small)), TimeSpan.FromMilliseconds(50));
        var output = new ThrottledStream(new MemoryStream(), TimeSpan.FromMilliseconds(50));
        int slowThreadId = 0;
        double[] contended = Array.Empty<double>();

        var snapshot = RunConcurrently(2, i =>
        {
            if (i == 0)
            {
                slowThreadId = Environment.CurrentManagedThreadId;
                using var merger = new PdfMerger(input);
                merger.Save(output);
            }
            else
            {
                contended = RenderLatencies(50);
            }
        });

        Assert.True(input.Calls.Count >= 2, "the throttled input was not read in several calls");
        Assert.NotEmpty(output.Calls);
        Assert.True(output.Inner.Length > 0);

        var slowHolds = snapshot.Events
            .Where(e => e.Kind == DiagnosticsEvent.GateHold && e.ThreadId == slowThreadId)
            .ToArray();
        Assert.NotEmpty(slowHolds);

        foreach (var (start, end) in input.Calls.Concat(output.Calls))
        {
            var call = new DiagnosticsEvent(start, end, slowThreadId, DiagnosticsEvent.GateHold);
            Assert.DoesNotContain(slowHolds, hold => hold.Overlaps(call));
        }

        double contendedP95 = Percentile(contended, 0.95);
        Assert.True(contendedP95 < Math.Max(3 * soloP95, soloP95 + 20),
            $"render p95 rose from {soloP95:F2} ms to {contendedP95:F2} ms while another caller used slow streams");
    }

    [Fact]
    public void FailingOperations_AlwaysReleaseTheGate()
    {
        const string good = "Docs/contract.pdf";
        var oracle = RenderHashesSequential(good, dpi: 36);
        var garbage = new byte[] { 1, 2, 3, 4, 5 };

        var snapshot = RunConcurrently(2, i =>
        {
            if (i == 1)
            {
                for (int round = 0; round < 5; round++)
                {
                    using var doc = new PdfDocument(good);
                    Assert.Equal(oracle, doc.RenderPages(36).Select(Hash).ToArray());
                }
                return;
            }

            for (int round = 0; round < 25; round++)
            {
                Assert.Throws<InvalidOperationException>(() => new PdfDocument("no-such-file.pdf"));
                Assert.Throws<InvalidOperationException>(() => new PdfDocument(garbage));
                Assert.Throws<InvalidOperationException>(() => new PdfMerger(garbage));

                var disposed = new PdfDocument(good);
                disposed.Dispose();
                Assert.Throws<ObjectDisposedException>(() => _ = disposed.PageCount);
                Assert.Throws<ObjectDisposedException>(() => disposed.RenderPages(36));

                using var doc = new PdfDocument(good);
                Assert.Throws<ArgumentOutOfRangeException>(() => doc.GetPage(10_000));
                Assert.Throws<IOException>(() => doc.SaveToStream(new FailingStream()));
                Assert.False(PdfiumRuntime.IsHeldByCurrentThread);
            }
        });

        AssertSerialized(snapshot);

        // Had any failure leaked the gate, this would never be admitted.
        var probe = Task.Run(() =>
        {
            using var _ = PdfiumRuntime.Enter();
            return PdfiumRuntime.IsHeldByCurrentThread;
        });
        Assert.True(probe.Wait(TimeSpan.FromSeconds(10)), "the gate was left held after a failing operation");
        Assert.True(probe.Result);
        Assert.False(PdfiumRuntime.IsHeldByCurrentThread);
    }

    [Fact]
    public async Task AsyncCallers_AreSerialized_AndMatchSequentialOracle()
    {
        var oracle = Inputs.ToDictionary(f => f, f => RenderHashesSequential(f, dpi: 72));
        PdfiumDiagnostics.Reset();

        var tasks = Enumerable.Range(0, 12).Select(i => Task.Run(async () =>
        {
            var file = Inputs[i % Inputs.Length];
            using var doc = new PdfDocument(file);

            var hashes = (await doc.RenderPagesAsync(72)).Select(Hash).ToArray();
            Assert.Equal(oracle[file], hashes);

            using var tiff = new MemoryStream();
            await doc.SaveAsTiffAsync(tiff, 100);
            Assert.True(tiff.Length > 0);

            int pages = 0;
            await foreach (var jpeg in doc.StreamImageBytesAsync(ImageFormat.Jpeg, 80, 72))
            {
                Assert.True(jpeg.Length > 0);
                pages++;
            }
            Assert.Equal(oracle[file].Length, pages);
        })).ToArray();

        await Task.WhenAll(tasks).WaitAsync(CallerTimeout);

        var snapshot = PdfiumDiagnostics.Snapshot();
        Assert.True(snapshot.GateWaits > 0, "no contention observed; tasks did not overlap");
        Assert.Equal(1, snapshot.MaxActiveHolders);
        Assert.Equal(1, snapshot.MaxActiveNative);
        AssertNativeCallsInsideHolds(snapshot);
    }

    /// <summary>
    /// The gate is handed to an async waiter before its continuation runs. If that continuation
    /// were posted to the caller's SynchronizationContext, a UI thread that then makes a synchronous
    /// call would wait forever for a gate "held" by work queued behind itself.
    /// </summary>
    [Fact]
    public void AsyncAdmission_DoesNotResumeOnTheCallersSynchronizationContext()
    {
        using var first = new PdfDocument("Docs/doc-1-page.pdf");
        using var second = new PdfDocument("Docs/doc-1-page.pdf");

        using var holderEntered = new ManualResetEventSlim();
        using var releaseHolder = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using (PdfiumRuntime.Enter())
            {
                holderEntered.Set();
                releaseHolder.Wait(TimeSpan.FromSeconds(30));
            }
        }) { IsBackground = true };
        holder.Start();
        Assert.True(holderEntered.Wait(TimeSpan.FromSeconds(30)));

        var context = new NeverPumpingContext();
        Exception? error = null;
        int pages = 0;
        long tiffLength = 0;

        var uiThread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);

                // Queues behind the holder and returns an incomplete task.
                var tiff = new MemoryStream();
                var pending = first.SaveAsTiffAsync(tiff, 72);
                Assert.False(pending.IsCompleted);

                // Let the async waiter be granted the gate while this thread is not pumping,
                // then make a synchronous call from the same thread.
                releaseHolder.Set();
                Thread.Sleep(300);
                pages = second.PageCount;

                Assert.True(pending.Wait(TimeSpan.FromSeconds(30)), "the async save never completed");
                tiffLength = tiff.Length;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }) { IsBackground = true };

        uiThread.Start();
        bool finished = uiThread.Join(TimeSpan.FromSeconds(60));
        releaseHolder.Set();

        Assert.True(finished, "a synchronous call deadlocked behind an async waiter's posted continuation");
        Assert.Null(error);
        Assert.Equal(1, pages);
        Assert.True(tiffLength > 0);
        Assert.Equal(0, context.Posted);
    }

    /// <summary>A context whose owner never pumps: anything posted to it is never run.</summary>
    private sealed class NeverPumpingContext : SynchronizationContext
    {
        private int _posted;

        public int Posted => Volatile.Read(ref _posted);

        public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref _posted);

        public override void Send(SendOrPostCallback d, object? state) => Interlocked.Increment(ref _posted);

        public override SynchronizationContext CreateCopy() => this;
    }

    /// <summary>
    /// Proves the detector the other tests rely on would catch a bypass: two native intervals
    /// opened at once, without the gate, are seen as two.
    /// </summary>
    [Fact]
    public void Detector_ReportsOverlap_WhenTheGateIsBypassed()
    {
        PdfiumDiagnostics.Reset();

        using var bothInside = new Barrier(2);
        var threads = Enumerable.Range(0, 2).Select(_ => new Thread(() =>
        {
            using (PdfiumDiagnostics.NativeInterval(NativeOp.Render))
            {
                bothInside.SignalAndWait(TimeSpan.FromSeconds(30));
            }
        }) { IsBackground = true }).ToList();

        threads.ForEach(t => t.Start());
        Assert.All(threads, t => Assert.True(t.Join(CallerTimeout)));

        Assert.Equal(2, PdfiumDiagnostics.Snapshot().MaxActiveNative);
        PdfiumDiagnostics.Reset();
    }

    private static double Percentile(double[] values, double p)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        int rank = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    /// <summary>Sleeps on every read and write and records when each call ran.</summary>
    private sealed class ThrottledStream : Stream
    {
        private const int MaxChunk = 4096;
        private readonly TimeSpan _delay;

        public ThrottledStream(MemoryStream inner, TimeSpan delay)
        {
            Inner = inner;
            _delay = delay;
        }

        public MemoryStream Inner { get; }
        public ConcurrentQueue<(long Start, long End)> Calls { get; } = new();

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => Inner.Length;
        public override long Position { get => Inner.Position; set => Inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            long start = Stopwatch.GetTimestamp();
            Thread.Sleep(_delay);
            int read = Inner.Read(buffer, offset, Math.Min(count, MaxChunk));
            Calls.Enqueue((start, Stopwatch.GetTimestamp()));
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            long start = Stopwatch.GetTimestamp();
            Thread.Sleep(_delay);
            Inner.Write(buffer, offset, count);
            Calls.Enqueue((start, Stopwatch.GetTimestamp()));
        }

        public override void Flush() => Inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => Inner.Seek(offset, origin);
        public override void SetLength(long value) => Inner.SetLength(value);
    }

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("disk full");
    }
}
