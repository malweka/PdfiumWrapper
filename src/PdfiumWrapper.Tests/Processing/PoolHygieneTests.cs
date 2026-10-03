using System.Diagnostics;
using PdfiumWrapper.Processing;
using PdfiumWrapper.Processing.Protocol;

namespace PdfiumWrapper.Tests.Processing;

/// <summary>
/// What the pool leaves behind and what outlives it: large text results, workers that die right
/// after starting, standard error, and workers whose coordinator is gone.
/// </summary>
[Collection(PdfTestCollection.Name)]
public class PoolHygieneTests : IDisposable
{
    private readonly string _output = PoolFixture.TempDirectory();
    private readonly List<PdfPoolEvent> _events = new();

    public void Dispose() => PoolFixture.DeleteDirectory(_output);

    private void Record(PdfProcessingPool pool) => pool.Events += (_, e) => { lock (_events) _events.Add(e); };

    private PdfPoolEvent[] Events(PdfPoolEventKind kind)
    {
        lock (_events) return _events.Where(e => e.Kind == kind).ToArray();
    }

    /// <summary>
    /// Text too large for a frame travels in a file. The worker writes it in the pool's own temp
    /// directory (the pool reads a text file from nowhere else) and the pool deletes it once read.
    /// </summary>
    [Fact]
    public async Task LargeText_TravelsThroughThePoolTempDirectory_AndLeavesNoFile()
    {
        string input = Path.Combine(_output, "large-text.pdf");
        string line = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 45));
        using (var doc = new PdfDocument())
        {
            for (int p = 0; p < 20; p++)
            {
                using var page = doc.AddPage(1400, 792);
                for (int l = 0; l < 60; l++)
                    page.AddText(line, 10, 10 + l * 12, "Helvetica", 1);
                page.GenerateContent();
            }

            doc.Save(input);
        }

        string temp = Path.Combine(_output, "pool-temp");
        await using var pool = await PdfProcessingPool.CreateAsync(PoolFixture.Options(o => o.TempDirectory = temp)).WaitAsync(PoolFixture.TestTimeout);

        var result = await pool.ExtractTextAsync(input).WaitAsync(PoolFixture.TestTimeout);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(20, result.Value!.Length);
        Assert.True(result.Value.Sum(t => (long)t.Length) * 2 > FrameStream.InlineTextLimit, "the text must be too large for a frame");
        Assert.Contains("lazy dog", result.Value[0]);
        Assert.Empty(Directory.GetFiles(temp, PdfWorkerHost.TextFilePrefix + "*", SearchOption.AllDirectories));
    }

    /// <summary>A text file path is trusted only directly in the pool's temp directory and with the worker's naming.</summary>
    [Fact]
    public async Task TextFilePaths_AreAcceptedOnlyFromThePoolTempDirectory()
    {
        await using var pool = new PdfProcessingPool(PoolFixture.Options(o =>
        {
            o.MinWorkers = 0;
            o.TempDirectory = _output;
        }));
        string temp = pool.TempDirectoryForTests;

        Assert.True(pool.IsPoolTextFile(Path.Combine(temp, "pdfium-text-7-abc.json")));
        Assert.False(pool.IsPoolTextFile(Path.Combine(_output, "pdfium-text-7-abc.json")));
        Assert.False(pool.IsPoolTextFile(Path.Combine(temp, "..", "pdfium-text-7-abc.json")));
        Assert.False(pool.IsPoolTextFile(Path.Combine(temp, "sub", "pdfium-text-7-abc.json")));
        Assert.False(pool.IsPoolTextFile(Path.Combine(temp, "input.pdf")));
        Assert.False(pool.IsPoolTextFile(Path.Combine(temp, "pdfium-text-7-abc.txt")));
        Assert.False(pool.IsPoolTextFile(Path.Combine(Path.GetTempPath(), "pdfium-text-7-abc.json")));
    }

    /// <summary>
    /// A worker that dies right after reporting ready, with its exit handled before the pool adds it
    /// to its table, is a failed start: it is never added (it would never be removed, and its free
    /// slots would stop the pool from starting a real worker), and the next start serves the job.
    /// </summary>
    [Fact]
    public async Task WorkerThatDiesRightAfterReady_IsNotAdded_AndThePoolRecovers()
    {
        string marker = Path.Combine(_output, "exit-after-hello");
        File.WriteAllText(marker, "");
        await using var pool = new PdfProcessingPool(PoolFixture.Options(o =>
        {
            o.MinWorkers = 0;
            o.MaxWorkers = 1;
            // Leaves the test time to remove the fault before the sizer starts the next worker.
            o.ScaleUpAfter = TimeSpan.FromSeconds(3);
            o.WorkerEnvironment["PDFIUMWRAPPER_TEST_FAULT"] = "exit-after-hello-if-exists:" + marker;
        }));
        Record(pool);

        // Holds the first start until its worker has exited and the exit has been handled.
        pool.WorkerReadyHookForTests = pid =>
        {
            pool.WorkerReadyHookForTests = null;
            try
            {
                using var process = Process.GetProcessById(pid);
                process.WaitForExit(30_000);
            }
            catch (ArgumentException)
            {
                // Already gone.
            }

            Thread.Sleep(1500); // the exit handler waits for the read loop, at most a second
        };

        var job = pool.GetPageCountAsync(PoolFixture.Input("doc-1-page.pdf"));
        await PdfProcessingPoolTests.WaitUntilAsync(
            () => Events(PdfPoolEventKind.WorkerStartFailed).Any(e => e.Detail!.Contains("right after reporting ready")),
            TimeSpan.FromSeconds(60));
        File.Delete(marker);
        // Before the fix the dead worker stayed in the table for good: Workers stayed 1 and the job never ran.
        await PdfProcessingPoolTests.WaitUntilAsync(() => pool.Workers == 0, TimeSpan.FromSeconds(2));

        var result = await job.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(1, result.Value);
        Assert.Equal(1, pool.Workers);
    }

    /// <summary>
    /// A worker's standard error arrives as WorkerMessage events, one per line, a long line cut at the
    /// limit; it is never reported as the worker stopping.
    /// </summary>
    [Fact]
    public async Task StandardError_IsAWorkerMessage_AndLongLinesAreCut()
    {
        // No worker until the first job, so the events are recorded from the worker's very start.
        var pool = new PdfProcessingPool(PoolFixture.Options(o =>
        {
            o.MinWorkers = 0;
            o.WorkerEnvironment["PDFIUMWRAPPER_TEST_FAULT"] = "stderr-line:100000";
        }));
        Record(pool);
        await using (pool)
        {
            var result = await pool.GetPageCountAsync(PoolFixture.Input("doc-1-page.pdf")).WaitAsync(PoolFixture.TestTimeout);
            Assert.Equal(1, result.Value);
            await PdfProcessingPoolTests.WaitUntilAsync(() => Events(PdfPoolEventKind.WorkerMessage).Length >= 2, TimeSpan.FromSeconds(30));
        }

        var messages = Events(PdfPoolEventKind.WorkerMessage);
        Assert.Contains(messages, e => e.Detail == "short line");
        var cut = Assert.Single(messages, e => e.Detail!.StartsWith("xxxx", StringComparison.Ordinal));
        Assert.Equal(new string('x', 4096) + " [truncated]", cut.Detail);
        Assert.DoesNotContain(Events(PdfPoolEventKind.WorkerStopped), e => e.Detail?.Contains("short line") == true);
    }

    /// <summary>On Windows every worker is in the job object that kills it when the coordinator process ends.</summary>
    [Fact]
    public async Task Workers_AreInTheKillOnCloseJob_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var pool = await PdfProcessingPool.CreateAsync(PoolFixture.Options()).WaitAsync(PoolFixture.TestTimeout);
        int pid = Assert.Single(pool.WorkerPidsForTests);
        using var process = Process.GetProcessById(pid);
        Assert.True(WorkerJobObject.Contains(process));
    }

    /// <summary>
    /// When the coordinator is gone (its end of standard input closes), a worker stops: at once when
    /// idle, and after the grace when a job is stuck and ignores cancellation, so no worker outlives
    /// its coordinator on any platform.
    /// </summary>
    [Fact]
    public async Task WorkerWhoseInputCloses_Exits_EvenWithAHungJob()
    {
        // Idle: exits as soon as its input ends.
        using (var idle = StartWorker(fault: null))
        {
            try
            {
                await ReadHelloAsync(idle);
                idle.StandardInput.Close();
                Assert.True(idle.WaitForExit(15_000), "an idle worker did not exit when its input closed");
                Assert.Equal(0, idle.ExitCode);
            }
            finally
            {
                KillIfRunning(idle);
            }
        }

        // A job that hangs on the worker's loop thread: only the grace ends it.
        string input = PoolFixture.CopyAs("doc-1-page.pdf", _output, "hang-me");
        using var hung = StartWorker(fault: "hang:hang-me");
        try
        {
            await ReadHelloAsync(hung);
            await FrameStream.WriteAsync(hung.StandardInput.BaseStream,
                Frame.ForJob(new JobPayload { Id = 1, Kind = JobKind.PageCount, Input = input }), new SemaphoreSlim(1, 1), CancellationToken.None);
            await Task.Delay(500);

            var closed = Stopwatch.StartNew();
            hung.StandardInput.Close();
            Assert.True(hung.WaitForExit(30_000), "a worker with a hung job did not exit when its input closed");
            Assert.Equal(PdfWorkerHost.InputClosedExitCode, hung.ExitCode);
            Assert.True(closed.Elapsed >= PdfWorkerHost.InputClosedGrace - TimeSpan.FromSeconds(1), $"exited after {closed.Elapsed}");
        }
        finally
        {
            KillIfRunning(hung);
        }
    }

    private static void KillIfRunning(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
        }
    }

    private static Process StartWorker(string? fault)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(HostRunner.HostPath)!,
        };
        psi.ArgumentList.Add(HostRunner.HostPath);
        psi.Environment[PdfWorkerHost.EnvironmentVariable] = "1";
        if (fault != null)
            psi.Environment["PDFIUMWRAPPER_TEST_FAULT"] = fault;

        var process = Process.Start(psi)!;
        _ = process.StandardError.ReadToEndAsync();
        return process;
    }

    private static async Task ReadHelloAsync(Process worker)
    {
        var hello = await FrameStream.ReadAsync(worker.StandardOutput.BaseStream, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(FrameKind.Hello, hello?.Kind);
    }
}
