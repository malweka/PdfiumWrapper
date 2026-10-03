using System.Buffers.Binary;
using PdfiumWrapper.Processing.Protocol;

namespace PdfiumWrapper.Tests.Processing;

[Collection(PdfTestCollection.Name)]
public class FrameStreamTests
{
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    [Fact]
    public async Task RoundTrip_PreservesEveryField()
    {
        var job = new JobPayload
        {
            Id = 42, Kind = JobKind.ConvertToTiff, Input = @"C:\in\a b.pdf", Output = "/out/a.tiff",
            FileNamePrefix = "p", DpiWidth = 200, DpiHeight = 100, Quality = 90, ColorMode = TiffColorMode.Grayscale,
            Threshold = 200, Password = "pw",
        };
        var stream = new MemoryStream();

        await FrameStream.WriteAsync(stream, Frame.ForJob(job), WriteLock, CancellationToken.None);
        await FrameStream.WriteAsync(stream, Frame.ForResult(new ResultPayload
        {
            JobId = 42, Status = ResultStatus.Succeeded, PageCount = 3, Files = new[] { "a", "b" },
            OutputBytes = 7, Text = new[] { "x" }, ProcessingMs = 1.5, RenderIntervalsUtcTicks = new[] { new long[] { 1, 2 } },
        }), WriteLock, CancellationToken.None);
        await FrameStream.WriteAsync(stream, Frame.ForShutdown(), WriteLock, CancellationToken.None);
        stream.Position = 0;

        var first = await FrameStream.ReadAsync(stream, CancellationToken.None);
        Assert.Equal(FrameKind.Job, first!.Kind);
        var read = first.Job!;
        Assert.Equal((job.Id, job.Kind, job.Input, job.Output, job.FileNamePrefix, job.DpiWidth, job.DpiHeight, job.Quality, job.ColorMode, job.Threshold, job.Password),
            (read.Id, read.Kind, read.Input, read.Output, read.FileNamePrefix, read.DpiWidth, read.DpiHeight, read.Quality, read.ColorMode, read.Threshold, read.Password));

        var second = await FrameStream.ReadAsync(stream, CancellationToken.None);
        Assert.Equal(FrameKind.Result, second!.Kind);
        Assert.Equal(new[] { "a", "b" }, second.Result!.Files);
        Assert.Equal(1.5, second.Result.ProcessingMs);
        Assert.Equal(2, second.Result.RenderIntervalsUtcTicks![0][1]);

        var third = await FrameStream.ReadAsync(stream, CancellationToken.None);
        Assert.Equal(FrameKind.Shutdown, third!.Kind);

        Assert.Null(await FrameStream.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task TruncatedPrefix_IsAProtocolError()
    {
        var stream = new MemoryStream(new byte[] { 1, 2 });
        await Assert.ThrowsAsync<ProtocolException>(() => FrameStream.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task TruncatedBody_IsAProtocolError()
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, 100);
        var stream = new MemoryStream(prefix.Concat(new byte[10]).ToArray());
        await Assert.ThrowsAsync<ProtocolException>(() => FrameStream.ReadAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(FrameStream.MaxFrameBytes + 1)]
    public async Task LengthOutsideTheLimit_IsAProtocolError(int length)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
        var stream = new MemoryStream(prefix);
        await Assert.ThrowsAsync<ProtocolException>(() => FrameStream.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task BodyThatIsNotJson_IsAProtocolError()
    {
        var body = "not json"u8.ToArray();
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, body.Length);
        var stream = new MemoryStream(prefix.Concat(body).ToArray());
        await Assert.ThrowsAsync<ProtocolException>(() => FrameStream.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task UnknownKind_IsAProtocolError()
    {
        var body = "{\"Kind\":\"Teleport\"}"u8.ToArray();
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, body.Length);
        var stream = new MemoryStream(prefix.Concat(body).ToArray());
        await Assert.ThrowsAsync<ProtocolException>(() => FrameStream.ReadAsync(stream, CancellationToken.None));
    }
}
