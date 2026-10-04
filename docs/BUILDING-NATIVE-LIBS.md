# Building Native Libraries

PdfiumWrapper loads five native libraries:

- **PDFium**: Google's PDF engine. It is not compiled here: the build scripts download a prebuilt binary from [bblanchon/pdfium-binaries](https://github.com/bblanchon/pdfium-binaries) (see [Getting PDFium](#getting-pdfium)).
- **libtiff**: the standard C library for reading and writing TIFF images.
- **tiff_shim**: a thin C wrapper around libtiff's variadic `TIFFSetField` function. .NET's P/Invoke cannot call variadic C functions correctly on ARM64, where variadic arguments are passed differently from fixed parameters (see [Why the Shim Exists](#why-the-shim-exists)). The wrapper uses the shim on every platform.
- **libjpeg-turbo**: SIMD-accelerated JPEG encoding and decoding through the TurboJPEG API.
- **pdfium_png**: a thin C shim around libpng. It handles setjmp/longjmp internally and exposes a simple return-code API that is safe for .NET P/Invoke. It statically links both libpng and zlib-ng (a SIMD-accelerated zlib replacement), so only one binary is shipped per platform and there is no system zlib dependency.

The four image libraries are compiled for each target platform. All five binaries go in `src/libs/{rid}/`.

The binaries in `src/libs/` are committed to the repository. CI never builds them: the release workflow packs `src/libs/<rid>/` as it is into the `PdfiumWrapper.runtime.<rid>` packages (see [Packaging](#packaging)). After rebuilding a library, commit the new binary.

## Quick Start

For macOS and Linux, use the Unix build script from the repository root:

```bash
# Build the host RID
bash src/native/build-natives.sh --target host --clean

# Or build a specific RID
bash src/native/build-natives.sh --target osx-arm64 --clean
bash src/native/build-natives.sh --target osx-x64 --clean
bash src/native/build-natives.sh --target linux-x64 --clean
```

The Unix script supports `osx-arm64`, `osx-x64` and `linux-x64`. Linux builds always run in Docker with `--platform linux/amd64`, so Docker must be available, even on a Linux host. Use `--only pdfium,libtiff,tiff_shim,libjpeg_turbo,pdfium_png` to rebuild selected components, or `--no-pdfium` to skip the PDFium download.

The Unix script needs these commands on the host: `curl`, `unzip` and `tar` (downloads); `cmake`, `cc` and `clang` (macOS builds); `install_name_tool` and `codesign` (macOS); `xattr` (macOS, optional); and `docker` (linux-x64).

For Windows x64, use the Windows build script:

```cmd
src\native\build-natives.cmd --clean
```

Both scripts create `_native_build/` for downloaded sources and intermediates, then copy final artifacts into `src/libs/{rid}/`. The manual sections below show what the scripts do, for troubleshooting or one-off builds. Where a manual step and the script differ, the script is authoritative.

## Source Versions

Both scripts pin these versions. Override one through its environment variable (`PDFIUM_VERSION`, `LIBTIFF_VERSION`, `LIBJPEG_TURBO_VERSION`, `ZLIB_NG_VERSION`, `LIBPNG_VERSION`) or, in the Unix script, its `--*-version` flag. `PDFIUM_VERSION` takes a build number (`8076`) or `latest`, which downloads again on every run. The Unix script also accepts the release tag form (`chromium/8076`); the Windows script takes only the bare number.

| Library | Version | Source |
|---|---|---|
| PDFium (prebuilt) | chromium/8076 | https://github.com/bblanchon/pdfium-binaries/releases/tag/chromium/8076 |
| libtiff | 4.7.2 | https://download.osgeo.org/libtiff/tiff-4.7.2.zip |
| libjpeg-turbo | 3.2.0 | https://github.com/libjpeg-turbo/libjpeg-turbo/archive/refs/tags/3.2.0.zip |
| zlib-ng | 2.3.3 | https://github.com/zlib-ng/zlib-ng/archive/refs/tags/2.3.3.zip |
| libpng | 1.6.59 | https://github.com/pnggroup/libpng/archive/refs/tags/v1.6.59.zip |

## Prerequisites

### macOS (ARM64 and x64)

Xcode Command Line Tools provide `clang`/`cc`, the linker, the system headers, `install_name_tool` and `codesign`:

```bash
# Check if installed
xcode-select -p

# Install if needed
xcode-select --install
```

CMake (and, optionally, NASM for x86-64 SIMD in libjpeg-turbo):

```bash
brew install cmake nasm
```

macOS ships with zlib, so libtiff's core dependency is covered: `libtiff.dylib` links the system `/usr/lib/libz.1.dylib`.

### Linux (x64)

Docker is required for the automated `build-natives.sh --target linux-x64` path. The manual commands below run in the same `ubuntu:22.04` container the script uses, and install their own build tools in it.

To build directly on a Linux host instead, install the equivalent tools:

```bash
# Ubuntu/Debian
apt-get update && apt-get install -y build-essential cmake zlib1g-dev nasm patchelf curl unzip

# RHEL/Fedora
dnf install gcc gcc-c++ cmake zlib-devel nasm patchelf curl unzip
```

> **Note**: `nasm` (or `yasm`) is required for libjpeg-turbo's SIMD optimizations. Without it, the library still builds but without SIMD acceleration.

### Windows (x64)

- Visual Studio 2022 or 2026 (or the matching Build Tools) with the C++ workload
- CMake (bundled with Visual Studio or install separately)
- curl and tar (included with current Windows releases)
- unzip on PATH, or PowerShell `Expand-Archive` as the script fallback
- NASM (optional, for libjpeg-turbo SIMD support): download from https://www.nasm.us/

---

## Getting PDFium

PDFium is not compiled. Both scripts download the prebuilt archive for the pinned build from bblanchon/pdfium-binaries:

```text
https://github.com/bblanchon/pdfium-binaries/releases/download/chromium/8076/pdfium-<archive>.tgz
```

| RID | `<archive>` | Library in the archive | Copied to |
|---|---|---|---|
| win-x64 | `win-x64` | `bin/pdfium.dll` | `src/libs/win-x64/pdfium.dll` |
| osx-arm64 | `mac-arm64` | `lib/libpdfium.dylib` | `src/libs/osx-arm64/libpdfium.dylib` |
| osx-x64 | `mac-x64` | `lib/libpdfium.dylib` | `src/libs/osx-x64/libpdfium.dylib` |
| linux-x64 | `linux-x64` | `lib/libpdfium.so` | `src/libs/linux-x64/libpdfium.so` |

With `PDFIUM_VERSION=latest` the URL is `https://github.com/bblanchon/pdfium-binaries/releases/latest/download/pdfium-<archive>.tgz`.

To get it by hand:

```bash
curl -fL -o pdfium-mac-arm64.tgz \
    https://github.com/bblanchon/pdfium-binaries/releases/download/chromium/8076/pdfium-mac-arm64.tgz
mkdir pdfium-mac-arm64 && tar -xzf pdfium-mac-arm64.tgz -C pdfium-mac-arm64
cp pdfium-mac-arm64/lib/libpdfium.dylib src/libs/osx-arm64/

# macOS only: remove the quarantine attribute and ad-hoc sign (see macOS Notes)
xattr -d com.apple.quarantine src/libs/osx-arm64/libpdfium.dylib 2>/dev/null || true
codesign --force --sign - src/libs/osx-arm64/libpdfium.dylib
```

Each archive also contains a `VERSION` file with the exact Chromium build.

---

## macOS Notes

These apply to every macOS library. The Unix script does all of them; the manual commands below include them.

- **Per-architecture binaries.** `osx-arm64` and `osx-x64` are built and shipped separately. There are no universal (fat) binaries. On Apple Silicon, the x64 build is a cross-compile (`CMAKE_OSX_ARCHITECTURES=x86_64`, `-arch x86_64`).
- **Minimum macOS version.** 11.0 for arm64 and 10.15 for x64. Pass `-DCMAKE_OSX_DEPLOYMENT_TARGET=<version>` to CMake and `-mmacosx-version-min=<version>` to `cc`/`clang`.
- **Install names.** Every shipped dylib's install name is `@rpath/<file>.dylib`. CMake builds libtiff and libjpeg-turbo with versioned install names (for example `@rpath/libtiff.6.dylib`), and those files are not shipped. Set the id with `install_name_tool -id`, and make sure `libtiff_shim.dylib` references `@rpath/libtiff.dylib`, not `@rpath/libtiff.6.dylib`:
  ```bash
  install_name_tool -id @rpath/libtiff.dylib src/libs/osx-arm64/libtiff.dylib
  install_name_tool -change @rpath/libtiff.6.dylib @rpath/libtiff.dylib src/libs/osx-arm64/libtiff_shim.dylib
  otool -L src/libs/osx-arm64/libtiff_shim.dylib   # check the result
  ```
- **Quarantine and signing.** A downloaded or modified dylib may carry the `com.apple.quarantine` attribute, and `install_name_tool` invalidates a signature. On Apple Silicon an unsigned or invalidly signed dylib fails to load. After the last change to each file, remove the attribute and ad-hoc sign:
  ```bash
  xattr -d com.apple.quarantine <file>.dylib 2>/dev/null || true
  codesign --force --sign - <file>.dylib
  ```

---

## Building libtiff

Download the source distribution:

```bash
curl -LO https://download.osgeo.org/libtiff/tiff-4.7.2.zip
unzip tiff-4.7.2.zip
cd tiff-4.7.2
```

Every build turns off the optional codecs. The wrapper writes only CCITT G4 and LZW, and an enabled codec would add a dependency (libjpeg, liblzma, libzstd, libwebp, ...) that is not shipped.

### macOS ARM64 (native on Apple Silicon)

```bash
cmake -B build-osx-arm64 -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON \
    -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=11.0 \
    -Dtiff-tools=OFF -Dtiff-tests=OFF -Dtiff-docs=OFF \
    -Djpeg=OFF -Djbig=OFF -Dlerc=OFF -Dlzma=OFF -Dwebp=OFF -Dzstd=OFF -Dlibdeflate=OFF
cmake --build build-osx-arm64 --config Release

# Copy to project, set the install name, sign
cp build-osx-arm64/libtiff/libtiff.dylib /path/to/PdfiumWrapper/src/libs/osx-arm64/
install_name_tool -id @rpath/libtiff.dylib /path/to/PdfiumWrapper/src/libs/osx-arm64/libtiff.dylib
codesign --force --sign - /path/to/PdfiumWrapper/src/libs/osx-arm64/libtiff.dylib
```

### macOS x64 (cross-compile on Apple Silicon)

```bash
cmake -B build-osx-x64 -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON \
    -DCMAKE_OSX_ARCHITECTURES=x86_64 -DCMAKE_OSX_DEPLOYMENT_TARGET=10.15 \
    -Dtiff-tools=OFF -Dtiff-tests=OFF -Dtiff-docs=OFF \
    -Djpeg=OFF -Djbig=OFF -Dlerc=OFF -Dlzma=OFF -Dwebp=OFF -Dzstd=OFF -Dlibdeflate=OFF
cmake --build build-osx-x64 --config Release

# Copy to project, set the install name, sign
cp build-osx-x64/libtiff/libtiff.dylib /path/to/PdfiumWrapper/src/libs/osx-x64/
install_name_tool -id @rpath/libtiff.dylib /path/to/PdfiumWrapper/src/libs/osx-x64/libtiff.dylib
codesign --force --sign - /path/to/PdfiumWrapper/src/libs/osx-x64/libtiff.dylib
```

### Linux x64 (via Docker)

Run from the repository root, with the source extracted at `_native_build/tiff-4.7.2` (where the script puts it). The shared library's soname is set to `libtiff.so`, the file name that is shipped; CMake's default `libtiff.so.6` is not shipped.

```bash
docker run --rm --platform linux/amd64 -v "$(pwd):/src" -w /src ubuntu:22.04 bash -c "
    apt-get update && apt-get install -y build-essential cmake zlib1g-dev patchelf &&
    cmake -S _native_build/tiff-4.7.2 -B _native_build/tiff-4.7.2/build-linux-x64 \
        -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON \
        -Dtiff-tools=OFF -Dtiff-tests=OFF -Dtiff-docs=OFF \
        -Djpeg=OFF -Djbig=OFF -Dlerc=OFF -Dlzma=OFF -Dwebp=OFF -Dzstd=OFF -Dlibdeflate=OFF &&
    cmake --build _native_build/tiff-4.7.2/build-linux-x64 --config Release &&
    cp -f _native_build/tiff-4.7.2/build-linux-x64/libtiff/libtiff.so src/libs/linux-x64/libtiff.so &&
    patchelf --set-soname libtiff.so src/libs/linux-x64/libtiff.so
"
```

`libtiff.so` links the system `libz.so.1` (package `zlib1g`), which is present on practically every Linux distribution.

Files the container creates are owned by root. The script runs `chown -R "$HOST_UID:$HOST_GID"` on `_native_build/` and `src/libs/linux-x64/` at the end of each container step; do the same after manual runs (`sudo chown -R "$(id -u):$(id -g)" _native_build src/libs/linux-x64`).

### Windows x64

From a **Developer Command Prompt** or **x64 Native Tools Command Prompt** for Visual Studio:

```cmd
cmake -B build -DBUILD_SHARED_LIBS=ON -DCMAKE_GENERATOR_PLATFORM=x64 ^
    -Dtiff-tools=OFF -Dtiff-tests=OFF -Dtiff-docs=OFF ^
    -Dzlib=OFF -Djpeg=OFF -Djbig=OFF -Dlerc=OFF -Dlzma=OFF -Dwebp=OFF -Dzstd=OFF -Dlibdeflate=OFF
cmake --build build --config Release

copy build\libtiff\Release\tiff.dll \path\to\PdfiumWrapper\src\libs\win-x64\
```

> **Note**: `build-natives.cmd` currently configures libtiff without the codec `-D...=OFF` flags. The shipped `tiff.dll` imports no zlib or JPEG DLL only because CMake found neither library on the build machine. If zlib or libjpeg is on `CMAKE_PREFIX_PATH`, the script's `tiff.dll` gains a dependency that is not shipped. Use the flags above for a manual build.

---

## Building tiff_shim

The shim source is at `src/native/tiff_shim.c`. It must be compiled against the libtiff you built above: it needs both the source headers (`libtiff/`) and the build directory's generated `tiffconf.h` (`<build>/libtiff/`).

### macOS ARM64

From the repository root:

```bash
cc -shared -fPIC -arch arm64 -mmacosx-version-min=11.0 \
    -Wl,-install_name,@rpath/libtiff_shim.dylib \
    -o src/libs/osx-arm64/libtiff_shim.dylib src/native/tiff_shim.c \
    -I /path/to/tiff-4.7.2/libtiff \
    -I /path/to/tiff-4.7.2/build-osx-arm64/libtiff \
    -L src/libs/osx-arm64 -ltiff

# Reference libtiff by its shipped name (a no-op if libtiff's id was already set), then sign
install_name_tool -change @rpath/libtiff.6.dylib @rpath/libtiff.dylib src/libs/osx-arm64/libtiff_shim.dylib
codesign --force --sign - src/libs/osx-arm64/libtiff_shim.dylib
```

### macOS x64 (cross-compile on Apple Silicon)

```bash
cc -shared -fPIC -target x86_64-apple-macos10.15 -arch x86_64 -mmacosx-version-min=10.15 \
    -Wl,-install_name,@rpath/libtiff_shim.dylib \
    -o src/libs/osx-x64/libtiff_shim.dylib src/native/tiff_shim.c \
    -I /path/to/tiff-4.7.2/libtiff \
    -I /path/to/tiff-4.7.2/build-osx-x64/libtiff \
    -L src/libs/osx-x64 -ltiff

install_name_tool -change @rpath/libtiff.6.dylib @rpath/libtiff.dylib src/libs/osx-x64/libtiff_shim.dylib
codesign --force --sign - src/libs/osx-x64/libtiff_shim.dylib
```

### Linux x64 (via Docker)

From the repository root, after the libtiff step above. The shim must find `libtiff.so` (not `libtiff.so.6`) next to itself, so its `NEEDED` entry is rewritten and its rpath set to `$ORIGIN`:

```bash
docker run --rm --platform linux/amd64 -v "$(pwd):/src" -w /src ubuntu:22.04 bash -c "
    apt-get update && apt-get install -y build-essential patchelf &&
    cc -shared -fPIC -o src/libs/linux-x64/libtiff_shim.so src/native/tiff_shim.c \
        -I _native_build/tiff-4.7.2/libtiff \
        -I _native_build/tiff-4.7.2/build-linux-x64/libtiff \
        -L src/libs/linux-x64 -ltiff &&
    patchelf --replace-needed libtiff.so.6 libtiff.so src/libs/linux-x64/libtiff_shim.so &&
    patchelf --set-rpath '\$ORIGIN' src/libs/linux-x64/libtiff_shim.so
"
```

Do not build the shim against a distribution's `libtiff-dev`: that is a different libtiff version (4.3 with soname `libtiff.so.5` on Ubuntu 22.04) from the one shipped.

### Windows x64

From a **x64 Native Tools Command Prompt**, in the libtiff source directory used above:

```cmd
cl /LD \path\to\PdfiumWrapper\src\native\tiff_shim.c ^
    /I libtiff /I build\libtiff ^
    /Fe:tiff_shim.dll ^
    /link build\libtiff\Release\tiff.lib

copy tiff_shim.dll \path\to\PdfiumWrapper\src\libs\win-x64\
```

`tiff.lib` is the import library CMake produces next to `tiff.dll`; it is not copied to `src\libs\win-x64\`.

---

## Building libjpeg-turbo

Download the source distribution:

```bash
curl -LO https://github.com/libjpeg-turbo/libjpeg-turbo/archive/refs/tags/3.2.0.zip
unzip 3.2.0.zip
cd libjpeg-turbo-3.2.0
```

`-DREQUIRE_SIMD=OFF` lets the build succeed without NASM (without x86 SIMD).

### macOS ARM64 (native on Apple Silicon)

```bash
cmake -B build-osx-arm64 -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON \
    -DENABLE_STATIC=OFF -DWITH_TURBOJPEG=ON -DREQUIRE_SIMD=OFF \
    -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=11.0
cmake --build build-osx-arm64 --config Release

# Copy to project, set the install name, sign
cp build-osx-arm64/libturbojpeg.dylib /path/to/PdfiumWrapper/src/libs/osx-arm64/
install_name_tool -id @rpath/libturbojpeg.dylib /path/to/PdfiumWrapper/src/libs/osx-arm64/libturbojpeg.dylib
codesign --force --sign - /path/to/PdfiumWrapper/src/libs/osx-arm64/libturbojpeg.dylib
```

### macOS x64 (cross-compile on Apple Silicon)

```bash
cmake -B build-osx-x64 -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON \
    -DENABLE_STATIC=OFF -DWITH_TURBOJPEG=ON -DREQUIRE_SIMD=OFF \
    -DCMAKE_OSX_ARCHITECTURES=x86_64 -DCMAKE_OSX_DEPLOYMENT_TARGET=10.15
cmake --build build-osx-x64 --config Release

# Copy to project, set the install name, sign
cp build-osx-x64/libturbojpeg.dylib /path/to/PdfiumWrapper/src/libs/osx-x64/
install_name_tool -id @rpath/libturbojpeg.dylib /path/to/PdfiumWrapper/src/libs/osx-x64/libturbojpeg.dylib
codesign --force --sign - /path/to/PdfiumWrapper/src/libs/osx-x64/libturbojpeg.dylib
```

> **Note**: The x64 cross-compile on Apple Silicon warns about missing NASM/YASM for x86 SIMD. The build still succeeds, but the x64 binary has no SSE2/AVX2 acceleration. Install NASM (`brew install nasm`) to enable x86 SIMD optimizations.

### Linux x64 (via Docker)

From the repository root, with the source extracted at `_native_build/libjpeg-turbo-3.2.0`:

```bash
docker run --rm --platform linux/amd64 -v "$(pwd):/src" -w /src ubuntu:22.04 bash -c "
    apt-get update && apt-get install -y build-essential cmake nasm patchelf &&
    cmake -S _native_build/libjpeg-turbo-3.2.0 -B _native_build/libjpeg-turbo-3.2.0/build-linux-x64 \
        -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON -DENABLE_STATIC=OFF \
        -DWITH_TURBOJPEG=ON -DREQUIRE_SIMD=OFF &&
    cmake --build _native_build/libjpeg-turbo-3.2.0/build-linux-x64 --config Release &&
    cp -f _native_build/libjpeg-turbo-3.2.0/build-linux-x64/libturbojpeg.so src/libs/linux-x64/libturbojpeg.so &&
    patchelf --set-soname libturbojpeg.so src/libs/linux-x64/libturbojpeg.so
"
```

### Windows x64

From a **Developer Command Prompt** or **x64 Native Tools Command Prompt** (with NASM on PATH for SIMD):

```cmd
cmake -B build -A x64 -DENABLE_STATIC=OFF -DWITH_TURBOJPEG=ON -DREQUIRE_SIMD=OFF
cmake --build build --config Release

copy build\Release\turbojpeg.dll \path\to\PdfiumWrapper\src\libs\win-x64\
```

If NASM is installed but not on PATH, pass `-DCMAKE_ASM_NASM_COMPILER=<path to nasm.exe>`, as the script does.

---

## Building pdfium_png (libpng shim)

The shim source is at `src/native/pdfium_png.c` and `src/native/pdfium_png.h`. It wraps libpng and handles setjmp/longjmp error handling internally, BGRA↔RGBA conversion (through `png_set_bgr()`) and memory I/O. Both libpng and zlib-ng are statically linked into the shim, so only one binary is shipped per platform and there is no runtime dependency on system zlib.

zlib-ng generates `zlib.h` and `zconf.h` at configure time, so libpng and the shim must include them from zlib-ng's install prefix (macOS, Linux) or build directory (Windows), not from the source root.

### Step 1: Build zlib-ng as a static library

zlib-ng is a SIMD-accelerated drop-in replacement for zlib (AVX2 on x86, NEON on ARM). Building with `ZLIB_COMPAT=ON` makes it API-compatible with zlib.

Download the source:

```bash
curl -LO https://github.com/zlib-ng/zlib-ng/archive/refs/tags/2.3.3.zip
unzip 2.3.3.zip
cd zlib-ng-2.3.3
```

#### macOS ARM64 (static)

```bash
cmake -B build-osx-arm64 -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DZLIB_COMPAT=ON \
    -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=11.0 \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON -DZLIB_ENABLE_TESTS=OFF
cmake --build build-osx-arm64 --config Release
cmake --install build-osx-arm64 --prefix /path/to/zlib-ng-install-osx-arm64
# output: /path/to/zlib-ng-install-osx-arm64/include/{zlib.h,zconf.h} and lib/libz.a
```

#### macOS x64 (static, cross-compile)

```bash
cmake -B build-osx-x64 -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DZLIB_COMPAT=ON \
    -DCMAKE_OSX_ARCHITECTURES=x86_64 -DCMAKE_OSX_DEPLOYMENT_TARGET=10.15 \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON -DZLIB_ENABLE_TESTS=OFF
cmake --build build-osx-x64 --config Release
cmake --install build-osx-x64 --prefix /path/to/zlib-ng-install-osx-x64
```

#### Linux x64 (static)

On Linux, all three steps run in one container; see [Linux x64 (zlib-ng, libpng and shim, via Docker)](#linux-x64-zlib-ng-libpng-and-shim-via-docker) under Step 3.

#### Windows x64 (static)

From a **x64 Native Tools Command Prompt**:

```cmd
cmake -B build -DBUILD_SHARED_LIBS=OFF -DZLIB_COMPAT=ON ^
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON -DZLIB_ENABLE_TESTS=OFF -A x64
cmake --build build --config Release
rem output: build\Release\zlibstatic.lib; the generated zlib.h and zconf.h are in build\
```

### Step 2: Build libpng as a static library (against zlib-ng)

Download the source:

```bash
curl -L -o libpng-1.6.59.zip https://github.com/pnggroup/libpng/archive/refs/tags/v1.6.59.zip
unzip libpng-1.6.59.zip
cd libpng-1.6.59
```

Point CMake at the zlib-ng headers and static library from Step 1. Without zlib-ng's generated `zconf.h`, libpng's `pnglibconf.h` generation step fails.

#### macOS ARM64 (static)

```bash
cmake -B build-osx-arm64-static -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF \
    -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=11.0 \
    -DPNG_TESTS=OFF -DPNG_TOOLS=OFF -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
    -DZLIB_INCLUDE_DIR=/path/to/zlib-ng-install-osx-arm64/include \
    -DZLIB_LIBRARY=/path/to/zlib-ng-install-osx-arm64/lib/libz.a
cmake --build build-osx-arm64-static --config Release
# output: build-osx-arm64-static/libpng16.a
```

#### macOS x64 (static, cross-compile)

```bash
cmake -B build-osx-x64-static -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF \
    -DCMAKE_OSX_ARCHITECTURES=x86_64 -DCMAKE_OSX_DEPLOYMENT_TARGET=10.15 \
    -DPNG_TESTS=OFF -DPNG_TOOLS=OFF -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
    -DZLIB_INCLUDE_DIR=/path/to/zlib-ng-install-osx-x64/include \
    -DZLIB_LIBRARY=/path/to/zlib-ng-install-osx-x64/lib/libz.a
cmake --build build-osx-x64-static --config Release
# output: build-osx-x64-static/libpng16.a
```

#### Linux x64 (static)

See the single container command in Step 3.

#### Windows x64 (static)

From a **x64 Native Tools Command Prompt**:

```cmd
cmake -B build-static -DBUILD_SHARED_LIBS=OFF -DPNG_TESTS=OFF -DPNG_TOOLS=OFF ^
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON -A x64 ^
    -DZLIB_INCLUDE_DIR=\path\to\zlib-ng-2.3.3\build ^
    -DZLIB_LIBRARY=\path\to\zlib-ng-2.3.3\build\Release\zlibstatic.lib
cmake --build build-static --config Release
rem output: build-static\Release\libpng16_static.lib
```

### Step 3: Build the pdfium_png shim

Link against both libpng and zlib-ng static libraries. No `-lz` flag is needed: zlib-ng is embedded.

#### macOS ARM64

From the repository root:

```bash
clang -shared -arch arm64 -mmacosx-version-min=11.0 \
    -O2 -fPIC -fvisibility=hidden \
    -I /path/to/libpng-1.6.59 -I /path/to/libpng-1.6.59/build-osx-arm64-static \
    -I /path/to/zlib-ng-install-osx-arm64/include \
    src/native/pdfium_png.c \
    /path/to/libpng-1.6.59/build-osx-arm64-static/libpng16.a \
    /path/to/zlib-ng-install-osx-arm64/lib/libz.a \
    -Wl,-install_name,@rpath/libpdfium_png.dylib \
    -o src/libs/osx-arm64/libpdfium_png.dylib

codesign --force --sign - src/libs/osx-arm64/libpdfium_png.dylib
```

#### macOS x64

```bash
clang -shared -arch x86_64 -mmacosx-version-min=10.15 \
    -O2 -fPIC -fvisibility=hidden \
    -I /path/to/libpng-1.6.59 -I /path/to/libpng-1.6.59/build-osx-x64-static \
    -I /path/to/zlib-ng-install-osx-x64/include \
    src/native/pdfium_png.c \
    /path/to/libpng-1.6.59/build-osx-x64-static/libpng16.a \
    /path/to/zlib-ng-install-osx-x64/lib/libz.a \
    -Wl,-install_name,@rpath/libpdfium_png.dylib \
    -o src/libs/osx-x64/libpdfium_png.dylib

codesign --force --sign - src/libs/osx-x64/libpdfium_png.dylib
```

#### Linux x64 (zlib-ng, libpng and shim, via Docker)

From the repository root, with the zlib-ng and libpng sources extracted under `_native_build/` (as the script does). zlib-ng, libpng and the shim are built in one container, and the shim is written straight to `src/libs/linux-x64/`:

```bash
docker run --rm --platform linux/amd64 -v "$(pwd):/src" -w /src/_native_build ubuntu:22.04 bash -c "
    apt-get update && apt-get install -y build-essential cmake &&

    # Build zlib-ng and install it into a staging prefix (for the generated zconf.h)
    cmake -S zlib-ng-2.3.3 -B zlib-ng-2.3.3/build-linux-x64 \
        -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DZLIB_COMPAT=ON \
        -DZLIB_ENABLE_TESTS=OFF -DCMAKE_POSITION_INDEPENDENT_CODE=ON &&
    cmake --build zlib-ng-2.3.3/build-linux-x64 --config Release &&
    cmake --install zlib-ng-2.3.3/build-linux-x64 --prefix /src/_native_build/zlib-ng-install-linux-x64 &&

    # Build libpng against zlib-ng
    cmake -S libpng-1.6.59 -B libpng-1.6.59/build-linux-x64-static \
        -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DPNG_TESTS=OFF -DPNG_TOOLS=OFF \
        -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
        -DZLIB_INCLUDE_DIR=/src/_native_build/zlib-ng-install-linux-x64/include \
        -DZLIB_LIBRARY=/src/_native_build/zlib-ng-install-linux-x64/lib/libz.a &&
    cmake --build libpng-1.6.59/build-linux-x64-static --config Release &&

    # Build the shim
    gcc -shared -O2 -fPIC -fvisibility=hidden -o /src/src/libs/linux-x64/libpdfium_png.so \
        -I libpng-1.6.59 -I libpng-1.6.59/build-linux-x64-static \
        -I zlib-ng-install-linux-x64/include \
        /src/src/native/pdfium_png.c \
        libpng-1.6.59/build-linux-x64-static/libpng16.a \
        zlib-ng-install-linux-x64/lib/libz.a \
        -lm
"
```

#### Windows x64

From a **x64 Native Tools Command Prompt** (after building the zlib-ng and libpng static libraries above). `/MD` is required: CMake builds the static libraries against the DLL C runtime, and `cl`'s default for `/LD` is the static runtime, which does not link with them.

```cmd
cl /LD /MD /O2 \path\to\PdfiumWrapper\src\native\pdfium_png.c ^
    /I \path\to\libpng-1.6.59 /I \path\to\libpng-1.6.59\build-static ^
    /I \path\to\zlib-ng-2.3.3\build ^
    /I \path\to\PdfiumWrapper\src\native ^
    /Fe:pdfium_png.dll ^
    /link ^
    \path\to\libpng-1.6.59\build-static\Release\libpng16_static.lib ^
    \path\to\zlib-ng-2.3.3\build\Release\zlibstatic.lib

copy pdfium_png.dll \path\to\PdfiumWrapper\src\libs\win-x64\
```

---

## Runtime Dependencies

What each shipped binary needs from the system:

| Platform | Binary | Needs |
|---|---|---|
| Windows | `tiff.dll`, `pdfium_png.dll` | The Visual C++ 2015-2022 x64 runtime: `VCRUNTIME140.dll` and the Universal CRT (`api-ms-win-crt-*.dll`). Windows 10 and later include the Universal CRT. `VCRUNTIME140.dll` comes with the [VC++ redistributable](https://aka.ms/vs/17/release/vc_redist.x64.exe), which most machines have but a bare Windows Server or container image may not |
| Windows | `pdfium.dll`, `turbojpeg.dll`, `tiff_shim.dll` | System DLLs only (`tiff_shim.dll` also loads `tiff.dll`) |
| Linux | `libtiff.so` | The system `libz.so.1` (package `zlib1g`), plus `libc`/`libm` |
| Linux | the others | `libc`/`libm` (and the other shipped libraries, found through `$ORIGIN`) |
| macOS | `libtiff.dylib` | The system `/usr/lib/libz.1.dylib` |
| macOS | the others | `libSystem.B.dylib` |

`pdfium_png` embeds libpng and zlib-ng, so it needs no external PNG or zlib library on any platform.

---

## Automated Build Script (Windows x64)

Instead of running each step manually, you can use the all-in-one build script at [`src/native/build-natives.cmd`](../src/native/build-natives.cmd). It downloads PDFium and all source archives, builds every library in the correct order, and copies the resulting DLLs to `src/libs/win-x64/`.

### Prerequisites

- **Visual Studio 2022 or later** (or the Build Tools) with the **"Desktop development with C++"** workload (provides MSVC, CMake and MSBuild)
- **curl and tar**: included with current Windows releases
- **unzip or PowerShell**: `unzip` is used when available; otherwise the script falls back to PowerShell `Expand-Archive`
- **(Optional) NASM**: for libjpeg-turbo SIMD acceleration. Download from https://www.nasm.us/. Without NASM the build still succeeds, but JPEG encoding and decoding are slower. The script finds `nasm.exe` on PATH or in `%LOCALAPPDATA%\bin\NASM\`.

### Configuration

Open `src/native/build-natives.cmd` and check that the `VSDIR` variable near the top matches your Visual Studio installation. The script sets it unconditionally, so edit the file rather than setting an environment variable. The default is Visual Studio 2026 (version 18) Enterprise:

```bat
set VSDIR=C:\Program Files\Microsoft Visual Studio\18\Enterprise
```

Common values:

| Edition | Path |
|---|---|
| VS 2026 Community | `C:\Program Files\Microsoft Visual Studio\18\Community` |
| VS 2026 Professional | `C:\Program Files\Microsoft Visual Studio\18\Professional` |
| VS 2026 Enterprise (default) | `C:\Program Files\Microsoft Visual Studio\18\Enterprise` |
| VS 2026 Build Tools | `C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools` |
| VS 2022 Community | `C:\Program Files\Microsoft Visual Studio\2022\Community` |
| VS 2022 Professional | `C:\Program Files\Microsoft Visual Studio\2022\Professional` |
| VS 2022 Enterprise | `C:\Program Files\Microsoft Visual Studio\2022\Enterprise` |
| VS 2022 Build Tools | `C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools` |

The script calls `vcvarsall.bat` automatically, so you do **not** need to run it from a Developer Command Prompt; a regular Command Prompt or terminal works.

### Running the script

```cmd
cd path\to\PdfiumWrapper
src\native\build-natives.cmd --clean
```

The script will:

1. Create a temporary `_native_build\` directory in the project root
2. Download and extract source archives (skipped if already present from a prior run)
3. Build each library in order, copying each DLL to `src\libs\win-x64\` as soon as it is built:
   - **PDFium** → `pdfium.dll` (downloaded prebuilt, not compiled)
   - **libtiff 4.7.2** → `tiff.dll`
   - **tiff_shim** → `tiff_shim.dll` (compiled against the libtiff built just before it)
   - **libjpeg-turbo 3.2.0** → `turbojpeg.dll`
   - **zlib-ng 2.3.3** → `zlibstatic.lib` (static, SIMD-accelerated)
   - **libpng 1.6.59** → `libpng16_static.lib` (static, linked against zlib-ng)
   - **pdfium_png shim** → `pdfium_png.dll` (statically embeds libpng + zlib-ng)
4. List the DLLs in `src\libs\win-x64\`

Each step prints `[OK]` when it finishes. A failed download, extract, configure, compile or link stops the script with `BUILD FAILED` and the error code. The `copy` after the libtiff, tiff_shim, libjpeg-turbo and pdfium_png builds is not checked (only the PDFium copy is), so check the timestamps in `src\libs\win-x64\` after a run.

Useful options:

```cmd
src\native\build-natives.cmd --only pdfium
src\native\build-natives.cmd --only libtiff,tiff_shim
src\native\build-natives.cmd --only pdfium_png
src\native\build-natives.cmd --no-pdfium
```

Library versions can be overridden from the caller environment:

```cmd
set LIBPNG_VERSION=1.6.59
src\native\build-natives.cmd --only pdfium_png
```

### Cleanup

After a successful build you can delete the `_native_build\` directory to reclaim disk space:

```cmd
rmdir /s /q _native_build
```

The downloaded sources are cached there, so keeping it makes later rebuilds faster (the script skips downloads if the source directories already exist).

---

## Directory Layout

After building, your `src/libs/` directory should look like:

```
src/libs/
  osx-arm64/
    libpdfium.dylib
    libtiff.dylib
    libtiff_shim.dylib
    libturbojpeg.dylib
    libpdfium_png.dylib
  osx-x64/
    libpdfium.dylib
    libtiff.dylib
    libtiff_shim.dylib
    libturbojpeg.dylib
    libpdfium_png.dylib
  linux-x64/
    libpdfium.so
    libtiff.so
    libtiff_shim.so
    libturbojpeg.so
    libpdfium_png.so
  win-x64/
    pdfium.dll
    tiff.dll
    tiff_shim.dll
    turbojpeg.dll
    pdfium_png.dll
```

For local builds and tests, `src/PdfiumWrapper/PdfiumWrapper.csproj` detects the host RID and copies that platform's files to `runtimes/<rid>/native/` in the build output. PDFium must be present: its item has no `Exists()` condition, so a missing PDFium binary fails the build (MSB3030). The four image libraries are guarded by `Exists()` conditions: a missing one does not break the build, only the feature that needs it at run time (TIFF, JPEG or PNG).

### How the libraries are found at run time

`NativeLibraryResolver` (`src/PdfiumWrapper/NativeLibraryResolver.cs`) is the assembly's `DllImportResolver`. For each library it tries, in order:

1. `<AppContext.BaseDirectory>/libs/<rid>/<file>` (source layout)
2. `<AppContext.BaseDirectory>/runtimes/<rid>/native/<file>` (NuGet and build output layout)
3. The platform's default search for the bare name (`pdfium`, `libpdfium`, ...), which finds system-installed libraries

To test a rebuilt library without repacking, copy it over the file in the test project's output `runtimes/<rid>/native/` directory, or put it in `libs/<rid>/` next to the application, which takes precedence.

---

## Packaging

The native binaries ship in four runtime packages, one per RID: `PdfiumWrapper.runtime.win-x64`, `PdfiumWrapper.runtime.linux-x64`, `PdfiumWrapper.runtime.osx-x64` and `PdfiumWrapper.runtime.osx-arm64`. Each is packed by `src/PdfiumWrapper.runtime/PdfiumWrapper.runtime.csproj` (`dotnet pack -p:NativeRid=<rid>`) and contains only `src/libs/<rid>/*` under `runtimes/<rid>/native/`, with no managed assembly.

The main `PdfiumWrapper` package depends on all four at exactly its own version (`[2.0.0]`):

- A portable build (no `RuntimeIdentifier`) gets every platform under `runtimes/<rid>/native/`, and the resolver loads the matching one.
- A RID-specific build or publish (`-r linux-x64`, ...) copies only its own platform.

The runtime packages exist only once the release workflow (`.github/workflows/release.yml`) has packed them, so the main project references them only when `PackRuntimeDependencies=true`. The release job sets it, restores the runtime packages from its own artifacts, and checks that the packed nuspec lists all four. Any other `dotnet pack` of `src/PdfiumWrapper/PdfiumWrapper.csproj` produces a package without natives and warns that it cannot run; such a package is for inspection only.

### Package icon

`icon.png` at the repository root (256 x 256, transparent background) is the package icon of the core, Processing and runtime packages (`<PackageIcon>icon.png</PackageIcon>` in each project). It is rendered from `logos/PdfiumWrapperLogo.svg` with Microsoft Edge in headless mode, because Inkscape drops the logo's `feDropShadow` filter. Re-render it the same way after changing the SVG.

---

## Why the Shim Exists

libtiff's `TIFFSetField` is a **variadic C function** (`uint32_t tag, ...`). On ARM64, the C calling convention passes variadic arguments differently from fixed parameters (on Apple platforms, variadic arguments go on the stack, while fixed arguments use registers). .NET's P/Invoke (both `DllImport` and `LibraryImport`) has no way to mark a function as variadic, so it generates a non-variadic call sequence, and `TIFFSetField` reads its values from the wrong place.

The shim provides non-variadic wrappers (`TIFFSetFieldInt`, `TIFFSetFieldDouble`) that take fixed parameters and forward to the real variadic `TIFFSetField` inside C, where the compiler handles the calling convention correctly. The wrapper calls them on every platform, so there is a single code path.

All other libtiff functions (`TIFFClientOpen`, `TIFFWriteScanline`, `TIFFClose`, etc.) are non-variadic and work directly via `LibraryImport` without the shim. The wrapper does not use `TIFFOpen`: on Windows it reads its `char*` path in the ANSI code page, so TIFF files are opened as a managed `FileStream` and written through `TIFFClientOpen`. The PNG shim's file-path functions (`pdfium_png_encode_to_file`, `pdfium_png_decode_from_file`, `pdfium_png_read_header`) are still built but not imported, for the same reason.

## Why the PNG Shim Exists

libpng uses **`setjmp`/`longjmp`** for error handling: when libpng encounters an error (corrupt data, out of memory, etc.), it calls `longjmp` to unwind back to a `setjmp` point. This is fundamentally incompatible with .NET's managed stack: a `longjmp` from native code through managed frames corrupts the runtime state, leading to crashes or undefined behavior.

The `pdfium_png` shim contains the `setjmp` scope entirely within C, converts libpng errors to integer return codes, and stores error messages in thread-local storage. It handles BGRA↔RGBA pixel conversion via libpng's built-in `png_set_bgr()` transform (PNG only stores RGB/RGBA; the shim tells libpng the input is BGR-ordered, so the swap happens inside libpng's write pipeline with no extra buffer copy). It also provides memory I/O functions, so PNG data can be encoded to and decoded from buffers without touching the filesystem, and exposes a configurable filter strategy parameter (`filter_flags`, default `PNG_FILTER_SUB`) to let callers trade compression ratio for encoding speed.

### Why zlib-ng

The shim statically links **zlib-ng** (a SIMD-accelerated fork of zlib) instead of relying on system zlib. zlib-ng uses AVX2 on x86 and NEON on ARM for significantly faster compression, roughly 2-3x faster than vanilla zlib at the same compression level. Building with `ZLIB_COMPAT=ON` makes it a transparent drop-in for libpng (same API, same header names). Since both libpng and zlib-ng are statically embedded, the resulting `pdfium_png` binary needs nothing beyond the C runtime (see [Runtime Dependencies](#runtime-dependencies)).
