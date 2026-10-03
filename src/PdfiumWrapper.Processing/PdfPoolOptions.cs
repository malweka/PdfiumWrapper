namespace PdfiumWrapper.Processing;

/// <summary>Settings for a <see cref="PdfProcessingPool"/>. Validated when the pool is created.</summary>
public sealed class PdfPoolOptions
{
    /// <summary>Default for <see cref="MaxWorkers"/>: half the logical processors, at least 1.</summary>
    /// <remarks>
    /// A worker is CPU-bound on rendering, so one worker is one core. Past the number of fast cores
    /// an extra worker adds memory and little throughput (measured: 8 workers 10.9 requests/s,
    /// 12 workers 12.3, 16 workers 13.1 at twice the memory of 8, on a 24-logical-processor machine).
    /// </remarks>
    public static int DefaultMaxWorkers => Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>Workers kept alive and warm at all times. Started when the pool is created. Default 1.</summary>
    public int MinWorkers { get; set; } = 1;

    /// <summary>Upper bound on workers. Default <see cref="DefaultMaxWorkers"/>.</summary>
    public int MaxWorkers { get; set; } = DefaultMaxWorkers;

    /// <summary>
    /// Jobs a worker runs at once. Default 2: PDFium serializes rendering inside a process, so a
    /// second job lets one document encode and write while the other renders, which is where a
    /// single-process caller gets its parallel capacity. More than 2 mostly adds memory (one
    /// rendered page per job in flight). 1 is strictly one job per worker.
    /// </summary>
    public int JobsPerWorker { get; set; } = 2;

    /// <summary>
    /// A worker is added when every worker's slots have been busy and jobs have been waiting for this long.
    /// Default 500 ms: a burst reaches <see cref="MaxWorkers"/> within seconds, a single stray job
    /// never starts a process.
    /// </summary>
    public TimeSpan ScaleUpAfter { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>A worker idle for this long is stopped, down to <see cref="MinWorkers"/>. Default 60 s.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Time allowed for one attempt of a job. On expiry the worker is killed and replaced and the
    /// attempt is reported as timed out. Default 2 minutes.
    /// </summary>
    public TimeSpan JobTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Attempts a job gets when its worker crashes or times out. A job that fails with an error
    /// from the document itself is not retried. Default 2.
    /// </summary>
    public int MaxAttempts { get; set; } = 2;

    /// <summary>
    /// Jobs that may wait for a worker. Submitting beyond this waits until a slot frees (backpressure).
    /// Default 1,000.
    /// </summary>
    public int QueueCapacity { get; set; } = 1_000;

    /// <summary>
    /// When set, a worker whose working set exceeds this many bytes after a job is retired and
    /// replaced. Null (the default) never retires a worker for memory.
    /// </summary>
    public long? MaxWorkerMemoryBytes { get; set; }

    /// <summary>
    /// Time a new worker gets to report ready. Default 30 s. Covers process start, native
    /// initialization and warm-up; raise it for slow hosts.
    /// </summary>
    public TimeSpan WorkerStartTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Executable (or <c>.dll</c>, run through <c>dotnet</c>) to start as a worker. Null (the
    /// default) starts this process's own executable again; its <c>Main</c> must then call
    /// <see cref="PdfWorkerHost.TryRun"/> first.
    /// </summary>
    public string? WorkerPath { get; set; }

    /// <summary>Arguments passed to <see cref="WorkerPath"/>. Ignored when the path is null.</summary>
    public IReadOnlyList<string> WorkerArguments { get; set; } = Array.Empty<string>();

    /// <summary>Extra environment variables for worker processes, for example a diagnostics switch.</summary>
    public IDictionary<string, string> WorkerEnvironment { get; } = new Dictionary<string, string>();

    /// <summary>Directory for temporary files (spooled inputs, large text results). Default: the system temp directory.</summary>
    public string? TempDirectory { get; set; }

    internal void Validate()
    {
        if (MinWorkers < 0) throw new ArgumentOutOfRangeException(nameof(MinWorkers), "MinWorkers must be 0 or more.");
        if (MaxWorkers < 1) throw new ArgumentOutOfRangeException(nameof(MaxWorkers), "MaxWorkers must be at least 1.");
        if (MinWorkers > MaxWorkers) throw new ArgumentOutOfRangeException(nameof(MinWorkers), "MinWorkers must not exceed MaxWorkers.");
        if (JobsPerWorker is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(JobsPerWorker), "JobsPerWorker must be between 1 and 16.");
        if (ScaleUpAfter < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ScaleUpAfter));
        if (IdleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(IdleTimeout));
        if (JobTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(JobTimeout));
        if (MaxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(MaxAttempts), "MaxAttempts must be at least 1.");
        if (QueueCapacity < 1) throw new ArgumentOutOfRangeException(nameof(QueueCapacity), "QueueCapacity must be at least 1.");
        if (MaxWorkerMemoryBytes is <= 0) throw new ArgumentOutOfRangeException(nameof(MaxWorkerMemoryBytes));
        if (WorkerStartTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(WorkerStartTimeout));
        if (WorkerPath != null && !File.Exists(WorkerPath)) throw new PdfPoolException($"WorkerPath '{WorkerPath}' does not exist.");
    }

    internal PdfPoolOptions Clone() => new()
    {
        MinWorkers = MinWorkers,
        MaxWorkers = MaxWorkers,
        JobsPerWorker = JobsPerWorker,
        ScaleUpAfter = ScaleUpAfter,
        IdleTimeout = IdleTimeout,
        JobTimeout = JobTimeout,
        MaxAttempts = MaxAttempts,
        QueueCapacity = QueueCapacity,
        MaxWorkerMemoryBytes = MaxWorkerMemoryBytes,
        WorkerStartTimeout = WorkerStartTimeout,
        WorkerPath = WorkerPath,
        WorkerArguments = WorkerArguments.ToArray(),
        TempDirectory = TempDirectory,
        WorkerEnvironmentCopy = new Dictionary<string, string>(WorkerEnvironment),
    };

    private IDictionary<string, string> WorkerEnvironmentCopy
    {
        init
        {
            foreach (var pair in value)
                WorkerEnvironment[pair.Key] = pair.Value;
        }
    }
}

/// <summary>Raised for pool-level problems: bad configuration, a worker that cannot start, use after disposal.</summary>
public sealed class PdfPoolException : Exception
{
    public PdfPoolException(string message) : base(message) { }

    public PdfPoolException(string message, Exception? inner) : base(message, inner) { }
}
