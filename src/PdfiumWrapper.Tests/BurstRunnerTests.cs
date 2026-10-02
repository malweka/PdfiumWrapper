using System.Text.Json;

namespace PdfiumWrapper.Tests;

/// <summary>
/// The burst runner in <c>PdfiumWrapper.Benchmarks</c> produces the numbers used for sizing, and
/// it writes to a directory the user names. Both need to be right, so it is run here as a child
/// process against a small input.
/// </summary>
[Collection(PdfTestCollection.Name)]
public class BurstRunnerTests : IDisposable
{
    private const string BenchmarksAssembly = "PdfiumWrapper.Benchmarks";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"PdfiumBurstTests_{Guid.NewGuid():N}");
    private readonly string _input;

    public BurstRunnerTests()
    {
        _input = Path.Combine(_root, "input");
        Directory.CreateDirectory(_input);
        File.Copy(Path.Combine(Bootstrapper.TestFilesDirectory, "doc-1-page.pdf"), Path.Combine(_input, "doc-1-page.pdf"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private JsonElement RunBurst(string outDirectory, params string[] extra)
    {
        string report = Path.Combine(_root, $"report-{Guid.NewGuid():N}.json");
        var arguments = new List<string>
        {
            "burst", "--dpi", "20", "--callers", "2", "--t", "600",
            "--input", _input, "--out", outDirectory, "--report", report,
        };
        arguments.AddRange(extra);

        var result = HostRunner.RunAssembly(BenchmarksAssembly, Timeout, arguments.ToArray());
        Assert.True(result.ExitCode == 0, result.ToString());
        return JsonDocument.Parse(File.ReadAllText(report)).RootElement;
    }

    /// <summary>
    /// A weight total of 37 shares a factor with the old fixed stride of 37, which sent every job
    /// to the first format. Two full cycles must give exactly twice the weights.
    /// </summary>
    [Fact]
    public void Mix_WhoseTotalIsAMultipleOfTheDefaultStride_StillGetsItsProportions()
    {
        var report = RunBurst(Path.Combine(_root, "out"), "--n", "74", "--mix", "png:18,jpeg:19");

        var byFormat = report.GetProperty("jobsByFormat");
        Assert.Equal(36, byFormat.GetProperty("png").GetInt32());
        Assert.Equal(38, byFormat.GetProperty("jpeg").GetInt32());
        Assert.Equal(74, report.GetProperty("jobs").GetProperty("success").GetInt32());
    }

    [Theory]
    [InlineData("tiff:50,png:30,jpeg:20", 100, 50, 30, 20)]
    [InlineData("tiff:1,png:1,jpeg:2", 8, 2, 2, 4)]
    [InlineData("tiff:2,png:2,jpeg:2", 12, 4, 4, 4)]
    public void Mix_GivesEachFormatItsShare(string mix, int jobs, int tiff, int png, int jpeg)
    {
        var report = RunBurst(Path.Combine(_root, "out"), "--n", jobs.ToString(), "--mix", mix);

        var byFormat = report.GetProperty("jobsByFormat");
        Assert.Equal(tiff, byFormat.GetProperty("tiff").GetInt32());
        Assert.Equal(png, byFormat.GetProperty("png").GetInt32());
        Assert.Equal(jpeg, byFormat.GetProperty("jpeg").GetInt32());
    }

    /// <summary>
    /// Weights near int.MaxValue used to overflow the stride search and leave a stride of zero,
    /// which sent every job to the first format. Totals that large are refused up front.
    /// </summary>
    [Theory]
    [InlineData("png:1073741823,jpeg:1073741823")]
    [InlineData("png:2147483647,jpeg:2147483647")]
    [InlineData("png:5000,jpeg:5001")]
    public void Mix_WhoseTotalIsAboveTheSupportedLimit_IsRejectedAsAnArgumentError(string mix)
    {
        string outDirectory = Path.Combine(_root, "rejected");
        var result = HostRunner.RunAssembly(BenchmarksAssembly, Timeout,
            "burst", "--n", "4", "--dpi", "20", "--input", _input, "--out", outDirectory,
            "--report", Path.Combine(_root, "rejected.json"), "--mix", mix);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("largest supported total", result.StandardError);
        Assert.False(Directory.Exists(outDirectory), "a rejected run must not create output");
    }

    /// <summary>
    /// The largest accepted total: the stride must be positive and coprime with it, and a run much
    /// shorter than the total must still reach both formats.
    /// </summary>
    [Theory]
    [InlineData("png:5000,jpeg:5000", 10_000)]
    [InlineData("png:4999,jpeg:5000", 9_999)]
    [InlineData("png:18,jpeg:19", 37)]
    [InlineData("tiff:50,png:30,jpeg:20", 100)]
    [InlineData("png:2,jpeg:2", 4)]
    [InlineData("png:1", 1)]
    public void Mix_UsesAPositiveStrideCoprimeWithItsTotal(string mix, int expectedTotal)
    {
        var report = RunBurst(Path.Combine(_root, "out"), "--n", "40", "--mix", mix);

        int total = report.GetProperty("mixTotalWeight").GetInt32();
        int stride = report.GetProperty("mixStride").GetInt32();
        Assert.Equal(expectedTotal, total);
        Assert.True(stride > 0, $"stride {stride} is not positive");
        Assert.Equal(1, Gcd(stride, total));

        if (expectedTotal == 100)
            Assert.Equal(37, stride); // the recorded benchmark runs used this sequence

        // Every format in the mix gets jobs even though 40 jobs is far less than a large total.
        var byFormat = report.GetProperty("jobsByFormat");
        foreach (var entry in mix.Split(','))
        {
            string format = entry.Split(':')[0];
            Assert.True(byFormat.TryGetProperty(format, out var count) && count.GetInt32() > 0,
                $"no {format} jobs were scheduled for --mix {mix}");
        }

        if (expectedTotal >= 9_999)
        {
            // Two equal weights: a 40-job run should land close to half and half.
            Assert.InRange(byFormat.GetProperty("png").GetInt32(), 14, 26);
        }
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
            (a, b) = (b, a % b);
        return a;
    }

    [Fact]
    public void ExistingFilesInTheOutDirectory_SurviveStartupAndCleanup()
    {
        string outDirectory = Path.Combine(_root, "populated");
        string sentinel = Path.Combine(outDirectory, "keep-me.txt");
        string nestedSentinel = Path.Combine(outDirectory, "archive", "keep-me-too.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(nestedSentinel)!);
        File.WriteAllText(sentinel, "not benchmark output");
        File.WriteAllText(nestedSentinel, "not benchmark output");

        // Kept output: the run directory is a new child and the existing data is untouched.
        var kept = RunBurst(outDirectory, "--n", "4", "--keep-output", "true");
        string runDirectory = kept.GetProperty("outputDirectory").GetString()!;

        Assert.True(File.Exists(sentinel));
        Assert.True(File.Exists(nestedSentinel));
        Assert.Equal(Path.GetFullPath(outDirectory), Path.GetFullPath(Path.GetDirectoryName(runDirectory)!));
        Assert.NotEmpty(Directory.GetFiles(runDirectory));

        // Default cleanup: only this run's own directory is removed.
        RunBurst(outDirectory, "--n", "4");

        Assert.True(File.Exists(sentinel));
        Assert.True(File.Exists(nestedSentinel));
        Assert.True(Directory.Exists(runDirectory), "an earlier run's kept output was deleted");
        Assert.Equal(2, Directory.GetDirectories(outDirectory).Length);
    }

    [Fact]
    public void OutDirectoryCreatedByTheRun_IsRemovedAgainWhenOutputIsNotKept()
    {
        string outDirectory = Path.Combine(_root, "fresh");

        RunBurst(outDirectory, "--n", "2");

        Assert.False(Directory.Exists(outDirectory));
    }
}
