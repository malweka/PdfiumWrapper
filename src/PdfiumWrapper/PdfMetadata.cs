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
    /// PDF date format: D:YYYYMMDDHHmmSSOHH'mm', where every field after the year may be omitted
    /// from the end (D:2023, D:20231215, ...). Missing month and day are 1, missing time fields 0.
    /// Example: D:20231215103045+05'30'
    /// </summary>
    /// <returns>
    /// UTC (<see cref="DateTimeKind.Utc"/>) when the string has an offset or Z, the local time as
    /// written (<see cref="DateTimeKind.Unspecified"/>) when it has none, null when it is not a
    /// valid PDF date.
    /// </returns>
    internal static DateTime? ParsePdfDate(string? pdfDate)
    {
        if (string.IsNullOrEmpty(pdfDate))
            return null;

        // Remove "D:" prefix if present
        var date = pdfDate.AsSpan();
        if (date.StartsWith("D:"))
            date = date[2..];

        if (!TryTakeDigits(ref date, 4, out int year) || year < 1)
            return null;

        // Month, day, hour, minute, second: each present only if the ones before it are.
        Span<int> fields = stackalloc int[] { 1, 1, 0, 0, 0 };
        for (int i = 0; i < fields.Length && !date.IsEmpty && char.IsAsciiDigit(date[0]); i++)
        {
            if (!TryTakeDigits(ref date, 2, out fields[i]))
                return null;
        }

        int month = fields[0], day = fields[1], hour = fields[2], minute = fields[3], second = fields[4];
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) ||
            hour > 23 || minute > 59 || second > 59)
            return null;

        var dateTime = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        if (date.IsEmpty)
            return dateTime;

        // Offset: Z, or +/- HH, optionally followed by 'mm' (some writers drop the apostrophes).
        // Z may carry a zero offset too (Z00'00').
        char sign = date[0];
        if (sign is not ('Z' or '+' or '-'))
            return null;
        date = date[1..];

        int offsetHours = 0, offsetMinutes = 0;
        if (!date.IsEmpty || sign != 'Z')
        {
            if (!TryTakeDigits(ref date, 2, out offsetHours))
                return null;
            if (!date.IsEmpty && date[0] == '\'')
                date = date[1..];
            if (!date.IsEmpty && !TryTakeDigits(ref date, 2, out offsetMinutes))
                return null;
            if (!date.IsEmpty && date[0] == '\'')
                date = date[1..];
        }
        if (!date.IsEmpty || offsetHours > 23 || offsetMinutes > 59)
            return null;

        var offset = new TimeSpan(offsetHours, offsetMinutes, 0);
        long utcTicks = dateTime.Ticks - (sign == '-' ? -offset.Ticks : offset.Ticks);
        if (utcTicks < DateTime.MinValue.Ticks || utcTicks > DateTime.MaxValue.Ticks)
            return null;
        return new DateTime(utcTicks, DateTimeKind.Utc);
    }

    private static bool TryTakeDigits(ref ReadOnlySpan<char> text, int count, out int value)
    {
        value = 0;
        if (text.Length < count)
            return false;

        for (int i = 0; i < count; i++)
        {
            if (!char.IsAsciiDigit(text[i]))
                return false;
            value = value * 10 + (text[i] - '0');
        }

        text = text[count..];
        return true;
    }
}
