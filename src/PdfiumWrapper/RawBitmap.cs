namespace PdfiumWrapper;

/// <summary>
/// Raw pixel data: 4 bytes per pixel in blue, green, red, fourth-byte order, 8 bits per channel.
/// </summary>
/// <remarks>
/// <para>
/// Page renders (<see cref="PdfDocument.RenderPages(int)"/> and <see cref="PdfDocument.RenderPagesAsync(int, System.Threading.CancellationToken)"/>)
/// are PDFium's BGRx layout: the fourth byte of each pixel is padding, not alpha, and its value
/// must not be relied on. Rows are <see cref="Stride"/> bytes apart, as PDFium allocated them.
/// </para>
/// <para>
/// Bitmaps the wrapper converts (<see cref="PdfPage.GetEmbeddedThumbnail"/>,
/// <see cref="PdfImageObject.GetBitmap"/> and <see cref="PdfImageObject.GetRenderedBitmap"/>) are
/// real BGRA, tightly packed (<see cref="Stride"/> is <c>Width * 4</c>). The fourth byte is the
/// source's alpha when it has one; gray, BGR and BGRx sources get full opacity (255).
/// </para>
/// </remarks>
/// <param name="Pixels">Pixel bytes, <see cref="Stride"/> bytes per row.</param>
/// <param name="Width">Image width in pixels.</param>
/// <param name="Height">Image height in pixels.</param>
/// <param name="Stride">Bytes per row (may include padding beyond Width * 4 for page renders).</param>
public record RawBitmap(byte[] Pixels, int Width, int Height, int Stride);
