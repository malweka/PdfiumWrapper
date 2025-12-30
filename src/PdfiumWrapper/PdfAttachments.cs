using System.Runtime.InteropServices;

namespace PdfiumWrapper;

/// <summary>
/// PDF attachments (embedded files)
/// </summary>
public class PdfAttachments
{
    private IntPtr _document;

    internal PdfAttachments(IntPtr document)
    {
        _document = document;
    }

    /// <summary>
    /// Get the number of attachments
    /// </summary>
    public int Count => PDFium.FPDFDoc_GetAttachmentCount(_document);

    /// <summary>
    /// Get all attachments
    /// </summary>
    public List<PdfAttachment> GetAllAttachments()
    {
        var attachments = new List<PdfAttachment>();
        int count = Count;

        for (int i = 0; i < count; i++)
        {
            var attachment = GetAttachment(i);
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
        var attachmentHandle = PDFium.FPDFDoc_GetAttachment(_document, index);
        if (attachmentHandle == IntPtr.Zero)
            return null;

        var attachment = new PdfAttachment();

        // Get name
        ulong nameLength = PDFium.FPDFAttachment_GetName(attachmentHandle, IntPtr.Zero, 0);
        if (nameLength > 0)
        {
            var nameBuffer = Marshal.AllocHGlobal((int)nameLength);
            try
            {
                PDFium.FPDFAttachment_GetName(attachmentHandle, nameBuffer, nameLength);
                attachment.Name = Marshal.PtrToStringUni(nameBuffer) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(nameBuffer);
            }
        }

        // Get file size
        PDFium.FPDFAttachment_GetFile(attachmentHandle, IntPtr.Zero, 0, out ulong fileSize);
        attachment.Size = (long)fileSize;

        // Get file data
        if (fileSize > 0)
        {
            var dataBuffer = Marshal.AllocHGlobal((int)fileSize);
            try
            {
                if (PDFium.FPDFAttachment_GetFile(attachmentHandle, dataBuffer, fileSize, out _))
                {
                    attachment.Data = new byte[fileSize];
                    Marshal.Copy(dataBuffer, attachment.Data, 0, (int)fileSize);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(dataBuffer);
            }
        }

        return attachment;
    }

    /// <summary>
    /// Extract all attachments to a directory
    /// </summary>
    public void ExtractAll(string outputDirectory)
    {
        if (!Directory.Exists(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        var attachments = GetAllAttachments();
        foreach (var attachment in attachments)
        {
            string filePath = Path.Combine(outputDirectory, attachment.Name);
            File.WriteAllBytes(filePath, attachment.Data);
        }
    }
}