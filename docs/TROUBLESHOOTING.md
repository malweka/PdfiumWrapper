# Troubleshooting

This guide covers common issues and their solutions when using Malweka.PdfiumSdk.

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

### SkiaSharp Issues

**Symptom:** `TypeInitializationException` related to SkiaSharp

**Solutions:**

1. Ensure SkiaSharp native assets are present:
   ```bash
   dotnet restore
   ```

2. For Linux, install required dependencies:
   ```bash
   sudo apt-get install libfontconfig1-dev
   ```

3. For Alpine Linux (Docker):
   ```dockerfile
   RUN apk add --no-cache \
       fontconfig \
       freetype \
       libstdc++
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
   // A 8.5x11 page at 300 DPI = ~8.4 MB per page (uncompressed)
   // At 150 DPI = ~2.1 MB per page
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
   var bitmaps = document.ConvertToBitmaps(300);
   
   // ✅ Process and dispose one at a time
   for (int i = 0; i < document.PageCount; i++)
   {
       using var page = document.GetPage(i);
       // Render, save, dispose immediately
   }
   ```

4. **Force garbage collection for large batches**
   ```csharp
   foreach (var pdfFile in largePdfList)
   {
       using (var doc = new PdfDocument(pdfFile))
       {
           // Process...
       }
       
       // Suggest GC after each large document
       if (i % 10 == 0)
       {
           GC.Collect();
           GC.WaitForPendingFinalizers();
       }
   }
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

3. **Not disposing SKBitmap**
   ```csharp
   // ❌ Memory leak
   var bitmaps = document.ConvertToBitmaps(300);
   // bitmaps never disposed
   
   // ✅ Dispose each bitmap
   foreach (var bitmap in bitmaps)
       bitmap.Dispose();
   ```

---

## Threading Issues

### Random Crashes or Corruption

**Symptom:** Application crashes randomly or produces corrupted output

**Cause:** Accessing PDFium from multiple threads simultaneously

**Solutions:**

1. **One document per thread**
   ```csharp
   // ✅ Safe: Each thread has own document
   await Parallel.ForEachAsync(files, async (file, ct) =>
   {
       using var doc = new PdfDocument(file);
       // Process...
   });
   ```

2. **Serialize access with locks**
   ```csharp
   private readonly object _pdfLock = new object();
   
   public void ProcessPage(int index)
   {
       lock (_pdfLock)
       {
           using var page = _document.GetPage(index);
           // Process...
       }
   }
   ```

3. **Use SemaphoreSlim for async code**
   ```csharp
   private readonly SemaphoreSlim _semaphore = new(1, 1);
   
   public async Task ProcessPageAsync(int index)
   {
       await _semaphore.WaitAsync();
       try
       {
           using var page = _document.GetPage(index);
           // Process...
       }
       finally
       {
           _semaphore.Release();
       }
   }
   ```

### AccessViolationException

**Symptom:** `AccessViolationException` in native code

**Causes:**
- Using disposed document/page
- Concurrent access from multiple threads
- Corrupted PDF file

**Solutions:**
```csharp
// Check if disposed before use
if (_disposed)
    throw new ObjectDisposedException(nameof(PdfDocument));

// Don't access after dispose
using var document = new PdfDocument("file.pdf");
using var page = document.GetPage(0);
// After this block, both are disposed - don't access them!
```

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

**Issue:** `libSkiaSharp.so` not found

**Solution:**
```bash
# Ensure LD_LIBRARY_PATH includes the runtime directory
export LD_LIBRARY_PATH=$LD_LIBRARY_PATH:/app/runtimes/linux-x64/native
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

**Recommended Dockerfile for .NET 8:**

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app

# Install dependencies for PDFium and SkiaSharp
RUN apt-get update && apt-get install -y \
    libfontconfig1 \
    libfreetype6 \
    fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
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
