# Building Native Libraries

PdfiumWrapper ships with native libraries for image format support:

- **libtiff** — The standard C library for reading and writing TIFF images
- **tiff_shim** — A thin C wrapper around libtiff's variadic `TIFFSetField` function, required because .NET's P/Invoke cannot correctly call variadic C functions on ARM64 (and the behavior is unreliable on x64)
- **libjpeg-turbo** — SIMD-accelerated JPEG encoding/decoding via the TurboJPEG API

All must be compiled for each target platform and placed in the `src/libs/{rid}/` directory.

## Prerequisites

### macOS (ARM64 and x64)

Xcode Command Line Tools (provides `clang`, linker, and system headers):

```bash
# Check if installed
xcode-select -p

# Install if needed
xcode-select --install
```

CMake:

```bash
brew install cmake
```

macOS ships with zlib already, so libtiff's core dependencies are covered.

### Linux (x64)

Build tools and library dependencies:

```bash
# Ubuntu/Debian
apt-get update && apt-get install -y build-essential cmake zlib1g-dev libjpeg-dev nasm

# RHEL/Fedora
dnf install gcc gcc-c++ cmake zlib-devel libjpeg-turbo-devel nasm
```

> **Note**: `nasm` (or `yasm`) is required for libjpeg-turbo's SIMD optimizations. Without it, the library still builds but without SIMD acceleration.

### Windows (x64)

- Visual Studio 2022 (or Build Tools) with C++ workload
- CMake (bundled with Visual Studio or install separately)
- NASM (for libjpeg-turbo SIMD support) — download from https://www.nasm.us/ and add to PATH

---

## Building libtiff

Download the source distribution:

```bash
curl -LO https://download.osgeo.org/libtiff/tiff-4.7.1.zip
unzip tiff-4.7.1.zip
cd tiff-4.7.1
```

### macOS ARM64 (native on Apple Silicon)

```bash
cmake -B build-arm64 -DBUILD_SHARED_LIBS=ON -DCMAKE_OSX_ARCHITECTURES=arm64 \
    -Dtiff-tools=OFF -Dtiff-tests=OFF -Dtiff-docs=OFF
cmake --build build-arm64 --config Release

# Copy to project
cp build-arm64/libtiff/libtiff.dylib /path/to/PdfiumWrapper/src/libs/osx-arm64/
```

### macOS x64 (cross-compile on Apple Silicon)

```bash
cmake -B build-x64 -DBUILD_SHARED_LIBS=ON -DCMAKE_OSX_ARCHITECTURES=x86_64 \
    -Dtiff-tools=OFF -Dtiff-tests=OFF -Dtiff-docs=OFF
cmake --build build-x64 --config Release

# Copy to project
cp build-x64/libtiff/libtiff.dylib /path/to/PdfiumWrapper/src/libs/osx-x64/
```

### Linux x64 (via Docker)

Use a Docker container for a clean, reproducible build:

```bash
docker run --rm -v "$(pwd):/src" -w /src ubuntu:22.04 bash -c "
    apt-get update && apt-get install -y build-essential cmake zlib1g-dev libjpeg-dev curl unzip &&
    curl -LO https://download.osgeo.org/libtiff/tiff-4.7.1.zip &&
    unzip tiff-4.7.1.zip &&
    cd tiff-4.7.1 &&
    cmake -B build -DBUILD_SHARED_LIBS=ON \
        -Dtiff-tools=OFF -Dtiff-tests=OFF -Dtiff-docs=OFF &&
    cmake --build build --config Release &&
    cp build/libtiff/libtiff.so /src/libtiff.so
"

# Copy to project
cp libtiff.so /path/to/PdfiumWrapper/src/libs/linux-x64/
```

### Windows x64

From a **Developer Command Prompt for VS 2022** or **x64 Native Tools Command Prompt**:

```cmd
cmake -B build -DBUILD_SHARED_LIBS=ON -DCMAKE_GENERATOR_PLATFORM=x64 ^
    -Dtiff-tools=OFF -Dtiff-tests=OFF -Dtiff-docs=OFF
cmake --build build --config Release

copy build\libtiff\Release\tiff.dll \path\to\PdfiumWrapper\src\libs\win-x64\
```

---

## Building tiff_shim

The shim source is at `src/native/tiff_shim.c`. It must be compiled against the same libtiff version you built above.

### macOS ARM64

```bash
cc -shared -o libtiff_shim.dylib src/native/tiff_shim.c \
    -I/path/to/tiff-4.7.1/libtiff \
    -L src/libs/osx-arm64 -ltiff

cp libtiff_shim.dylib src/libs/osx-arm64/
```

Or if libtiff headers are installed via Homebrew:

```bash
cc -shared -o libtiff_shim.dylib src/native/tiff_shim.c \
    -I$(brew --prefix libtiff)/include \
    -Lsrc/libs/osx-arm64 -ltiff

cp libtiff_shim.dylib src/libs/osx-arm64/
```

### macOS x64 (cross-compile on Apple Silicon)

```bash
cc -shared -target x86_64-apple-macos10.15 -o libtiff_shim.dylib src/native/tiff_shim.c \
    -I/path/to/tiff-4.7.1/libtiff \
    -Lsrc/libs/osx-x64 -ltiff

cp libtiff_shim.dylib src/libs/osx-x64/
```

### Linux x64 (via Docker)

```bash
docker run --rm -v "$(pwd):/src" -w /src ubuntu:22.04 bash -c "
    apt-get update && apt-get install -y build-essential libtiff-dev &&
    cc -shared -fPIC -o libtiff_shim.so src/native/tiff_shim.c -ltiff
"

cp libtiff_shim.so src/libs/linux-x64/
```

Alternatively, if you built libtiff from source in the previous step, point at those headers:

```bash
docker run --rm -v "$(pwd):/src" -w /src ubuntu:22.04 bash -c "
    apt-get update && apt-get install -y build-essential zlib1g-dev libjpeg-dev &&
    cc -shared -fPIC -o libtiff_shim.so src/native/tiff_shim.c \
        -I/path/to/tiff-4.7.1/libtiff \
        -Lsrc/libs/linux-x64 -ltiff
"
```

### Windows x64

From a **x64 Native Tools Command Prompt**:

```cmd
cl /LD src\native\tiff_shim.c /I \path\to\tiff-4.7.1\libtiff ^
    /link src\libs\win-x64\tiff.lib /out:tiff_shim.dll

copy tiff_shim.dll src\libs\win-x64\
```

---

## Building libjpeg-turbo

Download the source distribution:

```bash
curl -LO https://github.com/libjpeg-turbo/libjpeg-turbo/archive/refs/tags/3.1.4.1.zip
unzip 3.1.4.1.zip
cd libjpeg-turbo-3.1.4.1
```

### macOS ARM64 (native on Apple Silicon)

```bash
cmake -B build-arm64 -DBUILD_SHARED_LIBS=ON -DCMAKE_OSX_ARCHITECTURES=arm64 \
    -DENABLE_STATIC=OFF -DWITH_TURBOJPEG=ON
cmake --build build-arm64 --config Release

# Copy to project
cp build-arm64/libturbojpeg.dylib /path/to/PdfiumWrapper/src/libs/osx-arm64/
```

### macOS x64 (cross-compile on Apple Silicon)

```bash
cmake -B build-x64 -DBUILD_SHARED_LIBS=ON -DCMAKE_OSX_ARCHITECTURES=x86_64 \
    -DENABLE_STATIC=OFF -DWITH_TURBOJPEG=ON
cmake --build build-x64 --config Release

# Copy to project
cp build-x64/libturbojpeg.dylib /path/to/PdfiumWrapper/src/libs/osx-x64/
```

> **Note**: The x64 cross-compile on Apple Silicon will warn about missing NASM/YASM for x86 SIMD. The build still succeeds but without SSE2/AVX2 acceleration for the x64 binary. Install NASM (`brew install nasm`) to enable x86 SIMD optimizations.

### Linux x64 (via Docker)

```bash
docker run --rm --platform linux/amd64 -v "$(pwd):/src" -w /src ubuntu:22.04 bash -c "
    apt-get update && apt-get install -y cmake gcc g++ nasm &&
    cmake -B build-linux -DBUILD_SHARED_LIBS=ON -DENABLE_STATIC=OFF -DWITH_TURBOJPEG=ON &&
    cmake --build build-linux --config Release &&
    cp build-linux/libturbojpeg.so /src/libturbojpeg.so
"

# Copy to project
cp libturbojpeg.so /path/to/PdfiumWrapper/src/libs/linux-x64/
```

### Windows x64

From a **Developer Command Prompt for VS 2022** or **x64 Native Tools Command Prompt** (ensure NASM is on PATH):

```cmd
cmake -B build -A x64 -DENABLE_STATIC=OFF -DWITH_TURBOJPEG=ON
cmake --build build --config Release

copy build\Release\turbojpeg.dll \path\to\PdfiumWrapper\src\libs\win-x64\
```

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
  osx-x64/
    libpdfium.dylib
    libtiff.dylib
    libtiff_shim.dylib
    libturbojpeg.dylib
  linux-x64/
    libpdfium.so
    libtiff.so
    libtiff_shim.so
    libturbojpeg.so
  win-x64/
    pdfium.dll
    tiff.dll
    tiff_shim.dll
    turbojpeg.dll
```

The `.csproj` uses `Exists()` conditions, so missing binaries for a platform won't cause build errors — they'll only fail at runtime if the corresponding feature is used.

---

## Why the Shim Exists

libtiff's `TIFFSetField` is a **variadic C function** (`uint32_t tag, ...`). On ARM64, the C calling convention passes variadic arguments differently from fixed parameters (variadic args use general-purpose registers/stack, while fixed float args use SIMD registers). .NET's P/Invoke — both `DllImport` and `LibraryImport` — has no way to mark a function as variadic, so it generates non-variadic call sequences. This causes `TIFFSetField` to read garbage values from the wrong registers.

The shim provides non-variadic wrappers (`TIFFSetFieldInt`, `TIFFSetFieldDouble`) that take fixed parameters and forward to the real variadic `TIFFSetField` inside C, where the compiler handles the calling convention correctly.

All other libtiff functions (`TIFFOpen`, `TIFFWriteScanline`, `TIFFClose`, etc.) are non-variadic and work directly via `LibraryImport` without the shim.
