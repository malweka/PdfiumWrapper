using System.Runtime.InteropServices;

namespace PdfiumWrapper.Tests.Host;

internal static partial class WindowsErrorMode
{
    private const uint SEM_FAILCRITICALERRORS = 0x0001;
    private const uint SEM_NOGPFAULTERRORBOX = 0x0002;

    [LibraryImport("kernel32.dll")]
    private static partial uint SetErrorMode(uint mode);

    /// <summary>
    /// A deliberate crash probe must end quietly instead of raising the Windows fault dialog.
    /// </summary>
    public static void SuppressFaultDialogs()
    {
        if (OperatingSystem.IsWindows())
            SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
    }
}
