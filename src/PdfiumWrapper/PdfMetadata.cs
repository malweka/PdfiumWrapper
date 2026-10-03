namespace PdfiumWrapper;

/// <summary>
/// PDF document metadata and properties
/// </summary>
public class PdfMetadata
{
    private readonly PdfDocument _owner;

    internal PdfMetadata(PdfDocument owner)
    {
        _owner = owner;
    }

    /// <summary>The document handle. The native gate must be held.</summary>
    private IntPtr Handle
    {
        get
        {
            PdfiumRuntime.AssertHeld();
            _owner.ThrowIfDisposed();
            return _owner.Document;
        }
    }

    /// <summary>
    /// Document title
    /// </summary>
    public string Title => GetMetadataString(PDFium.METADATA_TITLE);

    /// <summary>
    /// Document author
    /// </summary>
    public string Author => GetMetadataString(PDFium.METADATA_AUTHOR);

    /// <summary>
    /// Document subject
    /// </summary>
    public string Subject => GetMetadataString(PDFium.METADATA_SUBJECT);

    /// <summary>
    /// Document keywords
    /// </summary>
    public string Keywords => GetMetadataString(PDFium.METADATA_KEYWORDS);

    /// <summary>
    /// Application that created the original document
    /// </summary>
    public string Creator => GetMetadataString(PDFium.METADATA_CREATOR);

    /// <summary>
    /// Application that produced the PDF
    /// </summary>
    public string Producer => GetMetadataString(PDFium.METADATA_PRODUCER);

    /// <summary>
    /// Creation date (raw string from PDF)
    /// </summary>
    public string CreationDate => GetMetadataString(PDFium.METADATA_CREATION_DATE);

    /// <summary>
    /// Modification date (raw string from PDF)
    /// </summary>
    public string ModificationDate => GetMetadataString(PDFium.METADATA_MOD_DATE);

    /// <summary>
    /// Trapped status
    /// </summary>
    public string Trapped => GetMetadataString(PDFium.METADATA_TRAPPED);

    /// <summary>
    /// PDF version (e.g., 14 for PDF 1.4, 17 for PDF 1.7)
    /// </summary>
    public int PdfVersion
    {
        get
        {
            using var _ = PdfiumRuntime.Enter();
            PDFium.FPDF_GetFileVersion(Handle, out int version);
            return version;
        }
    }

    /// <summary>
    /// PDF version as a string (e.g., "1.4", "1.7")
    /// </summary>
    public string PdfVersionString
    {
        get
        {
            int version = PdfVersion;
            if (version == 0) return "Unknown";
            int major = version / 10;
            int minor = version % 10;
            return $"{major}.{minor}";
        }
    }

    /// <summary>
    /// Get creation date as DateTime (if parseable)
    /// </summary>
    public DateTime? CreationDateTime => ParsePdfDate(CreationDate);

    /// <summary>
    /// Get modification date as DateTime (if parseable)
    /// </summary>
    public DateTime? ModificationDateTime => ParsePdfDate(ModificationDate);

    /// <summary>
    /// Get a custom metadata value by tag
    /// </summary>
    public string GetMetadataString(string tag)
    {
        using var _ = PdfiumRuntime.Enter();
        return NativeText.ReadUtf16((Handle, tag),
            static (s, buffer, length) => PDFium.FPDF_GetMetaText(s.Handle, s.tag, buffer, length)) ?? string.Empty;
    }

    /// <summary>
    /// Get all metadata as a dictionary
    /// </summary>
    public Dictionary<string, string> GetAllMetadata()
    {
        using var _ = PdfiumRuntime.Enter();
        return new Dictionary<string, string>
        {
            { "Title", Title },
            { "Author", Author },
            { "Subject", Subject },
            { "Keywords", Keywords },
            { "Creator", Creator },
            { "Producer", Producer },
            { "CreationDate", CreationDate },
            { "ModificationDate", ModificationDate },
            { "Trapped", Trapped },
            { "FileVersion", PdfVersionString }
        };
    }

    /// <summary>
    /// Parse PDF date string to DateTime
    /// PDF date format: D:YYYYMMDDHHmmSSOHH'mm'
    /// Example: D:20231215103045+05'30'
    /// </summary>
    private DateTime? ParsePdfDate(string pdfDate)
    {
        if (string.IsNullOrEmpty(pdfDate))
            return null;

        try
        {
            // Remove "D:" prefix if present
            string date = pdfDate.StartsWith("D:") ? pdfDate.Substring(2) : pdfDate;

            // Extract basic date components (at minimum we need YYYYMMDD)
            if (date.Length < 8)
                return null;

            int year = int.Parse(date.Substring(0, 4));
            int month = int.Parse(date.Substring(4, 2));
            int day = int.Parse(date.Substring(6, 2));

            int hour = 0, minute = 0, second = 0;

            if (date.Length >= 10)
                hour = int.Parse(date.Substring(8, 2));
            if (date.Length >= 12)
                minute = int.Parse(date.Substring(10, 2));
            if (date.Length >= 14)
                second = int.Parse(date.Substring(12, 2));

            var dateTime = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);

            // Try to parse timezone offset if present
            int tzIndex = date.IndexOfAny(new[] { '+', '-', 'Z' }, 14);
            if (tzIndex > 0)
            {
                if (date[tzIndex] == 'Z')
                {
                    return DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);
                }
                else
                {
                    // Parse offset like +05'30' or -08'00'
                    string tzPart = date.Substring(tzIndex);
                    int sign = tzPart[0] == '+' ? 1 : -1;

                    string[] parts = tzPart.Substring(1).Split('\'');
                    if (parts.Length >= 1)
                    {
                        int tzHours = int.Parse(parts[0]);
                        int tzMinutes = parts.Length > 1 ? int.Parse(parts[1]) : 0;

                        TimeSpan offset = new TimeSpan(sign * tzHours, sign * tzMinutes, 0);
                        return new DateTimeOffset(dateTime, offset).UtcDateTime;
                    }
                }
            }

            return dateTime;
        }
        catch
        {
            return null;
        }
    }
}
