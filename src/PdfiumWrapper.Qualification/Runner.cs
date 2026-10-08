using System.Collections.Concurrent;
using PdfiumWrapper.Processing;

namespace PdfiumWrapper.Qualification;

internal sealed record JobSpec(long Index, Op Op, Doc Doc, InputKind Input);

/// <summary>
/// Submits jobs to the pool, keeping a target number in flight, and checks every result against
/// the in-process reference. Outputs are deleted once checked.
/// </summary>
internal sealed class Runner
{
    private const int MaxProblems = 200;

    private readonly PdfProcessingPool _pool;
    private readonly Doc[] _good;
    private readonly Doc _encrypted;
    private readonly Doc _corrupt;
    private readonly IReadOnlyDictionary<string, Reference> _references;
    private readonly string _outputRoot;
    private readonly Sampler _sampler;
    private readonly Random _random;
    private readonly SemaphoreSlim _completedSignal = new(0);
    private readonly ConcurrentDictionary<string, long> _statusCounts = new();
    private readonly ConcurrentQueue<string> _problems = new();
    private readonly ConcurrentQueue<double> _totalMs = new();
    private readonly ConcurrentQueue<double> _processingMs = new();
    private long _submitted;
    private long _completed;
    private long _inFlight;
    private long _unexpected;
    private long _mismatches;
    private long _leftovers;
    private long _retried;
    private long _problemCount;

    public Runner(PdfProcessingPool pool, Doc[] good, Doc encrypted, Doc corrupt,
        IReadOnlyDictionary<string, Reference> references, string outputRoot, Sampler sampler, int seed)
    {
        _pool = pool;
        _good = good;
        _encrypted = encrypted;
        _corrupt = corrupt;
        _references = references;
        _outputRoot = outputRoot;
        _sampler = sampler;
        _random = new Random(seed);
    }

    public long Submitted => Interlocked.Read(ref _submitted);
    public long Completed => Interlocked.Read(ref _completed);
    public long InFlight => Interlocked.Read(ref _inFlight);
    public long Unexpected => Interlocked.Read(ref _unexpected);
    public long Mismatches => Interlocked.Read(ref _mismatches);
    public long Leftovers => Interlocked.Read(ref _leftovers);
    public long Retried => Interlocked.Read(ref _retried);
    public IReadOnlyDictionary<string, long> StatusCounts => _statusCounts;
    public IReadOnlyCollection<string> Problems => _problems;
    public IReadOnlyCollection<double> TotalMs => _totalMs;
    public IReadOnlyCollection<double> ProcessingMs => _processingMs;

    /// <summary>
    /// Submits jobs while <paramref name="keepSubmitting"/> holds, keeping <paramref name="target"/>
    /// jobs in flight, then waits for all of them.
    /// </summary>
    public async Task RunAsync(Func<long, bool> keepSubmitting, Func<int> target)
    {
        while (keepSubmitting(Submitted))
        {
            int wanted = target();
            while (InFlight < wanted && keepSubmitting(Submitted))
            {
                var spec = Next(Interlocked.Increment(ref _submitted) - 1);
                Interlocked.Increment(ref _inFlight);
                _ = RunJobAsync(spec);
            }

            await _completedSignal.WaitAsync(TimeSpan.FromMilliseconds(200));
        }

        while (InFlight > 0)
            await _completedSignal.WaitAsync(TimeSpan.FromMilliseconds(200));
    }

    /// <summary>
    /// Runs <paramref name="count"/> PNG conversions of the good documents through the batch API and
    /// checks each against the reference.
    /// </summary>
    public async Task RunBatchAsync(int count)
    {
        string root = Path.Combine(_outputRoot, "batch");
        var byPath = _good.ToDictionary(d => d.Path, StringComparer.Ordinal);
        var inputs = Enumerable.Range(0, count).Select(i => PdfInput.FromFile(_good[i % _good.Length].Path));
        await foreach (var result in _pool.ConvertToPngAsync(inputs, root, Corpus.PngDpi))
        {
            Interlocked.Increment(ref _completed);
            _sampler.Track(result.WorkerPid);
            Count($"Batch/{result.Status}");
            if (!result.IsSuccess)
            {
                Problem(ref _unexpected, $"batch {result.Input}: {result.Status} {result.Error}");
                continue;
            }

            var reference = _references[byPath[result.Input].Name];
            if (!Corpus.HashAll(result.Value!.Files).SequenceEqual(reference.PngHashes))
                Problem(ref _mismatches, $"batch {result.Input}: PNG output differs from the in-process reference");
            foreach (var file in result.Value.Files)
                File.Delete(file);
        }
    }

    private JobSpec Next(long index)
    {
        // Only the submitting loop draws, so the sequence is the same for the same seed.
        int roll = _random.Next(100);
        var doc = roll == 0 ? _encrypted : roll == 1 ? _corrupt : _good[_random.Next(_good.Length)];
        var op = _random.Next(100) switch
        {
            < 15 => Op.PageCount,
            < 40 => Op.Png,
            < 60 => Op.Jpeg,
            < 85 => Op.Tiff,
            _ => Op.Text,
        };
        var input = _random.Next(10) switch
        {
            < 7 => InputKind.File,
            < 9 => InputKind.Bytes,
            _ => InputKind.Stream,
        };
        return new JobSpec(index, op, doc, input);
    }

    private async Task RunJobAsync(JobSpec spec)
    {
        string directory = Path.Combine(_outputRoot, spec.Index.ToString());
        string tiff = directory + ".tiff";
        Stream? stream = null;
        try
        {
            await Task.Yield();
            PdfInput input;
            switch (spec.Input)
            {
                case InputKind.Bytes:
                    input = PdfInput.FromBytes(spec.Doc.Bytes, spec.Doc.Name);
                    break;
                case InputKind.Stream:
                    stream = File.OpenRead(spec.Doc.Path);
                    input = PdfInput.FromStream(stream, spec.Doc.Name);
                    break;
                default:
                    input = PdfInput.FromFile(spec.Doc.Path);
                    break;
            }

            switch (spec.Op)
            {
                case Op.PageCount:
                    Check(spec, await _pool.GetPageCountAsync(input), (r, v) => v == r.PageCount);
                    break;
                case Op.Png:
                    Check(spec, await _pool.ConvertToPngAsync(input, directory, Corpus.PngDpi),
                        (r, v) => v.PageCount == r.PageCount && Corpus.HashAll(v.Files).SequenceEqual(r.PngHashes));
                    break;
                case Op.Jpeg:
                    Check(spec, await _pool.ConvertToJpegAsync(input, directory, Corpus.JpegQuality, Corpus.JpegDpi),
                        (r, v) => v.PageCount == r.PageCount && Corpus.HashAll(v.Files).SequenceEqual(r.JpegHashes));
                    break;
                case Op.Tiff:
                    Check(spec, await _pool.ConvertToTiffAsync(input, tiff, Corpus.TiffDpi),
                        (r, v) => v.PageCount == r.PageCount && Corpus.Sha256(v.Path) == r.TiffHash);
                    break;
                case Op.Text:
                    Check(spec, await _pool.ExtractTextAsync(input), (r, v) => v.SequenceEqual(r.Text));
                    break;
            }

            // A job that did not succeed must leave nothing; a successful one is cleaned up here.
            bool left = (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) || File.Exists(tiff);
            if (left && !spec.Doc.Valid)
                Problem(ref _leftovers, $"job {spec.Index} {spec.Op} {spec.Doc.Name}: output left after a failed job");
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
            File.Delete(tiff);
        }
        catch (Exception ex)
        {
            Problem(ref _unexpected, $"job {spec.Index} {spec.Op} {spec.Doc.Name} ({spec.Input}): exception {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            stream?.Dispose();
            Interlocked.Decrement(ref _inFlight);
            long completed = Interlocked.Increment(ref _completed);
            _completedSignal.Release();
            _sampler.OnJobCompleted(completed);
        }
    }

    private void Check<T>(JobSpec spec, PdfJobResult<T> result, Func<Reference, T, bool> matches)
    {
        _sampler.Track(result.WorkerPid);
        Count($"{spec.Op}/{result.Status}");
        _totalMs.Enqueue(result.Timings.Total.TotalMilliseconds);
        _processingMs.Enqueue(result.Timings.Processing.TotalMilliseconds);
        if (result.Attempts > 1)
            Interlocked.Increment(ref _retried);

        var expected = spec.Doc.Valid ? PdfJobStatus.Succeeded : PdfJobStatus.Failed;
        if (result.Status != expected)
        {
            Problem(ref _unexpected, $"job {spec.Index} {spec.Op} {spec.Doc.Name} ({spec.Input}): {result.Status}, expected {expected}; attempts {result.Attempts}; {result.Error}");
            return;
        }

        if (result.IsSuccess && !matches(_references[spec.Doc.Name], result.Value!))
            Problem(ref _mismatches, $"job {spec.Index} {spec.Op} {spec.Doc.Name} ({spec.Input}): output differs from the in-process reference");
    }

    private void Count(string key) => _statusCounts.AddOrUpdate(key, 1, (_, n) => n + 1);

    private void Problem(ref long counter, string message)
    {
        Interlocked.Increment(ref counter);
        if (Interlocked.Increment(ref _problemCount) <= MaxProblems)
            _problems.Enqueue(message);
    }
}
