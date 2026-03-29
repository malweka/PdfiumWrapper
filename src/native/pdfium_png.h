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

typedef enum {
    PNG_FMT_RGB        = 0,
    PNG_FMT_RGBA       = 1,
    PNG_FMT_BGRA       = 2,
    PNG_FMT_GRAY       = 3,
    PNG_FMT_GRAY_ALPHA = 4,
} PdfiumPngFormat;

typedef struct {
    int width;
    int height;
    int bit_depth;
    int color_type;
    int channels;
} PdfiumPngInfo;

/* PNG filter flags — matches libpng PNG_FILTER_* constants.
   Use PNG_FILTER_SUB (0x10) for fast encoding, PNG_ALL_FILTERS (0xF8) for best compression.
   Pass 0 to use the default (PNG_FILTER_SUB). */
#define PDFIUM_PNG_FILTER_NONE  0x08
#define PDFIUM_PNG_FILTER_SUB   0x10
#define PDFIUM_PNG_FILTER_UP    0x20
#define PDFIUM_PNG_FILTER_AVG   0x40
#define PDFIUM_PNG_FILTER_PAETH 0x80
#define PDFIUM_PNG_ALL_FILTERS  0xF8

/* Encode raw pixels to a PNG file. Returns 0 on success.
   filter_flags: bitmask of PDFIUM_PNG_FILTER_* constants, or 0 for default (SUB). */
PDFIUM_PNG_API int pdfium_png_encode_to_file(
    const char* output_path,
    const uint8_t* pixel_data,
    int width,
    int height,
    int stride,
    PdfiumPngFormat format,
    int compression_level,
    int filter_flags
);

/* Encode raw pixels to a memory buffer.
   Caller must free *out_data with pdfium_png_free(). Returns 0 on success.
   filter_flags: bitmask of PDFIUM_PNG_FILTER_* constants, or 0 for default (SUB). */
PDFIUM_PNG_API int pdfium_png_encode_to_memory(
    const uint8_t* pixel_data,
    int width,
    int height,
    int stride,
    PdfiumPngFormat format,
    int compression_level,
    int filter_flags,
    uint8_t** out_data,
    size_t* out_size
);

/* Read PNG header from file. Returns 0 on success. */
PDFIUM_PNG_API int pdfium_png_read_header(
    const char* input_path,
    PdfiumPngInfo* info
);

/* Read PNG header from memory. Returns 0 on success. */
PDFIUM_PNG_API int pdfium_png_read_header_from_memory(
    const uint8_t* png_data,
    size_t png_size,
    PdfiumPngInfo* info
);

/* Decode PNG file to raw pixels.
   Caller must free *out_data with pdfium_png_free(). Returns 0 on success. */
PDFIUM_PNG_API int pdfium_png_decode_from_file(
    const char* input_path,
    PdfiumPngFormat output_format,
    uint8_t** out_data,
    int* out_width,
    int* out_height,
    int* out_stride
);

/* Decode PNG from memory to raw pixels.
   Caller must free *out_data with pdfium_png_free(). Returns 0 on success. */
PDFIUM_PNG_API int pdfium_png_decode_from_memory(
    const uint8_t* png_data,
    size_t png_size,
    PdfiumPngFormat output_format,
    uint8_t** out_data,
    int* out_width,
    int* out_height,
    int* out_stride
);

/* Free a buffer allocated by encode/decode functions. */
PDFIUM_PNG_API void pdfium_png_free(uint8_t* data);

/* Get the last error message (thread-local). */
PDFIUM_PNG_API const char* pdfium_png_get_error(void);

#ifdef __cplusplus
}
#endif

#endif /* PDFIUM_PNG_H */
