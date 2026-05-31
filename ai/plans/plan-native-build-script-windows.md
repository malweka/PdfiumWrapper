# Plan: build-natives.cmd (Windows x64)

Refactor the existing `src/native/build_win_x64.bat` into a parameter-driven `build-natives.cmd` that also:

1. Downloads the configured PDFium binary from `bblanchon/pdfium-binaries` and installs `pdfium.dll` to `src/libs/win-x64/`.
2. Switches the libpng source URL from SourceForge (`http://prdownloads.sourceforge.net/...`) to the GitHub tag archive (HTTPS, official org).
3. Exposes every library version as a top-of-script variable so upgrades are a one-line edit.

Authoritative reference for build flags and gotchas: `docs/BUILDING-NATIVE-LIBS.md`. Use the existing `build_win_x64.bat` as the working baseline — it already encodes the correct cmake invocations and link order for libtiff → tiff_shim → libjpeg-turbo → zlib-ng → libpng → pdfium_png.

---

## Script location and shape

- Path: `src/native/build-natives.cmd` (replaces `build_win_x64.bat`; keep the old file during Phase 1 for fallback, delete in Phase 6).
- Top: `@echo off` + `setlocal enabledelayedexpansion`.
- Wrap `vcvarsall.bat` call exactly as the current .bat does — keep the same `VSDIR` discovery, since users have already configured it.

### Configurable parameters (header block)

```bat
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
```

### CLI surface

```
build-natives.cmd [--only pdfium,libtiff,tiff_shim,libjpeg_turbo,pdfium_png]
                  [--clean] [--no-pdfium]
```

Keep it minimal — Windows users typically run the whole thing. cmd.exe arg parsing is painful; one or two flags is enough. Reuse the existing `:error` label and `[OK]` echoing pattern from the current .bat.

### Layout (unchanged from current .bat)

```
_native_build\
  tiff-%LIBTIFF_VERSION%\
  libjpeg-turbo-%LIBJPEG_TURBO_VERSION%\
  zlib-ng-%ZLIB_NG_VERSION%\
  libpng-%LIBPNG_VERSION%\        REM was: lpng1656\  (GitHub tag uses libpng-X.Y.Z)
  pdfium-win-x64\
src\libs\win-x64\                 REM final output
```

---

## Nuances to bake into the script (from BUILDING-NATIVE-LIBS.md + current .bat)

1. **libpng source dir name changes.** GitHub tag archive extracts to `libpng-1.6.56\`, not `lpng1656\`. Every `cd /d` and `-DZLIB_INCLUDE_DIR=...\lpng1656` reference in the current .bat must be updated to use `%LIBPNG_VERSION%` and `libpng-%LIBPNG_VERSION%`.
2. **PDFium archive layout** (verified by `tar -tzf pdfium-win-x64.tgz`): the DLL lives at `bin\pdfium.dll`, and the import library at `lib\pdfium.dll.lib`. Only `bin\pdfium.dll` needs to go into `src\libs\win-x64\` — the .lib is irrelevant for runtime since .NET uses LoadLibrary, not link-time imports.
3. **No native `tar` on older Windows.** Windows 10 1803+ ships `tar.exe` and `curl.exe`, but the script must verify. If `tar` is missing, fall back to PowerShell: `powershell -Command "tar -xzf pdfium-win-x64.tgz"` works on every modern Windows. Recommendation: just call `tar -xzf` and let it fail with a clear message — anyone running this has VS2022 which implies a recent enough OS.
4. **`unzip` is not native on Windows.** The current .bat uses `unzip -qo` which requires Git for Windows on PATH. That dependency is already documented. Keep it; don't switch to `tar -xf` for zips because tar.exe handling of zip archives is inconsistent across Windows versions.
5. **NASM optional** — the current .bat passes `-DREQUIRE_SIMD=OFF` so the build succeeds without NASM. Keep this. Print a `[INFO]` line at the start of the libjpeg-turbo step if `where nasm >nul 2>&1` returns non-zero.
6. **`vcvarsall` x64 mode** — required for `cl.exe` and the right cmake generator platform. Existing .bat handles this; keep verbatim.
7. **`/MD` runtime for pdfium_png** — the current .bat uses `/MD` (dynamic CRT) when building the shim DLL. This matters because mixing `/MD` and `/MT` static libs across boundaries can produce hard-to-debug heap issues. zlib-ng and libpng static libs must be built with `/MD` too (the default for CMake-generated MSVC projects, so we're already fine, but worth a comment).
8. **GitHub redirect handling** — `curl -LO` follows redirects, but the `.tgz` from `https://github.com/.../releases/latest/download/pdfium-win-x64.tgz` lands as `pdfium-win-x64.tgz` in the cwd. For pinned versions, swap to `https://github.com/.../releases/download/chromium/%PDFIUM_VERSION%/pdfium-win-x64.tgz`.
9. **Idempotent re-runs** — every download step keeps the existing `if not exist <srcdir>` guard from the current .bat. Critical for fast iteration on the script itself.
10. **Don't touch `pdfium.dll.lib`** — even though it's in the bblanchon archive, we don't need it. Adding it to `src/libs/win-x64/` would just be dead weight, and might confuse anyone grepping for `.lib` files.

---

## Phase 1 — Parameterize and add PDFium download (no logic changes to library builds)

Goal: prove the parameter substitution works end-to-end and pdfium download lands the DLL. Keep the library build steps byte-identical to the current .bat for now.

- [ ] Copy `build_win_x64.bat` to `build-natives.cmd`.
- [ ] Add the parameter header block at the top.
- [ ] Add `:download_pdfium` block before the libtiff step:
  - Resolve `PDFIUM_VERSION=latest` → `%PDFIUM_BASE%/latest/download/pdfium-win-x64.tgz`; else use the pinned tag URL form.
  - `curl -fLO ...` into `_native_build\`.
  - `tar -xzf pdfium-win-x64.tgz -C pdfium-win-x64\` (mkdir first).
  - `copy /Y _native_build\pdfium-win-x64\bin\pdfium.dll %OUTDIR%\`.
  - Echo `[OK] pdfium.dll`.
- [ ] Add `--no-pdfium` arg parsing using `if "%~1"=="..."` loop — keep it shallow.
- [ ] Confirm `_native_build` is in `.gitignore`.

**Test:**
- Run `src\native\build-natives.cmd --only pdfium` on a clean checkout.
- `src\libs\win-x64\pdfium.dll` exists; `dumpbin /headers src\libs\win-x64\pdfium.dll | findstr machine` reports `x64`.
- Re-run the script — second invocation skips the download (because the source dir is already there).

---

## Phase 2 — Migrate libpng URL to GitHub

The breaking change. Must happen before any cleanup of the rest of the script.

- [ ] Change `LIBPNG_URL` to the GitHub tag archive (already in header block).
- [ ] Change every `lpng1656` reference to `libpng-%LIBPNG_VERSION%`. Specifically the:
  - `if not exist libpng-%LIBPNG_VERSION%` guard.
  - `cd /d "%BUILDDIR%\libpng-%LIBPNG_VERSION%"` lines.
  - `cmake -B build-static ... -DZLIB_INCLUDE_DIR="%BUILDDIR%\zlib-ng-%ZLIB_NG_VERSION%\build"` (already using zlib-ng version, but verify).
  - `cl /LD ... /I "%BUILDDIR%\libpng-%LIBPNG_VERSION%" /I "%BUILDDIR%\libpng-%LIBPNG_VERSION%\build-static" ...`.
  - The static lib path: `"%BUILDDIR%\libpng-%LIBPNG_VERSION%\build-static\Release\libpng16_static.lib"`.
- [ ] Verify the GitHub tag archive ships the same `CMakeLists.txt` and produces the same `libpng16_static.lib` artifact name. (Spot check: the libpng release tag is just a tarball of the source tree — there's no SourceForge-only file. Should match.)

**Test:**
- `rmdir /s /q _native_build` then `src\native\build-natives.cmd --only pdfium_png` (which transitively builds zlib-ng + libpng + shim).
- `dumpbin /exports src\libs\win-x64\pdfium_png.dll | findstr png_` shows no exported `png_*` symbols (they're all `static` to the DLL — exports are the `pdfium_png_*` shim functions only).
- `dumpbin /dependents src\libs\win-x64\pdfium_png.dll` shows no `libpng16*.dll` and no `zlib*.dll` dependencies. Only system DLLs (kernel32, vcruntime, ucrtbase).

---

## Phase 3 — Parameterize remaining libraries

- [ ] Replace every hardcoded version string in libtiff/libjpeg-turbo/zlib-ng steps with the `%..._VERSION%` variable.
- [ ] Replace inline URLs in `curl -LO` commands with the corresponding `%..._URL%` variable.
- [ ] Add `--only` parsing: comma-split into a string and use `findstr` to gate each phase, e.g. `echo %ONLY% | findstr /C:"libtiff" >nul && call :build_libtiff`. Keep the all-on default if `--only` is absent.
- [ ] Refactor each library build into a `call :label` so `--only` can drive them.

**Test:**
- `set LIBTIFF_VERSION=4.7.0` then `src\native\build-natives.cmd --only libtiff` builds the older version into `_native_build\tiff-4.7.0\`. Reset back to 4.7.1 for the rest of testing.
- `src\native\build-natives.cmd --only libtiff,tiff_shim` runs only those two phases.

---

## Phase 4 — Optional NASM detection + clean target

- [ ] Add a `where nasm >nul 2>&1` check at the top of the libjpeg-turbo phase; print `[INFO] NASM not on PATH — libjpeg-turbo will build without SIMD. Install from https://www.nasm.us/ for ~2x speedup.` if missing.
- [ ] Add `--clean` flag that runs `rmdir /s /q "%BUILDDIR%"` before anything else.

**Test:**
- `src\native\build-natives.cmd --clean --only libjpeg_turbo` rebuilds from scratch.
- Temporarily rename `nasm.exe` (if installed) to confirm the INFO line fires.

---

## Phase 5 — Integration test on Windows VM

- [ ] Spin up a Windows 11 VM with VS 2022 Community + Git for Windows. No NASM (worst case).
- [ ] Clone the repo, switch to `feature/easy-upgrade`.
- [ ] Delete `src\libs\win-x64\*.dll`.
- [ ] `src\native\build-natives.cmd`.
- [ ] `dotnet test src\PdfiumWrapper.Tests\PdfiumWrapper.Tests.csproj`. All 154+ tests must pass.
- [ ] Repeat with NASM installed; confirm libjpeg-turbo SIMD is enabled by checking build output for "SIMD support: yes".
- [ ] Test version override: `set LIBPNG_VERSION=1.6.56` (or whatever latest is) and rebuild only the png chain.

---

## Phase 6 — Cleanup + docs

- [ ] Delete `src\native\build_win_x64.bat` (replaced by `build-natives.cmd`).
- [ ] Update `docs/BUILDING-NATIVE-LIBS.md`:
  - Section "Automated Build Script (Windows x64)" → point at `build-natives.cmd`.
  - Switch the manual libpng URL example to the GitHub form, and update the extracted directory name from `lpng1656` to `libpng-1.6.56`.
- [ ] Update `AGENTS.md` "Build" section.
- [ ] Update `/ai/current-state.md`.

---

## Out of scope (call out explicitly to avoid scope creep)

- ARM64 Windows builds — bblanchon publishes `pdfium-win-arm64.tgz`, but we don't ship that RID. Add when the project adds the runtime.
- Authenticode signing of the produced DLLs.
- CI integration — separate plan; this script just needs to be runnable from CI.
- PowerShell rewrite — `cmd` matches the existing style and `vcvarsall` flow. A PS port would be cleaner code but is more change than the value justifies right now.
- Checksum verification of downloaded archives — same reasoning as the Unix plan; defer until upstream publishes SHAs consistently.
