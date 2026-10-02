using System.Text;
using Xunit.Abstractions;

namespace PdfiumWrapper.Tests.Concurrency;

/// <summary>
/// Scenarios that change process-global state, need a fresh process, or may abort natively.
/// Each runs in <c>PdfiumWrapper.Tests.Host</c> with a bounded timeout.
/// </summary>
[Collection(PdfTestCollection.Name)]
public class PdfiumHostTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    private readonly ITestOutputHelper _output;

    public PdfiumHostTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private HostResult RunScenario(string scenario, params string[] keyValues)
    {
        var result = HostRunner.Run(scenario, Timeout, keyValues);
        _output.WriteLine($"{scenario}: {result}");
        return result;
    }

    [Fact]
    public void FirstUse_FromManyThreadsAtOnce_InitializesOnce()
    {
        for (int run = 0; run < 3; run++)
        {
            var result = RunScenario("init-race", "threads=16", $"input={HostRunner.Input("doc-1-page.pdf")}");

            Assert.True(result.ExitCode == 0, result.ToString());
            Assert.Equal(0, result.Json.GetProperty("exceptions").GetArrayLength());
            Assert.Equal(1, result.Json.GetProperty("initCount").GetInt64());
        }
    }

    [Theory, InlineData("document"), InlineData("merger"), InlineData("tiff")]
    public void FreshProcess_WorksWhateverIsUsedFirst(string first)
    {
        var result = RunScenario("cold-start", $"first={first}", $"input={HostRunner.Input("doc-1-page.pdf")}");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.True(result.Json.GetProperty("renderedBytes").GetInt32() > 0);
    }

    [Fact]
    public void AsyncCallers_DoNotStarveTheThreadPool()
    {
        string input = $"input={HostRunner.Input("doc-3-pages-with-comments.pdf")}";

        var asyncRun = RunScenario("starvation", "mode=async", "bound=100", input);
        Assert.True(asyncRun.ExitCode == 0, asyncRun.ToString());
        Assert.Equal(asyncRun.Json.GetProperty("callers").GetInt32(), asyncRun.Json.GetProperty("completed").GetInt32());
        Assert.True(asyncRun.Json.GetProperty("heartbeatP99Ms").GetDouble() <= 100);

        // Async image streaming: neither the factory call nor the enumeration may park pool threads.
        var streamRun = RunScenario("starvation", "mode=stream", "bound=100", input);
        Assert.True(streamRun.ExitCode == 0, streamRun.ToString());
        Assert.Equal(streamRun.Json.GetProperty("callers").GetInt32(), streamRun.Json.GetProperty("completed").GetInt32());
        Assert.True(streamRun.Json.GetProperty("heartbeatP99Ms").GetDouble() <= 100);

        // The synchronous API on pool threads blocks them while waiting for the gate. Only
        // completion is asserted; the heartbeat figures document the difference.
        var syncRun = RunScenario("starvation", "mode=sync", input);
        Assert.True(syncRun.ExitCode == 0, syncRun.ToString());
        Assert.Equal(syncRun.Json.GetProperty("callers").GetInt32(), syncRun.Json.GetProperty("completed").GetInt32());
    }

    [Fact]
    public void AbandonedObjectGraphs_AreReleasedByGatedDrains_InDependencyOrder()
    {
        var result = RunScenario("finalizer-drain", "graphs=400", "workers=4",
            $"input={HostRunner.Input("doc-3-pages-with-comments.pdf")}");

        Assert.True(result.ExitCode == 0, result.ToString());
        var json = result.Json;
        Assert.True(json.GetProperty("enqueued").GetInt64() > 0);
        Assert.Equal(json.GetProperty("enqueued").GetInt64(), json.GetProperty("drained").GetInt64());
        Assert.Equal(0, json.GetProperty("pendingCount").GetInt32());
        Assert.Equal(0, json.GetProperty("liveHandleCount").GetInt64());
        Assert.Equal(0, json.GetProperty("finalizerGateEvents").GetInt32());
        Assert.Equal(0, json.GetProperty("orderViolations").GetInt32());
        Assert.Equal(0, json.GetProperty("missingReleases").GetInt32());
    }

    [Fact]
    public void TwoAssemblyLoadContexts_ShareOneGate()
    {
        var result = RunScenario("alc-shared-gate", $"input={HostRunner.Input("doc-3-pages-with-comments.pdf")}");

        Assert.True(result.ExitCode == 0, result.ToString());
        var json = result.Json;
        Assert.True(json.GetProperty("typesDistinct").GetBoolean());
        Assert.True(json.GetProperty("sharedGate").GetBoolean());
        Assert.Equal(1, json.GetProperty("maxActiveNative").GetInt64());
        Assert.Equal(1, json.GetProperty("initCount").GetInt64());
    }

    [Fact]
    public void Shutdown_RefusesWhileObjectsAreAlive_AndTheLibraryRestartsAfterwards()
    {
        var result = RunScenario("shutdown", $"input={HostRunner.Input("doc-1-page.pdf")}");

        Assert.True(result.ExitCode == 0, result.ToString());
        var json = result.Json;
        Assert.True(json.GetProperty("threwWhileLive").GetBoolean());
        Assert.Equal(2, json.GetProperty("initCount").GetInt64());
    }

    /// <summary>
    /// Characterization, not a pass/fail on PDFium's robustness: feed damaged files to a child
    /// process and record what happens. The only assertion is that this process survives and that
    /// every probe ends. Inputs that abort the child are process-fatal for an in-process consumer.
    /// </summary>
    [Fact]
    public void MalformedInputs_AreCharacterizedInAChildProcess()
    {
        string directory = Path.Combine(Bootstrapper.WorkingDirectory, "malformed");
        Directory.CreateDirectory(directory);

        var table = new StringBuilder();
        table.AppendLine("# Crash probe");
        table.AppendLine();
        table.AppendLine("Each damaged input was opened, rendered, text-extracted and saved in a child process.");
        table.AppendLine("Exit 0: handled as a valid document. Exit 2: rejected with a managed exception. Any other exit code: the process aborted.");
        table.AppendLine();
        table.AppendLine("| Input | Exit code | Outcome |");
        table.AppendLine("|---|---|---|");

        int aborted = 0;
        foreach (var (name, bytes) in MalformedInputs())
        {
            string path = Path.GetFullPath(Path.Combine(directory, name));
            File.WriteAllBytes(path, bytes);

            var result = HostRunner.Run("crash-probe", Timeout, $"input={path}");
            string outcome = result.ExitCode switch
            {
                0 => "opened and processed",
                2 => "rejected: " + Describe(result),
                _ => "PROCESS ABORTED",
            };
            if (result.ExitCode is not (0 or 2))
                aborted++;

            table.AppendLine($"| {name} | {result.ExitCode} | {outcome} |");
        }

        table.AppendLine();
        table.AppendLine($"Inputs that aborted the process: {aborted}.");

        string report = Path.Combine(Bootstrapper.WorkingDirectory, "crash-probe.md");
        File.WriteAllText(report, table.ToString());
        _output.WriteLine(table.ToString());

        Assert.True(File.Exists(report));
    }

    private static string Describe(HostResult result)
    {
        try
        {
            var json = result.Json;
            string stage = json.TryGetProperty("stage", out var s) ? s.GetString() ?? "" : "";
            string exception = json.TryGetProperty("exception", out var e) ? e.GetString() ?? "" : "";
            return $"{stage}: {exception}".Replace('|', '/');
        }
        catch (Exception)
        {
            return "no report";
        }
    }

    private static IEnumerable<(string name, byte[] bytes)> MalformedInputs()
    {
        string[] fixtures =
        {
            "doc-1-page.pdf", "doc-3-pages-with-comments.pdf", "contract.pdf", "fw2.pdf", "presentation.pdf",
        };

        foreach (var fixture in fixtures)
        {
            byte[] original = File.ReadAllBytes(Path.Combine(Bootstrapper.TestFilesDirectory, fixture));
            string stem = Path.GetFileNameWithoutExtension(fixture);

            foreach (int percent in new[] { 25, 50, 75 })
                yield return ($"{stem}-truncated-{percent}.pdf", original[..(int)(original.LongLength * percent / 100)]);

            yield return ($"{stem}-flipped-1pct.pdf", FlipBytes(original, fraction: 0.01, seed: 20260930));
            yield return ($"{stem}-zeroed-xref.pdf", ZeroCrossReference(original));
        }
    }

    private static byte[] FlipBytes(byte[] original, double fraction, int seed)
    {
        var bytes = (byte[])original.Clone();
        var random = new Random(seed);
        int flips = (int)(bytes.Length * fraction);
        for (int i = 0; i < flips; i++)
        {
            int index = random.Next(bytes.Length);
            bytes[index] = (byte)~bytes[index];
        }
        return bytes;
    }

    /// <summary>Zero the cross-reference data that the trailing <c>startxref</c> points at.</summary>
    private static byte[] ZeroCrossReference(byte[] original)
    {
        var bytes = (byte[])original.Clone();
        string tail = Encoding.Latin1.GetString(bytes, Math.Max(0, bytes.Length - 1024), Math.Min(1024, bytes.Length));
        int marker = tail.LastIndexOf("startxref", StringComparison.Ordinal);
        if (marker < 0)
            return bytes;

        string digits = new(tail[(marker + "startxref".Length)..].SkipWhile(char.IsWhiteSpace).TakeWhile(char.IsDigit).ToArray());
        if (!long.TryParse(digits, out long offset) || offset < 0 || offset >= bytes.Length)
            return bytes;

        Array.Clear(bytes, (int)offset, (int)Math.Min(512, bytes.Length - offset));
        return bytes;
    }
}
