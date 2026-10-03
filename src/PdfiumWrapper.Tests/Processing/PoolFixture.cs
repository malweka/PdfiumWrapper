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

    public static string Input(string fileName) => HostRunner.Input(fileName);

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
