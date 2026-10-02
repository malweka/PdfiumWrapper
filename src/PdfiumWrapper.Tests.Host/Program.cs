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

var options = new HostArgs(args.Skip(1));

try
{
    return args[0] switch
    {
        "init-race" => Scenarios.InitRace(
            threads: options.Int("threads", 8),
            input: options.Required("input")),
        "cold-start" => Scenarios.ColdStart(
            first: options.String("first", "document"),
            input: options.Required("input"),
            mainStart: mainStart),
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
