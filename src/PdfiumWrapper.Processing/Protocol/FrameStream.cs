using System.Buffers.Binary;
using System.Text.Json;

namespace PdfiumWrapper.Processing.Protocol;

/// <summary>Thrown when the other side sends something that is not a frame.</summary>
internal sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }

    public ProtocolException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Frames on a byte stream: a 4-byte little-endian length followed by that many bytes of UTF-8 JSON.
/// </summary>
internal static class FrameStream
{
    /// <summary>Largest frame either side accepts. Text results above <see cref="InlineTextLimit"/> go through a file instead.</summary>
    public const int MaxFrameBytes = 16 * 1024 * 1024;

    /// <summary>Extracted text up to this many bytes is sent inline in the Result frame.</summary>
    public const int InlineTextLimit = 4 * 1024 * 1024;

    public static async Task WriteAsync(Stream stream, Frame frame, SemaphoreSlim writeLock, CancellationToken ct)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(frame, FrameJsonContext.Default.Frame);
        if (body.Length > MaxFrameBytes)
            throw new ProtocolException($"Frame of {body.Length} bytes exceeds the {MaxFrameBytes} byte limit.");

        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, body.Length);

        await writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(prefix, ct).ConfigureAwait(false);
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    /// <summary>Reads one frame. Returns null at a clean end of stream (no bytes of a frame read).</summary>
    public static async Task<Frame?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var prefix = new byte[4];
        int read = await ReadFullyAsync(stream, prefix, ct).ConfigureAwait(false);
        if (read == 0)
            return null;
        if (read < 4)
            throw new ProtocolException("The stream ended inside a frame length prefix.");

        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > MaxFrameBytes)
            throw new ProtocolException($"Frame length {length} is outside 1..{MaxFrameBytes}.");

        var body = new byte[length];
        read = await ReadFullyAsync(stream, body, ct).ConfigureAwait(false);
        if (read < length)
            throw new ProtocolException($"The stream ended after {read} of {length} frame bytes.");

        try
        {
            return JsonSerializer.Deserialize(body, FrameJsonContext.Default.Frame)
                   ?? throw new ProtocolException("Frame deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new ProtocolException("Frame is not valid JSON: " + ex.Message, ex);
        }
    }

    private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0)
                break;
            total += n;
        }

        return total;
    }
}
