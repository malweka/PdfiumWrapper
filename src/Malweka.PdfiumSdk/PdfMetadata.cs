using System.Runtime.InteropServices;

namespace Malweka.PdfiumSdk;

/// <summary>
/// PDF document metadata and properties
/// </summary>
public class PdfMetadata
{
    private IntPtr _document;

    internal PdfMetadata(IntPtr document)
    {
        _document = document;
    }

    /// <summary>
    /// Document title
    /// </summary>
    public string Title
    {
        get => GetMetadataString(PDFium.METADATA_TITLE);
        set => SetMetadataString(PDFium.METADATA_TITLE, value);
    }

    /// <summary>
    /// Document author
    /// </summary>
    public string Author
    {
        get => GetMetadataString(PDFium.METADATA_AUTHOR);
        set => SetMetadataString(PDFium.METADATA_AUTHOR, value);
    }

    /// <summary>
    /// Document subject
    /// </summary>
    public string Subject
    {
        get => GetMetadataString(PDFium.METADATA_SUBJECT);
        set => SetMetadataString(PDFium.METADATA_SUBJECT, value);
    }

    /// <summary>
    /// Document keywords
    /// </summary>
    public string Keywords
    {
        get => GetMetadataString(PDFium.METADATA_KEYWORDS);
        set => SetMetadataString(PDFium.METADATA_KEYWORDS, value);
    }

    /// <summary>
    /// Application that created the original document
    /// </summary>
    public string Creator
    {
        get => GetMetadataString(PDFium.METADATA_CREATOR);
        set => SetMetadataString(PDFium.METADATA_CREATOR, value);
    }

    /// <summary>
    /// Application that produced the PDF
    /// </summary>
    public string Producer
    {
        get => GetMetadataString(PDFium.METADATA_PRODUCER);
        set => SetMetadataString(PDFium.METADATA_PRODUCER, value);
    }

    /// <summary>
    /// Creation date (raw string from PDF)
    /// </summary>
    public string CreationDate
    {
        get => GetMetadataString(PDFium.METADATA_CREATION_DATE);
        set => SetMetadataString(PDFium.METADATA_CREATION_DATE, value);
    }

    /// <summary>
    /// Modification date (raw string from PDF)
    /// </summary>
    public string ModificationDate
    {
        get => GetMetadataString(PDFium.METADATA_MOD_DATE);
        set => SetMetadataString(PDFium.METADATA_MOD_DATE, value);
    }

    /// <summary>
    /// Trapped status
    /// </summary>
    public string Trapped
    {
        get => GetMetadataString(PDFium.METADATA_TRAPPED);
        set => SetMetadataString(PDFium.METADATA_TRAPPED, value);
    }

    /// <summary>
    /// PDF version (e.g., 14 for PDF 1.4, 17 for PDF 1.7)
    /// </summary>
    public int FileVersion
    {
        get
        {
            PDFium.FPDF_GetFileVersion(_document, out int version);
            return version;
        }
    }

    /// <summary>
    /// PDF version as a string (e.g., "1.4", "1.7")
    /// </summary>
    public string FileVersionString
    {
        get
        {
            int version = FileVersion;
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
        ulong length = PDFium.FPDF_GetMetaText(_document, tag, IntPtr.Zero, 0);
        if (length == 0)
            return string.Empty;

        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            PDFium.FPDF_GetMetaText(_document, tag, buffer, length);
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Set a metadata value by tag
    /// </summary>
    public bool SetMetadataString(string tag, string value)
    {
        return PDFium.FPDF_SetMetaText(_document, tag, value ?? string.Empty);
    }

    /// <summary>
    /// Set creation date from DateTime
    /// </summary>
    public void SetCreationDateTime(DateTime dateTime)
    {
        string pdfDate = FormatPdfDate(dateTime);
        SetMetadataString(PDFium.METADATA_CREATION_DATE, pdfDate);
    }

    /// <summary>
    /// Set modification date from DateTime
    /// </summary>
    public void SetModificationDateTime(DateTime dateTime)
    {
        string pdfDate = FormatPdfDate(dateTime);
        SetMetadataString(PDFium.METADATA_MOD_DATE, pdfDate);
    }

    /// <summary>
    /// Set all common metadata fields at once
    /// </summary>
    public void SetAllMetadata(string title = null, string author = null, string subject = null,
        string keywords = null, string creator = null, string producer = null)
    {
        if (title != null) Title = title;
        if (author != null) Author = author;
        if (subject != null) Subject = subject;
        if (keywords != null) Keywords = keywords;
        if (creator != null) Creator = creator;
        if (producer != null) Producer = producer;
    }

    /// <summary>
    /// Clear all metadata fields
    /// </summary>
    public void ClearAllMetadata()
    {
        Title = string.Empty;
        Author = string.Empty;
        Subject = string.Empty;
        Keywords = string.Empty;
        Creator = string.Empty;
        Producer = string.Empty;
        CreationDate = string.Empty;
        ModificationDate = string.Empty;
    }

    /// <summary>
    /// Get all metadata as a dictionary
    /// </summary>
    public Dictionary<string, string> GetAllMetadata()
    {
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
            { "FileVersion", FileVersionString }
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

    /// <summary>
    /// Format DateTime to PDF date string
    /// PDF date format: D:YYYYMMDDHHmmSSOHH'mm'
    /// Example: D:20231215103045+05'30'
    /// </summary>
    private string FormatPdfDate(DateTime dateTime)
    {
        // Convert to UTC if it's local time
        if (dateTime.Kind == DateTimeKind.Local)
        {
            dateTime = dateTime.ToUniversalTime();
        }

        // Format basic date/time: D:YYYYMMDDHHmmSS
        string formatted = $"D:{dateTime:yyyyMMddHHmmss}";

        // Add timezone
        if (dateTime.Kind == DateTimeKind.Utc)
        {
            formatted += "Z";
        }
        else
        {
            // For unspecified, assume local timezone
            var offset = TimeZoneInfo.Local.GetUtcOffset(dateTime);
            string sign = offset.TotalMinutes >= 0 ? "+" : "-";
            formatted += $"{sign}{Math.Abs(offset.Hours):D2}'{Math.Abs(offset.Minutes):D2}'";
        }

        return formatted;
    }
}