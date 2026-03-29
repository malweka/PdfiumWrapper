using System.Drawing;
using Xunit;

namespace PdfiumWrapper.Tests;

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
        // Generate a simple 100x100 red square PNG (BGRA pixel order)
        const int size = 100;
        const int stride = size * 4;
        var pixels = new byte[stride * size];

        // Fill with red in BGRA order: B=0, G=0, R=255, A=255
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 0] = 0;   // B
            pixels[i + 1] = 0;   // G
            pixels[i + 2] = 255; // R
            pixels[i + 3] = 255; // A
        }

        return PngEncoder.Encode(pixels, size, size, stride);
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
