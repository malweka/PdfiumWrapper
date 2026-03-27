# High-Throughput PDF Processing

This guide covers efficient patterns for processing large volumes of PDF documents, including image conversion, text extraction, merging, and parallel processing strategies.

## Table of Contents

- [Core Principles](#core-principles)
- [Converting Pages to Images (TIFF, PNG, JPEG)](#converting-pages-to-images)
- [Extracting Text from PDFs](#extracting-text-from-pdfs)
- [Merging PDF Documents](#merging-pdf-documents)
- [Parallel Processing Strategies](#parallel-processing-strategies)
- [Memory Management for Long-Running Processes](#memory-management-for-long-running-processes)
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

PDFium is not thread-safe. Each document should be processed independently:

```csharp
// ✅ CORRECT: Each iteration has its own document
foreach (var filePath in pdfFiles)
{
    using var doc = new PdfDocument(filePath);
    // Process...
}
```

---

## Converting Pages to Images

### Converting to TIFF (Built-in)

PdfiumWrapper includes native multi-page TIFF support via libtiff, with a zero-copy pipeline from PDFium's rendered bitmap directly to libtiff — no intermediate SkiaSharp encoding:

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

// Async version for UI responsiveness
public async Task ConvertPdfToTiffAsync(string pdfPath, string outputPath, int dpi = 200)
{
    using var doc = new PdfDocument(pdfPath);
    await doc.SaveAsTiffAsync(outputPath, dpi);
}
```

The TIFF pipeline renders each page at native resolution, converts BGRA pixels to the target format (bilevel or grayscale) using optimized unsafe code, and writes scanlines directly to libtiff with pinned buffers — no managed array copies per row.

### Converting to PNG/JPEG (Built-in)

For PNG and JPEG, use the built-in methods:

```csharp
public void ConvertPdfToImages(string pdfPath, string outputDirectory, int dpi = 300)
{
    using var doc = new PdfDocument(pdfPath);
    
    // Save all pages as PNG
    doc.SaveAsPngs(outputDirectory, "page", dpi);
    
    // Or save as JPEG with quality setting
    doc.SaveAsJpegs(outputDirectory, "page", quality: 90, dpi);
}
```

### Streaming Image Bytes Without Saving to Disk

Use `StreamImageBytes` / `StreamImageBytesAsync` to process one page at a time without holding all pages in memory:

```csharp
public void ProcessPdfPageImages(string pdfPath, SKEncodedImageFormat format, int dpi = 300)
{
    using var doc = new PdfDocument(pdfPath);
    int i = 0;
    foreach (var bytes in doc.StreamImageBytes(format, quality: 100, dpi))
    {
        File.WriteAllBytes($"page_{i++}.png", bytes);
        // Previous page's bytes are now eligible for GC
    }
}

// Async version — yields between pages for UI responsiveness
public async Task ProcessPdfPageImagesAsync(string pdfPath, SKEncodedImageFormat format, int dpi = 300)
{
    using var doc = new PdfDocument(pdfPath);
    int i = 0;
    await foreach (var bytes in doc.StreamImageBytesAsync(format, quality: 100, dpi))
    {
        await File.WriteAllBytesAsync($"page_{i++}.png", bytes);
    }
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

### ⚠️ Important: PDFium Thread Safety

PDFium is **NOT thread-safe**. You cannot:
- Share a `PdfDocument` across threads
- Access the same PDF file from multiple threads simultaneously

However, you **CAN** process different PDF files in parallel.

### Pattern 1: Parallel Processing of Different Files

```csharp
public async Task ProcessMultiplePdfsAsync(string[] pdfPaths, string outputDirectory)
{
    // ✅ SAFE: Each file processed independently with its own document
    await Parallel.ForEachAsync(pdfPaths, new ParallelOptions 
    { 
        MaxDegreeOfParallelism = Environment.ProcessorCount 
    }, 
    async (pdfPath, ct) =>
    {
        using var doc = new PdfDocument(pdfPath);
        
        var outputPath = Path.Combine(outputDirectory, 
            Path.GetFileNameWithoutExtension(pdfPath) + "_text.txt");
        
        var texts = doc.ProcessAllPages(page => page.ExtractText());
        await File.WriteAllTextAsync(outputPath, string.Join("\n\n", texts), ct);
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
    
    public PdfProcessor(int workerCount = 4, int boundedCapacity = 100)
    {
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
                
                // Process document...
                var pageCount = doc.PageCount;
                var texts = doc.ProcessAllPages(page => page.ExtractText());
                
                Console.WriteLine($"Processed {pdfPath}: {pageCount} pages");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing {pdfPath}: {ex.Message}");
            }
        }
    }
}

// Usage
var processor = new PdfProcessor(workerCount: 4);
var processingTask = processor.StartProcessingAsync();

foreach (var file in Directory.GetFiles("input", "*.pdf"))
{
    await processor.EnqueueAsync(file);
}

processor.Complete();
await processingTask;
```

### Pattern 3: Batch Processing with Periodic GC

For processing thousands of documents, periodically force garbage collection:

```csharp
public async Task ProcessLargeBatchAsync(string[] pdfPaths, int batchSize = 100)
{
    int processed = 0;
    
    foreach (var path in pdfPaths)
    {
        using var doc = new PdfDocument(path);
        
        // Process document...
        var texts = doc.ProcessAllPages(page => page.ExtractText());
        
        processed++;
        
        // Periodic cleanup to prevent memory buildup
        if (processed % batchSize == 0)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            
            Console.WriteLine($"Processed {processed}/{pdfPaths.Length} documents");
        }
    }
}
```

### Pattern 4: Parallel with File Copying (Avoid Concurrent File Access)

If you need maximum parallelism, copy files to temporary locations first:

```csharp
public async Task ProcessWithMaxParallelismAsync(string[] pdfPaths)
{
    await Parallel.ForEachAsync(pdfPaths, new ParallelOptions
    {
        MaxDegreeOfParallelism = Environment.ProcessorCount
    },
    async (originalPath, ct) =>
    {
        // Copy to temp file to avoid file locking issues
        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.pdf");
        
        try
        {
            await using (var source = File.OpenRead(originalPath))
            await using (var dest = File.Create(tempPath))
            {
                await source.CopyToAsync(dest, ct);
            }
            
            using var doc = new PdfDocument(tempPath);
            // Process...
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    });
}
```

---

## Memory Management for Long-Running Processes

### Monitor Memory Usage

```csharp
public class MemoryMonitor
{
    private readonly long _memoryThresholdBytes;
    
    public MemoryMonitor(long memoryThresholdMB = 1024)
    {
        _memoryThresholdBytes = memoryThresholdMB * 1024 * 1024;
    }
    
    public void CheckAndCollectIfNeeded()
    {
        var currentMemory = GC.GetTotalMemory(forceFullCollection: false);
        
        if (currentMemory > _memoryThresholdBytes)
        {
            Console.WriteLine($"Memory threshold exceeded ({currentMemory / 1024 / 1024}MB). Forcing GC...");
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            
            var afterGC = GC.GetTotalMemory(forceFullCollection: false);
            Console.WriteLine($"Memory after GC: {afterGC / 1024 / 1024}MB");
        }
    }
}
```

### Integrate Memory Monitoring with Processing

```csharp
public async Task ProcessWithMemoryMonitoringAsync(string[] pdfPaths)
{
    var monitor = new MemoryMonitor(memoryThresholdMB: 512);
    int processed = 0;
    
    foreach (var path in pdfPaths)
    {
        using var doc = new PdfDocument(path);
        
        // Use ProcessAllPages for safe page handling
        var images = doc.ConvertToBitmaps(dpi: 150);
        
        try
        {
            // Save images...
            for (int i = 0; i < images.Length; i++)
            {
                // Process each bitmap...
            }
        }
        finally
        {
            // Always dispose bitmaps
            foreach (var bitmap in images)
            {
                bitmap.Dispose();
            }
        }
        
        processed++;
        
        // Check memory every 50 documents
        if (processed % 50 == 0)
        {
            monitor.CheckAndCollectIfNeeded();
        }
    }
}
```

---

## Complete Examples

### Example 1: Batch Convert PDFs to Images

```csharp
public class PdfToImageConverter
{
    private readonly string _outputDirectory;
    private readonly int _dpi;
    private readonly SKEncodedImageFormat _format;
    
    public PdfToImageConverter(string outputDirectory, int dpi = 300, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
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
            
            // Periodic cleanup
            if (completed % 100 == 0)
            {
                GC.Collect(2, GCCollectionMode.Forced, true);
                GC.WaitForPendingFinalizers();
            }
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
            return true; // Return value required by Func<>
        });
        
        return new PdfTextIndex(pdfPath, doc.PageCount, pageTexts);
    }
    
    public async Task<PdfTextIndex[]> ExtractFromMultipleAsync(string[] pdfPaths)
    {
        var results = new List<PdfTextIndex>();
        
        // Process files in parallel (each file gets its own thread-local document)
        await Parallel.ForEachAsync(pdfPaths, async (path, ct) =>
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
    public async Task<byte[]> MergeAsync(string[] pdfPaths, IProgress<string>? progress = null)
    {
        using var merger = new PdfMerger();
        
        for (int i = 0; i < pdfPaths.Length; i++)
        {
            var path = pdfPaths[i];
            progress?.Report($"Adding document {i + 1}/{pdfPaths.Length}: {Path.GetFileName(path)}");
            
            merger.AppendDocument(path);
            
            // Yield to allow progress updates
            await Task.Yield();
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
| Periodic `GC.Collect()` in long batches | Prevents memory buildup |
| One document per thread | Required for thread safety |
| Use `Parallel.ForEachAsync` for file parallelism | Maximizes throughput |
| Bound parallel operations | Prevents resource exhaustion |
| Copy files for maximum parallelism | Avoids file locking issues |

---

## See Also

- [API Reference](API-REFERENCE.md) - Complete API documentation
- [Best Practices](BEST-PRACTICES.md) - Thread safety and ASP.NET Core integration
- [PDF Editing](PDF-EDITING.md) - Creating and editing PDF content
- [Troubleshooting](TROUBLESHOOTING.md) - Common issues and solutions
