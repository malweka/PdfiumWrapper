using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PdfiumWrapper;

/// <summary>
/// Shared native library resolver for the assembly.
/// Handles platform-specific resolution of all bundled native libraries (pdfium, libtiff, etc.).
/// Only one DllImportResolver can be registered per assembly — this class centralizes that.
/// </summary>
internal static class NativeLibraryResolver
{
    static NativeLibraryResolver()
    {
        // The CLR guarantees no thread observes this type before the static constructor completes,
        // so no P/Invoke can be reached before the resolver is registered.
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
    }

    internal static void EnsureRegistered()
        => RuntimeHelpers.RunClassConstructor(typeof(NativeLibraryResolver).TypeHandle);

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        var names = GetLibraryNames(libraryName);
        if (names.fileName == null)
            return IntPtr.Zero;

        string baseDir = AppContext.BaseDirectory;
        string rid = GetRuntimeIdentifier();

        // Primary: libs/{rid}/{name} (development / source layout)
        string devPath = Path.Combine(baseDir, "libs", rid, names.fileName);
        if (File.Exists(devPath) && NativeLibrary.TryLoad(devPath, out var h1))
            return h1;

        // Secondary: runtimes/{rid}/native/{name} (NuGet / published layout)
        string runtimesPath = Path.Combine(baseDir, "runtimes", rid, "native", names.fileName);
        if (File.Exists(runtimesPath) && NativeLibrary.TryLoad(runtimesPath, out var h2))
            return h2;

        // Fallback: standard platform resolution (system-installed libraries)
        if (names.platformName != null &&
            NativeLibrary.TryLoad(names.platformName, assembly, searchPath, out var h3))
            return h3;

        return IntPtr.Zero;
    }

    private static (string? fileName, string? platformName) GetLibraryNames(string libraryName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return libraryName switch
            {
                "pdfium" => ("pdfium.dll", "pdfium"),
                "tiff" => ("tiff.dll", "tiff"),
                "tiff_shim" => ("tiff_shim.dll", "tiff_shim"),
                "turbojpeg" => ("turbojpeg.dll", "turbojpeg"),
                "pdfium_png" => ("pdfium_png.dll", "pdfium_png"),
                _ => (null, null)
            };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return libraryName switch
            {
                "pdfium" => ("libpdfium.so", "libpdfium"),
                "tiff" => ("libtiff.so", "libtiff"),
                "tiff_shim" => ("libtiff_shim.so", "libtiff_shim"),
                "turbojpeg" => ("libturbojpeg.so", "libturbojpeg"),
                "pdfium_png" => ("libpdfium_png.so", "libpdfium_png"),
                _ => (null, null)
            };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return libraryName switch
            {
                "pdfium" => ("libpdfium.dylib", "libpdfium"),
                "tiff" => ("libtiff.dylib", "libtiff"),
                "tiff_shim" => ("libtiff_shim.dylib", "libtiff_shim"),
                "turbojpeg" => ("libturbojpeg.dylib", "libturbojpeg"),
                "pdfium_png" => ("libpdfium_png.dylib", "libpdfium_png"),
                _ => (null, null)
            };
        }

        return (null, null);
    }

    private static readonly Lazy<string> s_runtimeIdentifier = new(ComputeRuntimeIdentifier);

    /// <summary>
    /// The RID whose natives this process loads: <c>libs/{rid}</c> and <c>runtimes/{rid}/native</c>.
    /// On Linux it is <c>linux-musl-{arch}</c> on a musl libc (Alpine), whose loader cannot load the
    /// glibc builds, and <c>linux-{arch}</c> otherwise.
    /// </summary>
    internal static string GetRuntimeIdentifier() => s_runtimeIdentifier.Value;

    private static string ComputeRuntimeIdentifier()
    {
        string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return $"win-{arch}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return IsMusl() ? $"linux-musl-{arch}" : $"linux-{arch}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return $"osx-{arch}";

        throw new PlatformNotSupportedException("Unsupported platform");
    }

    /// <summary>
    /// True on a musl libc. The runtime's own RID says so on Alpine (<c>linux-musl-x64</c>); the musl
    /// loader, <c>/lib/ld-musl-{arch}.so.1</c>, is checked too, so a host whose runtime reports a
    /// non-portable RID still resolves. <c>/etc/os-release</c> is not read: other distributions use
    /// musl without being Alpine.
    /// </summary>
    private static bool IsMusl()
    {
        if (RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            return Directory.Exists("/lib") && Directory.EnumerateFiles("/lib", "ld-musl-*.so.1").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
