using System.Diagnostics;
using PdfiumWrapper.Processing;

namespace PdfiumWrapper.Tests.Processing;

/// <summary>The sizing policy: up fast under load, down slowly when idle, never when fixed.</summary>
[Collection(PdfTestCollection.Name)]
public class PoolSizingTests : IDisposable
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

    private int Count(PdfPoolEventKind kind)
    {
        lock (_events) return _events.Count(e => e.Kind == kind);
    }

    [Fact]
    public async Task Burst_ScalesUpToMax_ThenBackDownToMin()
    {
        await using var pool = await CreateAsync(o =>
        {
            o.MinWorkers = 1;
            o.MaxWorkers = 4;
            o.ScaleUpAfter = TimeSpan.FromMilliseconds(200);
            o.IdleTimeout = TimeSpan.FromSeconds(2);
        });
        Assert.Equal(1, pool.Workers);

        string big = PoolFixture.Input("presentation.pdf");
        var jobs = Enumerable.Range(0, 40).Select(i => pool.ConvertToPngAsync(big, Path.Combine(_output, $"burst-{i}"), dpi: 100)).ToArray();

        // A worker counts in Workers while it starts and raises ScaledUp once it is ready.
        await PdfProcessingPoolTests.WaitUntilAsync(() => pool.Workers == 4, TimeSpan.FromSeconds(30));
        await PdfProcessingPoolTests.WaitUntilAsync(() => Count(PdfPoolEventKind.ScaledUp) >= 3, TimeSpan.FromSeconds(30));

        var results = await Task.WhenAll(jobs).WaitAsync(PoolFixture.TestTimeout);
        Assert.All(results, r => Assert.True(r.IsSuccess, r.Error));
        Assert.True(results.Select(r => r.WorkerPid).Distinct().Count() >= 3, "the added workers did not take jobs");

        await PdfProcessingPoolTests.WaitUntilAsync(() => pool.Workers == 1, TimeSpan.FromSeconds(30));
        Assert.True(Count(PdfPoolEventKind.ScaledDown) >= 3, $"{Count(PdfPoolEventKind.ScaledDown)} scale-downs");
        Assert.Equal(1, (await pool.GetPageCountAsync(PoolFixture.Input("doc-1-page.pdf"))).Value);
    }

    [Fact]
    public async Task OccasionalJobs_DoNotScaleUp()
    {
        await using var pool = await CreateAsync(o =>
        {
            o.MinWorkers = 1;
            o.MaxWorkers = 4;
            o.ScaleUpAfter = TimeSpan.FromMilliseconds(500);
            o.IdleTimeout = TimeSpan.FromSeconds(30);
        });

        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(1, (await pool.GetPageCountAsync(PoolFixture.Input("doc-1-page.pdf"))).Value);
            await Task.Delay(300);
        }

        Assert.Equal(1, pool.Workers);
        Assert.Equal(0, Count(PdfPoolEventKind.ScaledUp));
        Assert.Equal(0, pool.Statistics.ScaleUps);
    }

    [Fact]
    public async Task FixedSize_NeverRaisesSizingEvents()
    {
        await using var pool = await CreateAsync(o =>
        {
            o.MinWorkers = 2;
            o.MaxWorkers = 2;
            o.ScaleUpAfter = TimeSpan.FromMilliseconds(100);
            o.IdleTimeout = TimeSpan.FromMilliseconds(500);
        });

        var jobs = Enumerable.Range(0, 12).Select(_ => pool.GetPageCountAsync(PoolFixture.Input("contract.pdf"))).ToArray();
        Assert.All(await Task.WhenAll(jobs), r => Assert.Equal(10, r.Value));
        await Task.Delay(1500);

        Assert.Equal(2, pool.Workers);
        Assert.Equal(0, Count(PdfPoolEventKind.ScaledUp));
        Assert.Equal(0, Count(PdfPoolEventKind.ScaledDown));
    }

    [Fact]
    public async Task WorkerAboveTheMemoryLimit_IsRetired_AndTheNextJobStillSucceeds()
    {
        await using var pool = await CreateAsync(o =>
        {
            o.MinWorkers = 1;
            o.MaxWorkers = 1;
            o.MaxWorkerMemoryBytes = 8 * 1024 * 1024; // far below any .NET process's working set
        });

        var first = await pool.GetPageCountAsync(PoolFixture.Input("doc-1-page.pdf")).WaitAsync(PoolFixture.TestTimeout);
        Assert.Equal(1, first.Value);

        await PdfProcessingPoolTests.WaitUntilAsync(() => Count(PdfPoolEventKind.WorkerRetiredForMemory) >= 1, TimeSpan.FromSeconds(30));
        var second = await pool.GetPageCountAsync(PoolFixture.Input("doc-1-page.pdf")).WaitAsync(PoolFixture.TestTimeout);
        Assert.Equal(1, second.Value);
        Assert.NotEqual(first.WorkerPid, second.WorkerPid);
        Assert.True(pool.Statistics.WorkersRetiredForMemory >= 1);
    }

    [Fact]
    public async Task MinWorkersZero_StartsAWorkerForTheFirstJob()
    {
        await using var pool = await CreateAsync(o =>
        {
            o.MinWorkers = 0;
            o.MaxWorkers = 2;
            o.IdleTimeout = TimeSpan.FromSeconds(1);
        });
        Assert.Equal(0, pool.Workers);

        var result = await pool.GetPageCountAsync(PoolFixture.Input("doc-1-page.pdf")).WaitAsync(PoolFixture.TestTimeout);
        Assert.Equal(1, result.Value);

        await PdfProcessingPoolTests.WaitUntilAsync(() => pool.Workers == 0, TimeSpan.FromSeconds(30));
    }

    /// <summary>A worker with two slots runs two jobs at the same time, and the sizer counts slots, not workers.</summary>
    [Fact]
    public async Task TwoJobsPerWorker_RunAtOnce_OnOneWorker()
    {
        await using var pool = await CreateAsync(o =>
        {
            o.MinWorkers = 1;
            o.MaxWorkers = 1;
            o.JobsPerWorker = 2;
        });

        string big = PoolFixture.Input("presentation.pdf");
        var first = pool.ConvertToPngAsync(big, Path.Combine(_output, "slot-1"), dpi: 100);
        var second = pool.ConvertToPngAsync(big, Path.Combine(_output, "slot-2"), dpi: 100);

        await PdfProcessingPoolTests.WaitUntilAsync(() => pool.RunningJobs == 2, TimeSpan.FromSeconds(30));
        Assert.Equal(1, pool.BusyWorkers);

        var results = await Task.WhenAll(first, second).WaitAsync(PoolFixture.TestTimeout);
        Assert.All(results, r => Assert.True(r.IsSuccess, r.Error));
        Assert.Equal(results[0].WorkerPid, results[1].WorkerPid);
        Assert.True(results[0].Timings.Processing + results[1].Timings.Processing > results.Max(r => r.Timings.Total),
            "the two jobs did not overlap in time");
        Assert.Equal(0, Count(PdfPoolEventKind.ScaledUp));
    }

    /// <summary>
    /// Two workers with diagnostics on report their native render intervals on the UTC clock.
    /// Some pair from different processes must overlap: that is the parallelism the pool exists for.
    /// </summary>
    [Fact]
    public async Task Renders_OverlapAcrossWorkerProcesses()
    {
        await using var pool = await CreateAsync(o =>
        {
            o.MinWorkers = 2;
            o.MaxWorkers = 2;
            o.WorkerEnvironment["PDFIUMWRAPPER_DIAGNOSTICS"] = "1";
        });

        string big = PoolFixture.Input("presentation.pdf");
        var results = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(i => pool.ConvertToPngAsync(big, Path.Combine(_output, $"overlap-{i}"), dpi: 100))).WaitAsync(PoolFixture.TestTimeout);
        Assert.All(results, r => Assert.True(r.IsSuccess, r.Error));

        var intervals = PdfProcessingPool.LastRenderIntervals;
        Assert.True(intervals.Count >= 2, "workers reported no render intervals; is diagnostics on in the worker?");
        var byPid = intervals.GroupBy(i => i.Pid).ToArray();
        Assert.True(byPid.Length >= 2, "renders came from one process only");

        bool overlap = intervals.Any(a => intervals.Any(b =>
            a.Pid != b.Pid && a.StartUtcTicks < b.EndUtcTicks && b.StartUtcTicks < a.EndUtcTicks));
        Assert.True(overlap, "no render in one worker overlapped a render in another");
    }

    /// <summary>Every damaged input from the crash-probe set goes through the pool without an exception reaching the caller.</summary>
    [Fact]
    public async Task DamagedInputs_AreReportedPerJob_AndNeverThrow()
    {
        await using var pool = await CreateAsync(o => { o.MinWorkers = 2; o.MaxWorkers = 2; o.MaxAttempts = 2; });

        var inputs = new List<PdfInput>();
        foreach (var fixture in new[] { "doc-1-page.pdf", "doc-3-pages-with-comments.pdf", "contract.pdf", "fw2.pdf", "presentation.pdf" })
        {
            byte[] original = File.ReadAllBytes(PoolFixture.Input(fixture));
            string stem = Path.GetFileNameWithoutExtension(fixture);
            foreach (int percent in new[] { 25, 50, 75 })
                inputs.Add(PdfInput.FromBytes(original[..(int)(original.LongLength * percent / 100)], $"{stem}-truncated-{percent}.pdf"));

            var flipped = (byte[])original.Clone();
            var random = new Random(20260930);
            for (int i = 0; i < flipped.Length / 100; i++)
            {
                int index = random.Next(flipped.Length);
                flipped[index] = (byte)~flipped[index];
            }
            inputs.Add(PdfInput.FromBytes(flipped, $"{stem}-flipped.pdf"));
        }

        int processed = 0, failed = 0, crashed = 0;
        await foreach (var result in pool.GetPageCountAsync(inputs).WithCancellation(new CancellationTokenSource(PoolFixture.TestTimeout).Token))
        {
            switch (result.Status)
            {
                case PdfJobStatus.Succeeded: processed++; break;
                case PdfJobStatus.Failed: failed++; break;
                case PdfJobStatus.WorkerCrashed: crashed++; break;
                default: Assert.Fail($"{result.Input}: {result.Status} {result.Error}"); break;
            }
        }

        Assert.Equal(inputs.Count, processed + failed + crashed);
        Assert.True(failed + processed > 0);

        // Whatever the damaged files did, the pool still serves.
        Assert.Equal(1, (await pool.GetPageCountAsync(PoolFixture.Input("doc-1-page.pdf")).WaitAsync(PoolFixture.TestTimeout)).Value);
    }
}
