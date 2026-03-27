/*
 * Non-variadic wrappers for libtiff's TIFFSetField.
 *
 * TIFFSetField is a variadic C function (uint32_t tag, ...).
 * .NET's P/Invoke (both DllImport and LibraryImport) cannot correctly call
 * variadic functions on ARM64, where the ABI passes variadic arguments
 * differently from fixed parameters.
 *
 * These thin wrappers take fixed parameters and forward to the real
 * variadic TIFFSetField, letting the C compiler handle the va_list correctly.
 *
 * Build per platform:
 *   macOS arm64:  cc -shared -o libtiff_shim.dylib tiff_shim.c -ltiff
 *   macOS x64:    cc -shared -o libtiff_shim.dylib tiff_shim.c -ltiff
 *   Linux x64:    cc -shared -fPIC -o libtiff_shim.so tiff_shim.c -ltiff
 *   Windows x64:  cl /LD tiff_shim.c /Fe:tiff_shim.dll tiff.lib
 */

#include <tiffio.h>

int TIFFSetFieldInt(TIFF* tiff, uint32_t tag, int value)
{
    return TIFFSetField(tiff, tag, value);
}

int TIFFSetFieldDouble(TIFF* tiff, uint32_t tag, double value)
{
    return TIFFSetField(tiff, tag, value);
}
