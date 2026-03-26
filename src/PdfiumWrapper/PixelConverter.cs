namespace PdfiumWrapper;

/// <summary>
/// High-performance pixel format conversion for PDFium's native BGRA output.
/// All methods read directly from native buffer pointers — no intermediate managed copy.
/// </summary>
internal static class PixelConverter
{
    /// <summary>
    /// Converts BGRA to 1-bit packed bilevel using luminance threshold.
    /// Output is MSB-first packed bytes (PHOTOMETRIC_MINISWHITE: 0=white, 1=black).
    /// </summary>
    /// <param name="bgra">Pointer to the source BGRA pixel buffer (4 bytes per pixel).</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="stride">Source stride in bytes (may include padding).</param>
    /// <param name="threshold">Luminance threshold 0–255. Pixels below = black, above = white.</param>
    /// <returns>Packed 1-bit data, one row = ceil(width/8) bytes.</returns>
    public static byte[] BgraToPackedBilevel(IntPtr bgra, int width, int height,
        int stride, byte threshold = 128)
    {
        int packedStride = (width + 7) / 8;
        byte[] output = new byte[packedStride * height];

        // Pre-scale threshold to avoid a division per pixel.
        // Max luminance (unscaled) = 255*299 + 255*587 + 255*114 = 255_000, fits in int.
        int scaledThreshold = threshold * 1000;

        unsafe
        {
            byte* src = (byte*)bgra;

            for (int y = 0; y < height; y++)
            {
                byte* row = src + y * stride;
                int outOffset = y * packedStride;

                for (int x = 0; x < width; x++)
                {
                    int px = x * 4;
                    // ITU-R BT.601 luminance (BGRA layout: B=0, G=1, R=2, A=3)
                    int luminance = row[px + 2] * 299 + row[px + 1] * 587 + row[px] * 114;

                    // MINISWHITE: 0=white, 1=black
                    if (luminance < scaledThreshold)
                    {
                        output[outOffset + (x >> 3)] |= (byte)(1 << (7 - (x & 7)));
                    }
                }
            }
        }

        return output;
    }

    /// <summary>
    /// Converts BGRA to 8-bit grayscale (1 byte per pixel, row-major).
    /// </summary>
    public static byte[] BgraToGrayscale(IntPtr bgra, int width, int height, int stride)
    {
        byte[] output = new byte[width * height];

        unsafe
        {
            byte* src = (byte*)bgra;

            for (int y = 0; y < height; y++)
            {
                byte* row = src + y * stride;
                int outOffset = y * width;

                for (int x = 0; x < width; x++)
                {
                    int px = x * 4;
                    // ITU-R BT.601 luminance
                    output[outOffset + x] = (byte)((row[px + 2] * 299 + row[px + 1] * 587 + row[px] * 114) / 1000);
                }
            }
        }

        return output;
    }
}
