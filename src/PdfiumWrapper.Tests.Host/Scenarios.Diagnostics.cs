namespace PdfiumWrapper.Tests.Host;

internal static partial class Scenarios
{
    /// <summary>
    /// Number of times the native library was initialized in this process.
    /// Reported as -1 until the runtime diagnostics exist (added with PdfiumRuntime).
    /// </summary>
    private static int InitCount() => -1;
}
