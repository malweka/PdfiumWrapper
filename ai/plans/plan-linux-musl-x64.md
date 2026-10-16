# Plan: linux-musl-x64 support (Alpine containers)

Created 2026-10-16. Adds a fifth native platform so the library runs on Alpine-based images (`mcr.microsoft.com/dotnet/runtime:10.0-alpine` and friends). Not started.

## Context

- Every Linux native shipped today is a glibc build and fails to load under musl. `libpdfium.so` is the bblanchon `pdfium-linux-x64` prebuilt (glibc, `libgcc_s`, pthread). `libtiff.so`, `libtiff_shim.so`, `libturbojpeg.so` and `libpdfium_png.so` are built by `build-natives.sh` in an `ubuntu:22.04` container (`docker_run_linux_build`).
- There is no `PdfiumWrapper.runtime.linux-musl-x64` package, so `dotnet publish -r linux-musl-x64` gets no natives at all (noted in `docs/TROUBLESHOOTING.md`).
- A portable build on Alpine is worse: `NativeLibraryResolver.GetRuntimeIdentifier()` maps every Linux to `linux-x64`, finds the glibc `libpdfium.so` under `runtimes/linux-x64/native` and the load fails with a loader error at first use.
- Local builds on an Alpine host hit the same gap: the `PdfiumWrapper.csproj` platform fallback sets `NativeRid=linux-x64` for any Linux.
- The worker pool is already musl-safe. `LinuxAllocator.TrimWhenIdle` catches `DllNotFoundException` for `libc.so.6` and stops trying; `MALLOC_ARENA_MAX` is ignored by musl. `docs/HIGH-THROUGHPUT-PROCESSING.md` (Memory on Linux) already says so.
- `gcompat` on Alpine is not a supported path. It is not reliable for a C++ library of PDFium's size, and we would be debugging someone else's shim.

What the shipped `linux-x64` natives link against (from their `NEEDED` entries), and what the musl build changes:

| Library | Source | Links against (glibc build) | musl build |
|---|---|---|---|
| `libpdfium.so` | bblanchon prebuilt | glibc, `libgcc_s.so.1`, `libpthread`, `libm` | bblanchon publishes `pdfium-linux-musl-x64.tgz` in the same release. Needs `libgcc` at runtime (present in the .NET Alpine images). |
| `libtiff.so` | libtiff 4.7.2, every codec but zlib off | glibc, `libm`, system `libz.so.1` | Rebuild on Alpine. `zlib` is in the .NET Alpine runtime-deps image; link it statically anyway so the package is self-contained. |
| `libtiff_shim.so` | `src/native/tiff_shim.c` | `libtiff.so` only (`$ORIGIN` rpath) | Rebuild. |
| `libturbojpeg.so` | libjpeg-turbo 3.2.0, NASM SIMD | glibc only | Rebuild. `apk add nasm` keeps the SIMD paths. |
| `libpdfium_png.so` | `src/native/pdfium_png.c` with libpng 1.6.59 and zlib-ng linked statically | glibc, `libm` only | Rebuild. No runtime dependencies. |

## Decisions

- **RID name:** `linux-musl-x64`, the official .NET RID. No `linux-musl-arm64` in this plan (nothing ships for `linux-arm64` either); the build and packaging changes below are written so arm64 variants are additions, not rework.
- **Build image:** `alpine:3.20` for the natives. musl has no symbol versioning, so binaries built on an older Alpine load on newer ones. Pin the tag; do not use `alpine:latest`.
- **Link zlib statically into `libtiff.so`.** The glibc build depends on the system `libz.so.1`, which is fine on Debian. On Alpine it works too (the `runtime-deps` image installs `zlib`), but a static link removes the only runtime package the natives need and costs about 100 KB. Reuse the zlib-ng static build the `pdfium_png` step already produces (`ZLIB_COMPAT=ON`).
- **musl detection at run time:** `RuntimeInformation.RuntimeIdentifier` returns `linux-musl-x64` on Alpine with the portable RID graph used since .NET 8, which is enough on net10.0. Add a file probe (`/lib/ld-musl-x86_64.so.1`, or any `/lib/ld-musl-*.so.1`) as a second signal so a host with a non-portable RID still resolves. Do not read `/etc/os-release`: Wolfi and other distros use musl without being Alpine, and the `ldd` trick needs a process.
- **musl detection at build time (local dev on Alpine):** condition the Linux fallback `PropertyGroup` in `PdfiumWrapper.csproj` on `$(NETCoreSdkRuntimeIdentifier)` containing `musl`. CI and the release workflow always pass `NativeRid`, so this only affects a developer building on Alpine.
- **No change to the four existing runtime packages.** The musl package is a fifth sibling; the main package depends on all five at its exact version, the same way it depends on four today.
- **Version:** this ships as a minor release (2.1.0). It adds a package reference to the core package, which changes every consumer's restore graph, and it is the first new platform since the packaging split. No API change.

## Phase 1: Build the natives

`src/native/build-natives.sh`:

- [ ] Accept `linux-musl-x64` in `normalize_target` and in the per-library `case` tables (`libtiff_name`, `tiff_shim_name`, `turbojpeg_name`, `pdfium_png_name` all match `linux-*` already; check each `case` that lists `linux-x64` explicitly: `build_libtiff`, `build_tiff_shim`, `build_libjpeg_turbo`, `build_pdfium_png`).
- [ ] `pdfium_archive_url`: map `linux-musl-x64` to the archive name `linux-musl-x64` (`pdfium-linux-musl-x64.tgz`). Same `PDFIUM_VERSION` (8076) as the glibc build so the two Linux packages carry the same PDFium.
- [ ] `docker_run_linux_build`: take the image as a parameter (`ubuntu:22.04` for `linux-x64`, `alpine:3.20` for `linux-musl-x64`). Alpine has no `bash` by default: run the script with `sh -e` or `apk add bash` first. `set -o pipefail` works in Alpine's `ash`; `set -euo pipefail` as a whole does too, but keep the scripts POSIX-clean where easy.
- [ ] Parameterise `out_dir` and `build_dir` by RID in the four Linux build functions (`/src/src/libs/${rid}`, `build-${rid}`), and the `chown` at the end of every container step.
- [ ] Alpine package lines: `apk add --no-cache build-base cmake nasm patchelf zlib-dev curl unzip` (patchelf is in Alpine's main repo). `cc` is gcc, as on Ubuntu. No `DEBIAN_FRONTEND`.
- [ ] `libtiff` on musl: build zlib-ng static first (the same cmake invocation `build_pdfium_png_linux` uses, installed to `/src/_native_build/zlib-ng-install-linux-musl-x64`), then pass `-DZLIB_INCLUDE_DIR` and `-DZLIB_LIBRARY=<install>/lib/libz.a` to libtiff's cmake. Keep every other codec off as in the glibc build. Confirm the result has no `libz.so.1` in `NEEDED`.
- [ ] `tiff_shim`, `libjpeg-turbo`, `pdfium_png`: same commands as the glibc build with the Alpine package line. The `patchelf --replace-needed libtiff.so.6 libtiff.so` and `--set-rpath '$ORIGIN'` steps stay.
- [ ] Run `bash src/native/build-natives.sh --target linux-musl-x64 --clean` on the dev machine (Docker, `--platform linux/amd64`). Commit the five files under `src/libs/linux-musl-x64/`.
- [ ] Verify each binary from inside an Alpine container (`docker run --rm -v "$PWD:/src" alpine:3.20 sh -c 'apk add --no-cache binutils file && file /src/src/libs/linux-musl-x64/*.so && for f in /src/src/libs/linux-musl-x64/*.so; do echo "== $f"; readelf -d "$f" | grep NEEDED; done'`). Expected: interpreter `/lib/ld-musl-x86_64.so.1` where present, no `libc.so.6`, no `libz.so.1`, no `GLIBC_` symbol versions (`readelf -V`).
- [ ] `docs/BUILDING-NATIVE-LIBS.md`: add `linux-musl-x64` to the target list, the image table, the PDFium archive table and the per-library manual command sections (one Alpine variant per section, or a note on what differs: image, `apk` line, static zlib for libtiff). Update the "four image libraries, five binaries" and `src/libs/{rid}` wording.
- [ ] `THIRD-PARTY-NOTICES.txt`: unchanged (same components, same licences), confirm nothing in the musl PDFium archive adds a notice.

## Phase 2: Load the right natives at run time

`src/PdfiumWrapper/NativeLibraryResolver.cs`:

- [ ] `GetRuntimeIdentifier()`: on Linux return `linux-musl-{arch}` when `IsMusl()` is true. `IsMusl()`: `RuntimeInformation.RuntimeIdentifier.Contains("musl", OrdinalIgnoreCase)` or a `/lib/ld-musl-*.so.1` file exists. Compute once (static readonly).
- [ ] Keep the `GetLibraryNames` Linux table as is (same file names on musl).
- [ ] Keep the system fallback (`NativeLibrary.TryLoad(platformName)`) as is.
- [ ] Unit test in `src/PdfiumWrapper.Tests`: the RID the resolver computes equals the RID of the natives in the test output (`runtimes/<rid>/native/libpdfium.so` exists for it). This runs on every platform and would have caught the glibc-on-Alpine case. If `GetRuntimeIdentifier` stays private, expose it through an `InternalsVisibleTo` already in place for the test project, or test through `NativeBoundaryTests`.

`src/PdfiumWrapper/PdfiumWrapper.csproj`:

- [ ] Split the Linux fallback `PropertyGroup`: `NativeRid=linux-musl-x64` when `$(NETCoreSdkRuntimeIdentifier)` contains `musl`, else `linux-x64`. Library names are the same.
- [ ] Add `<PackageReference Include="PdfiumWrapper.runtime.linux-musl-x64" Version="[$(Version)]" />` to the `PackRuntimeDependencies` group. Update the comment above it ("four" to "five").

`src/PdfiumWrapper.runtime/PdfiumWrapper.runtime.csproj` and `README.md`:

- [ ] No project change: it packs `../libs/$(NativeRid)/**`. Update the header comment and the package README ("one package per platform" list, "all four").

Worker pool (`src/PdfiumWrapper.Processing`):

- [ ] No code change expected. `LinuxAllocator` adds `MALLOC_ARENA_MAX=2` to musl workers too, which is harmless; leave it rather than branch on libc. Confirm `TrimWhenIdle` sets `s_trimUnavailable` after the first `DllNotFoundException` on Alpine and never throws into the idle timer (it is caught today; add nothing unless the Alpine test run says otherwise).

## Phase 3: CI and release

`.github/workflows/pr-build.yml`:

- [ ] Add `{ rid: 'linux-musl-x64', libname: 'libpdfium.so' }` to the pack matrix.
- [ ] Add a test job that runs the suite on Alpine: `runs-on: ubuntu-latest` with `container: mcr.microsoft.com/dotnet/sdk:10.0-alpine`. The container needs `apk add --no-cache bash git` for the checkout action and any shell steps, plus a font package (`font-dejavu` or `ttf-dejavu`) so text rendering tests that rely on substitutes behave; measure first which tests need it. The pool tests start `PdfiumWrapper.Tests.Host` as a worker; it is a framework-dependent build, so no musl apphost concern. Expect the same 358 tests to pass; a test that is glibc-specific gets a `[SkippableFact]` or a platform guard, not a skip of the job.
- [ ] Decide whether the Alpine job is required for merge (recommended: yes, it is the only automated check of the musl natives).

`.github/workflows/release.yml`:

- [ ] Add `linux-musl-x64` to the `pack-runtimes` matrix and to the `for RID in ...` nuspec check (`five` in the comment and the error text).
- [ ] The push step globs `nupkg-runtime-*`; confirm it picks up the fifth package without a change.

Qualification (`src/PdfiumWrapper.Qualification`, see `plan-pool-qualification.md`):

- [ ] Run `smoke` and a `burst` in `mcr.microsoft.com/dotnet/sdk:10.0-alpine` on the dev machine (same Docker setup as the linux-x64 runs). Record idle and peak worker memory: musl's allocator returns memory on its own, so the glibc numbers (94-117 MB idle with the fix) are not the baseline; whatever Alpine shows becomes the documented figure. A 30-minute soak is optional unless the burst shows growth.
- [ ] Also run with a self-contained `-r linux-musl-x64` publish of the harness, which exercises the RID-specific copy path (only the musl natives in the output, apphost on musl).

## Phase 4: Docs and release notes

- [ ] `README.md`: "four runtime packages" becomes five; list `linux-musl-x64`; mention Alpine in the platform list and the `dotnet publish -r linux-musl-x64` example.
- [ ] `docs/TROUBLESHOOTING.md`: drop `linux-musl-x64` from the "gets no natives" example (leave `linux-arm64`). In "Linux missing dependencies", split the Alpine line: `apk add fontconfig freetype` plus a font package, and say what is already in the .NET Alpine images (`libgcc`, `zlib`). Add the loader error text a glibc binary produces on musl ("Error relocating ... symbol not found" or "Dynamic loading not supported") as a symptom pointing at a package version before 2.1.0.
- [ ] `docs/HIGH-THROUGHPUT-PROCESSING.md` (Memory on Linux): add the Alpine numbers from the qualification run and a container example on `runtime:10.0-alpine` (no `ENV MALLOC_ARENA_MAX` needed; keep it harmless if present).
- [ ] `AGENTS.md`: "four runtime packages" to five in Packaging and Changelog notes; `build-natives.sh --target linux-musl-x64` in the build commands; the resolver's musl detection under the P/Invoke layer notes; test count and platforms in "When Making Changes".
- [ ] `CHANGELOG.md` 2.1.0 entry: new `PdfiumWrapper.runtime.linux-musl-x64` package, Alpine support, the resolver change, and that older versions fail at first native call on Alpine rather than at restore.
- [ ] Bump `Version` to 2.1.0 in `PdfiumWrapper.csproj`, `PdfiumWrapper.runtime.csproj` and the Processing pin (`PinCoreDependencyVersion`, `[2.1.0]`), then release from `release/2.1.0` with `publish_processing` ticked, since Processing pins the exact core version.

## Risks and open questions

- **Font availability on Alpine.** PDFium bundles FreeType but finds system fonts by scanning directories. A bare Alpine image has none, so PDFs with non-embedded fonts render with PDFium's internal fallback. Document the font package; decide whether the test suite needs one (likely for text-render comparisons).
- **fontconfig.** `docs/TROUBLESHOOTING.md` recommends it on Alpine, but the bblanchon Linux builds do not link it. Verify with `readelf -d` on the musl `libpdfium.so` and correct the doc if it is not needed.
- **Thread stack size.** musl's default thread stack is 128 KB against glibc's 8 MB. .NET sets its own stack size for managed threads (1.5 MB default on musl, configurable with `DOTNET_DefaultStackSize`), and PDFium renders on the calling thread, so deep PDF object trees could overflow sooner than on glibc. The existing hostile-input tests are the check; if one aborts on Alpine only, document `DOTNET_DefaultStackSize` rather than change the library.
- **`Process.Start` of workers on musl**: nothing musl-specific is expected, but the pool tests are the proof.
- **Docker on the dev machine**: the Alpine build runs under `--platform linux/amd64` emulation on Apple silicon, as the Ubuntu one does today. Slow but known to work.
