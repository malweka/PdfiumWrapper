namespace PdfiumWrapper;

/// <summary>
/// Why PDFium failed to load a document: the <c>FPDF_ERR_*</c> value of <c>FPDF_GetLastError</c>.
/// </summary>
public enum PdfiumErrorCode
{
    /// <summary>PDFium gave no specific reason.</summary>
    Unknown = 1,

    /// <summary>The file was not found or could not be opened.</summary>
    File = 2,

    /// <summary>The input is not a PDF or is corrupted.</summary>
    Format = 3,

    /// <summary>The document is encrypted and the password is missing or wrong.</summary>
    Password = 4,

    /// <summary>The document uses a security handler PDFium does not support.</summary>
    Security = 5,

    /// <summary>A page was not found or its content is broken.</summary>
    Page = 6,
}

/// <summary>
/// PDFium failed to load a document. <see cref="ErrorCode"/> tells a wrong password from a
/// corrupt or unreadable file without parsing the message.
/// </summary>
public sealed class PdfiumException : InvalidOperationException
{
    public PdfiumException(string message, PdfiumErrorCode errorCode)
        : base($"{message}: {Describe(errorCode)} (PDFium error {(int)errorCode}).")
    {
        ErrorCode = errorCode;
    }

    public PdfiumErrorCode ErrorCode { get; }

    /// <summary>
    /// Reads <c>FPDF_GetLastError</c>. Call in the same gated scope as the failing load, and
    /// only after a call that sets it (the <c>FPDF_Load*Document</c> functions).
    /// </summary>
    internal static PdfiumException FromLastError(string message)
    {
        ulong code = PDFium.FPDF_GetLastError().Value;
        var errorCode = code is >= (ulong)PdfiumErrorCode.Unknown and <= (ulong)PdfiumErrorCode.Page
            ? (PdfiumErrorCode)code
            : PdfiumErrorCode.Unknown;
        return new PdfiumException(message, errorCode);
    }

    private static string Describe(PdfiumErrorCode errorCode) => errorCode switch
    {
        PdfiumErrorCode.File => "the file was not found or could not be opened",
        PdfiumErrorCode.Format => "the input is not a PDF or is corrupted",
        PdfiumErrorCode.Password => "a password is required or the password is wrong",
        PdfiumErrorCode.Security => "the document uses an unsupported security handler",
        PdfiumErrorCode.Page => "a page was not found or its content is broken",
        _ => "unknown error",
    };
}
