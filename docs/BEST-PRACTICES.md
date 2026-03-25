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

### The Core Problem

PDFium, the underlying native library, is **not thread-safe**. This means:

- Do not access the same `PdfDocument` instance from multiple threads
- Do not access the same `PdfPage` instance from multiple threads  
- Do not access the same `PdfForm` instance from multiple threads
- Loading the same PDF file simultaneously from multiple threads can cause issues

### Safe Patterns

#### Pattern 1: One Document Per Thread

Each thread should have its own `PdfDocument` instance:

```csharp
// ✅ SAFE: Each thread has its own document
await Parallel.ForEachAsync(pdfFiles, async (file, ct) =>
{
    using var document = new PdfDocument(file);
    // Process document...
});
```

#### Pattern 2: Sequential Processing with Async

Use async methods for UI responsiveness, but process sequentially:

```csharp
// ✅ SAFE: Sequential processing, UI stays responsive
using var document = new PdfDocument("large.pdf");
var bitmaps = await document.ConvertToBitmapsAsync(dpi: 300);
```

#### Pattern 3: Document Per Request (ASP.NET Core)

Create a new document instance for each HTTP request:

```csharp
// ✅ SAFE: Each request gets its own document
[HttpPost("convert")]
public async Task<IActionResult> ConvertPdf(IFormFile file)
{
    using var stream = file.OpenReadStream();
    using var document = new PdfDocument(stream);
    
    var images = document.ConvertToImageBytes(SKEncodedImageFormat.Png, 100, 150);
    return File(images[0], "image/png");
}
```

### Unsafe Patterns to Avoid

```csharp
// ❌ UNSAFE: Sharing document across threads
public class PdfService
{
    private PdfDocument _sharedDocument; // NEVER DO THIS
    
    public void ProcessPage(int pageIndex)
    {
        // Multiple threads accessing _sharedDocument = corruption/crashes
        using var page = _sharedDocument.GetPage(pageIndex);
    }
}
```

```csharp
// ❌ UNSAFE: Parallel processing of the same document
using var document = new PdfDocument("file.pdf");
Parallel.For(0, document.PageCount, i =>
{
    using var page = document.GetPage(i); // CRASHES or corrupts data
});
```

```csharp
// ❌ UNSAFE: Loading same file from multiple threads simultaneously
var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
{
    using var doc = new PdfDocument("same-file.pdf"); // Can cause issues
}));
await Task.WhenAll(tasks);
```

### If You Need Concurrent Access

Use a semaphore or lock to serialize access:

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
            var images = document.ConvertToImageBytes(SKEncodedImageFormat.Png, 100, dpi);
            
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
        // Process jobs sequentially to avoid PDFium threading issues
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
        
        // Process sequentially
        for (int i = 0; i < document.PageCount && !ct.IsCancellationRequested; i++)
        {
            using var page = document.GetPage(i);
            // Process page...
            await Task.Yield(); // Allow cancellation checks
        }
    }
}
```

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

When using `GetAllPages()`, dispose each page:

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

### Disposing Bitmaps

`SKBitmap` objects consume significant memory:

```csharp
var bitmaps = document.ConvertToBitmaps(300);
try
{
    for (int i = 0; i < bitmaps.Length; i++)
    {
        // Process bitmap
        using var image = SKImage.FromBitmap(bitmaps[i]);
        // ...
    }
}
finally
{
    foreach (var bitmap in bitmaps)
    {
        bitmap.Dispose();
    }
}
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
            using var bitmap = RenderPage(page, 150);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            
            yield return data.ToArray();
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
document.SaveAsImages("output", "page", SKEncodedImageFormat.Png, 100, 300, 300);

// Rather than:
var allBytes = document.ConvertToImageBytes(...); // Holds everything in memory
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

### Monitor Memory Usage

For server applications, monitor memory:

```csharp
public class PdfProcessingMetrics
{
    private readonly ILogger _logger;
    
    public async Task ProcessWithMetrics(Func<Task> operation)
    {
        var before = GC.GetTotalMemory(false);
        var stopwatch = Stopwatch.StartNew();
        
        try
        {
            await operation();
        }
        finally
        {
            stopwatch.Stop();
            var after = GC.GetTotalMemory(false);
            
            _logger.LogInformation(
                "PDF operation completed in {ElapsedMs}ms. Memory: {Before}MB -> {After}MB",
                stopwatch.ElapsedMilliseconds,
                before / 1024 / 1024,
                after / 1024 / 1024);
        }
    }
}
```

### Force Garbage Collection for Large Operations

After processing large documents:

```csharp
public void ProcessLargePdf(string path)
{
    using (var document = new PdfDocument(path))
    {
        // Process...
    }
    
    // After disposing, suggest GC for large operations
    GC.Collect();
    GC.WaitForPendingFinalizers();
}
```

### Limit Concurrent Operations

Use semaphores to limit concurrent PDF operations:

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
    catch (InvalidOperationException ex) when (ex.Message.Contains("password"))
    {
        return new PdfProcessResult 
        { 
            Success = false, 
            Error = "PDF is password protected" 
        };
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("Failed to load"))
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

### Pitfall 4: Loading Same File Multiple Times Concurrently

```csharp
// ❌ UNSAFE: Can cause file locking or corruption
var tasks = new[]
{
    Task.Run(() => { using var d = new PdfDocument("same.pdf"); }),
    Task.Run(() => { using var d = new PdfDocument("same.pdf"); }),
};
await Task.WhenAll(tasks);

// ✅ SAFE: Load to memory first, then process
var pdfBytes = await File.ReadAllBytesAsync("file.pdf");
var tasks = new[]
{
    Task.Run(() => { using var d = new PdfDocument(pdfBytes); }),
    Task.Run(() => { using var d = new PdfDocument(pdfBytes); }),
};
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
