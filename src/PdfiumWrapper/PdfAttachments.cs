using System.Runtime.InteropServices;

namespace PdfiumWrapper;

/// <summary>
/// PDF attachments (embedded files)
/// </summary>
public class PdfAttachments
{
    private readonly PdfDocument _owner;

    internal PdfAttachments(PdfDocument owner)
    {
        _owner = owner;
    }

    /// <summary>
    /// Get the number of attachments
    /// </summary>
    public int Count
    {
        get
        {
            using var _ = PdfiumRuntime.Enter();
            _owner.ThrowIfDisposed();
            return PDFium.FPDFDoc_GetAttachmentCount(_owner.Document);
        }
    }

    /// <summary>
    /// Get all attachments
    /// </summary>
    public List<PdfAttachment> GetAllAttachments()
    {
        using var _ = PdfiumRuntime.Enter();
        _owner.ThrowIfDisposed();

        var attachments = new List<PdfAttachment>();
        int count = PDFium.FPDFDoc_GetAttachmentCount(_owner.Document);

        for (int i = 0; i < count; i++)
        {
            var attachment = GetAttachmentCore(i);
            if (attachment != null)
                attachments.Add(attachment);
        }

        return attachments;
    }

    /// <summary>
    /// Get attachment by index
    /// </summary>
    public PdfAttachment? GetAttachment(int index)
    {
        using var _ = PdfiumRuntime.Enter();
        _owner.ThrowIfDisposed();
        return GetAttachmentCore(index);
    }

    private PdfAttachment? GetAttachmentCore(int index)
    {
        var attachmentHandle = PDFium.FPDFDoc_GetAttachment(_owner.Document, index);
        if (attachmentHandle == IntPtr.Zero)
            return null;

        var attachment = new PdfAttachment();

        var name = NativeText.ReadUtf16(attachmentHandle, static (handle, buffer, length) => PDFium.FPDFAttachment_GetName(handle, buffer, length));
        if (name != null)
            attachment.Name = name;

        // Get file size
        PDFium.FPDFAttachment_GetFile(attachmentHandle, IntPtr.Zero, default, out CULong fileLength);
        ulong fileSize = fileLength.Value;
        attachment.Size = (long)fileSize;

        // The size comes from the document, so check it before it becomes an array length.
        if (fileSize > (ulong)Array.MaxLength)
            throw new InvalidDataException($"Attachment {index} reports {fileSize} bytes, more than an array can hold.");

        if (fileSize > 0)
        {
            var data = new byte[fileSize];
            unsafe
            {
                fixed (byte* dataPtr = data)
                {
                    if (PDFium.FPDFAttachment_GetFile(attachmentHandle, (IntPtr)dataPtr, fileLength, out _))
                        attachment.Data = data;
                }
            }
        }

        return attachment;
    }

    /// <summary>
    /// Extract all attachments to a directory. Each file is named after the last path
    /// component of its attachment name, so nothing is written outside the directory;
    /// an attachment without a usable name is written as attachment_N (N from 1).
    /// </summary>
    public void ExtractAll(string outputDirectory)
    {
        // The native read happens under the gate; the file writes run after it is released.
        var attachments = GetAllAttachments();

        if (!Directory.Exists(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        // Names that differ only in case, or that Windows would reduce to the same file, would
        // overwrite each other; later ones get "_2", "_3" before the extension.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < attachments.Count; i++)
        {
            string fileName = SafeFileName(attachments[i].Name, i);
            string stem = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            for (int suffix = 2; !used.Add(fileName); suffix++)
                fileName = $"{stem}_{suffix}{extension}";

            File.WriteAllBytes(Path.Combine(outputDirectory, fileName), attachments[i].Data ?? []);
        }
    }

    // Attachment names come from the document. Path.Combine would follow "..\" segments
    // and return an absolute name unchanged, so keep only the last component of either
    // separator style and replace characters the file system rejects (":" would open an
    // alternate data stream on Windows). On macOS PDFium returns "/" in a name as ":", the
    // classic Mac separator, so ":" is a separator there too.
    private static string SafeFileName(string? name, int index)
    {
        string normalized = (name ?? string.Empty).Replace('\\', '/');
        if (OperatingSystem.IsMacOS())
            normalized = normalized.Replace(':', '/');

        string fileName = Path.GetFileName(normalized);
        foreach (char c in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(c, '_');

        // Windows drops trailing dots and spaces, so "a." and "a " would both write "a".
        // Empty, ".", ".." and names of only dots or spaces would name the directory itself
        // or its parent.
        fileName = fileName.TrimEnd('.', ' ');
        if (fileName.Trim('.', ' ').Length == 0)
            return $"attachment_{index + 1}";

        // A device name ("CON", "nul.txt", "COM1.log") opens the device instead of a file on Windows
        return IsReservedDeviceName(fileName) ? "_" + fileName : fileName;
    }

    private static bool IsReservedDeviceName(string fileName)
    {
        int dot = fileName.IndexOf('.');
        string stem = (dot < 0 ? fileName : fileName[..dot]).TrimEnd(' ');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            return true;

        return stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && (char.IsAsciiDigit(stem[3]) || stem[3] is '¹' or '²' or '³');
    }
}
