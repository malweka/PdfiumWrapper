namespace PdfiumWrapper;

/// <summary>
/// Raw pixel data from a rendered PDF page.
/// Pixels are in BGRA format (PDFium's native layout), 8 bits per channel.
/// </summary>
/// <param name="Pixels">Raw pixel bytes in BGRA order.</param>
/// <param name="Width">Image width in pixels.</param>
/// <param name="Height">Image height in pixels.</param>
/// <param name="Stride">Bytes per row (may include padding beyond Width * 4).</param>
public record RawBitmap(byte[] Pixels, int Width, int Height, int Stride);
