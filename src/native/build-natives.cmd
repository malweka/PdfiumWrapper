@echo off
REM =============================================================================
REM Build all native libraries for Windows x64
REM
REM Prerequisites:
REM   - Visual Studio 2022+ with C++ workload
REM   - CMake (bundled with VS or standalone)
REM   - curl + tar (ship with Windows 10 1803+)
REM   - unzip on PATH (Git for Windows) for the library source archives,
REM     or PowerShell Expand-Archive as a fallback
REM   - (Optional) NASM for libjpeg-turbo SIMD acceleration
REM
REM Usage:
REM   build-natives.cmd                          Build everything (pdfium + libs)
REM   build-natives.cmd --no-pdfium              Build libs only, skip pdfium download
REM   build-natives.cmd --only pdfium            Download pdfium only
REM   build-natives.cmd --only libtiff,tiff_shim Build a subset
REM   build-natives.cmd --clean                  Wipe _native_build first, then build
REM
REM   --only tokens: pdfium, libtiff, tiff_shim, libjpeg_turbo, pdfium_png
REM   (pdfium_png transitively builds zlib-ng + libpng + the shim)
REM
REM Library versions can be overridden from the caller's environment, e.g.
REM   set PDFIUM_VERSION=6721 & build-natives.cmd --only pdfium
REM
REM Output: All DLLs are copied to src\libs\win-x64\
REM =============================================================================

setlocal enabledelayedexpansion

REM ---- Library versions (override from caller env if set) ----
if not defined LIBTIFF_VERSION       set LIBTIFF_VERSION=4.7.1
if not defined LIBJPEG_TURBO_VERSION set LIBJPEG_TURBO_VERSION=3.1.4.1
if not defined ZLIB_NG_VERSION       set ZLIB_NG_VERSION=2.2.4
if not defined LIBPNG_VERSION        set LIBPNG_VERSION=1.6.56
if not defined PDFIUM_VERSION        set PDFIUM_VERSION=latest

REM ---- Source URLs ----
set LIBTIFF_URL=https://download.osgeo.org/libtiff/tiff-%LIBTIFF_VERSION%.zip
set LIBJPEG_TURBO_URL=https://github.com/libjpeg-turbo/libjpeg-turbo/archive/refs/tags/%LIBJPEG_TURBO_VERSION%.zip
set ZLIB_NG_URL=https://github.com/zlib-ng/zlib-ng/archive/refs/tags/%ZLIB_NG_VERSION%.zip
set LIBPNG_URL=https://github.com/pnggroup/libpng/archive/refs/tags/v%LIBPNG_VERSION%.zip
set PDFIUM_BASE=https://github.com/bblanchon/pdfium-binaries/releases

set VSDIR=C:\Program Files\Microsoft Visual Studio\18\Enterprise
set PROJDIR=%~dp0..\..
set BUILDDIR=%PROJDIR%\_native_build
set OUTDIR=%PROJDIR%\src\libs\win-x64

REM ---- Parse arguments ----
set DO_PDFIUM=1
set DO_LIBTIFF=1
set DO_TIFF_SHIM=1
set DO_LIBJPEG_TURBO=1
set DO_PDFIUM_PNG=1
set ONLY=
set NOPDFIUM=0
set CLEAN=0
set "HAVE_ONLY="

REM cmd.exe splits args on commas, so "--only libtiff,tiff_shim" arrives as three
REM tokens. After --only, accumulate every following bare token into ONLY.
:parse_args
if "%~1"=="" goto :end_parse
if /I "%~1"=="--no-pdfium" (
    set NOPDFIUM=1
    shift
    goto :parse_args
)
if /I "%~1"=="--clean" (
    set CLEAN=1
    shift
    goto :parse_args
)
if /I "%~1"=="--only" (
    set HAVE_ONLY=1
    shift
    goto :parse_args
)
if defined HAVE_ONLY (
    set ONLY=!ONLY!,%~1
    shift
    goto :parse_args
)
echo WARNING: Unknown argument "%~1" (ignored)
shift
goto :parse_args
:end_parse

REM ---- Apply --only filter (comma-delimited token match) ----
if defined ONLY (
    set DO_PDFIUM=0
    set DO_LIBTIFF=0
    set DO_TIFF_SHIM=0
    set DO_LIBJPEG_TURBO=0
    set DO_PDFIUM_PNG=0
    set ONLYC=,%ONLY%,
    echo !ONLYC! | findstr /I /C:",pdfium," >nul        && set DO_PDFIUM=1
    echo !ONLYC! | findstr /I /C:",libtiff," >nul       && set DO_LIBTIFF=1
    echo !ONLYC! | findstr /I /C:",tiff_shim," >nul     && set DO_TIFF_SHIM=1
    echo !ONLYC! | findstr /I /C:",libjpeg_turbo," >nul && set DO_LIBJPEG_TURBO=1
    echo !ONLYC! | findstr /I /C:",pdfium_png," >nul    && set DO_PDFIUM_PNG=1
)
if "%NOPDFIUM%"=="1" set DO_PDFIUM=0

REM ---- Clean build tree if requested (before anything else) ----
if "%CLEAN%"=="1" (
    echo.
    echo ========== Cleaning %BUILDDIR% ==========
    rmdir /s /q "%BUILDDIR%" 2>nul
)

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

REM ---- Dispatch (each component is gated by its --only flag) ----
if "%DO_PDFIUM%"=="1" (
    call :build_pdfium
    if errorlevel 1 goto :error
)
if "%DO_LIBTIFF%"=="1" (
    call :build_libtiff
    if errorlevel 1 goto :error
)
if "%DO_TIFF_SHIM%"=="1" (
    call :build_tiff_shim
    if errorlevel 1 goto :error
)
if "%DO_LIBJPEG_TURBO%"=="1" (
    call :jpeg
    if errorlevel 1 goto :error
)
if "%DO_PDFIUM_PNG%"=="1" (
    call :build_png
    if errorlevel 1 goto :error
)

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

REM =============================================================================
REM Subroutines
REM =============================================================================

REM ---- 0. PDFium (prebuilt binary from bblanchon/pdfium-binaries) ----
:build_pdfium
echo.
echo ========== Downloading PDFium (%PDFIUM_VERSION%) ==========
cd /d "%BUILDDIR%"
if /I "%PDFIUM_VERSION%"=="latest" (
    set "PDFIUM_URL=%PDFIUM_BASE%/latest/download/pdfium-win-x64.tgz"
) else (
    set "PDFIUM_URL=%PDFIUM_BASE%/download/chromium/%PDFIUM_VERSION%/pdfium-win-x64.tgz"
)
if not exist pdfium-win-x64 (
    echo Downloading !PDFIUM_URL!
    curl -fLO "!PDFIUM_URL!"
    if errorlevel 1 exit /b 1
    mkdir pdfium-win-x64
    tar -xzf pdfium-win-x64.tgz -C pdfium-win-x64
    if errorlevel 1 exit /b 1
)
copy /Y "%BUILDDIR%\pdfium-win-x64\bin\pdfium.dll" "%OUTDIR%\"
if errorlevel 1 exit /b 1
echo [OK] pdfium.dll
exit /b 0

REM ---- 1. libtiff -> tiff.dll ----
:build_libtiff
echo.
echo ========== Building libtiff %LIBTIFF_VERSION% ==========
cd /d "%BUILDDIR%"
if not exist tiff-%LIBTIFF_VERSION% (
    curl -fLO "%LIBTIFF_URL%"
    if errorlevel 1 exit /b 1
    call :extract_zip "tiff-%LIBTIFF_VERSION%.zip" "%BUILDDIR%"
    if errorlevel 1 exit /b 1
)
cd /d "%BUILDDIR%\tiff-%LIBTIFF_VERSION%"
cmake -B build -DBUILD_SHARED_LIBS=ON -DCMAKE_GENERATOR_PLATFORM=x64 ^
    -Dtiff-tools=OFF -Dtiff-tests=OFF -Dtiff-docs=OFF
if %ERRORLEVEL% neq 0 exit /b %ERRORLEVEL%
cmake --build build --config Release
if %ERRORLEVEL% neq 0 exit /b %ERRORLEVEL%
copy /Y build\libtiff\Release\tiff.dll "%OUTDIR%\"
echo [OK] tiff.dll
exit /b 0

REM ---- 2. tiff_shim.dll (against libtiff from step 1) ----
:build_tiff_shim
echo.
echo ========== Building tiff_shim ==========
cd /d "%BUILDDIR%"
cl /LD "%PROJDIR%\src\native\tiff_shim.c" ^
    /I "%BUILDDIR%\tiff-%LIBTIFF_VERSION%\libtiff" ^
    /I "%BUILDDIR%\tiff-%LIBTIFF_VERSION%\build\libtiff" ^
    /Fe:tiff_shim.dll ^
    /link "%BUILDDIR%\tiff-%LIBTIFF_VERSION%\build\libtiff\Release\tiff.lib"
if %ERRORLEVEL% neq 0 exit /b %ERRORLEVEL%
copy /Y tiff_shim.dll "%OUTDIR%\"
echo [OK] tiff_shim.dll
exit /b 0

REM ---- 3. libjpeg-turbo -> turbojpeg.dll ----
:jpeg
echo.
echo ========== Building libjpeg-turbo %LIBJPEG_TURBO_VERSION% ==========
call :find_nasm
cd /d "%BUILDDIR%"
if not exist libjpeg-turbo-%LIBJPEG_TURBO_VERSION% (
    curl -fLO "%LIBJPEG_TURBO_URL%"
    if errorlevel 1 exit /b 1
    call :extract_zip "%LIBJPEG_TURBO_VERSION%.zip" "%BUILDDIR%"
    if errorlevel 1 exit /b 1
)
cd /d "%BUILDDIR%\libjpeg-turbo-%LIBJPEG_TURBO_VERSION%"
if defined NASM_EXE (
    echo [INFO] NASM found: %NASM_EXE%
    cmake -B build -A x64 -DENABLE_STATIC=OFF -DWITH_TURBOJPEG=ON -DREQUIRE_SIMD=OFF -DCMAKE_ASM_NASM_COMPILER="%NASM_EXE%"
) else (
    echo [INFO] NASM not found -- libjpeg-turbo will build without SIMD. Install from https://www.nasm.us/ for faster JPEG processing.
    cmake -B build -A x64 -DENABLE_STATIC=OFF -DWITH_TURBOJPEG=ON -DREQUIRE_SIMD=OFF
)
if %ERRORLEVEL% neq 0 exit /b %ERRORLEVEL%
cmake --build build --config Release
if %ERRORLEVEL% neq 0 exit /b %ERRORLEVEL%
copy /Y build\Release\turbojpeg.dll "%OUTDIR%\"
echo [OK] turbojpeg.dll
exit /b 0

REM ---- 4. pdfium_png.dll (zlib-ng static -> libpng static -> shim DLL) ----
:build_png
echo.
echo ========== Building zlib-ng %ZLIB_NG_VERSION% (static) ==========
cd /d "%BUILDDIR%"
if not exist zlib-ng-%ZLIB_NG_VERSION% (
    curl -fLO "%ZLIB_NG_URL%"
    if errorlevel 1 exit /b 1
    call :extract_zip "%ZLIB_NG_VERSION%.zip" "%BUILDDIR%"
    if errorlevel 1 exit /b 1
)
cd /d "%BUILDDIR%\zlib-ng-%ZLIB_NG_VERSION%"
cmake -B build -DBUILD_SHARED_LIBS=OFF -DZLIB_COMPAT=ON ^
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON -DZLIB_ENABLE_TESTS=OFF -A x64
if %ERRORLEVEL% neq 0 exit /b %ERRORLEVEL%
cmake --build build --config Release
if %ERRORLEVEL% neq 0 exit /b %ERRORLEVEL%
echo [OK] zlibstatic.lib

echo.
echo ========== Building libpng %LIBPNG_VERSION% (static) ==========
cd /d "%BUILDDIR%"
if not exist libpng-%LIBPNG_VERSION% (
    curl -fL -o "libpng-%LIBPNG_VERSION%.zip" "%LIBPNG_URL%"
    if errorlevel 1 exit /b 1
    call :extract_zip "libpng-%LIBPNG_VERSION%.zip" "%BUILDDIR%"
    if errorlevel 1 exit /b 1
)
cd /d "%BUILDDIR%\libpng-%LIBPNG_VERSION%"
cmake -B build-static -DBUILD_SHARED_LIBS=OFF -DPNG_TESTS=OFF -DPNG_TOOLS=OFF ^
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON -A x64 ^
    -DZLIB_INCLUDE_DIR="%BUILDDIR%\zlib-ng-%ZLIB_NG_VERSION%\build" ^
    -DZLIB_LIBRARY="%BUILDDIR%\zlib-ng-%ZLIB_NG_VERSION%\build\Release\zlibstatic.lib"
if %ERRORLEVEL% neq 0 exit /b %ERRORLEVEL%
cmake --build build-static --config Release
if %ERRORLEVEL% neq 0 exit /b %ERRORLEVEL%
echo [OK] libpng16_static.lib

echo.
echo ========== Building pdfium_png shim ==========
cd /d "%BUILDDIR%"
cl /LD /MD /O2 "%PROJDIR%\src\native\pdfium_png.c" ^
    /I "%BUILDDIR%\libpng-%LIBPNG_VERSION%" ^
    /I "%BUILDDIR%\libpng-%LIBPNG_VERSION%\build-static" ^
    /I "%BUILDDIR%\zlib-ng-%ZLIB_NG_VERSION%\build" ^
    /I "%PROJDIR%\src\native" ^
    /Fe:pdfium_png.dll ^
    /link ^
    "%BUILDDIR%\libpng-%LIBPNG_VERSION%\build-static\Release\libpng16_static.lib" ^
    "%BUILDDIR%\zlib-ng-%ZLIB_NG_VERSION%\build\Release\zlibstatic.lib"
if %ERRORLEVEL% neq 0 exit /b %ERRORLEVEL%
copy /Y pdfium_png.dll "%OUTDIR%\"
echo [OK] pdfium_png.dll
exit /b 0

REM ---- Utility: extract a .zip archive with unzip, or PowerShell fallback ----
:extract_zip
set "ZIPFILE=%~1"
set "DESTDIR=%~2"
where unzip >nul 2>&1
if %ERRORLEVEL% equ 0 (
    unzip -qo "%ZIPFILE%" -d "%DESTDIR%"
    exit /b %ERRORLEVEL%
)
echo [INFO] unzip not on PATH -- using PowerShell Expand-Archive.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -LiteralPath '%ZIPFILE%' -DestinationPath '%DESTDIR%' -Force"
exit /b %ERRORLEVEL%

REM ---- Utility: locate NASM on PATH or in the common per-user install path ----
:find_nasm
set "NASM_EXE="
for %%N in (nasm.exe nasm) do (
    for %%P in ("%%~$PATH:N") do (
        if not "%%~P"=="" set "NASM_EXE=%%~P"
    )
)
if not defined NASM_EXE if exist "%LOCALAPPDATA%\bin\NASM\nasm.exe" set "NASM_EXE=%LOCALAPPDATA%\bin\NASM\nasm.exe"
exit /b 0
