using System.Runtime.CompilerServices;
using Malweka.PdfiumSdk;

public static class TestInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        PDFium.FPDF_InitLibrary();
    }
}