using System.Runtime.InteropServices;

namespace PdfiumWrapper.Processing;

/// <summary>
/// Keeps Linux workers small. glibc's malloc keeps the memory of freed page buffers inside the
/// process: after the first large page bitmap is freed it serves later ones from its own heaps
/// (one per thread, up to 8 per core), which shrink only from the top. A worker then holds the
/// largest job mix it has seen for the rest of its life. Measured in the pool qualification on
/// Ubuntu 24.04: 250-430 MB per idle worker, against about 30 MB on Windows, where the same
/// blocks go back to the system when freed.
/// </summary>
/// <remarks>
/// Two parts: environment variables set when the pool starts a worker (glibc reads them only at
/// process start), and <see cref="TrimWhenIdle"/> in the worker, which hands free pages back once
/// it has had no job for a moment. musl (Alpine) ignores the variables and has no malloc_trim; its
/// allocator already returns memory eagerly. See docs/HIGH-THROUGHPUT-PROCESSING.md, "Memory on Linux".
/// </remarks>
internal static class LinuxAllocator
{
    /// <summary>
    /// Two heaps instead of up to 8 per core, and buffers of 128 KiB or more always mapped on
    /// their own, so freeing one returns it to the system (setting the threshold also stops glibc
    /// from raising it after each large free).
    /// </summary>
    internal static readonly IReadOnlyList<KeyValuePair<string, string>> WorkerDefaults =
    [
        new("MALLOC_ARENA_MAX", "2"),
        new("MALLOC_MMAP_THRESHOLD_", "131072"),
    ];

    /// <summary>A worker idle this long hands its free memory back.</summary>
    internal static readonly TimeSpan IdleTrimDelay = TimeSpan.FromSeconds(1);

    private static int s_trimUnavailable;

    /// <summary>
    /// Adds <see cref="WorkerDefaults"/> to a worker's environment on Linux. A value already there,
    /// inherited from this process (set on the container image, for example), is kept;
    /// <see cref="PdfPoolOptions.WorkerEnvironment"/> is applied afterwards and wins over both.
    /// </summary>
    internal static void AddWorkerDefaults(IDictionary<string, string?> environment, bool isLinux)
    {
        if (!isLinux)
            return;

        foreach (var (name, value) in WorkerDefaults)
            environment.TryAdd(name, value);
    }

    /// <summary>
    /// Returns free heap pages to the system (glibc <c>malloc_trim(0)</c>). Safe while other threads
    /// allocate: glibc locks one heap at a time. Does nothing off Linux or without glibc.
    /// </summary>
    internal static void TrimWhenIdle()
    {
        if (!OperatingSystem.IsLinux() || Volatile.Read(ref s_trimUnavailable) != 0)
            return;

        try
        {
            MallocTrim(0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Volatile.Write(ref s_trimUnavailable, 1);
        }
    }

    [DllImport("libc.so.6", EntryPoint = "malloc_trim")]
    private static extern int MallocTrim(nuint pad);
}
