@echo off
REM =============================================================================
REM Build all native libraries for Windows x64
REM
REM Prerequisites:
REM   - Visual Studio 2022+ with C++ workload
REM   - CMake (bundled with VS or standalone)
REM   - (Optional) NASM for libjpeg-turbo SIMD acceleration
REM
REM Usage: Run from a standard command prompt (vcvarsall is called automatically).
REM        Adjust VSDIR below if your VS installation path differs.
REM
REM Output: All DLLs are copied to src\libs\win-x64\
REM =============================================================================

setlocal enabledelayedexpansion

set VSDIR=C:\Program Files\Microsoft Visual Studio\18\Enterprise
set PROJDIR=%~dp0..\..
set BUILDDIR=%PROJDIR%\_native_build
set OUTDIR=%PROJDIR%\src\libs\win-x64

REM --- Set up VS x64 environment ---
call "%VSDIR%\VC\Auxiliary\Build\vcvarsall.bat" x64 >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo ERROR: Could not find vcvarsall.bat at %VSDIR%
    echo        Edit VSDIR in this script to match your Visual Studio installation.
    exit /b 1
)

mkdir "%BUILDDIR%" 2>nul
mkdir "%OUTDIR%" 2>nul
cd /d "%BUILDDIR%"

REM =============================================================================
REM 1. libtiff 4.7.1 -> tiff.dll
REM =============================================================================
echo.
echo ========== Building libtiff 4.7.1 ==========
if not exist tiff-4.7.1 (
    curl -LO https://download.osgeo.org/libtiff/tiff-4.7.1.zip
    unzip -qo tiff-4.7.1.zip
)
cd /d "%BUILDDIR%\tiff-4.7.1"
cmake -B build -DBUILD_SHARED_LIBS=ON -DCMAKE_GENERATOR_PLATFORM=x64 ^
    -Dtiff-tools=OFF -Dtiff-tests=OFF -Dtiff-docs=OFF
if %ERRORLEVEL% neq 0 goto :error
cmake --build build --config Release
if %ERRORLEVEL% neq 0 goto :error
copy /Y build\libtiff\Release\tiff.dll "%OUTDIR%\"
echo [OK] tiff.dll

REM =============================================================================
REM 2. tiff_shim.dll (against libtiff from step 1)
REM =============================================================================
echo.
echo ========== Building tiff_shim ==========
cd /d "%BUILDDIR%"
cl /LD "%PROJDIR%\src\native\tiff_shim.c" ^
    /I "%BUILDDIR%\tiff-4.7.1\libtiff" ^
    /I "%BUILDDIR%\tiff-4.7.1\build\libtiff" ^
    /Fe:tiff_shim.dll ^
    /link "%BUILDDIR%\tiff-4.7.1\build\libtiff\Release\tiff.lib"
if %ERRORLEVEL% neq 0 goto :error
copy /Y tiff_shim.dll "%OUTDIR%\"
echo [OK] tiff_shim.dll

REM =============================================================================
REM 3. libjpeg-turbo 3.1.4.1 -> turbojpeg.dll
REM =============================================================================
echo.
echo ========== Building libjpeg-turbo 3.1.4.1 ==========
cd /d "%BUILDDIR%"
if not exist libjpeg-turbo-3.1.4.1 (
    curl -LO https://github.com/libjpeg-turbo/libjpeg-turbo/archive/refs/tags/3.1.4.1.zip
    unzip -qo 3.1.4.1.zip
)
cd /d "%BUILDDIR%\libjpeg-turbo-3.1.4.1"
cmake -B build -A x64 -DENABLE_STATIC=OFF -DWITH_TURBOJPEG=ON -DREQUIRE_SIMD=OFF
if %ERRORLEVEL% neq 0 goto :error
cmake --build build --config Release
if %ERRORLEVEL% neq 0 goto :error
copy /Y build\Release\turbojpeg.dll "%OUTDIR%\"
echo [OK] turbojpeg.dll

REM =============================================================================
REM 4. pdfium_png.dll (zlib-ng static -> libpng static -> shim DLL)
REM =============================================================================
echo.
echo ========== Building zlib-ng 2.2.4 (static) ==========
cd /d "%BUILDDIR%"
if not exist zlib-ng-2.2.4 (
    curl -LO https://github.com/zlib-ng/zlib-ng/archive/refs/tags/2.2.4.zip
    unzip -qo 2.2.4.zip
)
cd /d "%BUILDDIR%\zlib-ng-2.2.4"
cmake -B build -DBUILD_SHARED_LIBS=OFF -DZLIB_COMPAT=ON ^
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON -DZLIB_ENABLE_TESTS=OFF -A x64
if %ERRORLEVEL% neq 0 goto :error
cmake --build build --config Release
if %ERRORLEVEL% neq 0 goto :error
echo [OK] zlibstatic.lib

echo.
echo ========== Building libpng 1.6.56 (static) ==========
cd /d "%BUILDDIR%"
if not exist lpng1656 (
    curl -L -o lpng1656.zip "http://prdownloads.sourceforge.net/libpng/lpng1656.zip?download"
    unzip -qo lpng1656.zip
)
cd /d "%BUILDDIR%\lpng1656"
cmake -B build-static -DBUILD_SHARED_LIBS=OFF -DPNG_TESTS=OFF -DPNG_TOOLS=OFF ^
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON -A x64 ^
    -DZLIB_INCLUDE_DIR="%BUILDDIR%\zlib-ng-2.2.4\build" ^
    -DZLIB_LIBRARY="%BUILDDIR%\zlib-ng-2.2.4\build\Release\zlibstatic.lib"
if %ERRORLEVEL% neq 0 goto :error
cmake --build build-static --config Release
if %ERRORLEVEL% neq 0 goto :error
echo [OK] libpng16_static.lib

echo.
echo ========== Building pdfium_png shim ==========
cd /d "%BUILDDIR%"
cl /LD /MD /O2 "%PROJDIR%\src\native\pdfium_png.c" ^
    /I "%BUILDDIR%\lpng1656" ^
    /I "%BUILDDIR%\lpng1656\build-static" ^
    /I "%BUILDDIR%\zlib-ng-2.2.4\build" ^
    /I "%PROJDIR%\src\native" ^
    /Fe:pdfium_png.dll ^
    /link ^
    "%BUILDDIR%\lpng1656\build-static\Release\libpng16_static.lib" ^
    "%BUILDDIR%\zlib-ng-2.2.4\build\Release\zlibstatic.lib"
if %ERRORLEVEL% neq 0 goto :error
copy /Y pdfium_png.dll "%OUTDIR%\"
echo [OK] pdfium_png.dll

REM =============================================================================
echo.
echo ========== All builds complete ==========
echo Output directory: %OUTDIR%
dir "%OUTDIR%\*.dll"
exit /b 0

:error
echo.
echo BUILD FAILED with error code %ERRORLEVEL%
exit /b %ERRORLEVEL%
