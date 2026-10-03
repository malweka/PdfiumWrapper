namespace PdfiumWrapper;

/// <summary>
/// Upper bound on the bitmaps the wrapper asks PDFium to allocate. A page's MediaBox and the
/// caller's DPI decide the render size, so without a bound one crafted PDF can demand a
/// multi-GiB bitmap per render, and every concurrent caller holds one while it encodes.
/// </summary>
internal static class RenderLimits
{
    /// <summary><see cref="AppContext"/> data key overriding <see cref="DefaultMaxPixels"/> (a number of pixels).</summary>
    public const string MaxPixelsKey = "PdfiumWrapper.MaxRenderPixels";

    /// <summary>2^28 pixels: 1 GiB as BGRA, for example 16,384 x 16,384, or a 54 x 54 inch page at 300 DPI.</summary>
    public const long DefaultMaxPixels = 1L << 28;

    public static long MaxPixels => AppContext.GetData(MaxPixelsKey) switch
    {
        long value when value > 0 => value,
        int value when value > 0 => value,
        string text when long.TryParse(text, out long value) && value > 0 => value,
        _ => DefaultMaxPixels,
    };

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when a <paramref name="width"/> x
    /// <paramref name="height"/> bitmap exceeds the limit or an <see cref="int"/> side.
    /// Takes doubles so a size computed from a hostile page box is checked before any cast.
    /// </summary>
    /// <param name="pageIndex">The page being rendered, or -1 for an image that is not a page.</param>
    public static void Check(double width, double height, int pageIndex)
    {
        long limit = MaxPixels;

        // Written so that NaN fails too.
        if (width <= int.MaxValue && height <= int.MaxValue && width * height <= limit)
            return;

        string subject = pageIndex >= 0 ? $"Rendering page index {pageIndex}" : "Creating an image bitmap";
        throw new InvalidOperationException(
            $"{subject} at {width:0} x {height:0} pixels ({width * height:N0} pixels) exceeds the render limit of " +
            $"{limit:N0} pixels. Lower the DPI or the requested size, or raise the limit with the AppContext data " +
            $"key \"{MaxPixelsKey}\".");
    }
}
