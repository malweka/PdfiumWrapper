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
            var label = GetOptionLabel(annot, i);
            if (label != null)
                options.Add(label);
        }

        return options;
    }

    private string? GetOptionLabel(IntPtr annot, int index) =>
        NativeText.ReadUtf16((_formHandle, annot, index),
            static (s, buffer, length) => PDFium.FPDFAnnot_GetOptionLabel(s._formHandle, s.annot, s.index, buffer, length));

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
                SetAnnotFieldValue(fieldInfo.Value.page, fieldInfo.Value.annot, fieldInfo.Value.field.Type, fieldName, value);
        }
        finally
        {
            CloseFieldAnnotation(fieldInfo.Value.annot, fieldInfo.Value.page);
        }
    }

    private void SetAnnotFieldValue(IntPtr page, IntPtr annot, FormFieldType fieldType, string fieldName, string value)
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
                SetChecked(page, annot, fieldName, shouldCheck);
                break;

            case PDFium.FPDF_FORMFIELD_LISTBOX:
                // For list box, set the value
                PDFium.FPDFAnnot_SetStringValue(annot, "V", value);
                break;

            default:
                throw new NotSupportedException($"Setting value for field type {fieldType} is not supported");
        }
    }

    // PDFium has no call that sets a button's state. Focusing the widget and pressing space toggles
    // it as a viewer would; removing the focus writes /AS and /V (as a name) back to the field.
    private void SetChecked(IntPtr page, IntPtr annot, string fieldName, bool isChecked)
    {
        if (PDFium.FPDFAnnot_IsChecked(_formHandle, annot) == isChecked)
            return;

        FocusForEditing(annot, fieldName);
        try
        {
            PDFium.FORM_OnChar(_formHandle, page, ' ', 0);
        }
        finally
        {
            PDFium.FORM_ForceToKillFocus(_formHandle);
        }

        // A read-only button does not toggle, and a radio button turns off only when another
        // button of its group is selected.
        if (PDFium.FPDFAnnot_IsChecked(_formHandle, annot) != isChecked)
            throw new InvalidOperationException(
                $"Form field '{fieldName}' could not be {(isChecked ? "checked" : "unchecked")}");
    }

    /// <summary>
    /// Checks or unchecks a check box or radio button, as a click in a viewer would: the field's
    /// appearance state (/AS) and value (/V) are set to its export value or Off.
    /// </summary>
    /// <remarks>
    /// Acts on the first widget with this name. A radio button cannot be unchecked; check another
    /// button of its group instead.
    /// </remarks>
    /// <exception cref="ArgumentException">No field has this name.</exception>
    /// <exception cref="NotSupportedException">The field type does not take a value.</exception>
    /// <exception cref="InvalidOperationException">PDFium did not change the state (for example a
    /// read-only field, or unchecking a radio button).</exception>
    public void SetFormFieldChecked(string fieldName, bool isChecked)
    {
        SetFormFieldValue(fieldName, isChecked ? "true" : "false");
    }

    /// <summary>
    /// Gets whether a check box or radio button is checked, from PDFium's checked state
    /// (<c>FPDFAnnot_IsChecked</c>), whatever the field's export value is ("On", "Yes", "1", ...).
    /// </summary>
    /// <remarks>
    /// Reads the first widget with this name, as <see cref="GetFormFieldValue"/> does: for a radio
    /// button group, which has one widget per button under one name, that is its first button.
    /// <see cref="FormField.Value"/> holds the same state as "true" or "false".
    /// </remarks>
    /// <exception cref="ArgumentException">No field has this name.</exception>
    /// <exception cref="InvalidOperationException">The field is not a check box or radio button.</exception>
    public bool GetFormFieldChecked(string fieldName)
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();

        var fieldInfo = FindFormFieldWithAnnotation(fieldName);
        if (fieldInfo == null)
            throw new ArgumentException($"Form field '{fieldName}' not found");

        try
        {
            if (fieldInfo.Value.field.Type is not (FormFieldType.CheckBox or FormFieldType.RadioButton))
                throw new InvalidOperationException($"Field '{fieldName}' is not a check box or radio button");

            return PDFium.FPDFAnnot_IsChecked(_formHandle, fieldInfo.Value.annot);
        }
        finally
        {
            CloseFieldAnnotation(fieldInfo.Value.annot, fieldInfo.Value.page);
        }
    }

    public void SetListBoxSelection(string fieldName, string selectedValue)
    {
        SetFormFieldValue(fieldName, selectedValue);
    }

    /// <summary>
    /// Selects exactly the given options of a list box and clears the others.
    /// </summary>
    /// <remarks>
    /// Values are matched (ordinal, case-sensitive) against the option labels in
    /// <see cref="FormField.Options"/>; a value may contain commas. The options are selected through
    /// PDFium's form filler (<c>FORM_SetIndexSelected</c> on the focused widget), which writes the
    /// field as a viewer does: /I holds the selected indexes, and /V an array of the selected values
    /// (a single value when one is selected). An empty array clears the selection.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="selectedValues"/> is null.</exception>
    /// <exception cref="ArgumentException">No field has this name, a value is null or not one of the
    /// options, or more than one value is given for a list box without the multi-select flag.</exception>
    /// <exception cref="InvalidOperationException">The field is not a list box, or PDFium did not apply
    /// the selection (for example a read-only field).</exception>
    public void SetListBoxSelections(string fieldName, string[] selectedValues)
    {
        using var _ = PdfiumRuntime.Enter();
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(selectedValues);

        var fieldInfo = FindFormFieldWithAnnotation(fieldName);
        if (fieldInfo == null)
            throw new ArgumentException($"Form field '{fieldName}' not found");

        try
        {
            if (fieldInfo.Value.field.Type != FormFieldType.ListBox)
                throw new InvalidOperationException($"Field '{fieldName}' is not a list box");

            var annot = fieldInfo.Value.annot;
            int optionCount = PDFium.FPDFAnnot_GetOptionCount(_formHandle, annot);
            var selected = FindOptionIndexes(annot, fieldName, optionCount, selectedValues);

            using (PdfiumDiagnostics.NativeInterval(NativeOp.FormFill))
                SelectOptions(fieldInfo.Value.page, annot, fieldName, optionCount, selected);
        }
        finally
        {
            CloseFieldAnnotation(fieldInfo.Value.annot, fieldInfo.Value.page);
        }
    }

    // Maps each value to the index of the first option with that label. Indexes come from PDFium,
    // not from FormField.Options, which skips options without a label.
    private HashSet<int> FindOptionIndexes(IntPtr annot, string fieldName, int optionCount, string[] values)
    {
        var labels = new string?[Math.Max(optionCount, 0)];
        for (int i = 0; i < labels.Length; i++)
            labels[i] = GetOptionLabel(annot, i);

        var selected = new HashSet<int>();
        foreach (var value in values)
        {
            if (value == null)
                throw new ArgumentException("A selected value is null", "selectedValues");

            int index = Array.IndexOf(labels, value);
            if (index < 0)
                throw new ArgumentException($"'{value}' is not an option of list box '{fieldName}'", "selectedValues");
            selected.Add(index);
        }

        int flags = PDFium.FPDFAnnot_GetFormFieldFlags(_formHandle, annot);
        if (selected.Count > 1 && (flags & PDFium.FPDF_FORMFLAG_CHOICE_MULTI_SELECT) == 0)
            throw new ArgumentException($"List box '{fieldName}' is not multi-select; give at most one value", "selectedValues");

        return selected;
    }

    // FORM_SetIndexSelected changes the focused widget's list; removing the focus commits the list
    // to the field (/V, /I) and regenerates its appearance.
    private void SelectOptions(IntPtr page, IntPtr annot, string fieldName, int optionCount, HashSet<int> selected)
    {
        FocusForEditing(annot, fieldName);
        try
        {
            for (int i = 0; i < optionCount; i++)
            {
                if (!selected.Contains(i) && PDFium.FORM_IsIndexSelected(_formHandle, page, i))
                    PDFium.FORM_SetIndexSelected(_formHandle, page, i, false);
            }
            foreach (int index in selected)
                PDFium.FORM_SetIndexSelected(_formHandle, page, index, true);
        }
        finally
        {
            PDFium.FORM_ForceToKillFocus(_formHandle);
        }

        for (int i = 0; i < optionCount; i++)
        {
            if (PDFium.FPDFAnnot_IsOptionSelected(_formHandle, annot, i) != selected.Contains(i))
                throw new InvalidOperationException($"PDFium did not apply the selection to list box '{fieldName}'");
        }
    }

    // The form filler edits only the focused widget. Callers remove the focus with
    // FORM_ForceToKillFocus before the page is closed.
    private void FocusForEditing(IntPtr annot, string fieldName)
    {
        if (!_formInitialized || !PDFium.FORM_SetFocusedAnnot(_formHandle, annot))
            throw new InvalidOperationException($"Form field '{fieldName}' cannot be edited: PDFium did not focus it");
    }

    private void CloseFieldAnnotation(IntPtr annot, IntPtr page)
    {
        PDFium.FPDFPage_CloseAnnot(annot);

        if (_formInitialized)
        {
            PDFium.FORM_OnBeforeClosePage(page, _formHandle);
        }
        PDFium.FPDF_ClosePage(page);
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
