using System.Runtime.CompilerServices;

namespace PdfiumWrapper.Tests;

public static class Bootstrapper
{
    public const string TestFilesDirectory = "Docs";
    public const string WorkingDirectory = "TestOutput";
    
    [ModuleInitializer]
    public static void Initialize()
    {
        if (Directory.Exists(WorkingDirectory))
        {
            Directory.Delete(WorkingDirectory, recursive: true);
        }
        Directory.CreateDirectory(WorkingDirectory);
    }
}