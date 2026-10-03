using PdfiumWrapper.Processing;
using PdfiumWrapper.Processing.Protocol;

namespace PdfiumWrapper.Tests.Host;

/// <summary>
/// Fault injection for worker-pool tests. The pool starts this host as a worker (through
/// <c>WorkerPath</c>) and the tests pass a fault through the worker environment:
/// <c>PDFIUMWRAPPER_TEST_FAULT=crash|hang|garbage:&lt;substring of the input path&gt;</c>.
/// A job whose input path contains the substring triggers the fault once per process.
/// <c>crash-after-page-N:&lt;substring&gt;</c> crashes once page N of an image job is in place.
/// <c>slow-start:&lt;milliseconds&gt;</c> delays the worker's ready report.
/// </summary>
internal static class WorkerFaults
{
    public const string EnvironmentVariable = "PDFIUMWRAPPER_TEST_FAULT";
    public const string PidFileVariable = "PDFIUMWRAPPER_TEST_PIDFILE";

    public static WorkerHooks? FromEnvironment()
    {
        var spec = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrEmpty(spec))
            return null;

        int colon = spec.IndexOf(':');
        string fault = colon < 0 ? spec : spec[..colon];
        string match = colon < 0 ? "" : spec[(colon + 1)..];
        bool fired = false;

        if (fault == "slow-start")
        {
            // The test learns this process's id from the file named by PDFIUMWRAPPER_TEST_PIDFILE,
            // so it can check the pool killed a worker it gave up on.
            return new WorkerHooks
            {
                BeforeHello = () =>
                {
                    if (Environment.GetEnvironmentVariable(PidFileVariable) is { Length: > 0 } pidFile)
                        File.AppendAllText(pidFile, Environment.ProcessId + Environment.NewLine);
                    Thread.Sleep(int.Parse(match));
                },
            };
        }

        if (fault.StartsWith("crash-after-page-", StringComparison.Ordinal))
        {
            int page = int.Parse(fault["crash-after-page-".Length..]);
            return new WorkerHooks
            {
                AfterPage = (job, done) =>
                {
                    if (done == page && job.Input.Contains(match, StringComparison.OrdinalIgnoreCase))
                        Environment.FailFast("test fault: crash after page " + page);
                },
            };
        }

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
