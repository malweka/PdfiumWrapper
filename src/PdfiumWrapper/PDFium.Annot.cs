using System.Runtime.InteropServices;

namespace PdfiumWrapper;

/// <summary>
/// PDFium Annotation APIs from fpdf_annot.h
/// </summary>
public static partial class PDFium
{
    #region Annotation Management

    [LibraryImport(LibraryName)]
    public static partial int FPDFPage_GetAnnotCount(IntPtr page);

    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFPage_GetAnnot(IntPtr page, int index);

    [LibraryImport(LibraryName)]
    public static partial void FPDFPage_CloseAnnot(IntPtr annot);

    [LibraryImport(LibraryName)]
    public static partial int FPDFAnnot_GetSubtype(IntPtr annot);

    #endregion

    #region Form Field Information

    [LibraryImport(LibraryName)]
    public static partial int FPDFAnnot_GetFormFieldType(IntPtr hHandle, IntPtr annot);

    [LibraryImport(LibraryName)]
    public static partial ulong FPDFAnnot_GetFormFieldName(IntPtr hHandle, IntPtr annot, IntPtr buffer, ulong buflen);

    [LibraryImport(LibraryName)]
    public static partial ulong FPDFAnnot_GetFormFieldAlternateName(IntPtr hHandle, IntPtr annot, IntPtr buffer, ulong buflen);

    [LibraryImport(LibraryName)]
    public static partial int FPDFAnnot_GetFormFieldFlags(IntPtr hHandle, IntPtr annot);

    #endregion

    #region Form Field Value Get/Set

    [LibraryImport(LibraryName)]
    public static partial ulong FPDFAnnot_GetFormFieldValue(IntPtr hHandle, IntPtr annot, IntPtr buffer, ulong buflen);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFAnnot_SetStringValue(IntPtr annot,
        [MarshalAs(UnmanagedType.LPStr)] string key,
        [MarshalAs(UnmanagedType.LPWStr)] string value);

    #endregion

    #region Form Field Options (for List/Combo boxes)

    [LibraryImport(LibraryName)]
    public static partial int FPDFAnnot_GetOptionCount(IntPtr hHandle, IntPtr annot);

    [LibraryImport(LibraryName)]
    public static partial ulong FPDFAnnot_GetOptionLabel(IntPtr hHandle, IntPtr annot, int index, IntPtr buffer, ulong buflen);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFAnnot_IsOptionSelected(IntPtr hHandle, IntPtr annot, int index);

    #endregion

    #region Form Field Selection (for Checkbox/Radio)

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFAnnot_IsChecked(IntPtr hHandle, IntPtr annot);

    [LibraryImport(LibraryName)]
    public static partial int FPDFAnnot_GetFormControlCount(IntPtr hHandle, IntPtr annot);

    [LibraryImport(LibraryName)]
    public static partial int FPDFAnnot_GetFormControlIndex(IntPtr hHandle, IntPtr annot);

    [LibraryImport(LibraryName)]
    public static partial ulong FPDFAnnot_GetFormFieldExportValue(IntPtr hHandle, IntPtr annot, IntPtr buffer, ulong buflen);

    #endregion

    #region Annotation Constants

    // Annotation subtypes
    public const int FPDF_ANNOT_UNKNOWN = 0;
    public const int FPDF_ANNOT_TEXT = 1;
    public const int FPDF_ANNOT_LINK = 2;
    public const int FPDF_ANNOT_FREETEXT = 3;
    public const int FPDF_ANNOT_LINE = 4;
    public const int FPDF_ANNOT_SQUARE = 5;
    public const int FPDF_ANNOT_CIRCLE = 6;
    public const int FPDF_ANNOT_POLYGON = 7;
    public const int FPDF_ANNOT_POLYLINE = 8;
    public const int FPDF_ANNOT_HIGHLIGHT = 9;
    public const int FPDF_ANNOT_UNDERLINE = 10;
    public const int FPDF_ANNOT_SQUIGGLY = 11;
    public const int FPDF_ANNOT_STRIKEOUT = 12;
    public const int FPDF_ANNOT_STAMP = 13;
    public const int FPDF_ANNOT_CARET = 14;
    public const int FPDF_ANNOT_INK = 15;
    public const int FPDF_ANNOT_POPUP = 16;
    public const int FPDF_ANNOT_FILEATTACHMENT = 17;
    public const int FPDF_ANNOT_SOUND = 18;
    public const int FPDF_ANNOT_MOVIE = 19;
    public const int FPDF_ANNOT_WIDGET = 20;
    public const int FPDF_ANNOT_SCREEN = 21;
    public const int FPDF_ANNOT_PRINTERMARK = 22;
    public const int FPDF_ANNOT_TRAPNET = 23;
    public const int FPDF_ANNOT_WATERMARK = 24;
    public const int FPDF_ANNOT_3D = 25;
    public const int FPDF_ANNOT_RICHMEDIA = 26;
    public const int FPDF_ANNOT_XFAWIDGET = 27;
    public const int FPDF_ANNOT_REDACT = 28;

    // Form field types
    public const int FPDF_FORMFIELD_UNKNOWN = 0;
    public const int FPDF_FORMFIELD_PUSHBUTTON = 1;
    public const int FPDF_FORMFIELD_CHECKBOX = 2;
    public const int FPDF_FORMFIELD_RADIOBUTTON = 3;
    public const int FPDF_FORMFIELD_COMBOBOX = 4;
    public const int FPDF_FORMFIELD_LISTBOX = 5;
    public const int FPDF_FORMFIELD_TEXTFIELD = 6;
    public const int FPDF_FORMFIELD_SIGNATURE = 7;
    public const int FPDF_FORMFIELD_XFA = 8;
    public const int FPDF_FORMFIELD_XFA_CHECKBOX = 9;
    public const int FPDF_FORMFIELD_XFA_COMBOBOX = 10;
    public const int FPDF_FORMFIELD_XFA_IMAGEFIELD = 11;
    public const int FPDF_FORMFIELD_XFA_LISTBOX = 12;
    public const int FPDF_FORMFIELD_XFA_PUSHBUTTON = 13;
    public const int FPDF_FORMFIELD_XFA_SIGNATURE = 14;
    public const int FPDF_FORMFIELD_XFA_TEXTFIELD = 15;

    // Form field flags
    public const int FPDF_FORMFLAG_READONLY = 1 << 0;
    public const int FPDF_FORMFLAG_REQUIRED = 1 << 1;
    public const int FPDF_FORMFLAG_NOEXPORT = 1 << 2;
    public const int FPDF_FORMFLAG_TEXT_MULTILINE = 1 << 12;
    public const int FPDF_FORMFLAG_TEXT_PASSWORD = 1 << 13;
    public const int FPDF_FORMFLAG_CHOICE_COMBO = 1 << 17;
    public const int FPDF_FORMFLAG_CHOICE_EDIT = 1 << 18;
    public const int FPDF_FORMFLAG_CHOICE_MULTI_SELECT = 1 << 21;

    #endregion
}