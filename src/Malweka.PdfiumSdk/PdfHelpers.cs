namespace Malweka.PdfiumSdk;

public static class PdfHelpers
{
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
}