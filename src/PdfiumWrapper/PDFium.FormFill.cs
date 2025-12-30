using System.Runtime.InteropServices;

namespace PdfiumWrapper;

/// <summary>
/// PDFium Form Fill APIs from fpdf_formfill.h
/// </summary>
public static partial class PDFium
{
    #region Form Fill Environment

    [LibraryImport(LibraryName)]
    public static partial IntPtr FPDFDOC_InitFormFillEnvironment(IntPtr document, ref FPDF_FORMFILLINFO formInfo);

    [LibraryImport(LibraryName)]
    public static partial void FPDFDOC_ExitFormFillEnvironment(IntPtr hHandle);

    [LibraryImport(LibraryName)]
    public static partial void FORM_OnAfterLoadPage(IntPtr page, IntPtr hHandle);

    [LibraryImport(LibraryName)]
    public static partial void FORM_OnBeforeClosePage(IntPtr page, IntPtr hHandle);

    #endregion

    #region Form Rendering

    [LibraryImport(LibraryName)]
    public static partial void FPDF_FFLDraw(IntPtr hHandle, IntPtr bitmap, IntPtr page,
        int start_x, int start_y, int size_x, int size_y, int rotate, int flags);

    [LibraryImport(LibraryName)]
    public static partial void FPDF_SetFormFieldHighlightColor(IntPtr hHandle, int fieldType, uint color);

    [LibraryImport(LibraryName)]
    public static partial void FPDF_SetFormFieldHighlightAlpha(IntPtr hHandle, byte alpha);

    [LibraryImport(LibraryName)]
    public static partial void FPDF_RemoveFormFieldHighlight(IntPtr hHandle);

    #endregion

    #region Form Events - Mouse

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FORM_OnMouseMove(IntPtr hHandle, IntPtr page, int modifier, double page_x, double page_y);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FORM_OnMouseWheel(IntPtr hHandle, IntPtr page, int modifier,
        ref FS_POINTF page_coord, int delta_x, int delta_y);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FORM_OnLButtonDown(IntPtr hHandle, IntPtr page, int modifier, double page_x, double page_y);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FORM_OnLButtonUp(IntPtr hHandle, IntPtr page, int modifier, double page_x, double page_y);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FORM_OnLButtonDoubleClick(IntPtr hHandle, IntPtr page, int modifier, double page_x, double page_y);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FORM_OnRButtonDown(IntPtr hHandle, IntPtr page, int modifier, double page_x, double page_y);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FORM_OnRButtonUp(IntPtr hHandle, IntPtr page, int modifier, double page_x, double page_y);

    #endregion

    #region Form Events - Keyboard

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FORM_OnKeyDown(IntPtr hHandle, IntPtr page, int nKeyCode, int modifier);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FORM_OnKeyUp(IntPtr hHandle, IntPtr page, int nKeyCode, int modifier);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FORM_OnChar(IntPtr hHandle, IntPtr page, int nChar, int modifier);

    #endregion

    #region Form Events - Focus

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FORM_ForceToKillFocus(IntPtr hHandle);

    [LibraryImport(LibraryName)]
    public static partial int FORM_GetFocusedText(IntPtr hHandle, IntPtr page, IntPtr buffer, ulong buflen);

    [LibraryImport(LibraryName)]
    public static partial int FORM_GetSelectedText(IntPtr hHandle, IntPtr page, IntPtr buffer, ulong buflen);

    [LibraryImport(LibraryName)]
    public static partial void FORM_ReplaceSelection(IntPtr hHandle, IntPtr page,
        [MarshalAs(UnmanagedType.LPWStr)] string wsText);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FORM_CanUndo(IntPtr hHandle, IntPtr page);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FORM_CanRedo(IntPtr hHandle, IntPtr page);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FORM_Undo(IntPtr hHandle, IntPtr page);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FORM_Redo(IntPtr hHandle, IntPtr page);

    #endregion

    #region Form Field Query

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FORM_OnFocus(IntPtr hHandle, IntPtr page, int modifier, double page_x, double page_y);

    [LibraryImport(LibraryName)]
    public static partial int FORM_GetFocusedAnnot(IntPtr hHandle, out int page_index, out IntPtr annot);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FORM_SetFocusedAnnot(IntPtr hHandle, IntPtr annot);

    #endregion

    #region XFA Support

    [LibraryImport(LibraryName)]
    public static partial int FPDF_GetFormType(IntPtr document);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FORM_SetIndexSelected(IntPtr hHandle, IntPtr page, int index,
        [MarshalAs(UnmanagedType.Bool)] bool selected);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FORM_IsIndexSelected(IntPtr hHandle, IntPtr page, int index);

    #endregion

    #region Helper Structures and Callbacks

    /// <summary>
    /// Interface for form fill callbacks
    /// This structure must be filled by the application before calling FPDFDOC_InitFormFillEnvironment
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_FORMFILLINFO
    {
        public int version;  // Version 1 or 2

        // Release callback
        public IntPtr Release;  // void (*Release)(struct _FPDF_FORMFILLINFO* pThis);

        // Invalidate callback - called when PDF needs to repaint
        public IntPtr FFI_Invalidate;  // void (*FFI_Invalidate)(struct _FPDF_FORMFILLINFO* pThis, FPDF_PAGE page, double left, double top, double right, double bottom);

        // Output selected rects callback
        public IntPtr FFI_OutputSelectedRect;  // void (*FFI_OutputSelectedRect)(struct _FPDF_FORMFILLINFO* pThis, FPDF_PAGE page, double left, double top, double right, double bottom);

        // Set cursor callback
        public IntPtr FFI_SetCursor;  // void (*FFI_SetCursor)(struct _FPDF_FORMFILLINFO* pThis, int nCursorType);

        // Set timer callback
        public IntPtr FFI_SetTimer;  // int (*FFI_SetTimer)(struct _FPDF_FORMFILLINFO* pThis, int uElapse, TimerCallback lpTimerFunc);

        // Kill timer callback
        public IntPtr FFI_KillTimer;  // void (*FFI_KillTimer)(struct _FPDF_FORMFILLINFO* pThis, int nTimerID);

        // Get local time callback
        public IntPtr FFI_GetLocalTime;  // FPDF_SYSTEMTIME (*FFI_GetLocalTime)(struct _FPDF_FORMFILLINFO* pThis);

        // On change callback
        public IntPtr FFI_OnChange;  // void (*FFI_OnChange)(struct _FPDF_FORMFILLINFO* pThis);

        // Get page callback
        public IntPtr FFI_GetPage;  // FPDF_PAGE (*FFI_GetPage)(struct _FPDF_FORMFILLINFO* pThis, FPDF_DOCUMENT document, int nPageIndex);

        // Get current page callback
        public IntPtr FFI_GetCurrentPage;  // FPDF_PAGE (*FFI_GetCurrentPage)(struct _FPDF_FORMFILLINFO* pThis, FPDF_DOCUMENT document);

        // Get rotation callback
        public IntPtr FFI_GetRotation;  // int (*FFI_GetRotation)(struct _FPDF_FORMFILLINFO* pThis, FPDF_PAGE page);

        // Execute named action callback
        public IntPtr FFI_ExecuteNamedAction;  // void (*FFI_ExecuteNamedAction)(struct _FPDF_FORMFILLINFO* pThis, FPDF_BYTESTRING namedAction);

        // Set text field focus callback
        public IntPtr FFI_SetTextFieldFocus;  // void (*FFI_SetTextFieldFocus)(struct _FPDF_FORMFILLINFO* pThis, FPDF_WIDESTRING value, FPDF_DWORD valueLen, FPDF_BOOL is_focus);

        // Do URI action callback
        public IntPtr FFI_DoURIAction;  // void (*FFI_DoURIAction)(struct _FPDF_FORMFILLINFO* pThis, FPDF_BYTESTRING bsURI);

        // Do goto action callback
        public IntPtr FFI_DoGoToAction;  // void (*FFI_DoGoToAction)(struct _FPDF_FORMFILLINFO* pThis, int nPageIndex, int zoomMode, float* fPosArray, int sizeofArray);

        // Version 2 additions (for XFA support)
        public IntPtr m_pJsPlatform;  // IPDF_JSPLATFORM* - JavaScript platform interface

        // Display caret callback
        public IntPtr FFI_DisplayCaret;  // void (*FFI_DisplayCaret)(struct _FPDF_FORMFILLINFO* pThis, FPDF_PAGE page, FPDF_BOOL bVisible, double left, double top, double right, double bottom);

        // Get current page index callback
        public IntPtr FFI_GetCurrentPageIndex;  // int (*FFI_GetCurrentPageIndex)(struct _FPDF_FORMFILLINFO* pThis, FPDF_DOCUMENT document);

        // Set current page callback
        public IntPtr FFI_SetCurrentPage;  // void (*FFI_SetCurrentPage)(struct _FPDF_FORMFILLINFO* pThis, FPDF_DOCUMENT document, int iCurPage);

        // Go to URL callback
        public IntPtr FFI_GotoURL;  // void (*FFI_GotoURL)(struct _FPDF_FORMFILLINFO* pThis, FPDF_DOCUMENT document, FPDF_WIDESTRING wsURL);

        // Get page view rect callback
        public IntPtr FFI_GetPageViewRect;  // void (*FFI_GetPageViewRect)(struct _FPDF_FORMFILLINFO* pThis, FPDF_PAGE page, double* left, double* top, double* right, double* bottom);

        // Page event callback
        public IntPtr FFI_PageEvent;  // void (*FFI_PageEvent)(struct _FPDF_FORMFILLINFO* pThis, int page_count, FPDF_DWORD event_type);

        // Pop-up menu callback
        public IntPtr FFI_PopupMenu;  // FPDF_BOOL (*FFI_PopupMenu)(struct _FPDF_FORMFILLINFO* pThis, FPDF_PAGE page, FPDF_WIDGET hWidget, int menuFlag, float x, float y);

        // Open file callback
        public IntPtr FFI_OpenFile;  // FPDF_FILEHANDLER* (*FFI_OpenFile)(struct _FPDF_FORMFILLINFO* pThis, int fileFlag, FPDF_WIDESTRING wsURL, const char* mode);

        // Email to callback
        public IntPtr FFI_EmailTo;  // void (*FFI_EmailTo)(struct _FPDF_FORMFILLINFO* pThis, FPDF_FILEHANDLER* fileHandler, FPDF_WIDESTRING pTo, FPDF_WIDESTRING pSubject, FPDF_WIDESTRING pCC, FPDF_WIDESTRING pBCC, FPDF_WIDESTRING pMsg);

        // Upload to callback
        public IntPtr FFI_UploadTo;  // void (*FFI_UploadTo)(struct _FPDF_FORMFILLINFO* pThis, FPDF_FILEHANDLER* fileHandler, int fileFlag, FPDF_WIDESTRING uploadTo);

        // Get platform callback
        public IntPtr FFI_GetPlatform;  // int (*FFI_GetPlatform)(struct _FPDF_FORMFILLINFO* pThis, void* platform, int length);

        // Get language callback
        public IntPtr FFI_GetLanguage;  // int (*FFI_GetLanguage)(struct _FPDF_FORMFILLINFO* pThis, void* language, int length);

        // Download from URL callback
        public IntPtr FFI_DownloadFromURL;  // FPDF_LPFILEHANDLER (*FFI_DownloadFromURL)(struct _FPDF_FORMFILLINFO* pThis, FPDF_WIDESTRING URL);

        // Post request URL callback
        public IntPtr FFI_PostRequestURL;  // FPDF_BOOL (*FFI_PostRequestURL)(struct _FPDF_FORMFILLINFO* pThis, FPDF_WIDESTRING wsURL, FPDF_WIDESTRING wsData, FPDF_WIDESTRING wsContentType, FPDF_WIDESTRING wsEncode, FPDF_WIDESTRING wsHeader, FPDF_BSTR* response);

        // Put request URL callback
        public IntPtr FFI_PutRequestURL;  // FPDF_BOOL (*FFI_PutRequestURL)(struct _FPDF_FORMFILLINFO* pThis, FPDF_WIDESTRING wsURL, FPDF_WIDESTRING wsData, FPDF_WIDESTRING wsEncode);

        // On focus change callback
        public IntPtr FFI_OnFocusChange;  // void (*FFI_OnFocusChange)(struct _FPDF_FORMFILLINFO* pThis, FPDF_ANNOTATION annot, int page_index);

        // Do URI action with key mods callback
        public IntPtr FFI_DoURIActionWithKeyboardModifier;  // void (*FFI_DoURIActionWithKeyboardModifier)(struct _FPDF_FORMFILLINFO* pThis, FPDF_BYTESTRING bsURI, int modifiers);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_POINTF
    {
        public float x;
        public float y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_SYSTEMTIME
    {
        public ushort wYear;
        public ushort wMonth;
        public ushort wDayOfWeek;
        public ushort wDay;
        public ushort wHour;
        public ushort wMinute;
        public ushort wSecond;
        public ushort wMilliseconds;
    }

    #endregion

    #region Form Type Constants

    public const int FORMTYPE_NONE = 0;           // No form
    public const int FORMTYPE_ACRO_FORM = 1;      // AcroForm
    public const int FORMTYPE_XFA_FULL = 2;       // Full XFA form
    public const int FORMTYPE_XFA_FOREGROUND = 3; // XFA foreground form

    #endregion

    #region Cursor Type Constants

    public const int FXCT_ARROW = 0;
    public const int FXCT_NESW = 1;
    public const int FXCT_NWSE = 2;
    public const int FXCT_VBEAM = 3;
    public const int FXCT_HBEAM = 4;
    public const int FXCT_HAND = 5;

    #endregion

    #region Keyboard Modifier Constants

    public const int FWL_EVENTFLAG_ShiftKey = 1 << 0;
    public const int FWL_EVENTFLAG_ControlKey = 1 << 1;
    public const int FWL_EVENTFLAG_AltKey = 1 << 2;
    public const int FWL_EVENTFLAG_MetaKey = 1 << 3;
    public const int FWL_EVENTFLAG_KeyPad = 1 << 4;
    public const int FWL_EVENTFLAG_AutoRepeat = 1 << 5;
    public const int FWL_EVENTFLAG_LeftButtonDown = 1 << 6;
    public const int FWL_EVENTFLAG_MiddleButtonDown = 1 << 7;
    public const int FWL_EVENTFLAG_RightButtonDown = 1 << 8;

    #endregion
}