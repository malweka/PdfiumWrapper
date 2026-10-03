using System.Diagnostics;
using PdfiumWrapper.Processing;

namespace PdfiumWrapper.Tests.Processing;

/// <summary>
/// Failures of the pool's own machinery: workers that stop starting, timeouts at the limit of .NET
/// timers, a dispatcher that dies, disposal with a retry pending. Every job still ends with a status.
/// </summary>
[Collection(PdfTestCollection.Name)]
public class PoolFailureTests : IDisposable
{
    private readonly string _output = PoolFixture.TempDirectory();
    private readonly List<PdfPoolEvent> _events = new();

    public void Dispose() => PoolFixture.DeleteDirectory(_output);

    private async Task<PdfProcessingPool> CreateAsync(Action<PdfPoolOptions> configure)
    {
        var pool = await PdfProcessingPool.CreateAsync(PoolFixture.Options(configure)).WaitAsync(PoolFixture.TestTimeout);
        pool.Events += (_, e) => { lock (_events) _events.Add(e); };
        return pool;
    }

    private PdfPoolEvent[] Events(PdfPoolEventKind kind)
    {
        lock (_events) return _events.Where(e => e.Kind == kind).ToArray();
    }

    private static Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout) => PdfProcessingPoolTests.WaitUntilAsync(condition, timeout);

    private static void Kill(int pid)
    {
        using var process = Process.GetProcessById(pid);
        process.Kill();
    }

    /// <summary>
    /// The pool exists, its only worker dies, and no replacement can start: the waiting job ends as
    /// Failed with the start error instead of waiting forever. Once starts work again, so does the pool.
    /// </summary>
    [Fact]
    public async Task WorkersThatCannotStart_FailWaitingJobs_AndThePoolRecovers()
    {
        string marker = Path.Combine(_output, "no-start");
        string input = PoolFixture.Input("doc-1-page.pdf");
        await using var pool = await CreateAsync(o =>
        {
            o.MinWorkers = 1;
            o.MaxWorkers = 1;
            o.MaxConsecutiveStartFailures = 2;
            o.WorkerEnvironment["PDFIUMWRAPPER_TEST_FAULT"] = "fail-start-if-exists:" + marker;
        });

        var first = await pool.GetPageCountAsync(input).WaitAsync(PoolFixture.TestTimeout);
        Assert.Equal(1, first.Value);

        File.WriteAllText(marker, "");
        Kill(first.WorkerPid);
        // Submit only once the pool has seen the exit: before that, the job can still be sent to the
        // dead worker (seen on Linux) and come back with a crashed attempt instead of none.
        await WaitUntilAsync(() => pool.Statistics.WorkersStopped >= 1, TimeSpan.FromSeconds(10));

        var stranded = await pool.GetPageCountAsync(input).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(PdfJobStatus.Failed, stranded.Status);
        Assert.Contains("no worker could be started", stranded.Error);
        Assert.Equal(0, stranded.Attempts);
        Assert.True(Events(PdfPoolEventKind.WorkerStartFailed).Length >= 2);
        await WaitUntilAsync(() => pool.QueuedJobs == 0, TimeSpan.FromSeconds(10));

        // Starts work again: the next job waits for one and runs.
        await WaitUntilAsync(() => pool.Workers == 0, TimeSpan.FromSeconds(30));
        File.Delete(marker);
        var recovered = await pool.GetPageCountAsync(input).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(1, recovered.Value);
    }

    /// <summary>
    /// Every worker of a full pool dies at once: each exit handler starts replacements, and together
    /// they must start exactly as many as were lost, never more than MaxWorkers.
    /// </summary>
    [Fact]
    public async Task SimultaneousExits_ReplaceEachWorkerOnce()
    {
        await using var pool = await CreateAsync(o =>
        {
            o.MinWorkers = 2;
            o.MaxWorkers = 2;
        });

        for (int round = 0; round < 5; round++)
        {
            await WaitUntilAsync(() => pool.WorkerPidsForTests.Length == 2 && pool.Workers == 2, TimeSpan.FromSeconds(60));
            int[] pids = pool.WorkerPidsForTests;
            int readyBefore = Events(PdfPoolEventKind.WorkerReady).Length;

            // Kill both together, so their exit handlers run concurrently
            using var go = new ManualResetEventSlim();
            var killers = pids.Select(pid => Task.Run(() => { go.Wait(); Kill(pid); })).ToArray();
            go.Set();
            await Task.WhenAll(killers);

            int most = 0;
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(3) || Events(PdfPoolEventKind.WorkerReady).Length < readyBefore + 2)
            {
                most = Math.Max(most, pool.Workers);
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), "the lost workers were not replaced");
                await Task.Delay(10);
            }

            Assert.True(most <= 2, $"round {round}: {most} workers alive or starting");
            Assert.Equal(readyBefore + 2, Events(PdfPoolEventKind.WorkerReady).Length);
            Assert.DoesNotContain(pool.WorkerPidsForTests, pids.Contains);
        }

        var result = await pool.GetPageCountAsync(PoolFixture.Input("doc-1-page.pdf")).WaitAsync(PoolFixture.TestTimeout);
        Assert.Equal(1, result.Value);
    }

    [Fact]
    public void Timeouts_BeyondTimerLimits_AreRejected()
    {
        foreach (var tooLong in new[] { TimeSpan.MaxValue, TimeSpan.FromDays(60), PdfPoolOptions.MaxTimeout + TimeSpan.FromMilliseconds(1) })
        {
            var job = Assert.Throws<ArgumentOutOfRangeException>(() => new PdfProcessingPool(PoolFixture.Options(o => o.JobTimeout = tooLong)));
            Assert.Equal(nameof(PdfPoolOptions.JobTimeout), job.ParamName);
            var start = Assert.Throws<ArgumentOutOfRangeException>(() => new PdfProcessingPool(PoolFixture.Options(o => o.WorkerStartTimeout = tooLong)));
            Assert.Equal(nameof(PdfPoolOptions.WorkerStartTimeout), start.ParamName);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfProcessingPool(PoolFixture.Options(o => o.MaxConsecutiveStartFailures = 0)));
    }

    /// <summary>The longest timeouts accepted work: jobs run, and disposal completes.</summary>
    [Fact]
    public async Task Timeouts_AtTheLimit_Work()
    {
        var pool = await CreateAsync(o =>
        {
            o.JobTimeout = PdfPoolOptions.MaxTimeout;
            o.WorkerStartTimeout = PdfPoolOptions.MaxTimeout;
        });

        var result = await pool.GetPageCountAsync(PoolFixture.Input("doc-1-page.pdf")).WaitAsync(PoolFixture.TestTimeout);
        Assert.Equal(1, result.Value);
        await pool.DisposeAsync().AsTask().WaitAsync(PoolFixture.TestTimeout);
    }

    /// <summary>Disposing while a crashed job waits for its retry (no worker can start) completes and cancels it.</summary>
    [Fact]
    public async Task Dispose_WithARetryPending_Completes()
    {
        string marker = Path.Combine(_output, "no-start");
        string slow = PoolFixture.CopyAs("contract.pdf", _output, "slow-retry");
        string directory = Path.Combine(_output, "retry");
        var pool = await CreateAsync(o =>
        {
            o.MinWorkers = 1;
            o.MaxWorkers = 1;
            o.MaxAttempts = 2;
            o.MaxConsecutiveStartFailures = 1_000;
            o.WorkerEnvironment["PDFIUMWRAPPER_TEST_FAULT"] = $"fail-start-if-exists:{marker};sleep-after-page-500:slow-retry";
        });

        var job = pool.ConvertToPngAsync(slow, directory, dpi: 20);
        await WaitUntilAsync(() => Events(PdfPoolEventKind.JobDispatched).Length > 0, TimeSpan.FromSeconds(30));
        File.WriteAllText(marker, "");
        Kill(Events(PdfPoolEventKind.JobDispatched)[0].WorkerPid);

        await WaitUntilAsync(() => Events(PdfPoolEventKind.JobRetried).Length > 0 && pool.QueuedJobs == 1, TimeSpan.FromSeconds(30));
        await pool.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));

        var result = await job.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(PdfJobStatus.Cancelled, result.Status);
        Assert.Equal("the pool was disposed", result.Error);
        if (Directory.Exists(directory))
            Assert.Empty(Directory.GetFiles(directory));
    }

    /// <summary>
    /// If the dispatcher dies, nothing would ever be dispatched again: every queued and in-flight job,
    /// and every job submitted later, ends as Failed, and disposal still completes.
    /// </summary>
    [Fact]
    public async Task DispatcherFailure_FailsEveryJob_InsteadOfHanging()
    {
        string slow = PoolFixture.CopyAs("contract.pdf", _output, "slow-first");
        string input = PoolFixture.Input("doc-1-page.pdf");
        var pool = await CreateAsync(o =>
        {
            o.MinWorkers = 1;
            o.MaxWorkers = 1;
            o.WorkerEnvironment["PDFIUMWRAPPER_TEST_FAULT"] = "sleep-after-page-300:slow-first";
        });

        var first = pool.ConvertToPngAsync(slow, Path.Combine(_output, "first"), dpi: 20);
        await WaitUntilAsync(() => pool.RunningJobs == 1, TimeSpan.FromSeconds(30));

        // The next dispatch throws on the dispatcher's task, after the job is registered in flight.
        pool.DispatchHookForTests = _ => throw new InvalidOperationException("injected dispatcher failure");
        string secondDirectory = Path.Combine(_output, "second");
        var second = pool.ConvertToPngAsync(PoolFixture.Input("doc-3-pages-with-comments.pdf"), secondDirectory, dpi: 20);
        var third = pool.GetPageCountAsync(input);

        Assert.True((await first.WaitAsync(PoolFixture.TestTimeout)).IsSuccess);
        foreach (var r in new[] { (await second.WaitAsync(PoolFixture.TestTimeout)).Status, (await third.WaitAsync(PoolFixture.TestTimeout)).Status })
            Assert.Equal(PdfJobStatus.Failed, r);
        Assert.Contains("injected dispatcher failure", (await second).Error);

        var later = await pool.GetPageCountAsync(input).WaitAsync(PoolFixture.TestTimeout);
        Assert.Equal(PdfJobStatus.Failed, later.Status);
        Assert.Contains("dispatcher stopped", later.Error);

        await pool.DisposeAsync().AsTask().WaitAsync(PoolFixture.TestTimeout);
        if (Directory.Exists(secondDirectory))
            Assert.Empty(Directory.GetFiles(secondDirectory));
    }
}
