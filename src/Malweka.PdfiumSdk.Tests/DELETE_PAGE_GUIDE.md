# DeletePage Methods Documentation

## Overview

The `PdfDocument` class now supports deleting pages from PDF documents with two convenient overloads:

1. `DeletePage(int pageIndex)` - Delete by page index
2. `DeletePage(PdfPage page)` - Delete using a PdfPage object

## Method Signatures

```csharp
/// <summary>
/// Delete a page from the document by index
/// </summary>
/// <param name="pageIndex">The 0-based index of the page to delete</param>
/// <exception cref="ArgumentOutOfRangeException">Thrown when pageIndex is out of range</exception>
public void DeletePage(int pageIndex)

/// <summary>
/// Delete a page from the document
/// </summary>
/// <param name="page">The page to delete</param>
/// <exception cref="ArgumentNullException">Thrown when page is null</exception>
public void DeletePage(PdfPage page)
```

## Usage Examples

### Delete by Index

```csharp
using var document = new PdfDocument("input.pdf");

// Delete the first page (index 0)
document.DeletePage(0);

// Delete the third page (index 2)
document.DeletePage(2);

document.Save("output.pdf");
```

### Delete Using PdfPage Object

```csharp
using var document = new PdfDocument("input.pdf");

// Get a page and delete it
using (var page = document.GetPage(1))
{
    document.DeletePage(page);
}

document.Save("output.pdf");
```

### Delete Multiple Pages

When deleting multiple pages, **always delete in reverse order** to avoid index shifting issues:

```csharp
using var document = new PdfDocument("input.pdf");

// Delete pages 1, 3, and 5 (indices 1, 3, 5)
int[] pagesToDelete = { 1, 3, 5 };

// Sort in descending order
foreach (var index in pagesToDelete.OrderByDescending(x => x))
{
    document.DeletePage(index);
}

document.Save("output.pdf");
```

### Keep Only Specific Pages

```csharp
using var document = new PdfDocument("input.pdf");

// Keep only pages 0, 2, 4
var pagesToKeep = new HashSet<int> { 0, 2, 4 };

// Delete in reverse to avoid index shifting
for (int i = document.PageCount - 1; i >= 0; i--)
{
    if (!pagesToKeep.Contains(i))
    {
        document.DeletePage(i);
    }
}

document.Save("output.pdf");
```

### Safe Deletion with Error Handling

```csharp
using var document = new PdfDocument("input.pdf");

try
{
    document.DeletePage(5);
    Console.WriteLine("Page deleted successfully");
}
catch (ArgumentOutOfRangeException ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}

document.Save("output.pdf");
```

### Create and Delete Pages

```csharp
// Create a new document
using var document = new PdfDocument();

// Add 5 pages
for (int i = 0; i < 5; i++)
{
    using var page = document.AddPage();
    var text = page.AddText($"Page {i + 1}", x: 100, y: 700);
    page.GenerateContent();
}

// Delete pages 1 and 3 (indices 1 and 3)
// Delete higher index first!
document.DeletePage(3);
document.DeletePage(1);

// Now we have pages: 0, 2, 4 (which were originally 1, 3, 5)

document.Save("output.pdf");
```

## Important Notes

1. **Page indices are 0-based**: First page is index 0, second page is index 1, etc.

2. **Index shifting**: When you delete a page, all subsequent pages shift down by one index. Always delete from highest to lowest index when deleting multiple pages.

3. **Validation**: Both methods validate inputs and throw appropriate exceptions:
   - `ArgumentOutOfRangeException` if the index is invalid
   - `ArgumentNullException` if the page object is null
   - `ObjectDisposedException` if the document has been disposed

4. **Page disposal**: After deleting a page, any PdfPage objects referencing that page or pages after it will have invalid indices.

5. **Save changes**: Remember to call `document.Save()` to persist the changes to disk.

## Common Patterns

### Remove First Page
```csharp
if (document.PageCount > 0)
    document.DeletePage(0);
```

### Remove Last Page
```csharp
if (document.PageCount > 0)
    document.DeletePage(document.PageCount - 1);
```

### Keep Only First Page
```csharp
while (document.PageCount > 1)
    document.DeletePage(document.PageCount - 1);
```

### Remove All Pages Except Specific Ones
```csharp
var keepIndices = new HashSet<int> { 0, 5, 10 };
for (int i = document.PageCount - 1; i >= 0; i--)
{
    if (!keepIndices.Contains(i))
        document.DeletePage(i);
}
```

## See Also

- `PdfPageDeletionExample.cs` - Complete working examples
- `AddPage()` method - For adding new pages
- PDFium documentation - For underlying FPDFPage_Delete function

