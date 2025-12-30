using System.Reflection;
using System.Runtime.InteropServices;

namespace PdfiumWrapper;

/// <summary>
/// C# wrapper for PDFium library using LibraryImport with cross-platform support
/// </summary>
public static partial class PDFium
{
    private const string LibraryName = "pdfium";

    static PDFium()
    {
        // Set up custom library resolver for cross-platform loading
        NativeLibrary.SetDllImportResolver(typeof(PDFium).Assembly, DllImportResolver);
    }

    private static IntPtr DllImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName == "pdfium")
        {
            // Try to load the platform-specific library
            string actualLibraryPath = GetNativeLibraryPath();
            
            if (File.Exists(actualLibraryPath))
            {
                return NativeLibrary.Load(actualLibraryPath);
            }
            
            // Fallback: try standard library loading with correct name
            string platformLibraryName = GetPlatformLibraryName();
            if (NativeLibrary.TryLoad(platformLibraryName, assembly, searchPath, out IntPtr handle))
            {
                return handle;
            }
        }
        
        // Default behavior
        return IntPtr.Zero;
    }

    private static string GetPlatformLibraryName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "pdfium";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "libpdfium";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "libpdfium";
        else
            return "pdfium";
    }

    private static string GetNativeLibraryPath()
    {
        string baseDir = AppContext.BaseDirectory;
        string architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string rid = $"win-{architecture}";
            return Path.Combine(baseDir, "libs", rid, "pdfium.dll");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            string rid = $"linux-{architecture}";
            return Path.Combine(baseDir, "libs", rid, "libpdfium.so");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            string rid = $"osx-{architecture}";
            return Path.Combine(baseDir, "libs", rid, "libpdfium.dylib");
        }
        
        throw new PlatformNotSupportedException("Unsupported platform");
    }

    #region Library Management

    [LibraryImport(LibraryName)]
    public static partial void FPDF_InitLibrary();

    [LibraryImport(LibraryName)]
    public static partial void FPDF_DestroyLibrary();

    [LibraryImport(LibraryName)]
    public static partial void FPDF_SetSandBoxPolicy(uint policy, int enable);

    #endregion

    #region Document Management

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr FPDF_LoadDocument(string file_path, string? password);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr FPDF_LoadMemDocument(IntPtr data_buf, int size, string? password);

    [LibraryImport(LibraryName)]
    public static partial void FPDF_CloseDocument(IntPtr document);

    [LibraryImport(LibraryName)]
    public static partial int FPDF_GetPageCount(IntPtr document);

    [LibraryImport(LibraryName)]
    public static partial uint FPDF_GetDocPermissions(IntPtr document);

    [LibraryImport(LibraryName)]
    public static partial int FPDF_GetSecurityHandlerRevision(IntPtr document);

    #endregion

    #region Page Management

    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDF_LoadPage(IntPtr document, int page_index);

    [LibraryImport(LibraryName)]
    public static partial void FPDF_ClosePage(IntPtr page);

    [LibraryImport(LibraryName)]
    public static partial double FPDF_GetPageWidth(IntPtr page);

    [LibraryImport(LibraryName)]
    public static partial double FPDF_GetPageHeight(IntPtr page);

    [LibraryImport(LibraryName)]
    public static partial int FPDF_GetPageSizeByIndex(IntPtr document, int page_index, out double width, out double height);

    #endregion

    #region Rendering

    [LibraryImport(LibraryName)]
    public static partial void FPDF_RenderPage(IntPtr dc, IntPtr page, int start_x, int start_y,
        int size_x, int size_y, int rotate, int flags);

    [LibraryImport(LibraryName)]
    public static partial void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int start_x, int start_y,
        int size_x, int size_y, int rotate, int flags);

    [LibraryImport(LibraryName)]
    public static partial void FPDF_RenderPageBitmapWithMatrix(IntPtr bitmap, IntPtr page,
        ref Matrix matrix, ref RectF clipping, int flags);

    #endregion

    #region Bitmap Management

    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFBitmap_Create(int width, int height, int alpha);

    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFBitmap_CreateEx(int width, int height, int format,
        IntPtr first_scan, int stride);

    [LibraryImport(LibraryName)]
    public static partial void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top,
        int width, int height, uint color);

    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);

    [LibraryImport(LibraryName)]
    public static partial int FPDFBitmap_GetWidth(IntPtr bitmap);

    [LibraryImport(LibraryName)]
    public static partial int FPDFBitmap_GetHeight(IntPtr bitmap);

    [LibraryImport(LibraryName)]
    public static partial int FPDFBitmap_GetStride(IntPtr bitmap);

    [LibraryImport(LibraryName)]
    public static partial void FPDFBitmap_Destroy(IntPtr bitmap);

    #endregion

    #region Text Extraction

    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFText_LoadPage(IntPtr page);

    [LibraryImport(LibraryName)]
    public static partial void FPDFText_ClosePage(IntPtr text_page);

    [LibraryImport(LibraryName)]
    public static partial int FPDFText_CountChars(IntPtr text_page);

    [LibraryImport(LibraryName)]
    public static partial uint FPDFText_GetUnicode(IntPtr text_page, int index);

    [LibraryImport(LibraryName)]
    public static partial int FPDFText_GetText(IntPtr text_page, int start_index, int count, IntPtr result);

    #endregion

    #region Error Handling

    [LibraryImport(LibraryName)]
    public static partial uint FPDF_GetLastError();

    #endregion

    #region Helper Structures

    [StructLayout(LayoutKind.Sequential)]
    public struct Matrix
    {
        public float A;
        public float B;
        public float C;
        public float D;
        public float E;
        public float F;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RectF
    {
        public float Left;
        public float Top;
        public float Right;
        public float Bottom;
    }

    #endregion

    #region Constants

    public const int FPDF_ERR_SUCCESS = 0;    // No error
    public const int FPDF_ERR_UNKNOWN = 1;    // Unknown error
    public const int FPDF_ERR_FILE = 2;       // File not found or could not be opened
    public const int FPDF_ERR_FORMAT = 3;     // File not in PDF format or corrupted
    public const int FPDF_ERR_PASSWORD = 4;   // Password required or incorrect password
    public const int FPDF_ERR_SECURITY = 5;   // Unsupported security scheme
    public const int FPDF_ERR_PAGE = 6;       // Page not found or content error

    // Render flags
    public const int FPDF_ANNOT = 0x01;       // Set if annotations are to be rendered
    public const int FPDF_LCD_TEXT = 0x02;    // Set if using text rendering optimized for LCD display
    public const int FPDF_NO_NATIVETEXT = 0x04; // Don't use the native text output available on some platforms
    public const int FPDF_GRAYSCALE = 0x08;   // Grayscale output
    public const int FPDF_DEBUG_INFO = 0x80;  // Set if you want to get some debug info
    public const int FPDF_NO_CATCH = 0x100;   // Set if you don't want to catch exceptions
    public const int FPDF_RENDER_LIMITEDIMAGECACHE = 0x200; // Limit image cache size
    public const int FPDF_RENDER_FORCEHALFTONE = 0x400; // Always use halftone for image stretching
    public const int FPDF_PRINTING = 0x800;   // Set if rendering for printing
    public const int FPDF_RENDER_NO_SMOOTHTEXT = 0x1000; // Set if you don't want to smooth text
    public const int FPDF_RENDER_NO_SMOOTHIMAGE = 0x2000; // Set if you don't want to smooth images
    public const int FPDF_RENDER_NO_SMOOTHPATH = 0x4000; // Set if you don't want to smooth paths

    // Bitmap formats
    public const int FPDFBitmap_Unknown = 0;
    public const int FPDFBitmap_Gray = 1;
    public const int FPDFBitmap_BGR = 2;
    public const int FPDFBitmap_BGRx = 3;
    public const int FPDFBitmap_BGRA = 4;

    #endregion
}