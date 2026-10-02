using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace PdfiumWrapper.Tests.Host;

internal static partial class Scenarios
{
    private const int Success = 0;
    private const int Failure = 2;

    private static int Report(bool ok, Dictionary<string, object?> report)
    {
        report["ok"] = ok;
        Console.Out.WriteLine(JsonSerializer.Serialize(report));
        Console.Out.Flush();
        return ok ? Success : Failure;
    }

    private static string[] Describe(IEnumerable<Exception> errors)
        => errors.Select(e => $"{e.GetType().Name}: {e.Message}").ToArray();

    /// <summary>
    /// N threads released by one barrier; each thread's first native use is a different entry point.
    /// </summary>
    public static int InitRace(int threads, string input)
    {
        var errors = new ConcurrentBag<Exception>();
        using var start = new Barrier(threads);

        var workers = Enumerable.Range(0, threads).Select(i => new Thread(() =>
        {
            try
            {
                start.SignalAndWait();
                switch (i % 4)
                {
                    case 0:
                    {
                        using var doc = new PdfDocument(input);
                        _ = doc.PageCount;
                        break;
                    }
                    case 1:
                    {
                        using var merger = new PdfMerger();
                        _ = merger.PageCount;
                        break;
                    }
                    case 2:
                    {
                        using var merger = new PdfMerger(input);
                        _ = merger.PageCount;
                        break;
                    }
                    default:
                    {
                        // TiffWriter on a rendered page.
                        using var doc = new PdfDocument(input);
                        using var tiff = new MemoryStream();
                        doc.SaveAsTiff(tiff, 72);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }) { IsBackground = true }).ToList();

        workers.ForEach(t => t.Start());
        bool joined = workers.All(t => t.Join(TimeSpan.FromMinutes(2)));

        return Report(joined && errors.IsEmpty, new Dictionary<string, object?>
        {
            ["scenario"] = "init-race",
            ["threads"] = threads,
            ["joined"] = joined,
            ["exceptions"] = Describe(errors),
            ["initCount"] = InitCount(),
        });
    }

    /// <summary>
    /// Milliseconds from process start to the first completed 72 DPI render, for a chosen first native use.
    /// </summary>
    public static int ColdStart(string first, string input, long mainStart)
    {
        switch (first)
        {
            case "document":
                break;
            case "merger":
            {
                using var merger = new PdfMerger(input);
                _ = merger.PageCount;
                break;
            }
            case "tiff":
            {
                using var doc = new PdfDocument(input);
                using var tiff = new MemoryStream();
                doc.SaveAsTiff(tiff, 72);
                break;
            }
            default:
                throw new HostArgumentException($"first must be document, merger or tiff; got '{first}'");
        }

        int bytes;
        using (var doc = new PdfDocument(input))
        using (var page = doc.GetPage(0))
        {
            int width = (int)Math.Round(page.Width);
            int height = (int)Math.Round(page.Height);
            bytes = page.RenderToBytes(width, height).Length;
        }

        double sinceMain = Stopwatch.GetElapsedTime(mainStart).TotalMilliseconds;
        double sinceProcessStart = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;

        return Report(bytes > 0, new Dictionary<string, object?>
        {
            ["scenario"] = "cold-start",
            ["first"] = first,
            ["msSinceProcessStart"] = Math.Round(sinceProcessStart, 1),
            ["msSinceMain"] = Math.Round(sinceMain, 1),
            ["renderedBytes"] = bytes,
        });
    }
}
