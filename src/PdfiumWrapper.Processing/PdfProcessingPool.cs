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
    // Jobs in either queue, so they can be failed where they wait (they are dropped when dequeued).
    private readonly ConcurrentDictionary<long, PendingJob> _waiting = new();
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
    private int _consecutiveStartFailures;
    // Set by the dispatcher while the job at the head must run alone and no worker is empty.
    private bool _headWaitsForEmptyWorker;
    // Set when the dispatcher has stopped on an unexpected error: every job from then on is Failed with it.
    private string? _fault;
    // FailAll's kill-clean-fail sequence for the jobs that were in flight; awaited by DisposeAsync.
    private Task _failing = Task.CompletedTask;
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

    /// <summary>Process ids of the workers in the table, retirees included, for tests that kill them.</summary>
    internal int[] WorkerPidsForTests
    {
        get
        {
            lock (_workersLock)
                return _workers.Select(w => w.Pid).ToArray();
        }
    }

    /// <summary>Slots taken across all workers (jobs in flight plus the dispatcher's claim), for tests that check none leaks.</summary>
    internal int SlotsInUseForTests
    {
        get
        {
            lock (_workersLock)
                return _workers.Sum(w => w.InUse);
        }
    }

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
        _waiting[job.Id] = job;
        try
        {
            if (!_queue.Writer.TryWrite(job))
            {
                if (Volatile.Read(ref _fault) != null)
                    throw new ChannelClosedException();   // the queue is closed, not full
                Raise(PdfPoolEventKind.QueueFull, 0, job.Id, $"{QueuedJobs} jobs queued");
                await _queue.Writer.WriteAsync(job, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled while waiting for a queue slot: the same Cancelled result as a job cancelled
            // later, never an exception.
            NotQueued(job);
            Finish(job, PdfJobStatus.Cancelled, "cancelled while waiting for a queue slot", new ResultPayload { JobId = job.Id, Status = ResultStatus.Cancelled });
        }
        catch (ChannelClosedException) when (Volatile.Read(ref _fault) is { } fault)
        {
            // The dispatcher has stopped: a status, like every other outcome.
            NotQueued(job);
            Finish(job, PdfJobStatus.Failed, fault, new ResultPayload { JobId = job.Id, Status = ResultStatus.Failed, Error = fault });
        }
        catch (Exception ex)
        {
            NotQueued(job);
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

    /// <summary>Undoes the queue bookkeeping for a job whose write to a queue failed.</summary>
    private void NotQueued(PendingJob job)
    {
        Interlocked.Decrement(ref _queuedCount);
        _waiting.TryRemove(job.Id, out _);
    }

    /// <summary>
    /// Puts a job that has been taken out of the queues back, ahead of new jobs. If the pool is
    /// shutting down and the retry queue is closed, the job ends here instead of being lost.
    /// </summary>
    private void Requeue(PendingJob job)
    {
        Interlocked.Increment(ref _queuedCount);
        _waiting[job.Id] = job;
        if (_retries.Writer.TryWrite(job))
            return;

        NotQueued(job);
        if (Volatile.Read(ref _fault) is { } fault)
            Finish(job, PdfJobStatus.Failed, fault, new ResultPayload { JobId = job.Id, Status = ResultStatus.Failed, Error = fault });
        else
            Finish(job, PdfJobStatus.Cancelled, "the pool was disposed", new ResultPayload { JobId = job.Id, Status = ResultStatus.Cancelled });
    }

    /// <summary>Wakes the dispatcher. Safe after disposal, when a late callback may still get here.</summary>
    private void SignalDispatcher()
    {
        try
        {
            _workerAvailable.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Cancel(PendingJob job)
    {
        if (job.Completion.Task.IsCompleted)
            return;

        if (TryWithdraw(job, out var worker))
        {
            // Queued: it stays in the channel and is dropped when it reaches the head of its queue.
            Finish(job, PdfJobStatus.Cancelled, "cancelled before dispatch", new ResultPayload { JobId = job.Id, Status = ResultStatus.Cancelled });
            SignalDispatcher();
        }
        else if (worker is { } running)
        {
            // In flight: ask the worker; the attempt timeout is replaced by the cancel grace.
            _ = Task.Run(async () =>
            {
                try
                {
                    await running.SendAsync(Frame.ForCancel(job.Id), CancellationToken.None).ConfigureAwait(false);
                    await Task.Delay(CancelGrace).ConfigureAwait(false);
                    // Taken out of flight first, so the worker's exit does not run it again. Its result
                    // is given only once the worker is dead and its staged pages are gone: a caller
                    // never holds a Cancelled result while a worker can still write its output.
                    if (TryEndAttempt(job, running))
                    {
                        // The other jobs on this worker are bystanders: they run again without using an attempt.
                        await KillWorkerAsync(running, PdfPoolEventKind.WorkerStopped, "killed after cancel grace", job.Id).ConfigureAwait(false);
                        RemovePartialOutput(job);
                        Finish(job, PdfJobStatus.Cancelled, "cancelled; the worker did not stop in time and was replaced", new ResultPayload { JobId = job.Id, Status = ResultStatus.Cancelled });
                    }
                }
                catch (Exception)
                {
                }
            });
        }
    }

    /// <summary>
    /// Takes a job that has not reached a worker out of dispatch for good, so the caller can end it
    /// where it waits. Decided under the worker lock, where <see cref="Dispatch"/> registers a job, so
    /// a job is either withdrawn or in flight, never both. Returns false, with the worker running it,
    /// if the job is in flight; false with null if it already ended.
    /// </summary>
    private bool TryWithdraw(PendingJob job, out Worker? worker)
    {
        lock (_workersLock)
        {
            worker = null;
            if (job.Completion.Task.IsCompleted)
                return false;
            worker = job.Worker;
            if (worker != null)
                return false;
            job.Withdrawn = true;
            return true;
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

                if (!TryPeekNext(out var source, out var next))
                    continue;

                // A job that ended while it waited (cancelled, or failed because no worker could be
                // started) leaves at once and frees its queue slot, without waiting for a worker.
                if (next.Completion.Task.IsCompleted)
                {
                    TryDequeue(source, out _);
                    Volatile.Write(ref _headWaitsForEmptyWorker, false);
                    continue;
                }

                var worker = ClaimWorker(next.RunAlone);
                // A job that must run alone waits at the head for an empty worker, and nothing behind
                // it is dispatched meanwhile: that is what lets a busy worker drain for it. The sizer
                // sees this and starts a worker for it, as it does when every slot is taken.
                Volatile.Write(ref _headWaitsForEmptyWorker, worker == null && next.RunAlone);
                if (worker == null)
                {
                    await _workerAvailable.WaitAsync(token).ConfigureAwait(false);
                    continue;
                }

                // The job peeked above: this loop is the queues' only reader while the pool runs
                // (DisposeAsync drains them once it has stopped this loop).
                if (!TryDequeue(source, out var job))
                {
                    ReleaseClaim(worker);
                    continue;
                }

                Dispatch(job, worker);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            FailAll("the pool's dispatcher stopped: " + ex.Message);
        }
        catch (Exception)
        {
            // Shutting down anyway; DisposeAsync ends every job.
        }
    }

    /// <summary>The next job to dispatch, retries first, and the queue it is in.</summary>
    private bool TryPeekNext(out ChannelReader<PendingJob> source, out PendingJob job)
    {
        source = _retries.Reader;
        if (source.TryPeek(out job!))
            return true;
        source = _queue.Reader;
        return source.TryPeek(out job!);
    }

    private bool TryDequeue(ChannelReader<PendingJob> source, out PendingJob job)
    {
        if (!source.TryRead(out job!))
            return false;
        Interlocked.Decrement(ref _queuedCount);
        _waiting.TryRemove(job.Id, out _);
        return true;
    }

    /// <summary>
    /// Called on the dispatcher's own task when it stops on an unexpected error. Nothing would ever
    /// dispatch again, so every job waiting or in flight, and every job submitted later, is Failed
    /// with the reason, and the workers are killed so no job the caller was told failed writes output.
    /// </summary>
    private void FailAll(string reason)
    {
        Volatile.Write(ref _fault, reason);
        Raise(PdfPoolEventKind.WorkerStartFailed, 0, 0, reason);

        _queue.Writer.TryComplete();
        _retries.Writer.TryComplete();
        while (_retries.Reader.TryRead(out var waiting) || _queue.Reader.TryRead(out waiting))
        {
            Interlocked.Decrement(ref _queuedCount);
            _waiting.TryRemove(waiting.Id, out _);
            Finish(waiting, PdfJobStatus.Failed, reason, new ResultPayload { JobId = waiting.Id, Status = ResultStatus.Failed, Error = reason });
        }

        // Jobs in flight are taken out of every ordinary completion path (result, timeout, worker
        // exit) and failed only once their workers are dead and what they staged is removed, so no
        // caller holds a Failed result while a worker can still write its output. DisposeAsync waits
        // for this.
        var inFlight = new List<PendingJob>();
        Worker[] workers;
        lock (_workersLock)
        {
            foreach (var job in _inFlight.Values.ToArray())
            {
                if (_inFlight.TryRemove(KeyValuePair.Create(job.Id, job)))
                    inFlight.Add(job);
            }

            workers = _workers.ToArray();
        }

        Volatile.Write(ref _failing, Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(workers.Select(w => KillWorkerAsync(w, PdfPoolEventKind.WorkerStopped, "the dispatcher stopped"))).ConfigureAwait(false);
            }
            finally
            {
                foreach (var job in inFlight)
                {
                    RemovePartialOutput(job);
                    Finish(job, PdfJobStatus.Failed, reason, new ResultPayload { JobId = job.Id, Status = ResultStatus.Failed, Error = reason });
                }
            }
        }));
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
            if (worker.InUse == 0)
                worker.Exclusive = false;
        }

        StopIfDrained(worker);
    }

    /// <summary>
    /// Claims a slot on a worker for the next job, or returns null when none is free. A job that
    /// must run alone takes a worker with nothing on it and keeps it to itself; any other job
    /// takes the least loaded worker with a free slot, which spreads jobs across workers first.
    /// </summary>
    private Worker? ClaimWorker(bool alone)
    {
        bool startOne;
        lock (_workersLock)
        {
            var usable = _workers.Where(w => !w.Retiring && !w.Exclusive && !w.HasExited);
            var chosen = alone
                ? usable.FirstOrDefault(w => w.InUse == 0)
                : usable.Where(w => w.InUse < w.Slots).OrderBy(w => w.InUse).FirstOrDefault();
            if (chosen != null)
            {
                chosen.InUse++;
                chosen.Exclusive = alone;
                return chosen;
            }

            // Nothing alive and nothing coming: the sizer only adds workers when some exist or min > 0.
            startOne = _workers.Count == 0 && _startingWorkers == 0 && _options.MinWorkers == 0 && !_shutdown.IsCancellationRequested;
            if (startOne)
                ReserveStarts(1);
        }

        if (startOne)
            _ = StartWorkerAsync(PdfPoolEventKind.ScaledUp, "first job").ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
        return null;
    }

    private void Dispatch(PendingJob job, Worker worker)
    {
        bool gone;
        lock (_workersLock)
        {
            // Checked and registered under the lock, so a worker retired for memory either sees
            // this job in Active (and waits for it) or this dispatch sees it retiring; a worker's exit
            // handler sees the job in both Active and the in-flight table, or in neither; and a job
            // ended where it waited (TryWithdraw) is never dispatched. A worker no longer in the
            // table may already be disposed, so HasExited is asked only of one that is.
            gone = !_workers.Contains(worker) || worker.Retiring || worker.HasExited || job.Withdrawn || job.Completion.Task.IsCompleted;
            if (!gone)
            {
                job.Attempts++;
                job.Worker = worker;
                job.PagesDone = 0;
                job.CommittingPages = 0;
                job.DispatchedAt = Stopwatch.GetTimestamp();
                worker.Active[job.Id] = job;
                _inFlight[job.Id] = job;
            }
        }

        if (gone)
        {
            // The worker went away between the claim and the dispatch: queue the job again (or drop
            // it, if it ended or was withdrawn while being dequeued; whoever withdrew it ends it).
            ReleaseClaim(worker);
            if (!job.Withdrawn && !job.Completion.Task.IsCompleted)
                Requeue(job);
            return;
        }

        DispatchHookForTests?.Invoke(job.Id);

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
                await KillWorkerAsync(worker, PdfPoolEventKind.WorkerCrashed, "send failed").ConfigureAwait(false);
            }
        });
    }

    /// <summary>Test hook: called with the job id once a job is registered in flight, on the dispatcher's task. An exception stops the dispatcher.</summary>
    internal Action<long>? DispatchHookForTests { get; set; }

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
        // The slot is this worker's to give back whatever became of the job meanwhile (cancelled,
        // failed where it waited, or already retried elsewhere after a kill). The job itself is
        // completed only if this is its current attempt: a late frame from a worker the job has left
        // must not end, or unregister, the attempt that replaced it.
        PendingJob? job;
        bool current;
        lock (_workersLock)
        {
            if (!worker.Active.TryRemove(result.JobId, out job))
                return;
            if (worker.InUse > 0)
                worker.InUse--;
            if (worker.InUse == 0)
                worker.Exclusive = false;
            if (worker.Active.IsEmpty)
                worker.IdleSince = Stopwatch.GetTimestamp();
            current = job.Worker == worker && _inFlight.TryRemove(KeyValuePair.Create(job.Id, job));
        }

        StopIfDrained(worker);
        SignalDispatcher();
        if (!current)
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
            RetireForMemory(worker, $"working set {worker.WorkingSetBytes:N0} bytes above {limit:N0}");
    }

    /// <summary>
    /// Ends <paramref name="job"/>'s attempt on <paramref name="worker"/> if it is still in flight
    /// there; true for exactly one caller. Under the worker lock, where <see cref="Dispatch"/>
    /// registers attempts, so a stale caller (an old timer, a late frame) never takes a later attempt.
    /// </summary>
    private bool TryEndAttempt(PendingJob job, Worker worker)
    {
        lock (_workersLock)
            return job.Worker == worker && _inFlight.TryRemove(KeyValuePair.Create(job.Id, job));
    }

    private async Task OnAttemptTimeoutAsync(PendingJob job, Worker worker)
    {
        if (!TryEndAttempt(job, worker))
            return;

        _counters.Increment(ref _counters.TimedOut);
        Raise(PdfPoolEventKind.JobTimedOut, worker.Pid, job.Id, $"attempt {job.Attempts} exceeded {_options.JobTimeout}");
        // The other jobs on this worker are bystanders: they run again without using an attempt.
        await KillWorkerAsync(worker, PdfPoolEventKind.WorkerStopped, "killed after job timeout", job.Id).ConfigureAwait(false);
        RetryOrFinish(job, PdfJobStatus.TimedOut, $"no result within {_options.JobTimeout}", worker.Pid);
    }

    private void OnWorkerExit(Worker worker)
    {
        PendingJob[] jobs;
        PendingJob[] live;
        long killedFor;
        lock (_workersLock)
        {
            if (!_workers.Remove(worker))
                return;
            jobs = worker.Active.Values.ToArray();
            worker.Active.Clear();
            worker.InUse = 0;
            worker.Exclusive = false;
            killedFor = worker.KilledFor;
            // Jobs whose attempt here has not already ended (completed, cancelled, timed out).
            live = jobs.Where(j => j.Worker == worker && _inFlight.TryRemove(KeyValuePair.Create(j.Id, j))).ToArray();
        }

        _counters.Increment(ref _counters.WorkersStopped);
        if (!worker.Retiring)
        {
            _counters.Increment(ref _counters.WorkersCrashed);
            Raise(PdfPoolEventKind.WorkerCrashed, worker.Pid, jobs.FirstOrDefault()?.Id ?? 0, "exited unexpectedly");
        }

        // Every job that was still running on this worker gets another attempt (or its final
        // status). Only the job that ended the worker is charged for it. On a worker killed for
        // another job's timeout or cancellation, every job left is a bystander. On a worker that
        // died with one job, that job is the cause. On a worker that died with several, the cause is
        // unknown: each runs again alone, uncharged, so the one that crashes again is charged then.
        foreach (var job in live)
        {
            job.AttemptTimeout?.Dispose();
            job.AttemptTimeout = null;
            if (killedFor != 0)
            {
                RetryOrFinish(job, PdfJobStatus.WorkerCrashed, $"the worker was killed because of job {killedFor}", worker.Pid, charged: false);
            }
            else if (live.Length > 1)
            {
                job.RunAlone = true;
                RetryOrFinish(job, PdfJobStatus.WorkerCrashed, "the worker process exited during the job", worker.Pid, charged: false);
            }
            else
            {
                RetryOrFinish(job, PdfJobStatus.WorkerCrashed, "the worker process exited during the job", worker.Pid);
            }
        }

        _ = worker.DisposeAsync();
        SignalDispatcher();
        EnsureMinimumWorkers();
    }

    /// <summary>
    /// Retries a job whose worker crashed or was killed, or gives it its final status.
    /// <paramref name="charged"/> is false for a job that was only a bystander: it runs again
    /// without using an attempt, at most <see cref="PdfPoolOptions.MaxAttempts"/> times, after
    /// which its attempts are charged as usual so no job is retried forever.
    /// </summary>
    private void RetryOrFinish(PendingJob job, PdfJobStatus status, string error, int workerPid, bool charged = true)
    {
        job.FinalWorkerPid = workerPid;
        lock (_workersLock)
            job.Worker = null;
        RemovePartialOutput(job); // the worker is gone (crashed, or killed on timeout) and could not clean up itself

        if (Volatile.Read(ref _fault) is { } fault)
        {
            Finish(job, PdfJobStatus.Failed, fault, new ResultPayload { JobId = job.Id, Status = ResultStatus.Failed, Error = fault });
            return;
        }

        bool free = !charged && job.FreeRetries < _options.MaxAttempts;
        if (!free && status == PdfJobStatus.WorkerCrashed)
            _counters.Increment(ref _counters.Crashed);

        if ((free || job.Attempts < _options.MaxAttempts) && !_shutdown.IsCancellationRequested && !job.CallerToken.IsCancellationRequested)
        {
            if (free)
            {
                job.FreeRetries++;
                job.Attempts--;
            }

            _counters.Increment(ref _counters.Retried);
            Raise(PdfPoolEventKind.JobRetried, workerPid, job.Id, free
                ? $"run again without using an attempt: {error}"
                : $"attempt {job.Attempts} ended with {status}");
            Requeue(job);
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
                    // The same naming contract the worker writes with (PdfDocument.PageFileName).
                    var format = payload.Kind == JobKind.ConvertToPng ? ImageFormat.Png : ImageFormat.Jpeg;
                    string pattern = PdfDocument.PageFileSearchPattern(payload.FileNamePrefix, format) + $".{job.Id}.tmp";
                    foreach (var temp in Directory.GetFiles(payload.Output, pattern))
                        TryDelete(temp);
                    for (int page = 1; page <= job.CommittingPages; page++)
                        TryDelete(Path.Combine(payload.Output, PdfDocument.PageFileName(payload.FileNamePrefix, page, format)));
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
        lock (_workersLock)
            ReserveStarts(_options.MinWorkers);
        var starts = Enumerable.Range(0, _options.MinWorkers).Select(_ => StartWorkerAsync(PdfPoolEventKind.WorkerStarting, "initial", ct)).ToArray();
        await Task.WhenAll(starts).ConfigureAwait(false);
    }

    /// <summary>
    /// Counts <paramref name="count"/> starts in <c>_startingWorkers</c> before they begin. Called
    /// under <c>_workersLock</c> by whoever decided there is room for them, so a capacity check and
    /// its reservation are one step and concurrent deciders never exceed MaxWorkers. Each reserved
    /// start must be followed by one <see cref="StartWorkerAsync"/>, which releases it.
    /// </summary>
    private void ReserveStarts(int count)
    {
        Debug.Assert(Monitor.IsEntered(_workersLock));
        Interlocked.Add(ref _startingWorkers, count);
    }

    /// <summary>Starts a worker whose place was reserved with <see cref="ReserveStarts"/>.</summary>
    private async Task StartWorkerAsync(PdfPoolEventKind reason, string detail, CancellationToken ct = default)
    {
        try
        {
            Raise(PdfPoolEventKind.WorkerStarting, 0, 0, detail);
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

            Interlocked.Exchange(ref _consecutiveStartFailures, 0);
            _counters.Increment(ref _counters.WorkersStarted);
            if (reason == PdfPoolEventKind.ScaledUp)
            {
                _counters.Increment(ref _counters.ScaleUps);
                Raise(PdfPoolEventKind.ScaledUp, worker.Pid, 0, $"{Workers} workers: {detail}");
            }

            Raise(PdfPoolEventKind.WorkerReady, worker.Pid, 0, $"ready in {Stopwatch.GetElapsedTime(worker.IdleSince).TotalMilliseconds:F0} ms");
            SignalDispatcher();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The pool is going away; Worker.StartAsync has already killed the child.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Raise(PdfPoolEventKind.WorkerStartFailed, 0, 0, ex.Message);
            OnStartFailed(ex.Message);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _startingWorkers);
        }
    }

    /// <summary>
    /// Counts a failed start. Once <see cref="PdfPoolOptions.MaxConsecutiveStartFailures"/> starts in
    /// a row have failed and no worker is alive to serve the queue, the jobs waiting for a worker
    /// would wait forever: they are Failed with the start error. They stay in their queue, marked
    /// complete, and the dispatcher drops them. A job submitted later waits for the next start.
    /// </summary>
    private void OnStartFailed(string error)
    {
        int failures = Interlocked.Increment(ref _consecutiveStartFailures);
        if (failures < _options.MaxConsecutiveStartFailures || _shutdown.IsCancellationRequested)
            return;

        lock (_workersLock)
        {
            if (_workers.Any(w => !w.Retiring && !w.HasExited))
                return;
        }

        string reason = $"no worker could be started ({failures} attempts in a row failed): {error}";
        foreach (var job in _waiting.Values)
        {
            // A worker may have started meanwhile and taken this job: then it runs, and is not failed.
            if (TryWithdraw(job, out _))
                Finish(job, PdfJobStatus.Failed, reason, new ResultPayload { JobId = job.Id, Status = ResultStatus.Failed, Error = reason });
        }

        SignalDispatcher();
    }

    /// <summary>
    /// Kills a worker now. <paramref name="forJob"/> is the job whose timeout or cancellation is the
    /// reason, if any: the worker's other jobs are then bystanders and are not charged an attempt.
    /// Unlike a graceful stop, this also applies to a worker already retiring.
    /// </summary>
    private async Task KillWorkerAsync(Worker worker, PdfPoolEventKind reason, string detail, long forJob = 0)
    {
        bool first;
        lock (_workersLock)
        {
            first = !worker.Killing;
            worker.Killing = true;
            worker.Retiring = true;
            worker.StopWhenDrained = false;
            if (forJob != 0 && worker.KilledFor == 0)
                worker.KilledFor = forJob;
        }

        if (first)
            Raise(reason, worker.Pid, forJob != 0 ? forJob : worker.Active.Keys.FirstOrDefault(), detail);
        await worker.KillAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Retires a worker whose working set is over the limit: it takes no new job, and is shut down
    /// once the jobs it is running have finished. Each of those is bounded by its own JobTimeout,
    /// whose expiry kills the worker as usual.
    /// </summary>
    private void RetireForMemory(Worker worker, string detail)
    {
        lock (_workersLock)
        {
            if (worker.Retiring)
                return;
            worker.Retiring = true;
            worker.StopWhenDrained = true;
        }

        _counters.Increment(ref _counters.RetiredForMemory);
        Raise(PdfPoolEventKind.WorkerRetiredForMemory, worker.Pid, 0, detail);
        StopIfDrained(worker);
    }

    /// <summary>Shuts down a worker retired for memory once it has no job and no claim left.</summary>
    private void StopIfDrained(Worker worker)
    {
        lock (_workersLock)
        {
            if (!worker.StopWhenDrained || worker.InUse > 0 || !worker.Active.IsEmpty)
                return;
            worker.StopWhenDrained = false;
        }

        _ = worker.StopAsync(StopGrace);
    }

    private void EnsureMinimumWorkers()
    {
        if (_shutdown.IsCancellationRequested || Volatile.Read(ref _fault) != null)
            return;

        int missing;
        lock (_workersLock)
        {
            // MinWorkers counts workers that take jobs; MaxWorkers counts processes, including
            // retirees still finishing their jobs. A replacement waits for a retiree to go if
            // starting it now would exceed MaxWorkers; the retiree's exit calls this again.
            int processes = _workers.Count + _startingWorkers;
            missing = Math.Min(_options.MinWorkers - (_workers.Count(w => !w.Retiring) + _startingWorkers),
                _options.MaxWorkers - processes);
            // Reserved here, under the lock where capacity was counted: two exit handlers running
            // at once must not both count the same free place.
            if (missing > 0)
                ReserveStarts(missing);
        }

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
        if (Volatile.Read(ref _fault) != null)
            return;

        lock (_workersLock)
        {
            int alive = _workers.Count(w => !w.Retiring) + _startingWorkers;
            // Retirees still draining are processes too: MaxWorkers bounds those, not only workers taking jobs.
            int processes = _workers.Count + _startingWorkers;
            int freeSlots = _workers.Where(w => !w.Retiring && !w.Exclusive).Sum(w => w.Slots - w.InUse);
            // A job that must run alone needs an empty worker, not a free slot: while it waits at the
            // head with every worker partly busy, nothing is dispatched, so the pool is as stuck as
            // when every slot is taken.
            bool aloneWaits = Volatile.Read(ref _headWaitsForEmptyWorker)
                && !_workers.Any(w => !w.Retiring && !w.Exclusive && w.InUse == 0);
            bool saturated = QueuedJobs > 0 && (freeSlots <= 0 || aloneWaits) && _startingWorkers == 0;

            if (saturated)
            {
                if (_allBusySince == 0)
                    _allBusySince = now;
                if (processes < _options.MaxWorkers && Stopwatch.GetElapsedTime(_allBusySince, now) >= _options.ScaleUpAfter)
                {
                    startOne = true;
                    ReserveStarts(1);
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
                    .Where(w => !IsBusy(w) && w.InUse == 0 && !w.Retiring)
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
        await Volatile.Read(ref _failing).ConfigureAwait(false);

        // Workers still starting are cancelled by the shutdown token and killed by Worker.StartAsync;
        // wait for that so no child outlives the pool and no start touches a disposed token source.
        long waitStart = Stopwatch.GetTimestamp();
        while (Volatile.Read(ref _startingWorkers) > 0 && Stopwatch.GetElapsedTime(waitStart) < _options.WorkerStartTimeout + TimeSpan.FromSeconds(10))
            await Task.Delay(20).ConfigureAwait(false);

        // Drain whatever is still queued. A retry decided from here on finds its queue closed and
        // is cancelled by Requeue instead of being lost.
        while (_retries.Reader.TryRead(out var queued) || _queue.Reader.TryRead(out queued))
        {
            Interlocked.Decrement(ref _queuedCount);
            _waiting.TryRemove(queued.Id, out _);
            Finish(queued, PdfJobStatus.Cancelled, "the pool was disposed", new ResultPayload { JobId = queued.Id, Status = ResultStatus.Cancelled });
        }

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
