# Malweka.PdfiumSdk — libjpeg-turbo P/Invoke Integration Plan

## Objective

Add JPEG encoding/decoding to Malweka.PdfiumSdk by P/Invoking **libjpeg-turbo**, eliminating the need for SkiaSharp or any managed imaging library. The wrapper takes raw pixel buffers (BGRA from PDFium) and encodes them to JPEG, or decodes JPEG files to raw pixel buffers.

---

## Library Overview

- **Library**: libjpeg-turbo (https://github.com/libjpeg-turbo/libjpeg-turbo)
- **License**: BSD-3-Clause / IJG (permissive, commercial-friendly)
- **Why libjpeg-turbo**: SIMD-accelerated (SSE2, AVX2, NEON), 2-6x faster than standard libjpeg. Used by Chrome, Firefox, Android, and most Linux distros.
- **Native binary names**:
  - Windows: `turbojpeg.dll`
  - Linux: `libturbojpeg.so`
  - macOS: `libturbojpeg.dylib`
- **Important**: Use the **TurboJPEG API** (not the lower-level libjpeg API). TurboJPEG is a simplified, higher-level API designed for exactly this use case — compress/decompress pixel buffers. Far fewer function calls, easier to P/Invoke.

---

## Building Native Binaries

### All Platforms — Source

Download release from: https://github.com/libjpeg-turbo/libjpeg-turbo/releases

### macOS (arm64 + x64)

```bash
# arm64 (native on Apple Silicon)
cmake -B build-arm64 -DBUILD_SHARED_LIBS=ON -DCMAKE_OSX_ARCHITECTURES=arm64 \
    -DENABLE_STATIC=OFF -DWITH_TURBOJPEG=ON
cmake --build build-arm64 --config Release
# output: build-arm64/libturbojpeg.dylib

# x64 (cross-compile)
cmake -B build-x64 -DBUILD_SHARED_LIBS=ON -DCMAKE_OSX_ARCHITECTURES=x86_64 \
    -DENABLE_STATIC=OFF -DWITH_TURBOJPEG=ON
cmake --build build-x64 --config Release
# output: build-x64/libturbojpeg.dylib
```

### Windows x64

Requires: Visual Studio Build Tools (or full VS) + CMake.

```cmd
cmake -B build -A x64 -DENABLE_STATIC=OFF -DWITH_TURBOJPEG=ON
cmake --build build --config Release
:: output: build\Release\turbojpeg.dll
```

### Linux x64 (Docker)

```dockerfile
FROM ubuntu:24.04
RUN apt-get update && apt-get install -y cmake gcc g++ nasm
COPY libjpeg-turbo-<version>/ /src/
WORKDIR /src
RUN cmake -B build -DBUILD_SHARED_LIBS=ON -DENABLE_STATIC=OFF -DWITH_TURBOJPEG=ON \
    && cmake --build build --config Release
# output: build/libturbojpeg.so
```

**Note**: Install `nasm` or `yasm` for SIMD acceleration. Without it, the library still builds but without SIMD optimizations.

### NuGet Package Layout

```
runtimes/
  win-x64/native/turbojpeg.dll
  linux-x64/native/libturbojpeg.so
  osx-arm64/native/libturbojpeg.dylib
  osx-x64/native/libturbojpeg.dylib
```

---

## P/Invoke Declarations

### DLL Import Constant

```csharp
// Use a constant that maps to the correct native name per platform.
// .NET's DllImport resolves "turbojpeg" to:
//   Windows: turbojpeg.dll
//   Linux:   libturbojpeg.so
//   macOS:   libturbojpeg.dylib
private const string TurboJpeg = "turbojpeg";
```

### Core Functions to Wrap

All functions use `CallingConvention.Cdecl`.

#### Lifecycle

```csharp
// Create a compressor instance. Returns an opaque handle.
// Must be destroyed with tjDestroy when done.
[DllImport(TurboJpeg, CallingConvention = CallingConvention.Cdecl)]
public static extern IntPtr tjInitCompress();

// Create a decompressor instance. Returns an opaque handle.
[DllImport(TurboJpeg, CallingConvention = CallingConvention.Cdecl)]
public static extern IntPtr tjInitDecompress();

// Destroy a compressor or decompressor instance.
[DllImport(TurboJpeg, CallingConvention = CallingConvention.Cdecl)]
public static extern int tjDestroy(IntPtr handle);
```

#### Compression (Pixel Buffer → JPEG)

```csharp
// Compress a raw pixel buffer to JPEG.
//
// handle:      compressor handle from tjInitCompress()
// srcBuf:      source pixel buffer (IntPtr for PDFium bitmap buffers)
// width:       image width in pixels
// pitch:       bytes per row (stride). 0 = tightly packed (width * pixelSize)
// height:      image height in pixels
// pixelFormat: pixel format enum (see TJPixelFormat below)
// jpegBuf:     OUTPUT — pointer to pointer. TurboJPEG allocates the buffer.
//              Caller must free with tjFree().
// jpegSize:    OUTPUT — size of the compressed JPEG data
// jpegSubsamp: chroma subsampling (see TJSubsampling below)
// jpegQual:    JPEG quality 1-100
// flags:       encoding flags (see TJFlag below)
//
// Returns 0 on success, -1 on error.
[DllImport(TurboJpeg, CallingConvention = CallingConvention.Cdecl)]
public static extern int tjCompress2(
    IntPtr handle,
    IntPtr srcBuf,
    int width,
    int pitch,
    int height,
    int pixelFormat,
    ref IntPtr jpegBuf,
    ref nuint jpegSize,
    int jpegSubsamp,
    int jpegQual,
    int flags);

// Free a buffer allocated by TurboJPEG (used to free jpegBuf after tjCompress2).
[DllImport(TurboJpeg, CallingConvention = CallingConvention.Cdecl)]
public static extern void tjFree(IntPtr buffer);
```

#### Decompression (JPEG → Pixel Buffer)

```csharp
// Read JPEG header to get dimensions and subsampling without decompressing.
//
// jpegBuf:     pointer to JPEG data
// jpegSize:    size of JPEG data
// width:       OUTPUT — image width
// height:      OUTPUT — image height
// jpegSubsamp: OUTPUT — chroma subsampling used
// jpegColorspace: OUTPUT — colorspace
//
// Returns 0 on success, -1 on error.
[DllImport(TurboJpeg, CallingConvention = CallingConvention.Cdecl)]
public static extern int tjDecompressHeader3(
    IntPtr handle,
    IntPtr jpegBuf,
    nuint jpegSize,
    out int width,
    out int height,
    out int jpegSubsamp,
    out int jpegColorspace);

// Decompress a JPEG image to a raw pixel buffer.
//
// handle:      decompressor handle from tjInitDecompress()
// jpegBuf:     pointer to JPEG data
// jpegSize:    size of JPEG data
// dstBuf:      pre-allocated destination buffer
// width:       desired output width (0 = use JPEG width)
// pitch:       bytes per row in destination (0 = tightly packed)
// height:      desired output height (0 = use JPEG height)
// pixelFormat: desired output pixel format
// flags:       decoding flags
//
// Returns 0 on success, -1 on error.
[DllImport(TurboJpeg, CallingConvention = CallingConvention.Cdecl)]
public static extern int tjDecompress2(
    IntPtr handle,
    IntPtr jpegBuf,
    nuint jpegSize,
    IntPtr dstBuf,
    int width,
    int pitch,
    int height,
    int pixelFormat,
    int flags);
```

#### Error Handling

```csharp
// Get the last error message.
// Returns a pointer to a null-terminated string. Do NOT free.
[DllImport(TurboJpeg, CallingConvention = CallingConvention.Cdecl)]
public static extern IntPtr tjGetErrorStr2(IntPtr handle);

// Get the last error code.
[DllImport(TurboJpeg, CallingConvention = CallingConvention.Cdecl)]
public static extern int tjGetErrorCode(IntPtr handle);
```

#### Utility

```csharp
// Calculate the required buffer size for a given image.
// Returns the maximum buffer size needed, or -1 on error.
[DllImport(TurboJpeg, CallingConvention = CallingConvention.Cdecl)]
public static extern nuint tjBufSize(int width, int height, int jpegSubsamp);
```

### Enums / Constants

```csharp
/// <summary>
/// Pixel formats for TurboJPEG.
/// Must match the TJPF_* constants from turbojpeg.h.
/// </summary>
public enum TJPixelFormat : int
{
    RGB = 0,
    BGR = 1,
    RGBX = 2,   // 32-bit RGB with padding byte
    BGRX = 3,   // 32-bit BGR with padding byte
    XBGR = 4,
    XRGB = 5,
    Gray = 6,
    RGBA = 7,
    BGRA = 8,   // <-- PDFium's native format
    ABGR = 9,
    ARGB = 10,
    CMYK = 11,
}

/// <summary>
/// Chroma subsampling options.
/// </summary>
public enum TJSubsampling : int
{
    Samp444 = 0,   // 4:4:4 — no subsampling, best quality
    Samp422 = 1,   // 4:2:2
    Samp420 = 2,   // 4:2:0 — most common, good compression
    Gray = 3,      // Grayscale
    Samp440 = 4,   // 4:4:0
    Samp411 = 5,   // 4:1:1
}

/// <summary>
/// Encoding/decoding flags.
/// </summary>
[Flags]
public enum TJFlag : int
{
    None = 0,
    BottomUp = 2,        // Image is stored bottom-up (BMP style)
    FastUpsample = 256,  // Use fast but less accurate upsampling
    NoRealloc = 1024,    // Don't realloc the JPEG buffer (use pre-allocated)
    FastDCT = 2048,      // Use fast DCT (slightly less accurate)
    AccurateDCT = 4096,  // Use accurate DCT
    Progressive = 16384, // Generate progressive JPEG
}
```

---

## Managed Wrapper Design

### Class: `JpegEncoder`

```
Namespace: Malweka.PdfiumSdk.Imaging

- Constructor: JpegEncoder() — calls tjInitCompress(), stores handle
- Implements: IDisposable — calls tjDestroy() on dispose
- Thread safety: NOT thread-safe per instance. Create one per thread or use pooling.

Methods:
  - byte[] Encode(IntPtr pixelBuffer, int width, int height, int stride,
                  PixelFormat format = BGRA, int quality = 85,
                  Subsampling subsampling = Samp420)
    → Calls tjCompress2, copies result to managed byte[], calls tjFree

  - void EncodeToFile(IntPtr pixelBuffer, int width, int height, int stride,
                      string outputPath, PixelFormat format = BGRA,
                      int quality = 85, Subsampling subsampling = Samp420)
    → Calls Encode(), writes to file via File.WriteAllBytes

  - void EncodeToStream(IntPtr pixelBuffer, int width, int height, int stride,
                        Stream output, PixelFormat format = BGRA,
                        int quality = 85, Subsampling subsampling = Samp420)
    → Calls Encode(), writes to stream
```

### Class: `JpegDecoder`

```
Namespace: Malweka.PdfiumSdk.Imaging

- Constructor: JpegDecoder() — calls tjInitDecompress(), stores handle
- Implements: IDisposable — calls tjDestroy() on dispose
- Thread safety: NOT thread-safe per instance. Create one per thread or use pooling.

Methods:
  - JpegInfo ReadHeader(byte[] jpegData)
    → Calls tjDecompressHeader3, returns { Width, Height, Subsampling, Colorspace }

  - JpegInfo ReadHeader(Stream stream)
    → Reads stream to byte[], calls ReadHeader(byte[])

  - byte[] Decode(byte[] jpegData, PixelFormat outputFormat = BGRA)
    → Calls ReadHeader, allocates buffer, calls tjDecompress2, returns pixel data

  - void Decode(byte[] jpegData, IntPtr destBuffer, int destPitch,
                PixelFormat outputFormat = BGRA)
    → Calls tjDecompress2 directly into caller-provided buffer (zero-copy for PDFium interop)
```

### Record: `JpegInfo`

```
- int Width
- int Height
- TJSubsampling Subsampling
- int Colorspace
```

---

## PDFium Integration Example

```csharp
// Render a PDF page to JPEG — the core use case
using var encoder = new JpegEncoder();

IntPtr bitmap = FPDFBitmap_Create(widthPx, heightPx, 0);
FPDFBitmap_FillRect(bitmap, 0, 0, widthPx, heightPx, 0xFFFFFFFF);
FPDF_RenderPageBitmap(bitmap, page, 0, 0, widthPx, heightPx, 0, 0x10);

IntPtr buffer = FPDFBitmap_GetBuffer(bitmap);
int stride = FPDFBitmap_GetStride(bitmap);

// Direct encode — no intermediate image objects, no pixel conversion needed.
// TurboJPEG accepts BGRA natively (TJPixelFormat.BGRA = 8).
byte[] jpegData = encoder.Encode(buffer, widthPx, heightPx, stride,
    PixelFormat.BGRA, quality: 85);

File.WriteAllBytes("page.jpg", jpegData);
FPDFBitmap_Destroy(bitmap);
```

---

## Key Implementation Notes

1. **BGRA direct support**: TurboJPEG accepts BGRA (format 8) directly. No pixel conversion needed from PDFium's output. This is a major advantage over standard libjpeg which only accepts RGB.

2. **Buffer management**: `tjCompress2` allocates the output buffer internally. You must call `tjFree()` to release it. Copy to a managed `byte[]` before freeing, or use `Span<byte>` if targeting modern .NET.

3. **Thread safety**: Each `tjHandle` is independent. Create one encoder/decoder per thread, or pool them. Do not share a handle across threads without synchronization.

4. **Error handling**: Always check return values. On error (-1), call `tjGetErrorStr2(handle)` and marshal the string with `Marshal.PtrToStringAnsi()`.

5. **Performance tip**: For batch processing, reuse the encoder/decoder handle across multiple images. The init/destroy overhead is small but adds up over thousands of files.

6. **Grayscale optimization**: For grayscale output (e.g., document scanning workflows), use `TJSubsampling.Gray` — this skips chroma entirely and produces smaller files faster.

7. **No pixel conversion needed**: Unlike the libtiff integration where BGRA must be converted to bilevel/grayscale, TurboJPEG handles BGRA natively. The encode path is: PDFium buffer → tjCompress2 → JPEG bytes. Zero intermediate steps.

8. **Progressive JPEG**: Pass `TJFlag.Progressive` for web-optimized output. Slightly slower to encode but loads progressively in browsers.

---

## Testing Checklist

- [ ] Encode BGRA buffer to JPEG at various quality levels (10, 50, 85, 100)
- [ ] Encode grayscale buffer to JPEG
- [ ] Decode JPEG to BGRA buffer
- [ ] Decode JPEG header only (dimensions, subsampling)
- [ ] Round-trip: encode → decode → compare pixel data (within JPEG lossy tolerance)
- [ ] Error handling: invalid buffer, zero dimensions, null handle
- [ ] Memory: no leaks after encoding/decoding 10,000 images (tjFree called correctly)
- [ ] Thread safety: concurrent encoding with separate handles
- [ ] Cross-platform: verify on Windows x64, Linux x64, macOS arm64
- [ ] PDFium integration: render PDF page → encode to JPEG end-to-end
- [ ] File size sanity check: output JPEG sizes are reasonable for given quality
