using System.Diagnostics;

namespace PdfiumWrapper.Benchmarks.Comparison;

/// <summary>
/// Ghostscript, driven the way a service would drive it: one console process per operation.
/// Every measurement therefore includes process startup, which <see cref="StartupBenchmark"/>
/// reports on its own.
/// </summary>
internal static class Ghostscript
{
    public const string PathVariable = "GHOSTSCRIPT_EXE";

    private static readonly Lazy<string?> s_executable = new(Locate);

    public static string? Executable => s_executable.Value;

    public static bool IsAvailable => Executable != null;

    private static string? Locate()
    {
        var configured = Environment.GetEnvironmentVariable(PathVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? configured : null;

        string[] names = OperatingSystem.IsWindows() ? new[] { "gswin64c.exe", "gswin32c.exe" } : new[] { "gs" };
        var searchPath = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var directory in searchPath)
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            // The installer does not always add Ghostscript to PATH.
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "gs");
            if (Directory.Exists(root))
            {
                return Directory.GetDirectories(root)
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .Select(d => Path.Combine(d, "bin", "gswin64c.exe"))
                    .FirstOrDefault(File.Exists);
            }
        }

        return null;
    }

    /// <summary>Runs Ghostscript to completion and returns its standard output.</summary>
    public static string Run(params string[] arguments)
    {
        var psi = new ProcessStartInfo(Executable ?? throw new InvalidOperationException("Ghostscript was not found."))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)!;
        s_live[process.Id] = process;
        try
        {
            var stderr = process.StandardError.ReadToEndAsync();
            string stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            try
            {
                Interlocked.Add(ref s_childCpuTicks, process.TotalProcessorTime.Ticks);
            }
            catch (InvalidOperationException)
            {
            }

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Ghostscript exited with {process.ExitCode}: {stderr.Result}{stdout}");
            return stdout;
        }
        finally
        {
            s_live.TryRemove(process.Id, out _);
        }
    }

    private static long s_childCpuTicks;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Process> s_live = new();

    /// <summary>Processor time used by all Ghostscript processes run so far, in ticks.</summary>
    public static long ChildCpuTicks => Interlocked.Read(ref s_childCpuTicks);

    /// <summary>Working set of the Ghostscript processes running right now.</summary>
    public static long LiveWorkingSetBytes()
    {
        long total = 0;
        foreach (var process in s_live.Values)
        {
            try
            {
                process.Refresh();
                total += process.WorkingSet64;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The process exited between the snapshot and the read.
            }
        }

        return total;
    }

    public static string Version() => Run("--version").Trim();

    /// <summary>A file name as a PostScript string literal.</summary>
    public static string PostScriptString(string path)
        => "(" + path.Replace('\\', '/').Replace("(", "\\(").Replace(")", "\\)") + ")";
}

/// <summary>
/// Aspose.PDF for .NET. Without a license it runs in evaluation mode, which limits the pages it
/// processes and watermarks its output, so its benchmarks are skipped unless a license is supplied.
/// </summary>
internal static class AsposeEngine
{
    public const string LicenseVariable = "ASPOSE_PDF_LICENSE";

    private static readonly object s_lock = new();
    private static bool s_licensed;

    /// <summary>The license file named by the environment. It stays where it is; nothing is copied.</summary>
    public static string? LicensePath
    {
        get
        {
            var path = Environment.GetEnvironmentVariable(LicenseVariable);
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
        }
    }

    public static bool IsAvailable => LicensePath != null;

    public static void EnsureLicensed()
    {
        lock (s_lock)
        {
            if (s_licensed)
                return;

            new Aspose.Pdf.License().SetLicense(
                LicensePath ?? throw new InvalidOperationException($"Set {LicenseVariable} to an Aspose.PDF license file."));
            s_licensed = true;
        }
    }

    public static string Version() => typeof(Aspose.Pdf.Document).Assembly.GetName().Version?.ToString() ?? "unknown";
}

/// <summary>
/// Where this project writes results. Figures for other engines are for local reading only, so
/// they go under the repository's git-ignored <c>ai/tmp</c> folder, never next to PdfiumWrapper's
/// own benchmark artifacts.
/// </summary>
internal static class PrivateOutput
{
    public static string Directory(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
            directory = directory.Parent;

        string root = directory != null
            ? Path.Combine(directory.FullName, "ai", "tmp")
            : Path.Combine(Path.GetTempPath(), "PdfiumComparison");
        return Path.Combine(root, name);
    }
}