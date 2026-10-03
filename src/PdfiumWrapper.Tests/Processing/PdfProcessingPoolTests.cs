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
