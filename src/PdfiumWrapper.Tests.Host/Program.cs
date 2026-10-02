using System.Diagnostics;
using PdfiumWrapper.Tests.Host;

// Out-of-process scenarios for tests that change process-global state or need a fresh process.
// Usage: PdfiumWrapper.Tests.Host <scenario> [key=value ...]
// Writes one JSON object to stdout. Exit codes: 0 success, 2 scenario failure, 3 bad arguments.
// A native abort surfaces as any other exit code; the parent test records it.

var mainStart = Stopwatch.GetTimestamp();

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: PdfiumWrapper.Tests.Host <scenario> [key=value ...]");
    return 3;
}

try
{
    var options = new HostArgs(args.Skip(1));

    // Must be set before the first wrapper type is touched: the switch is read once.
    AppContext.SetSwitch("PdfiumWrapper.Diagnostics", options.String("diag", "true") == "true");

    return args[0] switch
    {
        "init-race" => Scenarios.InitRace(
            threads: options.Int("threads", 8),
            input: options.Required("input")),
        "cold-start" => Scenarios.ColdStart(
            first: options.String("first", "document"),
            input: options.Required("input"),
            mainStart: mainStart),
        "starvation" => Scenarios.Starvation(
            callers: options.Int("callers", 8 * Environment.ProcessorCount),
            input: options.Required("input"),
            heartbeatBoundMs: options.Int("bound", 100),
            mode: options.String("mode", "async")),
        "finalizer-drain" => Scenarios.FinalizerDrain(
            graphs: options.Int("graphs", 200),
            workers: options.Int("workers", 4),
            input: options.Required("input")),
        "alc-shared-gate" => Scenarios.AlcSharedGate(input: options.Required("input")),
        "crash-probe" => Scenarios.CrashProbe(input: options.Required("input")),
        "shutdown" => Scenarios.Shutdown(input: options.Required("input")),
        _ => BadScenario(args[0])
    };
}
catch (HostArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 3;
}

static int BadScenario(string name)
{
    Console.Error.WriteLine($"unknown scenario '{name}'");
    return 3;
}
