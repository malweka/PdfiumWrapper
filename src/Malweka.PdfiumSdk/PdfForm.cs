using System.Runtime.InteropServices;

namespace Malweka.PdfiumSdk;

public class PdfForm : IDisposable
{
    private IntPtr _document;
    private int _pageCount;
    private IntPtr _formHandle;
    private bool _formInitialized;
    private PDFium.FPDF_FORMFILLINFO _formInfo;
    private GCHandle _formInfoHandle;
    private bool _disposed;
    private readonly object _lock = new object();

    internal bool HasFormFields => _formInitialized;

    internal PdfForm(IntPtr document, int pageCount)
    {
        _document = document;
        _pageCount = pageCount;
        InitializeFormEnvironment();
    }

    private void InitializeFormEnvironment()
    {
        // Create a minimal form fill info structure
        // This must remain pinned in memory for the lifetime of the form environment
        _formInfo = new PDFium.FPDF_FORMFILLINFO
        {
            version = 2  // Use version 2 for better compatibility
        };

        // Pin the structure in memory so PDFium can safely access it
        _formInfoHandle = GCHandle.Alloc(_formInfo, GCHandleType.Pinned);

        try
        {
            _formHandle = PDFium.FPDFDOC_InitFormFillEnvironment(_document, ref _formInfo);
            _formInitialized = _formHandle != IntPtr.Zero;
        }
        catch
        {
            // If initialization fails, clean up the pinned handle
            if (_formInfoHandle.IsAllocated)
            {
                _formInfoHandle.Free();
            }
            throw;
        }
    }

    public FormField[] GetAllFormFields()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, typeof(PdfForm));
            
            var fields = new List<FormField>();
            for (int pageIndex = 0; pageIndex < _pageCount; pageIndex++)
            {
                var pageFields = GetFormFieldsOnPageInternal(pageIndex);
                fields.AddRange(pageFields);
            }
            return fields.ToArray();
        }
    }

    public FormField[] GetFormFieldsOnPage(int pageIndex)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, typeof(PdfForm));
            return GetFormFieldsOnPageInternal(pageIndex);
        }
    }

    private FormField[] GetFormFieldsOnPageInternal(int pageIndex)
    {
        var fields = new List<FormField>();
        var page = PDFium.FPDF_LoadPage(_document, pageIndex);

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

    private FormField ExtractFormField(IntPtr annot, int pageIndex)
    {
        // Get field name
        ulong nameLength = PDFium.FPDFAnnot_GetFormFieldName(_formHandle, annot, IntPtr.Zero, 0);
        if (nameLength == 0)
            return null;

        var nameBuffer = Marshal.AllocHGlobal((int)nameLength);
        try
        {
            PDFium.FPDFAnnot_GetFormFieldName(_formHandle, annot, nameBuffer, nameLength);
            string name = Marshal.PtrToStringUni(nameBuffer);

            // Get field type
            int type = PDFium.FPDFAnnot_GetFormFieldType(_formHandle, annot);

            // Get field value
            string value = GetAnnotFieldValue(annot, type);

            // Get flags
            int flags = PDFium.FPDFAnnot_GetFormFieldFlags(_formHandle, annot);

            // Get options for combo/list boxes
            List<string> options = null;
            if (type == PDFium.FPDF_FORMFIELD_COMBOBOX || type == PDFium.FPDF_FORMFIELD_LISTBOX)
            {
                options = GetFieldOptions(annot);
            }

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
        finally
        {
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private string GetAnnotFieldValue(IntPtr annot, int fieldType)
    {
        switch (fieldType)
        {
            case PDFium.FPDF_FORMFIELD_CHECKBOX:
            case PDFium.FPDF_FORMFIELD_RADIOBUTTON:
                bool isChecked = PDFium.FPDFAnnot_IsChecked(_formHandle, annot);
                return isChecked ? "true" : "false";

            case PDFium.FPDF_FORMFIELD_COMBOBOX:
            case PDFium.FPDF_FORMFIELD_LISTBOX:
                // For combo/list boxes, get the current value
                ulong valueLength = PDFium.FPDFAnnot_GetFormFieldValue(_formHandle, annot, IntPtr.Zero, 0);
                if (valueLength > 0)
                {
                    var valueBuffer = Marshal.AllocHGlobal((int)valueLength);
                    try
                    {
                        PDFium.FPDFAnnot_GetFormFieldValue(_formHandle, annot, valueBuffer, valueLength);
                        return Marshal.PtrToStringUni(valueBuffer);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(valueBuffer);
                    }
                }
                return string.Empty;

            case PDFium.FPDF_FORMFIELD_TEXTFIELD:
            default:
                // Standard text field
                ulong textLength = PDFium.FPDFAnnot_GetFormFieldValue(_formHandle, annot, IntPtr.Zero, 0);
                if (textLength > 0)
                {
                    var textBuffer = Marshal.AllocHGlobal((int)textLength);
                    try
                    {
                        PDFium.FPDFAnnot_GetFormFieldValue(_formHandle, annot, textBuffer, textLength);
                        return Marshal.PtrToStringUni(textBuffer);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(textBuffer);
                    }
                }
                return string.Empty;
        }
    }

    private List<string> GetFieldOptions(IntPtr annot)
    {
        var options = new List<string>();
        int optionCount = PDFium.FPDFAnnot_GetOptionCount(_formHandle, annot);

        for (int i = 0; i < optionCount; i++)
        {
            ulong labelLength = PDFium.FPDFAnnot_GetOptionLabel(_formHandle, annot, i, IntPtr.Zero, 0);
            if (labelLength > 0)
            {
                var labelBuffer = Marshal.AllocHGlobal((int)labelLength);
                try
                {
                    PDFium.FPDFAnnot_GetOptionLabel(_formHandle, annot, i, labelBuffer, labelLength);
                    string label = Marshal.PtrToStringUni(labelBuffer);
                    options.Add(label);
                }
                finally
                {
                    Marshal.FreeHGlobal(labelBuffer);
                }
            }
        }

        return options;
    }

    public string GetFormFieldValue(string fieldName)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, typeof(PdfForm));
            
            var field = FindFormField(fieldName);
            if (field == null)
                throw new ArgumentException($"Form field '{fieldName}' not found");

            return field.Value;
        }
    }

    public void SetFormFieldValue(string fieldName, string value)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, typeof(PdfForm));
            
            var fieldInfo = FindFormFieldWithAnnotation(fieldName);
            if (fieldInfo == null)
                throw new ArgumentException($"Form field '{fieldName}' not found");

            try
            {
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
                    ulong exportLength = PDFium.FPDFAnnot_GetFormFieldExportValue(_formHandle, annot, IntPtr.Zero, 0);
                    if (exportLength > 0)
                    {
                        var exportBuffer = Marshal.AllocHGlobal((int)exportLength);
                        try
                        {
                            PDFium.FPDFAnnot_GetFormFieldExportValue(_formHandle, annot, exportBuffer, exportLength);
                            string exportValue = Marshal.PtrToStringUni(exportBuffer);
                            PDFium.FPDFAnnot_SetStringValue(annot, "V", exportValue);
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(exportBuffer);
                        }
                    }
                    else
                    {
                        PDFium.FPDFAnnot_SetStringValue(annot, "V", "Yes");
                    }
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
        string value = GetFormFieldValue(fieldName);
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
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, typeof(PdfForm));
            
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
    }

    private FormField FindFormField(string fieldName)
    {
        for (int pageIndex = 0; pageIndex < _pageCount; pageIndex++)
        {
            var pageFields = GetFormFieldsOnPageInternal(pageIndex);
            var field = pageFields.FirstOrDefault(f => f.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
            if (field != null)
                return field;
        }
        return null;
    }

    private (FormField field, IntPtr annot, IntPtr page)? FindFormFieldWithAnnotation(string fieldName)
    {
        for (int pageIndex = 0; pageIndex < _pageCount; pageIndex++)
        {
            var page = PDFium.FPDF_LoadPage(_document, pageIndex);

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
                    // Get field name
                    ulong nameLength = PDFium.FPDFAnnot_GetFormFieldName(_formHandle, annot, IntPtr.Zero, 0);
                    if (nameLength > 0)
                    {
                        var nameBuffer = Marshal.AllocHGlobal((int)nameLength);
                        try
                        {
                            PDFium.FPDFAnnot_GetFormFieldName(_formHandle, annot, nameBuffer, nameLength);
                            string name = Marshal.PtrToStringUni(nameBuffer);

                            if (name.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                            {
                                var field = ExtractFormField(annot, pageIndex);
                                return (field, annot, page);
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(nameBuffer);
                        }
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

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            if (_formInitialized && _formHandle != IntPtr.Zero)
            {
                PDFium.FPDFDOC_ExitFormFillEnvironment(_formHandle);
                _formHandle = IntPtr.Zero;
                _formInitialized = false;
            }

            // Free the pinned GCHandle
            if (_formInfoHandle.IsAllocated)
            {
                _formInfoHandle.Free();
            }

            _disposed = true;
        }
    }
}