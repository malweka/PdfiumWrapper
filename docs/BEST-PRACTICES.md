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
    await document.SaveAsTiffAsync(Path.ChangeExtension(file, ".tiff"), 200);
});
```

#### Pattern 2: Async Methods

The async methods (`SaveAsTiffAsync`, `RenderPagesAsync`, `StreamImageBytesAsync`, `SaveAsJpegsAsync`, `SaveAsImagesAsync`, `ProcessAllPagesAsync`) wait for the gate without blocking a thread. They process pages sequentially and yield between pages.

```csharp
// ✅ SAFE: sequential processing, no thread blocked while waiting for the gate
using var document = new PdfDocument("large.pdf");
var bitmaps = await document.RenderPagesAsync(dpi: 300);
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

    await foreach (var image in document.StreamImageBytesAsync(ImageFormat.Png, 100, 150))
    {
        return File(image, "image/png"); // first page
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
            
        if (!file.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
            return BadRequest("File must be a PDF");
        
        try
        {
            using var stream = new MemoryStream();
            await file.CopyToAsync(stream);
            stream.Position = 0;
            
            using var document = _pdfFactory.CreateFromStream(stream);
            
            var textBuilder = new StringBuilder();
            for (int i = 0; i < document.PageCount; i++)
            {
                using var page = document.GetPage(i);
                textBuilder.AppendLine($"--- Page {i + 1} ---");
                textBuilder.AppendLine(page.ExtractText());
            }
            
            return Ok(new { text = textBuilder.ToString(), pageCount = document.PageCount });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract text from PDF");
            return StatusCode(500, "Failed to process PDF");
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
            await file.CopyToAsync(stream);
            stream.Position = 0;
            
            using var document = _pdfFactory.CreateFromStream(stream);
            
            // For large documents, consider streaming response
            var images = document.StreamImageBytes(ImageFormat.Png, 100, dpi).ToList();
            
            // Return as zip for multiple pages
            if (images.Count > 1)
            {
                using var zipStream = new MemoryStream();
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
                {
                    for (int i = 0; i < images.Count; i++)
                    {
                        var entry = archive.CreateEntry($"page_{i + 1:D3}.png");
                        using var entryStream = entry.Open();
                        await entryStream.WriteAsync(images[i]);
                    }
                }
                
                zipStream.Position = 0;
                return File(zipStream.ToArray(), "application/zip", "pages.zip");
            }
            
            return File(images[0], "image/png", "page.png");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to convert PDF to images");
            return StatusCode(500, "Failed to process PDF");
        }
    }
}
```

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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process PDF job {JobId}", job.Id);
            }
        }
    }
    
    private async Task ProcessJobAsync(PdfJob job, CancellationToken ct)
    {
        using var document = new PdfDocument(job.PdfPath);
        
        // The async methods wait for the native gate without blocking a thread
        int pageNumber = 0;
        await foreach (var bytes in document.StreamImageBytesAsync(ImageFormat.Jpeg, 90, 200))
        {
            ct.ThrowIfCancellationRequested();
            await File.WriteAllBytesAsync($"{job.Id}_{++pageNumber:D3}.jpg", bytes, ct);
        }
    }
}
```

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

// Controller
[HttpPost("convert")]
[EnableRateLimiting("pdf-processing")]
public async Task<IActionResult> Convert(IFormFile file) { ... }
```

---

## Resource Management

### Always Use `using` Statements

Every disposable object must be properly disposed:

```csharp
// ✅ CORRECT: All resources disposed
using var document = new PdfDocument("file.pdf");
using var page = document.GetPage(0);
using var form = document.GetForm();

// Or with explicit blocks
using (var document = new PdfDocument("file.pdf"))
{
    using (var page = document.GetPage(0))
    {
        string text = page.ExtractText();
    }
}
```

### Dispose Order Matters

Dispose child objects before parent objects:

```csharp
// ✅ CORRECT: Page disposed before document
using var document = new PdfDocument("file.pdf");
using var page = document.GetPage(0);
string text = page.ExtractText();
// page.Dispose() called first (end of scope)
// document.Dispose() called second
```

### Disposing Arrays of Pages

`GetAllPages()` is obsolete: it loads every page at once. Prefer `ProcessAllPages`, which loads and disposes one page at a time. If you use it, dispose each page:

```csharp
var pages = document.GetAllPages();
try
{
    foreach (var page in pages)
    {
        Console.WriteLine(page.ExtractText());
    }
}
finally
{
    foreach (var page in pages)
    {
        page.Dispose();
    }
}
```

### Working with RawBitmaps

`RawBitmap` is a lightweight record and does not require disposal, but the pixel arrays can consume significant memory:

```csharp
var bitmaps = document.RenderPages(300);

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

### Process Large Documents in Chunks

For very large documents, process pages in batches:

```csharp
public async IAsyncEnumerable<byte[]> ConvertInChunksAsync(
    string pdfPath, 
    int batchSize = 10,
    [EnumeratorCancellation] CancellationToken ct = default)
{
    using var document = new PdfDocument(pdfPath);
    
    for (int i = 0; i < document.PageCount; i += batchSize)
    {
        ct.ThrowIfCancellationRequested();
        
        int endPage = Math.Min(i + batchSize, document.PageCount);
        
        for (int j = i; j < endPage; j++)
        {
            using var page = document.GetPage(j);
            // Render and encode page as PNG
            int width = (int)(page.Width / 72.0 * 150);
            int height = (int)(page.Height / 72.0 * 150);
            byte[] pixels = page.RenderToBytes(width, height);

            yield return pixels;
        }
        
        await Task.Yield(); // Allow other work
    }
}
```

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

### Stream Large Files

For very large output, stream to disk instead of memory:

```csharp
// ✅ Stream to files instead of holding all in memory
document.SaveAsImages("output", "page", ImageFormat.Png, 100, 300, 300);

// Or stream one page at a time for minimal memory:
foreach (var bytes in document.StreamImageBytes(ImageFormat.Png, 100, 300))
{
    // Process and discard — only one page in memory at a time
}
```

### Use JPEG for Size, PNG for Quality

```csharp
// Smaller files, acceptable quality
document.SaveAsJpegs("output", quality: 85, dpi: 150);

// Lossless, larger files
document.SaveAsPngs("output", dpi: 150);
```

---

## Memory Management

### Where the Memory Goes

Most memory used while processing PDFs is native: PDFium's document and font data, and rendered bitmaps. A US Letter page rendered as BGRA at 300 DPI is about 32 MiB. Native memory is released by `Dispose()`, not by the garbage collector.

- `RenderPages` returns every page as a managed `byte[]` at once. For large documents use `StreamImageBytes` / `StreamImageBytesAsync`, `SaveAsTiff`, or the `SaveAs...` methods, which hold one page at a time.
- `Save`, `SaveToStream`, `PdfMerger.Save` and `PdfMerger.ToBytes` serialize the whole PDF into a pooled in-memory buffer before writing it, so peak memory includes the full output size.
- `new PdfDocument(stream)` and `new PdfMerger(stream)` read the stream to its end during construction. Up to 64 MB is held in memory; larger inputs go to a temporary file that is deleted on dispose.

### Monitor Memory Usage

`GC.GetTotalMemory` reports managed memory only. It does not include PDFium's native memory or rendered bitmaps, so it is not a PDF memory monitor. Use the process working set:

```csharp
public class PdfProcessingMetrics
{
    private readonly ILogger _logger;
    
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
public class PdfProcessingPool
{
    private readonly SemaphoreSlim _semaphore;
    
    public PdfProcessingPool(int maxConcurrent = 4)
    {
        _semaphore = new SemaphoreSlim(maxConcurrent);
    }
    
    public async Task<T> ProcessAsync<T>(Func<Task<T>> operation)
    {
        await _semaphore.WaitAsync();
        try
        {
            return await operation();
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
```

---

## Error Handling

### Handle Common Exceptions

```csharp
public PdfProcessResult ProcessPdf(byte[] pdfData, string? password = null)
{
    try
    {
        using var document = new PdfDocument(pdfData, password);
        return new PdfProcessResult 
        { 
            Success = true, 
            PageCount = document.PageCount 
        };
    }
    catch (PdfiumException ex) when (ex.ErrorCode == PdfiumErrorCode.Password)
    {
        return new PdfProcessResult 
        { 
            Success = false, 
            Error = "PDF is password protected" 
        };
    }
    catch (PdfiumException)
    {
        return new PdfProcessResult 
        { 
            Success = false, 
            Error = "Invalid or corrupted PDF file" 
        };
    }
    catch (OutOfMemoryException)
    {
        return new PdfProcessResult 
        { 
            Success = false, 
            Error = "PDF too large to process" 
        };
    }
}
```

### Validate Input

```csharp
public void ValidatePdfInput(IFormFile file)
{
    if (file == null)
        throw new ArgumentNullException(nameof(file));
        
    if (file.Length == 0)
        throw new ArgumentException("File is empty");
        
    if (file.Length > 100_000_000) // 100MB
        throw new ArgumentException("File exceeds maximum size");
        
    // Check magic bytes
    using var stream = file.OpenReadStream();
    var header = new byte[5];
    stream.Read(header, 0, 5);
    
    if (header[0] != '%' || header[1] != 'P' || header[2] != 'D' || header[3] != 'F')
        throw new ArgumentException("File is not a valid PDF");
}
```

### Save Errors

`SaveToStream` and `PdfMerger.Save(stream)` serialize the PDF in memory and then write to your stream. A failure inside PDFium throws `InvalidOperationException`. A failure in the destination stream throws whatever the stream throws, unchanged:

```csharp
try
{
    document.SaveToStream(output);
}
catch (IOException ex)
{
    // The destination failed (disk full, connection closed, ...)
}
catch (InvalidOperationException ex)
{
    // PDFium could not serialize the document
}
```

### Malformed Input

A damaged PDF normally fails with `InvalidOperationException` when it is opened. A native abort inside PDFium cannot be caught and ends the process. If the service must survive hostile input, run conversions in a separate process. See [Troubleshooting](TROUBLESHOOTING.md#process-aborts-on-a-damaged-pdf).

---

## Common Pitfalls

### Pitfall 1: Not Disposing Resources

```csharp
// ❌ MEMORY LEAK: Document never disposed
public string GetText(string path)
{
    var document = new PdfDocument(path);
    var page = document.GetPage(0);
    return page.ExtractText();
    // document and page are never disposed!
}

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
// ❌ CRASH: GetForm() can return null
var form = document.GetForm();
form.SetFormFieldValue("Name", "John"); // NullReferenceException!

// ✅ CORRECT
var form = document.GetForm();
if (form != null)
{
    form.SetFormFieldValue("Name", "John");
    form.Dispose();
}
```

### Pitfall 3: Wrong Page Index

```csharp
// ❌ Pages are 0-indexed
using var page = document.GetPage(1); // Gets SECOND page, not first

// ✅ CORRECT
using var page = document.GetPage(0); // First page

// Note: PdfMerger.AppendPages with string range uses 1-based indexing
merger.AppendPages(source, "1,2,3"); // First three pages (1-based)
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

// ✅ CORRECT: use the form while the document is alive
using var document = new PdfDocument("form.pdf");
using var form = document.GetForm();
form?.SetFormFieldValue("Name", "John");
document.Save("filled_form.pdf");
```

### Pitfall 5: Ignoring Save After Modifications

```csharp
// ❌ Changes lost: Modified but not saved
using var document = new PdfDocument("form.pdf");
var form = document.GetForm();
form?.SetFormFieldValue("Name", "John");
// Document disposed without saving!

// ✅ CORRECT
using var document = new PdfDocument("form.pdf");
var form = document.GetForm();
if (form != null)
{
    form.SetFormFieldValue("Name", "John");
    form.Dispose();
}
document.Save("filled_form.pdf");
```

### Pitfall 6: High DPI for Thumbnails

```csharp
// ❌ WASTEFUL: 300 DPI for a 100px thumbnail
document.SaveAsPngs("thumbs", dpi: 300); // Generates huge images

// ✅ EFFICIENT: Use appropriate DPI
document.SaveAsPngs("thumbs", dpi: 72);
```


---

## See Also

- [High-Throughput Processing](HIGH-THROUGHPUT-PROCESSING.md) - Processing large volumes of PDFs efficiently
- [API Reference](API-REFERENCE.md) - Complete API documentation
- [PDF Editing](PDF-EDITING.md) - Creating and editing PDF content
- [Troubleshooting](TROUBLESHOOTING.md) - Common issues and solutions
