using System.Buffers;

namespace PdfiumWrapper;

/// <summary>
/// A caller's stream, read to its end before the native gate is entered, so that a slow or
/// network stream never stalls every other PDFium caller in the process.
/// Small inputs are held in memory; larger ones go to a temp file that PDFium reads directly.
/// </summary>
internal sealed class SpooledInput
{
    /// <summary><see cref="AppContext"/> data key overriding <see cref="DefaultMemoryThreshold"/> (a number of bytes).</summary>
    public const string ThresholdKey = "PdfiumWrapper.SpoolThreshold";

    public const long DefaultMemoryThreshold = 64L * 1024 * 1024;

    private SpooledInput(byte[] buffer, int offset, int length)
    {
        Buffer = buffer;
        Offset = offset;
        Length = length;
    }

    private SpooledInput(string tempPath) => TempPath = tempPath;

    /// <summary>Set when the input is held in memory.</summary>
    public byte[]? Buffer { get; }

    public int Offset { get; }

    public int Length { get; }

    /// <summary>Set when the input was spooled to a temp file. The owner deletes it after closing the document.</summary>
    public string? TempPath { get; }

    public static long MemoryThreshold => AppContext.GetData(ThresholdKey) switch
    {
        long value => value,
        int value => value,
        string text when long.TryParse(text, out long value) => value,
        _ => DefaultMemoryThreshold,
    };

    /// <summary>
    /// Read <paramref name="stream"/> from its current position to its end. Runs entirely outside the gate.
    /// </summary>
    public static SpooledInput From(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // A MemoryStream with an exposable buffer is used in place, without a copy.
        if (stream is MemoryStream memoryStream && memoryStream.TryGetBuffer(out ArraySegment<byte> segment))
        {
            int offset = segment.Offset + checked((int)memoryStream.Position);
            int length = checked((int)(memoryStream.Length - memoryStream.Position));
            memoryStream.Position = memoryStream.Length;
            return new SpooledInput(segment.Array!, offset, length);
        }

        if (!stream.CanRead)
            throw new ArgumentException("Stream must be readable.", nameof(stream));

        long threshold = Math.Min(MemoryThreshold, Array.MaxLength);

        if (stream.CanSeek)
        {
            long remaining = stream.Length - stream.Position;
            if (remaining <= threshold)
            {
                var bytes = new byte[remaining];
                stream.ReadExactly(bytes);
                return new SpooledInput(bytes, 0, bytes.Length);
            }

            return ToTempFile(stream, buffered: null);
        }

        // Unknown length: buffer until the threshold is crossed, then spill.
        var memory = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                memory.Write(chunk, 0, read);
                if (memory.Length > threshold)
                    return ToTempFile(stream, memory);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        memory.TryGetBuffer(out var buffered);
        return new SpooledInput(buffered.Array ?? Array.Empty<byte>(), buffered.Offset, checked((int)memory.Length));
    }

    private static SpooledInput ToTempFile(Stream stream, MemoryStream? buffered)
    {
        string path = Path.Combine(Path.GetTempPath(), "pdfium-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 1 << 16, FileOptions.SequentialScan);
            buffered?.WriteTo(file);
            stream.CopyTo(file);
        }
        catch
        {
            TryDelete(path);
            throw;
        }

        return new SpooledInput(path);
    }

    /// <summary>Best-effort delete. Never throws: it also runs while draining finalizer releases.</summary>
    public static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still open elsewhere or already gone; the temp directory is cleaned by the OS.
        }
    }
}
