# Current State

## Current focus

Completed Phase 6 docs and cleanup for `ai/plans/plan-native-build-script-unix.md`.

## Completed

- Read `AGENTS.md` and `README.md`.
- Checked `ai/plans/` and read `plan-native-build-script-unix.md`.
- Confirmed Phase 1 is limited to script skeleton, CLI/path plumbing, and PDFium download/copy validation.
- Recreated this state-tracking file because `ai/current-state.md` was missing.
- Created `src/native/build-natives.sh` with version defaults, CLI parsing, host RID detection, directory setup, `--clean`, `--no-pdfium`, and PDFium download/install.
- Corrected PDFium archive RID mapping for bblanchon assets: `osx-arm64 -> mac-arm64`, `osx-x64 -> mac-x64`, `linux-x64 -> linux-x64`.
- Verified PDFium-only runs for `osx-arm64`, `osx-x64`, and `linux-x64`.
- Verified macOS binaries with `file` and `otool -L`; verified Linux binary with `file`.
- Verified `--no-pdfium`, invalid target handling, and idempotent reuse of `_native_build/pdfium-${rid}`.
- Marked Phase 1 checklist items complete in `ai/plans/plan-native-build-script-unix.md`.
- Implemented `build_libtiff <rid>` for `osx-arm64`, `osx-x64`, and `linux-x64`.
- Implemented `build_tiff_shim <rid>` for `osx-arm64`, `osx-x64`, and `linux-x64`.
- Added libtiff source download/extract plumbing shared by Phase 2.
- Added macOS deployment targets: `11.0` for arm64 and `10.15` for x64.
- Added macOS quarantine removal and ad-hoc codesigning after copying or mutating dylibs.
- Disabled libtiff optional external codecs except zlib so macOS outputs do not depend on Homebrew JPEG/ZSTD/LZMA libraries.
- Patched Linux libtiff/shim with `patchelf` so the shipped `libtiff_shim.so` resolves sibling `libtiff.so` via `$ORIGIN`.
- Verified macOS arm64 and x64 libtiff/shim builds with `file`, `otool -L`, `xattr`, and `codesign`.
- Verified Linux x64 libtiff/shim builds with Docker and `ldd`.
- Ran TIFF-focused tests: 13 passed.
- Ran full local test suite: 186 passed.
- Marked Phase 2 checklist items complete in `ai/plans/plan-native-build-script-unix.md`.
- Completed Phases 3 and 4 for libjpeg-turbo and the zlib-ng/libpng/pdfium_png chain.
- Created an isolated Phase 5 validation copy at `/private/tmp/pdfium-phase5-codex-20260531` with Unix native output directories starting empty.
- Ran a clean `osx-arm64` full native build and local full test suite: 186 passed.
- Ran a clean `osx-x64` full native build and verified all dylibs report `x86_64`; `otool` shows expected `@rpath`/system-only load paths.
- Ran a clean `linux-x64` full native build via Docker and Dockerized .NET 8 full test suite: 186 passed.
- Verified Linux artifacts are x86-64 ELF binaries; `ldd` confirms `tiff_shim` resolves sibling `libtiff.so`, and `pdfium_png` only depends on `libm`, `libc`, and the loader.
- Compared `ls -lh src/libs/*/` before and after the isolated builds; sizes matched the upgraded artifacts closely with no suspicious 10x growth.
- Marked Phase 5 checklist items complete in `ai/plans/plan-native-build-script-unix.md`.
- Updated `docs/BUILDING-NATIVE-LIBS.md` with a Quick Start section for `src/native/build-natives.sh` and `src/native/build_win_x64.bat`.
- Updated `docs/BUILDING-NATIVE-LIBS.md` manual libpng references from the old `lpng1656` SourceForge archive to the GitHub `libpng-1.6.56/` archive/layout.
- Updated `AGENTS.md` Build instructions with macOS/Linux native build commands and the Windows x64 native build script.
- Added `.gitignore` rules for `src/native/*.zip` and `src/native/*.tgz`; `_native_build/` was already covered.
- Verified no `*.zip` or `*.tgz` archives are currently present directly under `src/native/`.
- Marked Phase 6 checklist items complete in `ai/plans/plan-native-build-script-unix.md`.
- Ran final local full test suite after Phase 6 docs/cleanup changes: 186 passed.

## In progress

- No implementation work is currently in progress.

## Next recommended step

Review the final diff, then decide whether to commit the native build script, docs, plan/state files, and upgraded native binaries together.

## Blockers or open questions

- Network access was approved for `bash src/native/build-natives.sh`.
- Docker access was approved for Linux x64 native builds, Dockerized .NET tests, and `ldd` checks.
- Existing git status shows unrelated deleted/untracked files; do not revert them without explicit instruction.
- `otool -L` for downloaded macOS PDFium reports install name `./libpdfium.dylib`; this is not an absolute build-host path, but it differs from the plan's earlier expectation that bblanchon uses `@rpath`.
- `install_name_tool` prints warnings when changing already-signed dylibs; the script signs again after the final changes.
- Phase 5 used an isolated `/private/tmp` copy instead of a literal git worktree because the script and plan files are still uncommitted/untracked; a worktree from `feature/easy-upgrade` would not contain the script under test.

## Recently changed files

- `ai/current-state.md`
- `ai/plans/plan-native-build-script-unix.md`
- `src/native/build-natives.sh`
- `src/libs/osx-arm64/libpdfium.dylib`
- `src/libs/osx-x64/libpdfium.dylib`
- `src/libs/linux-x64/libpdfium.so`
- `src/libs/osx-arm64/libtiff.dylib`
- `src/libs/osx-arm64/libtiff_shim.dylib`
- `src/libs/osx-x64/libtiff.dylib`
- `src/libs/osx-x64/libtiff_shim.dylib`
- `src/libs/linux-x64/libtiff.so`
- `src/libs/linux-x64/libtiff_shim.so`
- `src/libs/osx-arm64/libturbojpeg.dylib`
- `src/libs/osx-x64/libturbojpeg.dylib`
- `src/libs/linux-x64/libturbojpeg.so`
- `src/libs/osx-arm64/libpdfium_png.dylib`
- `src/libs/osx-x64/libpdfium_png.dylib`
- `src/libs/linux-x64/libpdfium_png.so`
- `docs/BUILDING-NATIVE-LIBS.md`
- `AGENTS.md`
- `.gitignore`
