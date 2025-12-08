using Malweka.PdfiumSdk;

// Quick test to verify PdfForm dispose doesn't cause stack overflow
var testPdfPath = "Malweka.PdfiumSdk.Tests/Docs/contract.pdf";

Console.WriteLine("Testing PdfForm fix...");
using (var doc = new PdfDocument(testPdfPath))
{
    Console.WriteLine($"Loaded document with {doc.PageCount} pages");
    
    var form = doc.GetForm();
    if (form == null)
    {
        Console.WriteLine("No forms found in document");
    }
    else
    {
        Console.WriteLine("Found forms in document");
        form.Dispose();
        Console.WriteLine("Form disposed successfully");
    }
}

Console.WriteLine("Test completed successfully - no stack overflow!");

