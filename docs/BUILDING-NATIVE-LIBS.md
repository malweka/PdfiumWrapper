# Building Native Libraries

PdfiumWrapper ships with native libraries for image format support:

- **libtiff** — The standard C library for reading and writing TIFF images
- **tiff_shim** — A thin C wrapper around libtiff's variadic `TIFFSetField` function, required because .NET's P/Invoke cannot correctly call variadic C functions on ARM64 (and the behavior is unreliable on x64)
- **libjpeg-turbo** — SIMD-accelerated JPEG encoding/decoding via the TurboJPEG API
- **pdfium_png** — A thin C shim around libpng that handles setjmp/longjmp internally and exposes a simple return-code API safe for .NET P/Invoke. Statically links both libpng and zlib-ng (SIMD-accelerated zlib replacement) so only one self-contained binary is shipped per platform — no system zlib dependency.

All must be compiled for each target platform and placed in the `src/libs/{rid}/` directory.

## Source Versions

| Library | Version | Source |
|---|---|---|
| libtiff | 4.7.1 | https://download.osgeo.org/libtiff/tiff-4.7.1.zip |
| libjpeg-turbo | 3.1.4.1 | https://github.com/libjpeg-turbo/libjpeg-turbo/archive/refs/tags/3.1.4.1.zip |
| zlib-ng | 2.2.4 | https://github.com/zlib-ng/zlib-ng/archive/refs/tags/2.2.4.zip |
| libpng | 1.6.56 | http://prdownloads.sourceforge.net/libpng/lpng1656.zip?download |

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

## Building pdfium_png (libpng shim)

The shim source is at `src/native/pdfium_png.c` and `src/native/pdfium_png.h`. It wraps libpng and handles the setjmp/longjmp error handling internally, BGRA↔RGBA conversion (via `png_set_bgr()`), and memory I/O. Both libpng and zlib-ng are statically linked into the shim so only one self-contained binary is shipped per platform — no runtime dependency on system zlib.

### Step 1: Build zlib-ng as a static library

zlib-ng is a SIMD-accelerated drop-in replacement for zlib (AVX2 on x86, NEON on ARM). Building with `ZLIB_COMPAT=ON` makes it API-compatible with zlib.

Download the source:

```bash
curl -LO https://github.com/zlib-ng/zlib-ng/archive/refs/tags/2.2.4.zip
unzip 2.2.4.zip
cd zlib-ng-2.2.4
```

#### macOS ARM64 (static)

```bash
cmake -B build-arm64 -DBUILD_SHARED_LIBS=OFF -DZLIB_COMPAT=ON \
    -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
    -DZLIB_ENABLE_TESTS=OFF
cmake --build build-arm64 --config Release
# output: build-arm64/libz.a
```

#### macOS x64 (static, cross-compile)

```bash
cmake -B build-x64 -DBUILD_SHARED_LIBS=OFF -DZLIB_COMPAT=ON \
    -DCMAKE_OSX_ARCHITECTURES=x86_64 -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
    -DZLIB_ENABLE_TESTS=OFF
cmake --build build-x64 --config Release
# output: build-x64/libz.a
```

#### Linux x64 (static, via Docker)

```bash
docker run --rm --platform linux/amd64 -v "$(pwd):/src" -w /src ubuntu:22.04 bash -c "
    apt-get update && apt-get install -y cmake gcc g++ &&
    cmake -B build-linux -DBUILD_SHARED_LIBS=OFF -DZLIB_COMPAT=ON \
        -DCMAKE_POSITION_INDEPENDENT_CODE=ON -DZLIB_ENABLE_TESTS=OFF &&
    cmake --build build-linux --config Release
"
# output: build-linux/libz.a
```

#### Windows x64 (static)

From a **x64 Native Tools Command Prompt**:

```cmd
cmake -B build -DBUILD_SHARED_LIBS=OFF -DZLIB_COMPAT=ON ^
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON -DZLIB_ENABLE_TESTS=OFF -A x64
cmake --build build --config Release
rem output: build\Release\zlibstatic.lib
```

### Step 2: Build libpng as a static library (against zlib-ng)

Download the source:

```bash
curl -L -o lpng1656.zip "http://prdownloads.sourceforge.net/libpng/lpng1656.zip?download"
unzip lpng1656.zip
cd lpng1656
```

Point CMake at the zlib-ng headers and static library from Step 1.

#### macOS ARM64 (static)

```bash
cmake -B build-arm64-static -DBUILD_SHARED_LIBS=OFF -DCMAKE_OSX_ARCHITECTURES=arm64 \
    -DPNG_TESTS=OFF -DPNG_TOOLS=OFF -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
    -DZLIB_INCLUDE_DIR=/path/to/zlib-ng-2.2.4 \
    -DZLIB_LIBRARY=/path/to/zlib-ng-2.2.4/build-arm64/libz.a
cmake --build build-arm64-static --config Release
# output: build-arm64-static/libpng16.a
```

#### macOS x64 (static, cross-compile)

```bash
cmake -B build-x64-static -DBUILD_SHARED_LIBS=OFF -DCMAKE_OSX_ARCHITECTURES=x86_64 \
    -DPNG_TESTS=OFF -DPNG_TOOLS=OFF -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
    -DZLIB_INCLUDE_DIR=/path/to/zlib-ng-2.2.4 \
    -DZLIB_LIBRARY=/path/to/zlib-ng-2.2.4/build-x64/libz.a
cmake --build build-x64-static --config Release
# output: build-x64-static/libpng16.a
```

#### Linux x64 (static, via Docker)

> **Note**: On Linux, use `cmake --install` to create a staging directory for zlib-ng so that libpng picks up the correct headers (including `zconf.h`). Without this, libpng's `pnglibconf.h` generation step fails because it can't find zlib-ng's `zconf.h`.

```bash
# Inside the Docker container, after building zlib-ng:
cmake --install build-linux --prefix /path/to/zlib-ng-install

# Then build libpng:
cmake -B build-static -DBUILD_SHARED_LIBS=OFF -DPNG_TESTS=OFF -DPNG_TOOLS=OFF \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
    -DZLIB_INCLUDE_DIR=/path/to/zlib-ng-install/include \
    -DZLIB_LIBRARY=/path/to/zlib-ng-install/lib/libz.a
cmake --build build-static --config Release
```

#### Windows x64 (static)

From a **x64 Native Tools Command Prompt**:

```cmd
cmake -B build-static -DBUILD_SHARED_LIBS=OFF -DPNG_TESTS=OFF -DPNG_TOOLS=OFF ^
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON -A x64 ^
    -DZLIB_INCLUDE_DIR=\path\to\zlib-ng-2.2.4 ^
    -DZLIB_LIBRARY=\path\to\zlib-ng-2.2.4\build\Release\zlibstatic.lib
cmake --build build-static --config Release
rem output: build-static\Release\libpng16_static.lib
```

### Step 3: Build the pdfium_png shim

Link against both libpng and zlib-ng static libraries. No `-lz` flag needed — zlib-ng is embedded.

#### macOS ARM64

```bash
clang -shared -o libpdfium_png.dylib \
    -arch arm64 \
    -I/path/to/lpng1656 -I/path/to/lpng1656/build-arm64-static \
    -I/path/to/zlib-ng-2.2.4 \
    src/native/pdfium_png.c \
    /path/to/lpng1656/build-arm64-static/libpng16.a \
    /path/to/zlib-ng-2.2.4/build-arm64/libz.a \
    -O2 -fPIC -fvisibility=hidden \
    -Wl,-install_name,@rpath/libpdfium_png.dylib

cp libpdfium_png.dylib src/libs/osx-arm64/
```

#### macOS x64

```bash
clang -shared -o libpdfium_png.dylib \
    -arch x86_64 \
    -I/path/to/lpng1656 -I/path/to/lpng1656/build-x64-static \
    -I/path/to/zlib-ng-2.2.4 \
    src/native/pdfium_png.c \
    /path/to/lpng1656/build-x64-static/libpng16.a \
    /path/to/zlib-ng-2.2.4/build-x64/libz.a \
    -O2 -fPIC -fvisibility=hidden \
    -Wl,-install_name,@rpath/libpdfium_png.dylib

cp libpdfium_png.dylib src/libs/osx-x64/
```

#### Linux x64 (via Docker)

```bash
docker run --rm --platform linux/amd64 \
    -v "$(pwd)/src/native:/shim" \
    -w /build ubuntu:22.04 bash -c "
    apt-get update && apt-get install -y gcc g++ cmake curl unzip &&

    # Build zlib-ng
    curl -sLO https://github.com/zlib-ng/zlib-ng/archive/refs/tags/2.2.4.zip &&
    unzip -q 2.2.4.zip && cd zlib-ng-2.2.4 &&
    cmake -B build -DBUILD_SHARED_LIBS=OFF -DZLIB_COMPAT=ON \
        -DCMAKE_POSITION_INDEPENDENT_CODE=ON -DZLIB_ENABLE_TESTS=OFF &&
    cmake --build build --config Release &&
    cmake --install build --prefix /build/zlib-ng-install &&
    cd /build &&

    # Build libpng against zlib-ng
    curl -sL -o lpng1656.zip 'http://prdownloads.sourceforge.net/libpng/lpng1656.zip?download' &&
    unzip -q lpng1656.zip && cd lpng1656 &&
    cmake -B build-static -DBUILD_SHARED_LIBS=OFF -DPNG_TESTS=OFF -DPNG_TOOLS=OFF \
        -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
        -DZLIB_INCLUDE_DIR=/build/zlib-ng-install/include \
        -DZLIB_LIBRARY=/build/zlib-ng-install/lib/libz.a &&
    cmake --build build-static --config Release &&
    cd /build &&

    # Build shim
    gcc -shared -o /shim/libpdfium_png.so \
        -I/build/lpng1656 -I/build/lpng1656/build-static \
        -I/build/zlib-ng-install/include \
        /shim/pdfium_png.c \
        /build/lpng1656/build-static/libpng16.a \
        /build/zlib-ng-install/lib/libz.a \
        -O2 -fPIC -fvisibility=hidden -lm
"

cp src/native/libpdfium_png.so src/libs/linux-x64/
```

#### Windows x64

From a **x64 Native Tools Command Prompt** (build zlib-ng and libpng static libs first using CMake):

```cmd
cl /LD src\native\pdfium_png.c /I \path\to\lpng1656 /I \path\to\lpng1656\build-static ^
    /I \path\to\zlib-ng-2.2.4 ^
    \path\to\lpng1656\build-static\Release\libpng16_static.lib ^
    \path\to\zlib-ng-2.2.4\build\Release\zlibstatic.lib ^
    /O2 /link /out:pdfium_png.dll

copy pdfium_png.dll src\libs\win-x64\
```

> **Dependencies**: The resulting binary is fully self-contained — both libpng and zlib-ng are statically embedded. The only runtime dependencies are system libraries (`libSystem.B.dylib` on macOS, `libc`/`libm` on Linux, kernel32 on Windows).

---

## Automated Build Script (Windows x64)

Instead of running each step manually, you can use the all-in-one build script at [`src/native/build_win_x64.bat`](../src/native/build_win_x64.bat). It downloads all source archives, builds every library in the correct order, and copies the resulting DLLs to `src/libs/win-x64/`.

### Prerequisites

- **Visual Studio 2022 or later** with the **"Desktop development with C++"** workload (provides MSVC, CMake, and MSBuild)
- **Git for Windows** (provides `curl` and `unzip` used by the script)
- **(Optional) NASM** — for libjpeg-turbo SIMD acceleration. Download from https://www.nasm.us/ and add to PATH. Without NASM the build still succeeds, but JPEG encoding/decoding will be slower.

### Configuration

Open `src/native/build_win_x64.bat` and verify the `VSDIR` variable near the top matches your Visual Studio installation path:

```bat
set VSDIR=C:\Program Files\Microsoft Visual Studio\18\Enterprise
```

Common values:

| Edition | Path |
|---|---|
| VS 2022 Community | `C:\Program Files\Microsoft Visual Studio\2022\Community` |
| VS 2022 Professional | `C:\Program Files\Microsoft Visual Studio\2022\Professional` |
| VS 2022 Enterprise | `C:\Program Files\Microsoft Visual Studio\2022\Enterprise` |

The script calls `vcvarsall.bat` automatically, so you do **not** need to run it from a Developer Command Prompt — a regular Command Prompt or terminal works.

### Running the script

```cmd
cd path\to\PdfiumWrapper
src\native\build_win_x64.bat
```

The script will:

1. Create a temporary `_native_build\` directory in the project root
2. Download and extract source archives (skipped if already present from a prior run)
3. Build each library in order:
   - **libtiff 4.7.1** → `tiff.dll`
   - **tiff_shim** → `tiff_shim.dll` (compiled against the libtiff from step 1)
   - **libjpeg-turbo 3.1.4.1** → `turbojpeg.dll`
   - **zlib-ng 2.2.4** → `zlibstatic.lib` (static, SIMD-accelerated)
   - **libpng 1.6.56** → `libpng16_static.lib` (static, linked against zlib-ng)
   - **pdfium_png shim** → `pdfium_png.dll` (statically embeds libpng + zlib-ng)
4. Copy all DLLs to `src\libs\win-x64\`

Each step prints `[OK]` on success. If any step fails, the script stops and prints the error code.

### Cleanup

After a successful build you can delete the `_native_build\` directory to reclaim disk space:

```cmd
rmdir /s /q _native_build
```

The downloaded sources are cached there, so keeping it around makes subsequent rebuilds faster (the script skips downloads if the source directories already exist).

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

The `.csproj` uses `Exists()` conditions, so missing binaries for a platform won't cause build errors — they'll only fail at runtime if the corresponding feature is used.

---

## Why the Shim Exists

libtiff's `TIFFSetField` is a **variadic C function** (`uint32_t tag, ...`). On ARM64, the C calling convention passes variadic arguments differently from fixed parameters (variadic args use general-purpose registers/stack, while fixed float args use SIMD registers). .NET's P/Invoke — both `DllImport` and `LibraryImport` — has no way to mark a function as variadic, so it generates non-variadic call sequences. This causes `TIFFSetField` to read garbage values from the wrong registers.

The shim provides non-variadic wrappers (`TIFFSetFieldInt`, `TIFFSetFieldDouble`) that take fixed parameters and forward to the real variadic `TIFFSetField` inside C, where the compiler handles the calling convention correctly.

All other libtiff functions (`TIFFOpen`, `TIFFWriteScanline`, `TIFFClose`, etc.) are non-variadic and work directly via `LibraryImport` without the shim.

## Why the PNG Shim Exists

libpng uses **`setjmp`/`longjmp`** for error handling — when libpng encounters an error (corrupt data, out of memory, etc.), it calls `longjmp` to unwind back to a `setjmp` point. This is fundamentally incompatible with .NET's managed stack: a `longjmp` from native code through managed frames corrupts the runtime state, leading to crashes or undefined behavior.

The `pdfium_png` shim contains the `setjmp` scope entirely within C, converts libpng errors to integer return codes, and stores error messages in thread-local storage. It handles BGRA↔RGBA pixel conversion via libpng's built-in `png_set_bgr()` transform (PNG only stores RGB/RGBA — the shim tells libpng the input is BGR-ordered so the swap happens inside libpng's write pipeline with no extra buffer copy). It also provides memory I/O functions so PNG data can be encoded to/decoded from buffers without touching the filesystem, and exposes a configurable filter strategy parameter (`filter_flags`) to let callers trade compression ratio for encoding speed.

### Why zlib-ng

The shim statically links **zlib-ng** (a SIMD-accelerated fork of zlib) instead of relying on system zlib. zlib-ng uses AVX2 on x86 and NEON on ARM for significantly faster compression — roughly 2-3x faster than vanilla zlib at the same compression level. Building with `ZLIB_COMPAT=ON` makes it a transparent drop-in for libpng (same API, same header names). Since both libpng and zlib-ng are statically embedded, the resulting `pdfium_png` binary is fully self-contained with no external dependencies beyond the C runtime.
