namespace PdfiumWrapper;

internal static class PdfHelpers
{
    private const int SaveFileBufferSize = 128 * 1024;

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
