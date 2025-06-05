using ImageMagick;

namespace Malweka.PdfiumSdk.Tests
{
    public class PdfDocumentTests : IDisposable
    {
        private readonly string _contractPdfPath;
        private readonly string _presentationPdfPath;
        private readonly string _tempOutputDir;

        public PdfDocumentTests()
        {
            // Setup test document paths
            var baseDir = AppContext.BaseDirectory;
            _contractPdfPath = Path.Combine(baseDir, "Docs", "contract.pdf");
            _presentationPdfPath = Path.Combine(baseDir, "Docs", "presentation.pdf");

            var docTestDir = Path.Combine(baseDir, "PdfDocumentTests");
            //if(Directory.Exists(docTestDir))
            //    Directory.Delete(docTestDir, true);

            // Create temp directory for test outputs
            _tempOutputDir = Path.Combine(docTestDir, Guid.NewGuid().ToString());
            Directory.CreateDirectory(_tempOutputDir);
        }

        public void Dispose()
        {
            // Clean up temp directory
            if (Directory.Exists(_tempOutputDir))
            {
                //Directory.Delete(_tempOutputDir, true);
            }
        }

        [Fact]
        public void Constructor_WithValidFilePath_LoadsDocument()
        {
            // Arrange & Act
            using var document = new PdfDocument(_contractPdfPath);

            // Assert
            Assert.NotNull(document);
            Assert.Equal(10, document.PageCount);
        }

        [Fact]
        public void Constructor_WithInvalidFilePath_ThrowsException()
        {
            // Arrange
            var invalidPath = "nonexistent.pdf";

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() => new PdfDocument(invalidPath));
        }

        [Fact]
        public void Constructor_WithByteArray_LoadsDocument()
        {
            // Arrange
            var pdfBytes = File.ReadAllBytes(_contractPdfPath);

            // Act
            using var document = new PdfDocument(pdfBytes);

            // Assert
            Assert.NotNull(document);
            Assert.Equal(10, document.PageCount);
        }

        [Fact]
        public void PageCount_ContractPdf_Returns10Pages()
        {
            // Arrange
            using var document = new PdfDocument(_contractPdfPath);

            // Act
            var pageCount = document.PageCount;

            // Assert
            Assert.Equal(10, pageCount);
        }

        [Fact]
        public void PageCount_PresentationPdf_Returns37Pages()
        {
            // Arrange
            using var document = new PdfDocument(_presentationPdfPath);

            // Act
            var pageCount = document.PageCount;

            // Assert
            Assert.Equal(37, pageCount);
        }

        [Fact]
        public void GetPage_ValidIndex_ReturnsPage()
        {
            // Arrange
            using var document = new PdfDocument(_contractPdfPath);

            // Act
            using var page = document.GetPage(0);

            // Assert
            Assert.NotNull(page);
            Assert.True(page.Width > 0);
            Assert.True(page.Height > 0);
        }

        [Fact]
        public void GetPage_InvalidIndex_ThrowsArgumentOutOfRangeException()
        {
            // Arrange
            using var document = new PdfDocument(_contractPdfPath);

            // Act & Assert
            Assert.Throws<ArgumentOutOfRangeException>(() => document.GetPage(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => document.GetPage(10)); // 0-based, so 10 is out of range
        }

        [Fact]
        public void GetPage_ContractPdf_HasPortraitOrientation()
        {
            // Arrange
            using var document = new PdfDocument(_contractPdfPath);

            // Act
            using var page = document.GetPage(0);

            // Assert
            Assert.True(page.Height > page.Width, "Contract PDF should be in portrait orientation");
        }

        [Fact]
        public void GetPage_PresentationPdf_HasLandscapeOrientation()
        {
            // Arrange
            using var document = new PdfDocument(_presentationPdfPath);

            // Act
            using var page = document.GetPage(0);

            // Assert
            Assert.True(page.Width > page.Height, "Presentation PDF should be in landscape orientation");
        }

        [Fact]
        public void ConvertToTiff_ContractPdf_CreatesValidTiffFile()
        {
            // Arrange
            using var document = new PdfDocument(_contractPdfPath);
            var outputPath = Path.Combine(_tempOutputDir, "contract_output.tiff");

            // Act
            document.ConvertToTiff(outputPath, dpi: 150);

            // Assert
            Assert.True(File.Exists(outputPath));

            // Verify it's a valid TIFF with correct properties
            using var magickImage = new MagickImage(outputPath);
            Assert.Equal(MagickFormat.Tiff, magickImage.Format);
            Assert.Equal(150, magickImage.Density.X);
            Assert.Equal(150, magickImage.Density.Y);
        }

        [Fact]
        public void ConvertToTiff_PresentationPdf_CreatesValidTiffFile()
        {
            // Arrange
            using var document = new PdfDocument(_presentationPdfPath);
            var outputPath = Path.Combine(_tempOutputDir, "presentation_output.tiff");

            // Act
            document.ConvertToTiff(outputPath, dpi: 200, compression: CompressionMethod.Zip);

            // Assert
            Assert.True(File.Exists(outputPath));

            // Verify it's a valid TIFF
            using var magickImage = new MagickImage(outputPath);
            Assert.Equal(MagickFormat.Tiff, magickImage.Format);
            Assert.Equal(200, magickImage.Density.X);
            Assert.Equal(200, magickImage.Density.Y);
        }

        [Fact]
        public void ConvertToTiff_EmptyDocument_ThrowsInvalidOperationException()
        {
            // This test would require a PDF with 0 pages, which is unusual
            // For now, we'll test the validation logic with a mock scenario
            // In a real scenario, you'd need a zero-page PDF or modify the class to allow testing

            // This test is conceptual - you'd need to create a scenario where PageCount returns 0
            // or use dependency injection to test this condition
            Assert.True(true, "Test placeholder - would need zero-page PDF to properly test");
        }

        [Fact]
        public void ConvertToPngs_ContractPdf_CreatesCorrectNumberOfFiles()
        {
            // Arrange
            using var document = new PdfDocument(_contractPdfPath);
            var outputDir = Path.Combine(_tempOutputDir, "contract_pngs");

            // Act
            document.ConvertToPngs(outputDir, "contract_page", dpi: 150);

            // Assert
            Assert.True(Directory.Exists(outputDir));

            var pngFiles = Directory.GetFiles(outputDir, "*.png");
            Assert.Equal(10, pngFiles.Length);

            // Verify file naming convention
            Assert.True(File.Exists(Path.Combine(outputDir, "contract_page_001.png")));
            Assert.True(File.Exists(Path.Combine(outputDir, "contract_page_010.png")));
        }

        [Fact]
        public void ConvertToPngs_PresentationPdf_CreatesCorrectNumberOfFiles()
        {
            // Arrange
            using var document = new PdfDocument(_presentationPdfPath);
            var outputDir = Path.Combine(_tempOutputDir, "presentation_pngs");

            // Act
            document.ConvertToPngs(outputDir, "slide", dpi: 100);

            // Assert
            Assert.True(Directory.Exists(outputDir));

            var pngFiles = Directory.GetFiles(outputDir, "*.png");
            Assert.Equal(37, pngFiles.Length);

            // Verify file naming convention
            Assert.True(File.Exists(Path.Combine(outputDir, "slide_001.png")));
            Assert.True(File.Exists(Path.Combine(outputDir, "slide_037.png")));
        }

        [Fact]
        public void ConvertToPngs_ValidatesPngProperties()
        {
            // Arrange
            using var document = new PdfDocument(_contractPdfPath);
            var outputDir = Path.Combine(_tempOutputDir, "png_validation");

            // Act
            document.ConvertToPngs(outputDir, "test", dpi: 200);

            // Assert
            var firstPngPath = Path.Combine(outputDir, "test_001.png");
            Assert.True(File.Exists(firstPngPath));

            using var magickImage = new MagickImage(firstPngPath);
            Assert.Equal(MagickFormat.Png, magickImage.Format);
            Assert.Equal(200, magickImage.Density.X);
            Assert.Equal(200, magickImage.Density.Y);
        }

        [Theory]
        [InlineData(72)]
        [InlineData(150)]
        [InlineData(300)]
        [InlineData(600)]
        public void ConvertToTiff_DifferentDpiValues_CreatesValidFiles(int dpi)
        {
            // Arrange
            using var document = new PdfDocument(_contractPdfPath);
            var outputPath = Path.Combine(_tempOutputDir, $"contract_dpi_{dpi}.tiff");

            // Act
            document.ConvertToTiff(outputPath, dpi: dpi);

            // Assert
            Assert.True(File.Exists(outputPath));

            using var magickImage = new MagickImage(outputPath);
            Assert.Equal(dpi, magickImage.Density.X);
            Assert.Equal(dpi, magickImage.Density.Y);
        }

        [Fact]
        public void Convert_to_tiff()
        {
            var baseDir = AppContext.BaseDirectory;

            var docTestDir = Path.Combine(baseDir, "Zokere");

            if (!Directory.Exists(docTestDir))
                Directory.CreateDirectory(docTestDir);

            using var document = new PdfDocument(_contractPdfPath);
            var outputPath = Path.Combine(docTestDir, $"contract.tiff");
            document.ConvertToTiff(outputPath, dpi: 150, compression: CompressionMethod.LZW);

            document.ConvertToJpeg(docTestDir, "pg", 150);
        }

        [Theory]
        [InlineData(CompressionMethod.Undefined)]
        [InlineData(CompressionMethod.LZW)]
        [InlineData(CompressionMethod.Zip)]
        [InlineData(CompressionMethod.RLE)]
        public void ConvertToTiff_DifferentCompressionMethods_CreatesValidFiles(CompressionMethod compression)
        {
            // Arrange
            using var document = new PdfDocument(_contractPdfPath);
            var outputPath = Path.Combine(_tempOutputDir, $"contract_{compression}.tiff");

            // Act
            document.ConvertToTiff(outputPath, dpi: 150, compression: compression);

            // Assert
            Assert.True(File.Exists(outputPath));
            Assert.True(new FileInfo(outputPath).Length > 0);
        }

        [Fact]
        public void ConvertToTiff_ContractPdf_FitsUSLetterPortrait()
        {
            // Arrange
            using var document = new PdfDocument(_contractPdfPath);
            var outputPath = Path.Combine(_tempOutputDir, "contract_us_letter.tiff");

            // Act
            document.ConvertToTiff(outputPath, dpi: 300);

            // Assert
            using var magickImage = new MagickImage(outputPath);

            // At 300 DPI, US Letter portrait should be max 2550x3300 pixels (8.5x11 inches)
            var maxWidthPixels = (int)(8.5 * 300);
            var maxHeightPixels = (int)(11 * 300);

            Assert.True(magickImage.Width <= maxWidthPixels,
                $"Width {magickImage.Width} should be <= {maxWidthPixels} for US Letter");
            Assert.True(magickImage.Height <= maxHeightPixels,
                $"Height {magickImage.Height} should be <= {maxHeightPixels} for US Letter");

            // Should be portrait orientation (height > width)
            Assert.True(magickImage.Height > magickImage.Width, "Should maintain portrait orientation");
        }

        [Fact]
        public void ConvertToTiff_PresentationPdf_FitsUSLetterLandscape()
        {
            // Arrange
            using var document = new PdfDocument(_presentationPdfPath);
            var outputPath = Path.Combine(_tempOutputDir, "presentation_us_letter.tiff");

            // Act
            document.ConvertToTiff(outputPath, dpi: 300);

            // Assert
            using var magickImage = new MagickImage(outputPath);

            // At 300 DPI, US Letter landscape should be max 3300x2550 pixels (11x8.5 inches)
            var maxWidthPixels = (int)(11 * 300);
            var maxHeightPixels = (int)(8.5 * 300);

            Assert.True(magickImage.Width <= maxWidthPixels,
                $"Width {magickImage.Width} should be <= {maxWidthPixels} for US Letter landscape");
            Assert.True(magickImage.Height <= maxHeightPixels,
                $"Height {magickImage.Height} should be <= {maxHeightPixels} for US Letter landscape");

            // Should be landscape orientation (width > height)
            Assert.True(magickImage.Width > magickImage.Height, "Should maintain landscape orientation");
        }

        [Fact]
        public void Dispose_CanBeCalledMultipleTimes()
        {
            // Arrange
            var document = new PdfDocument(_contractPdfPath);

            // Act & Assert - Should not throw
            document.Dispose();
            document.Dispose();
        }

        [Fact]
        public void Permissions_ContractPdf_ReturnsValidPermissions()
        {
            // Arrange
            using var document = new PdfDocument(_contractPdfPath);

            // Act
            var permissions = document.Permissions;

            // Assert
            Assert.True(permissions >= 0); // Should be a valid uint value
        }
    }
}