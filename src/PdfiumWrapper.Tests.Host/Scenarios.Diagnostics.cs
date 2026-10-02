namespace PdfiumWrapper.Tests.Host;

internal static partial class Scenarios
{
    /// <summary>Number of times the native library was initialized in this process.</summary>
    private static long InitCount() => PdfiumDiagnostics.Snapshot().InitCount;
}
