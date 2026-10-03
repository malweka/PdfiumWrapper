# Troubleshooting

This guide covers common issues and their solutions when using PdfiumWrapper.

## Table of Contents

- [Installation Issues](#installation-issues)
- [Loading PDF Documents](#loading-pdf-documents)
- [Rendering Issues](#rendering-issues)
- [Form Filling Issues](#form-filling-issues)
- [Memory Issues](#memory-issues)
- [Threading Issues](#threading-issues)
- [Platform-Specific Issues](#platform-specific-issues)
- [Error Codes](#error-codes)

---

## Installation Issues

### Native Library Not Found

**Symptom:** `DllNotFoundException` or `Unable to load DLL 'pdfium'`

**Causes and Solutions:**

1. **Missing runtime identifier**
   
   Ensure your project targets the correct runtime:
   ```xml
   <PropertyGroup>
     <RuntimeIdentifier>win-x64</RuntimeIdentifier>
   </PropertyGroup>
   ```
   
   Or for multiple platforms:
   ```xml
   <PropertyGroup>
     <RuntimeIdentifiers>win-x64;linux-x64;osx-x64;osx-arm64</RuntimeIdentifiers>
   </PropertyGroup>
   ```

2. **Self-contained deployment without native binaries**
   
   When publishing self-contained, ensure native libraries are included:
   ```bash
   dotnet publish -c Release -r win-x64 --self-contained true
   ```

3. **Linux missing dependencies**
   
   PDFium may require additional libraries:
   ```bash
   # Ubuntu/Debian
   sudo apt-get install libfontconfig1 libfreetype6
   
   # Alpine
   apk add fontconfig freetype
   ```

---

## Loading PDF Documents

### "Failed to load PDF document"

**Symptom:** `InvalidOperationException: Failed to load PDF document`

**Possible Causes:**

1. **File doesn't exist**
   ```csharp
   // Check file exists first
   if (!File.Exists(path))
       throw new FileNotFoundException($"PDF not found: {path}");
   
   using var document = new PdfDocument(path);
   ```

2. **File is corrupted**
   ```csharp
   // Validate PDF header
   byte[] header = new byte[5];
   using var fs = File.OpenRead(path);
   fs.Read(header, 0, 5);
   
   if (header[0] != '%' || header[1] != 'P' || header[2] != 'D' || header[3] != 'F')
       throw new InvalidDataException("Not a valid PDF file");
   ```

3. **File is password-protected**
   ```csharp
   // Try with password
   try
   {
       using var document = new PdfDocument(path, password: "secret");
   }
   catch (InvalidOperationException ex) when (ex.Message.Contains("Error"))
   {
       Console.WriteLine("Incorrect password or encrypted PDF");
   }
   ```

4. **File is locked by another process**
   ```csharp
   // Load into memory first
   byte[] pdfBytes = File.ReadAllBytes(path);
   using var document = new PdfDocument(pdfBytes);
   ```

### "Failed to load PDF document from memory"

**Symptom:** PDF loads from file but not from byte array

**Possible Causes:**

1. **Empty or incomplete byte array**
   ```csharp
   if (pdfBytes == null || pdfBytes.Length == 0)
       throw new ArgumentException("PDF data is empty");
   ```

2. **Stream not fully read**
   ```csharp
   // Ensure stream is fully read
   using var memoryStream = new MemoryStream();
   await sourceStream.CopyToAsync(memoryStream);
   byte[] pdfBytes = memoryStream.ToArray();
   ```

3. **Stream position not reset**
   ```csharp
   // Reset position before loading
   stream.Position = 0;
   using var document = new PdfDocument(stream);
   ```

---

## Rendering Issues

### Blank or White Images

**Symptom:** Rendered images are completely white/blank

**Solutions:**

1. **Check page dimensions**
   ```csharp
   using var page = document.GetPage(0);
   Console.WriteLine($"Page size: {page.Width} x {page.Height}");
   
   // If dimensions are 0, the page might be invalid
   if (page.Width <= 0 || page.Height <= 0)
       throw new InvalidOperationException("Invalid page dimensions");
   ```

2. **Use correct render flags**
   ```csharp
   // Include annotations in render
   byte[] pixels = page.RenderToBytes(width, height, PDFium.FPDF_ANNOT);
   ```

### Poor Image Quality

**Symptom:** Images appear blurry or pixelated

**Solutions:**

1. **Increase DPI**
   ```csharp
   // For screen: 96-150 DPI
   // For print: 300+ DPI
   document.SaveAsPngs("output", dpi: 300);
   ```

2. **Use PNG instead of JPEG for text-heavy documents**
   ```csharp
   document.SaveAsPngs("output", dpi: 150); // Lossless
   // vs
   document.SaveAsJpegs("output", quality: 100, dpi: 150); // Still lossy
   ```

### Missing Text in Rendered Images

**Symptom:** Some text doesn't appear in rendered images

**Possible Causes:**

1. **Font embedding issues** - The PDF may reference fonts not available on the system
2. **Text rendering mode** - Some PDFs use invisible text (for OCR purposes)

**Solutions:**
- Ensure common fonts are installed on the system
- Test with a known-good PDF to isolate the issue

### Colors Look Wrong

**Symptom:** Colors appear inverted or incorrect

**Cause:** PDFium renders in BGRA format, not RGBA

**Solution:**
```csharp
// The library handles this internally, but if doing manual processing:
// Remember: byte order is Blue, Green, Red, Alpha
```

### "... exceeds the render limit of 268,435,456 pixels"

**Symptom:** `InvalidOperationException` such as `Rendering page index 4 at 60000 x 60000 pixels (3,600,000,000 pixels) exceeds the render limit ...` from `RenderPages`, `SaveAsTiff`, `SaveAsPngs`, `StreamImageBytes`, `RenderToBytes` or a worker pool image job

**Cause:** The page is very large (its page box comes from the file), the DPI is very high, or both. Each render is capped at 2^28 pixels (1 GiB as BGRA). The cap stops one crafted PDF from forcing a multi-GiB allocation. It is checked before PDFium allocates anything, and a partly written TIFF file is deleted.

**Solutions:**
- Lower the DPI for that document. `GetPageSize(i)` returns the page size in points, so the pixel size is points / 72 × DPI.
- If you trust the input and have the memory, raise the cap before rendering:
  ```csharp
  AppContext.SetData("PdfiumWrapper.MaxRenderPixels", 1L << 30); // pixels
  ```

A DPI, width or height of zero or less throws `ArgumentOutOfRangeException` instead.

---

## Form Filling Issues

### GetForm() Returns Null

**Symptom:** `document.GetForm()` returns `null` even though PDF has forms

**Possible Causes:**

1. **PDF has no AcroForm fields**
   - The PDF may have visual form elements that aren't actual form fields
   - Use a PDF editor to verify form fields exist

2. **XFA forms**
   - XFA forms are partially supported
   - Check if fields are XFA type in the form field list

### Form Field Not Found

**Symptom:** `ArgumentException: Form field 'FieldName' not found`

**Solutions:**

1. **List all fields to find correct name**
   ```csharp
   var form = document.GetForm();
   if (form != null)
   {
       foreach (var field in form.GetAllFormFields())
       {
           Console.WriteLine($"'{field.Name}' ({field.Type})");
       }
   }
   ```

2. **Field names are case-sensitive**
   ```csharp
   // These are different fields:
   form.SetFormFieldValue("FullName", "John");  // Correct
   form.SetFormFieldValue("fullname", "John");  // Different field!
   ```

3. **Field might be on a specific page**
   ```csharp
   var pageFields = form.GetFormFieldsOnPage(0);
   ```

### Changes Not Saved

**Symptom:** Form values revert after saving/reopening

**Solutions:**

1. **Ensure you call Save()**
   ```csharp
   var form = document.GetForm();
   if (form != null)
   {
       form.SetFormFieldValue("Name", "John");
       form.Dispose(); // Dispose form before saving
   }
   document.Save("output.pdf"); // Don't forget this!
   ```

2. **Dispose form before saving**
   ```csharp
   using (var form = document.GetForm())
   {
       form?.SetFormFieldValue("Field", "Value");
   }
   // Form disposed here
   document.Save("output.pdf");
   ```

### Checkbox Won't Check/Uncheck

**Symptom:** Checkbox appearance doesn't change

**Solutions:**

1. **Use correct method**
   ```csharp
   form.SetFormFieldChecked("CheckboxName", true);
   // Not:
   form.SetFormFieldValue("CheckboxName", "true"); // May not work for all PDFs
   ```

2. **Check export value**
   ```csharp
   // Some checkboxes have specific export values
   var field = form.GetAllFormFields().First(f => f.Name == "CheckboxName");
   Console.WriteLine($"Options: {string.Join(", ", field.Options)}");
   ```

---

## Memory Issues

### OutOfMemoryException

**Symptom:** `OutOfMemoryException` when processing PDFs

**Solutions:**

1. **Reduce DPI for large documents**
   ```csharp
   // A US Letter page rendered as BGRA at 300 DPI is about 32 MiB (uncompressed)
   // At 150 DPI it is about 8 MiB
   document.SaveAsPngs("output", dpi: 150);
   ```

2. **Process pages one at a time**
   ```csharp
   for (int i = 0; i < document.PageCount; i++)
   {
       using var page = document.GetPage(i);
       // Process single page
       // Page is disposed after each iteration
   }
   ```

3. **Don't hold all bitmaps in memory**
   ```csharp
   // ❌ Holds all bitmaps in memory
   var bitmaps = document.RenderPages(300);

   // ✅ Process one at a time
   for (int i = 0; i < document.PageCount; i++)
   {
       using var page = document.GetPage(i);
       // Render, save, dispose immediately
   }
   ```

4. **Stream encoded pages instead of collecting them, and bound how many documents are processed at once**
   ```csharp
   foreach (var pdfFile in largePdfList)
   {
       using var doc = new PdfDocument(pdfFile);

       int pageNumber = 0;
       foreach (var bytes in doc.StreamImageBytes(ImageFormat.Jpeg, 90, 150))
       {
           File.WriteAllBytes($"{Path.GetFileNameWithoutExtension(pdfFile)}_{++pageNumber:D3}.jpg", bytes);
           // Only one page's pixels and encoded bytes are alive at a time
       }
   }
   ```

   Forcing `GC.Collect()` between documents is not a fix. Rendered bitmaps and PDFium's own memory are native and are released by `Dispose()`, not by the garbage collector. `GC.GetTotalMemory` reports managed memory only, so it does not show them either; watch the process working set (`Process.WorkingSet64`) instead.

5. **Large saves and large stream inputs are buffered**

   `Save`, `SaveToStream`, `PdfMerger.Save` and `PdfMerger.ToBytes` serialize the whole PDF into a pooled in-memory buffer before writing it out, so peak memory includes the full output size. `new PdfDocument(stream)` and `new PdfMerger(stream)` hold inputs of up to 64 MB in memory and spool larger ones to a temporary file. The threshold can be changed before loading:
   ```csharp
   AppContext.SetData("PdfiumWrapper.SpoolThreshold", 16L * 1024 * 1024); // bytes
   ```

### Memory Leak

**Symptom:** Memory usage grows continuously

**Common Causes:**

1. **Not disposing PdfDocument**
   ```csharp
   // ❌ Memory leak
   var doc = new PdfDocument("file.pdf");
   // doc never disposed
   
   // ✅ Proper disposal
   using var doc = new PdfDocument("file.pdf");
   ```

2. **Not disposing PdfPage**
   ```csharp
   // ❌ Memory leak
   var pages = document.GetAllPages();
   // pages never disposed
   
   // ✅ Dispose each page
   foreach (var page in pages)
       page.Dispose();
   ```

3. **Holding all rendered pages in memory**
   ```csharp
   // ❌ High memory usage: all bitmaps in memory at once
   var bitmaps = document.RenderPages(300);

   // ✅ Process pages one at a time instead
   for (int i = 0; i < document.PageCount; i++)
   {
       using var page = document.GetPage(i);
       // Render and process single page
   }
   ```

   Note: `RawBitmap` is a lightweight record and does not need disposal.

4. **Relying on finalizers**

   An undisposed document, page, merger or detached page object is not closed by its finalizer directly. The finalizer queues the native handles, and the next PdfiumWrapper operation on any thread closes them. In a process that stops using the library for a while, that memory stays allocated until the next operation. Dispose objects explicitly; to release queued handles on demand, call:
   ```csharp
   PdfiumRuntime.ReleasePending();
   ```

---

## Threading Issues

PDFium allows one native call per process at a time, across all documents. Before 2.0 the wrapper did not enforce this, so using two documents from two threads could crash or corrupt output even though each thread had its own document. Copying files, loading into byte arrays, or "one document per thread" did not fix that.

PdfiumWrapper 2.0 serializes native work itself through one process-wide gate (`PdfiumRuntime`). Using different documents or mergers from different threads is safe and needs no lock of your own.

### Random Crashes or Corruption

**Symptom:** Application crashes randomly or produces corrupted output

**Causes in 2.0:**

1. **One object used from two threads at once.** A `PdfDocument`, `PdfPage`, `PdfForm`, `PdfMerger` or page object must be used by one thread at a time.
   ```csharp
   // ❌ Unsupported: one document shared by parallel workers
   using var document = new PdfDocument("file.pdf");
   Parallel.For(0, document.PageCount, i =>
   {
       using var page = document.GetPage(i);
   });

   // ✅ Safe: each worker has its own document
   await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (file, ct) =>
   {
       using var doc = new PdfDocument(file);
       await doc.SaveAsTiffAsync(Path.ChangeExtension(file, ".tiff"), 200);
   });
   ```

   If one object really must be shared, guard that object with your own `SemaphoreSlim(1, 1)` or `lock`.

2. **A native abort on malformed input.** See [Process Aborts on a Damaged PDF](#process-aborts-on-a-damaged-pdf).

3. **An older package version.** Versions before 2.0 have no gate. Upgrade, or serialize every PdfiumWrapper call in the process behind one lock.

### AccessViolationException

**Symptom:** `AccessViolationException` in native code

**Causes:**
- One object used from two threads at once (see above)
- A damaged PDF that PDFium does not reject cleanly
- `PdfPageObject.GetMatrix()` on a version before 2.0: it used a wrong native signature and could crash. Fixed in 2.0.

Using a disposed document, page, form or page object does not reach native code; it throws `ObjectDisposedException`.

### Process Aborts on a Damaged PDF

**Symptom:** The whole process exits while opening or rendering a corrupt or hostile file

A native abort inside PDFium cannot be caught as a .NET exception; it ends the hosting process.

As a characterization, 25 deliberately damaged inputs were processed in a child process on win-x64 and linux-x64: each of the five test fixtures truncated at 25%, 50% and 75%, with 1% of its bytes flipped, and with its cross-reference data zeroed. Every input was either rejected with `InvalidOperationException` (PDFium error 3) or processed. None aborted the process.

That sample does not prove PDFium never aborts. Services that must survive hostile input should run conversions in a separate process, so a native abort takes down a worker and not the service.

### ObjectDisposedException After Disposing a Document

**Symptom:** `ObjectDisposedException` from a page, form or page object that was not disposed explicitly

**Cause:** A document owns its pages, the forms returned by `GetForm()`, and the page objects removed from its pages with `RemoveObject`. Disposing the document disposes all of them.

```csharp
PdfForm form;
using (var document = new PdfDocument("form.pdf"))
{
    form = document.GetForm()!;
}

form.GetAllFormFields(); // ObjectDisposedException: the document is gone
```

**Solution:** Keep the document alive for as long as anything obtained from it is in use.

### Save Throws IOException

**Symptom:** `SaveToStream` or `PdfMerger.Save(stream)` throws `IOException` (or another exception type from the destination stream), where earlier versions threw `InvalidOperationException("Failed to save...")`

**Cause:** In 2.0 the PDF is serialized into memory first and written to the stream afterwards. An exception thrown by the stream now propagates unchanged.

**Solution:** Catch the stream's own exception types around save calls. `InvalidOperationException` is still thrown when PDFium itself fails to serialize the document.

### Thread-Pool Starvation or a Slow Async Service

**Symptom:** Unrelated requests stall while many PDF conversions are in flight

**Cause:** Synchronous methods, including constructors, block the calling thread while they wait for the gate. Calling them from many thread-pool threads at once leaves no threads for other work. With 192 concurrent conversions on a pool pinned to 24 threads, a heartbeat work item waited 1.6 ms (p99) when the conversions used the async API and about 2.5 s when they called the synchronous API from pool threads.

**Solution:** Use the async methods (`SaveAsTiffAsync`, `RenderPagesAsync`, `StreamImageBytesAsync`, `SaveAsPngsAsync`, `SaveAsJpegsAsync`, `SaveAsImagesAsync`, `ProcessAllPagesAsync`). They wait for the gate without blocking a thread. Also bound how many conversions run at once.

The async methods run their page work on the thread pool and never post to the caller's `SynchronizationContext`. So blocking on one from a WinForms, WPF or classic ASP.NET thread (`.Result`, `.Wait()`) does not deadlock, although it still blocks that thread until the work is done.

```csharp
using var document = new PdfDocument(path);
await document.SaveAsTiffAsync(outputPath, 200);
```

### PdfiumRuntime.Shutdown Throws

**Symptom:** `InvalidOperationException: Cannot shut down PDFium: N live handles, M pending releases.`

**Cause:** `Shutdown()` destroys the native library and refuses to do so while any wrapper object is alive.

**Solution:** Dispose every document, page, form, merger and detached page object first. `PdfiumRuntime.LiveHandleCount` shows how many native handles are still open. Objects that were dropped without `Dispose()` are released once the garbage collector has finalized them and `PdfiumRuntime.ReleasePending()` (or any other operation) has run. `Shutdown()` is meant for tests and controlled host shutdown; the library initializes again on next use.

---

## Platform-Specific Issues

### Windows

**Issue:** DLL not found on Windows Server

**Solution:** Install Visual C++ Redistributable:
```
https://aka.ms/vs/17/release/vc_redist.x64.exe
```

### Linux

**Issue:** Font rendering issues

**Solution:** Install font packages:
```bash
# Ubuntu/Debian
sudo apt-get install fonts-liberation fonts-dejavu-core fontconfig

# Update font cache
fc-cache -f -v
```

### macOS

**Issue:** Library not signed (Gatekeeper)

**Solution:**
```bash
# Remove quarantine attribute
xattr -d com.apple.quarantine /path/to/libpdfium.dylib
```

**Issue:** ARM64 vs x64 mismatch on Apple Silicon

**Solution:** Ensure correct runtime identifier:
```xml
<RuntimeIdentifier>osx-arm64</RuntimeIdentifier>
```

### Docker

**Recommended Dockerfile for .NET 10:**

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app

# Install dependencies for PDFium
RUN apt-get update && apt-get install -y \
    libfontconfig1 \
    libfreetype6 \
    fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "YourApp.dll"]
```

---

## Error Codes

PDFium returns error codes that can help diagnose issues. The error code is included in exception messages.

| Error Code | Meaning | Common Causes |
|------------|---------|---------------|
| 0 | Success | N/A |
| 1 | Unknown error | Corrupted PDF, internal error |
| 2 | File not found | File path incorrect |
| 3 | Invalid format | Not a valid PDF file |
| 4 | Password required | PDF is encrypted |
| 5 | Unsupported security | Encryption method not supported |
| 6 | Page not found | Invalid page index |

### Checking Error Codes

```csharp
try
{
    using var document = new PdfDocument("file.pdf");
}
catch (InvalidOperationException ex)
{
    // Error code is in the message
    Console.WriteLine(ex.Message);
    // Output: "Failed to load PDF document. Error: 4"
    // Error 4 = Password required
}
```

---

## Getting Help

If you encounter an issue not covered here:

1. **Check the GitHub Issues** for similar problems
2. **Create a minimal reproduction** of the issue
3. **Include relevant information:**
   - .NET version
   - Operating system
   - PDF characteristics (size, encrypted, form-based)
   - Full exception message and stack trace
4. **Open an issue** at the GitHub repository

When reporting issues, please include:
```csharp
Console.WriteLine($"OS: {Environment.OSVersion}");
Console.WriteLine($".NET: {Environment.Version}");
Console.WriteLine($"64-bit: {Environment.Is64BitProcess}");
```

