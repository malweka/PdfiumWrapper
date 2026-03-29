/*
 * pdfium_png.c — Thin C shim around libpng.
 *
 * Wraps libpng's setjmp/longjmp error handling into simple return-code
 * functions safe for P/Invoke from .NET.  Handles BGRA ↔ RGBA conversion
 * so the managed side never touches raw pixel channels.
 */

#include "pdfium_png.h"
#include <png.h>
#include <stdlib.h>
#include <string.h>

/* ── thread-local error message ────────────────────────────────────── */

#if defined(_WIN32)
  static __declspec(thread) char s_error[256];
#else
  static _Thread_local char s_error[256];
#endif

static void set_error(const char* msg)
{
    if (msg) {
        strncpy(s_error, msg, sizeof(s_error) - 1);
        s_error[sizeof(s_error) - 1] = '\0';
    } else {
        s_error[0] = '\0';
    }
}

static void png_error_callback(png_structp png, png_const_charp msg)
{
    set_error(msg);
    png_longjmp(png, 1);
}

static void png_warning_callback(png_structp png, png_const_charp msg)
{
    (void)png;
    (void)msg;
}

/* ── helpers ───────────────────────────────────────────────────────── */

static int bytes_per_pixel(PdfiumPngFormat fmt)
{
    switch (fmt) {
        case PNG_FMT_GRAY:       return 1;
        case PNG_FMT_GRAY_ALPHA: return 2;
        case PNG_FMT_RGB:        return 3;
        case PNG_FMT_RGBA:
        case PNG_FMT_BGRA:       return 4;
        default:                 return 0;
    }
}

static int fmt_to_png_color_type(PdfiumPngFormat fmt)
{
    switch (fmt) {
        case PNG_FMT_GRAY:       return PNG_COLOR_TYPE_GRAY;
        case PNG_FMT_GRAY_ALPHA: return PNG_COLOR_TYPE_GRAY_ALPHA;
        case PNG_FMT_RGB:        return PNG_COLOR_TYPE_RGB;
        case PNG_FMT_RGBA:
        case PNG_FMT_BGRA:       return PNG_COLOR_TYPE_RGB_ALPHA;
        default:                 return -1;
    }
}

/* Swap B and R in a 4-byte-per-pixel row (BGRA ↔ RGBA). */
static void swap_br_row(uint8_t* dst, const uint8_t* src, int width)
{
    for (int i = 0; i < width; i++) {
        dst[i * 4 + 0] = src[i * 4 + 2]; /* R ← B */
        dst[i * 4 + 1] = src[i * 4 + 1]; /* G ← G */
        dst[i * 4 + 2] = src[i * 4 + 0]; /* B ← R */
        dst[i * 4 + 3] = src[i * 4 + 3]; /* A ← A */
    }
}

/* ── memory write target ───────────────────────────────────────────── */

typedef struct {
    uint8_t* buf;
    size_t   size;
    size_t   capacity;
} MemBuf;

static void mem_write_fn(png_structp png, png_bytep data, size_t length)
{
    MemBuf* mb = (MemBuf*)png_get_io_ptr(png);
    size_t need = mb->size + length;
    if (need > mb->capacity) {
        size_t cap = mb->capacity ? mb->capacity : 4096;
        while (cap < need) cap *= 2;
        uint8_t* tmp = (uint8_t*)realloc(mb->buf, cap);
        if (!tmp) {
            png_error(png, "pdfium_png: out of memory");
            return;
        }
        mb->buf = tmp;
        mb->capacity = cap;
    }
    memcpy(mb->buf + mb->size, data, length);
    mb->size += length;
}

static void mem_flush_fn(png_structp png)
{
    (void)png;
}

/* ── memory read source ────────────────────────────────────────────── */

typedef struct {
    const uint8_t* data;
    size_t         size;
    size_t         offset;
} MemRead;

static void mem_read_fn(png_structp png, png_bytep out, size_t length)
{
    MemRead* mr = (MemRead*)png_get_io_ptr(png);
    if (mr->offset + length > mr->size) {
        png_error(png, "pdfium_png: read past end of buffer");
        return;
    }
    memcpy(out, mr->data + mr->offset, length);
    mr->offset += length;
}

/* ── encoding ──────────────────────────────────────────────────────── */

static int encode_core(
    const uint8_t* pixel_data, int width, int height, int stride,
    PdfiumPngFormat format, int compression_level, int filter_flags,
    /* file target (NULL for memory) */
    FILE* fp,
    /* memory target (ignored when fp != NULL) */
    MemBuf* mb)
{
    int bpp = bytes_per_pixel(format);
    if (bpp == 0) { set_error("invalid pixel format"); return -1; }

    int color_type = fmt_to_png_color_type(format);
    int actual_stride = stride > 0 ? stride : width * bpp;
    int is_bgra = (format == PNG_FMT_BGRA);

    png_structp png = png_create_write_struct(PNG_LIBPNG_VER_STRING, NULL,
                                              png_error_callback, png_warning_callback);
    if (!png) { set_error("png_create_write_struct failed"); return -1; }

    png_infop info = png_create_info_struct(png);
    if (!info) {
        png_destroy_write_struct(&png, NULL);
        set_error("png_create_info_struct failed");
        return -1;
    }

    if (setjmp(png_jmpbuf(png))) {
        /* libpng jumped here on error — s_error already set by callback */
        png_destroy_write_struct(&png, &info);
        return -1;
    }

    if (fp) {
        png_init_io(png, fp);
    } else {
        png_set_write_fn(png, mb, mem_write_fn, mem_flush_fn);
    }

    if (compression_level >= 0 && compression_level <= 9)
        png_set_compression_level(png, compression_level);

    /* Filter strategy: default to SUB if caller passes 0 */
    if (filter_flags == 0)
        filter_flags = PNG_FILTER_SUB;
    png_set_filter(png, 0, filter_flags);

    png_set_IHDR(png, info, (png_uint_32)width, (png_uint_32)height,
                 8, color_type, PNG_INTERLACE_NONE,
                 PNG_COMPRESSION_TYPE_DEFAULT, PNG_FILTER_TYPE_DEFAULT);

    /* Let libpng handle BGR ↔ RGB swap internally — no manual row copy needed */
    if (is_bgra)
        png_set_bgr(png);

    png_write_info(png, info);

    for (int y = 0; y < height; y++) {
        /* libpng won't modify the row; cast away const safely */
        png_write_row(png, (png_bytep)(pixel_data + (size_t)y * actual_stride));
    }

    png_write_end(png, NULL);
    png_destroy_write_struct(&png, &info);
    return 0;
}

PDFIUM_PNG_API int pdfium_png_encode_to_file(
    const char* output_path,
    const uint8_t* pixel_data,
    int width, int height, int stride,
    PdfiumPngFormat format, int compression_level, int filter_flags)
{
    FILE* fp = fopen(output_path, "wb");
    if (!fp) { set_error("cannot open output file"); return -1; }

    int rc = encode_core(pixel_data, width, height, stride,
                         format, compression_level, filter_flags, fp, NULL);
    fclose(fp);
    if (rc != 0) remove(output_path);
    return rc;
}

PDFIUM_PNG_API int pdfium_png_encode_to_memory(
    const uint8_t* pixel_data,
    int width, int height, int stride,
    PdfiumPngFormat format, int compression_level, int filter_flags,
    uint8_t** out_data, size_t* out_size)
{
    if (!out_data || !out_size) { set_error("null output pointers"); return -1; }
    *out_data = NULL;
    *out_size = 0;

    MemBuf mb = { NULL, 0, 0 };
    int rc = encode_core(pixel_data, width, height, stride,
                         format, compression_level, filter_flags, NULL, &mb);
    if (rc != 0) {
        free(mb.buf);
        return rc;
    }
    *out_data = mb.buf;
    *out_size = mb.size;
    return 0;
}

/* ── decoding ──────────────────────────────────────────────────────── */

static int decode_core(
    /* file source (NULL for memory) */
    FILE* fp,
    /* memory source (ignored when fp != NULL) */
    MemRead* mr,
    PdfiumPngFormat output_format,
    uint8_t** out_data, int* out_width, int* out_height, int* out_stride)
{
    int want_bgra = (output_format == PNG_FMT_BGRA);
    /* For decoding, ask libpng for RGBA and we convert to BGRA afterwards */
    int want_gray = (output_format == PNG_FMT_GRAY);
    int want_gray_alpha = (output_format == PNG_FMT_GRAY_ALPHA);

    png_structp png = png_create_read_struct(PNG_LIBPNG_VER_STRING, NULL,
                                             png_error_callback, png_warning_callback);
    if (!png) { set_error("png_create_read_struct failed"); return -1; }

    png_infop info = png_create_info_struct(png);
    if (!info) {
        png_destroy_read_struct(&png, NULL, NULL);
        set_error("png_create_info_struct failed");
        return -1;
    }

    png_bytep* row_ptrs = NULL;
    uint8_t* buf = NULL;

    if (setjmp(png_jmpbuf(png))) {
        free(row_ptrs);
        free(buf);
        png_destroy_read_struct(&png, &info, NULL);
        return -1;
    }

    if (fp) {
        png_init_io(png, fp);
    } else {
        png_set_read_fn(png, mr, mem_read_fn);
    }

    png_read_info(png, info);

    png_uint_32 w = png_get_image_width(png, info);
    png_uint_32 h = png_get_image_height(png, info);
    int bit_depth  = png_get_bit_depth(png, info);
    int color_type = png_get_color_type(png, info);

    /* Normalize to 8-bit RGBA / Gray / GrayAlpha */
    if (bit_depth == 16)
        png_set_strip_16(png);
    if (color_type == PNG_COLOR_TYPE_PALETTE)
        png_set_palette_to_rgb(png);
    if (color_type == PNG_COLOR_TYPE_GRAY && bit_depth < 8)
        png_set_expand_gray_1_2_4_to_8(png);
    if (png_get_valid(png, info, PNG_INFO_tRNS))
        png_set_tRNS_to_alpha(png);

    if (want_gray || want_gray_alpha) {
        if (color_type == PNG_COLOR_TYPE_RGB || color_type == PNG_COLOR_TYPE_RGB_ALPHA
            || color_type == PNG_COLOR_TYPE_PALETTE) {
            png_set_rgb_to_gray_fixed(png, 1, -1, -1);
        }
        if (want_gray) {
            png_set_strip_alpha(png);
        } else if (!(color_type & PNG_COLOR_MASK_ALPHA)) {
            png_set_add_alpha(png, 0xFF, PNG_FILLER_AFTER);
        }
    } else {
        /* want RGB / RGBA / BGRA */
        if (color_type == PNG_COLOR_TYPE_GRAY || color_type == PNG_COLOR_TYPE_GRAY_ALPHA)
            png_set_gray_to_rgb(png);

        /* Ensure alpha channel exists for RGBA/BGRA */
        if (output_format == PNG_FMT_RGBA || output_format == PNG_FMT_BGRA) {
            if (!(color_type & PNG_COLOR_MASK_ALPHA))
                png_set_add_alpha(png, 0xFF, PNG_FILLER_AFTER);
        } else if (output_format == PNG_FMT_RGB) {
            png_set_strip_alpha(png);
        }
    }

    png_read_update_info(png, info);

    int out_bpp = bytes_per_pixel(output_format);
    int row_bytes = (int)w * out_bpp;
    buf = (uint8_t*)malloc((size_t)row_bytes * h);
    if (!buf) {
        png_error(png, "pdfium_png: out of memory");
        return -1;
    }

    row_ptrs = (png_bytep*)malloc(sizeof(png_bytep) * h);
    if (!row_ptrs) {
        free(buf);
        png_error(png, "pdfium_png: out of memory for row pointers");
        return -1;
    }

    for (png_uint_32 y = 0; y < h; y++)
        row_ptrs[y] = buf + (size_t)y * row_bytes;

    png_read_image(png, row_ptrs);
    png_read_end(png, NULL);
    png_destroy_read_struct(&png, &info, NULL);
    free(row_ptrs);

    /* RGBA → BGRA swap if needed */
    if (want_bgra) {
        for (png_uint_32 y = 0; y < h; y++) {
            uint8_t* row = buf + (size_t)y * row_bytes;
            swap_br_row(row, row, (int)w);  /* in-place is fine: R/B swap is its own inverse */
        }
    }

    *out_data   = buf;
    *out_width  = (int)w;
    *out_height = (int)h;
    *out_stride = row_bytes;
    return 0;
}

PDFIUM_PNG_API int pdfium_png_decode_from_file(
    const char* input_path,
    PdfiumPngFormat output_format,
    uint8_t** out_data, int* out_width, int* out_height, int* out_stride)
{
    FILE* fp = fopen(input_path, "rb");
    if (!fp) { set_error("cannot open input file"); return -1; }

    int rc = decode_core(fp, NULL, output_format, out_data, out_width, out_height, out_stride);
    fclose(fp);
    return rc;
}

PDFIUM_PNG_API int pdfium_png_decode_from_memory(
    const uint8_t* png_data, size_t png_size,
    PdfiumPngFormat output_format,
    uint8_t** out_data, int* out_width, int* out_height, int* out_stride)
{
    MemRead mr = { png_data, png_size, 0 };
    return decode_core(NULL, &mr, output_format, out_data, out_width, out_height, out_stride);
}

/* ── header reading ────────────────────────────────────────────────── */

static int read_header_core(FILE* fp, MemRead* mr, PdfiumPngInfo* info)
{
    png_structp png = png_create_read_struct(PNG_LIBPNG_VER_STRING, NULL,
                                             png_error_callback, png_warning_callback);
    if (!png) { set_error("png_create_read_struct failed"); return -1; }

    png_infop pi = png_create_info_struct(png);
    if (!pi) {
        png_destroy_read_struct(&png, NULL, NULL);
        set_error("png_create_info_struct failed");
        return -1;
    }

    if (setjmp(png_jmpbuf(png))) {
        png_destroy_read_struct(&png, &pi, NULL);
        return -1;
    }

    if (fp) {
        png_init_io(png, fp);
    } else {
        png_set_read_fn(png, mr, mem_read_fn);
    }

    png_read_info(png, pi);

    info->width     = (int)png_get_image_width(png, pi);
    info->height    = (int)png_get_image_height(png, pi);
    info->bit_depth = png_get_bit_depth(png, pi);
    info->color_type = png_get_color_type(png, pi);
    info->channels  = png_get_channels(png, pi);

    png_destroy_read_struct(&png, &pi, NULL);
    return 0;
}

PDFIUM_PNG_API int pdfium_png_read_header(const char* input_path, PdfiumPngInfo* info)
{
    FILE* fp = fopen(input_path, "rb");
    if (!fp) { set_error("cannot open input file"); return -1; }
    int rc = read_header_core(fp, NULL, info);
    fclose(fp);
    return rc;
}

PDFIUM_PNG_API int pdfium_png_read_header_from_memory(
    const uint8_t* png_data, size_t png_size, PdfiumPngInfo* info)
{
    MemRead mr = { png_data, png_size, 0 };
    return read_header_core(NULL, &mr, info);
}

/* ── utility ───────────────────────────────────────────────────────── */

PDFIUM_PNG_API void pdfium_png_free(uint8_t* data) { free(data); }

PDFIUM_PNG_API const char* pdfium_png_get_error(void) { return s_error; }
