using System.Diagnostics;
using PdfiumWrapper.Processing;

namespace PdfiumWrapper.Tests.Processing;

/// <summary>
/// Two jobs on one worker (JobsPerWorker = 2, the default): a fault in one job, or the worker being
/// retired, must not cost its neighbour an attempt or its result.
/// </summary>
[Collection(PdfTestCollection.Name)]
public class SharedWorkerTests : IDisposable
{
    private readonly string _output = PoolFixture.TempDirectory();
    private readonly List<PdfPoolEvent> _events = new();

    public void Dispose() => PoolFixture.DeleteDirectory(_output);

    private async Task<PdfProcessingPool> CreateAsync(Action<PdfPoolOptions> configure)
    {
        var pool = await PdfProcessingPool.CreateAsync(PoolFixture.SharedWorkerOptions(configure)).WaitAsync(PoolFixture.TestTimeout);
        pool.Events += (_, e) => { lock (_events) _events.Add(e); };
        return pool;
    }

    private PdfPoolEvent[] Events(PdfPoolEventKind kind)
    {
        lock (_events) return _events.Where(e => e.Kind == kind).ToArray();
    }

    private static Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout) => PdfProcessingPoolTests.WaitUntilAsync(condition, timeout);

    /// <summary>
    /// A worker that crashes with two jobs on it cannot say which one did it. Both run again alone,
    /// uncharged; the neighbour then succeeds on its first charged attempt, and only the job that
    /// crashes its worker again is charged, up to MaxAttempts.
    /// </summary>
    [Fact]
    public async Task Crash_DoesNotChargeTheNeighbour()
    {
        string neighbourInput = PoolFixture.CopyAs("contract.pdf", _output, "slow-neighbour");
        string crashing = PoolFixture.CopyAs("doc-1-page.pdf", _output, "crash-me");

        await using var pool = await CreateAsync(o =>
        {
            o.MaxAttempts = 2;
            o.WorkerEnvironment["PDFIUMWRAPPER_TEST_FAULT"] = "crash:crash-me;sleep-after-page-300:slow-neighbour";
        });

        var neighbour = pool.ConvertToPngAsync(neighbourInput, Path.Combine(_output, "neighbour"), dpi: 20);
        await WaitUntilAsync(() => pool.RunningJobs == 1, TimeSpan.FromSeconds(30));
        var crash = pool.GetPageCountAsync(crashing);

        var neighbourResult = await neighbour.WaitAsync(PoolFixture.TestTimeout);
        var crashResult = await crash.WaitAsync(PoolFixture.TestTimeout);

        Assert.True(neighbourResult.IsSuccess, neighbourResult.Error);
        Assert.Equal(1, neighbourResult.Attempts);
        Assert.Equal(10, neighbourResult.Value!.Files.Count);
        Assert.Empty(Directory.GetFiles(Path.Combine(_output, "neighbour"), "*.tmp"));

        Assert.Equal(PdfJobStatus.WorkerCrashed, crashResult.Status);
        Assert.Equal(2, crashResult.Attempts);
        Assert.Equal(2, pool.Statistics.JobsCrashed);  // the crashing job's two charged attempts, nothing for the neighbour
    }

    /// <summary>A worker killed for one job's timeout takes its neighbour down; the neighbour runs again without using an attempt.</summary>
    [Fact]
    public async Task Timeout_DoesNotChargeTheNeighbour()
    {
        string hanging = PoolFixture.CopyAs("doc-1-page.pdf", _output, "hang-me");
        string neighbourInput = PoolFixture.CopyAs("contract.pdf", _output, "held-neighbour");
        Hold(neighbourInput);

        await using var pool = await CreateAsync(o =>
        {
            o.MaxAttempts = 1;
            o.JobTimeout = TimeSpan.FromSeconds(8);
            o.WorkerEnvironment["PDFIUMWRAPPER_TEST_FAULT"] = "hang-after-page-1:hang-me;pause-while-marked:held-neighbour";
        });

        var hung = pool.ConvertToPngAsync(hanging, Path.Combine(_output, "hung"), dpi: 20);
        await WaitForStagedPageAsync(Path.Combine(_output, "hung"));

        // The neighbour starts well after the hung job, so the hung job's timeout is the one that
        // fires, and it is held after its first page, so it is still running when the worker is killed.
        await Task.Delay(TimeSpan.FromSeconds(3));
        var neighbour = pool.ConvertToPngAsync(neighbourInput, Path.Combine(_output, "neighbour"), dpi: 20);
        await WaitForStagedPageAsync(Path.Combine(_output, "neighbour"));

        var hungResult = await hung.WaitAsync(PoolFixture.TestTimeout);
        await WaitUntilAsync(() => Events(PdfPoolEventKind.JobRetried).Any(e => e.Detail!.Contains("without using an attempt")), TimeSpan.FromSeconds(30));
        Release(neighbourInput);
        var neighbourResult = await neighbour.WaitAsync(PoolFixture.TestTimeout);

        Assert.Equal(PdfJobStatus.TimedOut, hungResult.Status);
        Assert.Equal(1, hungResult.Attempts);
        Assert.True(neighbourResult.IsSuccess, $"{neighbourResult.Status}: {neighbourResult.Error}");
        Assert.Equal(1, neighbourResult.Attempts);
        Assert.Equal(10, neighbourResult.Value!.Files.Count);
        Assert.Equal(0, pool.Statistics.JobsCrashed);
    }

    /// <summary>A cancelled job that ignores the cancel gets its worker killed; the neighbour runs again without using an attempt.</summary>
    [Fact]
    public async Task SlowCancel_DoesNotChargeTheNeighbour()
    {
        string hanging = PoolFixture.CopyAs("doc-1-page.pdf", _output, "hang-me");
        string neighbourInput = PoolFixture.CopyAs("contract.pdf", _output, "held-neighbour");
        Hold(neighbourInput);

        await using var pool = await CreateAsync(o =>
        {
            o.MaxAttempts = 1;
            o.WorkerEnvironment["PDFIUMWRAPPER_TEST_FAULT"] = "hang-after-page-1:hang-me;pause-while-marked:held-neighbour";
        });

        using var cancel = new CancellationTokenSource();
        var hung = pool.ConvertToPngAsync(hanging, Path.Combine(_output, "hung"), dpi: 20, ct: cancel.Token);
        // Once its first page is staged the job hangs and never looks at its token again. Cancelled
        // any earlier, it could stop cleanly (or never start) and nothing would be killed.
        await WaitForStagedPageAsync(Path.Combine(_output, "hung"));
        var neighbour = pool.ConvertToPngAsync(neighbourInput, Path.Combine(_output, "neighbour"), dpi: 20);
        await WaitForStagedPageAsync(Path.Combine(_output, "neighbour"));

        // The worker is killed after the 2 s cancel grace, with the neighbour held on it.
        cancel.Cancel();
        var hungResult = await hung.WaitAsync(PoolFixture.TestTimeout);
        await WaitUntilAsync(() => Events(PdfPoolEventKind.JobRetried).Any(e => e.Detail!.Contains("without using an attempt")), TimeSpan.FromSeconds(30));
        Release(neighbourInput);
        var neighbourResult = await neighbour.WaitAsync(PoolFixture.TestTimeout);

        Assert.Equal(PdfJobStatus.Cancelled, hungResult.Status);
        Assert.True(neighbourResult.IsSuccess, $"{neighbourResult.Status}: {neighbourResult.Error}");
        Assert.Equal(1, neighbourResult.Attempts);
        Assert.Equal(10, neighbourResult.Value!.Files.Count);
    }

    /// <summary>
    /// A job that must run alone (its worker crashed with a neighbour) waits for an empty worker, and
    /// nothing behind it is dispatched meanwhile. With every worker partly busy and room for another,
    /// the pool starts one for it instead of leaving it waiting until a busy worker drains.
    /// </summary>
    [Fact]
    public async Task RunAloneJob_GetsANewWorker_WhenEveryWorkerIsPartlyBusy()
    {
        string[] held = ["held-a", "held-b", "held-c"];
        var inputs = held.Select(name => PoolFixture.CopyAs("doc-1-page.pdf", _output, name)).ToArray();
        string crashing = PoolFixture.CopyAs("doc-1-page.pdf", _output, "crash-me");
        foreach (var input in inputs)
            Hold(input);

        await using var pool = await CreateAsync(o =>
        {
            o.MinWorkers = 1;
            o.MaxWorkers = 3;
            o.MaxAttempts = 1;
            o.WorkerEnvironment["PDFIUMWRAPPER_TEST_FAULT"] = "pause-while-marked:held-;crash:crash-me";
        });

        // Two held jobs fill the first worker; the third makes the pool start a second worker for it.
        var jobs = inputs.Select((input, i) => pool.ConvertToPngAsync(input, Path.Combine(_output, held[i]), dpi: 20)).ToArray();
        await WaitUntilAsync(() => pool.RunningJobs == 3 && pool.Workers == 2, TimeSpan.FromSeconds(60));

        // One of the first two finishes: each worker now runs one held job and has a slot free.
        Release(inputs[0]);
        Assert.True((await jobs[0].WaitAsync(PoolFixture.TestTimeout)).IsSuccess);

        // The crashing job joins one of them and takes that worker down: it and the held job it
        // shared the worker with must now each run alone. The other worker stays busy with its held
        // job for as long as the test likes, so only a new worker can serve them.
        var crashResult = await pool.GetPageCountAsync(crashing).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(PdfJobStatus.WorkerCrashed, crashResult.Status);
        Assert.Equal(1, crashResult.Attempts);   // the shared crash was free; crashing alone is charged

        Release(inputs[1]);
        Release(inputs[2]);
        foreach (var job in jobs[1..])
        {
            var result = await job.WaitAsync(PoolFixture.TestTimeout);
            Assert.True(result.IsSuccess, $"{result.Status}: {result.Error}");
            Assert.Equal(1, result.Attempts);
        }
    }

    /// <summary>
    /// Jobs cancelled while the dispatcher is handing them out (as a batch does when its caller stops
    /// early) end Cancelled or run to the end, and every slot they took is given back.
    /// </summary>
    [Fact]
    public async Task CancelledWhileDispatching_LeaksNoSlot()
    {
        string input = PoolFixture.Input("doc-1-page.pdf");
        await using var pool = await CreateAsync(o =>
        {
            o.MinWorkers = 2;
            o.MaxWorkers = 2;
        });

        for (int round = 0; round < 25; round++)
        {
            using var cancel = new CancellationTokenSource();
            var jobs = Enumerable.Range(0, 40).Select(_ => pool.GetPageCountAsync(input, cancel.Token)).ToArray();
            if (round % 5 != 0)
                await Task.Delay(round % 5);
            cancel.Cancel();

            foreach (var result in await Task.WhenAll(jobs).WaitAsync(PoolFixture.TestTimeout))
                Assert.True(result.Status is PdfJobStatus.Succeeded or PdfJobStatus.Cancelled, $"{result.Status}: {result.Error}");
        }

        await WaitUntilAsync(() => pool.SlotsInUseForTests == 0 && pool.RunningJobs == 0 && pool.QueuedJobs == 0, TimeSpan.FromSeconds(30));
        var after = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => pool.GetPageCountAsync(input))).WaitAsync(PoolFixture.TestTimeout);
        Assert.All(after, r => Assert.Equal(1, r.Value));
    }

    /// <summary>
    /// A worker retired for memory takes no new job but lets the ones it is running finish, however
    /// long they take within JobTimeout, and only then shuts down.
    /// </summary>
    [Fact]
    public async Task MemoryRetirement_LetsTheRunningNeighbourFinish()
    {
        string neighbourInput = PoolFixture.CopyAs("contract.pdf", _output, "slow-neighbour");

        await using var pool = await CreateAsync(o =>
        {
            o.MaxAttempts = 1;
            o.MaxWorkerMemoryBytes = 1;    // every worker is over the limit after its first job
            o.WorkerEnvironment["PDFIUMWRAPPER_TEST_FAULT"] = "sleep-after-page-700:slow-neighbour";
        });

        // At least 7 s: longer than the 5 s a retiring worker used to get before it was killed.
        var neighbour = pool.ConvertToPngAsync(neighbourInput, Path.Combine(_output, "neighbour"), dpi: 20);
        await WaitUntilAsync(() => pool.RunningJobs == 1, TimeSpan.FromSeconds(30));

        var quick = await pool.GetPageCountAsync(PoolFixture.Input("doc-1-page.pdf")).WaitAsync(PoolFixture.TestTimeout);
        Assert.Equal(1, quick.Value);
        await WaitUntilAsync(() => Events(PdfPoolEventKind.WorkerRetiredForMemory).Length > 0, TimeSpan.FromSeconds(10));

        var neighbourResult = await neighbour.WaitAsync(PoolFixture.TestTimeout);
        Assert.True(neighbourResult.IsSuccess, $"{neighbourResult.Status}: {neighbourResult.Error}");
        Assert.Equal(1, neighbourResult.Attempts);
        Assert.Equal(quick.WorkerPid, neighbourResult.WorkerPid);   // finished where it started
        Assert.Equal(10, neighbourResult.Value!.Files.Count);
        Assert.Equal(0, pool.Statistics.WorkersCrashed);

        // Then the retired worker shuts down and a fresh one serves.
        await WaitUntilAsync(() => !IsRunning(quick.WorkerPid), TimeSpan.FromSeconds(30));
        var next = await pool.GetPageCountAsync(PoolFixture.Input("doc-1-page.pdf")).WaitAsync(PoolFixture.TestTimeout);
        Assert.Equal(1, next.Value);
        Assert.NotEqual(quick.WorkerPid, next.WorkerPid);
    }

    /// <summary>
    /// MaxWorkers bounds processes, and a worker retired for memory is one until it has drained:
    /// jobs that arrive meanwhile wait for it to go instead of starting a second process.
    /// </summary>
    [Fact]
    public async Task MemoryRetirement_NeverExceedsMaxWorkers()
    {
        string neighbourInput = PoolFixture.CopyAs("contract.pdf", _output, "held-neighbour");
        string neighbourDirectory = Path.Combine(_output, "neighbour");

        await using var pool = await CreateAsync(o =>
        {
            o.MaxAttempts = 1;
            o.MaxWorkerMemoryBytes = 1;    // every worker is over the limit after its first job
            o.WorkerEnvironment["PDFIUMWRAPPER_TEST_FAULT"] = "pause-while-marked:held-neighbour";
        });

        Hold(neighbourInput);
        try
        {
            var neighbour = pool.ConvertToPngAsync(neighbourInput, neighbourDirectory, dpi: 20);
            await WaitForStagedPageAsync(neighbourDirectory);
            var quick = await pool.GetPageCountAsync(PoolFixture.Input("doc-1-page.pdf")).WaitAsync(PoolFixture.TestTimeout);
            Assert.Equal(1, quick.Value);
            await WaitUntilAsync(() => Events(PdfPoolEventKind.WorkerRetiredForMemory).Length > 0, TimeSpan.FromSeconds(10));

            // Work arrives while the retiree drains; give the sizer many times ScaleUpAfter to (wrongly) add a process
            var more = Enumerable.Range(0, 3).Select(_ => pool.GetPageCountAsync(PoolFixture.Input("doc-1-page.pdf"))).ToArray();
            int most = 0;
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(2))
            {
                most = Math.Max(most, pool.Workers);
                await Task.Delay(20);
            }

            Release(neighbourInput);
            var neighbourResult = await neighbour.WaitAsync(PoolFixture.TestTimeout);
            var results = await Task.WhenAll(more).WaitAsync(PoolFixture.TestTimeout);

            Assert.Equal(1, most);
            Assert.True(neighbourResult.IsSuccess, $"{neighbourResult.Status}: {neighbourResult.Error}");
            Assert.All(results, r => Assert.Equal(1, r.Value));
            Assert.All(results, r => Assert.NotEqual(quick.WorkerPid, r.WorkerPid));   // served by the replacement
            Assert.Empty(Events(PdfPoolEventKind.ScaledUp));
        }
        finally
        {
            Release(neighbourInput);
        }
    }

    /// <summary>
    /// When the dispatcher dies, a job running on a worker is failed only after that worker is dead
    /// and what the job staged is gone: the caller never holds Failed while its output can still change.
    /// </summary>
    [Fact]
    public async Task DispatcherFailure_FailsARunningJobOnlyAfterItsWorkerAndOutputAreGone()
    {
        string heldInput = PoolFixture.CopyAs("contract.pdf", _output, "held-job");
        string heldDirectory = Path.Combine(_output, "held");

        await using var pool = await CreateAsync(o =>
        {
            o.WorkerEnvironment["PDFIUMWRAPPER_TEST_FAULT"] = "pause-while-marked:held-job";
        });

        Hold(heldInput);
        try
        {
            var held = pool.ConvertToPngAsync(heldInput, heldDirectory, dpi: 20);
            await WaitForStagedPageAsync(heldDirectory);
            int pid = Events(PdfPoolEventKind.JobDispatched).Single().WorkerPid;

            // The next dispatch, onto the same worker's second slot, kills the dispatcher.
            pool.DispatchHookForTests = _ => throw new InvalidOperationException("injected dispatcher failure");
            var trigger = pool.GetPageCountAsync(PoolFixture.Input("doc-1-page.pdf"));
            var result = await held.WaitAsync(PoolFixture.TestTimeout);

            // Checked as soon as the result is in, before anything else can catch up
            bool stillRunning = IsRunning(pid);
            string[] left = Directory.Exists(heldDirectory) ? Directory.GetFiles(heldDirectory) : [];
            Assert.Equal(PdfJobStatus.Failed, result.Status);
            Assert.Contains("injected dispatcher failure", result.Error);
            Assert.False(stillRunning, "the worker was still running when the job was reported failed");
            Assert.Empty(left);
            Assert.Equal(PdfJobStatus.Failed, (await trigger.WaitAsync(PoolFixture.TestTimeout)).Status);
        }
        finally
        {
            Release(heldInput);
        }
    }

    /// <summary>Holds jobs on this input after each page (fault <c>pause-while-marked</c>) until <see cref="Release"/>.</summary>
    private static void Hold(string input) => File.WriteAllText(input + ".pause", "");

    private static void Release(string input) => File.Delete(input + ".pause");

    /// <summary>Waits until an image job has staged its first page in <paramref name="directory"/>: it is running on a worker.</summary>
    private static Task WaitForStagedPageAsync(string directory)
        => WaitUntilAsync(() => Directory.Exists(directory) && Directory.GetFiles(directory, "*.tmp").Length > 0, TimeSpan.FromSeconds(60));

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
}
