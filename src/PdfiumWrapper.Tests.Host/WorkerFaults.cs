using PdfiumWrapper.Processing;
using PdfiumWrapper.Processing.Protocol;

namespace PdfiumWrapper.Tests.Host;

/// <summary>
/// Fault injection for worker-pool tests. The pool starts this host as a worker (through
/// <c>WorkerPath</c>) and the tests pass a fault through the worker environment:
/// <c>PDFIUMWRAPPER_TEST_FAULT=crash|hang|garbage:&lt;substring of the input path&gt;</c>.
/// A job whose input path contains the substring triggers the fault once per process.
/// </summary>
internal static class WorkerFaults
{
    public const string EnvironmentVariable = "PDFIUMWRAPPER_TEST_FAULT";

    public static WorkerHooks? FromEnvironment()
    {
        var spec = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrEmpty(spec))
            return null;

        int colon = spec.IndexOf(':');
        string fault = colon < 0 ? spec : spec[..colon];
        string match = colon < 0 ? "" : spec[(colon + 1)..];
        bool fired = false;

        return new WorkerHooks
        {
            BeforeJob = job =>
            {
                if (fired || !job.Input.Contains(match, StringComparison.OrdinalIgnoreCase))
                    return;
                fired = true;

                switch (fault)
                {
                    case "crash":
                        // Same effect as a native abort: the process dies mid-job with no result frame.
                        Environment.FailFast("test fault: crash");
                        break;
                    case "hang":
                        Thread.Sleep(Timeout.Infinite);
                        break;
                    case "garbage":
                        // Bytes that are not a frame, on the protocol stream.
                        using (var stdout = Console.OpenStandardOutput())
                        {
                            stdout.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F, 0x00, 0x01, 0x02 });
                            stdout.Flush();
                        }
                        Thread.Sleep(Timeout.Infinite);
                        break;
                }
            },
        };
    }
}
