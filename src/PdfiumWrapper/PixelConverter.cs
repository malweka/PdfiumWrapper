namespace PdfiumWrapper;

/// <summary>
/// High-performance pixel format conversion for PDFium's native 8-bit gray output (TIFF pages).
/// All methods read directly from native buffer pointers — no intermediate managed copy.
/// </summary>
internal static class PixelConverter
{
    /// <summary>
    /// Converts 8-bit gray to 1-bit packed bilevel using a threshold.
    /// Output is MSB-first packed bytes (PHOTOMETRIC_MINISWHITE: 0=white, 1=black).
    /// </summary>
    /// <param name="gray">Pointer to the source gray buffer (1 byte per pixel).</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="stride">Source stride in bytes (may include padding).</param>
    /// <param name="threshold">Threshold 0–255. Pixels below = black, at or above = white.</param>
    /// <returns>Packed 1-bit data, one row = ceil(width/8) bytes.</returns>
    public static byte[] GrayToPackedBilevel(IntPtr gray, int width, int height,
        int stride, byte threshold = 128)
    {
        int packedStride = (width + 7) / 8;
        byte[] output = new byte[packedStride * height];

        unsafe
        {
            byte* src = (byte*)gray;

            for (int y = 0; y < height; y++)
            {
                byte* row = src + (long)y * stride;
                int outOffset = y * packedStride;

                for (int x = 0; x < width; x++)
                {
                    // MINISWHITE: 0=white, 1=black
                    if (row[x] < threshold)
                    {
                        output[outOffset + (x >> 3)] |= (byte)(1 << (7 - (x & 7)));
                    }
                }
            }
        }

        return output;
    }

    /// <summary>
    /// Copies 8-bit gray rows into a tightly packed array (1 byte per pixel, row-major),
    /// dropping any stride padding.
    /// </summary>
    public static byte[] GrayToGrayscale(IntPtr gray, int width, int height, int stride)
    {
        byte[] output = new byte[width * height];

        unsafe
        {
            byte* src = (byte*)gray;
            fixed (byte* dst = output)
            {
                for (int y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(src + (long)y * stride, dst + (long)y * width, width, width);
                }
            }
        }

        return output;
    }
}