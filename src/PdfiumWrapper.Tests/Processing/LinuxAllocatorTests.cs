using PdfiumWrapper.Processing;

namespace PdfiumWrapper.Tests.Processing;

[Collection(PdfTestCollection.Name)]
public class LinuxAllocatorTests
{
    [Fact]
    public void Linux_CapsTheArenas()
    {
        var environment = new Dictionary<string, string?>();

        LinuxAllocator.AddWorkerDefaults(environment, isLinux: true);

        Assert.Equal("2", environment["MALLOC_ARENA_MAX"]);
        // Costs 9% throughput, so it is the application's choice.
        Assert.False(environment.ContainsKey("MALLOC_MMAP_THRESHOLD_"));
    }

    [Fact]
    public void Linux_KeepsAnInheritedValue()
    {
        // Set on the container image, for example: the worker inherits it unchanged.
        var environment = new Dictionary<string, string?> { ["MALLOC_ARENA_MAX"] = "4" };

        LinuxAllocator.AddWorkerDefaults(environment, isLinux: true);

        Assert.Equal("4", environment["MALLOC_ARENA_MAX"]);
    }

    [Fact]
    public void OtherSystems_AddNothing()
    {
        var environment = new Dictionary<string, string?>();

        LinuxAllocator.AddWorkerDefaults(environment, isLinux: false);

        Assert.Empty(environment);
    }

    [Fact]
    public void Launcher_AddsTheDefaultOnLinuxOnly()
    {
        var psi = WorkerLauncher.Create(new PdfPoolOptions { WorkerPath = "worker" }, Path.GetTempPath());

        // Inherited from this process if set here, otherwise the Linux default; nothing elsewhere.
        string? inherited = Environment.GetEnvironmentVariable("MALLOC_ARENA_MAX");
        string? expected = inherited ?? (OperatingSystem.IsLinux() ? "2" : null);
        Assert.Equal(expected, psi.Environment.TryGetValue("MALLOC_ARENA_MAX", out var value) ? value : null);
    }

    [Fact]
    public void WorkerEnvironment_OverridesTheDefaults()
    {
        var options = new PdfPoolOptions { WorkerPath = "worker" };
        options.WorkerEnvironment["MALLOC_ARENA_MAX"] = "4";
        options.WorkerEnvironment["MALLOC_MMAP_THRESHOLD_"] = "131072";

        var psi = WorkerLauncher.Create(options, Path.GetTempPath());

        Assert.Equal("4", psi.Environment["MALLOC_ARENA_MAX"]);
        Assert.Equal("131072", psi.Environment["MALLOC_MMAP_THRESHOLD_"]);
    }

    [Fact]
    public void TrimWhenIdle_NeverThrows()
    {
        LinuxAllocator.TrimWhenIdle();
        LinuxAllocator.TrimWhenIdle();
    }
}
