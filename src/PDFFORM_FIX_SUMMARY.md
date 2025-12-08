# PdfForm Stack Overflow Fix - Summary

## Problem
When calling `PDFium.FPDFDOC_ExitFormFillEnvironment(_formHandle)` in `PdfForm.Dispose()`, a **stack overflow exception** was occurring. The test would crash with this error when trying to dispose a PdfForm object.

## Root Cause
The issue was caused by **improper memory management** of the `FPDF_FORMFILLINFO` structure:

1. **Original Code Issue:**
   ```csharp
   private void InitializeFormEnvironment()
   {
       // This structure was a LOCAL VARIABLE - it goes out of scope!
       var formInfo = new PDFium.FPDF_FORMFILLINFO
       {
           version = 2
       };

       // PDFium stores a POINTER to this structure
       _formHandle = PDFium.FPDFDOC_InitFormFillEnvironment(_document, ref formInfo);
   }
   ```

2. **What Happened:**
   - The `formInfo` structure was created as a **local variable on the stack**
   - It was passed **by reference** (`ref`) to PDFium's `FPDFDOC_InitFormFillEnvironment`
   - PDFium **stored a pointer** to this structure for later use
   - When the method returned, the structure **went out of scope** and the memory was invalidated
   - Later, when `FPDFDOC_ExitFormFillEnvironment` was called, PDFium tried to access the structure
   - Accessing this invalid memory caused **undefined behavior** → **stack overflow**

## Solution
The fix involves three key changes:

### 1. Store the Structure as a Class Member
```csharp
private PDFium.FPDF_FORMFILLINFO _formInfo;
private GCHandle _formInfoHandle;
private bool _disposed;
```

### 2. Pin the Structure in Memory
```csharp
private void InitializeFormEnvironment()
{
    // Create the structure as a class member
    _formInfo = new PDFium.FPDF_FORMFILLINFO
    {
        version = 2
    };

    // PIN it in memory so it won't move or be garbage collected
    _formInfoHandle = GCHandle.Alloc(_formInfo, GCHandleType.Pinned);

    try
    {
        _formHandle = PDFium.FPDFDOC_InitFormFillEnvironment(_document, ref _formInfo);
        _formInitialized = _formHandle != IntPtr.Zero;
    }
    catch
    {
        // Clean up on error
        if (_formInfoHandle.IsAllocated)
        {
            _formInfoHandle.Free();
        }
        throw;
    }
}
```

### 3. Properly Dispose Everything
```csharp
public void Dispose()
{
    if (_disposed)
        return;

    // First, exit the form environment
    if (_formInitialized && _formHandle != IntPtr.Zero)
    {
        PDFium.FPDFDOC_ExitFormFillEnvironment(_formHandle);
        _formHandle = IntPtr.Zero;
        _formInitialized = false;
    }

    // Then, unpin the structure
    if (_formInfoHandle.IsAllocated)
    {
        _formInfoHandle.Free();
    }

    _disposed = true;
}
```

## Why This Works

1. **Lifetime Management:** The `_formInfo` structure now lives as long as the `PdfForm` object, not just for the duration of the `InitializeFormEnvironment` method.

2. **Memory Pinning:** Using `GCHandle.Alloc` with `GCHandleType.Pinned` ensures:
   - The structure won't be moved by the garbage collector
   - The memory address remains valid
   - PDFium can safely access it throughout the form's lifetime

3. **Proper Cleanup:** The `Dispose` method now:
   - Exits the form environment first (while the structure is still valid)
   - Then unpins and releases the GCHandle
   - Prevents double-disposal with the `_disposed` flag

## Key Concepts

**GCHandle.Pinned:** When working with unmanaged code (like PDFium), managed objects can be moved by the garbage collector. Pinning prevents this movement and guarantees a stable memory address.

**Structure Lifetime:** When passing managed structures to unmanaged code by reference, ensure the structure remains alive and at the same memory location for as long as the unmanaged code might access it.

## Testing
The fix should now allow tests like this to pass without stack overflow:

```csharp
using var doc = new PdfDocument("test.pdf");
var form = doc.GetForm();
if (form != null)
{
    form.Dispose(); // No more stack overflow!
}
```

## Related Files Modified
- `/Users/hamsman/Dev/PdfiumWrapper/src/Malweka.PdfiumSdk/PdfForm.cs`

