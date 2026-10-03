using System.Diagnostics;
using PdfiumWrapper.Processing.Protocol;

namespace PdfiumWrapper.Processing;

/// <summary>A submitted job: its payload, its attempts so far, and the promise of its result.</summary>
internal sealed class PendingJob
{
    public PendingJob(JobPayload payload, string displayInput, string? spooledInput, CancellationToken ct)
    {
        Payload = payload;
        DisplayInput = displayInput;
        SpooledInput = spooledInput;
        CallerToken = ct;
        SubmittedAt = Stopwatch.GetTimestamp();
    }

    public JobPayload Payload { get; }
    public long Id => Payload.Id;
    public string DisplayInput { get; }
    /// <summary>Temp file holding a byte[] or Stream input; deleted when the job completes.</summary>
    public string? SpooledInput { get; }
    public CancellationToken CallerToken { get; }
    public long SubmittedAt { get; }
    public long DispatchedAt { get; set; }
    public int Attempts { get; set; }
    public Worker? Worker { get; set; }
    public CancellationTokenSource? AttemptTimeout { get; set; }
    /// <summary>Pages the worker reported staged for the current attempt (image jobs).</summary>
    public int PagesDone { get; set; }
    /// <summary>Non-zero once the worker reported it was moving that many staged pages to their final names.</summary>
    public int CommittingPages { get; set; }
    public int Retired;

    public TaskCompletionSource<ResultPayload> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);


    /// <summary>Set on the terminal outcome, before <see cref="Completion"/> completes.</summary>
    public PdfJobStatus FinalStatus { get; set; } = PdfJobStatus.Succeeded;
    public string? FinalError { get; set; }
    public int FinalWorkerPid { get; set; }

    public PdfJobTimings Timings(long completedAt)
    {
        long dispatched = DispatchedAt == 0 ? completedAt : DispatchedAt;
        return new PdfJobTimings(
            Queued: Stopwatch.GetElapsedTime(SubmittedAt, dispatched),
            Processing: Stopwatch.GetElapsedTime(dispatched, completedAt),
            Total: Stopwatch.GetElapsedTime(SubmittedAt, completedAt));
    }
}
