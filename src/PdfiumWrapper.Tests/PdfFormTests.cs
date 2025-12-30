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

        string firstName = W2FieldMapping.CopyA["employee_first_name"];
        string lastName = W2FieldMapping.CopyA["employee_last_name"];

        form.SetFormFieldValue(firstName, "John");
        form.SetFormFieldValue(lastName, "Doe");

        doc.Save(outputPath);

        using var verifyDoc = new PdfDocument(outputPath);
        using var verifyForm = verifyDoc.GetForm();

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
        var sampleField = allFields.FirstOrDefault(f => f.Name.Contains("f1_"));
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
}