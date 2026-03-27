namespace PdfiumWrapper;

/// <summary>
/// Color mode for TIFF output.
/// </summary>
public enum TiffColorMode
{
    /// <summary>
    /// 1-bit bilevel (black and white) with CCITT Group 4 compression.
    /// Best compression ratio. Ideal for text documents and scanned pages.
    /// Uses a luminance threshold to convert pixels to black or white.
    /// </summary>
    Bilevel,

    /// <summary>
    /// 8-bit grayscale with LZW compression.
    /// Preserves gray tones. Suitable for documents with images or gradients.
    /// </summary>
    Grayscale
}
