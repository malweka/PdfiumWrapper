using System.Diagnostics;
using System.Reflection;
using BenchmarkDotNet.Attributes;

namespace PdfiumWrapper.Benchmarks.Comparison;

/// <summary>
/// Runs every benchmark method once for one document and prints what it produced. A timing is
/// only worth comparing if the engines did the same job: the same number of pages, output of a
/// plausible size, and (for Aspose) a license that is actually in effect.
/// </summary>
internal static class EngineCheck
{
    public static int Run(IReadOnlyCollection<string> engines)
    {
        var document = ComparisonBase.Documents.First(d => d.FileName == "contract.pdf");
        bool ok = true;

        foreach (var type in new[]
                 {
                     typeof(PageCountComparison), typeof(TiffComparison), typeof(PngComparison),
                     typeof(JpegComparison), typeof(MergeComparison), typeof(TextComparison),
                 })
        {
            Console.WriteLine();
            Console.WriteLine($"{type.Name} on {document}");

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Where(m => m.GetCustomAttribute<BenchmarkAttribute>() != null))
            {
                string engine = method.GetCustomAttribute<BenchmarkCategoryAttribute>()!.Categories[0];
                if (!engines.Contains(engine))
                    continue;

                var instance = (ComparisonBase)Activator.CreateInstance(type)!;
                instance.Document = document;
                instance.Setup();
                try
                {
                    long start = Stopwatch.GetTimestamp();
                    object? result = method.Invoke(instance, null);
                    double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

                    var files = Directory.GetFiles(OutputDirectoryOf(instance));
                    long bytes = files.Sum(f => new FileInfo(f).Length);
                    string outcome = result != null
                        ? $"returned {result}"
                        : $"{files.Length} file(s), {bytes:N0} bytes";

                    string note = Verify(type, document, result, files);
                    if (note.Length > 0)
                        ok = false;

                    Console.WriteLine($"  {engine,-14} {ms,10:F1} ms   {outcome}{(note.Length > 0 ? "   <-- " + note : "")}");
                }
                catch (TargetInvocationException ex)
                {
                    ok = false;
                    Console.WriteLine($"  {engine,-14} FAILED: {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
                }
                finally
                {
                    instance.IterationCleanup();
                    instance.Cleanup();
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine(ok ? "All engines produced the expected output." : "At least one engine did not produce the expected output.");
        return ok ? 0 : 2;
    }

    private static string OutputDirectoryOf(ComparisonBase instance)
        => (string)typeof(ComparisonBase)
            .GetField("OutputDirectory", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance)!;

    /// <summary>Returns a description of what is wrong, or an empty string.</summary>
    private static string Verify(Type type, CorpusDocument document, object? result, string[] files)
    {
        if (type == typeof(PageCountComparison))
            return result is int pages && pages == document.Pages ? "" : $"expected {document.Pages} pages";

        if (type == typeof(PngComparison) || type == typeof(JpegComparison))
            return files.Length == document.Pages ? "" : $"expected {document.Pages} image files";

        if (type == typeof(TiffComparison))
        {
            if (files.Length != 1)
                return "expected one TIFF file";
            int pages = CountTiffPages(files[0]);
            return pages == document.Pages ? "" : $"TIFF has {pages} pages, expected {document.Pages}";
        }

        if (type == typeof(MergeComparison))
        {
            if (files.Length != 1)
                return "expected one merged PDF";
            using var merged = new PdfDocument(files[0]);
            return merged.PageCount == document.Pages * 2 ? "" : $"merged PDF has {merged.PageCount} pages, expected {document.Pages * 2}";
        }

        if (type == typeof(TextComparison))
            return Convert.ToInt64(result) > 1000 ? "" : "expected more than 1,000 characters of text";

        return "";
    }

    /// <summary>Counts the image directories of a classic (non-BigTIFF) TIFF file.</summary>
    private static int CountTiffPages(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        ushort byteOrder = reader.ReadUInt16();
        bool littleEndian = byteOrder == 0x4949;
        ushort ReadUInt16() { var v = reader.ReadUInt16(); return littleEndian ? v : (ushort)((v >> 8) | (v << 8)); }
        uint ReadUInt32()
        {
            var v = reader.ReadUInt32();
            return littleEndian ? v : (v >> 24) | ((v >> 8) & 0xFF00) | ((v << 8) & 0xFF0000) | (v << 24);
        }

        if (ReadUInt16() != 42)
            return -1;

        int pages = 0;
        uint offset = ReadUInt32();
        while (offset != 0 && pages < 10_000)
        {
            reader.BaseStream.Position = offset;
            ushort entries = ReadUInt16();
            reader.BaseStream.Position += entries * 12L;
            offset = ReadUInt32();
            pages++;
        }

        return pages;
    }
}
