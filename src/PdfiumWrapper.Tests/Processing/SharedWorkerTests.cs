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
        string neighbourInput = PoolFixture.CopyAs("contract.pdf", _output, "slow-neighbour");

        await using var pool = await CreateAsync(o =>
        {
            o.MaxAttempts = 1;
            o.JobTimeout = TimeSpan.FromSeconds(8);
            o.WorkerEnvironment["PDFIUMWRAPPER_TEST_FAULT"] = "hang-after-page-1:hang-me;sleep-after-page-600:slow-neighbour";
        });

        var hung = pool.ConvertToPngAsync(hanging, Path.Combine(_output, "hung"), dpi: 20);
        await WaitUntilAsync(() => pool.RunningJobs == 1, TimeSpan.FromSeconds(30));

        // The neighbour needs at least 6 s and starts 4 s into the hung job's 8 s: it is still
        // running when the worker is killed, and a fresh run fits well inside the timeout.
        await Task.Delay(TimeSpan.FromSeconds(4));
        var neighbour = pool.ConvertToPngAsync(neighbourInput, Path.Combine(_output, "neighbour"), dpi: 20);

        var hungResult = await hung.WaitAsync(PoolFixture.TestTimeout);
        var neighbourResult = await neighbour.WaitAsync(PoolFixture.TestTimeout);

        Assert.Equal(PdfJobStatus.TimedOut, hungResult.Status);
        Assert.Equal(1, hungResult.Attempts);
        Assert.True(neighbourResult.IsSuccess, $"{neighbourResult.Status}: {neighbourResult.Error}");
        Assert.Equal(1, neighbourResult.Attempts);
        Assert.Equal(10, neighbourResult.Value!.Files.Count);
        Assert.Contains(Events(PdfPoolEventKind.JobRetried), e => e.Detail!.Contains("without using an attempt"));
        Assert.Equal(0, pool.Statistics.JobsCrashed);
    }

    /// <summary>A cancelled job that ignores the cancel gets its worker killed; the neighbour runs again without using an attempt.</summary>
    [Fact]
    public async Task SlowCancel_DoesNotChargeTheNeighbour()
    {
        string hanging = PoolFixture.CopyAs("doc-1-page.pdf", _output, "hang-me");
        string neighbourInput = PoolFixture.CopyAs("contract.pdf", _output, "slow-neighbour");

        await using var pool = await CreateAsync(o =>
        {
            o.MaxAttempts = 1;
            o.WorkerEnvironment["PDFIUMWRAPPER_TEST_FAULT"] = "hang-after-page-1:hang-me;sleep-after-page-500:slow-neighbour";
        });

        using var cancel = new CancellationTokenSource();
        var hung = pool.ConvertToPngAsync(hanging, Path.Combine(_output, "hung"), dpi: 20, ct: cancel.Token);
        await WaitUntilAsync(() => pool.RunningJobs == 1, TimeSpan.FromSeconds(30));
        var neighbour = pool.ConvertToPngAsync(neighbourInput, Path.Combine(_output, "neighbour"), dpi: 20);
        await WaitUntilAsync(() => pool.RunningJobs == 2, TimeSpan.FromSeconds(30));

        // The hung job never observes the cancel, so the worker is killed after the 2 s grace,
        // while the neighbour (at least 5 s) is still running.
        cancel.Cancel();

        var hungResult = await hung.WaitAsync(PoolFixture.TestTimeout);
        var neighbourResult = await neighbour.WaitAsync(PoolFixture.TestTimeout);

        Assert.Equal(PdfJobStatus.Cancelled, hungResult.Status);
        Assert.True(neighbourResult.IsSuccess, $"{neighbourResult.Status}: {neighbourResult.Error}");
        Assert.Equal(1, neighbourResult.Attempts);
        Assert.Equal(10, neighbourResult.Value!.Files.Count);
        Assert.Contains(Events(PdfPoolEventKind.JobRetried), e => e.Detail!.Contains("without using an attempt"));
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
