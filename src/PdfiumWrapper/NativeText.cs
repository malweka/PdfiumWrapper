using System.Buffers;
using System.Runtime.InteropServices;

namespace PdfiumWrapper;

/// <summary>
/// Reads the UTF-16LE strings PDFium returns through the "call once for the length, again for
/// the text" pattern. The length is in bytes and counts the two-byte terminator. Call inside the
/// native gate.
/// </summary>
internal static class NativeText
{
    /// <summary>
    /// Calls <paramref name="get"/> with a null buffer for the byte length, then with a pooled
    /// buffer of that length. Returns null when PDFium reports no value (a length of 0) and
    /// <see cref="string.Empty"/> for a value that is present but empty.
    /// </summary>
    /// <param name="state">Passed to <paramref name="get"/>, so the callback can be a static lambda.</param>
    /// <param name="get">The PDFium call: (state, buffer, buffer length in bytes) → required length in bytes.</param>
    /// <exception cref="InvalidDataException">The document reports a length a string cannot have.</exception>
    public static unsafe string? ReadUtf16<TState>(TState state, Func<TState, IntPtr, CULong, CULong> get)
    {
        long byteLength = CheckedLength(get(state, IntPtr.Zero, default));
        if (byteLength == 0)
            return null;

        var buffer = ArrayPool<char>.Shared.Rent((int)(byteLength / 2));
        try
        {
            long written;
            fixed (char* bufferPtr = buffer)
                written = CheckedLength(get(state, (IntPtr)bufferPtr, new CULong((nuint)byteLength)));

            // A second call that reports a different length wrote at most what the buffer holds.
            var text = buffer.AsSpan(0, (int)(Math.Min(written, byteLength) / 2));
            int terminator = text.IndexOf('\0');
            return new string(terminator >= 0 ? text[..terminator] : text);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static long CheckedLength(CULong length)
    {
        ulong value = length.Value;

        // A UTF-16 string is a whole number of code units; the bound keeps the char count an int.
        if (value % 2 != 0 || value > (ulong)Array.MaxLength * 2)
            throw new InvalidDataException($"PDFium reported a string length of {value} bytes, which is not a valid UTF-16 length.");

        return (long)value;
    }
}
