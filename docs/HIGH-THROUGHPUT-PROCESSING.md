# High-Throughput PDF Processing

This guide covers efficient patterns for processing large volumes of PDF documents, including image conversion, text extraction, merging, parallel processing strategies, and capacity sizing.

## Table of Contents

- [Core Principles](#core-principles)
- [Two Paths: In-Process or Worker Pool](#two-paths-in-process-or-worker-pool)
- [Converting Pages to Images (TIFF, PNG, JPEG)](#converting-pages-to-images)
- [Extracting Text from PDFs](#extracting-text-from-pdfs)
- [Merging PDF Documents](#merging-pdf-documents)
- [Parallel Processing Strategies](#parallel-processing-strategies)
- [Measured Capacity and Sizing](#measured-capacity-and-sizing)
  - [Worker Pool](#worker-pool)
  - [Pool Events and Lifecycle](#pool-events-and-lifecycle)
- [Memory Management for Long-Running Processes](#memory-management-for-long-running-processes)
  - [Memory on Linux](#memory-on-linux)
  - [Setting the variables in a container](#setting-the-variables-in-a-container)
- [Complete Examples](#complete-examples)

---

## Core Principles

### 1. Always Use `using` Statements

Every `PdfDocument`, `PdfPage`, `PdfMerger`, and `PdfForm` instance **must** be disposed:

```csharp
// ✅ CORRECT: Resources properly disposed
using var doc = new PdfDocument("input.pdf");
using var page = doc.GetPage(0);
var text = page.ExtractText();
```

### 2. Prefer `ProcessAllPages` Over `GetAllPages`

The `ProcessAllPages` method handles page disposal automatically, preventing memory leaks:

```csharp
// ✅ RECOMMENDED: Automatic disposal, no leaks possible
var texts = doc.ProcessAllPages(page => page.ExtractText());

// ⚠️ RISKY: Requires manual disposal of each page
var pages = doc.GetAllPages(); // Marked [Obsolete] for this reason
foreach (var page in pages)
{
    // ... process
    page.Dispose(); // Easy to forget!
}
```

### 3. One Document Per Processing Unit

PDFium allows one native call per process at a time, across all documents. PdfiumWrapper enforces that itself with one process-wide gate, so separate documents can be processed from separate threads without a lock of your own. A single document, page, form or merger must still be used by one thread at a time, so give each unit of work its own document:

```csharp
// ✅ CORRECT: Each iteration has its own document
foreach (var filePath in pdfFiles)
{
    using var doc = new PdfDocument(filePath);
    // Process...
}
```

---

## Two Paths: In-Process or Worker Pool

PdfiumWrapper ships as two packages:

| Package | What it is | Processes it starts |
|---|---|---|
| `PdfiumWrapper` | The core library: `PdfDocument`, `PdfPage`, `PdfMerger`, image and text output. Everything runs inside your process. | None |
| `PdfiumWrapper.Processing` | Optional. `PdfProcessingPool` runs the same operations in worker processes it starts and manages. Depends on the core package of exactly the same version. | Workers, between `MinWorkers` and `MaxWorkers` |

The pool is not required. The core package never launches a process, and an application that only references it needs no `Main` changes and no worker configuration. Add the pool only when the reasons below apply.

### The same job both ways

Convert every PDF in a folder to PNG at 150 DPI and report each document's page count.

**In-process (core package only):**

```csharp
using PdfiumWrapper;

public static async Task RunAsync(string inputDirectory, string outputRoot)
{
    var files = Directory.EnumerateFiles(inputDirectory, "*.pdf");

    // Bounded callers: rendering takes turns at the gate, encoding and writes overlap
    await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (file, ct) =>
    {
        try
        {
            using var doc = new PdfDocument(file);
            var outputDirectory = Path.Combine(outputRoot, Path.GetFileNameWithoutExtension(file));
            Directory.CreateDirectory(outputDirectory);

            int page = 0;
            await foreach (var png in doc.StreamImageBytesAsync(ImageFormat.Png, 100, 150).WithCancellation(ct))
                await File.WriteAllBytesAsync(Path.Combine(outputDirectory, $"page_{++page:D3}.png"), png, ct);

            Console.WriteLine($"{file}: {doc.PageCount} pages");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{file}: failed: {ex.Message}");
        }
    });
}
```

**Worker pool (`PdfiumWrapper.Processing`):**

```csharp
using PdfiumWrapper.Processing;

public static async Task<int> Main(string[] args)
{
    if (PdfWorkerHost.TryRun())        // required: a copy of this executable started as a worker runs here
        return 0;

    await using var pool = await PdfProcessingPool.CreateAsync();   // defaults: 1 warm worker, up to cores / 2

    var files = Directory.EnumerateFiles(args[0], "*.pdf").Select(f => PdfInput.FromFile(f));

    // Results arrive as they complete; submission waits while QueueCapacity + MaxWorkers x JobsPerWorker documents are in flight
    await foreach (var r in pool.ConvertToPngAsync(files, args[1], dpi: 150))
    {
        if (r.IsSuccess)
            Console.WriteLine($"{r.Input}: {r.Value!.PageCount} pages");
        else
            Console.WriteLine($"{r.Input}: {r.Status}: {r.Error}");   // Failed, TimedOut, Cancelled or WorkerCrashed
    }

    return 0;
}
```

Both write `page_001.png`, `page_002.png`, ... into one directory per document. The pool adds one line to `Main`, and it reports the outcome of each document as a status on the result instead of throwing. The pool still throws for misuse and setup problems: `ArgumentException` for an empty input or output path, `ArgumentOutOfRangeException` for invalid `PdfPoolOptions`, `ObjectDisposedException` after the pool is disposed, `PdfPoolException` from `CreateAsync` when a worker cannot be started, and `OperationCanceledException` from a batch whose `CancellationToken` is cancelled (see [Pool Events and Lifecycle](#pool-events-and-lifecycle)).

For a single document the difference is smaller still:

```csharp
// In-process
using (var doc = new PdfDocument("invoice.pdf"))
{
    doc.SaveAsPngs("out/invoice", "page", dpi: 150);
    string[] pageTexts = doc.ProcessAllPages(page => page.ExtractText());
}

// Pool (pool created as above; a string converts to PdfInput)
PdfJobResult<ImageFiles> png = await pool.ConvertToPngAsync("invoice.pdf", "out/invoice", dpi: 150);
PdfJobResult<string[]> text = await pool.ExtractTextAsync("invoice.pdf");
```

### What each path gives you

| | In-process | Worker pool |
|---|---|---|
| Rendering | One page at a time per process (the native gate). Extra callers overlap encoding and output only: about 1.25x over sequential on the mixed corpus. | One page at a time per worker, workers in parallel. 8 workers reached 4.5x one process on the 1,000-request PNG run (10.07 against 2.21 requests/s). |
| A damaged PDF that aborts PDFium | Takes the process down. | Takes one worker down. It is replaced and the job is retried up to `MaxAttempts` (default 2) before it is reported as `WorkerCrashed`; a job that only shared the crashed worker runs again without using an attempt. Every other job proceeds. |
| Memory | One process: about 120 to 140 MB plus one rendered page per caller in flight. On Linux, see [Memory on Linux](#memory-on-linux). | The same per worker, so 8 workers is about 1 GB. Idle workers above `MinWorkers` are stopped after `IdleTimeout` (60 s). On Linux the pool tunes the workers' allocator; see [Memory on Linux](#memory-on-linux). |
| First request | Pays native initialization once, a few milliseconds. | `PdfProcessingPool.CreateAsync` starts and warms `MinWorkers` before it returns; the constructor starts them in the background and the first jobs wait for them. A scale-up costs a few hundred milliseconds of process start. |
| Errors | Exceptions. | A `PdfJobStatus` on every result (`Succeeded`, `Failed`, `TimedOut`, `Cancelled`, `WorkerCrashed`), with attempts, worker id and timings. `Failed` also covers "no worker could be started" (after `MaxConsecutiveStartFailures`) and "the pool's dispatcher stopped"; the error says which. Misuse and setup problems still throw (see above). |
| Timeouts, retries, backpressure | Yours to write (see the patterns below). | `JobTimeout`, `MaxAttempts`, `QueueCapacity`. |
| Operations | The whole API: render, images, text, merge, forms, metadata, bookmarks, attachments, page editing. | Page count, PNG, JPEG, TIFF, text. Merge and forms are planned. |
| Inputs | Path, bytes, stream. | Path, bytes, stream. Bytes and streams are spooled to a temp file under `TempDirectory` for the worker. |
| Deployment | One package. | Two packages and the `TryRun` line in `Main`, or `WorkerPath` for a dedicated worker executable. Nothing extra to publish per platform. |

### When to use which

Use the **in-process API** when:

- The volume is small: a CLI over a few files, a report generator, a service with low or steady load. One process handles 1.3 to 1.6 documents per second on the mixed corpus, which is thousands of documents an hour.
- You need an operation the pool does not offer yet: merge, forms, metadata, page editing.
- You already run several replicas of your service behind a queue. Replicas give the same parallel rendering as the pool, and you have the orchestration already.
- The work is almost entirely native (text extraction, merging). In-process callers gain nothing from each other there, and the pool only helps if the burst is beyond one process.

Use the **worker pool** when:

- A burst must finish in a window that one process cannot meet. Apply the sizing rule in [Measured Capacity and Sizing](#measured-capacity-and-sizing): if `N / T` exceeds the usable rate of one process, you need several processes, and the pool is the way to have them inside one deployable.
- Your service must stay one deployable, or you would rather not write the bounded loop, the timeouts and the retry logic yourself.
- You process untrusted or damaged PDFs and a native abort must not take the service down.

In both cases the rule is the same: measure one process on your documents and hardware first. The pool costs memory per worker and a process start per scale-up, and buys nothing if one process already meets the required rate.

### Moving between them

The two paths expose the same operations, so changing your mind later is a mechanical edit:

| In-process | Pool |
|---|---|
| `doc.PageCount` | `pool.GetPageCountAsync(input)` |
| `doc.SaveAsPngs(dir, prefix, dpi)` | `pool.ConvertToPngAsync(input, dir, dpi, prefix)` |
| `doc.SaveAsJpegs(dir, prefix, quality, dpi)` | `pool.ConvertToJpegAsync(input, dir, quality, dpi, prefix)` |
| `doc.SaveAsTiff(path, dpi, colorMode, threshold)` | `pool.ConvertToTiffAsync(input, path, dpi, colorMode, threshold)` |
| `doc.ProcessAllPages(p => p.ExtractText())` | `pool.ExtractTextAsync(input)` |
| `try { ... } catch` | `if (result.IsSuccess) ... else result.Status, result.Error` |

Each pool method also takes an `IEnumerable<PdfInput>` and returns results in completion order. In the batch form the output argument is a root directory, not a per-document path: image batches write into `{outputRoot}/{document name}/`, and the TIFF batch writes `{outputRoot}/{document name}.tiff`. See [Batches](#batches).

---

## Converting Pages to Images

### Converting to TIFF (Built-in)

PdfiumWrapper includes native multi-page TIFF support via libtiff, with a zero-copy pipeline from PDFium's rendered bitmap directly to libtiff:

```csharp
public void ConvertPdfToTiff(string pdfPath, string outputPath, int dpi = 200)
{
    using var doc = new PdfDocument(pdfPath);

    // Bilevel (1-bit CCITT G4) — smallest files, ideal for scanned documents
    doc.SaveAsTiff(outputPath, dpi);

    // Grayscale (8-bit LZW) — preserves shading
    doc.SaveAsTiff(outputPath, dpi, colorMode: TiffColorMode.Grayscale);
}

// Write to a stream (must be writable and seekable)
public void ConvertPdfToTiffStream(string pdfPath, Stream output, int dpi = 200)
{
    using var doc = new PdfDocument(pdfPath);
    doc.SaveAsTiff(output, dpi);
}

// Async version: waits for the native gate without blocking a thread.
// The token is checked before each page; a cancelled export deletes the partly written file.
public async Task ConvertPdfToTiffAsync(string pdfPath, string outputPath, int dpi = 200, CancellationToken ct = default)
{
    using var doc = new PdfDocument(pdfPath);
    await doc.SaveAsTiffAsync(outputPath, dpi, cancellationToken: ct);
}
```

The TIFF pipeline renders each page straight into an 8-bit gray PDFium bitmap (a quarter of the memory of a BGRA render), thresholds it to 1 bit (bilevel) or copies it (grayscale) with unsafe code outside the native gate, and writes scanlines directly to libtiff with pinned buffers — no managed array copies per row. Text edges are anti-aliased slightly differently at 8 bits, so a TIFF page is not pixel-identical to the PNG or JPEG render of the same page.

### Converting to PNG/JPEG (Built-in)

For PNG and JPEG, use the built-in methods:

```csharp
public void ConvertPdfToImages(string pdfPath, string outputDirectory, int dpi = 300)
{
    using var doc = new PdfDocument(pdfPath);
    
    // Save all pages as PNG
    doc.SaveAsPngs(outputDirectory, "page", dpi);
    
    // Or save as JPEG with quality setting (90 is also the default)
    doc.SaveAsJpegs(outputDirectory, "page", quality: 90, dpi);
}
```

Pages are written as `{prefix}_001.png`, `{prefix}_002.png`, and so on. Every output file, TIFF included, is opened by .NET, so non-ASCII directories, prefixes and document names behave the same on Windows, Linux and macOS. The worker pool's image jobs use the same names.

### Streaming Image Bytes Without Saving to Disk

Use `StreamImageBytes` / `StreamImageBytesAsync` to process one page at a time without holding all pages in memory:

```csharp
public void ProcessPdfPageImages(string pdfPath, ImageFormat format, int dpi = 300)
{
    using var doc = new PdfDocument(pdfPath);
    int i = 0;
    foreach (var bytes in doc.StreamImageBytes(format, quality: 90, dpi))
    {
        File.WriteAllBytes($"page_{i++}.png", bytes);
        // Previous page's bytes are now eligible for GC
    }
}

// Async version: renders and encodes on the thread pool, never on the caller's context
public async Task ProcessPdfPageImagesAsync(string pdfPath, ImageFormat format, int dpi = 300)
{
    using var doc = new PdfDocument(pdfPath);
    int i = 0;
    await foreach (var bytes in doc.StreamImageBytesAsync(format, quality: 90, dpi))
    {
        await File.WriteAllBytesAsync($"page_{i++}.png", bytes);
    }
}
```

`StreamImageBytesAsync` and `StreamJpegBytesAsync` take no `CancellationToken` parameter. Cancel them with `WithCancellation`; the token is checked before each page:

```csharp
await foreach (var bytes in doc.StreamImageBytesAsync(ImageFormat.Jpeg, 90, 200).WithCancellation(ct))
{
    // ...
}
```

---

## Extracting Text from PDFs

### Extract Text from All Pages

```csharp
public string[] ExtractAllText(string pdfPath)
{
    using var doc = new PdfDocument(pdfPath);
    
    // Safe method with automatic page disposal
    return doc.ProcessAllPages(page => page.ExtractText());
}
```

### Extract Text with Page Metadata

```csharp
public record PageTextInfo(int PageIndex, string Text, double Width, double Height);

public PageTextInfo[] ExtractTextWithMetadata(string pdfPath)
{
    using var doc = new PdfDocument(pdfPath);
    
    return doc.ProcessAllPages(page => new PageTextInfo(
        page.PageIndex,
        page.ExtractText(),
        page.Width,
        page.Height
    ));
}
```

### Async Text Extraction

```csharp
public async Task<string[]> ExtractAllTextAsync(string pdfPath)
{
    using var doc = new PdfDocument(pdfPath);
    return await doc.ProcessAllPagesAsync(page => page.ExtractText());
}
```

---

## Merging PDF Documents

### Basic Merge - Combine Multiple PDFs

```csharp
public void MergePdfs(string[] inputPaths, string outputPath)
{
    using var merger = new PdfMerger();
    
    foreach (var path in inputPaths)
    {
        // AppendDocument handles source document disposal internally
        merger.AppendDocument(path);
    }
    
    merger.Save(outputPath);
}
```

### Merge Specific Pages

```csharp
public void MergeSpecificPages(string outputPath)
{
    using var merger = new PdfMerger();
    
    // Append all pages from first document
    merger.AppendDocument("document1.pdf");
    
    // Append only pages 1, 3, 5-7 from second document (1-based page range)
    merger.AppendPages("document2.pdf", "1,3,5-7");
    
    // Append specific pages by 0-based index
    merger.AppendPages("document3.pdf", new[] { 0, 2, 4 });
    
    merger.Save(outputPath);
}
```

### Merge with Existing Document

```csharp
public void AppendToExisting(string existingPdf, string[] additionalPdfs, string outputPath)
{
    // Start with an existing document
    using var merger = new PdfMerger(existingPdf);
    
    foreach (var path in additionalPdfs)
    {
        merger.AppendDocument(path);
    }
    
    merger.Save(outputPath);
}
```

### Get Merged PDF as Bytes

```csharp
public byte[] MergePdfsToBytes(string[] inputPaths)
{
    using var merger = new PdfMerger();
    
    foreach (var path in inputPaths)
    {
        merger.AppendDocument(path);
    }
    
    return merger.ToBytes();
}
```

---

## Parallel Processing Strategies

### The Native Gate and What Parallelism Buys

PDFium allows **one native call per process at a time**, across all documents. Its fonts, caches and reference counts are shared between documents, so separate documents do not isolate it. PdfiumWrapper serializes native work itself through one process-wide gate (`PdfiumRuntime`):

- **Inside the gate (one caller at a time):** loading, page rendering, text extraction, form access, page import, saving a PDF.
- **Outside the gate (callers overlap):** pixel conversion (gray to bilevel or grayscale for TIFF), PNG/JPEG/TIFF encoding, and writing output files or streams.

Consequences for a batch:

- Processing different files from different threads is safe. No lock, file copy or byte-array load is needed. Loading the same file from several threads is also fine.
- Parallel callers gain throughput only from the encode and output share of the work. One caller's encoding runs while another caller renders. Native rendering is not multiplied by adding threads.
- Work that is almost entirely native (text extraction, merging, form filling) gains little or nothing from more callers in one process.
- One `PdfDocument`, `PdfPage`, `PdfForm` or `PdfMerger` must still be used by one thread at a time. Splitting one document's pages across threads is not supported.
- Bound the number of callers. Each caller in flight holds a rendered page (for US Letter at 300 DPI about 32 MiB as BGRA for PNG/JPEG, about 8 MiB as 8-bit gray for TIFF), and callers beyond what the encode share can use only wait for the gate. Do not set the degree of parallelism to `Environment.ProcessorCount` blindly; see [Measured Capacity and Sizing](#measured-capacity-and-sizing).
- Async methods wait for the gate without blocking a thread. Synchronous methods, including constructors, block the calling thread while they wait. With 192 concurrent conversions on a thread pool pinned to 24 threads, a heartbeat work item waited 1.6 ms (p99) when the conversions used the async API and about 2.5 s when they called the synchronous API from pool threads.

### Pattern 1: Bounded Parallel Conversion of Different Files

```csharp
public async Task ConvertMultiplePdfsAsync(string[] pdfPaths, string outputDirectory, int maxCallers = 4)
{
    // ✅ SAFE: each file has its own document; rendering takes turns, encoding overlaps
    await Parallel.ForEachAsync(pdfPaths, new ParallelOptions 
    { 
        MaxDegreeOfParallelism = maxCallers 
    }, 
    async (pdfPath, ct) =>
    {
        using var doc = new PdfDocument(pdfPath);
        
        var outputPath = Path.Combine(outputDirectory, 
            Path.GetFileNameWithoutExtension(pdfPath) + ".tiff");
        
        // Waits for the native gate without blocking a thread-pool thread
        await doc.SaveAsTiffAsync(outputPath, 200, cancellationToken: ct);
    });
}
```

### Pattern 2: Producer-Consumer with Bounded Channel

For high-throughput scenarios, use a bounded channel to control memory usage:

```csharp
using System.Threading.Channels;

public class PdfProcessor
{
    private readonly Channel<string> _inputChannel;
    private readonly int _workerCount;
    
    private readonly string _outputDirectory;
    
    public PdfProcessor(string outputDirectory, int workerCount = 4, int boundedCapacity = 100)
    {
        _outputDirectory = outputDirectory;
        _workerCount = workerCount;
        _inputChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(boundedCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
    }
    
    public async Task StartProcessingAsync(CancellationToken ct = default)
    {
        var workers = Enumerable.Range(0, _workerCount)
            .Select(_ => ProcessWorkerAsync(ct))
            .ToArray();
        
        await Task.WhenAll(workers);
    }
    
    public async Task EnqueueAsync(string pdfPath)
    {
        await _inputChannel.Writer.WriteAsync(pdfPath);
    }
    
    public void Complete() => _inputChannel.Writer.Complete();
    
    private async Task ProcessWorkerAsync(CancellationToken ct)
    {
        await foreach (var pdfPath in _inputChannel.Reader.ReadAllAsync(ct))
        {
            try
            {
                using var doc = new PdfDocument(pdfPath);
                
                // One encoded page in memory at a time; the gate is awaited, not blocked on
                var baseName = Path.GetFileNameWithoutExtension(pdfPath);
                int pageNumber = 0;
                await foreach (var bytes in doc.StreamImageBytesAsync(ImageFormat.Jpeg, 90, 200).WithCancellation(ct))
                {
                    var outputPath = Path.Combine(_outputDirectory, $"{baseName}_{++pageNumber:D3}.jpg");
                    await File.WriteAllBytesAsync(outputPath, bytes, ct);
                }
                
                Console.WriteLine($"Processed {pdfPath}: {pageNumber} pages");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"Error processing {pdfPath}: {ex.Message}");
            }
        }
    }
}

// Usage
var processor = new PdfProcessor("output", workerCount: 4);
var processingTask = processor.StartProcessingAsync();

foreach (var file in Directory.GetFiles("input", "*.pdf"))
{
    await processor.EnqueueAsync(file);
}

processor.Complete();
await processingTask;
```

### Pattern 3: Sequential Batch for Native-Only Work

Text extraction, merging and form filling are almost entirely native work, so extra callers in one process mostly wait for the gate. A plain sequential loop is the simplest shape and uses the least memory:

```csharp
public void ExtractLargeBatch(string[] pdfPaths, string outputDirectory)
{
    int processed = 0;
    
    foreach (var path in pdfPaths)
    {
        using var doc = new PdfDocument(path);
        
        var texts = doc.ProcessAllPages(page => page.ExtractText());
        File.WriteAllText(
            Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(path) + ".txt"),
            string.Join("\n\n", texts));
        
        if (++processed % 100 == 0)
            Console.WriteLine($"Processed {processed}/{pdfPaths.Length} documents");
    }
}
```

No periodic `GC.Collect()` is needed. Disposing each document releases its native memory.

For more throughput on native-only work, scale out across processes (see below).

---

## Measured Capacity and Sizing

These figures come from one machine (Intel Core i7-13700F, 8 performance and 8 efficiency cores, 24 logical processors, NVMe SSD, Windows 11) and the repository's five test documents (62 pages).

> **Measured before the .NET 10 upgrade.** Every figure in this section was recorded on .NET 8.0.31 with PDFium 150.0.7869.0. The library now targets .NET 10 and ships PDFium chromium/8076, and the figures have not been re-measured. Treat absolute rates as approximate; the shape of the scaling (one gate per process, near-linear across processes) is a property of the design and does not depend on those versions.

They show the shape of the scaling. They do not size your deployment; measure on your hardware with your documents. The full record is in [`benchmark.md`](../benchmark.md).

**One process.** 200 jobs enqueued at once (2,480 pages), half bilevel TIFF, 30% PNG, 20% JPEG, 200 DPI, output written to disk:

| Callers | Docs/sec | Pages/sec | Peak working set |
|---|---|---|---|
| 1 | 1.30 | 16.1 | 124 MB |
| 2 | 1.58 | 19.6 | 145 MB |
| 4 | 1.62 | 20.0 | 180 MB |
| 8 | 1.62 | 20.0 | 253 MB |
| 16 | 1.62 | 20.1 | 387 MB |
| 24 | 1.62 | 20.1 | 493 MB |

> **Note:** half of this mix is bilevel TIFF, measured before TIFF output switched to 8-bit gray rendering. TIFF conversion is now 27% to 39% faster on text documents (see [`benchmark.md`](../benchmark.md)), so a TIFF-heavy workload will measure higher than this table. The ceiling still comes from rendering being serialized in one process, so the shape of the scaling is the same.

With 8 callers the gate was held 99.8% of the time. Rendering is the serialized part and it dominates, so one process tops out at about 1.25 times its sequential rate. Two to four callers reach that ceiling; more only use memory. By format, the gain from extra callers was 1.11x for bilevel TIFF, 1.09x for JPEG and 1.45x for PNG, whose encoding is the largest share.

**Several processes.** The same batch split across independent processes, 2 callers each:

| Processes | Docs/sec | Pages/sec | Speedup |
|---|---|---|---|
| 1 | 1.58 | 19.6 | 1.0x |
| 2 | 3.13 | 38.8 | 2.0x |
| 4 | 5.75 | 71.3 | 3.6x |
| 8 | 9.05 | 112.3 | 5.7x |
| 12 | 10.31 | 127.9 | 6.5x |
| 16 | 11.07 | 137.2 | 7.0x |

Processes scale close to linearly up to the number of fast cores. Each used about 142 MB on this mix.

**The wrapper itself costs nothing measurable.** One uncontended gate entry takes 28 ns. A one-page load, render and close is within 0.5% of the ungated code, and a sequential batch takes the same time (154.3 s gated, 153.5 s before).

**Sizing rule.** Use 2 to 4 callers per process and as many processes as the required rate needs:

```text
required rate          = N / T                       (documents in the burst / window in seconds)
usable rate per process = 0.8 x measured docs/sec    (25% headroom)
processes              = ceil(required rate / usable rate per process)
```

On the machine above the usable rate is `0.8 x 1.62 = 1.30` docs/sec per process for this mix, so a burst of 1,000 such documents in 5 minutes (3.33 docs/sec) needs 3 processes, and the same burst in 2 minutes (8.33 docs/sec) is at the edge of what 8 processes delivered there.

### Scaling Out

One process has one PDFium and one gate. When a single process cannot meet the required rate, run more replicas of the service behind the existing queue. Each replica is a separate process with its own PDFium and its own gate, so replicas render in parallel.

- Required rate = `N / T`: the number of documents in the burst divided by the completion window in seconds.
- Measure the single-process rate on your own documents, formats, DPI and hardware. The burst runner in `src/PdfiumWrapper.Benchmarks` (`dotnet run -c Release -- burst ...`) measures completion of a whole batch, including queueing and output writes.
- Size the replica count from the measured single-process rate with 25% headroom: `replicas = ceil(1.25 * (N / T) / measured docs per second per process)`.
- Give each replica enough memory for its callers in flight (one rendered page per caller) plus the documents it has open.

### Worker Pool

If your application must stay one deployable (one API that receives a document and returns the result), or you would rather not hand-build the orchestration, the `PdfiumWrapper.Processing` package runs the conversions in worker processes it starts and manages:

```bash
dotnet add package PdfiumWrapper.Processing
```

```csharp
using PdfiumWrapper.Processing;

public static async Task<int> Main(string[] args)
{
    if (PdfWorkerHost.TryRun())            // first statement: a copy of this app started as a worker runs here
        return 0;

    await using var pool = await PdfProcessingPool.CreateAsync(new PdfPoolOptions
    {
        MinWorkers = 2,                    // kept warm
        MaxWorkers = 8,                    // default: half the logical processors
    });

    var files = Directory.EnumerateFiles(args[0], "*.pdf").Select(f => PdfInput.FromFile(f));

    await foreach (var r in pool.ConvertToPngAsync(files, "out", dpi: 150))
        Console.WriteLine($"{r.Input}: {r.Status}");

    return 0;
}
```

What it gives you over the patterns above:

- **Parallel rendering.** Each worker is a separate process with its own PDFium, so workers render at the same time. The pool reaches the throughput of the replica table above from inside one application.
- **Dynamic size.** Workers are added when every job slot is taken (each worker runs `JobsPerWorker` jobs at once, default 2) and jobs are waiting (after `ScaleUpAfter`, 500 ms), and removed when idle (after `IdleTimeout`, 60 s), between `MinWorkers` and `MaxWorkers`. A burst scales up within seconds; quiet periods cost only `MinWorkers` of memory.
- **Crash isolation.** A native abort on a damaged PDF kills one worker, which is replaced; the job is retried up to `MaxAttempts` (default 2) and then reported as `WorkerCrashed`, and every other job proceeds. A job that only shared the crashed worker runs again, alone, without using one of its attempts, so a job that crashes every time can run up to three times with the defaults. In-process, that abort would take the service down.
- **Backpressure, timeouts, retries, cancellation**, and a typed API: `GetPageCountAsync`, `ConvertToPngAsync`, `ConvertToJpegAsync`, `ConvertToTiffAsync`, `ExtractTextAsync`, single or batch. See the [API reference](API-REFERENCE.md#pdfprocessingpool-pdfiumwrapperprocessing).

What it costs: about 120 to 140 MB per worker on the mix above, and a few hundred milliseconds of process start when the pool grows.

Measured on the machine above with the same 1,000-request scenario (page count plus PNG at 150 DPI, 12,400 pages), `JobsPerWorker = 2`:

| Shape | Requests/sec | Peak memory, all processes |
|---|---|---|
| One process, 4 threads, no pool | 2.21 | 159 MB |
| Pool, 4 workers | 6.89 | 562 MB |
| Pool, 8 workers | 10.07 | 999 MB |
| Pool, 1 to 8 workers, cold start | 9.80 | 1,008 MB |
| Pool, 16 workers | 12.11 | 1,823 MB |
| 8 independent processes (replicas) | 10.86 | 953 MB |

A warm pool of 8 is within 7% of 8 replicas; a cold pool reached 8 workers 4.5 s into the burst. Measured on .NET 8 with PDFium 7869, like the tables above. Full record in [`benchmark.md`](../benchmark.md).

When you already run replicas behind a queue, keep doing that; the pool is for the single-deployable case and for applications that want the orchestration done for them.

### Pool Events and Lifecycle

#### Observing the pool

`pool.Events` (`EventHandler<PdfPoolEvent>`) reports everything the pool does. Handlers run on pool threads, must be quick and must not throw. Each event has a `Kind`, the `WorkerPid` and `JobId` involved (0 when none), a `Detail` text and a `Timestamp`.

```csharp
pool.Events += (_, e) =>
{
    if (e.Kind is PdfPoolEventKind.WorkerMessage or PdfPoolEventKind.WorkerCrashed or PdfPoolEventKind.JobRetried)
        Console.Error.WriteLine(e);   // e.g. "WorkerMessage worker=1234 job=0 <text>"
};
```

| Kind | Raised when |
|---|---|
| `WorkerStarting`, `WorkerReady`, `WorkerStartFailed` | A worker process is started, reports ready, or fails to start. |
| `WorkerStopped` | The pool killed a worker: after a job's timeout, after a cancelled job ignored its grace period, or because the dispatcher stopped. `Detail` gives the reason. |
| `WorkerCrashed` | A worker exited without being asked to, or could not be sent a job, or sent a frame the pool did not expect. |
| `WorkerRetiredForMemory` | A worker exceeded `MaxWorkerMemoryBytes` and is draining. |
| `ScaledUp`, `ScaledDown` | The sizer started a worker because jobs were waiting, or stopped one idle for `IdleTimeout`. |
| `JobDispatched`, `JobCompleted`, `JobFailed`, `JobTimedOut`, `JobRetried`, `JobCancelled` | A job's progress. |
| `QueueFull` | A submission has to wait because `QueueCapacity` jobs are already queued. |
| `WorkerMessage` | A line the worker wrote to standard error, or a problem the pool saw on the worker's protocol stream. A line longer than 4,096 characters is cut and ends with ` [truncated]`. |

Worker stderr is reported only as `WorkerMessage`. Write diagnostics from a worker to `Console.Error`; standard output carries the protocol and must not be written to.

For polling, `Workers` (alive, starting included), `BusyWorkers` (at least one job in flight), `RunningJobs`, `QueuedJobs` and `Statistics` (a `PdfPoolStatistics` snapshot of counters since creation: jobs submitted, succeeded, failed, timed out, cancelled, crashed and retried; workers started, stopped, crashed and retired for memory; scale-ups and scale-downs) give the current state.

#### Creating the pool

- `await PdfProcessingPool.CreateAsync(options)` starts `MinWorkers` workers and waits until they are ready. If one cannot start it throws `PdfPoolException`, and the pool is disposed.
- `new PdfProcessingPool(options)` returns at once and starts `MinWorkers` in the background; the first jobs wait for them, and a start failure surfaces only through job results.
- Invalid options throw `ArgumentOutOfRangeException` from either: `MinWorkers` at least 0 and at most `MaxWorkers`, `MaxWorkers` at least 1, `JobsPerWorker` between 1 and 16, `MaxAttempts`, `QueueCapacity` and `MaxConsecutiveStartFailures` at least 1. A `WorkerPath` that does not exist throws `PdfPoolException`.

#### Timeouts and start failures

- `JobTimeout` (default 2 minutes) bounds one attempt; the worker is killed and the job retried or reported `TimedOut`. `WorkerStartTimeout` (default 30 s) bounds process start, native initialization and warm-up.
- Both must be positive and at most `PdfPoolOptions.MaxTimeout`, about 49.7 days (the limit of .NET timers). There is no infinite timeout; use `MaxTimeout` for "practically never".
- After `MaxConsecutiveStartFailures` (default 3) failed starts in a row with no worker alive, every job waiting for a worker ends `Failed` with the last start error. A successful start resets the count.

#### Workers

- **Executable.** By default a worker is a copy of your own executable, which is why `PdfWorkerHost.TryRun()` must be the first statement of `Main`. Set `WorkerPath` to use a dedicated worker executable instead (its `Main` also calls `TryRun()`). A `.dll` path is started through `dotnet`; `WorkerArguments` and `WorkerEnvironment` are passed to the worker:

  ```csharp
  var options = new PdfPoolOptions
  {
      WorkerPath = Path.Combine(AppContext.BaseDirectory, "MyPdfWorker.dll"),
  };
  options.WorkerEnvironment["DOTNET_gcServer"] = "0";
  await using var pool = await PdfProcessingPool.CreateAsync(options);
  ```

- **Working directory.** Workers run with `AppContext.BaseDirectory` as their working directory. The pool turns relative input and output paths into absolute paths before it sends them.
- **Lifetime.** On Windows every worker is placed in a job object that kills it when the host process ends, a crash included. On Linux and macOS (and on Windows if a worker cannot be placed in the job) a worker whose standard input closes cancels its jobs and exits; if they have not stopped within 5 seconds it exits anyway with exit code 5. Workers therefore never outlive the host.
- **Memory.** With `MaxWorkerMemoryBytes` set, a worker whose working set exceeds it after a job is retired: it takes no new job, the jobs it is running finish (each within its `JobTimeout`), and then it is shut down and replaced. Retirement never kills a running job.
- **Render limit.** The `PdfiumWrapper.MaxRenderPixels` cap applies inside each worker, but `AppContext.SetData` in the host does not reach worker processes. To change it for workers, call `AppContext.SetData` before `PdfWorkerHost.TryRun()`, or set it in the project file so it is in the runtime config of every process started from that executable (the worker executable's own project when you use `WorkerPath`):

  ```xml
  <ItemGroup>
    <RuntimeHostConfigurationOption Include="PdfiumWrapper.MaxRenderPixels" Value="536870912" />
  </ItemGroup>
  ```

#### Temporary files

Each pool creates its own folder `pdfium-pool-{pid}-{guid}` under `PdfPoolOptions.TempDirectory` (default: the system temp directory) and deletes it on disposal. Byte-array and stream inputs are spooled there for the worker, and a text extraction result larger than 4 MiB is written there by the worker instead of being sent inline. Workers learn the folder from the `PDFIUMWRAPPER_WORKER_TEMP` environment variable, and the pool reads a text result file only from that folder. Point `TempDirectory` at a volume with room for the inputs in flight.

#### Batches

The batch overloads take an `IEnumerable<PdfInput>` and yield results in completion order.

- **Output layout.** Image batches write each document into `{outputRoot}/{name}/`; the TIFF batch writes `{outputRoot}/{name}.tiff`. `{name}` is the input's file name without its extension. Characters not allowed in file names become `_`, and an empty name, `.`, `..`, or a name ending in a dot or a space becomes `document`. Names are compared ignoring case after normalization, and a name that repeats in the batch gets `-2`, `-3`, ... appended, so documents never write over each other. Every output stays directly under `outputRoot`.
- **Bounds.** The input sequence is enumerated up front, but inputs are spooled and submitted only as room frees: at most `QueueCapacity + MaxWorkers x JobsPerWorker` documents are in any stage at once (spooled, queued, running, or finished but not yet read by you).
- **Leaving early.** Breaking out of the `await foreach` (or an exception in its body) cancels every outstanding job of that batch and stops submission. Those jobs end `Cancelled` and leave no output.
- **Cancelling.** Cancelling the batch's `CancellationToken` makes the enumeration throw `OperationCanceledException`, and the batch's outstanding jobs are cancelled. The single-document methods report a cancelled token as the `Cancelled` status instead.

#### Output files

A job that does not succeed leaves no output. Image pages are staged as `<name>.<jobId>.tmp` and renamed only once every page is written; a crashed or killed worker's staged files are removed by the pool. A job never deletes a file it did not write.

---

## Memory Management for Long-Running Processes

### Where the Memory Goes

Most memory used while processing PDFs is native: PDFium's document and font data, and rendered bitmaps. A US Letter page rendered as BGRA at 300 DPI (PNG, JPEG, `RenderPages`) is about 32 MiB; TIFF output renders in 8-bit gray, about 8 MiB for the same page. This memory is released by `Dispose()`, not by the garbage collector.

- Dispose every document, page, form and merger. If one is dropped without `Dispose()`, its finalizer does not call PDFium; it queues the native handles and the next PdfiumWrapper operation on any thread closes them (`PdfiumRuntime.ReleasePending()` does so on demand). That delays the release of native memory, so treat it as a safety net.
- Do not force garbage collection between documents or batches. It does not release PDF memory and only pauses the process.
- `RenderPages` / `RenderPagesAsync` buffer the whole document: every page is held as a managed `byte[]` until the call returns, so a 100-page document at 300 DPI needs about 3.3 GB. For large documents prefer `StreamImageBytes` / `StreamImageBytesAsync` or the `SaveAs...` methods, which hold one page at a time.
- Every render is capped at 268,435,456 pixels (1 GiB as BGRA). The cap is checked before PDFium allocates the bitmap, so a PDF with a huge page box, or a mistaken DPI, fails fast with an `InvalidOperationException` that names the page and its pixel size. Without the cap, such a file could force a multi-GiB allocation per caller. A DPI, width or height of zero or less throws `ArgumentOutOfRangeException`. The cap applies to each bitmap: with N concurrent callers, up to N bitmaps of that size can exist at once. Raise or lower it with the `PdfiumWrapper.MaxRenderPixels` `AppContext` data key (pixels):

  ```csharp
  // A service that only renders ordinary pages can afford a tighter bound per caller.
  AppContext.SetData("PdfiumWrapper.MaxRenderPixels", 64L * 1024 * 1024);
  ```

  The worker pool's `MaxWorkerMemoryBytes` bounds a whole worker process. This cap also protects in-process callers and bounds each render inside a worker, but a value set with `AppContext.SetData` in the host does not reach the workers; see [Pool Events and Lifecycle](#pool-events-and-lifecycle) for how to change it there.
- Saving a PDF (`Save`, `SaveToStream`, `PdfMerger.Save`, `PdfMerger.ToBytes`) serializes the whole output into a pooled in-memory buffer and writes it to the file or stream afterwards. Peak memory includes the full output size.
- Bound the number of concurrent callers: each one in flight holds a rendered page.

### Memory on Linux

On Linux with glibc (Debian, Ubuntu, RHEL and the default `mcr.microsoft.com/dotnet` images), a process that renders many pages keeps memory it no longer uses. When a page bitmap is freed, glibc's allocator keeps it inside the process for reuse instead of returning it to the system, and it keeps a separate pool of such memory per thread. Memory therefore grows to the largest mix of pages the process has rendered and stays there, even when the process is idle. It is not a leak: it levels off. But it is large: in the pool qualification an idle worker held 316 to 400 MB, against about 30 MB on Windows, where the same blocks go back to the system when freed.

Two glibc environment variables change this. The trailing underscore in the second name is part of it:

| Variable | Value | Effect |
|---|---|---|
| `MALLOC_ARENA_MAX` | `2` | At most two allocator pools instead of up to eight per CPU core. |
| `MALLOC_MMAP_THRESHOLD_` | `131072` | Every buffer of 128 KiB or more (every page bitmap) is mapped on its own, so freeing it returns it to the system at once. Each page then gets fresh memory from the system, which costs time. |

glibc reads them only when a process starts, so they have to be in the environment before your application runs. They have no effect on Windows or macOS, and none on Alpine (musl), whose allocator already returns memory.

**The worker pool handles this for you.** On Linux every worker starts with `MALLOC_ARENA_MAX=2`, and a worker that has had no job for one second hands its free memory back (`malloc_trim`). Nothing to configure. Measured with the qualification harness (10,000-job burst, 8 workers, Debian 12 in Docker on a 24-core machine; every output checked):

| Worker setting | Burst time | Median job | Worker peak | Idle worker |
|---|---|---|---|---|
| Before 2.0.1 (glibc defaults) | 6.9 min | 98 ms | 355-430 MB | 316-400 MB |
| Default since 2.0.1: `MALLOC_ARENA_MAX=2` + idle trim | 6.9-7.0 min | 97-99 ms | 175-206 MB | 72-117 MB |
| Also `MALLOC_MMAP_THRESHOLD_=131072` | 7.5 min | 108-113 ms | 107-121 MB | 64-69 MB |

In a 30-minute soak with the default (38,063 jobs), the longest-lived worker stayed at 103-106 MB whenever idle.

The threshold saves another 35-80 MB per worker but costs about 9% throughput, so the pool leaves it to you. Set it when memory is tighter than CPU time, for example a container with a low memory limit and many workers. A value in your application's environment, for example from the container image, is passed to the workers unchanged, and `PdfPoolOptions.WorkerEnvironment` overrides both:

```csharp
var options = new PdfPoolOptions();
options.WorkerEnvironment["MALLOC_MMAP_THRESHOLD_"] = "131072"; // smaller workers, about 9% slower
```

**In-process (no pool), set them yourself.** PdfiumWrapper cannot change the allocator of a process that is already running, and does not trim it. For a long-running Linux service that renders in-process, set `MALLOC_ARENA_MAX=2` where the service starts. Without the pool's idle trim, freed pages are returned only with the threshold, so add `MALLOC_MMAP_THRESHOLD_=131072` if the service's memory stays high while idle. `MALLOC_ARENA_MAX=2` also keeps the pool's coordinator process small.

### Setting the variables in a container

They are ordinary environment variables, so set them like any other. The examples set `MALLOC_ARENA_MAX`; add `MALLOC_MMAP_THRESHOLD_=131072` the same way to trade throughput for memory as described above. Workers inherit both.

In the image (`Dockerfile`), for every container started from it:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0
ENV MALLOC_ARENA_MAX=2
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MyPdfService.dll"]
```

For a self-contained publish, use `mcr.microsoft.com/dotnet/runtime-deps:10.0` as the base and start the executable instead; the `ENV` line is the same.

When starting a container (`docker run`), without changing the image:

```bash
docker run -e MALLOC_ARENA_MAX=2 my-pdf-service
```

Docker Compose:

```yaml
services:
  pdf-service:
    image: my-pdf-service
    environment:
      MALLOC_ARENA_MAX: "2"
```

Kubernetes (container spec):

```yaml
containers:
  - name: pdf-service
    image: my-pdf-service
    env:
      - name: MALLOC_ARENA_MAX
        value: "2"
```

Outside containers, a systemd unit takes `Environment=MALLOC_ARENA_MAX=2` in its `[Service]` section.

To check, read a process's resident memory while it is idle: `grep VmRSS /proc/<pid>/status`, or `docker stats` for a container. On Linux, compare `VmRSS` (`Process.WorkingSet64`), not `Process.PrivateMemorySize64`: there the latter counts reserved address space, including 8 MB per thread stack, and is much larger than the memory actually in use.

### Monitor Memory Usage

`GC.GetTotalMemory` reports managed memory only. It does not include PDFium's native memory or rendered bitmaps, so it is not a PDF memory monitor. Watch the process working set instead:

```csharp
public class MemoryMonitor
{
    private readonly long _thresholdBytes;
    
    public MemoryMonitor(long thresholdMB = 1024)
    {
        _thresholdBytes = thresholdMB * 1024 * 1024;
    }
    
    /// <summary>True when the process working set is above the threshold.</summary>
    public bool IsAboveThreshold(out long workingSetMB)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        workingSetMB = process.WorkingSet64 / 1024 / 1024;
        return process.WorkingSet64 > _thresholdBytes;
    }
}
```

Use the reading to apply backpressure (stop admitting new documents, lower the number of callers), not to trigger a collection:

```csharp
public async Task ProcessWithMemoryMonitoringAsync(string[] pdfPaths, string outputDirectory)
{
    var monitor = new MemoryMonitor(thresholdMB: 2048);
    
    foreach (var path in pdfPaths)
    {
        while (monitor.IsAboveThreshold(out long workingSetMB))
        {
            Console.WriteLine($"Working set {workingSetMB} MB is above the threshold; pausing admission");
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        
        using var doc = new PdfDocument(path);
        await doc.SaveAsTiffAsync(
            Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(path) + ".tiff"), 200);
    }
}
```

### Consideration: Large Input Streams

`new PdfDocument(stream)` and `new PdfMerger(stream)` read the stream from its current position to its end during construction, before any native work starts, and leave it positioned at its end. A slow stream therefore delays only its own caller, and the source stream can be closed, reset or reused as soon as the constructor returns.

- Streams of up to 64 MB are copied into a buffer the document owns, pinned for the lifetime of the document. For a seekable stream (a `MemoryStream` included) the buffer is rented from `ArrayPool<byte>.Shared` and returned once the document is closed.
- A `MemoryStream`'s own buffer is never used in place. PDFium reads pages from its input lazily for as long as the document is open, so a pooled or reused stream overwritten after construction would otherwise change, or break, the pages of a document that is still open.
- Larger inputs are copied to a temporary file that PDFium reads directly. The file is deleted when the document or merger is disposed.

The threshold can be changed before loading:

```csharp
AppContext.SetData("PdfiumWrapper.SpoolThreshold", 16L * 1024 * 1024); // bytes
```

For very large PDFs or high-volume stream ingestion pipelines, consider these tradeoffs:

- If you already have the full PDF in a `byte[]` that you will not modify until the document is disposed, pass it directly: it is used in place, without a copy.
- If the PDF is already a file, open it by path. PDFium then reads it from disk as needed and nothing is copied into managed memory.
- Stream inputs under the threshold, `MemoryStream` included, are copied once into memory the document owns.

### Large Stream Examples

#### 1. If You Already Have the PDF as `byte[]`

This is the simplest option when the payload is already fully materialized in memory:

```csharp
public void ProcessPdfBytes(byte[] pdfBytes)
{
    using var doc = new PdfDocument(pdfBytes);

    foreach (var text in doc.ProcessAllPages(page => page.ExtractText()))
    {
        // Process extracted text
    }
}
```

Why use this:

- No extra copy inside the wrapper
- Good when your upstream already gives you a `byte[]`
- The array is pinned and read for as long as the document is open: do not modify, reuse or return it to a pool before disposing the document

#### 2. If the PDF Is in a Stream You Will Reuse

If the PDF arrives in a `MemoryStream` (or any stream) that you reset or return to a pool after loading, pass the stream:

```csharp
public void ProcessPdfMemoryStream(byte[] pdfBytes)
{
    using var stream = new MemoryStream(capacity: pdfBytes.Length);
    stream.Write(pdfBytes, 0, pdfBytes.Length);
    stream.Position = 0;

    using var doc = new PdfDocument(stream);

    doc.ProcessAllPages(page =>
    {
        var size = (page.Width, page.Height);
        // Process page
    });
}
```

Why use this:

- The document copies the bytes into a pooled buffer it owns, so the stream can be reset, reused or returned to a pool as soon as the constructor returns
- The copy is one `memcpy` into a rented array, returned to the pool when the document is disposed

#### 3. For Very Large Streams, Spool to a Temporary File

The stream constructors spool inputs above the threshold to a temporary file on their own, but they read the stream synchronously. For a network stream or an HTTP upload, copying it to disk yourself keeps that I/O asynchronous, and opening by path avoids any managed copy:

```csharp
public async Task ProcessLargePdfStreamAsync(Stream input, CancellationToken cancellationToken = default)
{
    string tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pdf");

    try
    {
        await using (var file = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            useAsync: true))
        {
            await input.CopyToAsync(file, cancellationToken);
        }

        using var doc = new PdfDocument(tempPath);

        await foreach (var bytes in doc.StreamImageBytesAsync(ImageFormat.Jpeg, quality: 90, dpi: 200))
        {
            // Process one page at a time
        }
    }
    finally
    {
        if (File.Exists(tempPath))
            File.Delete(tempPath);
    }
}
```

Why use this:

- The copy from the source stream is asynchronous
- Nothing is held in managed memory, whatever the file size
- Works well with network streams, HTTP uploads, and other forward-only sources

#### 4. For ASP.NET Core Uploads

For large uploads, avoid reading the entire request body into a new `byte[]` unless you know the files are small:

```csharp
public async Task<IActionResult> ProcessUpload(IFormFile file, CancellationToken cancellationToken)
{
    string tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}_{file.FileName}");

    try
    {
        await using (var output = System.IO.File.Create(tempPath))
        await using (var input = file.OpenReadStream())
        {
            await input.CopyToAsync(output, cancellationToken);
        }

        using var doc = new PdfDocument(tempPath);
        var pageCount = doc.PageCount;

        return Ok(new { pageCount });
    }
    finally
    {
        if (File.Exists(tempPath))
            File.Delete(tempPath);
    }
}
```

Rule of thumb:

- Small payload already in memory, left untouched while the document is open: use `byte[]`
- In-memory stream that is reused or pooled: pass the stream (its bytes are copied)
- Any other stream: pass it to the constructor (read up front; spooled to a temporary file above 64 MB)
- Large or slow stream in async code: copy it to a file asynchronously, then open by path

---

## Complete Examples

### Example 1: Batch Convert PDFs to Images

```csharp
public class PdfToImageConverter
{
    private readonly string _outputDirectory;
    private readonly int _dpi;
    private readonly ImageFormat _format;

    public PdfToImageConverter(string outputDirectory, int dpi = 300, ImageFormat format = ImageFormat.Png)
    {
        _outputDirectory = outputDirectory;
        _dpi = dpi;
        _format = format;
        
        Directory.CreateDirectory(outputDirectory);
    }
    
    public async Task ConvertAllAsync(string[] pdfPaths, IProgress<int>? progress = null)
    {
        int completed = 0;
        
        foreach (var pdfPath in pdfPaths)
        {
            await ConvertSingleAsync(pdfPath);
            
            completed++;
            progress?.Report(completed * 100 / pdfPaths.Length);
        }
    }
    
    private async Task ConvertSingleAsync(string pdfPath)
    {
        var baseName = Path.GetFileNameWithoutExtension(pdfPath);
        var docOutputDir = Path.Combine(_outputDirectory, baseName);
        Directory.CreateDirectory(docOutputDir);
        
        using var doc = new PdfDocument(pdfPath);
        await doc.SaveAsImagesAsync(docOutputDir, "page", _format, 100, _dpi, _dpi);
    }
}

// Usage
var converter = new PdfToImageConverter("output", dpi: 300);
var progress = new Progress<int>(p => Console.WriteLine($"Progress: {p}%"));
await converter.ConvertAllAsync(Directory.GetFiles("input", "*.pdf"), progress);
```

### Example 2: Extract and Index PDF Text

```csharp
public record PdfTextIndex(string FilePath, int PageCount, Dictionary<int, string> PageTexts);

public class PdfTextExtractor
{
    public PdfTextIndex ExtractFromFile(string pdfPath)
    {
        using var doc = new PdfDocument(pdfPath);
        
        var pageTexts = new Dictionary<int, string>();
        
        doc.ProcessAllPages(page =>
        {
            pageTexts[page.PageIndex] = page.ExtractText();
        });
        
        return new PdfTextIndex(pdfPath, doc.PageCount, pageTexts);
    }
    
    public async Task<PdfTextIndex[]> ExtractFromMultipleAsync(string[] pdfPaths)
    {
        var results = new List<PdfTextIndex>();
        
        // Safe: each file gets its own document. Text extraction is native work, which the
        // library serializes, so the parallel loop adds little throughput here.
        await Parallel.ForEachAsync(pdfPaths, new ParallelOptions { MaxDegreeOfParallelism = 2 }, async (path, ct) =>
        {
            var index = ExtractFromFile(path);
            lock (results)
            {
                results.Add(index);
            }
            await Task.CompletedTask;
        });
        
        return results.ToArray();
    }
}
```

### Example 3: Merge Multiple PDFs with Progress

```csharp
public class PdfMergeService
{
    // PdfMerger is synchronous. From a UI or request thread, run the whole merge with
    // Task.Run(() => service.Merge(paths, progress)); do not use await Task.Yield() for this,
    // which posts back to the caller's SynchronizationContext.
    public byte[] Merge(string[] pdfPaths, IProgress<string>? progress = null)
    {
        using var merger = new PdfMerger();
        
        for (int i = 0; i < pdfPaths.Length; i++)
        {
            var path = pdfPaths[i];
            progress?.Report($"Adding document {i + 1}/{pdfPaths.Length}: {Path.GetFileName(path)}");
            
            merger.AppendDocument(path);
        }
        
        progress?.Report("Generating final PDF...");
        return merger.ToBytes();
    }
    
    public void MergeWithOptions(MergeOptions options)
    {
        using var merger = new PdfMerger();
        
        foreach (var source in options.Sources)
        {
            if (source.PageRange != null)
            {
                merger.AppendPages(source.FilePath, source.PageRange);
            }
            else if (source.PageIndices != null)
            {
                merger.AppendPages(source.FilePath, source.PageIndices);
            }
            else
            {
                merger.AppendDocument(source.FilePath);
            }
        }
        
        merger.Save(options.OutputPath);
    }
}

public record MergeOptions(MergeSource[] Sources, string OutputPath);
public record MergeSource(string FilePath, string? PageRange = null, int[]? PageIndices = null);

// Usage
var service = new PdfMergeService();
service.MergeWithOptions(new MergeOptions(
    Sources: new[]
    {
        new MergeSource("cover.pdf"),
        new MergeSource("content.pdf", PageRange: "1-10"),
        new MergeSource("appendix.pdf", PageIndices: new[] { 0, 2, 4 })
    },
    OutputPath: "final-document.pdf"
));
```

---

## Performance Tips Summary

| Tip | Impact |
|-----|--------|
| Use `SaveAsTiff` for document scanning workflows | Direct PDFium-to-libtiff pipeline, fastest TIFF output |
| Use `StreamImageBytes` instead of collecting all pages | O(1) memory per page instead of O(N) |
| Use `ProcessAllPages` instead of `GetAllPages` | Prevents memory leaks |
| Dispose all PDF objects with `using` | Critical for memory management |
| Lower DPI for previews (72-150 DPI) | Faster processing, less memory |
| Higher DPI for print (300 DPI) | Better quality, more memory |
| Do not force `GC.Collect()` in batches | Native memory is released by `Dispose()`; a forced collection only pauses the process |
| One thread at a time per document, page, form or merger | Required; different objects on different threads are safe |
| Parallel callers for image conversion | Encoding and output overlap; native rendering stays serialized |
| Bound parallel operations | Each caller in flight holds a rendered page; extra callers only wait for the gate |
| Use async methods in services | Wait for the native gate without blocking thread-pool threads |
| More replicas for more throughput | Each process has its own PDFium and its own gate |
| `PdfiumWrapper.Processing` worker pool for a single deployable | Parallel rendering, crash isolation and dynamic sizing without hand-built orchestration |

---

## See Also

- [API Reference](API-REFERENCE.md) - Complete API documentation
- [Best Practices](BEST-PRACTICES.md) - Thread safety and ASP.NET Core integration
- [PDF Editing](PDF-EDITING.md) - Creating and editing PDF content
- [Troubleshooting](TROUBLESHOOTING.md) - Common issues and solutions
