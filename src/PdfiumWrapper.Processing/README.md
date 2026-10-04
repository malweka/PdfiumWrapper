# PdfiumWrapper.Processing

<img src="https://raw.githubusercontent.com/malweka/PdfiumWrapper/main/icon.png" alt="PdfiumWrapper Icon" width="64" height="64" align="left" />

A worker pool for [PdfiumWrapper](https://www.nuget.org/packages/PdfiumWrapper). It runs PDF conversions in a dynamically sized set of worker processes, so rendering runs in parallel and a native crash on a damaged PDF costs one job, not your service.

Supported operations:

- page count
- PNG, JPEG and multi-page TIFF conversion
- text extraction

Each works on a single document or on a batch. Every job ends with a status: `Succeeded`, `Failed`, `TimedOut`, `Cancelled` or `WorkerCrashed`.

## Installation

```bash
dotnet add package PdfiumWrapper.Processing
```

Requires .NET 10. The package depends on exactly the same version of `PdfiumWrapper`, which brings the native libraries for Windows x64, Linux x64 and macOS x64/ARM64.

## Quick start

Workers are copies of your own executable. Call `PdfWorkerHost.TryRun()` first in `Main`: in a worker process it runs the worker loop and returns `true`; in your application it returns `false` at once.

```csharp
using PdfiumWrapper.Processing;

if (PdfWorkerHost.TryRun())
    return;

await using var pool = await PdfProcessingPool.CreateAsync(new PdfPoolOptions { MaxWorkers = 4 });

var result = await pool.ConvertToPngAsync("input.pdf", "output", dpi: 150);
if (result.IsSuccess)
    Console.WriteLine($"{result.Value!.PageCount} pages written to {string.Join(", ", result.Value.Files)}");
else
    Console.WriteLine($"{result.Status}: {result.Error}");
```

How the pool reports problems:

- **Job failures are statuses.** A damaged file, a crash, a timeout or a cancellation comes back as a status on the result, not an exception.
- **Exceptions are reserved for misuse and startup.** These throw:
  - invalid arguments or options;
  - use after disposal;
  - `CreateAsync`, when the first workers cannot start (`PdfPoolException`);
  - a batch whose cancellation token is cancelled (`OperationCanceledException`).

Batches take any number of inputs and stream results as they finish:

```csharp
var inputs = Directory.EnumerateFiles("in", "*.pdf").Select(f => PdfInput.FromFile(f));
await foreach (var r in pool.ConvertToTiffAsync(inputs, "out"))
    Console.WriteLine($"{r.Input}: {r.Status}");
```

## Documentation

- [High-Throughput Processing](https://github.com/malweka/PdfiumWrapper/blob/main/docs/HIGH-THROUGHPUT-PROCESSING.md) covers when to use the pool and how to size it, events, worker lifetime and batch behaviour.
- [API Reference](https://github.com/malweka/PdfiumWrapper/blob/main/docs/API-REFERENCE.md) lists every pool type and option.
- [Changelog](https://github.com/malweka/PdfiumWrapper/blob/main/CHANGELOG.md) has the release notes.

## License

MIT.
