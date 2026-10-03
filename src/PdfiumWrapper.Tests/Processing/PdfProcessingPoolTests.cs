using System.Diagnostics;
using System.Security.Cryptography;
using PdfiumWrapper.Processing;

namespace PdfiumWrapper.Tests.Processing;

[Collection(PdfTestCollection.Name)]
public class PdfProcessingPoolTests : IDisposable
{
    private readonly string _output = PoolFixture.TempDirectory();
    private readonly List<PdfPoolEvent> _events = new();

    public void Dispose() => PoolFixture.DeleteDirectory(_output);

    private async Task<PdfProcessingPool> CreateAsync(Action<PdfPoolOptions>? configure = null)
    {
        var pool = await PdfProcessingPool.CreateAsync(PoolFixture.Options(configure)).WaitAsync(PoolFixture.TestTimeout);
        pool.Events += (_, e) => { lock (_events) _events.Add(e); };
        return pool;
    }

    private PdfPoolEvent[] Events(PdfPoolEventKind kind)
    {
        lock (_events) return _events.Where(e => e.Kind == kind).ToArray();
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task PageCount_PngAndTiff_MatchTheInProcessResults()
    {
        await using var pool = await CreateAsync();
        string input = PoolFixture.Input("contract.pdf");

        var pages = await pool.GetPageCountAsync(input).WaitAsync(PoolFixture.TestTimeout);
        Assert.Equal(PdfJobStatus.Succeeded, pages.Status);
        Assert.Equal(10, pages.Value);
        Assert.Equal(1, pages.Attempts);
        Assert.True(pages.WorkerPid > 0);

        var png = await pool.ConvertToPngAsync(input, Path.Combine(_output, "png"), dpi: 50).WaitAsync(PoolFixture.TestTimeout);
        Assert.True(png.IsSuccess, png.Error);
        Assert.Equal(10, png.Value!.Files.Count);
        Assert.All(png.Value.Files, f => Assert.True(File.Exists(f)));
        Assert.Empty(Directory.GetFiles(Path.Combine(_output, "png"), "*.tmp"));

        var tiff = await pool.ConvertToTiffAsync(input, Path.Combine(_output, "out.tiff"), dpi: 50).WaitAsync(PoolFixture.TestTimeout);
        Assert.True(tiff.IsSuccess, tiff.Error);
        Assert.Equal(10, tiff.Value!.PageCount);

        // Byte-identical to the same calls made in this process.
        string reference = Path.Combine(_output, "reference");
        using (var doc = new PdfDocument(input))
        {
            doc.SaveAsPngs(Path.Combine(reference, "png"), "page", dpi: 50);
            doc.SaveAsTiff(Path.Combine(reference, "out.tiff"), dpi: 50);
        }

        for (int i = 0; i < 10; i++)
            Assert.Equal(Sha256(Path.Combine(reference, "png", $"page_{i + 1:D3}.png")), Sha256(png.Value.Files[i]));
        Assert.Equal(Sha256(Path.Combine(reference, "out.tiff")), Sha256(tiff.Value.Path));
    }

    [Fact]
    public async Task Text_Jpeg_BytesAndStreamInputs_Work()
    {
        await using var pool = await CreateAsync();
        string input = PoolFixture.Input("doc-3-pages-with-comments.pdf");
        string[] expectedText;
        using (var doc = new PdfDocument(input))
            expectedText = doc.ProcessAllPages(p => p.ExtractText());

        var text = await pool.ExtractTextAsync(input).WaitAsync(PoolFixture.TestTimeout);
        Assert.True(text.IsSuccess, text.Error);
        Assert.Equal(expectedText, text.Value);

        var fromBytes = await pool.GetPageCountAsync(PdfInput.FromBytes(File.ReadAllBytes(input), "bytes.pdf")).WaitAsync(PoolFixture.TestTimeout);
        Assert.Equal(3, fromBytes.Value);
        Assert.Equal("bytes.pdf", fromBytes.Input);

        using var stream = File.OpenRead(input);
        var jpeg = await pool.ConvertToJpegAsync(PdfInput.FromStream(stream, "stream.pdf"), Path.Combine(_output, "jpg"), quality: 80, dpi: 40).WaitAsync(PoolFixture.TestTimeout);
        Assert.True(jpeg.IsSuccess, jpeg.Error);
        Assert.Equal(3, jpeg.Value!.Files.Count);
        Assert.EndsWith(".jpg", jpeg.Value.Files[0]);
    }

    [Fact]
    public async Task BadDocument_IsFailed_NotRetried_AndTheWorkerSurvives()
    {
        await using var pool = await CreateAsync();
        string bad = Path.Combine(_output, "bad.pdf");
        File.WriteAllBytes(bad, new byte[] { 1, 2, 3 });

        var failed = await pool.GetPageCountAsync(bad).WaitAsync(PoolFixture.TestTimeout);
        Assert.Equal(PdfJobStatus.Failed, failed.Status);
        Assert.Contains("InvalidOperationException", failed.Error);
        Assert.Equal(1, failed.Attempts);

        var missing = await pool.GetPageCountAsync(Path.Combine(_output, "missing.pdf")).WaitAsync(PoolFixture.TestTimeout);
        Assert.Equal(PdfJobStatus.Failed, missing.Status);

        var ok = await pool.GetPageCountAsync(PoolFixture.Input("doc-1-page.pdf")).WaitAsync(PoolFixture.TestTimeout);
        Assert.Equal(1, ok.Value);
        Assert.Equal(failed.WorkerPid, ok.WorkerPid); // the same worker kept going
        Assert.Equal(0, pool.Statistics.WorkersCrashed);
    }

    [Fact]
    public async Task Batch_YieldsEveryResult_AndRunsOnSeveralWorkers()
    {
        await using var pool = await CreateAsync(o => { o.MinWorkers = 2; o.MaxWorkers = 2; });
        var inputs = new[] { "doc-1-page.pdf", "doc-3-pages-with-comments.pdf", "contract.pdf", "fw2.pdf" }
            .SelectMany(f => Enumerable.Repeat(PdfInput.FromFile(PoolFixture.Input(f)), 5))
            .ToArray();

        var results = new List<PdfJobResult<ImageFiles>>();
        await foreach (var r in pool.ConvertToPngAsync(inputs, Path.Combine(_output, "batch"), dpi: 30).WithCancellation(new CancellationTokenSource(PoolFixture.TestTimeout).Token))
            results.Add(r);

        Assert.Equal(20, results.Count);
        Assert.All(results, r => Assert.True(r.IsSuccess, r.Error));
        Assert.Equal((1 + 3 + 10 + 11) * 5, results.Sum(r => r.Value!.PageCount));

        // Five copies of each document: one directory each, suffixed, so no two jobs wrote the same files.
        var directories = results.Select(r => Path.GetDirectoryName(r.Value!.Files[0])!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(20, directories.Length);
        Assert.Contains(directories, d => d.EndsWith("contract", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(directories, d => d.EndsWith("contract-5", StringComparison.OrdinalIgnoreCase));
        Assert.True(results.Select(r => r.WorkerPid).Distinct().Count() >= 2, "only one worker did all the work");
        Assert.Equal(20, pool.Statistics.JobsSucceeded);
    }

    [Fact]
    public async Task WorkerCrash_IsRetriedOnAFreshWorker_AndReported()
    {
        string input = PoolFixture.Input("doc-1-page.pdf");
        string crashing = Path.Combine(_output, "crash-me.pdf");
        File.Copy(input, crashing);

        await using var pool = await CreateAsync(o => o.WorkerEnvironment["PDFIUMWRAPPER_TEST_FAULT"] = "crash:crash-me");

        var result = await pool.GetPageCountAsync(crashing).WaitAsync(PoolFixture.TestTimeout);

        // The first worker died on the job; the replacement (same fault, fires once per process) crashed too,
        // so with MaxAttempts = 2 the job ends as WorkerCrashed, and every worker that saw it is gone.
        Assert.Equal(PdfJobStatus.WorkerCrashed, result.Status);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(1, pool.Statistics.JobsRetried);
        Assert.True(pool.Statistics.WorkersCrashed >= 2);
        Assert.NotEmpty(Events(PdfPoolEventKind.JobRetried));

        // The pool is still serving.
        var ok = await pool.GetPageCountAsync(input).WaitAsync(PoolFixture.TestTimeout);
        Assert.Equal(1, ok.Value);
    }

    /// <summary>The fault fires once per worker process and every attempt gets a fresh worker, so every attempt crashes.</summary>
    [Fact]
    public async Task WorkerCrash_UsesEveryAttempt()
    {
        string input = PoolFixture.Input("doc-1-page.pdf");
        string crashing = Path.Combine(_output, "crash-thrice.pdf");
        File.Copy(input, crashing);

        await using var pool = await CreateAsync(o =>
        {
            o.MaxAttempts = 3;
            o.WorkerEnvironment["PDFIUMWRAPPER_TEST_FAULT"] = "crash:crash-thrice";
        });

        var result = await pool.GetPageCountAsync(crashing).WaitAsync(PoolFixture.TestTimeout);
        Assert.Equal(PdfJobStatus.WorkerCrashed, result.Status);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(2, pool.Statistics.JobsRetried);
    }
    [Fact]
    public async Task HungWorker_TimesOut_IsReplaced_AndOtherJobsProceed()
    {
        string input = PoolFixture.Input("doc-1-page.pdf");
        string hanging = Path.Combine(_output, "hang-me.pdf");
        File.Copy(input, hanging);

        await using var pool = await CreateAsync(o =>
        {
            o.MinWorkers = 2;
            o.MaxWorkers = 2;
            o.MaxAttempts = 1;
            o.JobTimeout = TimeSpan.FromSeconds(2);
            o.WorkerEnvironment["PDFIUMWRAPPER_TEST_FAULT"] = "hang:hang-me";
        });

        var hung = pool.GetPageCountAsync(hanging);
        var others = Enumerable.Range(0, 4).Select(_ => pool.GetPageCountAsync(input)).ToArray();

        var result = await hung.WaitAsync(PoolFixture.TestTimeout);
        Assert.Equal(PdfJobStatus.TimedOut, result.Status);
        Assert.True(result.Timings.Processing >= TimeSpan.FromSeconds(1.5), $"processing {result.Timings.Processing}");
        Assert.NotEmpty(Events(PdfPoolEventKind.JobTimedOut));

        foreach (var other in await Task.WhenAll(others).WaitAsync(PoolFixture.TestTimeout))
            Assert.Equal(1, other.Value);

        // The hung worker was replaced: the pool is back at MinWorkers and serving.
        await WaitUntilAsync(() => pool.Workers == 2, TimeSpan.FromSeconds(30));
        Assert.Equal(1, (await pool.GetPageCountAsync(input)).Value);
    }

    [Fact]
    public async Task GarbageFromAWorker_KillsThatWorker_AndTheJobIsRetried()
    {
        string input = PoolFixture.Input("doc-1-page.pdf");
        string garbage = Path.Combine(_output, "garbage.pdf");
        File.Copy(input, garbage);

        await using var pool = await CreateAsync(o =>
        {
            o.MaxAttempts = 2;
            o.JobTimeout = TimeSpan.FromSeconds(10);
            o.WorkerEnvironment["PDFIUMWRAPPER_TEST_FAULT"] = "garbage:garbage";
        });

        var result = await pool.GetPageCountAsync(garbage).WaitAsync(PoolFixture.TestTimeout);
        Assert.NotEqual(PdfJobStatus.Succeeded, result.Status);
        Assert.Equal(2, result.Attempts);
        Assert.True(pool.Statistics.WorkersCrashed >= 1 || pool.Statistics.JobsTimedOut >= 1);

        Assert.Equal(1, (await pool.GetPageCountAsync(input).WaitAsync(PoolFixture.TestTimeout)).Value);
    }

    [Fact]
    public async Task CancelQueuedAndInFlight_ReportsCancelled_WithoutPartialOutput()
    {
        await using var pool = await CreateAsync(o => { o.MinWorkers = 1; o.MaxWorkers = 1; });
        string big = PoolFixture.Input("presentation.pdf");

        using var inFlight = new CancellationTokenSource();
        using var queued = new CancellationTokenSource();
        var first = pool.ConvertToPngAsync(big, Path.Combine(_output, "cancel-inflight"), dpi: 200, ct: inFlight.Token);
        var second = pool.ConvertToPngAsync(big, Path.Combine(_output, "cancel-queued"), dpi: 200, ct: queued.Token);

        await WaitUntilAsync(() => pool.BusyWorkers == 1, TimeSpan.FromSeconds(30));
        queued.Cancel();
        await Task.Delay(300);
        inFlight.Cancel();

        var firstResult = await first.WaitAsync(PoolFixture.TestTimeout);
        var secondResult = await second.WaitAsync(PoolFixture.TestTimeout);
        Assert.Equal(PdfJobStatus.Cancelled, firstResult.Status);
        Assert.Equal(PdfJobStatus.Cancelled, secondResult.Status);
        Assert.False(Directory.Exists(Path.Combine(_output, "cancel-queued")));
        if (Directory.Exists(Path.Combine(_output, "cancel-inflight")))
            Assert.Empty(Directory.GetFiles(Path.Combine(_output, "cancel-inflight"), "*.tmp"));

        // The worker survived the cancel and keeps working.
        Assert.Equal(1, (await pool.GetPageCountAsync(PoolFixture.Input("doc-1-page.pdf")).WaitAsync(PoolFixture.TestTimeout)).Value);
        Assert.Equal(2, pool.Statistics.JobsCancelled);
    }

    [Fact]
    public async Task Backpressure_SubmitWaitsWhenTheQueueIsFull()
    {
        string hanging = Path.Combine(_output, "hang-queue.pdf");
        File.Copy(PoolFixture.Input("doc-1-page.pdf"), hanging);

        await using var pool = await CreateAsync(o =>
        {
            o.MinWorkers = 1;
            o.MaxWorkers = 1;
            o.QueueCapacity = 2;
            o.MaxAttempts = 1;
            o.JobTimeout = TimeSpan.FromSeconds(20);
            o.WorkerEnvironment["PDFIUMWRAPPER_TEST_FAULT"] = "hang:hang-queue";
        });

        // Blocks the only worker; the next two fill the queue; the fourth must wait.
        var blocker = pool.GetPageCountAsync(hanging);
        await WaitUntilAsync(() => pool.BusyWorkers == 1, TimeSpan.FromSeconds(30));
        var a = pool.GetPageCountAsync(PoolFixture.Input("doc-1-page.pdf"));
        var b = pool.GetPageCountAsync(PoolFixture.Input("doc-1-page.pdf"));
        await WaitUntilAsync(() => pool.QueuedJobs == 2, TimeSpan.FromSeconds(5));

        var submitStarted = Stopwatch.GetTimestamp();
        var submitting = Task.Run(() => pool.GetPageCountAsync(PoolFixture.Input("doc-1-page.pdf")));
        await Task.Delay(500);
        Assert.False(submitting.IsCompleted, "the fourth submission should still be waiting for a queue slot");
        Assert.NotEmpty(Events(PdfPoolEventKind.QueueFull));

        var all = await Task.WhenAll(blocker, a, b, submitting).WaitAsync(PoolFixture.TestTimeout);
        Assert.Equal(PdfJobStatus.TimedOut, all[0].Status);
        Assert.All(all.Skip(1), r => Assert.Equal(1, r.Value));
        Assert.True(Stopwatch.GetElapsedTime(submitStarted) > TimeSpan.FromMilliseconds(400));
    }

    /// <summary>A submission cancelled while it waits for a queue slot is a Cancelled result, like any other cancellation.</summary>
    [Fact]
    public async Task CancelWhileWaitingForAQueueSlot_ReportsCancelled_NotAnException()
    {
        string hanging = Path.Combine(_output, "hang-admission.pdf");
        File.Copy(PoolFixture.Input("doc-1-page.pdf"), hanging);
        string input = PoolFixture.Input("doc-1-page.pdf");

        await using var pool = await CreateAsync(o =>
        {
            o.MinWorkers = 1;
            o.MaxWorkers = 1;
            o.QueueCapacity = 1;
            o.MaxAttempts = 1;
            o.JobTimeout = TimeSpan.FromSeconds(10);
            o.WorkerEnvironment["PDFIUMWRAPPER_TEST_FAULT"] = "hang:hang-admission";
        });

        var blocker = pool.GetPageCountAsync(hanging);
        await WaitUntilAsync(() => pool.BusyWorkers == 1, TimeSpan.FromSeconds(30));
        var queued = pool.GetPageCountAsync(input);
        await WaitUntilAsync(() => pool.QueuedJobs == 1, TimeSpan.FromSeconds(5));

        using var waiting = new CancellationTokenSource();
        var waitingForSlot = pool.GetPageCountAsync(PdfInput.FromBytes(File.ReadAllBytes(input), "waiting.pdf"), waiting.Token);
        await WaitUntilAsync(() => Events(PdfPoolEventKind.QueueFull).Length > 0, TimeSpan.FromSeconds(5));
        Assert.False(waitingForSlot.IsCompleted);
        waiting.Cancel();

        var cancelled = await waitingForSlot.WaitAsync(PoolFixture.TestTimeout);
        Assert.Equal(PdfJobStatus.Cancelled, cancelled.Status);
        Assert.Equal("waiting.pdf", cancelled.Input);
        Assert.Equal(0, cancelled.Attempts);

        // Cancelled before the input was even spooled: the same status.
        var neverStarted = await pool.GetPageCountAsync(PdfInput.FromBytes(File.ReadAllBytes(input), "never.pdf"), new CancellationToken(canceled: true));
        Assert.Equal(PdfJobStatus.Cancelled, neverStarted.Status);
        Assert.Equal(2, pool.Statistics.JobsCancelled);
        Assert.NotEmpty(Events(PdfPoolEventKind.JobCancelled));

        var rest = await Task.WhenAll(blocker, queued).WaitAsync(PoolFixture.TestTimeout);
        Assert.Equal(PdfJobStatus.TimedOut, rest[0].Status);
        Assert.Equal(1, rest[1].Value);
    }

    /// <summary>A batch never has more documents spooled, queued or running than the pool can hold, however large it is.</summary>
    [Fact]
    public async Task Batch_BoundsDocumentsInFlight_ToTheQueueAndTheSlots()
    {
        string hanging = Path.Combine(_output, "hang-batch.pdf");
        File.Copy(PoolFixture.Input("doc-1-page.pdf"), hanging);
        byte[] bytes = File.ReadAllBytes(PoolFixture.Input("doc-1-page.pdf"));
        string spool = Path.Combine(_output, "spool");
        Directory.CreateDirectory(spool);

        await using var pool = await CreateAsync(o =>
        {
            o.MinWorkers = 1;
            o.MaxWorkers = 1;
            o.QueueCapacity = 2;
            o.MaxAttempts = 1;
            o.JobTimeout = TimeSpan.FromSeconds(8);
            o.TempDirectory = spool;
            o.WorkerEnvironment["PDFIUMWRAPPER_TEST_FAULT"] = "hang:hang-batch";
        });

        var blocker = pool.GetPageCountAsync(hanging);
        await WaitUntilAsync(() => pool.BusyWorkers == 1, TimeSpan.FromSeconds(30));

        var inputs = Enumerable.Range(0, 40).Select(i => PdfInput.FromBytes(bytes, $"doc-{i}.pdf")).ToArray();
        int maxSpooled = 0, maxQueued = 0;
        var results = new List<PdfJobResult<int>>();
        var batch = Task.Run(async () =>
        {
            await foreach (var r in pool.GetPageCountAsync(inputs).WithCancellation(new CancellationTokenSource(PoolFixture.TestTimeout).Token))
                results.Add(r);
        });

        // While the only worker hangs: QueueCapacity queued plus one waiting for a slot (QueuedJobs counts
        // both), and no other input materialized.
        for (int i = 0; i < 20; i++)
        {
            maxSpooled = Math.Max(maxSpooled, Directory.GetFiles(spool, "*.pdf", SearchOption.AllDirectories).Length);
            maxQueued = Math.Max(maxQueued, pool.QueuedJobs);
            await Task.Delay(100);
        }

        Assert.True(maxSpooled <= 3, $"{maxSpooled} inputs spooled with a bound of 3");
        Assert.True(maxQueued <= 3, $"{maxQueued} jobs queued or waiting with a capacity of 2");

        await batch.WaitAsync(PoolFixture.TestTimeout);
        Assert.Equal(PdfJobStatus.TimedOut, (await blocker).Status);
        Assert.Equal(40, results.Count);
        Assert.All(results, r => Assert.Equal(1, r.Value));
        Assert.Empty(Directory.GetFiles(spool, "*.pdf", SearchOption.AllDirectories));
    }

    /// <summary>Generated output names are reserved against the originals: "report", "report", "report-2" get three paths.</summary>
    [Fact]
    public async Task Batch_OutputNames_NeverCollide()
    {
        await using var pool = await CreateAsync();
        byte[] one = File.ReadAllBytes(PoolFixture.Input("doc-1-page.pdf"));
        byte[] three = File.ReadAllBytes(PoolFixture.Input("doc-3-pages-with-comments.pdf"));
        var inputs = new[]
        {
            PdfInput.FromBytes(one, "report.pdf"),
            PdfInput.FromBytes(three, "report.pdf"),
            PdfInput.FromBytes(one, "report-2.pdf"),
            PdfInput.FromBytes(three, "REPORT.pdf"),
        };

        var results = new List<PdfJobResult<TiffFile>>();
        await foreach (var r in pool.ConvertToTiffAsync(inputs, Path.Combine(_output, "names"), dpi: 30).WithCancellation(new CancellationTokenSource(PoolFixture.TestTimeout).Token))
            results.Add(r);

        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.True(r.IsSuccess, r.Error));
        var paths = results.Select(r => r.Value!.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(4, paths.Length);
        Assert.Equal(4, Directory.GetFiles(Path.Combine(_output, "names"), "*.tiff").Length);

        // Each file still has the size its own job wrote, so no job wrote over another's output.
        foreach (var r in results)
            Assert.Equal(r.Value!.Bytes, new FileInfo(r.Value.Path).Length);
        Assert.Equal(new[] { 1, 1, 3, 3 }, results.Select(r => r.Value!.PageCount).OrderBy(p => p));
    }

    /// <summary>
    /// Names are reserved as the file system resolves them, not as written: on Windows "a", "a " and
    /// "a." are one directory, and "..." would be the root's parent. Every document gets its own
    /// directory directly under the root.
    /// </summary>
    [Fact]
    public void BatchOutputPaths_AreDistinct_AndDirectlyUnderTheRoot()
    {
        string root = Path.Combine(_output, "root");
        string[] names = { "a.pdf", "a .pdf", "a..pdf", "A.pdf", "...pdf", "..pdf", ".pdf", "", "a-2.pdf", "dir/b.pdf", "b.pdf", "x:y.pdf", "café.pdf", "café.pdf" };
        var outputs = PdfProcessingPool.OutputsFor(root, names.Select(n => PdfInput.FromBytes(Array.Empty<byte>(), n)).ToArray());

        Assert.Equal(names.Length, outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(outputs, o =>
        {
            Assert.Equal(Path.GetFullPath(root), Path.GetDirectoryName(o));
            Assert.Equal(o, Path.GetFullPath(o!));                       // nothing left for the file system to normalize
            Assert.True(Path.GetFileName(o)!.IndexOfAny(Path.GetInvalidFileNameChars()) < 0, o);
        });
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "a"), outputs[0]);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "document"), outputs[1]);   // "a " ends in a space
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "A-2"), outputs[3]);        // "a" is taken, whatever the case
    }

    /// <summary>The same names through a real batch: each document's pages in its own directory, nothing outside the root.</summary>
    [Fact]
    public async Task Batch_NamesThatResolveAlike_NeverShareADirectory()
    {
        await using var pool = await CreateAsync(o => { o.MinWorkers = 2; o.MaxWorkers = 2; });
        byte[] one = File.ReadAllBytes(PoolFixture.Input("doc-1-page.pdf"));
        byte[] three = File.ReadAllBytes(PoolFixture.Input("doc-3-pages-with-comments.pdf"));
        byte[] ten = File.ReadAllBytes(PoolFixture.Input("contract.pdf"));
        var inputs = new[]
        {
            PdfInput.FromBytes(one, "a.pdf"),
            PdfInput.FromBytes(three, "a .pdf"),
            PdfInput.FromBytes(ten, "a..pdf"),
            PdfInput.FromBytes(three, "...pdf"),
            PdfInput.FromBytes(one, "A.pdf"),
        };
        string parent = Path.Combine(_output, "resolve");
        string root = Path.Combine(parent, "out");

        var results = new List<PdfJobResult<ImageFiles>>();
        await foreach (var r in pool.ConvertToPngAsync(inputs, root, dpi: 20).WithCancellation(new CancellationTokenSource(PoolFixture.TestTimeout).Token))
            results.Add(r);

        Assert.Equal(5, results.Count);
        Assert.All(results, r => Assert.True(r.IsSuccess, r.Error));
        var directories = results.Select(r => Path.GetDirectoryName(r.Value!.Files[0])!).ToArray();
        Assert.Equal(5, directories.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var r in results)
        {
            string directory = Path.GetDirectoryName(r.Value!.Files[0])!;
            Assert.Equal(root, Path.GetDirectoryName(directory));
            Assert.Equal(r.Value.PageCount, Directory.GetFiles(directory, "*.png").Length);   // only this document's pages
        }

        Assert.Equal(new[] { 1, 1, 3, 3, 10 }, results.Select(r => r.Value!.PageCount).OrderBy(p => p));
        Assert.Equal(new[] { "out" }, Directory.GetFileSystemEntries(parent).Select(p => Path.GetFileName(p)));
    }

    /// <summary>
    /// Leaving a batch early cancels what it had submitted and stops submitting: no job keeps a
    /// worker busy or writes output after the caller has gone.
    /// </summary>
    [Fact]
    public async Task Batch_LeftEarly_CancelsItsJobs_AndSubmitsNoMore()
    {
        await using var pool = await CreateAsync(o => { o.MinWorkers = 1; o.MaxWorkers = 1; o.QueueCapacity = 4; });
        var inputs = Enumerable.Repeat(PdfInput.FromFile(PoolFixture.Input("doc-1-page.pdf")), 200).ToArray();
        string root = Path.Combine(_output, "early");

        await foreach (var r in pool.ConvertToPngAsync(inputs, root, dpi: 20).WithCancellation(new CancellationTokenSource(PoolFixture.TestTimeout).Token))
        {
            Assert.True(r.IsSuccess, r.Error);
            break;
        }

        await WaitUntilAsync(() => pool.QueuedJobs == 0 && pool.RunningJobs == 0, TimeSpan.FromSeconds(30));
        var settled = pool.Statistics;
        Assert.True(settled.JobsSubmitted <= 10, $"{settled.JobsSubmitted} jobs submitted with a bound of 5");
        Assert.True(settled.JobsCancelled >= 1, "nothing was cancelled");
        Assert.Equal(settled.JobsSubmitted, settled.JobsSucceeded + settled.JobsCancelled);

        int directories = Directory.GetDirectories(root).Length;
        await Task.Delay(1000);
        Assert.Equal(settled.JobsSubmitted, pool.Statistics.JobsSubmitted);
        Assert.Equal(directories, Directory.GetDirectories(root).Length);
    }

    /// <summary>An image job that fails part-way leaves nothing: no pages already written, no temp file.</summary>
    [Fact]
    public async Task FailedImageJob_LeavesNoPartialOutput()
    {
        await using var pool = await CreateAsync();
        string directory = Path.Combine(_output, "partial");
        Directory.CreateDirectory(Path.Combine(directory, "page_002.png"));   // the second page cannot be moved into place

        var result = await pool.ConvertToPngAsync(PoolFixture.Input("doc-3-pages-with-comments.pdf"), directory, dpi: 30).WaitAsync(PoolFixture.TestTimeout);

        Assert.Equal(PdfJobStatus.Failed, result.Status);
        Assert.False(File.Exists(Path.Combine(directory, "page_001.png")), "the first page was left behind");
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        Assert.Empty(Directory.GetFiles(directory, "*.png"));
    }

    /// <summary>A worker that dies mid-job cannot clean up; the coordinator removes the pages it reported.</summary>
    [Fact]
    public async Task WorkerCrashMidJob_LeavesNoPartialOutput()
    {
        string crashing = Path.Combine(_output, "crash-mid.pdf");
        File.Copy(PoolFixture.Input("contract.pdf"), crashing);

        await using var pool = await CreateAsync(o =>
        {
            o.MaxAttempts = 1;
            o.WorkerEnvironment["PDFIUMWRAPPER_TEST_FAULT"] = "crash-after-page-4:crash-mid";
        });

        string directory = Path.Combine(_output, "crash-mid");
        var result = await pool.ConvertToPngAsync(crashing, directory, dpi: 30).WaitAsync(PoolFixture.TestTimeout);

        Assert.Equal(PdfJobStatus.WorkerCrashed, result.Status);
        Assert.Empty(Directory.GetFiles(directory, "*.png"));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));

        // A retry starts clean too: the second attempt succeeds with exactly the document's pages.
        await using var retrying = await CreateAsync(o =>
        {
            o.MaxAttempts = 2;
            o.WorkerEnvironment["PDFIUMWRAPPER_TEST_FAULT"] = "crash-after-page-4:crash-mid";
        });
        string again = Path.Combine(_output, "crash-mid-retry");
        string survives = Path.Combine(_output, "survives.pdf");
        File.Copy(PoolFixture.Input("contract.pdf"), survives);
        var crashed = await retrying.ConvertToPngAsync(crashing, again, dpi: 30).WaitAsync(PoolFixture.TestTimeout);
        Assert.Equal(PdfJobStatus.WorkerCrashed, crashed.Status);  // the fault fires in every fresh worker
        Assert.Equal(2, crashed.Attempts);
        Assert.Empty(Directory.GetFiles(again, "*.png"));
        var ok = await retrying.ConvertToPngAsync(survives, Path.Combine(_output, "survives"), dpi: 30).WaitAsync(PoolFixture.TestTimeout);
        Assert.True(ok.IsSuccess, ok.Error);
        Assert.Equal(10, ok.Value!.Files.Count);
    }

    /// <summary>
    /// Output that was in the directory before a job is never the job's to delete: a job cancelled
    /// before submission, one whose input is missing, one whose worker was killed before it wrote
    /// anything, and one that failed part-way all leave the existing file byte for byte.
    /// </summary>
    [Fact]
    public async Task CleanupNeverTouchesOutputTheJobDidNotWrite()
    {
        string hanging = Path.Combine(_output, "hang-seeded.pdf");
        File.Copy(PoolFixture.Input("doc-1-page.pdf"), hanging);
        string directory = Path.Combine(_output, "seeded");
        Directory.CreateDirectory(directory);
        byte[] seed = { 0x53, 0x45, 0x45, 0x44 };
        string existing = Path.Combine(directory, "page_001.png");
        File.WriteAllBytes(existing, seed);
        File.WriteAllBytes(Path.Combine(directory, "page_002.png"), seed);

        await using var pool = await CreateAsync(o =>
        {
            o.MaxAttempts = 1;
            o.JobTimeout = TimeSpan.FromSeconds(3);
            o.WorkerEnvironment["PDFIUMWRAPPER_TEST_FAULT"] = "hang:hang-seeded";
        });
        byte[] bytes = File.ReadAllBytes(PoolFixture.Input("doc-1-page.pdf"));

        var beforeSubmission = await pool.ConvertToPngAsync(PdfInput.FromBytes(bytes, "early.pdf"), directory, dpi: 30, ct: new CancellationToken(canceled: true));
        Assert.Equal(PdfJobStatus.Cancelled, beforeSubmission.Status);
        Assert.Equal(seed, File.ReadAllBytes(existing));

        var missingInput = await pool.ConvertToPngAsync(Path.Combine(_output, "missing.pdf"), directory, dpi: 30).WaitAsync(PoolFixture.TestTimeout);
        Assert.Equal(PdfJobStatus.Failed, missingInput.Status);
        Assert.Equal(seed, File.ReadAllBytes(existing));

        var killedBeforeWriting = await pool.ConvertToPngAsync(hanging, directory, dpi: 30).WaitAsync(PoolFixture.TestTimeout);
        Assert.Equal(PdfJobStatus.TimedOut, killedBeforeWriting.Status);
        Assert.Equal(seed, File.ReadAllBytes(existing));

        // A job that fails while moving its pages into place owns those names by then: it takes the
        // pages it moved with it (the seed under page_001 was overwritten first, as a success would
        // have done) and leaves no staged file.
        Directory.CreateDirectory(Path.Combine(directory, "page_003.png"));   // the third page cannot be moved into place
        var failedPartWay = await pool.ConvertToPngAsync(PoolFixture.Input("doc-3-pages-with-comments.pdf"), directory, dpi: 30).WaitAsync(PoolFixture.TestTimeout);
        Assert.Equal(PdfJobStatus.Failed, failedPartWay.Status);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        Assert.Empty(Directory.GetFiles(directory, "*.png"));

        // And a successful job replaces them, as it should.
        var ok = await pool.ConvertToPngAsync(PoolFixture.Input("doc-1-page.pdf"), Path.Combine(_output, "seeded-ok"), dpi: 30).WaitAsync(PoolFixture.TestTimeout);
        Assert.True(ok.IsSuccess, ok.Error);
    }

    /// <summary>Cancelling CreateAsync while a worker starts fails the creation and kills the child.</summary>
    [Fact]
    public async Task CancelledStart_FailsCreateAsync_AndKillsTheChild()
    {
        string pidFile = Path.Combine(_output, "worker.pid");
        var options = PoolFixture.Options(o =>
        {
            o.WorkerEnvironment["PDFIUMWRAPPER_TEST_FAULT"] = "slow-start:20000";
            o.WorkerEnvironment["PDFIUMWRAPPER_TEST_PIDFILE"] = pidFile;
        });

        using var cts = new CancellationTokenSource();
        var creating = PdfProcessingPool.CreateAsync(options, cts.Token);
        await WaitUntilAsync(() => File.Exists(pidFile) && File.ReadAllText(pidFile).Trim().Length > 0, TimeSpan.FromSeconds(30));
        int pid = int.Parse(File.ReadAllText(pidFile).Trim());
        Assert.True(IsRunning(pid));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creating.WaitAsync(TimeSpan.FromSeconds(30)));
        await WaitUntilAsync(() => !IsRunning(pid), TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task InvalidWorkerPath_IsReportedByTheConstructor()
    {
        var ex = Assert.Throws<PdfPoolException>(() => new PdfProcessingPool(PoolFixture.Options(o => o.WorkerPath = Path.Combine(_output, "nope.dll"))));
        Assert.Contains("nope.dll", ex.Message);
    }

    [Fact]
    public async Task Dispose_CancelsQueuedJobs_AndStopsEveryWorker()
    {
        var pool = await CreateAsync(o => { o.MinWorkers = 2; o.MaxWorkers = 2; o.JobTimeout = TimeSpan.FromSeconds(30); });
        var pids = new HashSet<int>();
        string big = PoolFixture.Input("presentation.pdf");
        var running = Enumerable.Range(0, 2).Select(i => pool.ConvertToPngAsync(big, Path.Combine(_output, $"dispose-{i}"), dpi: 150)).ToArray();
        await WaitUntilAsync(() => pool.BusyWorkers == 2, TimeSpan.FromSeconds(30));
        var queuedJobs = Enumerable.Range(0, 3).Select(_ => pool.GetPageCountAsync(big)).ToArray();
        lock (_events) pids.UnionWith(_events.Where(e => e.WorkerPid > 0).Select(e => e.WorkerPid));

        await pool.DisposeAsync().AsTask().WaitAsync(PoolFixture.TestTimeout);

        foreach (var queued in await Task.WhenAll(queuedJobs))
            Assert.Equal(PdfJobStatus.Cancelled, queued.Status);
        foreach (var r in await Task.WhenAll(running))
            Assert.True(r.Status is PdfJobStatus.Succeeded or PdfJobStatus.Cancelled, r.Status.ToString());
        foreach (var pid in pids)
            Assert.False(IsRunning(pid), $"worker {pid} is still running");
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.GetPageCountAsync(big));
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        long start = Stopwatch.GetTimestamp();
        while (!condition())
        {
            Assert.True(Stopwatch.GetElapsedTime(start) < timeout, "condition not met in time");
            await Task.Delay(50);
        }
    }
}
