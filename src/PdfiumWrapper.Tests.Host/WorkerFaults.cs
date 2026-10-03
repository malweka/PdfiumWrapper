using PdfiumWrapper.Processing;
using PdfiumWrapper.Processing.Protocol;

namespace PdfiumWrapper.Tests.Host;

/// <summary>
/// Fault injection for worker-pool tests. The pool starts this host as a worker (through
/// <c>WorkerPath</c>) and the tests pass a fault through the worker environment:
/// <c>PDFIUMWRAPPER_TEST_FAULT=crash|hang|garbage:&lt;substring of the input path&gt;</c>.
/// A job whose input path contains the substring triggers the fault once per process.
/// <c>crash-after-page-N:&lt;substring&gt;</c> crashes once page N of an image job is in place;
/// <c>hang-after-page-N:&lt;substring&gt;</c> hangs that job (not the worker's loop) there;
/// <c>sleep-after-page-MS:&lt;substring&gt;</c> sleeps MS milliseconds after every page of such jobs;
/// <c>pause-while-marked:&lt;substring&gt;</c> holds such a job after each page while <c>&lt;input&gt;.pause</c> exists.
/// <c>slow-start:&lt;milliseconds&gt;</c> delays the worker's ready report;
/// <c>fail-start-if-exists:&lt;path&gt;</c> makes the worker exit before reporting ready while that file exists;
/// <c>exit-after-hello-if-exists:&lt;path&gt;</c> makes it exit right after reporting ready while that file exists;
/// <c>stderr-line:&lt;length&gt;</c> writes a short line and one of that length to standard error at start.
/// Several faults combine with <c>;</c>.
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

        WorkerHooks? combined = null;
        foreach (var one in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var hooks = FromSpec(one);
            combined = combined == null ? hooks : new WorkerHooks
            {
                BeforeHello = combined.BeforeHello + hooks.BeforeHello,
                AfterHello = combined.AfterHello + hooks.AfterHello,
                BeforeJob = combined.BeforeJob + hooks.BeforeJob,
                AfterPage = combined.AfterPage + hooks.AfterPage,
            };
        }

        return combined;
    }

    private static WorkerHooks FromSpec(string spec)
    {
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

        if (fault == "fail-start-if-exists")
        {
            // A worker that cannot start, switched on by the test after the pool exists.
            return new WorkerHooks
            {
                BeforeHello = () =>
                {
                    if (File.Exists(match))
                        Environment.Exit(4);
                },
            };
        }

        if (fault == "exit-after-hello-if-exists")
        {
            // A worker that reports ready and dies at once, while that file exists.
            return new WorkerHooks
            {
                AfterHello = () =>
                {
                    if (File.Exists(match))
                        Environment.Exit(4);
                },
            };
        }

        if (fault == "stderr-line")
        {
            // A short line, then one of this many characters, on standard error before reporting ready.
            return new WorkerHooks
            {
                BeforeHello = () =>
                {
                    Console.Error.WriteLine("short line");
                    Console.Error.WriteLine(new string('x', int.Parse(match)));
                    Console.Error.Flush();
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

        if (fault.StartsWith("hang-after-page-", StringComparison.Ordinal))
        {
            // Hangs the job on its own thread: the worker's loop keeps taking other jobs, and the
            // job ignores cancellation, like a page that never finishes rendering.
            int page = int.Parse(fault["hang-after-page-".Length..]);
            return new WorkerHooks
            {
                AfterPage = (job, done) =>
                {
                    if (done == page && job.Input.Contains(match, StringComparison.OrdinalIgnoreCase))
                        Thread.Sleep(Timeout.Infinite);
                },
            };
        }

        if (fault == "pause-while-marked")
        {
            // Holds the job after each page for as long as "<input>.pause" exists, ignoring
            // cancellation: the test decides exactly when the job may go on, on any attempt.
            return new WorkerHooks
            {
                AfterPage = (job, _) =>
                {
                    if (!job.Input.Contains(match, StringComparison.OrdinalIgnoreCase))
                        return;
                    while (File.Exists(job.Input + ".pause"))
                        Thread.Sleep(20);
                },
            };
        }

        if (fault.StartsWith("sleep-after-page-", StringComparison.Ordinal))
        {
            // A slow job that still finishes: every page of it takes at least this long.
            int milliseconds = int.Parse(fault["sleep-after-page-".Length..]);
            return new WorkerHooks
            {
                AfterPage = (job, _) =>
                {
                    if (job.Input.Contains(match, StringComparison.OrdinalIgnoreCase))
                        Thread.Sleep(milliseconds);
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
