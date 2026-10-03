using System.Runtime.InteropServices;

namespace PdfiumWrapper;

/// <summary>
/// Provides access to PDF form fields and allows reading and modifying form data.
/// </summary>
/// <remarks>
/// Thread safety: operations on different objects may run concurrently; the wrapper serializes
/// native work. Do not use one object from two threads at once.
/// A form belongs to the document that created it and is disposed with that document.
/// </remarks>
public class PdfForm : IDisposable
{
    private readonly PdfDocument _owner;
    private int _pageCount;

    // Internal so the owning document's finalizer can hand these to the deferred-release queue.
    internal IntPtr _formHandle;
    internal IntPtr _formInfo;

    private bool _formInitialized;
    private bool _disposed;
    private readonly object _disposeLock = new object();

    internal bool HasFormFields => _formInitialized;

    /// <summary>The native gate must be held.</summary>
    internal PdfForm(PdfDocument owner, int pageCount)
    {
        PdfiumRuntime.AssertHeld();
        _owner = owner;
        _pageCount = pageCount;
        InitializeFormEnvironment();
    }

    private void InitializeFormEnvironment()
    {
        // PDFium keeps a pointer to this structure for the lifetime of the form environment, so it
        // lives in native memory and is freed only after FPDFDOC_ExitFormFillEnvironment.
        // A minimal structure: version 2, no callbacks.
        int size = Marshal.SizeOf<PDFium.FPDF_FORMFILLINFO>();
        _formInfo = Marshal.AllocHGlobal(size);
        unsafe
        {
            new Span<byte>(_formInfo.ToPointer(), size).Clear();
        }
        Marshal.WriteInt32(_formInfo, 2);

        try
        {
            _formHandle = PDFium.FPDFDOC_InitFormFillEnvironment(_owner.Document, _formInfo);
            _formInitialized = _formHandle != IntPtr.Zero;
            if (_formInitialized)
                PdfiumRuntime.HandleOpened();
        }
        catch
        {
            Marshal.FreeHGlobal(_formInfo);
            _formInfo = IntPtr.Zero;
            throw;
        }
    }

    public FormField[] GetAllFormFields()
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();

        var fields = new List<FormField>();
        for (int pageIndex = 0; pageIndex < _pageCount; pageIndex++)
        {
            var pageFields = GetFormFieldsOnPageInternal(pageIndex);
            fields.AddRange(pageFields);
        }
        return fields.ToArray();
    }

    public FormField[] GetFormFieldsOnPage(int pageIndex)
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();
        return GetFormFieldsOnPageInternal(pageIndex);
    }

    private FormField[] GetFormFieldsOnPageInternal(int pageIndex)
    {
        var fields = new List<FormField>();
        var page = PDFium.FPDF_LoadPage(_owner.Document, pageIndex);

        try
        {
            // Notify form environment about page load
            if (_formInitialized)
            {
                PDFium.FORM_OnAfterLoadPage(page, _formHandle);
            }

            int annotCount = PDFium.FPDFPage_GetAnnotCount(page);

            for (int i = 0; i < annotCount; i++)
            {
                var annot = PDFium.FPDFPage_GetAnnot(page, i);
                int subtype = PDFium.FPDFAnnot_GetSubtype(annot);

                if (subtype == PDFium.FPDF_ANNOT_WIDGET)
                {
                    var field = ExtractFormField(annot, pageIndex);
                    if (field != null)
                        fields.Add(field);
                }

                PDFium.FPDFPage_CloseAnnot(annot);
            }

            // Notify form environment about page close
            if (_formInitialized)
            {
                PDFium.FORM_OnBeforeClosePage(page, _formHandle);
            }
        }
        finally
        {
            PDFium.FPDF_ClosePage(page);
        }

        return fields.ToArray();
    }

    private FormField? ExtractFormField(IntPtr annot, int pageIndex)
    {
        string? name = GetFieldName(annot);
        if (name == null)
            return null;

        int type = PDFium.FPDFAnnot_GetFormFieldType(_formHandle, annot);
        string? value = GetAnnotFieldValue(annot, type);
        int flags = PDFium.FPDFAnnot_GetFormFieldFlags(_formHandle, annot);

        // Options for combo/list boxes
        var options = type is PDFium.FPDF_FORMFIELD_COMBOBOX or PDFium.FPDF_FORMFIELD_LISTBOX
            ? GetFieldOptions(annot)
            : new List<string>();

        return new FormField
        {
            Name = name,
            Type = (FormFieldType)type,
            Value = value,
            PageIndex = pageIndex,
            IsRequired = (flags & PDFium.FPDF_FORMFLAG_REQUIRED) != 0,
            IsReadOnly = (flags & PDFium.FPDF_FORMFLAG_READONLY) != 0,
            Options = options
        };
    }

    private string? GetFieldName(IntPtr annot) =>
        NativeText.ReadUtf16((_formHandle, annot),
            static (s, buffer, length) => PDFium.FPDFAnnot_GetFormFieldName(s._formHandle, s.annot, buffer, length));

    private string? GetAnnotFieldValue(IntPtr annot, int fieldType)
    {
        switch (fieldType)
        {
            case PDFium.FPDF_FORMFIELD_CHECKBOX:
            case PDFium.FPDF_FORMFIELD_RADIOBUTTON:
                bool isChecked = PDFium.FPDFAnnot_IsChecked(_formHandle, annot);
                return isChecked ? "true" : "false";

            default:
                // Text fields and the current value of combo/list boxes
                return NativeText.ReadUtf16((_formHandle, annot),
                    static (s, buffer, length) => PDFium.FPDFAnnot_GetFormFieldValue(s._formHandle, s.annot, buffer, length))
                    ?? string.Empty;
        }
    }

    private List<string> GetFieldOptions(IntPtr annot)
    {
        var options = new List<string>();
        int optionCount = PDFium.FPDFAnnot_GetOptionCount(_formHandle, annot);

        for (int i = 0; i < optionCount; i++)
        {
            var label = NativeText.ReadUtf16((_formHandle, annot, i),
                static (s, buffer, length) => PDFium.FPDFAnnot_GetOptionLabel(s._formHandle, s.annot, s.i, buffer, length));
            if (label != null)
                options.Add(label);
        }

        return options;
    }

    public string? GetFormFieldValue(string fieldName)
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();

        var field = FindFormField(fieldName);
        if (field == null)
            throw new ArgumentException($"Form field '{fieldName}' not found");

        return field.Value;
    }

    public void SetFormFieldValue(string fieldName, string value)
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();

        var fieldInfo = FindFormFieldWithAnnotation(fieldName);
        if (fieldInfo == null)
            throw new ArgumentException($"Form field '{fieldName}' not found");

        try
        {
            using (PdfiumDiagnostics.NativeInterval(NativeOp.FormFill))
                SetAnnotFieldValue(fieldInfo.Value.annot, fieldInfo.Value.field.Type, value);
        }
        finally
        {
            PDFium.FPDFPage_CloseAnnot(fieldInfo.Value.annot);

            if (_formInitialized)
            {
                PDFium.FORM_OnBeforeClosePage(fieldInfo.Value.page, _formHandle);
            }
            PDFium.FPDF_ClosePage(fieldInfo.Value.page);
        }
    }

    private void SetAnnotFieldValue(IntPtr annot, FormFieldType fieldType, string value)
    {
        switch ((int)fieldType)
        {
            case PDFium.FPDF_FORMFIELD_TEXTFIELD:
            case PDFium.FPDF_FORMFIELD_COMBOBOX:
                // Set text value directly
                PDFium.FPDFAnnot_SetStringValue(annot, "V", value);
                break;

            case PDFium.FPDF_FORMFIELD_CHECKBOX:
            case PDFium.FPDF_FORMFIELD_RADIOBUTTON:
                // For checkbox/radio, value should be "true"/"false" or "1"/"0" or "yes"/"no"
                bool shouldCheck = value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                   value.Equals("1") ||
                                   value.Equals("yes", StringComparison.OrdinalIgnoreCase);

                if (shouldCheck)
                {
                    // Get export value for the field
                    var exportValue = NativeText.ReadUtf16((_formHandle, annot),
                        static (s, buffer, length) => PDFium.FPDFAnnot_GetFormFieldExportValue(s._formHandle, s.annot, buffer, length));
                    PDFium.FPDFAnnot_SetStringValue(annot, "V", exportValue ?? "Yes");
                }
                else
                {
                    PDFium.FPDFAnnot_SetStringValue(annot, "V", "Off");
                }
                break;

            case PDFium.FPDF_FORMFIELD_LISTBOX:
                // For list box, set the value
                PDFium.FPDFAnnot_SetStringValue(annot, "V", value);
                break;

            default:
                throw new NotSupportedException($"Setting value for field type {fieldType} is not supported");
        }
    }

    public void SetFormFieldChecked(string fieldName, bool isChecked)
    {
        SetFormFieldValue(fieldName, isChecked ? "true" : "false");
    }

    public bool GetFormFieldChecked(string fieldName)
    {
        string value = GetFormFieldValue(fieldName)!;
        return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("1") ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public void SetListBoxSelection(string fieldName, string selectedValue)
    {
        SetFormFieldValue(fieldName, selectedValue);
    }

    public void SetListBoxSelections(string fieldName, string[] selectedValues)
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();

        // For multi-select list boxes
        var fieldInfo = FindFormFieldWithAnnotation(fieldName);
        if (fieldInfo == null)
            throw new ArgumentException($"Form field '{fieldName}' not found");

        try
        {
            if (fieldInfo.Value.field.Type != FormFieldType.ListBox)
                throw new InvalidOperationException($"Field '{fieldName}' is not a list box");

            // Join multiple selections (PDFium typically uses arrays, but we'll use comma-separated for simplicity)
            string value = string.Join(",", selectedValues);
            using (PdfiumDiagnostics.NativeInterval(NativeOp.FormFill))
                PDFium.FPDFAnnot_SetStringValue(fieldInfo.Value.annot, "V", value);
        }
        finally
        {
            PDFium.FPDFPage_CloseAnnot(fieldInfo.Value.annot);

            if (_formInitialized)
            {
                PDFium.FORM_OnBeforeClosePage(fieldInfo.Value.page, _formHandle);
            }
            PDFium.FPDF_ClosePage(fieldInfo.Value.page);
        }
    }

    private FormField? FindFormField(string fieldName)
    {
        for (int pageIndex = 0; pageIndex < _pageCount; pageIndex++)
        {
            var pageFields = GetFormFieldsOnPageInternal(pageIndex);
            var field = pageFields.FirstOrDefault(f => string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase));
            if (field != null)
                return field;
        }
        return null;
    }

    private (FormField field, IntPtr annot, IntPtr page)? FindFormFieldWithAnnotation(string fieldName)
    {
        for (int pageIndex = 0; pageIndex < _pageCount; pageIndex++)
        {
            var page = PDFium.FPDF_LoadPage(_owner.Document, pageIndex);

            if (_formInitialized)
            {
                PDFium.FORM_OnAfterLoadPage(page, _formHandle);
            }

            int annotCount = PDFium.FPDFPage_GetAnnotCount(page);

            for (int i = 0; i < annotCount; i++)
            {
                var annot = PDFium.FPDFPage_GetAnnot(page, i);
                int subtype = PDFium.FPDFAnnot_GetSubtype(annot);

                if (subtype == PDFium.FPDF_ANNOT_WIDGET)
                {
                    string? name = GetFieldName(annot);
                    if (name != null && name.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                    {
                        var field = ExtractFormField(annot, pageIndex);
                        return (field!, annot, page);
                    }
                }

                PDFium.FPDFPage_CloseAnnot(annot);
            }

            if (_formInitialized)
            {
                PDFium.FORM_OnBeforeClosePage(page, _formHandle);
            }

            PDFium.FPDF_ClosePage(page);
        }

        return null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(PdfForm));
        if (_owner.IsDisposed)
            throw new ObjectDisposedException(nameof(PdfDocument), "The owning document has been disposed.");
    }

    public void Dispose()
    {
        using var _ = PdfiumRuntime.Enter();
        DisposeCore();
        _owner.UnregisterForm(this);
    }

    /// <summary>The owning document is being disposed. The native gate must be held.</summary>
    internal void DisposeFromOwner()
    {
        PdfiumRuntime.AssertHeld();
        DisposeCore();
    }

    private void DisposeCore()
    {
        PdfiumRuntime.AssertHeld();
        lock (_disposeLock)
        {
            if (_disposed)
                return;

            if (_formInitialized && _formHandle != IntPtr.Zero)
            {
                PDFium.FPDFDOC_ExitFormFillEnvironment(_formHandle);
                PdfiumRuntime.HandleClosed();
                _formHandle = IntPtr.Zero;
                _formInitialized = false;
            }

            // Only after the environment is gone may the structure it pointed at be freed.
            if (_formInfo != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_formInfo);
                _formInfo = IntPtr.Zero;
            }

            _disposed = true;
        }
    }
}
