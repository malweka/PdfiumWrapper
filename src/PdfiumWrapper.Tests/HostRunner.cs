using System.Diagnostics;
using System.Text.Json;

namespace PdfiumWrapper.Tests;

internal sealed record HostResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>The single JSON object the host writes on stdout.</summary>
    public JsonElement Json
    {
        get
        {
            var line = StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(l => l.StartsWith('{'));
            Assert.True(line != null, $"Host wrote no JSON. Exit {ExitCode}. stdout: {StandardOutput} stderr: {StandardError}");
            return JsonDocument.Parse(line!).RootElement;
        }
    }

    public override string ToString() => $"exit {ExitCode}; stdout: {StandardOutput}; stderr: {StandardError}";
}

/// <summary>
/// Runs <c>PdfiumWrapper.Tests.Host</c> scenarios in a child process. Used for tests that change
/// process-global state, need a fresh process, or may abort natively.
/// </summary>
internal static class HostRunner
{
    private const string HostAssemblyName = "PdfiumWrapper.Tests.Host";

    /// <summary>
    /// The host builds next to this project; its output mirrors ours (same configuration and TFM).
    /// </summary>
    public static string HostPath
    {
        get
        {
            var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var tfmDir = new DirectoryInfo(baseDir);
            var configurationDir = tfmDir.Parent!;
            var projectDir = configurationDir.Parent!.Parent!;
            var path = Path.Combine(projectDir.Parent!.FullName, HostAssemblyName, "bin",
                configurationDir.Name, tfmDir.Name, HostAssemblyName + ".dll");
            Assert.True(File.Exists(path), $"Test host not found at '{path}'. Build {HostAssemblyName} first.");
            return path;
        }
    }

    public static string Input(string fileName)
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Bootstrapper.TestFilesDirectory, fileName));

    public static HostResult Run(string scenario, TimeSpan timeout, params string[] keyValues)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(HostPath)!,
        };
        psi.ArgumentList.Add(HostPath);
        psi.ArgumentList.Add(scenario);
        foreach (var keyValue in keyValues)
            psi.ArgumentList.Add(keyValue);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Host scenario '{scenario}' did not finish within {timeout}.");
        }

        process.WaitForExit();
        return new HostResult(process.ExitCode, stdout.Result, stderr.Result);
    }
}
