using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using PdfiumWrapper.Processing.Protocol;

namespace PdfiumWrapper.Processing;

/// <summary>
/// The worker side of <see cref="PdfProcessingPool"/>. A worker is the consumer's own executable
/// started again by the pool with <see cref="EnvironmentVariable"/> set; it runs this loop instead
/// of the application. Make it the first statement of <c>Main</c>:
/// <code>
/// if (PdfWorkerHost.TryRun())
///     return 0;
/// </code>
/// </summary>
public static class PdfWorkerHost
{
    /// <summary>Set to <c>1</c> in a worker process's environment by the pool.</summary>
    public const string EnvironmentVariable = "PDFIUMWRAPPER_WORKER";

    /// <summary>
    /// Set to <c>1</c> in a worker's environment (<see cref="PdfPoolOptions.WorkerEnvironment"/>)
    /// to turn on PdfiumWrapper's internal diagnostics in that worker; results then carry native
    /// render intervals. For tests and measurements, not for production.
    /// </summary>
    public const string DiagnosticsVariable = "PDFIUMWRAPPER_DIAGNOSTICS";

    /// <summary>Jobs this worker runs at once; set by the pool from <see cref="PdfPoolOptions.JobsPerWorker"/>.</summary>
    internal const string SlotsVariable = "PDFIUMWRAPPER_WORKER_SLOTS";

    /// <summary>The pool's temp directory, where large text results are written; set by the pool.</summary>
    internal const string TempDirectoryVariable = "PDFIUMWRAPPER_WORKER_TEMP";

    /// <summary>Prefix and extension of the file a large text result travels in.</summary>
    internal const string TextFilePrefix = "pdfium-text-";
    internal const string TextFileExtension = ".json";

    /// <summary>
    /// Time the jobs in flight get to stop once the coordinator is gone (standard input closed), before
    /// the worker exits regardless. Nobody can receive their results any more; this only lets them
    /// remove what they staged.
    /// </summary>
    internal static readonly TimeSpan InputClosedGrace = TimeSpan.FromSeconds(5);

    /// <summary>Exit code of a worker that ended because its jobs did not stop within <see cref="InputClosedGrace"/>.</summary>
    internal const int InputClosedExitCode = 5;

    /// <summary>
    /// Runs the worker loop if this process was started as a worker, and returns true when it has
    /// finished (the process should then exit). Returns false at once in a normal process.
    /// </summary>
    public static bool TryRun() => TryRun(null);

    internal static bool TryRun(WorkerHooks? hooks)
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) != "1")
            return false;

        // Must happen before the first wrapper type is touched: the switch is read once.
        if (Environment.GetEnvironmentVariable(DiagnosticsVariable) == "1")
            AppContext.SetSwitch("PdfiumWrapper.Diagnostics", true);

        int exitCode = RunAsync(hooks).GetAwaiter().GetResult();
        if (exitCode != 0)
            Environment.ExitCode = exitCode;
        return true;
    }

    private static async Task<int> RunAsync(WorkerHooks? hooks)
    {
        // Protocol frames are the only thing this process may write to stdout.
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        var writeLock = new SemaphoreSlim(1, 1);
        using var exit = new CancellationTokenSource();

        try
        {
            Warm();
            hooks?.BeforeHello?.Invoke();

            await FrameStream.WriteAsync(output, Frame.ForHello(new HelloPayload
            {
                ProtocolVersion = Frame.ProtocolVersion,
                Pid = Environment.ProcessId,
                WrapperVersion = typeof(PdfDocument).Assembly.GetName().Version?.ToString(),
                UtcTicks = DateTime.UtcNow.Ticks,
                DiagnosticsEnabled = PdfiumDiagnostics.Enabled,
            }), writeLock, exit.Token).ConfigureAwait(false);
            hooks?.AfterHello?.Invoke();

            // Frames are read on their own task so a Cancel can arrive while a job runs.
            var inbox = new BlockingCollection<Frame>();
            var cancellations = new ConcurrentDictionary<long, CancellationTokenSource>();
            bool inputClosed = false;
            var reader = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        Frame? frame;
                        try
                        {
                            frame = await FrameStream.ReadAsync(input, exit.Token).ConfigureAwait(false);
                        }
                        catch (ProtocolException ex)
                        {
                            // A malformed frame is reported and skipped; the coordinator decides what to do.
                            await Console.Error.WriteLineAsync("malformed frame: " + ex.Message).ConfigureAwait(false);
                            continue;
                        }

                        if (frame == null)
                        {
                            Volatile.Write(ref inputClosed, true);
                            OnInputClosed(cancellations);
                            break;
                        }

                        if (frame.Kind == FrameKind.Cancel && frame.CancelJobId is { } cancelId)
                        {
                            if (cancellations.TryGetValue(cancelId, out var cts))
                                cts.Cancel();
                            else
                                inbox.Add(frame); // not started yet: handled when dequeued
                            continue;
                        }

                        inbox.Add(frame);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    inbox.CompleteAdding();
                }
            });

            // Several jobs at once: PDFium serializes rendering inside this process, so while one
            // job renders another can encode and write. The pool never sends more than this many.
            int slots = int.TryParse(Environment.GetEnvironmentVariable(SlotsVariable), out int parsed) && parsed > 0 ? parsed : 1;
            using var slotFree = new SemaphoreSlim(slots, slots);
            var running = new ConcurrentDictionary<long, Task>();
            var cancelledBeforeStart = new HashSet<long>();

            foreach (var frame in inbox.GetConsumingEnumerable())
            {
                // Once the coordinator is gone, nothing more is started: no result could reach it.
                if (frame.Kind == FrameKind.Shutdown || Volatile.Read(ref inputClosed))
                    break;

                if (frame.Kind == FrameKind.Cancel && frame.CancelJobId is { } id)
                {
                    cancelledBeforeStart.Add(id);
                    continue;
                }

                if (frame.Kind != FrameKind.Job || frame.Job == null)
                    continue;

                var job = frame.Job;
                if (cancelledBeforeStart.Remove(job.Id))
                {
                    await FrameStream.WriteAsync(output, Frame.ForResult(new ResultPayload { JobId = job.Id, Status = ResultStatus.Cancelled }), writeLock, exit.Token).ConfigureAwait(false);
                    continue;
                }

                await slotFree.WaitAsync(exit.Token).ConfigureAwait(false);
                var jobCancel = new CancellationTokenSource();
                cancellations[job.Id] = jobCancel;

                // Test hooks run on the loop thread: a hang here hangs the worker, which is the point.
                hooks?.BeforeJob?.Invoke(job);

                running[job.Id] = Task.Run(async () =>
                {
                    ResultPayload result;
                    try
                    {
                        result = Execute(job, jobCancel.Token, output, writeLock, hooks);
                    }
                    catch (OperationCanceledException)
                    {
                        result = new ResultPayload { JobId = job.Id, Status = ResultStatus.Cancelled };
                    }
                    catch (Exception ex)
                    {
                        result = new ResultPayload { JobId = job.Id, Status = ResultStatus.Failed, Error = $"{ex.GetType().Name}: {ex.Message}" };
                    }
                    finally
                    {
                        cancellations.TryRemove(job.Id, out _);
                        jobCancel.Dispose();
                    }

                    try
                    {
                        await FrameStream.WriteAsync(output, Frame.ForResult(result), writeLock, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch when (result.TextFile != null)
                    {
                        // The coordinator will never read the text file it was not told about.
                        TryDelete(result.TextFile);
                        throw;
                    }
                    finally
                    {
                        running.TryRemove(job.Id, out _);
                        if (running.IsEmpty && PdfiumDiagnostics.Enabled)
                            PdfiumDiagnostics.Reset(); // keeps the event buffer small between bursts
                        slotFree.Release();
                    }
                });
            }

            // Jobs in flight finish before the worker exits; the pool waits for them up to its JobTimeout.
            await Task.WhenAll(running.Values.ToArray()).ConfigureAwait(false);
            exit.Cancel();
            await Task.WhenAny(reader, Task.Delay(1000)).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync("worker failed: " + ex).ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>
    /// Standard input has ended without the process being stopped: the coordinator closed the pipe
    /// or is gone (crashed or killed; on Windows the job object usually kills this process first).
    /// Every job in flight is told to stop, and if they have not stopped within
    /// <see cref="InputClosedGrace"/> the process exits anyway, so a worker hung in a job, or in native
    /// code, never outlives its coordinator.
    /// </summary>
    private static void OnInputClosed(ConcurrentDictionary<long, CancellationTokenSource> cancellations)
    {
        foreach (var cts in cancellations.Values)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The job finished meanwhile.
            }
        }

        new Thread(() =>
        {
            Thread.Sleep(InputClosedGrace);
            Environment.Exit(InputClosedExitCode);
        })
        {
            IsBackground = true,
            Name = "PdfWorkerHost input-closed watchdog",
        }.Start();
    }

    /// <summary>Pays native initialization and JIT before the first job: one small page rendered.</summary>
    private static void Warm()
    {
        using var doc = new PdfDocument();
        using var page = doc.AddPage(200, 200);
        _ = page.RenderToBytes(50, 50);
    }

    private static ResultPayload Execute(JobPayload job, CancellationToken ct, Stream output, SemaphoreSlim writeLock, WorkerHooks? hooks)
    {
        long start = Stopwatch.GetTimestamp();
        int thread = Environment.CurrentManagedThreadId;

        var result = new ResultPayload { JobId = job.Id, Status = ResultStatus.Succeeded };
        using var doc = new PdfDocument(job.Input, job.Password);
        result.PageCount = doc.PageCount;
        ct.ThrowIfCancellationRequested();

        switch (job.Kind)
        {
            case JobKind.PageCount:
                break;

            case JobKind.ConvertToPng:
            case JobKind.ConvertToJpeg:
            {
                string directory = job.Output ?? throw new ArgumentException("Image jobs need an output directory.");
                Directory.CreateDirectory(directory);
                var format = job.Kind == JobKind.ConvertToPng ? ImageFormat.Png : ImageFormat.Jpeg;
                var staged = new List<(string Temp, string Final)>(result.PageCount);
                var files = new List<string>(result.PageCount);
                long bytes = 0;

                // Every page is first written under a name that carries this job's id, and only once
                // all of them are staged are they moved to their final names. So whatever ends the job
                // early (cancel, a page that will not render, a write that fails, the process dying)
                // leaves either nothing or files that are unmistakably this job's. All or nothing, and
                // nothing that was in the directory before is touched unless the job reached the end.
                try
                {
                    foreach (var image in doc.StreamImageBytes(format, job.Quality, job.DpiWidth, job.DpiHeight))
                    {
                        ct.ThrowIfCancellationRequested();

                        string path = Path.Combine(directory, PdfDocument.PageFileName(job.FileNamePrefix, staged.Count + 1, format));
                        string temp = path + $".{job.Id}.tmp";
                        File.WriteAllBytes(temp, image);
                        staged.Add((temp, path));
                        bytes += image.Length;
                        SendProgress(output, writeLock, job.Id, staged.Count);
                        hooks?.AfterPage?.Invoke(job, staged.Count);
                    }

                    ct.ThrowIfCancellationRequested();
                    SendProgress(output, writeLock, job.Id, staged.Count, committingPages: staged.Count);
                    foreach (var (temp, path) in staged)
                    {
                        File.Move(temp, path, overwrite: true);
                        files.Add(path);
                    }
                }
                catch
                {
                    foreach (var (temp, _) in staged)
                        TryDelete(temp);
                    foreach (var moved in files)
                        TryDelete(moved);
                    throw;
                }

                result.Files = files.ToArray();
                result.OutputBytes = bytes;
                break;
            }

            case JobKind.ConvertToTiff:
            {
                string path = job.Output ?? throw new ArgumentException("TIFF jobs need an output path.");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                string temp = path + $".{job.Id}.tmp";
                try
                {
                    doc.SaveAsTiff(temp, job.DpiWidth, job.DpiHeight, job.ColorMode, job.Threshold);
                    ct.ThrowIfCancellationRequested();
                    File.Move(temp, path, overwrite: true);
                }
                catch
                {
                    TryDelete(temp);
                    throw;
                }

                result.Files = new[] { path };
                result.OutputBytes = new FileInfo(path).Length;
                break;
            }

            case JobKind.ExtractText:
            {
                var pages = doc.ProcessAllPages(page =>
                {
                    ct.ThrowIfCancellationRequested();
                    return page.ExtractText();
                });

                long size = pages.Sum(p => (long)p.Length * 2);
                if (size <= FrameStream.InlineTextLimit)
                {
                    result.Text = pages;
                }
                else
                {
                    // In the pool's own temp directory: the coordinator accepts a text file only from
                    // there, and removes whatever a lost worker left in it when it is disposed.
                    string directory = Environment.GetEnvironmentVariable(TempDirectoryVariable) is { Length: > 0 } temp ? temp : Path.GetTempPath();
                    string file = Path.Combine(directory, $"{TextFilePrefix}{job.Id}-{Guid.NewGuid():N}{TextFileExtension}");
                    try
                    {
                        File.WriteAllText(file, JsonSerializer.Serialize(pages));
                    }
                    catch
                    {
                        TryDelete(file);
                        throw;
                    }

                    result.TextFile = file;
                }

                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(job.Kind), job.Kind, "Unknown job kind.");
        }

        result.ProcessingMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        if (PdfiumDiagnostics.Enabled)
        {
            // This job's renders: native work runs on the job's own thread (the core API is synchronous),
            // so the thread id separates concurrent jobs. Stopwatch ticks are per process; they are
            // placed on the UTC clock for the coordinator.
            result.RenderIntervalsUtcTicks = PdfiumDiagnostics.Snapshot().Events
                .Where(e => e.IsNative && e.NativeOp == NativeOp.Render && e.ThreadId == thread && e.StartTicks >= start)
                .Select(e => new[] { ToUtcTicks(e.StartTicks), ToUtcTicks(e.EndTicks) })
                .ToArray();
        }

        return result;

        static long ToUtcTicks(long stopwatchTicks)
        {
            long nowSw = Stopwatch.GetTimestamp();
            long nowUtc = DateTime.UtcNow.Ticks;
            return nowUtc - (long)((nowSw - stopwatchTicks) * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency));
        }
    }

    private static void SendProgress(Stream output, SemaphoreSlim writeLock, long jobId, int pagesDone, int committingPages = 0)
    {
        try
        {
            FrameStream.WriteAsync(output, Frame.ForProgress(jobId, pagesDone, committingPages), writeLock, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (IOException)
        {
            // The coordinator went away; the job result will fail to send too and the loop ends.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Test hooks for fault injection; not part of the public API.</summary>
internal sealed class WorkerHooks
{
    /// <summary>Called once the worker is warm, just before it reports ready.</summary>
    public Action? BeforeHello { get; set; }

    /// <summary>Called once the worker has reported ready, before it reads its first frame.</summary>
    public Action? AfterHello { get; set; }

    /// <summary>Called before each job runs, with the job about to run.</summary>
    public Action<JobPayload>? BeforeJob { get; set; }

    /// <summary>Called after each page of an image job is in place, with the job and the page number written.</summary>
    public Action<JobPayload, int>? AfterPage { get; set; }
}
