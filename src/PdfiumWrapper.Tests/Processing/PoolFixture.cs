using PdfiumWrapper.Processing;

namespace PdfiumWrapper.Tests.Processing;

/// <summary>Pool options for tests: the test host as the worker, short timeouts, a temp output root.</summary>
internal static class PoolFixture
{
    public static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(3);

    public static PdfPoolOptions Options(Action<PdfPoolOptions>? configure = null)
    {
        var options = new PdfPoolOptions
        {
            WorkerPath = HostRunner.HostPath,
            MinWorkers = 1,
            MaxWorkers = 2,
            JobsPerWorker = 1,          // one job per worker: the tests reason about exact slots
            ScaleUpAfter = TimeSpan.FromMilliseconds(200),
            IdleTimeout = TimeSpan.FromSeconds(30),
            JobTimeout = TimeSpan.FromSeconds(60),
            WorkerStartTimeout = TimeSpan.FromSeconds(60),
        };
        configure?.Invoke(options);
        return options;
    }

    /// <summary>
    /// One worker with two slots, the default <see cref="PdfPoolOptions.JobsPerWorker"/>: every two
    /// jobs in flight share a process, for tests of what one job's fault does to its neighbour.
    /// </summary>
    public static PdfPoolOptions SharedWorkerOptions(Action<PdfPoolOptions>? configure = null)
        => Options(o =>
        {
            o.MinWorkers = 1;
            o.MaxWorkers = 1;
            o.JobsPerWorker = 2;
            configure?.Invoke(o);
        });

    public static string Input(string fileName) => HostRunner.Input(fileName);

    /// <summary>A copy of a test document whose path contains <paramref name="name"/>, for faults that match on the input path.</summary>
    public static string CopyAs(string fileName, string directory, string name)
    {
        string path = Path.Combine(directory, name + ".pdf");
        File.Copy(Input(fileName), path);
        return path;
    }

    public static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"PdfiumPoolTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
        }
    }
}
