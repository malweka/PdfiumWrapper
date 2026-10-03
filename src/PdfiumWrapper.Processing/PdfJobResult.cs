namespace PdfiumWrapper.Processing;

/// <summary>Final status of a job.</summary>
public enum PdfJobStatus
{
    /// <summary>The job completed; <see cref="PdfJobResult{T}.Value"/> is set.</summary>
    Succeeded,
    /// <summary>
    /// The document or the request was rejected (bad file, wrong password, missing output path). Not
    /// retried. Also reported when no worker could be started (see
    /// <see cref="PdfPoolOptions.MaxConsecutiveStartFailures"/>) or the pool's dispatcher stopped on an
    /// unexpected error; the error says which.
    /// </summary>
    Failed,
    /// <summary>An attempt exceeded <see cref="PdfPoolOptions.JobTimeout"/>; the worker was killed. Reported after the last attempt.</summary>
    TimedOut,
    /// <summary>The caller cancelled the job, or the pool was disposed while it waited.</summary>
    Cancelled,
    /// <summary>The worker process died during the job (for example a native abort on a damaged PDF). Reported after the last attempt.</summary>
    WorkerCrashed,
}

/// <summary>How long a job spent waiting and running.</summary>
/// <param name="Queued">From submission to dispatch to a worker (the last attempt's dispatch).</param>
/// <param name="Processing">From dispatch to result, for the last attempt.</param>
/// <param name="Total">From submission to the final result, all attempts included.</param>
public sealed record PdfJobTimings(TimeSpan Queued, TimeSpan Processing, TimeSpan Total);

/// <summary>The outcome of one job.</summary>
/// <typeparam name="T">The operation's value type.</typeparam>
/// <param name="Input">The input path, or the name given with a byte array or stream input.</param>
/// <param name="Status">Final status.</param>
/// <param name="Value">The operation's value when <paramref name="Status"/> is <see cref="PdfJobStatus.Succeeded"/>.</param>
/// <param name="Error">The worker's exception type and message, or the pool's reason, when not succeeded.</param>
/// <param name="Attempts">
/// Attempts charged to the job: 1 unless its own worker crashed or it timed out, 0 if it never reached
/// a worker. A run ended only by a neighbour on the same worker is not counted (see <see cref="PdfPoolOptions.MaxAttempts"/>).
/// </param>
/// <param name="WorkerPid">Process id of the worker that produced the result, or 0.</param>
/// <param name="Timings">Queue and processing times.</param>
public sealed record PdfJobResult<T>(
    string Input,
    PdfJobStatus Status,
    T? Value,
    string? Error,
    int Attempts,
    int WorkerPid,
    PdfJobTimings Timings)
{
    /// <summary>True when the job succeeded and <see cref="Value"/> is set.</summary>
    public bool IsSuccess => Status == PdfJobStatus.Succeeded;
}

/// <summary>Result of an image conversion: one file per page, in page order.</summary>
public sealed record ImageFiles(int PageCount, IReadOnlyList<string> Files, long TotalBytes);

/// <summary>Result of a TIFF conversion: one multi-page file.</summary>
public sealed record TiffFile(int PageCount, string Path, long Bytes);

/// <summary>Kinds of events a pool raises.</summary>
public enum PdfPoolEventKind
{
    WorkerStarting,
    WorkerReady,
    WorkerStopped,
    WorkerCrashed,
    WorkerStartFailed,
    WorkerRetiredForMemory,
    ScaledUp,
    ScaledDown,
    JobDispatched,
    JobCompleted,
    JobFailed,
    JobTimedOut,
    JobRetried,
    JobCancelled,
    QueueFull,
}

/// <summary>Something the pool did or observed. Raised on a pool thread; handlers must be quick and must not throw.</summary>
public sealed class PdfPoolEvent : EventArgs
{
    internal PdfPoolEvent(PdfPoolEventKind kind, int workerPid, long jobId, string? detail)
    {
        Kind = kind;
        WorkerPid = workerPid;
        JobId = jobId;
        Detail = detail;
        Timestamp = DateTimeOffset.UtcNow;
    }

    public PdfPoolEventKind Kind { get; }
    /// <summary>The worker involved, or 0.</summary>
    public int WorkerPid { get; }
    /// <summary>The job involved, or 0.</summary>
    public long JobId { get; }
    public string? Detail { get; }
    public DateTimeOffset Timestamp { get; }

    public override string ToString() => $"{Kind} worker={WorkerPid} job={JobId} {Detail}".TrimEnd();
}

/// <summary>Counters since the pool was created.</summary>
public sealed record PdfPoolStatistics(
    long JobsSubmitted,
    long JobsSucceeded,
    long JobsFailed,
    long JobsTimedOut,
    long JobsCancelled,
    long JobsCrashed,
    long JobsRetried,
    long WorkersStarted,
    long WorkersStopped,
    long WorkersCrashed,
    long WorkersRetiredForMemory,
    long ScaleUps,
    long ScaleDowns);
