# Plan: build-natives.sh (macOS + Linux)

A single shell script that:

1. Downloads the configured PDFium binary from `bblanchon/pdfium-binaries` and installs it.
2. Downloads, builds, and installs `libtiff`, `tiff_shim`, `libjpeg-turbo`, `zlib-ng` (static), `libpng` (static), and `pdfium_png` (shim).
3. Targets `osx-arm64`, `osx-x64`, and `linux-x64` from a single entrypoint, selectable per-run.
4. Copies every artifact to `src/libs/{rid}/` with the names the .NET resolver expects (`libpdfium.dylib`/`.so`, `libtiff.dylib`/`.so`, `libtiff_shim.*`, `libturbojpeg.*`, `libpdfium_png.*`).

Authoritative reference for build flags and gotchas: `docs/BUILDING-NATIVE-LIBS.md`.

---

## Script location and shape

- Path: `src/native/build-natives.sh` (sibling to `build_win_x64.bat`).
- Shebang: `#!/usr/bin/env bash`, `set -euo pipefail`.
- All paths derived from `$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)` so the script works from any cwd.

### Configurable parameters (header block)

```bash
# Library versions (override via env or CLI flag)
: "${LIBTIFF_VERSION:=4.7.1}"
: "${LIBJPEG_TURBO_VERSION:=3.1.4.1}"
: "${ZLIB_NG_VERSION:=2.2.4}"
: "${LIBPNG_VERSION:=1.6.56}"
: "${PDFIUM_VERSION:=latest}"   # 'latest' or a chromium tag like 'chromium/7204'

# Source URLs (templated; rarely overridden)
LIBTIFF_URL="https://download.osgeo.org/libtiff/tiff-${LIBTIFF_VERSION}.zip"
LIBJPEG_TURBO_URL="https://github.com/libjpeg-turbo/libjpeg-turbo/archive/refs/tags/${LIBJPEG_TURBO_VERSION}.zip"
ZLIB_NG_URL="https://github.com/zlib-ng/zlib-ng/archive/refs/tags/${ZLIB_NG_VERSION}.zip"
LIBPNG_URL="https://github.com/pnggroup/libpng/archive/refs/tags/v${LIBPNG_VERSION}.zip"
PDFIUM_BASE="https://github.com/bblanchon/pdfium-binaries/releases"
```

### CLI surface

```
Usage: build-natives.sh [--target <rid>] [--only <lib>[,<lib>...]] [--clean] [--no-pdfium]
                        [--libtiff-version X] [--pdfium-version Y] ...
  --target      osx-arm64 | osx-x64 | linux-x64 | host (default: host)
  --only        pdfium,libtiff,tiff_shim,libjpeg_turbo,pdfium_png (default: all)
  --clean       wipe _native_build before starting
  --no-pdfium   skip pdfium download step
```

`host` resolves to `osx-arm64` / `osx-x64` / `linux-x64` from `uname -sm`. Linux builds always run via Docker (matches what the docs already prescribe — clean, reproducible, no host toolchain dependency).

### Layout

```
_native_build/                      # all sources + intermediates, gitignored
  tiff-${LIBTIFF_VERSION}/
  libjpeg-turbo-${LIBJPEG_TURBO_VERSION}/
  zlib-ng-${ZLIB_NG_VERSION}/
  libpng-${LIBPNG_VERSION}/         # NOTE: GitHub tag extracts as libpng-X.Y.Z, NOT lpng1656
  pdfium-${rid}/                    # extracted bblanchon archive
src/libs/{rid}/                     # final output, checked in
```

---

## Nuances to bake into the script (from BUILDING-NATIVE-LIBS.md)

These are easy to miss; the script must encode them as defaults, not as TODOs.

1. **libpng source layout changes** — switching from SourceForge `lpng1656.zip` to the GitHub tag archive means the extracted directory is `libpng-1.6.56/`, not `lpng1656/`. The docs and the Windows .bat currently assume the old name; the new script uses the new name and the docs should be updated to match.
2. **macOS cross-compile flags** — x64 build on Apple Silicon needs `-DCMAKE_OSX_ARCHITECTURES=x86_64` for every CMake project, and `tiff_shim` needs `-target x86_64-apple-macos10.15` on the `cc` line. Without it the shim builds for arm64 and link-fails at runtime.
3. **NASM for x64 SIMD** — libjpeg-turbo x64 builds warn (not fail) without NASM. Script should detect (`command -v nasm`) and print a clear "SIMD disabled, install via `brew install nasm`" notice, not silently miss it.
4. **zlib-ng install step on Linux** — libpng's `pnglibconf.h` generation reads `zconf.h` from the install prefix, so the Docker step must `cmake --install build --prefix /build/zlib-ng-install` before building libpng. On macOS this works without install because the source tree itself has `zconf.h.in` patched in place by the build, but the script should use the same install-prefix flow on macOS too for symmetry — it's free.
5. **pdfium-binaries archive layout** (verified by tar -tzf):
   - mac/linux: `lib/libpdfium.dylib` / `lib/libpdfium.so` → copy to `src/libs/{rid}/`.
   - The archive also ships `include/` and `LICENSE`; ignore everything except the lib.
6. **`@rpath` install_name on macOS** — pdfium_png is built with `-Wl,-install_name,@rpath/libpdfium_png.dylib`. libpdfium.dylib from bblanchon already has the right install_name; libtiff and libturbojpeg built locally need verification (`otool -D`) — if they install as absolute paths to `_native_build/`, .NET-side loading from `src/libs/osx-*/` will fail. Script should run `install_name_tool -id @rpath/<name>` on each dylib after build as a belt-and-suspenders step.
7. **Linux Docker reproducibility** — always pass `--platform linux/amd64` so the build is deterministic regardless of host arch (matters on Apple Silicon).
8. **GitHub redirects** — the `pdfium-binaries` "latest" download URL is `https://github.com/bblanchon/pdfium-binaries/releases/latest/download/pdfium-<archive-rid>.tgz`. For pinned versions, the URL is `.../releases/download/<tag>/pdfium-<archive-rid>.tgz` where tag looks like `chromium/7204`. Encode both. Current output RIDs map to archive RIDs as `osx-arm64 -> mac-arm64`, `osx-x64 -> mac-x64`, and `linux-x64 -> linux-x64`.
9. **Build idempotence** — every "download source" step should check `[ -d _native_build/<srcdir> ]` and skip on hit. The existing .bat does this and it makes iteration painless.
10. **No `git clean` shortcut on failure** — if a step fails, leave `_native_build/` intact so the user can inspect.
11. **macOS downloaded/built dylibs need local loadability fixups** — clear `com.apple.quarantine` and ad-hoc sign copied dylibs after download or `install_name_tool`, otherwise tests can fail with "library load disallowed by system policy".
12. **Linux shim dependency name** — libtiff's CMake build uses SONAME `libtiff.so.6`; because the package ships `libtiff.so`, patch Linux outputs so `libtiff.so` has SONAME `libtiff.so`, and `libtiff_shim.so` has a `libtiff.so` needed entry plus `$ORIGIN` runpath.

---

## Phase 1 — Skeleton & parameter plumbing

Goal: prove the CLI parses, paths resolve, and pdfium download works. No native compilation yet.

- [x] Create `src/native/build-natives.sh` with the header block above.
- [x] Implement `parse_args`, `detect_host_rid`, `ensure_dirs`, and a `log()` helper that prefixes each line with the current phase.
- [x] Implement `download_pdfium <rid>`:
  - Resolves `PDFIUM_VERSION=latest` to the redirect URL or uses the pinned tag form.
  - Curl with `-fL --retry 3` to `_native_build/pdfium-${rid}.tgz`.
  - Extract to `_native_build/pdfium-${rid}/`.
  - Copy `lib/libpdfium.{dylib,so}` to `src/libs/${rid}/`.
- [x] Add `--no-pdfium` to skip step.
- [x] Add `_native_build/` to `.gitignore` if not already there.

**Test:**
- `bash src/native/build-natives.sh --target osx-arm64 --only pdfium` → `src/libs/osx-arm64/libpdfium.dylib` exists, `file` reports `Mach-O ... arm64`, and `otool -L` shows no absolute build-host paths.
- Same for `osx-x64` (cross-target — pdfium is a download, no cross-compile needed).
- For `linux-x64`, run without Docker (download only — no compile yet).

---

## Phase 2 — libtiff + tiff_shim

- [x] `build_libtiff <rid>`:
  - macOS: native cmake build with the correct `CMAKE_OSX_ARCHITECTURES`.
  - Linux: Docker `ubuntu:22.04 --platform linux/amd64` with `build-essential cmake zlib1g-dev libjpeg-dev curl unzip` (matches docs).
  - Flags: `-DBUILD_SHARED_LIBS=ON -Dtiff-tools=OFF -Dtiff-tests=OFF -Dtiff-docs=OFF`.
  - Output: `libtiff.{dylib,so}` → `src/libs/${rid}/`.
- [x] `build_tiff_shim <rid>`:
  - macOS arm64: `cc -shared -o libtiff_shim.dylib src/native/tiff_shim.c -I .../libtiff -L src/libs/osx-arm64 -ltiff`.
  - macOS x64 (cross): add `-target x86_64-apple-macos10.15`.
  - Linux: Docker run, same toolchain image, `cc -shared -fPIC -o libtiff_shim.so ...`.
  - Output: `libtiff_shim.{dylib,so}` → `src/libs/${rid}/`.
- [x] After build, run `install_name_tool -id @rpath/libtiff.dylib` (macOS only) on both libtiff and tiff_shim.

**Test:**
- `bash src/native/build-natives.sh --target osx-arm64 --only libtiff,tiff_shim` succeeds.
- `otool -L src/libs/osx-arm64/libtiff_shim.dylib` shows `@rpath/libtiff.dylib` as the libtiff dependency, not an absolute path under `_native_build/`.
- Run `dotnet test --filter "Category=Tiff"` (or run the full suite if no category filter exists) — the tiff tests must pass on the new binaries.

---

## Phase 3 — libjpeg-turbo

- [x] `build_libjpeg_turbo <rid>`:
  - All three RIDs use the same cmake flags: `-DBUILD_SHARED_LIBS=ON -DENABLE_STATIC=OFF -DWITH_TURBOJPEG=ON`.
  - Add `-DREQUIRE_SIMD=OFF` to match the Windows .bat behavior — build without SIMD shouldn't be a hard error.
  - Output: `libturbojpeg.{dylib,so}` → `src/libs/${rid}/`.
  - macOS: reset id to `@rpath/libturbojpeg.dylib` + ad-hoc sign. Linux (Docker): `patchelf --set-soname libturbojpeg.so`.
- [x] Print a warning if `nasm` is missing on macOS x64 (SIMD disabled).

> Verified all three RIDs (2026-05-30): macOS arm64 (JPEG tests 2/2) + x64 (x86_64, NASM-warning path), linux-x64 Docker (ELF x86-64, SONAME `libturbojpeg.so`, NEEDED = libc/ld only, host-owned output).

**Test:**
- `dotnet test --filter "FullyQualifiedName~Jpeg"` passes on the new binary.
- `file src/libs/osx-x64/libturbojpeg.dylib` reports `x86_64`.

---

## Phase 4 — zlib-ng + libpng + pdfium_png shim

This is the most fragile chain — three sequential builds where each consumes the previous.

- [x] `build_zlib_ng <rid>` (static, `ZLIB_COMPAT=ON`, `CMAKE_POSITION_INDEPENDENT_CODE=ON`, `ZLIB_ENABLE_TESTS=OFF`). After build, run `cmake --install build --prefix _native_build/zlib-ng-install-${rid}/` to expose `zconf.h`.
- [x] `build_libpng <rid>` against the zlib-ng install prefix from previous step (`-DZLIB_INCLUDE_DIR=...` + `-DZLIB_LIBRARY=...`). Note: source dir is `libpng-${LIBPNG_VERSION}/`, not `lpng1656/`.
- [x] `build_pdfium_png <rid>`:
  - macOS: `clang -shared -arch <arch> -fPIC -fvisibility=hidden -O2 -Wl,-install_name,@rpath/libpdfium_png.dylib` linking both static libs.
  - Linux: Docker, same flags minus the install_name, plus `-lm`.
- [x] Output: `libpdfium_png.{dylib,so}` → `src/libs/${rid}/`.

> Implemented as a single `build_pdfium_png` unit (the three steps chain). Install-prefix flow used on all platforms (nuance #4). Verified all three RIDs (2026-05-30): osx-arm64 (deps = libSystem only; PNG 2/2 + full suite 186/186), osx-x64 (x86_64, libSystem only), linux-x64 Docker (ELF x86-64, NEEDED = libm/libc/ld only — no libz/libpng leak, host-owned). The `--only` surface and selectable-libs guard already covered `pdfium_png`; removed the now-obsolete `fail_unimplemented_libs` gate.

**Test:**
- `dotnet test --filter "FullyQualifiedName~Png"` passes on new binary.
- `otool -L src/libs/osx-arm64/libpdfium_png.dylib` shows NO libpng/libz/libpdfium_png references (everything is statically embedded — only system libs allowed).
- Linux equivalent: `ldd src/libs/linux-x64/libpdfium_png.so` → only `libc`, `libm`, `linux-vdso`.

---

## Phase 5 — Integration test

- [x] Run end-to-end on a clean checkout in a worktree:
  ```bash
  git worktree add /tmp/pdfium-test feature/easy-upgrade
  cd /tmp/pdfium-test
  rm -rf src/libs/osx-arm64/*
  bash src/native/build-natives.sh --target osx-arm64 --clean
  dotnet test src/PdfiumWrapper.Tests/PdfiumWrapper.Tests.csproj
  ```
- [x] Repeat for `osx-x64` (the test process is arm64, so we can't actually `dotnet test` the x64 binary on Apple Silicon — verify with `file` and `otool` only).
- [x] Repeat for `linux-x64` via Docker:
  ```bash
  docker run --rm --platform linux/amd64 -v "$PWD:/src" -w /src \
    mcr.microsoft.com/dotnet/sdk:8.0 \
    dotnet test src/PdfiumWrapper.Tests/PdfiumWrapper.Tests.csproj
  ```
- [x] Compare binary sizes before/after upgrade with `ls -lh src/libs/*/`. Sudden 10× jumps usually mean accidental debug build or missing strip.

> Verified Phase 5 (2026-05-31) in `/private/tmp/pdfium-phase5-codex-20260531`, an isolated copy of the current working tree with Unix target output dirs excluded. A literal `git worktree add` was not used because `src/native/build-natives.sh` and the plan files are still uncommitted/untracked on `feature/easy-upgrade`, so a fresh worktree would not contain the script under test. Results: `osx-arm64` full clean build + local full suite 186/186; `osx-x64` full clean build with `file` reporting x86_64 for all dylibs and `otool` showing `@rpath`/system-only load paths; `linux-x64` full clean Docker build + Dockerized .NET 8 suite 186/186. Binary sizes matched the checked-in upgraded artifacts closely; no suspicious 10x growth.

---

## Phase 6 — Docs + cleanup

- [x] Update `docs/BUILDING-NATIVE-LIBS.md`: add a "Quick start" section at the top pointing at `build-natives.sh` and `build_win_x64.bat`; update libpng URL and extracted-dir name.
- [x] Update `AGENTS.md` "Build" section with the new script entrypoint.
- [x] Confirm `.gitignore` covers `_native_build/` and any `*.zip`/`*.tgz` left in `src/native/`.
- [x] Update `/ai/current-state.md` with what shipped.

> Completed Phase 6 (2026-05-31): added a native-build quick start for Unix and Windows scripts, updated libpng manual references from the old `lpng1656` SourceForge archive to the GitHub `libpng-1.6.56/` layout, added the Unix script commands to `AGENTS.md`, and added `src/native/*.zip` plus `src/native/*.tgz` ignore rules. Verified there are no source archives currently left under `src/native/`.

---

## Out of scope (call out explicitly to avoid scope creep)

- Linux arm64 / musl variants — bblanchon publishes them but we don't ship them; add later if needed.
- Code signing or notarization of macOS dylibs.
- Caching the `_native_build/` tree in CI — separate concern.
- Verifying release archive checksums — worth doing eventually, but pdfium-binaries doesn't publish SHA files alongside every release; defer.
