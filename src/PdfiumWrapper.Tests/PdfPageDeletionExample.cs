using PdfiumWrapper;

namespace PdfiumWrapper.Tests;

/// <summary>
/// Example demonstrating page deletion functionality
/// </summary>
public class PdfPageDeletionExample
{
    /// <summary>
    /// Example: Delete pages by index
    /// </summary>
    public static void DeletePagesByIndex(string inputPath, string outputPath, params int[] pageIndicesToDelete)
    {
        using var document = new PdfDocument(inputPath);
        
        Console.WriteLine($"Original page count: {document.PageCount}");
        
        // Sort in descending order to avoid index shifting issues
        var sortedIndices = pageIndicesToDelete.OrderByDescending(x => x).ToArray();
        
        foreach (var pageIndex in sortedIndices)
        {
            if (pageIndex >= 0 && pageIndex < document.PageCount)
            {
                Console.WriteLine($"Deleting page {pageIndex}...");
                document.DeletePage(pageIndex);
            }
        }
        
        Console.WriteLine($"New page count: {document.PageCount}");
        
        document.Save(outputPath);
        Console.WriteLine($"Saved to: {outputPath}");
    }

    /// <summary>
    /// Example: Delete pages using PdfPage objects
    /// </summary>
    public static void DeletePagesUsingPageObjects(string inputPath, string outputPath)
    {
        using var document = new PdfDocument(inputPath);
        
        Console.WriteLine($"Original page count: {document.PageCount}");
        
        // Get the first page and delete it
        using (var firstPage = document.GetPage(0))
        {
            Console.WriteLine($"Deleting first page (index {firstPage.PageIndex})...");
            document.DeletePage(firstPage);
        }
        
        // Get the last page (after deletion, indices have shifted)
        if (document.PageCount > 0)
        {
            using (var lastPage = document.GetPage(document.PageCount - 1))
            {
                Console.WriteLine($"Deleting last page (index {lastPage.PageIndex})...");
                document.DeletePage(lastPage);
            }
        }
        
        Console.WriteLine($"New page count: {document.PageCount}");
        
        document.Save(outputPath);
        Console.WriteLine($"Saved to: {outputPath}");
    }

    /// <summary>
    /// Example: Remove all pages except the first one
    /// </summary>
    public static void KeepOnlyFirstPage(string inputPath, string outputPath)
    {
        using var document = new PdfDocument(inputPath);
        
        Console.WriteLine($"Original page count: {document.PageCount}");
        
        // Delete all pages from the end, keeping only the first page
        while (document.PageCount > 1)
        {
            document.DeletePage(document.PageCount - 1);
        }
        
        Console.WriteLine($"New page count: {document.PageCount}");
        
        document.Save(outputPath);
        Console.WriteLine($"Saved to: {outputPath}");
    }

    /// <summary>
    /// Example: Extract specific pages by deleting others
    /// </summary>
    public static void ExtractPages(string inputPath, string outputPath, params int[] pagesToKeep)
    {
        using var document = new PdfDocument(inputPath);
        
        Console.WriteLine($"Original page count: {document.PageCount}");
        
        var keepSet = new HashSet<int>(pagesToKeep);
        
        // Delete pages in reverse order to avoid index shifting
        for (int i = document.PageCount - 1; i >= 0; i--)
        {
            if (!keepSet.Contains(i))
            {
                Console.WriteLine($"Deleting page {i}...");
                document.DeletePage(i);
            }
        }
        
        Console.WriteLine($"New page count: {document.PageCount}");
        
        document.Save(outputPath);
        Console.WriteLine($"Saved to: {outputPath}");
    }

    /// <summary>
    /// Example: Create a document and then delete some pages
    /// </summary>
    public static void CreateAndDeletePages(string outputPath)
    {
        using var document = new PdfDocument();
        
        // Add 5 pages
        for (int i = 0; i < 5; i++)
        {
            using var page = document.AddPage();
            var text = page.AddText($"Page {i + 1}", x: 100, y: 700);
            text.Color = System.Drawing.Color.Black;
            page.GenerateContent();
        }
        
        Console.WriteLine($"Created {document.PageCount} pages");
        
        // Delete pages 1 and 3 (indices 1 and 3)
        document.DeletePage(3); // Delete page 4 first
        document.DeletePage(1); // Then delete page 2
        
        Console.WriteLine($"After deletion: {document.PageCount} pages remain");
        
        document.Save(outputPath);
        Console.WriteLine($"Saved to: {outputPath}");
    }

    /// <summary>
    /// Example: Safely delete pages with error handling
    /// </summary>
    public static void SafeDeletePages(string inputPath, string outputPath, params int[] pageIndicesToDelete)
    {
        using var document = new PdfDocument(inputPath);
        
        Console.WriteLine($"Original page count: {document.PageCount}");
        
        // Sort in descending order
        var sortedIndices = pageIndicesToDelete.OrderByDescending(x => x).ToArray();
        
        foreach (var pageIndex in sortedIndices)
        {
            try
            {
                document.DeletePage(pageIndex);
                Console.WriteLine($"✓ Deleted page {pageIndex}");
            }
            catch (ArgumentOutOfRangeException ex)
            {
                Console.WriteLine($"✗ Could not delete page {pageIndex}: {ex.Message}");
            }
        }
        
        Console.WriteLine($"Final page count: {document.PageCount}");
        
        document.Save(outputPath);
        Console.WriteLine($"Saved to: {outputPath}");
    }
}

