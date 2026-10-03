# Best Practices

This guide covers thread safety, ASP.NET Core integration, performance optimization, and common pitfalls when using PdfiumWrapper.

## Table of Contents

- [Thread Safety](#thread-safety)
- [ASP.NET Core Integration](#aspnet-core-integration)
- [Resource Management](#resource-management)
- [Performance Optimization](#performance-optimization)
- [Memory Management](#memory-management)
- [Error Handling](#error-handling)
- [Common Pitfalls](#common-pitfalls)

---

## Thread Safety

### How It Works

PDFium, the underlying native library, allows **one native call per process at a time**, across all documents. Its fonts, caches and reference counts are shared between documents, so two threads working on two different documents still collide inside PDFium.

PdfiumWrapper 2.0 enforces this rule itself. Every public operation enters one process-wide gate (`PdfiumRuntime`) before it calls PDFium. You do not need your own lock around the library.

What this means in practice:

- **Different objects on different threads are safe.** Each thread can load, render, extract, fill and save its own documents and mergers. Loading the same file from several threads is fine.
- **Native work takes turns.** Loading, rendering, text extraction, form access, page import and saving are serialized across the whole process.
- **Encoding and output overlap.** Pixel conversion, PNG/JPEG/TIFF encoding and file or stream writes run outside the gate, so one caller's encoding runs while another caller renders. That share of the work is what parallel callers gain; native rendering is not multiplied by adding threads.
- **One object is still single-threaded.** Do not use the same `PdfDocument`, `PdfPage`, `PdfForm`, `PdfMerger` or page object from two threads at once.
- Copying files to temporary locations, loading into byte arrays, or keeping "one document per thread" never fixed PDFium's shared state, and none of it is needed for safety.

### Safe Patterns

#### Pattern 1: Parallel Over Different Files, Bounded

Each worker has its own `PdfDocument`. Bound the degree of parallelism: every caller in flight holds a rendered page in memory, and callers beyond the point where encoding keeps the cores busy only wait for the gate. See [High-Throughput Processing](HIGH-THROUGHPUT-PROCESSING.md#measured-capacity-and-sizing) for sizing.

```csharp
// ✅ SAFE: each worker has its own document
await Parallel.ForEachAsync(pdfFiles, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (file, ct) =>
{
    using var document = new PdfDocument(file);
    await document.SaveAsTiffAsync(Path.ChangeExtension(file, ".tiff"), dpi: 200, cancellationToken: ct);
});
```

#### Pattern 2: Async Methods

The async methods (`RenderPagesAsync`, `StreamImageBytesAsync`, `StreamJpegBytesAsync`, `SaveAsPngsAsync`, `SaveAsJpegsAsync`, `SaveAsImagesAsync`, `SaveAsTiffAsync`, `ProcessAllPagesAsync`):

- wait for the gate without blocking a thread;
- process pages sequentially, on thread-pool threads;
- never post work to the caller's `SynchronizationContext`, so blocking on one from a UI thread does not deadlock, and the delegate given to `ProcessAllPagesAsync` runs on a pool thread;
- can be cancelled. Task-returning methods take a `CancellationToken` as their last parameter; the streaming methods take it through `WithCancellation`. The token is checked before each page (see [Cancellation](EXAMPLES.md#cancellation)).

```csharp
// ✅ SAFE: sequential processing, no thread blocked while waiting for the gate
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
using var document = new PdfDocument("large.pdf");
await document.SaveAsPngsAsync("pages", dpi: 300, cancellationToken: cts.Token);
```

Synchronous methods, including constructors, block the calling thread while they wait. In a busy async service prefer the async methods: with 192 concurrent conversions on a thread pool pinned to 24 threads, a heartbeat work item waited 1.6 ms (p99) when the conversions used the async API and about 2.5 s when they called the synchronous API from pool threads.

#### Pattern 3: Document Per Request (ASP.NET Core)

Create a new document instance for each HTTP request:

```csharp
// ✅ SAFE: Each request gets its own document
[HttpPost("convert")]
public async Task<IActionResult> ConvertPdf(IFormFile file)
{
    using var stream = file.OpenReadStream();
    using var document = new PdfDocument(stream);

    // First page only; stops if the client disconnects
    await foreach (var image in document.StreamImageBytesAsync(ImageFormat.Png, dpi: 150)
                       .WithCancellation(HttpContext.RequestAborted))
    {
        return File(image, "image/png");
    }

    return NoContent();
}
```

### Unsafe Patterns to Avoid

```csharp
// ❌ UNSAFE: One document used by several threads at once
public class PdfService
{
    private PdfDocument _sharedDocument; // shared by concurrent requests
    
    public void ProcessPage(int pageIndex)
    {
        // Two threads inside the same PdfDocument at once is not supported
        using var page = _sharedDocument.GetPage(pageIndex);
    }
}
```

```csharp
// ❌ UNSAFE: Parallel processing of the same document
using var document = new PdfDocument("file.pdf");
Parallel.For(0, document.PageCount, i =>
{
    using var page = document.GetPage(i); // one document, many threads
});
```

Splitting one document's pages across threads would not be faster even if it were supported: page rendering is native work and is serialized.

### Sharing One Object Across Threads

The library's gate protects PDFium. It does not make a single wrapper object safe for concurrent use. If one object must be shared, for example a long-lived document that serves page requests, serialize access to that object yourself:

```csharp
public class ThreadSafePdfService : IDisposable
{
    private readonly PdfDocument _document;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    
    public ThreadSafePdfService(string path)
    {
        _document = new PdfDocument(path);
    }
    
    public async Task<string> ExtractTextAsync(int pageIndex)
    {
        await _semaphore.WaitAsync();
        try
        {
            using var page = _document.GetPage(pageIndex);
            return page.ExtractText();
        }
        finally
        {
            _semaphore.Release();
        }
    }
    
    public void Dispose()
    {
        _semaphore.Dispose();
        _document.Dispose();
    }
}
```

---

## ASP.NET Core Integration

### Recommended: Transient/Scoped Document Creation

**Do not register `PdfDocument` as a singleton.** Create documents per-request:

```csharp
// ✅ CORRECT: Factory pattern
public interface IPdfDocumentFactory
{
    PdfDocument CreateFromStream(Stream stream);
    PdfDocument CreateFromBytes(byte[] data);
}

public class PdfDocumentFactory : IPdfDocumentFactory
{
    public PdfDocument CreateFromStream(Stream stream) => new PdfDocument(stream);
    public PdfDocument CreateFromBytes(byte[] data) => new PdfDocument(data);
}

// Register in Program.cs
builder.Services.AddSingleton<IPdfDocumentFactory, PdfDocumentFactory>();
```

`new PdfDocument(stream)` reads the stream to its end during construction and keeps its own copy. `new PdfDocument(byte[])` uses the array in place: it stays pinned until the document is disposed and must not be modified until then.

### Controller Example

```csharp
[ApiController]
[Route("api/[controller]")]
public class PdfController : ControllerBase
{
    private readonly IPdfDocumentFactory _pdfFactory;
    private readonly ILogger<PdfController> _logger;
    
    public PdfController(IPdfDocumentFactory pdfFactory, ILogger<PdfController> logger)
    {
        _pdfFactory = pdfFactory;
        _logger = logger;
    }
    
    [HttpPost("extract-text")]
    [RequestSizeLimit(50_000_000)] // 50MB limit
    public async Task<IActionResult> ExtractText(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest("No file provided");

        try
        {
            using var stream = new MemoryStream();
            await file.CopyToAsync(stream, HttpContext.RequestAborted);
            stream.Position = 0;

            using var document = _pdfFactory.CreateFromStream(stream);

            // One page loaded at a time; waits for the native gate without blocking a thread
            string[] pages = await document.ProcessAllPagesAsync(page => page.ExtractText(), HttpContext.RequestAborted);

            return Ok(new { pages, pageCount = pages.Length });
        }
        catch (PdfiumException ex)
        {
            // Wrong password, not a PDF, corrupted: the client's problem, not the server's
            return BadRequest($"Cannot open the PDF ({ex.ErrorCode})");
        }
    }

    [HttpPost("convert-to-images")]
    [RequestSizeLimit(50_000_000)]
    public async Task<IActionResult> ConvertToImages(IFormFile file, [FromQuery] int dpi = 150)
    {
        if (file == null || file.Length == 0)
            return BadRequest("No file provided");

        dpi = Math.Clamp(dpi, 72, 600); // Limit DPI range

        try
        {
            using var stream = new MemoryStream();
            await file.CopyToAsync(stream, HttpContext.RequestAborted);
            stream.Position = 0;

            using var document = _pdfFactory.CreateFromStream(stream);

            // Pages are rendered and added one at a time; only the zip grows in memory.
            // For very large outputs, write the zip to a temporary file instead.
            var zipStream = new MemoryStream();
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                int pageNumber = 0;
                await foreach (var png in document.StreamImageBytesAsync(ImageFormat.Png, dpi: dpi)
                                   .WithCancellation(HttpContext.RequestAborted))
                {
                    // PNG is already compressed
                    var entry = archive.CreateEntry($"page_{++pageNumber:D3}.png", CompressionLevel.NoCompression);
                    await using var entryStream = entry.Open();
                    await entryStream.WriteAsync(png, HttpContext.RequestAborted);
                }
            }

            zipStream.Position = 0;
            return File(zipStream, "application/zip", "pages.zip");
        }
        catch (PdfiumException ex)
        {
            return BadRequest($"Cannot open the PDF ({ex.ErrorCode})");
        }
        catch (InvalidOperationException ex)
        {
            // For example a page above the render pixel limit
            _logger.LogWarning(ex, "Failed to convert PDF to images");
            return UnprocessableEntity("The PDF could not be rendered");
        }
    }
}
```

The controller needs `using Microsoft.AspNetCore.Mvc;` and `using System.IO.Compression;`. Catch `PdfiumException` before `InvalidOperationException`: it derives from it.

### Background Service Example

For processing PDFs in background jobs:

```csharp
public class PdfProcessingService : BackgroundService
{
    private readonly ILogger<PdfProcessingService> _logger;
    private readonly Channel<PdfJob> _jobChannel;
    
    public PdfProcessingService(ILogger<PdfProcessingService> logger)
    {
        _logger = logger;
        _jobChannel = Channel.CreateBounded<PdfJob>(100);
    }
    
    public async Task QueueJobAsync(PdfJob job)
    {
        await _jobChannel.Writer.WriteAsync(job);
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // One job at a time keeps memory use predictable. Running several consumers is also
        // safe: the library serializes native work, and only encoding and output overlap.
        await foreach (var job in _jobChannel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessJobAsync(job, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to process PDF job {JobId}", job.Id);
            }
        }
    }
    
    private async Task ProcessJobAsync(PdfJob job, CancellationToken ct)
    {
        using var document = new PdfDocument(job.PdfPath);
        
        // Waits for the native gate without blocking a thread; the token is checked before each page
        int pageNumber = 0;
        await foreach (var bytes in document.StreamImageBytesAsync(ImageFormat.Jpeg, quality: 90, dpi: 200).WithCancellation(ct))
        {
            await File.WriteAllBytesAsync($"{job.Id}_{++pageNumber:D3}.jpg", bytes, ct);
        }
    }
}

public record PdfJob(string Id, string PdfPath);
```

A PDFium failure on one job (for example `PdfiumException` for a broken file) is logged and the loop continues; cancellation of `stoppingToken` ends it.

### Prefer Async Methods and Bound Concurrency

- In request handlers and background services, call the async methods. Synchronous methods block the calling thread while they wait for the native gate, and many blocked pool threads starve the rest of the application.
- Constructors are synchronous. Opening a document is short, but under heavy contention it also waits its turn.
- Limit how many conversions run at once (a bounded channel, `SemaphoreSlim`, or `MaxDegreeOfParallelism`). The limit bounds memory: each conversion in flight holds a rendered page. It is not needed for safety.

### Rate Limiting and Resource Protection

Protect your API from abuse:

```csharp
// Program.cs
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("pdf-processing", opt =>
    {
        opt.Window = TimeSpan.FromMinutes(1);
        opt.PermitLimit = 10; // 10 requests per minute
        opt.QueueLimit = 5;
    });
});

var app = builder.Build();
app.UseRateLimiter();
```

Then put `[EnableRateLimiting("pdf-processing")]` on the PDF actions, next to `[HttpPost]`.

---

## Resource Management

### Always Use `using` Statements

Every disposable object must be properly disposed:

```csharp
// ✅ CORRECT: All resources disposed
using var document = new PdfDocument("file.pdf");
using var page = document.GetPage(0);
using var form = document.GetForm();   // null when the PDF has no form; using accepts null
```

```csharp
// Or with explicit blocks
using (var document = new PdfDocument("file.pdf"))
{
    using (var page = document.GetPage(0))
    {
        string text = page.ExtractText();
    }
}
```

### The Document Owns Its Children

A document owns its pages, the forms returned by `GetForm()`, and page objects removed from its pages with `RemoveObject`. Disposing the document disposes all of them, so dispose order does not matter for releasing memory. What matters is not to use a child after its document is disposed: it throws `ObjectDisposedException` (see [Pitfall 4](#pitfall-4-using-a-form-or-page-after-its-document-is-disposed)). Still dispose pages and forms when you are done with them, so a long-lived document does not keep them loaded.

### Processing Every Page

Use `ProcessAllPages` or `ProcessAllPagesAsync`. They load, process and dispose one page at a time:

```csharp
using var document = new PdfDocument("file.pdf");

string[] texts = document.ProcessAllPages(page => page.ExtractText());

document.ProcessAllPages(page => Console.WriteLine($"Page {page.PageIndex + 1}: {page.Width} x {page.Height} pt"));
```

`GetAllPages()` is obsolete: it loads every page at once and leaves every page for you to dispose.

### Working with RawBitmaps

`RawBitmap` is a record holding a managed array and does not require disposal, but the pixel arrays can consume significant memory. Rendered pages are BGRx: the fourth byte of each pixel is not alpha.

```csharp
using var document = new PdfDocument("file.pdf");
var bitmaps = document.RenderPages(dpi: 150);   // every page at once

for (int i = 0; i < bitmaps.Length; i++)
{
    // Access raw pixel data
    byte[] pixels = bitmaps[i].Pixels;
    int width = bitmaps[i].Width;
    int height = bitmaps[i].Height;
    int stride = bitmaps[i].Stride;
    // Process pixel data...
}
// No disposal needed — RawBitmap is a plain record
```

---

## Performance Optimization

### Choose Appropriate DPI

Higher DPI = larger images = more memory = slower processing:

| Use Case | Recommended DPI |
|----------|-----------------|
| Screen display | 72-96 |
| Web thumbnails | 72-100 |
| Email/sharing | 150 |
| Print quality | 300 |
| High-quality print | 600 |

```csharp
// Thumbnail generation - low DPI is fine
document.SaveAsPngs("thumbs", dpi: 72);

// Print-ready images
document.SaveAsPngs("print", dpi: 300);
```

The DPI must be positive; zero or a negative value throws `ArgumentOutOfRangeException`. When the DPI comes from a request, clamp it (for example `Math.Clamp(dpi, 72, 600)`).

### Stream Large Documents Page by Page

For large documents, never hold every page at once. The streaming methods render, encode and hand over one page at a time:

```csharp
public async IAsyncEnumerable<byte[]> ConvertToPngsAsync(
    string pdfPath,
    int dpi = 150,
    [EnumeratorCancellation] CancellationToken ct = default)
{
    using var document = new PdfDocument(pdfPath);

    // The token is checked before each page; only one page's PNG is in memory at a time
    await foreach (var png in document.StreamImageBytesAsync(ImageFormat.Png, dpi: dpi).WithCancellation(ct))
    {
        yield return png;
    }
}
```

(`EnumeratorCancellationAttribute` is in `System.Runtime.CompilerServices`.) To write files, `SaveAsPngsAsync`, `SaveAsJpegsAsync`, `SaveAsImagesAsync` and `SaveAsTiffAsync` also hold one page at a time.

### Reuse Document Instances

Don't load the same document multiple times:

```csharp
// ❌ INEFFICIENT: Loading document multiple times
for (int i = 0; i < 10; i++)
{
    using var doc = new PdfDocument("file.pdf");
    using var page = doc.GetPage(0);
    // ...
}

// ✅ EFFICIENT: Load once, use multiple times
using var document = new PdfDocument("file.pdf");
for (int i = 0; i < document.PageCount; i++)
{
    using var page = document.GetPage(i);
    // ...
}
```

### Use JPEG for Size, PNG for Quality

```csharp
// Smaller files, acceptable quality (the default JPEG quality is 90)
document.SaveAsJpegs("output", quality: 85, dpi: 150);

// Lossless, larger files; PNG has no quality setting
document.SaveAsPngs("output", dpi: 150);
```

---

## Memory Management

### Where the Memory Goes

Most memory used while processing PDFs is native: PDFium's document and font data, and rendered bitmaps. A US Letter page rendered at 300 DPI (4 bytes per pixel) is about 32 MiB. Native memory is released by `Dispose()`, not by the garbage collector.

- `RenderPages` returns every page as a managed `byte[]` at once. For large documents use `StreamImageBytes` / `StreamImageBytesAsync`, `SaveAsTiff`, or the `SaveAs...` methods, which hold one page at a time.
- Each rendered page is limited to 268,435,456 pixels by default, so one oversized or hostile page cannot demand a multi-GiB bitmap. A larger page throws `InvalidOperationException`; the `PdfiumWrapper.MaxRenderPixels` AppContext data key changes the limit (see [Troubleshooting](TROUBLESHOOTING.md)).
- `Save`, `SaveToStream`, `PdfMerger.Save` and `PdfMerger.ToBytes` serialize the whole PDF into a pooled in-memory buffer before writing it, so peak memory includes the full output size.
- `new PdfDocument(stream)` and `new PdfMerger(stream)` read the stream to its end during construction. Up to 64 MB is held in memory; larger inputs go to a temporary file that is deleted on dispose. The `PdfiumWrapper.SpoolThreshold` AppContext data key changes the threshold.

### Monitor Memory Usage

`GC.GetTotalMemory` reports managed memory only. It does not include PDFium's native memory or rendered bitmaps, so it is not a PDF memory monitor. Use the process working set:

```csharp
public class PdfProcessingMetrics
{
    private readonly ILogger _logger;

    public PdfProcessingMetrics(ILogger<PdfProcessingMetrics> logger) => _logger = logger;

    public async Task ProcessWithMetrics(Func<Task> operation)
    {
        using var process = Process.GetCurrentProcess();
        long before = process.WorkingSet64;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await operation();
        }
        finally
        {
            stopwatch.Stop();
            process.Refresh();
            long after = process.WorkingSet64;

            _logger.LogInformation(
                "PDF operation completed in {ElapsedMs}ms. Working set: {Before}MB -> {After}MB",
                stopwatch.ElapsedMilliseconds,
                before / 1024 / 1024,
                after / 1024 / 1024);
        }
    }
}
```

### Do Not Force Garbage Collection

Calling `GC.Collect()` after each document or batch does not release PDF memory: disposing the document already did that. A forced blocking collection only pauses the process. Dispose every document, page, form and merger and let the runtime schedule collections.

If an object is dropped without `Dispose()`, its finalizer does not call PDFium. It queues the native handles, and the next PdfiumWrapper operation on any thread closes them. `PdfiumRuntime.ReleasePending()` closes them on demand. Relying on this delays the release of native memory, so treat it as a safety net.

### Limit Concurrent Operations

Use a semaphore to limit how many PDF operations are in flight. The limit bounds memory (each operation holds a rendered page); the library is safe without it:

```csharp
public sealed class PdfConcurrencyLimiter : IDisposable
{
    private readonly SemaphoreSlim _semaphore;

    public PdfConcurrencyLimiter(int maxConcurrent = 4)
    {
        _semaphore = new SemaphoreSlim(maxConcurrent);
    }

    public async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            return await operation();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose() => _semaphore.Dispose();
}
```

To run conversions in separate processes instead, use `PdfProcessingPool` from the `PdfiumWrapper.Processing` package (see [Malformed and Hostile Input](#malformed-and-hostile-input)).

---

## Error Handling

### Handle Common Exceptions

Opening a document that PDFium cannot load throws `PdfiumException` (it derives from `InvalidOperationException`). Its `ErrorCode` tells the cases apart: `Password`, `Format` (not a PDF or corrupted), `File` (cannot be opened), `Security` (unsupported security handler), `Page` or `Unknown`.

```csharp
public PdfProcessResult ProcessPdf(byte[] pdfData, string? password = null)
{
    try
    {
        using var document = new PdfDocument(pdfData, password);
        return new PdfProcessResult(Success: true, PageCount: document.PageCount);
    }
    catch (PdfiumException ex) when (ex.ErrorCode == PdfiumErrorCode.Password)
    {
        return new PdfProcessResult(Success: false, Error: "PDF is password protected");
    }
    catch (PdfiumException ex)
    {
        return new PdfProcessResult(Success: false, Error: $"Invalid or corrupted PDF file ({ex.ErrorCode})");
    }
}

public record PdfProcessResult(bool Success, int PageCount = 0, string? Error = null);
```

Other exceptions to expect:

| Exception | When |
|-----------|------|
| `ArgumentOutOfRangeException` | A page index outside the document, or a DPI that is zero or negative |
| `InvalidOperationException` | A page above the render pixel limit, a document with no pages passed to a render method, a failed save or page import |
| `ObjectDisposedException` | A document, page, form or page object used after it was disposed |
| `OperationCanceledException` | An async method whose `CancellationToken` was cancelled |

### Validate Input

```csharp
public void ValidatePdfInput(IFormFile file)
{
    ArgumentNullException.ThrowIfNull(file);

    if (file.Length == 0)
        throw new ArgumentException("File is empty");

    if (file.Length > 100_000_000) // 100MB
        throw new ArgumentException("File exceeds maximum size");

    // Check the magic bytes
    using var stream = file.OpenReadStream();
    Span<byte> header = stackalloc byte[5];
    int read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);

    if (read < 5 || !header.StartsWith("%PDF-"u8))
        throw new ArgumentException("File is not a PDF");
}
```

The header check is cheap but not proof: a file can start with `%PDF-` and still be corrupted. Opening it is the real check.

### Save Errors

`SaveToStream` and `PdfMerger.Save(stream)` serialize the PDF in memory and then write to your stream. A failure inside PDFium throws `InvalidOperationException`. A failure in the destination stream throws whatever the stream throws, unchanged:

```csharp
try
{
    document.SaveToStream(output);
}
catch (IOException)
{
    // The destination failed (disk full, connection closed, ...)
}
catch (InvalidOperationException)
{
    // PDFium could not serialize the document
}
```

### Malformed and Hostile Input

A damaged PDF normally fails when it is opened, with `PdfiumException` and `ErrorCode == PdfiumErrorCode.Format`. But a native abort inside PDFium cannot be caught and ends the process. If the service must survive hostile input, run conversions in worker processes with `PdfProcessingPool` from the optional `PdfiumWrapper.Processing` package. A worker that dies is replaced, and the job reports `PdfJobStatus.WorkerCrashed` instead of taking your process down:

```csharp
using PdfiumWrapper.Processing;

await using var pool = await PdfProcessingPool.CreateAsync();

var result = await pool.ConvertToTiffAsync(PdfInput.FromFile("upload.pdf"), "upload.tiff", dpi: 200);
if (!result.IsSuccess)
    Console.WriteLine($"{result.Status}: {result.Error}");
```

A cancelled single job reports `PdfJobStatus.Cancelled`; cancelling the token of a batch throws `OperationCanceledException`. See [High-Throughput Processing](HIGH-THROUGHPUT-PROCESSING.md#worker-pool) and [Troubleshooting](TROUBLESHOOTING.md#process-aborts-on-a-damaged-pdf).

---

## Common Pitfalls

### Pitfall 1: Not Disposing Resources

```csharp
// ❌ Native memory held until the finalizer runs
public string GetText(string path)
{
    var document = new PdfDocument(path);
    var page = document.GetPage(0);
    return page.ExtractText();
}
```

```csharp
// ✅ CORRECT
public string GetText(string path)
{
    using var document = new PdfDocument(path);
    using var page = document.GetPage(0);
    return page.ExtractText();
}
```

### Pitfall 2: Assuming Form Exists

```csharp
// ❌ CRASH: GetForm() returns null when the PDF has no form fields
var form = document.GetForm();
form.SetFormFieldValue("Name", "John"); // NullReferenceException!
```

```csharp
// ✅ CORRECT
using var form = document.GetForm();
if (form != null)
{
    form.SetFormFieldValue("Name", "John");
}
```

### Pitfall 3: Wrong Page Index

Page indices are 0-based; page ranges given as strings to `PdfMerger` are 1-based.

```csharp
using var second = document.GetPage(1); // the SECOND page
using var first = document.GetPage(0);  // the first page

merger.AppendPages(source, "1,2,3");           // the first three pages (1-based)
merger.AppendPages(source, new[] { 0, 1, 2 }); // the same pages by index (0-based)
```

### Pitfall 4: Using a Form or Page After Its Document Is Disposed

A document owns its pages, the forms returned by `GetForm()`, and the page objects removed from its pages with `RemoveObject`. Disposing the document disposes all of them. `GetForm()` returns a new form on each call.

```csharp
// ❌ ObjectDisposedException: the form died with its document
PdfForm? form;
using (var document = new PdfDocument("form.pdf"))
{
    form = document.GetForm();
}
form?.SetFormFieldValue("Name", "John");
```

```csharp
// ✅ CORRECT: use the form while the document is alive
using var document = new PdfDocument("form.pdf");
using var form = document.GetForm();
form?.SetFormFieldValue("Name", "John");
document.Save("filled_form.pdf");
```

### Pitfall 5: Ignoring Save After Modifications

Changes to forms and page content live in the open document until you save it.

```csharp
// ❌ Changes lost: modified but never saved
using var document = new PdfDocument("form.pdf");
using var form = document.GetForm();
form?.SetFormFieldValue("Name", "John");
// Document disposed without saving!
```

```csharp
// ✅ CORRECT
using var document = new PdfDocument("form.pdf");
using var form = document.GetForm();
form?.SetFormFieldValue("Name", "John");
document.Save("filled_form.pdf");
```

Page content also needs `page.GenerateContent()` before saving (see [PDF Editing](PDF-EDITING.md#important-workflow)).

### Pitfall 6: Rendering at a Higher DPI Than Needed

Render cost and memory grow with the square of the DPI: 300 DPI is 16 times the pixels of 75 DPI. Pick the DPI from the table in [Choose Appropriate DPI](#choose-appropriate-dpi); for thumbnails, 72 or less is enough.

---

## See Also

- [High-Throughput Processing](HIGH-THROUGHPUT-PROCESSING.md) - Processing large volumes of PDFs efficiently
- [API Reference](API-REFERENCE.md) - Complete API documentation
- [PDF Editing](PDF-EDITING.md) - Creating and editing PDF content
- [Troubleshooting](TROUBLESHOOTING.md) - Common issues and solutions
