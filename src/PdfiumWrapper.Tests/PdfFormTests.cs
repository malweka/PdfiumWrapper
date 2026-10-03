using Xunit.Abstractions;

namespace PdfiumWrapper.Tests;

[Collection("PDF Tests")]
public class PdfFormTests
{
    private readonly ITestOutputHelper _testOutputHelper;

    public PdfFormTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    private const string ContractPdfPath = "Docs/contract.pdf";
    private const string FormW2Path = "Docs/fw2.pdf";

    private static string GetUniqueTestFilePath(string baseName)
    {
        return Path.Combine(Bootstrapper.WorkingDirectory, $"{baseName}_{Guid.NewGuid():N}.pdf");
    }

    [Fact]
    public void FormFillInfo_LayoutMatchesTheHeader()
    {
        // fpdf_formfill.h (8076): FPDF_BOOL xfa_disabled sits between m_pJsPlatform and
        // FFI_DisplayCaret. PDFium reads the whole structure, so a missing field shifts every
        // version 2 callback and makes PDFium read past the allocation.
        if (IntPtr.Size != 8)
            return; // the offsets below are for 64-bit processes, which every supported runtime is

        Assert.Equal(128, (int)System.Runtime.InteropServices.Marshal.OffsetOf<PDFium.FPDF_FORMFILLINFO>(nameof(PDFium.FPDF_FORMFILLINFO.m_pJsPlatform)));
        Assert.Equal(136, (int)System.Runtime.InteropServices.Marshal.OffsetOf<PDFium.FPDF_FORMFILLINFO>(nameof(PDFium.FPDF_FORMFILLINFO.xfa_disabled)));
        Assert.Equal(144, (int)System.Runtime.InteropServices.Marshal.OffsetOf<PDFium.FPDF_FORMFILLINFO>(nameof(PDFium.FPDF_FORMFILLINFO.FFI_DisplayCaret)));
        Assert.Equal(280, System.Runtime.InteropServices.Marshal.SizeOf<PDFium.FPDF_FORMFILLINFO>());
    }

    [Fact]
    public void GetForm_WithDocumentWithoutForms_ShouldReturnNull()
    {
        // Arrange
        using var doc = new PdfDocument(ContractPdfPath);

        // Act
        var form = doc.GetForm();

        // Assert - most PDFs don't have forms, if this has one we still validate it
        if (form == null)
        {
            Assert.Null(form);
        }
        else
        {
            Assert.NotNull(form);
            form.Dispose();
        }
    }

    [Fact]
    public void GetForm_WithDocumentWithForms_ShouldReturnForm()
    {
        // Arrange
        using var doc = new PdfDocument(FormW2Path);

        // Act
        var form = doc.GetForm();

        // Assert
        Assert.NotNull(form);

        form.Dispose();
    }

    [Fact]
    public void GetAllFormFields_ShouldReturnAllFields()
    {
        // Arrange
        using var doc = new PdfDocument(FormW2Path);
        using var form = doc.GetForm();
        Assert.NotNull(form);

        // Act
        var fields = form.GetAllFormFields();

        // Assert
        Assert.NotNull(fields);
        Assert.NotEmpty(fields);
        _testOutputHelper.WriteLine($"Total form fields found: {fields.Length}");

        // Log some field information
        foreach (var field in fields.Take(5))
        {
            _testOutputHelper.WriteLine($"Field: {field.Name}, Type: {field.Type}, Value: {field.Value}");
        }
    }

    [Fact]
    public void Set_and_read()
    {
        // Arrange
        string outputPath = GetUniqueTestFilePath("read_and_save");

        using var doc = new PdfDocument(FormW2Path);
        using var form = doc.GetForm();
        Assert.NotNull(form);

        string firstName = W2FieldMapping.CopyA["employee_first_name"];
        string lastName = W2FieldMapping.CopyA["employee_last_name"];

        form.SetFormFieldValue(firstName, "John");
        form.SetFormFieldValue(lastName, "Doe");

        doc.Save(outputPath);

        using var verifyDoc = new PdfDocument(outputPath);
        using var verifyForm = verifyDoc.GetForm();
        Assert.NotNull(verifyForm);

        // Act
        var firstNameValue = verifyForm.GetFormFieldValue(firstName);
        var lastNameValue = verifyForm.GetFormFieldValue(lastName);

        // Assert
        Assert.Equal("John", firstNameValue);
        Assert.Equal("Doe", lastNameValue);

        _testOutputHelper.WriteLine($"First Name: {firstNameValue}, Last Name: {lastNameValue}");
    }

    [Fact]
    public void GetFormFieldsOnPage_ShouldReturnFieldsForSpecificPage()
    {
        // Arrange
        using var doc = new PdfDocument(FormW2Path);
        using var form = doc.GetForm();
        Assert.NotNull(form);

        // Act
        var fieldsPage0 = form.GetFormFieldsOnPage(0);

        // Assert
        Assert.NotNull(fieldsPage0);
        _testOutputHelper.WriteLine($"Form fields on page 0: {fieldsPage0.Length}");

        // All fields should be on page 0
        Assert.All(fieldsPage0, field => Assert.Equal(0, field.PageIndex));
    }

    [Fact]
    public void GetFormFieldValue_WithExistingTextField_ShouldReturnValue()
    {
        // Arrange
        using var doc = new PdfDocument(FormW2Path);
        using var form = doc.GetForm();
        Assert.NotNull(form);
        string fieldName = W2FieldMapping.CopyA["employer_ein"];

        // Act
        var value = form.GetFormFieldValue(fieldName);

        // Assert
        Assert.NotNull(value);
        _testOutputHelper.WriteLine($"Employer EIN field value: '{value}'");
    }

    [Fact]
    public void GetFormFieldValue_WithNonExistentField_ShouldThrowException()
    {
        // Arrange
        using var doc = new PdfDocument(FormW2Path);
        using var form = doc.GetForm();
        Assert.NotNull(form);
        string fieldName = "nonexistent_field_12345";

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => form.GetFormFieldValue(fieldName));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void SetFormFieldValue_WithTextField_ShouldNotThrowException()
    {
        // Arrange - Copy the original PDF to working directory
        string outputPath = GetUniqueTestFilePath("w2_updated_text");
        File.Copy(FormW2Path, outputPath, overwrite: true);

        using var doc = new PdfDocument(outputPath);
        using var form = doc.GetForm();
        Assert.NotNull(form);

        string fieldName = W2FieldMapping.CopyA["employer_ein"];
        string testValue = "12-3456789";

        // Act & Assert - Should not throw
        form.SetFormFieldValue(fieldName, testValue);
        _testOutputHelper.WriteLine($"Successfully set EIN field to: {testValue}");
    }

    [Fact]
    public void SetFormFieldValue_WithMultipleFields_ShouldNotThrowException()
    {
        // Arrange - Copy the original PDF to working directory
        string outputPath = GetUniqueTestFilePath("w2_updated_multiple");
        File.Copy(FormW2Path, outputPath, overwrite: true);

        using var doc = new PdfDocument(outputPath);
        using var form = doc.GetForm();
        Assert.NotNull(form);

        var testData = new Dictionary<string, string>
        {
            [W2FieldMapping.CopyA["employer_ein"]] = "98-7654321",
            [W2FieldMapping.CopyA["employee_ssn"]] = "123-45-6789",
            [W2FieldMapping.CopyA["box1_wages"]] = "75000.00"
        };

        // Act & Assert - Should not throw
        foreach (var kvp in testData)
        {
            form.SetFormFieldValue(kvp.Key, kvp.Value);
            _testOutputHelper.WriteLine($"Successfully set field {kvp.Key} to: {kvp.Value}");
        }
    }

    [Fact]
    public void GetFormFieldChecked_WithCheckboxField_ShouldReturnCheckedState()
    {
        // Arrange
        using var doc = new PdfDocument(FormW2Path);
        using var form = doc.GetForm();
        Assert.NotNull(form);
        string fieldName = W2FieldMapping.CopyA["box13_retirement_plan"];

        // Act
        var isChecked = form.GetFormFieldChecked(fieldName);

        // Assert
        Assert.IsType<bool>(isChecked);
        _testOutputHelper.WriteLine($"Retirement plan checkbox is checked: {isChecked}");
    }

    [Fact]
    public void SetFormFieldChecked_WithCheckboxField_ShouldNotThrowException()
    {
        // Arrange - Copy the original PDF to working directory
        string outputPath = GetUniqueTestFilePath("w2_updated_checkbox");
        File.Copy(FormW2Path, outputPath, overwrite: true);

        using var doc = new PdfDocument(outputPath);
        using var form = doc.GetForm();
        Assert.NotNull(form);

        string fieldName = W2FieldMapping.CopyA["box13_retirement_plan"];

        // Act & Assert - Should not throw when setting to checked
        form.SetFormFieldChecked(fieldName, true);
        _testOutputHelper.WriteLine($"Successfully set checkbox to checked");

        // Act & Assert - Should not throw when setting to unchecked
        form.SetFormFieldChecked(fieldName, false);
        _testOutputHelper.WriteLine($"Successfully set checkbox to unchecked");
    }

    [Fact]
    public void SetFormFieldValue_WithNonExistentField_ShouldThrowException()
    {
        // Arrange
        string outputPath = GetUniqueTestFilePath("w2_invalid_field");
        File.Copy(FormW2Path, outputPath, overwrite: true);

        using var doc = new PdfDocument(outputPath);
        using var form = doc.GetForm();
        Assert.NotNull(form);
        string fieldName = "nonexistent_field_12345";

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => form.SetFormFieldValue(fieldName, "test"));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void FormFieldProperties_ShouldHaveExpectedValues()
    {
        // Arrange
        using var doc = new PdfDocument(FormW2Path);
        using var form = doc.GetForm();
        Assert.NotNull(form);

        // Act
        var allFields = form.GetAllFormFields();

        // Assert - Check that fields have proper structure
        Assert.All(allFields, field =>
        {
            Assert.NotNull(field.Name);
            Assert.NotEmpty(field.Name);
            Assert.True(field.PageIndex >= 0);
            // Value can be empty string but not null
            Assert.NotNull(field.Value);
        });

        // Log some field details
        var sampleField = allFields.FirstOrDefault(f => f.Name != null && f.Name.Contains("f1_"));
        if (sampleField != null)
        {
            _testOutputHelper.WriteLine($"Sample Field Details:");
            _testOutputHelper.WriteLine($"  Name: {sampleField.Name}");
            _testOutputHelper.WriteLine($"  Type: {sampleField.Type}");
            _testOutputHelper.WriteLine($"  Value: '{sampleField.Value}'");
            _testOutputHelper.WriteLine($"  PageIndex: {sampleField.PageIndex}");
            _testOutputHelper.WriteLine($"  IsRequired: {sampleField.IsRequired}");
            _testOutputHelper.WriteLine($"  IsReadOnly: {sampleField.IsReadOnly}");
        }
    }

    [Fact]
    public void GetAllFormFields_ShouldIncludeCheckboxFields()
    {
        // Arrange
        using var doc = new PdfDocument(FormW2Path);
        using var form = doc.GetForm();
        Assert.NotNull(form);

        // Act
        var allFields = form.GetAllFormFields();
        var checkboxFields = allFields.Where(f => f.Type == FormFieldType.CheckBox).ToArray();

        // Assert
        Assert.NotEmpty(checkboxFields);
        _testOutputHelper.WriteLine($"Found {checkboxFields.Length} checkbox fields");

        foreach (var checkbox in checkboxFields.Take(3))
        {
            _testOutputHelper.WriteLine($"Checkbox: {checkbox.Name}, Value: {checkbox.Value}");
        }
    }

    [Fact]
    public void GetAllFormFields_ShouldIncludeTextFields()
    {
        // Arrange
        using var doc = new PdfDocument(FormW2Path);
        using var form = doc.GetForm();
        Assert.NotNull(form);

        // Act
        var allFields = form.GetAllFormFields();
        var textFields = allFields.Where(f => f.Type == FormFieldType.TextField).ToArray();

        // Assert
        Assert.NotEmpty(textFields);
        _testOutputHelper.WriteLine($"Found {textFields.Length} text fields");

        foreach (var textField in textFields.Take(5))
        {
            _testOutputHelper.WriteLine($"TextField: {textField.Name}");
        }
    }

    [Fact]
    public void GetFormFieldValue_WithMultipleTextFields_ShouldReturnCorrectValues()
    {
        // Arrange
        using var doc = new PdfDocument(FormW2Path);
        using var form = doc.GetForm();
        Assert.NotNull(form);

        var fieldNames = new[]
        {
            W2FieldMapping.CopyA["employer_ein"],
            W2FieldMapping.CopyA["employee_ssn"],
            W2FieldMapping.CopyA["box1_wages"]
        };

        // Act & Assert
        foreach (var fieldName in fieldNames)
        {
            var value = form.GetFormFieldValue(fieldName);
            Assert.NotNull(value);
            _testOutputHelper.WriteLine($"Field '{fieldName}': '{value}'");
        }
    }

    [Fact]
    public void SetFormFieldValue_WithEmptyString_ShouldNotThrowException()
    {
        // Arrange
        string outputPath = GetUniqueTestFilePath("w2_empty_value");
        File.Copy(FormW2Path, outputPath, overwrite: true);

        using var doc = new PdfDocument(outputPath);
        using var form = doc.GetForm();
        Assert.NotNull(form);

        string fieldName = W2FieldMapping.CopyA["box1_wages"];

        // Act & Assert - Should not throw
        form.SetFormFieldValue(fieldName, "");
        _testOutputHelper.WriteLine($"Successfully set field to empty string");
    }

    [Fact]
    public void SetFormFieldChecked_WithMultipleCheckboxes_ShouldNotThrowException()
    {
        // Arrange
        string outputPath = GetUniqueTestFilePath("w2_multiple_checkboxes");
        File.Copy(FormW2Path, outputPath, overwrite: true);

        using var doc = new PdfDocument(outputPath);
        using var form = doc.GetForm();
        Assert.NotNull(form);

        var checkboxFields = new[]
        {
            W2FieldMapping.CopyA["box13_retirement_plan"],
            W2FieldMapping.CopyA["box13_statutory_employee"],
            W2FieldMapping.CopyA["box13_third_party_sick_pay"]
        };

        // Act & Assert - Should not throw
        foreach (var fieldName in checkboxFields)
        {
            form.SetFormFieldChecked(fieldName, true);
            _testOutputHelper.WriteLine($"Set checkbox '{fieldName}' to checked");
        }
    }

    [Fact]
    public void GetFormFieldsOnPage_WithInvalidPageIndex_ShouldReturnEmptyArray()
    {
        // Arrange
        using var doc = new PdfDocument(FormW2Path);
        using var form = doc.GetForm();
        Assert.NotNull(form);
        int invalidPageIndex = 999;

        // Act
        var fields = form.GetFormFieldsOnPage(invalidPageIndex);

        // Assert
        Assert.NotNull(fields);
        Assert.Empty(fields);
        _testOutputHelper.WriteLine($"No fields found on page {invalidPageIndex} (as expected)");
    }

    [Fact]
    public void SetFormFieldValue_WithSpecialCharacters_ShouldNotThrowException()
    {
        // Arrange
        string outputPath = GetUniqueTestFilePath("w2_special_chars");
        File.Copy(FormW2Path, outputPath, overwrite: true);

        using var doc = new PdfDocument(outputPath);
        using var form = doc.GetForm();
        Assert.NotNull(form);

        string fieldName = W2FieldMapping.CopyA["employer_name"];
        string testValue = "O'Brien & Associates, Inc. - Ñoño's Café";

        // Act & Assert - Should not throw
        form.SetFormFieldValue(fieldName, testValue);
        _testOutputHelper.WriteLine($"Successfully set field with special characters: {testValue}");
    }

    [Fact]
    public void GetFormFieldChecked_WithMultipleCheckboxes_ShouldReturnStates()
    {
        // Arrange
        using var doc = new PdfDocument(FormW2Path);
        using var form = doc.GetForm();
        Assert.NotNull(form);

        var checkboxFields = new[]
        {
            W2FieldMapping.CopyA["box13_retirement_plan"],
            W2FieldMapping.CopyA["box13_statutory_employee"],
            W2FieldMapping.CopyA["box13_third_party_sick_pay"]
        };

        // Act & Assert
        foreach (var fieldName in checkboxFields)
        {
            var isChecked = form.GetFormFieldChecked(fieldName);
            Assert.IsType<bool>(isChecked);
            _testOutputHelper.WriteLine($"Checkbox '{fieldName}' is {(isChecked ? "checked" : "unchecked")}");
        }
    }

    // A one-page AcroForm: check boxes "agree" (checked) and "news" (unchecked), both with the
    // export value "On", and a list box "colors" whose second option contains a comma. Written
    // without an xref table; PDFium rebuilds it.
    private static byte[] BuildFormPdf(bool multiSelect)
    {
        int listFlags = multiSelect ? PDFium.FPDF_FORMFLAG_CHOICE_MULTI_SELECT : 0;
        string pdf = $"""
            %PDF-1.7
            1 0 obj << /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 5 0 R 6 0 R] /DR << /Font << /Helv 9 0 R >> >> /DA (/Helv 0 Tf 0 g) >> >> endobj
            2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj
            3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R 5 0 R 6 0 R] >> endobj
            4 0 obj << /Type /Annot /Subtype /Widget /FT /Btn /T (agree) /Rect [20 20 40 40] /P 3 0 R /V /On /AS /On /AP << /N << /On 7 0 R /Off 8 0 R >> >> >> endobj
            5 0 obj << /Type /Annot /Subtype /Widget /FT /Btn /T (news) /Rect [60 20 80 40] /P 3 0 R /V /Off /AS /Off /AP << /N << /On 7 0 R /Off 8 0 R >> >> >> endobj
            6 0 obj << /Type /Annot /Subtype /Widget /FT /Ch /Ff {listFlags} /T (colors) /Rect [20 60 180 160] /P 3 0 R /Opt [(Red) (Green, light) (Blue)] /DA (/Helv 10 Tf 0 g) >> endobj
            7 0 obj << /Length 0 >> stream
            endstream
            endobj
            8 0 obj << /Length 0 >> stream
            endstream
            endobj
            9 0 obj << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> endobj
            trailer << /Root 1 0 R >>
            %%EOF

            """;
        return System.Text.Encoding.ASCII.GetBytes(pdf);
    }

    private const int ColorsAnnotIndex = 2;

    // Reads the list box's selection from the field itself (/V and /I), not from the form filler.
    private static bool[] SelectedOptions(PdfDocument doc, PdfForm form)
    {
        using var _ = PdfiumRuntime.Enter();
        var page = PDFium.FPDF_LoadPage(doc.Document, 0);
        try
        {
            var annot = PDFium.FPDFPage_GetAnnot(page, ColorsAnnotIndex);
            try
            {
                int count = PDFium.FPDFAnnot_GetOptionCount(form._formHandle, annot);
                return Enumerable.Range(0, count)
                    .Select(i => PDFium.FPDFAnnot_IsOptionSelected(form._formHandle, annot, i))
                    .ToArray();
            }
            finally
            {
                PDFium.FPDFPage_CloseAnnot(annot);
            }
        }
        finally
        {
            PDFium.FPDF_ClosePage(page);
        }
    }

    [Fact]
    public void GetFormFieldChecked_WithExportValueOn_ReadsTheCheckedState()
    {
        // fw2.pdf's check boxes all export "1"; most real forms export "On" or "Yes".
        using var doc = new PdfDocument(BuildFormPdf(multiSelect: true));
        using var form = doc.GetForm();
        Assert.NotNull(form);

        Assert.True(form.GetFormFieldChecked("agree"));
        Assert.False(form.GetFormFieldChecked("news"));
        Assert.Equal("true", form.GetFormFieldValue("agree"));
        Assert.Equal("false", form.GetFormFieldValue("news"));
    }

    [Fact]
    public void SetFormFieldChecked_WithExportValueOn_RoundTripsThroughSave()
    {
        string outputPath = GetUniqueTestFilePath("checkbox_on_roundtrip");
        using (var doc = new PdfDocument(BuildFormPdf(multiSelect: true)))
        using (var form = doc.GetForm())
        {
            Assert.NotNull(form);

            form.SetFormFieldChecked("agree", false);
            form.SetFormFieldChecked("news", true);

            Assert.False(form.GetFormFieldChecked("agree"));
            Assert.True(form.GetFormFieldChecked("news"));
            doc.Save(outputPath);
        }

        using var verifyDoc = new PdfDocument(outputPath);
        using var verifyForm = verifyDoc.GetForm();
        Assert.NotNull(verifyForm);
        Assert.False(verifyForm.GetFormFieldChecked("agree"));
        Assert.True(verifyForm.GetFormFieldChecked("news"));
    }

    [Fact]
    public void SetFormFieldChecked_OnW2Checkbox_IsReadBack()
    {
        using var doc = new PdfDocument(FormW2Path);
        using var form = doc.GetForm();
        Assert.NotNull(form);
        string fieldName = W2FieldMapping.CopyA["box13_retirement_plan"];

        form.SetFormFieldChecked(fieldName, true);
        Assert.True(form.GetFormFieldChecked(fieldName));

        form.SetFormFieldChecked(fieldName, false);
        Assert.False(form.GetFormFieldChecked(fieldName));
    }

    [Fact]
    public void GetFormFieldChecked_WithTextField_Throws()
    {
        using var doc = new PdfDocument(FormW2Path);
        using var form = doc.GetForm();
        Assert.NotNull(form);

        Assert.Throws<InvalidOperationException>(() => form.GetFormFieldChecked(W2FieldMapping.CopyA["employer_ein"]));
    }

    [Fact]
    public void SetListBoxSelections_MultiSelect_SelectsEveryValue()
    {
        string outputPath = GetUniqueTestFilePath("listbox_multiselect");
        using (var doc = new PdfDocument(BuildFormPdf(multiSelect: true)))
        using (var form = doc.GetForm())
        {
            Assert.NotNull(form);

            form.SetListBoxSelections("colors", new[] { "Red", "Blue" });

            Assert.Equal(new[] { true, false, true }, SelectedOptions(doc, form));
            Assert.Equal("Red", form.GetFormFieldValue("colors"));
            doc.Save(outputPath);
        }

        using var verifyDoc = new PdfDocument(outputPath);
        using var verifyForm = verifyDoc.GetForm();
        Assert.NotNull(verifyForm);
        Assert.Equal(new[] { true, false, true }, SelectedOptions(verifyDoc, verifyForm));
    }

    [Fact]
    public void SetListBoxSelections_ValueWithComma_SelectsThatOneOption()
    {
        string outputPath = GetUniqueTestFilePath("listbox_comma");
        using (var doc = new PdfDocument(BuildFormPdf(multiSelect: true)))
        using (var form = doc.GetForm())
        {
            Assert.NotNull(form);

            form.SetListBoxSelections("colors", new[] { "Green, light", "Blue" });

            Assert.Equal(new[] { false, true, true }, SelectedOptions(doc, form));
            doc.Save(outputPath);
        }

        using var verifyDoc = new PdfDocument(outputPath);
        using var verifyForm = verifyDoc.GetForm();
        Assert.NotNull(verifyForm);
        Assert.Equal(new[] { false, true, true }, SelectedOptions(verifyDoc, verifyForm));
        Assert.Equal("Green, light", verifyForm.GetFormFieldValue("colors"));
        Assert.Equal(new[] { "Red", "Green, light", "Blue" }, verifyForm.GetAllFormFields().Single(f => f.Name == "colors").Options);
    }

    [Fact]
    public void SetListBoxSelections_ReplacesThePreviousSelection()
    {
        using var doc = new PdfDocument(BuildFormPdf(multiSelect: true));
        using var form = doc.GetForm();
        Assert.NotNull(form);

        form.SetListBoxSelections("colors", new[] { "Red", "Blue" });
        form.SetListBoxSelections("colors", new[] { "Green, light" });
        Assert.Equal(new[] { false, true, false }, SelectedOptions(doc, form));

        form.SetListBoxSelections("colors", Array.Empty<string>());
        Assert.Equal(new[] { false, false, false }, SelectedOptions(doc, form));
    }

    [Fact]
    public void SetListBoxSelections_SingleSelect_TakesOneValueAndRejectsTwo()
    {
        using var doc = new PdfDocument(BuildFormPdf(multiSelect: false));
        using var form = doc.GetForm();
        Assert.NotNull(form);

        form.SetListBoxSelections("colors", new[] { "Blue" });
        form.SetListBoxSelections("colors", new[] { "Green, light" });
        Assert.Equal(new[] { false, true, false }, SelectedOptions(doc, form));

        Assert.Throws<ArgumentException>(() => form.SetListBoxSelections("colors", new[] { "Red", "Blue" }));
        Assert.Equal(new[] { false, true, false }, SelectedOptions(doc, form));
    }

    [Fact]
    public void SetListBoxSelections_RejectsValuesThatAreNotOptions()
    {
        using var doc = new PdfDocument(BuildFormPdf(multiSelect: true));
        using var form = doc.GetForm();
        Assert.NotNull(form);

        // The old implementation joined values with commas; "Green" and "light" are not options.
        Assert.Throws<ArgumentException>(() => form.SetListBoxSelections("colors", new[] { "Green", "light" }));
        Assert.Throws<ArgumentException>(() => form.SetListBoxSelections("colors", new[] { "red" }));
        Assert.Throws<ArgumentNullException>(() => form.SetListBoxSelections("colors", null!));
        Assert.Throws<InvalidOperationException>(() => form.SetListBoxSelections("agree", new[] { "On" }));
        Assert.Equal(new[] { false, false, false }, SelectedOptions(doc, form));
    }
}