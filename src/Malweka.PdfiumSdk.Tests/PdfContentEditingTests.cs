using System.Drawing;
using SkiaSharp;
using Xunit;

namespace Malweka.PdfiumSdk.Tests;

[Collection("PDF Tests")]
public class PdfContentEditingTests : IDisposable
{
    private readonly List<string> _tempDirectories = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirectories)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    private string CreateTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"PdfiumTests_Content_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        _tempDirectories.Add(tempDir);
        return tempDir;
    }

    private byte[] GenerateSampleImage()
    {
        // Generate a 10x10 red square PNG using SkiaSharp
        using var bitmap = new SKBitmap(10, 10);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Red);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void AddText_ShouldIncreaseObjectCount()
    {
        // Arrange
        using var doc = new PdfDocument();
        using var page = doc.AddPage();
        int initialCount = page.ObjectCount;

        // Act
        page.AddText("Hello World", 100, 100);
        page.GenerateContent();

        // Assert
        Assert.Equal(initialCount + 1, page.ObjectCount);
    }

    [Fact]
    public void AddRectangle_ShouldIncreaseObjectCount()
    {
        // Arrange
        using var doc = new PdfDocument();
        using var page = doc.AddPage();
        int initialCount = page.ObjectCount;

        // Act
        page.AddRectangle(100, 100, 200, 150, Color.Blue, Color.Black);
        page.GenerateContent();

        // Assert
        Assert.Equal(initialCount + 1, page.ObjectCount);
    }

    [Fact]
    public void AddImage_ShouldIncreaseObjectCount()
    {
        // Arrange
        using var doc = new PdfDocument();
        using var page = doc.AddPage();
        int initialCount = page.ObjectCount;
        var imageBytes = GenerateSampleImage();

        // Act
        page.AddImage(imageBytes, 50, 50, 100, 100);
        page.GenerateContent();

        // Assert
        Assert.Equal(initialCount + 1, page.ObjectCount);
    }

    [Fact]
    public void AddPath_ShouldIncreaseObjectCount()
    {
        // Arrange
        using var doc = new PdfDocument();
        using var page = doc.AddPage();
        int initialCount = page.ObjectCount;

        // Act
        page.AddPath();
        page.GenerateContent();

        // Assert
        Assert.Equal(initialCount + 1, page.ObjectCount);
    }

    [Fact]
    public void RemoveObject_ShouldDecreaseObjectCount()
    {
        // Arrange
        using var doc = new PdfDocument();
        using var page = doc.AddPage();
        var textObj = page.AddText("To be removed", 50, 50);
        page.GenerateContent();
        int countAfterAdd = page.ObjectCount;
        Assert.True(countAfterAdd > 0);

        // Act
        bool removed = page.RemoveObject(textObj);
        page.GenerateContent();

        // Assert
        Assert.True(removed);
        Assert.Equal(countAfterAdd - 1, page.ObjectCount);
    }

    [Fact]
    public void AddMultipleObjects_ShouldReflectInCount()
    {
        // Arrange
        using var doc = new PdfDocument();
        using var page = doc.AddPage();

        // Act
        page.AddText("Title", 100, 700, fontSize: 24);
        page.AddText("Body text", 100, 650);
        page.AddRectangle(50, 50, 500, 700, strokeColor: Color.Black);
        page.GenerateContent();

        // Assert
        Assert.Equal(3, page.ObjectCount);
    }

    [Fact]
    public void GetObject_ShouldReturnValidHandle()
    {
        // Arrange
        using var doc = new PdfDocument();
        using var page = doc.AddPage();
        page.AddText("Test Object", 100, 100);
        page.GenerateContent();

        // Act
        var objHandle = page.GetObject(0);

        // Assert
        Assert.NotEqual(IntPtr.Zero, objHandle);
    }

    [Fact]
    public void EditPageContent_AndSave_ShouldProduceValidFile()
    {
        // Arrange
        using var doc = new PdfDocument();
        using var page = doc.AddPage();

        page.AddText("Hello PDF World", 50, 700, fontSize: 18);
        page.AddRectangle(50, 650, 200, 2, fillColor: Color.Red);

        var imageBytes = GenerateSampleImage();
        page.AddImage(imageBytes, 50, 500, 50, 50);

        page.GenerateContent();

        var tempDir = CreateTempDirectory();
        var outputPath = Path.Combine(tempDir, "edited_content.pdf");

        // Act
        doc.Save(outputPath);

        // Assert
        Assert.True(File.Exists(outputPath));
        var info = new FileInfo(outputPath);
        Assert.True(info.Length > 0);

        // Verify content by reloading
        using var loadedDoc = new PdfDocument(outputPath);
        using var loadedPage = loadedDoc.GetPage(0);

        Assert.Equal(3, loadedPage.ObjectCount);
        // Note: ExtractText might verify text content, but simple object count is a good start.
        string text = loadedPage.ExtractText();
        Assert.Contains("Hello PDF World", text);
    }
}
