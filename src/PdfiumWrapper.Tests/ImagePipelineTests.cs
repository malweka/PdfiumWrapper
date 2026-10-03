using System.Text;
using PdfiumWrapper.Processing;
using PdfiumWrapper.Tests.Processing;

namespace PdfiumWrapper.Tests;

/// <summary>
/// The page image pipeline: render size limits, DPI validation, the shared encode-then-write
/// outputs (names, defaults, managed file I/O), async behaviour under a blocked
/// synchronization context, and embedded thumbnails.
/// </summary>
[Collection(PdfTestCollection.Name)]
public class ImagePipelineTests : IDisposable
{
    private const string OnePagePdf = "Docs/doc-1-page.pdf";
    private const string ContractPdf = "Docs/contract.pdf";

    private readonly List<string> _tempDirectories = new();

    public void Dispose()
    {
        AppContext.SetData(RenderLimits.MaxPixelsKey, null);
        foreach (var directory in _tempDirectories)
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private string CreateTempDirectory(string suffix = "")
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PdfiumImages_{Guid.NewGuid():N}{suffix}");
        Directory.CreateDirectory(directory);
        _tempDirectories.Add(directory);
        return directory;
    }

    #region Render size limit (AUD-003)

    /// <summary>A 200 x 200 inch page: 60,000 x 60,000 pixels at 300 DPI, far over the default limit.</summary>
    private static PdfDocument OversizedDocument()
    {
        var doc = new PdfDocument();
        doc.AddPage(612, 792).Dispose();
        doc.AddPage(14400, 14400).Dispose();
        return doc;
    }

    public static TheoryData<string> OversizedOperations => new()
    {
        "RenderPages", "RenderPagesAsync", "StreamImageBytes", "StreamImageBytesAsync", "SaveAsPngs",
        "SaveAsJpegs", "SaveAsTiffFile", "SaveAsTiffStream", "SaveAsTiffAsync", "SaveAsImagesStreams",
    };

    [Theory]
    [MemberData(nameof(OversizedOperations))]
    public async Task OversizedPage_FailsBeforeAnyBitmapIsAllocated(string operation)
    {
        using var doc = OversizedDocument();
        string directory = CreateTempDirectory();
        string tiffPath = Path.Combine(directory, "out.tiff");
        long liveBefore = PdfiumRuntime.LiveHandleCount;

        Func<Task> act = operation switch
        {
            "RenderPages" => () => Task.FromResult(doc.RenderPages(300)),
            "RenderPagesAsync" => () => doc.RenderPagesAsync(300),
            "StreamImageBytes" => () => Task.FromResult(doc.StreamImageBytes(ImageFormat.Png).ToList()),
            "StreamImageBytesAsync" => async () => { await foreach (var _ in doc.StreamImageBytesAsync(ImageFormat.Jpeg)) { } },
            "SaveAsPngs" => () => { doc.SaveAsPngs(directory); return Task.CompletedTask; },
            "SaveAsJpegs" => () => { doc.SaveAsJpegs(directory); return Task.CompletedTask; },
            "SaveAsTiffFile" => () => { doc.SaveAsTiff(tiffPath, 300); return Task.CompletedTask; },
            "SaveAsTiffStream" => () => { doc.SaveAsTiff(new MemoryStream(), 300); return Task.CompletedTask; },
            "SaveAsTiffAsync" => () => doc.SaveAsTiffAsync(tiffPath, 300),
            "SaveAsImagesStreams" => () =>
            {
                doc.SaveAsImages(new Stream[] { new MemoryStream(), new MemoryStream() }, ImageFormat.Png, 90, 300, 300);
                return Task.CompletedTask;
            },
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(act);

        Assert.Contains("page index 1", ex.Message);
        Assert.Contains("60000 x 60000", ex.Message);
        Assert.Contains(RenderLimits.MaxPixelsKey, ex.Message);
        Assert.Equal(liveBefore, PdfiumRuntime.LiveHandleCount);
        Assert.False(File.Exists(tiffPath), "a failed TIFF export must not leave a partial file behind");
    }

    [Fact]
    public void OversizedPage_NeverReachesTheRenderer()
    {
        using var doc = OversizedDocument();
        PdfiumDiagnostics.Reset();

        Assert.Throws<InvalidOperationException>(() => doc.SaveAsTiff(new MemoryStream(), 300));

        // Only the first (letter) page was rendered; the oversized one was refused before PDFium saw it.
        int renders = PdfiumDiagnostics.Snapshot().Events.Count(e => e.IsNative && e.NativeOp == NativeOp.Render);
        Assert.Equal(1, renders);
    }

    [Fact]
    public void RenderToBytes_ChecksSizeAndLimit()
    {
        using var doc = new PdfDocument(OnePagePdf);
        using var page = doc.GetPage(0);

        Assert.Throws<ArgumentOutOfRangeException>(() => page.RenderToBytes(0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => page.RenderToBytes(10, -1));

        var ex = Assert.Throws<InvalidOperationException>(() => page.RenderToBytes(20_000, 20_000));
        Assert.Contains("page index 0", ex.Message);
        Assert.Contains(RenderLimits.MaxPixelsKey, ex.Message);

        // Small sizes still render: one BGRx pixel per row, four rows.
        Assert.Equal(4 * 4, page.RenderToBytes(1, 4).Length);
    }

    [Fact]
    public void RenderLimit_IsConfigurableThroughAppContext()
    {
        using var doc = new PdfDocument(OnePagePdf);
        using var page = doc.GetPage(0);

        AppContext.SetData(RenderLimits.MaxPixelsKey, 10_000L);
        Assert.Throws<InvalidOperationException>(() => doc.RenderPages(72));
        Assert.Equal(100 * 100 * 4, page.RenderToBytes(100, 100).Length);
        Assert.Throws<InvalidOperationException>(() => page.RenderToBytes(101, 100));

        AppContext.SetData(RenderLimits.MaxPixelsKey, "20000");
        Assert.Equal(200 * 100 * 4, page.RenderToBytes(200, 100).Length);

        // Not a positive number: the default applies.
        AppContext.SetData(RenderLimits.MaxPixelsKey, "nonsense");
        Assert.Equal(RenderLimits.DefaultMaxPixels, RenderLimits.MaxPixels);
        Assert.Single(doc.RenderPages(72));
    }

    [Fact]
    public void BitmapThatPdfiumCannotCreate_IsAnInvalidOperation_NotOutOfMemory()
    {
        using var doc = new PdfDocument(OnePagePdf);
        using var page = doc.GetPage(0);
        AppContext.SetData(RenderLimits.MaxPixelsKey, long.MaxValue);

        // 32 bits x int.MaxValue overflows PDFium's 32-bit pitch, so it refuses without allocating.
        var ex = Assert.Throws<InvalidOperationException>(() => page.RenderToBytes(int.MaxValue, 1));
        Assert.Contains("could not create", ex.Message);
    }

    [Fact]
    public async Task NonPositiveDpi_IsRejectedBeforeAnyOutput()
    {
        using var doc = new PdfDocument(OnePagePdf);
        string directory = Path.Combine(CreateTempDirectory(), "never-created");

        Assert.Throws<ArgumentOutOfRangeException>(() => doc.RenderPages(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.RenderPages(72, -72));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.StreamImageBytes(ImageFormat.Png, 90, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.StreamImageBytesAsync(ImageFormat.Png, 90, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.StreamJpegBytes(90, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.StreamJpegBytesAsync(90, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.SaveAsTiff(new MemoryStream(), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.SaveAsPngs(directory, dpi: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.SaveAsJpegs(directory, dpi: -5));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.SaveAsImages(new Stream[] { new MemoryStream() }, ImageFormat.Png, 90, 0, 72));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => doc.RenderPagesAsync(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => doc.SaveAsTiffAsync(new MemoryStream(), 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => doc.SaveAsPngsAsync(directory, dpi: 0));

        Assert.False(Directory.Exists(directory));
    }

    #endregion

    #region Outputs, names and defaults (AUD-006, AUD-013)

    [Fact]
    public void PageFileName_IsTheDocumentedContract()
    {
        Assert.Equal("page_001.png", PdfDocument.PageFileName(null, 1, ImageFormat.Png));
        Assert.Equal("scan_012.jpg", PdfDocument.PageFileName("scan", 12, ImageFormat.Jpeg));
        Assert.Equal("scan_1234.jpg", PdfDocument.PageFileName("scan", 1234, ImageFormat.Jpeg));
        Assert.Equal("page_*.png", PdfDocument.PageFileSearchPattern(null, ImageFormat.Png));
    }

    [Fact]
    public async Task FileOutputs_KeepNonAsciiDirectoriesAndPrefixesExactly()
    {
        // libtiff's TIFFOpen and the PNG shim's fopen read char* paths in the ANSI code page on
        // Windows; every output file is now opened in managed code instead.
        string root = CreateTempDirectory("_é中");
        string directory = Path.Combine(root, "sortie_é中");
        const string prefix = "café";

        using (var doc = new PdfDocument(ContractPdf))
        {
            doc.SaveAsPngs(directory, prefix, dpi: 36);
            doc.SaveAsJpegs(directory, prefix, dpi: 36);
            doc.SaveAsTiff(Path.Combine(directory, $"{prefix}.tiff"), 36);
            await doc.SaveAsPngsAsync(Path.Combine(directory, "async"), prefix, dpi: 36);
            await doc.SaveAsTiffAsync(Path.Combine(directory, "async", $"{prefix}.tiff"), 36);

            int pages = doc.PageCount;
            var expected = Enumerable.Range(1, pages)
                .SelectMany(n => new[] { $"café_{n:D3}.png", $"café_{n:D3}.jpg" })
                .Append("café.tiff")
                .Order(StringComparer.Ordinal)
                .ToArray();

            var written = Directory.GetFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(expected, written);

            var writtenAsync = Directory.GetFiles(Path.Combine(directory, "async")).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(Enumerable.Range(1, pages).Select(n => $"café_{n:D3}.png").Append("café.tiff").Order(StringComparer.Ordinal), writtenAsync);
        }

        // Nothing stray (a garbled name) next to the output directory either.
        Assert.Equal(new[] { "sortie_é中" }, Directory.GetFileSystemEntries(root).Select(Path.GetFileName));

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, File.ReadAllBytes(Path.Combine(directory, "café_001.png"))[..4]);
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, File.ReadAllBytes(Path.Combine(directory, "café_001.jpg"))[..2]);
        var tiff = File.ReadAllBytes(Path.Combine(directory, "café.tiff"));
        Assert.True(tiff.AsSpan(0, 4).SequenceEqual("II*\0"u8) || tiff.AsSpan(0, 4).SequenceEqual("MM\0*"u8));
    }

    [Fact]
    public async Task PoolJobs_WriteNonAsciiNamesExactly_AndLeaveNothingStray()
    {
        string root = CreateTempDirectory("_é中");
        string input = PoolFixture.Input("doc-3-pages-with-comments.pdf");

        await using var pool = await PdfProcessingPool.CreateAsync(PoolFixture.Options()).WaitAsync(PoolFixture.TestTimeout);

        var tiff = await pool.ConvertToTiffAsync(input, Path.Combine(root, "Rechnung_März.tiff"), dpi: 50).WaitAsync(PoolFixture.TestTimeout);
        Assert.True(tiff.IsSuccess, tiff.Error);

        var png = await pool.ConvertToPngAsync(input, Path.Combine(root, "png"), dpi: 50, fileNamePrefix: "café").WaitAsync(PoolFixture.TestTimeout);
        Assert.True(png.IsSuccess, png.Error);

        Assert.Equal(new[] { "Rechnung_März.tiff", "png" },
            Directory.GetFileSystemEntries(root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "café_001.png", "café_002.png", "café_003.png" },
            Directory.GetFiles(Path.Combine(root, "png")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(Path.Combine(root, "Rechnung_März.tiff"), tiff.Value!.Path);
    }

    [Fact]
    public void JpegQuality_DefaultsTo90_OnEveryEntryPoint()
    {
        using var doc = new PdfDocument(OnePagePdf);
        string directory = CreateTempDirectory();

        byte[] at90 = doc.StreamImageBytes(ImageFormat.Jpeg, 90, 72).Single();
        Assert.NotEqual(at90, doc.StreamImageBytes(ImageFormat.Jpeg, 100, 72).Single());

        Assert.Equal(at90, doc.StreamImageBytes(ImageFormat.Jpeg, dpi: 72).Single());
        Assert.Equal(at90, doc.StreamJpegBytes(dpi: 72).Single());

        doc.SaveAsJpegs(Path.Combine(directory, "jpegs"), dpi: 72);
        Assert.Equal(at90, File.ReadAllBytes(Path.Combine(directory, "jpegs", "page_001.jpg")));

        doc.SaveAsImages(Path.Combine(directory, "images"), "page", ImageFormat.Jpeg, dpi: 72);
        Assert.Equal(at90, File.ReadAllBytes(Path.Combine(directory, "images", "page_001.jpg")));
    }

    [Fact]
    public async Task JpegQuality_DefaultsTo90_OnAsyncEntryPoints()
    {
        using var doc = new PdfDocument(OnePagePdf);
        string directory = CreateTempDirectory();
        byte[] at90 = doc.StreamImageBytes(ImageFormat.Jpeg, 90, 72).Single();

        var streamed = new List<byte[]>();
        await foreach (var bytes in doc.StreamImageBytesAsync(ImageFormat.Jpeg, dpi: 72))
            streamed.Add(bytes);
        Assert.Equal(at90, Assert.Single(streamed));

        await doc.SaveAsImagesAsync(directory, "page", ImageFormat.Jpeg, dpi: 72);
        Assert.Equal(at90, File.ReadAllBytes(Path.Combine(directory, "page_001.jpg")));
    }

    [Fact]
    public async Task EveryOutputShape_WritesTheSameBytes()
    {
        using var doc = new PdfDocument(ContractPdf);
        string directory = CreateTempDirectory();
        var reference = doc.StreamImageBytes(ImageFormat.Png, 90, 36).ToArray();

        doc.SaveAsPngs(Path.Combine(directory, "sync"), dpi: 36);
        await doc.SaveAsPngsAsync(Path.Combine(directory, "async"), dpi: 36);
        var streams = reference.Select(_ => new MemoryStream()).ToArray<Stream>();
        doc.SaveAsImages(streams, ImageFormat.Png, 90, 36, 36);
        var asyncStreams = reference.Select(_ => new MemoryStream()).ToArray<Stream>();
        await doc.SaveAsImagesAsync(asyncStreams, ImageFormat.Png, 90, 36, 36);

        for (int i = 0; i < reference.Length; i++)
        {
            string name = PdfDocument.PageFileName("page", i + 1, ImageFormat.Png);
            Assert.Equal(reference[i], File.ReadAllBytes(Path.Combine(directory, "sync", name)));
            Assert.Equal(reference[i], File.ReadAllBytes(Path.Combine(directory, "async", name)));
            Assert.Equal(reference[i], ((MemoryStream)streams[i]).ToArray());
            Assert.Equal(reference[i], ((MemoryStream)asyncStreams[i]).ToArray());
        }
    }

    [Fact]
    public async Task SaveAsImages_ToStreams_ValidatesLikeEveryOtherImageApi()
    {
        using (var empty = new PdfDocument())
        {
            Assert.Throws<InvalidOperationException>(() => empty.SaveAsImages(Array.Empty<Stream>(), ImageFormat.Png, 90, 72, 72));
            await Assert.ThrowsAsync<InvalidOperationException>(() => empty.SaveAsImagesAsync(Array.Empty<Stream>(), ImageFormat.Png, 90, 72, 72));
        }

        using var doc = new PdfDocument(OnePagePdf);
        Assert.Throws<ArgumentNullException>(() => doc.SaveAsImages(null!, ImageFormat.Png, 90, 72, 72));
        Assert.Throws<ArgumentException>(() => doc.SaveAsImages(new Stream[] { null! }, ImageFormat.Png, 90, 72, 72));
        Assert.Throws<ArgumentException>(() => doc.SaveAsImages(new Stream[2] { new MemoryStream(), new MemoryStream() }, ImageFormat.Png, 90, 72, 72));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.SaveAsImages(new Stream[] { new MemoryStream() }, ImageFormat.Tiff, 90, 72, 72));
        await Assert.ThrowsAsync<ArgumentNullException>(() => doc.SaveAsImagesAsync(null!, ImageFormat.Png, 90, 72, 72));
    }

    [Fact]
    public async Task SaveAsImages_Tiff_IsRejectedBeforeTheDirectoryIsCreated()
    {
        using var doc = new PdfDocument(OnePagePdf);
        string directory = Path.Combine(CreateTempDirectory(), "never-created");

        Assert.Throws<ArgumentOutOfRangeException>(() => doc.SaveAsImages(directory, "page", ImageFormat.Tiff));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => doc.SaveAsImagesAsync(directory, "page", ImageFormat.Tiff));
        Assert.False(Directory.Exists(directory));
    }

    #endregion

    #region Async under a blocked synchronization context (AUD-012)

    /// <summary>
    /// The context of a UI thread that is blocked in <c>.Wait()</c>: whatever is posted to it
    /// never runs. Counts posts so the test can show the library never used it.
    /// </summary>
    private sealed class BlockedContext : SynchronizationContext
    {
        public int Posts;

        public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref Posts);

        public override void Send(SendOrPostCallback d, object? state)
            => throw new InvalidOperationException("The blocked thread cannot run a sent callback.");
    }

    [Fact]
    public void AsyncMethods_CompleteWhileTheCallersContextIsBlocked()
    {
        string directory = CreateTempDirectory();
        var timeout = TimeSpan.FromSeconds(60);
        var failures = new List<string>();
        int posts = -1;
        Exception? crash = null;

        var thread = new Thread(() =>
        {
            try
            {
                var context = new BlockedContext();
                SynchronizationContext.SetSynchronizationContext(context);
                using var doc = new PdfDocument(ContractPdf);

                var operations = new (string Name, Func<Task> Start)[]
                {
                    ("RenderPagesAsync", () => doc.RenderPagesAsync(36)),
                    ("SaveAsTiffAsync(Stream)", () => doc.SaveAsTiffAsync(new MemoryStream(), 36)),
                    ("SaveAsTiffAsync(string)", () => doc.SaveAsTiffAsync(Path.Combine(directory, "doc.tiff"), 36)),
                    ("SaveAsJpegsAsync", () => doc.SaveAsJpegsAsync(Path.Combine(directory, "jpg"), dpi: 36)),
                    ("SaveAsPngsAsync", () => doc.SaveAsPngsAsync(Path.Combine(directory, "png"), dpi: 36)),
                    ("SaveAsImagesAsync", () => doc.SaveAsImagesAsync(Path.Combine(directory, "img"), "p", ImageFormat.Png, 90, 36, 36)),
                    ("ProcessAllPagesAsync<T>", () => doc.ProcessAllPagesAsync(page => page.PageIndex)),
                    ("ProcessAllPagesAsync", () => doc.ProcessAllPagesAsync(_ => { })),
                    ("StreamImageBytesAsync", () => Drain(doc.StreamImageBytesAsync(ImageFormat.Png, 90, 36))),
                    ("StreamJpegBytesAsync", () => Drain(doc.StreamJpegBytesAsync(90, 36))),
                };

                foreach (var (name, start) in operations)
                {
                    // .Wait() on the thread that owns the context: Task.Yield would post the
                    // continuation here and it would never run.
                    if (!start().Wait(timeout))
                        failures.Add(name);
                }

                // The same with the gate busy (held by this very thread), so the method has to
                // wait for it and is resumed by whoever releases it.
                Task contended;
                using (PdfiumRuntime.Enter())
                {
                    contended = doc.RenderPagesAsync(36);
                    Thread.Sleep(100);
                }
                if (!contended.Wait(timeout))
                    failures.Add("RenderPagesAsync with the gate busy");

                posts = context.Posts;
            }
            catch (Exception ex)
            {
                crash = ex;
            }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(5)), "the test thread hung");

        Assert.Null(crash);
        Assert.Empty(failures);
        Assert.Equal(0, posts);

        static async Task Drain(IAsyncEnumerable<byte[]> pages)
        {
            await foreach (var _ in pages.ConfigureAwait(false)) { }
        }
    }

    #endregion

    #region Embedded thumbnails (AUD-017)

    [Fact]
    public void GetEmbeddedThumbnail_ExpandsGrayToBgra_InOneCall()
    {
        // 3 x 2 DeviceGray, 8 bits per component.
        byte[] gray = { 0, 64, 128, 192, 255, 7 };
        using var doc = new PdfDocument(BuildPdfWithThumbnail("/DeviceGray", 3, 2, gray));
        using var page = doc.GetPage(0);
        long liveBefore = PdfiumRuntime.LiveHandleCount;

        Assert.True(page.HasEmbeddedThumbnail);
        var thumbnail = page.GetEmbeddedThumbnail();

        Assert.NotNull(thumbnail);
        Assert.Equal(3, thumbnail.Width);
        Assert.Equal(2, thumbnail.Height);
        Assert.Equal(3 * 4, thumbnail.Stride);
        var expected = gray.SelectMany(g => new[] { g, g, g, (byte)255 }).ToArray();
        Assert.Equal(expected, thumbnail.Pixels);
        Assert.Equal(liveBefore, PdfiumRuntime.LiveHandleCount);

#pragma warning disable CS0618 // the obsolete pair now reports the same BGRA pixels and size
        Assert.Equal(expected, page.GetEmbeddedThumbnailBytes());
        Assert.Equal((3, 2), page.GetEmbeddedThumbnailSize());
#pragma warning restore CS0618
    }

    [Fact]
    public void GetEmbeddedThumbnail_ConvertsRgbToBgra()
    {
        // 2 x 1 DeviceRGB: pure red, then (10, 20, 30).
        byte[] rgb = { 255, 0, 0, 10, 20, 30 };
        using var doc = new PdfDocument(BuildPdfWithThumbnail("/DeviceRGB", 2, 1, rgb));
        using var page = doc.GetPage(0);

        var thumbnail = page.GetEmbeddedThumbnail();

        Assert.NotNull(thumbnail);
        Assert.Equal(new byte[] { 0, 0, 255, 255, 30, 20, 10, 255 }, thumbnail.Pixels);
    }

    [Fact]
    public void PageWithoutThumbnail_ReportsNone()
    {
        using var doc = new PdfDocument(OnePagePdf);
        using var page = doc.GetPage(0);
        long liveBefore = PdfiumRuntime.LiveHandleCount;

        Assert.False(page.HasEmbeddedThumbnail);
        Assert.Null(page.GetEmbeddedThumbnail());
        Assert.Equal(liveBefore, PdfiumRuntime.LiveHandleCount);
    }

    /// <summary>One 200 x 200 page whose /Thumb is an unfiltered image of the given colour space.</summary>
    [Fact]
    public void HasEmbeddedThumbnail_MeasuresTheStoredStream()
    {
        // A 64 MiB thumbnail stored as a few dozen KB of Flate data. The presence check holds the
        // process-wide gate, so it measures the stored stream instead of inflating it. The cost
        // difference (about 45 ms per check when inflated) is not asserted here; the results are.
        byte[] compressed;
        using (var buffer = new MemoryStream())
        {
            using (var zlib = new System.IO.Compression.ZLibStream(buffer, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
                zlib.Write(new byte[64 * 1024 * 1024]);
            compressed = buffer.ToArray();
        }

        using var doc = new PdfDocument(BuildPdfWithThumbnail("/DeviceGray", 8192, 8192, compressed, " /Filter /FlateDecode"));
        using var page = doc.GetPage(0);
        Assert.True(page.HasEmbeddedThumbnail);

        // A filter PDFium cannot run is still a thumbnail that is present
        using var undecodable = new PdfDocument(BuildPdfWithThumbnail("/DeviceGray", 3, 2, [1, 2, 3, 4, 5, 6], " /Filter /NoSuchDecode"));
        using var undecodablePage = undecodable.GetPage(0);
        Assert.True(undecodablePage.HasEmbeddedThumbnail);
    }

    private static byte[] BuildPdfWithThumbnail(string colorSpace, int width, int height, byte[] samples, string filter = "")
    {
        var output = new MemoryStream();
        var offsets = new List<long>();

        void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));

        void Object(string dictionary, byte[]? stream = null)
        {
            offsets.Add(output.Position);
            Write($"{offsets.Count} 0 obj\n{dictionary}\n");
            if (stream != null)
            {
                Write("stream\n");
                output.Write(stream);
                Write("\nendstream\n");
            }
            Write("endobj\n");
        }

        Write("%PDF-1.7\n");
        Object("<< /Type /Catalog /Pages 2 0 R >>");
        Object("<< /Type /Pages /MediaBox [0 0 200 200] /Count 1 /Kids [3 0 R] >>");
        Object("<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Thumb 5 0 R >>");
        Object("<< /Length 0 >>", Array.Empty<byte>());
        Object($"<< /Width {width} /Height {height} /BitsPerComponent 8 /ColorSpace {colorSpace}{filter} /Length {samples.Length} >>", samples);

        long xref = output.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (long offset in offsets)
            Write($"{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    #endregion
}
