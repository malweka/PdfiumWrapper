using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using PdfiumWrapper.Processing.Protocol;

namespace PdfiumWrapper.Processing;

/// <summary>
/// Runs PDF operations in a dynamically sized set of worker processes. Each worker has its own
/// PDFium, so workers render in parallel, and a native failure in one costs that job, not the
/// process that owns the pool.
/// </summary>
/// <remarks>
/// Create one pool per application and keep it for the application's lifetime. All members are
/// thread-safe. See <see cref="PdfPoolOptions"/> for sizing and <see cref="PdfWorkerHost"/> for
/// how workers are hosted.
/// </remarks>
public sealed partial class PdfProcessingPool : IAsyncDisposable
{
    private static readonly TimeSpan SizerInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(5);

    private readonly PdfPoolOptions _options;
    private readonly Channel<PendingJob> _queue;
    // Retries go ahead of new jobs and are never subject to the queue capacity.
    private readonly Channel<PendingJob> _retries = Channel.CreateUnbounded<PendingJob>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<Worker> _workers = new();
    private readonly object _workersLock = new();
    private readonly ConcurrentDictionary<long, PendingJob> _inFlight = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _dispatcher;
    private readonly Task _sizer;
    private readonly SemaphoreSlim _workerAvailable = new(0);
    private readonly string _tempDirectory;
    private readonly Counters _counters = new();
    private long _nextJobId;
    private int _queuedCount;
    private long _allBusySince;
    private int _startingWorkers;
    private bool _disposed;

    /// <summary>Creates the pool and starts <see cref="PdfPoolOptions.MinWorkers"/> workers, waiting until they are ready.</summary>
    /// <exception cref="PdfPoolException">A worker could not be started; see the message.</exception>
    public static async Task<PdfProcessingPool> CreateAsync(PdfPoolOptions? options = null, CancellationToken ct = default)
    {
        var pool = new PdfProcessingPool(options);
        try
        {
            await pool.StartInitialWorkersAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await pool.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return pool;
    }

    /// <summary>
    /// Creates the pool. <see cref="PdfPoolOptions.MinWorkers"/> workers are started in the
    /// background; the first jobs wait for them. Prefer <see cref="CreateAsync"/> to observe
    /// start-up failures directly.
    /// </summary>
    public PdfProcessingPool(PdfPoolOptions? options = null)
    {
        _options = (options ?? new PdfPoolOptions()).Clone();
        _options.Validate();

        _tempDirectory = Path.Combine(_options.TempDirectory ?? Path.GetTempPath(), $"pdfium-pool-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);

        _queue = Channel.CreateBounded<PendingJob>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

        _dispatcher = Task.Run(DispatchLoopAsync);
        _sizer = Task.Run(SizerLoopAsync);
    }

    /// <summary>Raised for worker and job events. Handlers run on pool threads and must not throw.</summary>
    public event EventHandler<PdfPoolEvent>? Events;

    /// <summary>Workers alive (ready or starting).</summary>
    public int Workers
    {
        get
        {
            lock (_workersLock)
                return _workers.Count + _startingWorkers;
        }
    }

    /// <summary>Workers with at least one job in flight.</summary>
    public int BusyWorkers
    {
        get
        {
            lock (_workersLock)
                return _workers.Count(IsBusy);
        }
    }

    /// <summary>Jobs in flight across all workers.</summary>
    public int RunningJobs
    {
        get
        {
            lock (_workersLock)
                return _workers.Sum(w => w.Active.Count);
        }
    }

    private static bool IsBusy(Worker worker) => !worker.Active.IsEmpty;

    /// <summary>Jobs waiting for a worker.</summary>
    public int QueuedJobs => Volatile.Read(ref _queuedCount);

    public PdfPoolStatistics Statistics => _counters.Snapshot();

    // ---- Submission ----

    internal async Task<PdfJobResult<T>> SubmitAsync<T>(JobPayload payload, string displayInput, string? spooledInput,
        Func<ResultPayload, T> project, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        payload.Id = Interlocked.Increment(ref _nextJobId);
        var job = new PendingJob(payload, displayInput, spooledInput, ct);
        _counters.Increment(ref _counters.Submitted);

        Interlocked.Increment(ref _queuedCount);
        try
        {
            if (!_queue.Writer.TryWrite(job))
            {
                Raise(PdfPoolEventKind.QueueFull, 0, job.Id, $"{QueuedJobs} jobs queued");
                await _queue.Writer.WriteAsync(job, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled while waiting for a queue slot: the same Cancelled result as a job cancelled
            // later, never an exception.
            Interlocked.Decrement(ref _queuedCount);
            Finish(job, PdfJobStatus.Cancelled, "cancelled while waiting for a queue slot", new ResultPayload { JobId = job.Id, Status = ResultStatus.Cancelled });
        }
        catch (Exception ex)
        {
            Interlocked.Decrement(ref _queuedCount);
            CleanUp(job);
            if (ex is ChannelClosedException)
                throw new ObjectDisposedException(nameof(PdfProcessingPool));
            throw;
        }

        ResultPayload result;
        using (ct.Register(() => Cancel(job)))
        {
            result = await job.Completion.Task.ConfigureAwait(false);
        }

        long completedAt = Stopwatch.GetTimestamp();
        T? value = default;
        string? error = job.FinalError ?? result.Error;
        if (job.FinalStatus == PdfJobStatus.Succeeded)
        {
            try
            {
                value = project(result);
            }
            catch (Exception ex)
            {
                job.FinalStatus = PdfJobStatus.Failed;
                error = $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        return new PdfJobResult<T>(displayInput, job.FinalStatus, value, error, job.Attempts, job.FinalWorkerPid, job.Timings(completedAt));
    }

    private void Cancel(PendingJob job)
    {
        if (job.Completion.Task.IsCompleted)
            return;

        if (job.Worker is { } worker && !job.Completion.Task.IsCompleted)
        {
            // In flight: ask the worker; the attempt timeout is replaced by the cancel grace.
            _ = Task.Run(async () =>
            {
                try
                {
                    await worker.SendAsync(Frame.ForCancel(job.Id), CancellationToken.None).ConfigureAwait(false);
                    await Task.Delay(CancelGrace).ConfigureAwait(false);
                    if (!job.Completion.Task.IsCompleted)
                    {
                        Finish(job, PdfJobStatus.Cancelled, "cancelled; the worker did not stop in time and was replaced", new ResultPayload { JobId = job.Id, Status = ResultStatus.Cancelled });
                        await RetireWorkerAsync(worker, PdfPoolEventKind.WorkerStopped, "killed after cancel grace").ConfigureAwait(false);
                        RemovePartialOutput(job);
                    }
                }
                catch (Exception)
                {
                }
            });
        }
        else
        {
            // Queued: it stays in the channel and is dropped when dequeued.
            Finish(job, PdfJobStatus.Cancelled, "cancelled before dispatch", new ResultPayload { JobId = job.Id, Status = ResultStatus.Cancelled });
        }
    }

    // ---- Dispatch ----

    private async Task DispatchLoopAsync()
    {
        var token = _shutdown.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                // A job stays in its queue until a worker is free, so QueueCapacity is exact and no
                // worker is held while nothing is waiting.
                if (!await WaitForJobAsync(token).ConfigureAwait(false))
                    break;

                var worker = await AcquireWorkerAsync(token).ConfigureAwait(false);
                if (worker == null)
                    break;

                if (!_retries.Reader.TryRead(out var job) && !_queue.Reader.TryRead(out job))
                {
                    ReleaseClaim(worker);
                    continue;
                }

                Interlocked.Decrement(ref _queuedCount);
                if (job.Completion.Task.IsCompleted)
                {
                    // Cancelled while queued.
                    ReleaseClaim(worker);
                    continue;
                }

                Dispatch(job, worker);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Raise(PdfPoolEventKind.WorkerStartFailed, 0, 0, "dispatcher stopped: " + ex.Message);
        }
    }

    /// <summary>Waits until a retry or a queued job is available. Returns false when both queues are closed.</summary>
    private async Task<bool> WaitForJobAsync(CancellationToken token)
    {
        var retry = _retries.Reader.WaitToReadAsync(token).AsTask();
        var queued = _queue.Reader.WaitToReadAsync(token).AsTask();
        var first = await Task.WhenAny(retry, queued).ConfigureAwait(false);
        if (await first.ConfigureAwait(false))
            return true;

        // One side is closed; the other may still deliver.
        var other = first == retry ? queued : retry;
        return await other.ConfigureAwait(false);
    }

    private void ReleaseClaim(Worker worker)
    {
        lock (_workersLock)
        {
            if (worker.InUse > 0)
                worker.InUse--;
        }
    }
    /// <summary>Waits for an idle, non-retiring worker. Returns null when the pool shuts down.</summary>
    private async Task<Worker?> AcquireWorkerAsync(CancellationToken token)
    {
        while (true)
        {
            lock (_workersLock)
            {
                // The least loaded worker with a free slot: spreads jobs across workers first.
                var idle = _workers
                    .Where(w => w.InUse < w.Slots && !w.Retiring && !w.HasExited)
                    .OrderBy(w => w.InUse)
                    .FirstOrDefault();
                if (idle != null)
                {
                    idle.InUse++;
                    return idle;
                }

                if (_workers.Count == 0 && _startingWorkers == 0 && _options.MinWorkers == 0 && !token.IsCancellationRequested)
                {
                    // Nothing alive and nothing coming: the sizer only adds workers when some exist or min > 0.
                    _ = StartWorkerAsync(PdfPoolEventKind.ScaledUp, "first job");
                }
            }

            try
            {
                await _workerAvailable.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
    }

    private void Dispatch(PendingJob job, Worker worker)
    {
        if (worker.Retiring || worker.HasExited)
        {
            // The worker went away between the claim and the dispatch: queue the job again.
            ReleaseClaim(worker);
            Interlocked.Increment(ref _queuedCount);
            _retries.Writer.TryWrite(job);
            return;
        }

        job.Attempts++;
        job.Worker = worker;
        job.PagesDone = 0;
        job.CommittingPages = 0;
        job.DispatchedAt = Stopwatch.GetTimestamp();
        worker.Active[job.Id] = job;
        _inFlight[job.Id] = job;

        var timeout = new CancellationTokenSource(_options.JobTimeout);
        job.AttemptTimeout = timeout;
        timeout.Token.Register(() => _ = OnAttemptTimeoutAsync(job, worker));

        Raise(PdfPoolEventKind.JobDispatched, worker.Pid, job.Id, $"attempt {job.Attempts}");

        _ = Task.Run(async () =>
        {
            try
            {
                await worker.SendAsync(Frame.ForJob(job.Payload), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The worker is gone or its pipe is broken; its exit handler reports the crash.
                Raise(PdfPoolEventKind.WorkerCrashed, worker.Pid, job.Id, "could not send the job: " + ex.Message);
                await RetireWorkerAsync(worker, PdfPoolEventKind.WorkerCrashed, "send failed").ConfigureAwait(false);
            }
        });
    }

    private void OnFrame(Worker worker, Frame frame)
    {
        switch (frame.Kind)
        {
            case FrameKind.Result when frame.Result != null:
                OnResult(worker, frame.Result);
                break;
            case FrameKind.Progress when frame.Progress != null:
                // What the worker has staged, and whether it has started moving pages into place: what
                // the coordinator may remove if the worker dies.
                if (_inFlight.TryGetValue(frame.Progress.JobId, out var inProgress) && inProgress.Worker == worker)
                {
                    inProgress.PagesDone = frame.Progress.PagesDone;
                    if (frame.Progress.CommittingPages > 0)
                        inProgress.CommittingPages = frame.Progress.CommittingPages;
                }
                break;
            default:
                Raise(PdfPoolEventKind.WorkerCrashed, worker.Pid, 0, $"unexpected {frame.Kind} frame");
                break;
        }
    }

    /// <summary>Native render intervals reported by workers running with diagnostics on; the most recent 10,000.</summary>
    internal static IReadOnlyList<(int Pid, long StartUtcTicks, long EndUtcTicks)> LastRenderIntervals
    {
        get { lock (s_renderIntervals) return s_renderIntervals.ToArray(); }
    }

    private static readonly Queue<(int Pid, long StartUtcTicks, long EndUtcTicks)> s_renderIntervals = new();

    private void OnResult(Worker worker, ResultPayload result)
    {
        if (!_inFlight.TryRemove(result.JobId, out var job) || job.Worker != worker)
            return;

        if (result.RenderIntervalsUtcTicks != null)
        {
            lock (s_renderIntervals)
            {
                foreach (var interval in result.RenderIntervalsUtcTicks)
                {
                    s_renderIntervals.Enqueue((worker.Pid, interval[0], interval[1]));
                    if (s_renderIntervals.Count > 10_000)
                        s_renderIntervals.Dequeue();
                }
            }
        }

        job.AttemptTimeout?.Dispose();
        job.AttemptTimeout = null;
        job.FinalWorkerPid = worker.Pid;
        Release(worker, job);

        switch (result.Status)
        {
            case ResultStatus.Succeeded:
                Finish(job, PdfJobStatus.Succeeded, null, result);
                break;
            case ResultStatus.Cancelled:
                Finish(job, PdfJobStatus.Cancelled, result.Error ?? "cancelled", result);
                break;
            default:
                Finish(job, PdfJobStatus.Failed, result.Error, result);
                break;
        }

        if (_options.MaxWorkerMemoryBytes is { } limit && worker.WorkingSetBytes > limit)
            _ = RetireWorkerAsync(worker, PdfPoolEventKind.WorkerRetiredForMemory, $"working set {worker.WorkingSetBytes:N0} bytes above {limit:N0}");
    }

    private void Release(Worker worker, PendingJob job)
    {
        lock (_workersLock)
        {
            if (worker.Active.TryRemove(job.Id, out _) && worker.InUse > 0)
                worker.InUse--;
            if (worker.Active.IsEmpty)
                worker.IdleSince = Stopwatch.GetTimestamp();
        }

        _workerAvailable.Release();
    }

    private async Task OnAttemptTimeoutAsync(PendingJob job, Worker worker)
    {
        if (!_inFlight.TryRemove(job.Id, out _))
            return;

        _counters.Increment(ref _counters.TimedOut);
        Raise(PdfPoolEventKind.JobTimedOut, worker.Pid, job.Id, $"attempt {job.Attempts} exceeded {_options.JobTimeout}");
        await RetireWorkerAsync(worker, PdfPoolEventKind.WorkerStopped, "killed after job timeout").ConfigureAwait(false);
        RetryOrFinish(job, PdfJobStatus.TimedOut, $"no result within {_options.JobTimeout}", worker.Pid);
    }

    private void OnWorkerExit(Worker worker)
    {
        PendingJob[] jobs;
        lock (_workersLock)
        {
            if (!_workers.Remove(worker))
                return;
            jobs = worker.Active.Values.ToArray();
            worker.Active.Clear();
            worker.InUse = 0;
        }

        _counters.Increment(ref _counters.WorkersStopped);
        if (!worker.Retiring)
        {
            _counters.Increment(ref _counters.WorkersCrashed);
            Raise(PdfPoolEventKind.WorkerCrashed, worker.Pid, jobs.FirstOrDefault()?.Id ?? 0, "exited unexpectedly");
        }

        // Every job that was on this worker gets another attempt (or its final status).
        foreach (var job in jobs)
        {
            if (!_inFlight.TryRemove(job.Id, out _))
                continue;
            job.AttemptTimeout?.Dispose();
            job.AttemptTimeout = null;
            _counters.Increment(ref _counters.Crashed);
            RetryOrFinish(job, PdfJobStatus.WorkerCrashed, "the worker process exited during the job", worker.Pid);
        }

        _ = worker.DisposeAsync();
        _workerAvailable.Release();
        EnsureMinimumWorkers();
    }

    private void RetryOrFinish(PendingJob job, PdfJobStatus status, string error, int workerPid)
    {
        job.FinalWorkerPid = workerPid;
        job.Worker = null;
        RemovePartialOutput(job); // the worker is gone (crashed, or killed on timeout) and could not clean up itself
        if (job.Attempts < _options.MaxAttempts && !_shutdown.IsCancellationRequested && !job.CallerToken.IsCancellationRequested)
        {
            _counters.Increment(ref _counters.Retried);
            Raise(PdfPoolEventKind.JobRetried, workerPid, job.Id, $"attempt {job.Attempts} ended with {status}");
            Interlocked.Increment(ref _queuedCount);
            _retries.Writer.TryWrite(job);
            return;
        }

        Finish(job, status, error, new ResultPayload { JobId = job.Id, Status = ResultStatus.Failed, Error = error });
    }

    private void Finish(PendingJob job, PdfJobStatus status, string? error, ResultPayload result)
    {
        if (Interlocked.Exchange(ref job.Retired, 1) != 0)
            return;

        job.FinalStatus = status;
        job.FinalError = error;
        job.AttemptTimeout?.Dispose();
        _inFlight.TryRemove(job.Id, out _);
        CleanUp(job);

        switch (status)
        {
            case PdfJobStatus.Succeeded:
                _counters.Increment(ref _counters.Succeeded);
                Raise(PdfPoolEventKind.JobCompleted, job.FinalWorkerPid, job.Id, $"{result.PageCount} pages, {result.ProcessingMs:F0} ms");
                break;
            case PdfJobStatus.Cancelled:
                _counters.Increment(ref _counters.Cancelled);
                Raise(PdfPoolEventKind.JobCancelled, job.FinalWorkerPid, job.Id, error);
                break;
            default:
                _counters.Increment(ref _counters.Failed);
                Raise(PdfPoolEventKind.JobFailed, job.FinalWorkerPid, job.Id, $"{status}: {error}");
                break;
        }

        job.Completion.TrySetResult(result);
    }

    private void CleanUp(PendingJob job)
    {
        if (job.SpooledInput != null)
        {
            try
            {
                File.Delete(job.SpooledInput);
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>
    /// Removes what an attempt whose worker was killed or crashed left behind. Called only for such
    /// attempts: a worker that fails or is cancelled in a managed way cleans up itself, and a job that
    /// never reached a worker has nothing to remove. Only files the attempt owns are touched: its
    /// staged pages (named with the job id) and, if it had begun moving them into place, the final
    /// names it had claimed. Files that were in the directory before are never deleted.
    /// </summary>
    private static void RemovePartialOutput(PendingJob job)
    {
        var payload = job.Payload;
        if (payload.Output == null || job.DispatchedAt == 0)
            return;

        try
        {
            switch (payload.Kind)
            {
                case JobKind.ConvertToPng:
                case JobKind.ConvertToJpeg:
                {
                    if (!Directory.Exists(payload.Output))
                        return;
                    string prefix = payload.FileNamePrefix ?? "page";
                    string extension = payload.Kind == JobKind.ConvertToPng ? "png" : "jpg";
                    foreach (var temp in Directory.GetFiles(payload.Output, $"{prefix}_*.{extension}.{job.Id}.tmp"))
                        TryDelete(temp);
                    for (int page = 1; page <= job.CommittingPages; page++)
                        TryDelete(Path.Combine(payload.Output, $"{prefix}_{page:D3}.{extension}"));
                    break;
                }

                case JobKind.ConvertToTiff:
                    TryDelete(payload.Output + $".{job.Id}.tmp");
                    break;
            }
        }
        catch (Exception)
        {
            // Best effort: a directory that vanished or is unreadable is not the job's problem any more.
        }

        static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
            }
        }
    }


    // ---- Workers and sizing ----

    private async Task StartInitialWorkersAsync(CancellationToken ct)
    {
        var starts = Enumerable.Range(0, _options.MinWorkers).Select(_ => StartWorkerAsync(PdfPoolEventKind.WorkerStarting, "initial", ct)).ToArray();
        await Task.WhenAll(starts).ConfigureAwait(false);
    }

    private async Task StartWorkerAsync(PdfPoolEventKind reason, string detail, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _startingWorkers);
        Raise(PdfPoolEventKind.WorkerStarting, 0, 0, detail);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
            var worker = await Worker.StartAsync(_options, OnFrame, OnStderr, OnWorkerExit, linked.Token).ConfigureAwait(false);

            bool accepted;
            lock (_workersLock)
            {
                accepted = !_shutdown.IsCancellationRequested;
                if (accepted)
                    _workers.Add(worker);
            }

            if (!accepted)
            {
                await worker.DisposeAsync().ConfigureAwait(false);
                return;
            }

            _counters.Increment(ref _counters.WorkersStarted);
            if (reason == PdfPoolEventKind.ScaledUp)
            {
                _counters.Increment(ref _counters.ScaleUps);
                Raise(PdfPoolEventKind.ScaledUp, worker.Pid, 0, $"{Workers} workers: {detail}");
            }

            Raise(PdfPoolEventKind.WorkerReady, worker.Pid, 0, $"ready in {Stopwatch.GetElapsedTime(worker.IdleSince).TotalMilliseconds:F0} ms");
            _workerAvailable.Release();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The pool is going away; Worker.StartAsync has already killed the child.
        }
        catch (PdfPoolException ex)
        {
            Raise(PdfPoolEventKind.WorkerStartFailed, 0, 0, ex.Message);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _startingWorkers);
        }
    }

    private async Task RetireWorkerAsync(Worker worker, PdfPoolEventKind reason, string detail)
    {
        lock (_workersLock)
        {
            if (worker.Retiring)
                return;
            worker.Retiring = true;
        }

        if (reason == PdfPoolEventKind.WorkerRetiredForMemory)
        {
            _counters.Increment(ref _counters.RetiredForMemory);
            Raise(reason, worker.Pid, 0, detail);
            await worker.StopAsync(StopGrace).ConfigureAwait(false);
        }
        else
        {
            Raise(reason, worker.Pid, worker.Active.Keys.FirstOrDefault(), detail);
            await worker.KillAsync().ConfigureAwait(false);
        }
    }

    private void EnsureMinimumWorkers()
    {
        if (_shutdown.IsCancellationRequested)
            return;

        int missing;
        lock (_workersLock)
            missing = _options.MinWorkers - (_workers.Count(w => !w.Retiring) + _startingWorkers);

        for (int i = 0; i < missing; i++)
            _ = StartWorkerAsync(PdfPoolEventKind.WorkerStarting, "replacing a lost worker").ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task SizerLoopAsync()
    {
        var token = _shutdown.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(SizerInterval, token).ConfigureAwait(false);
                Resize();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Resize()
    {
        Worker? toStop = null;
        bool startOne = false;
        long now = Stopwatch.GetTimestamp();

        lock (_workersLock)
        {
            int alive = _workers.Count(w => !w.Retiring) + _startingWorkers;
            int freeSlots = _workers.Where(w => !w.Retiring).Sum(w => w.Slots - w.InUse);
            bool saturated = QueuedJobs > 0 && freeSlots <= 0 && _startingWorkers == 0;

            if (saturated)
            {
                if (_allBusySince == 0)
                    _allBusySince = now;
                if (alive < _options.MaxWorkers && Stopwatch.GetElapsedTime(_allBusySince, now) >= _options.ScaleUpAfter)
                {
                    startOne = true;
                    _allBusySince = 0;
                }
            }
            else
            {
                _allBusySince = 0;
            }

            if (!startOne && alive > _options.MinWorkers && QueuedJobs == 0)
            {
                toStop = _workers
                    .Where(w => !IsBusy(w) && !w.Retiring)
                    .OrderBy(w => w.IdleSince)
                    .FirstOrDefault(w => Stopwatch.GetElapsedTime(w.IdleSince, now) >= _options.IdleTimeout);
                if (toStop != null)
                    toStop.Retiring = true;
            }
        }

        if (startOne)
            _ = StartWorkerAsync(PdfPoolEventKind.ScaledUp, $"{QueuedJobs} jobs waiting").ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);

        if (toStop != null)
        {
            _counters.Increment(ref _counters.ScaleDowns);
            Raise(PdfPoolEventKind.ScaledDown, toStop.Pid, 0, $"idle for {_options.IdleTimeout}");
            _ = toStop.StopAsync(StopGrace);
        }
    }

    private void OnStderr(Worker worker, string line)
        => Raise(PdfPoolEventKind.WorkerStopped, worker.Pid, 0, "stderr: " + line);

    private void Raise(PdfPoolEventKind kind, int pid, long jobId, string? detail)
    {
        var handlers = Events;
        if (handlers == null)
            return;
        try
        {
            handlers(this, new PdfPoolEvent(kind, pid, jobId, detail));
        }
        catch (Exception)
        {
        }
    }

    // ---- Shutdown ----

    /// <summary>
    /// Stops accepting jobs, cancels queued ones, lets in-flight ones finish up to
    /// <see cref="PdfPoolOptions.JobTimeout"/>, then stops every worker.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        _queue.Writer.TryComplete();
        _retries.Writer.TryComplete();
        _shutdown.Cancel();
        _workerAvailable.Release(int.MaxValue / 2);

        await Task.WhenAny(_dispatcher, Task.Delay(1000)).ConfigureAwait(false);
        await Task.WhenAny(_sizer, Task.Delay(1000)).ConfigureAwait(false);

        // Workers still starting are cancelled by the shutdown token and killed by Worker.StartAsync;
        // wait for that so no child outlives the pool and no start touches a disposed token source.
        long waitStart = Stopwatch.GetTimestamp();
        while (Volatile.Read(ref _startingWorkers) > 0 && Stopwatch.GetElapsedTime(waitStart) < _options.WorkerStartTimeout + TimeSpan.FromSeconds(10))
            await Task.Delay(20).ConfigureAwait(false);

        // Drain whatever is still queued.
        while (_queue.Reader.TryRead(out var queued))
            Finish(queued, PdfJobStatus.Cancelled, "the pool was disposed", new ResultPayload { JobId = queued.Id, Status = ResultStatus.Cancelled });
        while (_retries.Reader.TryRead(out var retry))
            Finish(retry, PdfJobStatus.Cancelled, "the pool was disposed", new ResultPayload { JobId = retry.Id, Status = ResultStatus.Cancelled });

        // In-flight jobs may finish; wait up to the job timeout.
        var pending = _inFlight.Values.Select(j => j.Completion.Task).ToArray();
        if (pending.Length > 0)
            await Task.WhenAny(Task.WhenAll(pending), Task.Delay(_options.JobTimeout)).ConfigureAwait(false);
        var abandoned = _inFlight.Values.ToArray();
        foreach (var job in abandoned)
            Finish(job, PdfJobStatus.Cancelled, "the pool was disposed", new ResultPayload { JobId = job.Id, Status = ResultStatus.Cancelled });

        // Taken out of the table here, so their exit handlers see nothing left to do.
        Worker[] workers;
        lock (_workersLock)
        {
            workers = _workers.ToArray();
            _workers.Clear();
            foreach (var w in workers)
                w.Retiring = true;
        }

        await Task.WhenAll(workers.Select(w => w.StopAsync(StopGrace))).ConfigureAwait(false);
        await Task.WhenAll(workers.Select(w => w.DisposeAsync().AsTask())).ConfigureAwait(false);

        // Their workers are gone now, so what those jobs had staged can be removed.
        foreach (var job in abandoned)
            RemovePartialOutput(job);

        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (Exception)
        {
        }

        _shutdown.Dispose();
        _workerAvailable.Dispose();
    }

    private sealed class Counters
    {
        public long Submitted, Succeeded, Failed, TimedOut, Cancelled, Crashed, Retried;
        public long WorkersStarted, WorkersStopped, WorkersCrashed, RetiredForMemory, ScaleUps, ScaleDowns;

        public void Increment(ref long counter) => Interlocked.Increment(ref counter);

        public PdfPoolStatistics Snapshot() => new(
            Interlocked.Read(ref Submitted), Interlocked.Read(ref Succeeded), Interlocked.Read(ref Failed),
            Interlocked.Read(ref TimedOut), Interlocked.Read(ref Cancelled), Interlocked.Read(ref Crashed),
            Interlocked.Read(ref Retried), Interlocked.Read(ref WorkersStarted), Interlocked.Read(ref WorkersStopped),
            Interlocked.Read(ref WorkersCrashed), Interlocked.Read(ref RetiredForMemory),
            Interlocked.Read(ref ScaleUps), Interlocked.Read(ref ScaleDowns));
    }
}
