# Malweka.PdfiumSdk — libpng P/Invoke Integration Plan

## Objective

Add PNG encoding/decoding to Malweka.PdfiumSdk by P/Invoking **libpng**, eliminating the need for SkiaSharp or any managed imaging library. The wrapper takes raw pixel buffers (BGRA from PDFium) and encodes them to PNG, or decodes PNG files to raw pixel buffers.

---

## Library Overview

- **Library**: libpng (http://www.libpng.org/pub/png/libpng.html)
- **License**: libpng/zlib license (permissive, commercial-friendly)
- **Version**: 1.6.x (latest stable)
- **Dependency**: zlib (already a dependency of libtiff, can share the binary)
- **Native binary names**:
  - Windows: `libpng16.dll` (also requires `zlib1.dll` or statically linked)
  - Linux: `libpng16.so`
  - macOS: `libpng16.dylib`
- **API style**: Callback/struct-based C API. More complex than TurboJPEG. Uses setjmp/longjmp for error handling which requires careful P/Invoke handling.

---

## Building Native Binaries

### All Platforms — Source

Download release from: https://github.com/pnggroup/libpng/releases or http://www.libpng.org/pub/png/libpng.html

**zlib dependency**: libpng requires zlib. Either:
- Install zlib system-wide (Linux: `apt install zlib1g-dev`, macOS: already present)
- Build zlib from source alongside libpng
- Use `-DPNG_BUILD_ZLIB=ON` and point to zlib source with `-DZLIB_ROOT=<path>`

### macOS (arm64 + x64)

```bash
# arm64 (native on Apple Silicon)
cmake -B build-arm64 -DBUILD_SHARED_LIBS=ON -DCMAKE_OSX_ARCHITECTURES=arm64 \
    -DPNG_TESTS=OFF -DPNG_TOOLS=OFF
cmake --build build-arm64 --config Release
# output: build-arm64/libpng16.dylib

# x64 (cross-compile)
cmake -B build-x64 -DBUILD_SHARED_LIBS=ON -DCMAKE_OSX_ARCHITECTURES=x86_64 \
    -DPNG_TESTS=OFF -DPNG_TOOLS=OFF
cmake --build build-x64 --config Release
# output: build-x64/libpng16.dylib
```

### Windows x64

Requires: Visual Studio Build Tools + CMake. Must have zlib available.

```cmd
:: Option A: Build zlib first, then libpng pointing to it
:: Build zlib
cd zlib-<version>
cmake -B build -A x64 -DBUILD_SHARED_LIBS=ON
cmake --build build --config Release

:: Build libpng
cd libpng-<version>
cmake -B build -A x64 -DBUILD_SHARED_LIBS=ON -DPNG_TESTS=OFF -DPNG_TOOLS=OFF ^
    -DZLIB_LIBRARY=<path-to-zlib>/build/Release/zlib.lib ^
    -DZLIB_INCLUDE_DIR=<path-to-zlib>
cmake --build build --config Release
:: output: build\Release\libpng16.dll
```

### Linux x64 (Docker)

```dockerfile
FROM ubuntu:24.04
RUN apt-get update && apt-get install -y cmake gcc g++ zlib1g-dev
COPY libpng-<version>/ /src/
WORKDIR /src
RUN cmake -B build -DBUILD_SHARED_LIBS=ON -DPNG_TESTS=OFF -DPNG_TOOLS=OFF \
    && cmake --build build --config Release
# output: build/libpng16.so
```

### NuGet Package Layout

```
runtimes/
  win-x64/native/libpng16.dll
  win-x64/native/zlib1.dll        (if not statically linked)
  linux-x64/native/libpng16.so
  osx-arm64/native/libpng16.dylib
  osx-x64/native/libpng16.dylib
```

**Consideration**: Since libtiff already depends on zlib, share the zlib binary. Alternatively, build libpng with zlib statically linked (`-DPNG_STATIC=ON` and link zlib statically) to avoid shipping a separate zlib DLL.

---

## P/Invoke Declarations

### DLL Import Constant

```csharp
private const string LibPng = "libpng16";
```

### Important: Error Handling Strategy

libpng uses `setjmp`/`longjmp` for error handling, which is **incompatible with P/Invoke** (longjmp will corrupt the managed stack). There are two approaches:

**Approach A (Recommended): Write a thin C shim library** that wraps libpng calls, handles setjmp internally, and exposes a simple error-code-based API. This shim can be compiled alongside libpng and bundled as a single DLL.

**Approach B: Use `png_set_error_fn`** to install a custom error handler that does NOT call longjmp. Instead, set a flag and return. This is fragile and not officially supported by libpng — after an error, the png_struct is in an undefined state.

**This plan uses Approach A — a C shim library.**

---

## C Shim Library: `pdfium_png.c`

Create a small C file that wraps libpng and exposes a simplified API.

### Shim Header: `pdfium_png.h`

```c
#ifndef PDFIUM_PNG_H
#define PDFIUM_PNG_H

#include <stdint.h>
#include <stddef.h>

#ifdef _WIN32
  #define PDFIUM_PNG_API __declspec(dllexport)
#else
  #define PDFIUM_PNG_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

// Pixel format enum
typedef enum {
    PNG_FMT_RGB    = 0,  // 3 bytes per pixel
    PNG_FMT_RGBA   = 1,  // 4 bytes per pixel
    PNG_FMT_BGRA   = 2,  // 4 bytes per pixel (PDFium native) — converted internally
    PNG_FMT_GRAY   = 3,  // 1 byte per pixel
    PNG_FMT_GRAY_ALPHA = 4, // 2 bytes per pixel
} PdfiumPngFormat;

// Compression level: 0 = none, 1 = fast, 6 = default, 9 = max
typedef struct {
    int width;
    int height;
    int bit_depth;       // typically 8
    int color_type;      // PNG color type (2=RGB, 6=RGBA, 0=Gray, 4=GrayAlpha)
    int channels;
} PdfiumPngInfo;

// --- Encoding ---

// Encode a raw pixel buffer to a PNG file.
// Returns 0 on success, non-zero on error.
PDFIUM_PNG_API int pdfium_png_encode_to_file(
    const char* output_path,
    const uint8_t* pixel_data,
    int width,
    int height,
    int stride,            // bytes per row (0 = tightly packed)
    PdfiumPngFormat format,
    int compression_level  // 0-9, use 6 for default
);

// Encode a raw pixel buffer to a memory buffer.
// On success, *out_data is allocated (caller must free with pdfium_png_free)
// and *out_size is set. Returns 0 on success.
PDFIUM_PNG_API int pdfium_png_encode_to_memory(
    const uint8_t* pixel_data,
    int width,
    int height,
    int stride,
    PdfiumPngFormat format,
    int compression_level,
    uint8_t** out_data,
    size_t* out_size
);

// --- Decoding ---

// Read PNG header from file. Returns 0 on success.
PDFIUM_PNG_API int pdfium_png_read_header(
    const char* input_path,
    PdfiumPngInfo* info
);

// Read PNG header from memory buffer. Returns 0 on success.
PDFIUM_PNG_API int pdfium_png_read_header_from_memory(
    const uint8_t* png_data,
    size_t png_size,
    PdfiumPngInfo* info
);

// Decode PNG file to raw pixel buffer.
// output_format: desired output format (BGRA recommended for PDFium interop).
// *out_data is allocated (caller must free with pdfium_png_free).
// Returns 0 on success.
PDFIUM_PNG_API int pdfium_png_decode_from_file(
    const char* input_path,
    PdfiumPngFormat output_format,
    uint8_t** out_data,
    int* out_width,
    int* out_height,
    int* out_stride
);

// Decode PNG from memory buffer.
// *out_data is allocated (caller must free with pdfium_png_free).
// Returns 0 on success.
PDFIUM_PNG_API int pdfium_png_decode_from_memory(
    const uint8_t* png_data,
    size_t png_size,
    PdfiumPngFormat output_format,
    uint8_t** out_data,
    int* out_width,
    int* out_height,
    int* out_stride
);

// --- Utility ---

// Free a buffer allocated by encoding/decoding functions.
PDFIUM_PNG_API void pdfium_png_free(uint8_t* data);

// Get the last error message (thread-local).
PDFIUM_PNG_API const char* pdfium_png_get_error(void);

#ifdef __cplusplus
}
#endif

#endif // PDFIUM_PNG_H
```

### Shim Implementation Notes

The C shim (`pdfium_png.c`) should:

1. **Handle setjmp/longjmp internally** — Each function sets up `setjmp` and returns an error code if `longjmp` fires.
2. **Convert BGRA → RGBA row-by-row** — libpng doesn't understand BGRA. Swap R and B channels per row before writing. Do this in a temporary row buffer to avoid modifying the source.
3. **Use `png_set_write_fn` for memory encoding** — Instead of file I/O, write to a dynamically growing buffer.
4. **Use `png_set_read_fn` for memory decoding** — Read from a memory buffer instead of file.
5. **Store error messages in thread-local storage** — Use `_Thread_local` (C11) or `__thread` (GCC/Clang) for thread safety.

### Building the Shim

The shim should be compiled as a shared library that statically links libpng and zlib:

```bash
# macOS example
gcc -shared -o libpdfium_png.dylib pdfium_png.c \
    -I/path/to/libpng -I/path/to/zlib \
    -L/path/to/libpng/build -L/path/to/zlib/build \
    -lpng16 -lz -O2 -fPIC

# Or better: use CMake to build the shim alongside libpng/zlib
```

Alternatively, add the shim as a source file in a CMake project that builds libpng + zlib + shim as a single shared library.

---

## P/Invoke Declarations (for the Shim)

```csharp
internal static class PdfiumPngNative
{
    private const string Lib = "pdfium_png"; // or whatever you name the shim

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int pdfium_png_encode_to_file(
        string outputPath,
        IntPtr pixelData,
        int width,
        int height,
        int stride,
        PngPixelFormat format,
        int compressionLevel);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int pdfium_png_encode_to_memory(
        IntPtr pixelData,
        int width,
        int height,
        int stride,
        PngPixelFormat format,
        int compressionLevel,
        out IntPtr outData,
        out nuint outSize);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int pdfium_png_read_header(
        string inputPath,
        out PngInfo info);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int pdfium_png_read_header_from_memory(
        IntPtr pngData,
        nuint pngSize,
        out PngInfo info);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int pdfium_png_decode_from_file(
        string inputPath,
        PngPixelFormat outputFormat,
        out IntPtr outData,
        out int outWidth,
        out int outHeight,
        out int outStride);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int pdfium_png_decode_from_memory(
        IntPtr pngData,
        nuint pngSize,
        PngPixelFormat outputFormat,
        out IntPtr outData,
        out int outWidth,
        out int outHeight,
        out int outStride);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void pdfium_png_free(IntPtr data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pdfium_png_get_error();
}

public enum PngPixelFormat : int
{
    RGB = 0,
    RGBA = 1,
    BGRA = 2,       // PDFium's native format — shim converts to RGBA internally
    Gray = 3,
    GrayAlpha = 4,
}

[StructLayout(LayoutKind.Sequential)]
public struct PngInfo
{
    public int Width;
    public int Height;
    public int BitDepth;
    public int ColorType;
    public int Channels;
}
```

---

## Managed Wrapper Design

### Class: `PngEncoder`

```
Namespace: Malweka.PdfiumSdk.Imaging

- No constructor state needed (the shim functions are stateless)
- Thread safety: Thread-safe (each call is independent, error messages are thread-local)

Static Methods:
  - static void EncodeToFile(IntPtr pixelBuffer, int width, int height, int stride,
                             string outputPath, PngPixelFormat format = BGRA,
                             int compressionLevel = 6)
    → Calls pdfium_png_encode_to_file
    → Throws PngException on non-zero return

  - static byte[] Encode(IntPtr pixelBuffer, int width, int height, int stride,
                         PngPixelFormat format = BGRA, int compressionLevel = 6)
    → Calls pdfium_png_encode_to_memory
    → Copies to managed byte[], calls pdfium_png_free, returns

  - static void EncodeToStream(IntPtr pixelBuffer, int width, int height, int stride,
                               Stream output, PngPixelFormat format = BGRA,
                               int compressionLevel = 6)
    → Calls Encode(), writes to stream
```

### Class: `PngDecoder`

```
Namespace: Malweka.PdfiumSdk.Imaging

Static Methods:
  - static PngInfo ReadHeader(string filePath)
    → Calls pdfium_png_read_header

  - static PngInfo ReadHeader(ReadOnlySpan<byte> pngData)
    → Pins data, calls pdfium_png_read_header_from_memory

  - static PngImage Decode(string filePath, PngPixelFormat outputFormat = BGRA)
    → Calls pdfium_png_decode_from_file
    → Wraps result in PngImage (which handles freeing via pdfium_png_free)

  - static PngImage Decode(ReadOnlySpan<byte> pngData, PngPixelFormat outputFormat = BGRA)
    → Pins data, calls pdfium_png_decode_from_memory
    → Wraps result in PngImage
```

### Class: `PngImage`

```
Namespace: Malweka.PdfiumSdk.Imaging

- Implements IDisposable — calls pdfium_png_free on the native buffer
- Properties:
    IntPtr Buffer       — pointer to raw pixel data
    int Width
    int Height
    int Stride
    PngPixelFormat Format

- Methods:
    Span<byte> AsSpan()      — creates a span over the native buffer
    byte[] ToArray()         — copies to managed byte[]
    void CopyTo(IntPtr dest) — copies to a caller-provided buffer
```

### Class: `PngException`

```
Namespace: Malweka.PdfiumSdk.Imaging

- Inherits: Exception
- Constructor takes error code + message from pdfium_png_get_error()
```

---

## PDFium Integration Example

```csharp
// Render a PDF page to PNG — the core use case
IntPtr bitmap = FPDFBitmap_Create(widthPx, heightPx, 1); // 1 = has alpha
FPDFBitmap_FillRect(bitmap, 0, 0, widthPx, heightPx, 0xFFFFFFFF);
FPDF_RenderPageBitmap(bitmap, page, 0, 0, widthPx, heightPx, 0, 0x10);

IntPtr buffer = FPDFBitmap_GetBuffer(bitmap);
int stride = FPDFBitmap_GetStride(bitmap);

// Direct encode — the shim handles BGRA→RGBA conversion internally.
PngEncoder.EncodeToFile(buffer, widthPx, heightPx, stride,
    "page.png", PngPixelFormat.BGRA, compressionLevel: 6);

FPDFBitmap_Destroy(bitmap);
```

---

## Key Implementation Notes

1. **Why a C shim instead of direct P/Invoke**: libpng's `setjmp`/`longjmp` error handling is fundamentally incompatible with .NET's managed stack. A thin C wrapper that contains the setjmp scope and returns error codes is the only safe approach. This is a well-known issue — even Mono's own libpng usage goes through a shim.

2. **BGRA → RGBA conversion**: libpng only understands RGB/RGBA. The shim must swap B and R channels. Do this row-by-row in a temporary buffer during encoding to avoid modifying the source PDFium buffer. This is a simple byte swap loop and is fast.

3. **zlib sharing**: libtiff already depends on zlib. Options:
   - Ship one `zlib1.dll` shared by both libtiff and the PNG shim
   - Statically link zlib into both libtiff and the PNG shim (results in slightly larger binaries but zero dependency issues — **recommended**)

4. **Compression levels**: PNG compression is lossless. Level 6 (default) is a good balance. Level 1 is fast with larger files. Level 9 is slow with minimal additional size reduction. For document rendering output, level 6 is fine.

5. **Alpha channel**: PDFium renders with `FPDFBitmap_CreateEx` format `FPDFBitmap_BGRA` which includes alpha. If alpha is not needed (opaque document pages), the shim should support stripping alpha (BGRA → RGB) to reduce file size by ~25%.

6. **Memory management**: The shim allocates output buffers with `malloc`. The managed wrapper must ensure `pdfium_png_free` is called for every allocation. The `PngImage` class handles this via `IDisposable`. For encoding, copy to `byte[]` and free immediately.

7. **Thread safety**: libpng itself is thread-safe as long as each `png_struct` is used by only one thread. Since the shim creates and destroys `png_struct` within each function call, all shim functions are thread-safe. Error messages use thread-local storage.

8. **Interlaced PNG**: The shim should NOT support interlaced (Adam7) output by default. Interlaced PNGs require multiple passes and significantly complicate the encoding path. For document rendering output, non-interlaced is standard.

9. **16-bit depth**: The shim should assume 8-bit depth for encoding (matching PDFium's output). For decoding, the shim should handle 16-bit PNGs by stripping to 8-bit via `png_set_strip_16`.

---

## Alternative Approach: Skip libpng, Use stb_image_write

If the complexity of the C shim feels like overkill, consider **stb_image_write** (https://github.com/nothings/stb):

- Single-header C library (just `stb_image_write.h`)
- Public domain
- `stbi_write_png()` — one function call, takes pixel buffer and writes PNG
- `stbi_write_png_to_mem()` — encodes to memory buffer
- No setjmp/longjmp issues
- No zlib dependency (uses its own minimal deflate implementation)
- Downside: Not as optimized as libpng for compression, larger output files
- Downside: No decode support (would need `stb_image.h` for that — also single-header)

If PNG output quality/size is not critical and you want the simplest possible integration, stb is worth considering. You could P/Invoke it directly with 2-3 functions. The shim would be trivial — just compile `stb_image_write.h` as a shared library.

---

## Build Matrix Summary

| Target         | libpng16 | zlib  | pdfium_png shim | Notes                         |
|----------------|----------|-------|-----------------|-------------------------------|
| Windows x64    | .dll     | .dll  | .dll            | VS Build Tools + CMake        |
| Linux x64      | .so      | .so   | .so             | Docker (ubuntu:24.04)         |
| macOS arm64    | .dylib   | system| .dylib          | Xcode CLT + CMake             |
| macOS x64      | .dylib   | system| .dylib          | Cross-compile on Apple Silicon|

---

## Testing Checklist

- [ ] Encode BGRA buffer to PNG at various compression levels (0, 1, 6, 9)
- [ ] Encode RGBA buffer to PNG
- [ ] Encode grayscale buffer to PNG
- [ ] Encode RGB buffer (no alpha) to PNG — verify smaller file size
- [ ] Decode PNG to BGRA buffer
- [ ] Decode PNG to RGBA buffer
- [ ] Decode PNG to grayscale buffer
- [ ] Decode 16-bit PNG (should strip to 8-bit)
- [ ] Decode paletted PNG (should expand to RGBA)
- [ ] Read header only (dimensions, color type, bit depth)
- [ ] Round-trip: encode → decode → compare pixel data (must be bit-exact, PNG is lossless)
- [ ] Error handling: corrupt PNG, truncated file, invalid dimensions
- [ ] Memory: no leaks after encoding/decoding 10,000 images
- [ ] Thread safety: concurrent encoding from separate threads
- [ ] Cross-platform: verify on Windows x64, Linux x64, macOS arm64
- [ ] PDFium integration: render PDF page → encode to PNG end-to-end
- [ ] BGRA→RGBA conversion: verify colors are correct (no red/blue swap in output)
- [ ] Large image: encode/decode a 10000x10000 pixel image
- [ ] Alpha preservation: verify transparent regions survive round-trip
