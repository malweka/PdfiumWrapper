using System.Text.Json.Serialization;

namespace PdfiumWrapper.Processing.Protocol;

/// <summary>
/// What crosses between the coordinator and a worker: one JSON object per frame, length-prefixed.
/// Only file paths, options and small results travel; never pixels, handles or delegates.
/// </summary>
internal enum FrameKind
{
    /// <summary>Worker to coordinator, once, when the worker is ready for jobs.</summary>
    Hello,
    /// <summary>Coordinator to worker: run this job.</summary>
    Job,
    /// <summary>Worker to coordinator: pages done so far for the job in flight.</summary>
    Progress,
    /// <summary>Coordinator to worker: stop the job in flight as soon as possible.</summary>
    Cancel,
    /// <summary>Worker to coordinator: the job's outcome.</summary>
    Result,
    /// <summary>Coordinator to worker: finish the job in flight, if any, then exit.</summary>
    Shutdown,
}

internal enum JobKind
{
    PageCount,
    ConvertToPng,
    ConvertToJpeg,
    ConvertToTiff,
    ExtractText,
}

internal enum ResultStatus
{
    Succeeded,
    Failed,
    Cancelled,
}

internal sealed class Frame
{
    public const int ProtocolVersion = 1;

    public FrameKind Kind { get; set; }
    public HelloPayload? Hello { get; set; }
    public JobPayload? Job { get; set; }
    public ProgressPayload? Progress { get; set; }
    public long? CancelJobId { get; set; }
    public ResultPayload? Result { get; set; }

    public static Frame ForHello(HelloPayload hello) => new() { Kind = FrameKind.Hello, Hello = hello };
    public static Frame ForJob(JobPayload job) => new() { Kind = FrameKind.Job, Job = job };
    public static Frame ForProgress(long jobId, int pagesDone, int committingPages = 0)
        => new() { Kind = FrameKind.Progress, Progress = new ProgressPayload { JobId = jobId, PagesDone = pagesDone, CommittingPages = committingPages } };
    public static Frame ForCancel(long jobId) => new() { Kind = FrameKind.Cancel, CancelJobId = jobId };
    public static Frame ForResult(ResultPayload result) => new() { Kind = FrameKind.Result, Result = result };
    public static Frame ForShutdown() => new() { Kind = FrameKind.Shutdown };
}

internal sealed class HelloPayload
{
    public int ProtocolVersion { get; set; }
    public int Pid { get; set; }
    public string? WrapperVersion { get; set; }
    /// <summary>Worker clock at Hello, so the coordinator can place the worker's timings on its own clock.</summary>
    public long UtcTicks { get; set; }
    public bool DiagnosticsEnabled { get; set; }
}

internal sealed class JobPayload
{
    public long Id { get; set; }
    public JobKind Kind { get; set; }
    /// <summary>Path of the input PDF.</summary>
    public string Input { get; set; } = "";
    /// <summary>Output directory (images) or output file (TIFF). Unused for page count and text.</summary>
    public string? Output { get; set; }
    public string? FileNamePrefix { get; set; }
    public int DpiWidth { get; set; }
    public int DpiHeight { get; set; }
    public int Quality { get; set; }
    public TiffColorMode ColorMode { get; set; }
    public byte Threshold { get; set; }
    public string? Password { get; set; }
}

internal sealed class ProgressPayload
{
    public long JobId { get; set; }
    /// <summary>Pages rendered and staged as the job's own temp files so far.</summary>
    public int PagesDone { get; set; }
    /// <summary>
    /// Non-zero once every page is staged and the worker starts moving them to their final names:
    /// from here the job owns those names, and a worker that dies leaves them to the coordinator.
    /// </summary>
    public int CommittingPages { get; set; }
}

internal sealed class ResultPayload
{
    public long JobId { get; set; }
    public ResultStatus Status { get; set; }
    /// <summary>Exception type and message, when the status is Failed.</summary>
    public string? Error { get; set; }
    public int PageCount { get; set; }
    /// <summary>Output files, in page order, for image jobs.</summary>
    public string[]? Files { get; set; }
    /// <summary>Output size in bytes for file-producing jobs.</summary>
    public long OutputBytes { get; set; }
    /// <summary>Extracted text per page, when it fits in a frame.</summary>
    public string[]? Text { get; set; }
    /// <summary>Path of a JSON file holding the text per page, when the text is too large for a frame.</summary>
    public string? TextFile { get; set; }
    /// <summary>Milliseconds the worker spent on the job.</summary>
    public double ProcessingMs { get; set; }
    /// <summary>Native render intervals as UTC ticks, present when the worker runs with diagnostics on.</summary>
    public long[][]? RenderIntervalsUtcTicks { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(Frame))]
internal sealed partial class FrameJsonContext : JsonSerializerContext
{
}
