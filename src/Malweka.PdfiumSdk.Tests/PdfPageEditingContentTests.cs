using System.Drawing;
using SkiaSharp;
using Xunit;

namespace Malweka.PdfiumSdk.Tests;

[Collection("PDF Tests")]
public class PdfPageEditingContentTests : IDisposable
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
        var tempDir = Path.Combine(Path.GetTempPath(), $"PdfiumTests_EditingContent_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        _tempDirectories.Add(tempDir);
        return tempDir;
    }

    private byte[] GenerateSampleImage()
    {
        // Generate a simple 100x100 red square image
        using var bitmap = new SKBitmap(100, 100);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Red);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void AddText_ShouldAddTextObjectToPage()
    {
        // Arrange
        using var doc = new PdfDocument();
        using var page = doc.AddPage();
        int initialCount = page.ObjectCount;

        // Act
        var textObj = page.AddText("Hello World", 100, 100);
        page.GenerateContent();

        // Assert
        Assert.NotNull(textObj);
        Assert.Equal(initialCount + 1, page.ObjectCount);
    }

    [Fact]
    public void AddRectangle_ShouldAddPathObjectToPage()
    {
        // Arrange
        using var doc = new PdfDocument();
        using var page = doc.AddPage();
        int initialCount = page.ObjectCount;

        // Act
        var rectObj = page.AddRectangle(50, 50, 200, 100, Color.Blue, Color.Black);
        page.GenerateContent();

        // Assert
        Assert.NotNull(rectObj);
        Assert.Equal(initialCount + 1, page.ObjectCount);
    }

    [Fact]
    public void AddImage_ShouldAddImageObjectToPage()
    {
        // Arrange
        using var doc = new PdfDocument();
        using var page = doc.AddPage();
        int initialCount = page.ObjectCount;
        byte[] imageBytes = GenerateSampleImage();

        // Act
        var imageObj = page.AddImage(imageBytes, 100, 100, 100, 100);
        page.GenerateContent();

        // Assert
        Assert.NotNull(imageObj);
        Assert.Equal(initialCount + 1, page.ObjectCount);
    }

    [Fact]
    public void RemoveObject_ShouldRemoveObjectFromPage()
    {
        // Arrange
        using var doc = new PdfDocument();
        using var page = doc.AddPage();
        var textObj = page.AddText("To be removed", 100, 100);
        page.GenerateContent();
        int countAfterAdd = page.ObjectCount;

        // Act
        bool removed = page.RemoveObject(textObj);
        page.GenerateContent();

        // Assert
        Assert.True(removed);
        Assert.Equal(countAfterAdd - 1, page.ObjectCount);
    }
}
