using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace PdfiumWrapper.Benchmarks;

/// <summary>
/// Save to, and load a merger from, a file, a MemoryStream and a slow stream. User streams are
/// read before the native gate and written after it, so the slow stream costs its own caller time
/// but must not lengthen the time the gate is held (see <see cref="StreamHoldReport"/>).
/// </summary>
[MemoryDiagnoser]
public class StreamCallbackBenchmark
{
    private StreamCallbackOperations _operations = null!;

    [GlobalSetup]
    public void Setup() => _operations = new StreamCallbackOperations();

    [GlobalCleanup]
    public void Cleanup() => _operations.Dispose();

    [Benchmark]
    public void SaveToFile() => _operations.SaveToFile();

    [Benchmark]
    public void SaveToMemoryStream() => _operations.SaveToMemoryStream();

    [Benchmark]
    public void SaveToThrottledStream() => _operations.SaveToThrottledStream();

    [Benchmark]
    public int MergerFromFileStream() => _operations.MergerFromFileStream();

    [Benchmark]
    public int MergerFromMemoryStream() => _operations.MergerFromMemoryStream();

    [Benchmark]
    public int MergerFromThrottledStream() => _operations.MergerFromThrottledStream();
}

internal sealed class StreamCallbackOperations : IDisposable
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(5);

    private readonly string _inputPath = Path.Combine(AppContext.BaseDirectory, "Docs", "contract.pdf");
    private readonly string _outputPath = Path.Combine(Path.GetTempPath(), $"pdfium-stream-bench-{Guid.NewGuid():N}.pdf");
    private readonly byte[] _inputBytes;
    private readonly PdfDocument _document;

    public StreamCallbackOperations()
    {
        _inputBytes = File.ReadAllBytes(_inputPath);
        _document = new PdfDocument(_inputPath);
    }

    public void SaveToFile() => _document.Save(_outputPath);

    public void SaveToMemoryStream()
    {
        using var output = new MemoryStream();
        _document.SaveToStream(output);
    }

    public void SaveToThrottledStream()
    {
        using var output = new ThrottledStream(new MemoryStream(), Delay);
        _document.SaveToStream(output);
    }

    public int MergerFromFileStream()
    {
        using var input = File.OpenRead(_inputPath);
        using var merger = new PdfMerger(input);
        return merger.PageCount;
    }

    public int MergerFromMemoryStream()
    {
        using var input = new MemoryStream(_inputBytes);
        using var merger = new PdfMerger(input);
        return merger.PageCount;
    }

    public int MergerFromThrottledStream()
    {
        using var input = new ThrottledStream(new MemoryStream(_inputBytes), Delay);
        using var merger = new PdfMerger(input);
        return merger.PageCount;
    }

    public void Dispose()
    {
        _document.Dispose();
        if (File.Exists(_outputPath))
            File.Delete(_outputPath);
    }
}

/// <summary>Sleeps on every read and write; reads return at most 16 KB so a load takes several calls.</summary>
internal sealed class ThrottledStream : Stream
{
    private const int MaxChunk = 16 * 1024;
    private readonly MemoryStream _inner;
    private readonly TimeSpan _delay;

    public ThrottledStream(MemoryStream inner, TimeSpan delay)
    {
        _inner = inner;
        _delay = delay;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        Thread.Sleep(_delay);
        return _inner.Read(buffer, offset, Math.Min(count, MaxChunk));
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Thread.Sleep(_delay);
        _inner.Write(buffer, offset, count);
    }

    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Instrumented companion to <see cref="StreamCallbackBenchmark"/>: gate hold time per operation
/// for each stream type. Run with <c>dotnet run -c Release -- streams [report.json]</c>.
/// </summary>
internal static class StreamHoldReport
{
    private const int Iterations = 100;

    public static int Run(string[] args)
    {
        // Must happen before the first wrapper type is touched.
        AppContext.SetSwitch("PdfiumWrapper.Diagnostics", true);

        using var operations = new StreamCallbackOperations();
        var cases = new (string name, Action run)[]
        {
            ("SaveToFile", operations.SaveToFile),
            ("SaveToMemoryStream", operations.SaveToMemoryStream),
            ("SaveToThrottledStream", operations.SaveToThrottledStream),
            ("MergerFromFileStream", () => operations.MergerFromFileStream()),
            ("MergerFromMemoryStream", () => operations.MergerFromMemoryStream()),
            ("MergerFromThrottledStream", () => operations.MergerFromThrottledStream()),

            // Controls: the same fast operation after the thread has been idle for as long as the
            // throttled variant sleeps. Separates "the gate was held during stream I/O" (it is not)
            // from "native work runs slower on a thread that has just been asleep".
            ("SaveToMemoryStream+idle15ms", () => { Thread.Sleep(15); operations.SaveToMemoryStream(); }),
            ("MergerFromMemoryStream+idle60ms", () => { Thread.Sleep(60); operations.MergerFromMemoryStream(); }),
        };

        var report = new Dictionary<string, object?>();
        Console.WriteLine($"{"operation",-34} {"wall ms/op",12} {"gate hold ms/op",16}");

        foreach (var (name, run) in cases)
        {
            for (int i = 0; i < 5; i++)
                run();

            PdfiumDiagnostics.Reset();
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < Iterations; i++)
                run();
            double wallMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds / Iterations;

            var snapshot = PdfiumDiagnostics.Snapshot();
            double holdMs = TimeSpan.FromSeconds((double)snapshot.HoldTicks / System.Diagnostics.Stopwatch.Frequency).TotalMilliseconds / Iterations;

            Console.WriteLine($"{name,-34} {wallMs,12:F3} {holdMs,16:F3}");
            report[name] = new Dictionary<string, object?>
            {
                ["wallMsPerOp"] = Math.Round(wallMs, 3),
                ["gateHoldMsPerOp"] = Math.Round(holdMs, 3),
            };
        }

        if (args.Length > 0)
            File.WriteAllText(args[0], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        return 0;
    }
}
