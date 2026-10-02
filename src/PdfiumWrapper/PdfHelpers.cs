namespace PdfiumWrapper;

public static class PdfHelpers
{
    private const int SaveFileBufferSize = 128 * 1024;

    public static byte[] ReadStreamToBytes(this Stream stream)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        if (stream is MemoryStream ms)
        {
            return ms.ToArray();
        }

        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    public static FileStream OpenWriteFileStream(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        return new FileStream(
            filePath,
            new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.Create,
                Share = FileShare.None,
                BufferSize = SaveFileBufferSize
            });
    }
}
