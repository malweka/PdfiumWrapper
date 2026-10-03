using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using PdfiumWrapper.Processing.Protocol;

namespace PdfiumWrapper.Processing;

/// <summary>A PDF to process: a file path, or bytes or a stream with a name for the result.</summary>
public readonly struct PdfInput
{
    private PdfInput(string? path, byte[]? bytes, Stream? stream, string name, string? password)
    {
        Path = path;
        Bytes = bytes;
        Stream = stream;
        Name = name;
        Password = password;
    }

    public string? Path { get; }
    public byte[]? Bytes { get; }
    public Stream? Stream { get; }
    /// <summary>What <see cref="PdfJobResult{T}.Input"/> reports.</summary>
    public string Name { get; }
    public string? Password { get; }

    public static PdfInput FromFile(string path, string? password = null)
        => new(path ?? throw new ArgumentNullException(nameof(path)), null, null, path, password);

    public static PdfInput FromBytes(byte[] bytes, string name, string? password = null)
        => new(null, bytes ?? throw new ArgumentNullException(nameof(bytes)), null, name ?? throw new ArgumentNullException(nameof(name)), password);

    /// <summary>The stream is read to its end during submission and may be closed afterwards.</summary>
    public static PdfInput FromStream(Stream stream, string name, string? password = null)
        => new(null, null, stream ?? throw new ArgumentNullException(nameof(stream)), name ?? throw new ArgumentNullException(nameof(name)), password);

    public static implicit operator PdfInput(string path) => FromFile(path);
}

public sealed partial class PdfProcessingPool
{
    /// <summary>Reads the number of pages.</summary>
    public Task<PdfJobResult<int>> GetPageCountAsync(PdfInput input, CancellationToken ct = default)
        => RunAsync(input, new JobPayload { Kind = JobKind.PageCount }, r => r.PageCount, ct);

    /// <summary>Converts every page to a PNG file <c>{prefix}_{page:D3}.png</c> in <paramref name="outputDirectory"/>.</summary>
    public Task<PdfJobResult<ImageFiles>> ConvertToPngAsync(PdfInput input, string outputDirectory, int dpi = 300,
        string fileNamePrefix = "page", CancellationToken ct = default)
        => RunAsync(input, new JobPayload
        {
            Kind = JobKind.ConvertToPng,
            Output = RequireOutput(outputDirectory),
            DpiWidth = dpi,
            DpiHeight = dpi,
            Quality = 100,
            FileNamePrefix = fileNamePrefix,
        }, ToImageFiles, ct);

    /// <summary>Converts every page to a JPEG file <c>{prefix}_{page:D3}.jpg</c> in <paramref name="outputDirectory"/>.</summary>
    public Task<PdfJobResult<ImageFiles>> ConvertToJpegAsync(PdfInput input, string outputDirectory, int quality = 90, int dpi = 300,
        string fileNamePrefix = "page", CancellationToken ct = default)
        => RunAsync(input, new JobPayload
        {
            Kind = JobKind.ConvertToJpeg,
            Output = RequireOutput(outputDirectory),
            DpiWidth = dpi,
            DpiHeight = dpi,
            Quality = quality,
            FileNamePrefix = fileNamePrefix,
        }, ToImageFiles, ct);

    /// <summary>Converts all pages to one multi-page TIFF file.</summary>
    public Task<PdfJobResult<TiffFile>> ConvertToTiffAsync(PdfInput input, string outputPath, int dpi = 200,
        TiffColorMode colorMode = TiffColorMode.Bilevel, byte threshold = 128, CancellationToken ct = default)
        => RunAsync(input, new JobPayload
        {
            Kind = JobKind.ConvertToTiff,
            Output = RequireOutput(outputPath),
            DpiWidth = dpi,
            DpiHeight = dpi,
            ColorMode = colorMode,
            Threshold = threshold,
        }, r => new TiffFile(r.PageCount, r.Files![0], r.OutputBytes), ct);

    /// <summary>Extracts the text of every page, one string per page.</summary>
    public Task<PdfJobResult<string[]>> ExtractTextAsync(PdfInput input, CancellationToken ct = default)
        => RunAsync(input, new JobPayload { Kind = JobKind.ExtractText }, r =>
        {
            if (r.Text != null)
                return r.Text;
            if (r.TextFile == null)
                return Array.Empty<string>();
            try
            {
                return JsonSerializer.Deserialize<string[]>(File.ReadAllText(r.TextFile)) ?? Array.Empty<string>();
            }
            finally
            {
                try { File.Delete(r.TextFile); } catch (Exception) { }
            }
        }, ct);

    // ---- Batches: results in completion order ----

    /// <summary>Page counts for many documents, yielded as they complete.</summary>
    public IAsyncEnumerable<PdfJobResult<int>> GetPageCountAsync(IEnumerable<PdfInput> inputs, CancellationToken ct = default)
        => Batch(inputs, null, (input, _, token) => GetPageCountAsync(input, token), ct);

    /// <summary>
    /// PNG conversion for many documents, yielded as they complete. Each document's files go to
    /// <c>{outputRoot}/{document name without extension}</c>; a name that repeats within the batch
    /// gets <c>-2</c>, <c>-3</c>, ... appended so documents never write over each other.
    /// </summary>
    public IAsyncEnumerable<PdfJobResult<ImageFiles>> ConvertToPngAsync(IEnumerable<PdfInput> inputs, string outputRoot, int dpi = 300,
        string fileNamePrefix = "page", CancellationToken ct = default)
        => Batch(inputs, outputRoot, (input, output, token) => ConvertToPngAsync(input, output!, dpi, fileNamePrefix, token), ct);

    /// <summary>JPEG conversion for many documents, yielded as they complete, into <c>{outputRoot}/{document name}</c> (suffixed when names repeat).</summary>
    public IAsyncEnumerable<PdfJobResult<ImageFiles>> ConvertToJpegAsync(IEnumerable<PdfInput> inputs, string outputRoot, int quality = 90, int dpi = 300,
        string fileNamePrefix = "page", CancellationToken ct = default)
        => Batch(inputs, outputRoot, (input, output, token) => ConvertToJpegAsync(input, output!, quality, dpi, fileNamePrefix, token), ct);

    /// <summary>TIFF conversion for many documents, yielded as they complete, into <c>{outputRoot}/{document name}.tiff</c> (suffixed when names repeat).</summary>
    public IAsyncEnumerable<PdfJobResult<TiffFile>> ConvertToTiffAsync(IEnumerable<PdfInput> inputs, string outputRoot, int dpi = 200,
        TiffColorMode colorMode = TiffColorMode.Bilevel, byte threshold = 128, CancellationToken ct = default)
        => Batch(inputs, outputRoot, (input, output, token) => ConvertToTiffAsync(input, output + ".tiff", dpi, colorMode, threshold, token), ct);

    /// <summary>Text extraction for many documents, yielded as they complete.</summary>
    public IAsyncEnumerable<PdfJobResult<string[]>> ExtractTextAsync(IEnumerable<PdfInput> inputs, CancellationToken ct = default)
        => Batch(inputs, null, (input, _, token) => ExtractTextAsync(input, token), ct);

    /// <summary>
    /// Per-document output paths under <paramref name="outputRoot"/>: the document name without its
    /// extension, made unique within the batch. Null when there is no output root.
    /// </summary>
    private static string?[] OutputsFor(string? outputRoot, IReadOnlyList<PdfInput> inputs)
    {
        var outputs = new string?[inputs.Count];
        if (outputRoot == null)
            return outputs;

        string root = RequireOutput(outputRoot);
        // Every name handed out, original or generated, so "report", "report" and "report-2" get three paths.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < inputs.Count; i++)
        {
            string stem = Path.GetFileNameWithoutExtension(inputs[i].Name);
            if (stem.Length == 0)
                stem = "document";

            string name = stem;
            for (int suffix = 2; !used.Add(name); suffix++)
                name = $"{stem}-{suffix}";

            outputs[i] = Path.Combine(root, name);
        }

        return outputs;
    }

    private async IAsyncEnumerable<PdfJobResult<T>> Batch<T>(IEnumerable<PdfInput> inputs, string? outputRoot,
        Func<PdfInput, string?, CancellationToken, Task<PdfJobResult<T>>> run, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var list = inputs as IReadOnlyList<PdfInput> ?? inputs.ToList();
        var outputs = OutputsFor(outputRoot, list);

        // Documents in any stage (spooled, waiting for admission, queued, running, or finished but not
        // yet read by the caller) are bounded, so a batch of a million inputs holds a million nothing:
        // no task, spool file or result per input beyond what the pool can have in flight.
        int bound = _options.QueueCapacity + _options.MaxWorkers * _options.JobsPerWorker;
        var inFlight = new SemaphoreSlim(bound, bound);
        var results = Channel.CreateUnbounded<PdfJobResult<T>>(new UnboundedChannelOptions { SingleReader = true });
        // Outstanding jobs are counted, not collected: a finished job leaves nothing behind, so memory
        // follows the bound above and not the size of the batch. The submitter holds one count of its
        // own until it has submitted everything; the last to finish completes the results.
        int outstanding = 1;
        void OneDone()
        {
            if (Interlocked.Decrement(ref outstanding) == 0)
                results.Writer.TryComplete();
        }

        var submitter = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < list.Count; i++)
                {
                    await inFlight.WaitAsync(ct).ConfigureAwait(false);
                    Interlocked.Increment(ref outstanding);
                    _ = run(list[i], outputs[i], ct).ContinueWith(t =>
                    {
                        if (t.IsCompletedSuccessfully)
                            results.Writer.TryWrite(t.Result);
                        else
                            results.Writer.TryComplete(t.Exception?.GetBaseException() ?? new OperationCanceledException(ct));
                        OneDone();
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }

                OneDone();
            }
            catch (Exception ex)
            {
                results.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        await foreach (var result in results.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            inFlight.Release();
            yield return result;
        }

        await submitter.ConfigureAwait(false);
    }

    // ---- Plumbing ----

    private async Task<PdfJobResult<T>> RunAsync<T>(PdfInput input, JobPayload payload, Func<ResultPayload, T> project, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (input.Path == null && input.Bytes == null && input.Stream == null)
            throw new ArgumentException("The input is empty; use PdfInput.FromFile, FromBytes or FromStream.", nameof(input));

        string? spooled = null;
        if (input.Path != null)
        {
            payload.Input = Path.GetFullPath(input.Path);
        }
        else
        {
            // Workers read files, so bytes and streams are written to a temp file owned by this job.
            spooled = Path.Combine(_tempDirectory, $"{Guid.NewGuid():N}.pdf");
            try
            {
                if (input.Bytes != null)
                    await File.WriteAllBytesAsync(spooled, input.Bytes, ct).ConfigureAwait(false);
                else
                {
                    await using var file = new FileStream(spooled, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                    await input.Stream!.CopyToAsync(file, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Cancellation is a status, never an exception, wherever it lands.
                return CancelledBeforeSubmission<T>(payload, input.Name, spooled, ct, "cancelled while the input was being spooled");
            }

            payload.Input = spooled;
        }

        payload.Password = input.Password;
        return await SubmitAsync(payload, input.Name, spooled, project, ct).ConfigureAwait(false);
    }

    private PdfJobResult<T> CancelledBeforeSubmission<T>(JobPayload payload, string displayInput, string? spooled, CancellationToken ct, string reason)
    {
        payload.Id = Interlocked.Increment(ref _nextJobId);
        var job = new PendingJob(payload, displayInput, spooled, ct);
        _counters.Increment(ref _counters.Submitted);
        Finish(job, PdfJobStatus.Cancelled, reason, new ResultPayload { JobId = job.Id, Status = ResultStatus.Cancelled });
        return new PdfJobResult<T>(displayInput, PdfJobStatus.Cancelled, default, reason, 0, 0, job.Timings(Stopwatch.GetTimestamp()));
    }

    private static ImageFiles ToImageFiles(ResultPayload r) => new(r.PageCount, r.Files ?? Array.Empty<string>(), r.OutputBytes);

    private static string RequireOutput(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("An output path is required.", nameof(path));
        return Path.GetFullPath(path);
    }
}
