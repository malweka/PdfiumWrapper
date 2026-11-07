# Plan: linux-musl-x64 support (Alpine containers)

Created 2026-10-16. Adds a fifth native platform so the library runs on Alpine-based images (`mcr.microsoft.com/dotnet/runtime:10.0-alpine` and friends). In progress since 2026-11-06 on `feature/linux-musl-x64`: Phases 1-4 done; all five platforms carry PDFium 8138 / libjpeg-turbo 3.2.1. The release remains.

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

- [x] Accept `linux-musl-x64` in `normalize_target` and in the per-library `case` tables (`libtiff_name`, `tiff_shim_name`, `turbojpeg_name`, `pdfium_png_name` all match `linux-*` already; check each `case` that lists `linux-x64` explicitly: `build_libtiff`, `build_tiff_shim`, `build_libjpeg_turbo`, `build_pdfium_png`).
- [x] `pdfium_archive_url`: map `linux-musl-x64` to the archive name `linux-musl-x64` (`pdfium-linux-musl-x64.tgz`). Same `PDFIUM_VERSION` (8076) as the glibc build so the two Linux packages carry the same PDFium.
- [x] `docker_run_linux_build`: take the image as a parameter (`ubuntu:22.04` for `linux-x64`, `alpine:3.20` for `linux-musl-x64`). Alpine has no `bash` by default: run the script with `sh -e` or `apk add bash` first. `set -o pipefail` works in Alpine's `ash`; `set -euo pipefail` as a whole does too, but keep the scripts POSIX-clean where easy.
- [x] Parameterise `out_dir` and `build_dir` by RID in the four Linux build functions (`/src/src/libs/${rid}`, `build-${rid}`), and the `chown` at the end of every container step.
- [x] Alpine package lines: `apk add --no-cache build-base cmake nasm patchelf zlib-dev curl unzip` (patchelf is in Alpine's main repo). `cc` is gcc, as on Ubuntu. No `DEBIAN_FRONTEND`.
- [x] `libtiff` on musl: build zlib-ng static first (the same cmake invocation `build_pdfium_png_linux` uses, installed to `/src/_native_build/zlib-ng-install-linux-musl-x64`), then pass `-DZLIB_INCLUDE_DIR` and `-DZLIB_LIBRARY=<install>/lib/libz.a` to libtiff's cmake. Keep every other codec off as in the glibc build. Confirm the result has no `libz.so.1` in `NEEDED`.
- [x] `tiff_shim`, `libjpeg-turbo`, `pdfium_png`: same commands as the glibc build with the Alpine package line. The `patchelf --replace-needed libtiff.so.6 libtiff.so` and `--set-rpath '$ORIGIN'` steps stay.
- [x] Run `bash src/native/build-natives.sh --target linux-musl-x64 --clean` on the dev machine (Docker, `--platform linux/amd64`). Commit the five files under `src/libs/linux-musl-x64/`.
- [x] Verify each binary from inside an Alpine container (`docker run --rm -v "$PWD:/src" alpine:3.20 sh -c 'apk add --no-cache binutils file && file /src/src/libs/linux-musl-x64/*.so && for f in /src/src/libs/linux-musl-x64/*.so; do echo "== $f"; readelf -d "$f" | grep NEEDED; done'`). Expected: interpreter `/lib/ld-musl-x86_64.so.1` where present, no `libc.so.6`, no `libz.so.1`, no `GLIBC_` symbol versions (`readelf -V`).
- [x] `docs/BUILDING-NATIVE-LIBS.md`: add `linux-musl-x64` to the target list, the image table, the PDFium archive table and the per-library manual command sections (one Alpine variant per section, or a note on what differs: image, `apk` line, static zlib for libtiff). Update the "four image libraries, five binaries" and `src/libs/{rid}` wording.
- [x] `THIRD-PARTY-NOTICES.txt`: unchanged (same components, same licences), confirm nothing in the musl PDFium archive adds a notice.

### Phase 1 as built (2026-11-06)

- **Versions upgraded with it (owner's decision, 2026-11-06).** PDFium chromium/8076 -> 8138 and libjpeg-turbo 3.2.0 -> 3.2.1 in both build scripts; libtiff 4.7.2, libpng 1.6.59 and zlib-ng 2.3.3 were already the latest. All five platforms must carry the same versions, so win-x64 and linux-x64 were rebuilt too; osx-arm64 and osx-x64 were rebuilt on a Mac by the owner (commit `0431c6b`: only `libpdfium` and `libturbojpeg` changed; 404/404 on osx-arm64, osx-x64 smoke-tested under Rosetta). PDFium's public headers between 8076 and 8138 differ only by one new experimental function (`FPDF_StructElement_GetRawType`) and a removed `PDF_USE_XFA` define (XFA builds only): no struct layout the imports depend on changed.
- **Windows host.** The Linux targets were built from Git Bash on Windows. Two fixes made that work: `.gitattributes` (`*.sh text eol=lf`; with `core.autocrlf=true` the checkout had CRLF scripts, which carried `\r` into the container scripts) and `MSYS_NO_PATHCONV=1` on every `docker run` (MSYS rewrote `-w /src` into a Windows path).
- **Alpine runs bash.** The container installs `bash` and runs the same script as on Ubuntu (`BUILD_SCRIPT` in the environment), rather than keeping two script dialects.
- **Package lines.** The libtiff step does not install `zlib-dev` on Alpine, so libtiff cannot pick up a shared zlib by accident; `curl` and `unzip` are not needed in the container (sources are downloaded on the host).
- **libtiff on musl** links the static zlib-ng with `-Wl,--exclude-libs,ALL`, so it exports no zlib symbols (checked: 0).
- **Built without `--clean`**: it wipes all of `_native_build`, and every musl and new-version directory was new anyway.
- **Result.** No library needs `libc.so.6` or `libz.so.1`, none has a `GLIBC_` symbol version. `libpdfium.so` needs `libc.so`, which the musl loader maps to itself; the other four need `libc.musl-x86_64.so.1`; `libtiff_shim.so` finds `libtiff.so` through `$ORIGIN`. The rebuilt linux-x64 binaries have the same `NEEDED`, `SONAME` and `RUNPATH` as before.
- **Size.** The musl `libpdfium.so` was 17.8 MB against 8.3 MB for glibc: bblanchon's musl archive keeps its DWARF debug info (8 `.debug_*` sections; the glibc one has none). At the owner's decision (2026-11-06) `download_pdfium` strips it for musl targets (`strip_pdfium_debug_info`, `strip --strip-debug` in the build image), keeping the symbol table as the glibc build does: 10.1 MB.
- **Notices.** Not unchanged after all: PDFium 8138 bundles dragonbox (Apache-2.0 with LLVM exceptions, and Boost), HarfBuzz and Highway, and the musl archive carries musl libc's licence in place of LLVM libc's. All five added to `THIRD-PARTY-NOTICES.txt`, with the new versions and the musl libtiff's static zlib-ng.

## Phase 2: Load the right natives at run time

`src/PdfiumWrapper/NativeLibraryResolver.cs`:

- [x] `GetRuntimeIdentifier()`: on Linux return `linux-musl-{arch}` when `IsMusl()` is true. `IsMusl()`: `RuntimeInformation.RuntimeIdentifier.Contains("musl", OrdinalIgnoreCase)` or a `/lib/ld-musl-*.so.1` file exists. Compute once (static readonly).
- [x] Keep the `GetLibraryNames` Linux table as is (same file names on musl).
- [x] Keep the system fallback (`NativeLibrary.TryLoad(platformName)`) as is.
- [x] Unit test in `src/PdfiumWrapper.Tests`: the RID the resolver computes equals the RID of the natives in the test output (`runtimes/<rid>/native/libpdfium.so` exists for it). This runs on every platform and would have caught the glibc-on-Alpine case. If `GetRuntimeIdentifier` stays private, expose it through an `InternalsVisibleTo` already in place for the test project, or test through `NativeBoundaryTests`.

`src/PdfiumWrapper/PdfiumWrapper.csproj`:

- [x] Split the Linux fallback `PropertyGroup`: `NativeRid=linux-musl-x64` when `$(NETCoreSdkRuntimeIdentifier)` contains `musl`, else `linux-x64`. Library names are the same.
- [x] Add `<PackageReference Include="PdfiumWrapper.runtime.linux-musl-x64" Version="[$(Version)]" />` to the `PackRuntimeDependencies` group. Update the comment above it ("four" to "five").

`src/PdfiumWrapper.runtime/PdfiumWrapper.runtime.csproj` and `README.md`:

- [x] No project change: it packs `../libs/$(NativeRid)/**`. Update the header comment and the package README ("one package per platform" list, "all four").

Worker pool (`src/PdfiumWrapper.Processing`):

- [x] No code change expected. `LinuxAllocator` adds `MALLOC_ARENA_MAX=2` to musl workers too, which is harmless; leave it rather than branch on libc. Confirm `TrimWhenIdle` sets `s_trimUnavailable` after the first `DllNotFoundException` on Alpine and never throws into the idle timer (it is caught today; add nothing unless the Alpine test run says otherwise).

### Phase 2 as built (2026-11-06)

- `NativeLibraryResolver.GetRuntimeIdentifier()` is `internal`, computed once (`Lazy<string>`). `IsMusl()` reads the runtime's RID, then probes `/lib/ld-musl-*.so.1`, and treats an I/O or access error as glibc.
- Test: `NativeBoundaryTests.Resolver_ChoosesTheRidWhoseNativesTheBuildCopied` (the computed RID has `runtimes/<rid>/native/<pdfium>` in the test output).
- The csproj musl fallback uses `$(NETCoreSdkRuntimeIdentifier.Contains('musl'))`; on the Alpine SDK image it set `NativeRid=linux-musl-x64` with no argument.
- The runtime package README also lists the new versions (PDFium 8138, libjpeg-turbo 3.2.1) and the musl libtiff's static zlib-ng.
- Worker pool: no change. `LinuxAllocator.TrimWhenIdle` imports `malloc_trim` from `libc.so.6`, which musl does not have: the first call throws `DllNotFoundException`, which is caught and turns trimming off.
- **Results: 404/404 on win-x64, on linux-x64 (`sdk:10.0`) and on Alpine 3.24 (`sdk:10.0-alpine`, RID `linux-musl-x64`).** The Alpine run needed no font package: no test depends on system fonts. The pool tests (which start `PdfiumWrapper.Tests.Host` workers) passed there too.

## Phase 3: CI and release

`.github/workflows/pr-build.yml`:

- [x] Add `{ rid: 'linux-musl-x64', libname: 'libpdfium.so' }` to the pack matrix.
- [x] Add a test job that runs the suite on Alpine: `runs-on: ubuntu-latest` with `container: mcr.microsoft.com/dotnet/sdk:10.0-alpine`. The container needs `apk add --no-cache bash git` for the checkout action and any shell steps, plus a font package (`font-dejavu` or `ttf-dejavu`) so text rendering tests that rely on substitutes behave; measure first which tests need it. The pool tests start `PdfiumWrapper.Tests.Host` as a worker; it is a framework-dependent build, so no musl apphost concern. Expect the same 358 tests to pass; a test that is glibc-specific gets a `[SkippableFact]` or a platform guard, not a skip of the job.
- [x] Decide whether the Alpine job is required for merge (recommended: yes, it is the only automated check of the musl natives). Owner: yes (2026-11-06). Added to the `main-protection` ruleset as a required status check, `Build and Run Tests (Alpine, linux-musl-x64)`, beside `Build and Run Tests`.

`.github/workflows/release.yml`:

- [x] Add `linux-musl-x64` to the `pack-runtimes` matrix and to the `for RID in ...` nuspec check (`five` in the comment and the error text).
- [x] The push step globs `nupkg-runtime-*`; confirm it picks up the fifth package without a change.

Qualification (`src/PdfiumWrapper.Qualification`, see `plan-pool-qualification.md`):

- [x] Run `smoke` and a `burst` in `mcr.microsoft.com/dotnet/sdk:10.0-alpine` on the dev machine (same Docker setup as the linux-x64 runs). Record idle and peak worker memory: musl's allocator returns memory on its own, so the glibc numbers (94-117 MB idle with the fix) are not the baseline; whatever Alpine shows becomes the documented figure. A 30-minute soak is optional unless the burst shows growth.
- [x] Also run with a self-contained `-r linux-musl-x64` publish of the harness, which exercises the RID-specific copy path (only the musl natives in the output, apphost on musl).

### Phase 3 as built (2026-11-06)

- `pr-build.yml`: new job `build-and-test-alpine` in `mcr.microsoft.com/dotnet/sdk:10.0-alpine` (`apk add bash git`, then restore, build and test `PdfiumWrapper.Tests` in Release). No font package: the local Alpine run passed 404/404 without one. `linux-musl-x64` added to the pack matrix. **Not run on GitHub yet**: it first runs when a PR from this branch is opened.
- **Required check:** `Build and Run Tests (Alpine, linux-musl-x64)` is required on `main` by the `main-protection` ruleset since 2026-11-06 (owner's decision). A PR whose branch lacks the job cannot merge: PR #33 (the plan alone) is superseded by this branch, which contains it.
- `release.yml`: `linux-musl-x64` in the `pack-runtimes` matrix and in the nuspec check. The push step's `artifacts/*.nupkg` picks up the fifth package; the nuget.org Trusted Publishing policy (`PdfiumWrapper*`, new packages allowed) covers the new id.
- Packaging rehearsed locally at `2.1.0-local.1`: the five runtime packages pack, and the main package's nuspec depends on all five at `[2.1.0-local.1]`. A fresh net10.0 app restoring only those packages rendered a 10-page PDF to PNG, JPEG and TIFF and extracted text on bare Alpine images, both portable on `runtime:10.0-alpine` (five RIDs in `runtimes/`, musl chosen) and self-contained `-r linux-musl-x64` on `runtime-deps:10.0-alpine` (only the musl natives copied), with identical output. The same app with 2.0.1 fails on Alpine with `DllNotFoundException` (`ld-linux-x86-64.so.2` needed by the linux-x64 `libpdfium.so`).
- Qualification on Alpine 3.24, self-contained `-r linux-musl-x64` publish of the harness (apphost on musl), built from `git archive HEAD` in `sdk:10.0-alpine`, run in the same image on the dev machine (24 cores):
  - smoke: 1,100 jobs, PASS, 8 workers gone after dispose.
  - burst: 10,500 jobs in 8.7 min, PASS. 0 unexpected, 0 mismatches, 0 retries, 0 output left by failed jobs, 0 of 8 workers alive after dispose, 0 files left. Job processing p50/p95/p99 133 / 4,067 / 4,994 ms (glibc: 97-99 / 3,410-3,540 / 4,300-4,400). Workers: peak 102-113 MB, idle 60-69 MB, flat (end against first-half high -7.5% to +0.4%); coordinator idle 81-90 MB. No soak: the burst shows no growth.

## Phase 4: Docs and release notes

- [x] `README.md`: "four runtime packages" becomes five; list `linux-musl-x64`; mention Alpine in the platform list and the `dotnet publish -r linux-musl-x64` example.
- [x] `docs/TROUBLESHOOTING.md`: drop `linux-musl-x64` from the "gets no natives" example (leave `linux-arm64`). In "Linux missing dependencies", split the Alpine line: `apk add fontconfig freetype` plus a font package, and say what is already in the .NET Alpine images (`libgcc`, `zlib`). Add the loader error text a glibc binary produces on musl ("Error relocating ... symbol not found" or "Dynamic loading not supported") as a symptom pointing at a package version before 2.1.0.
- [x] `docs/HIGH-THROUGHPUT-PROCESSING.md` (Memory on Linux): add the Alpine numbers from the qualification run and a container example on `runtime:10.0-alpine` (no `ENV MALLOC_ARENA_MAX` needed; keep it harmless if present).
- [x] `AGENTS.md`: "four runtime packages" to five in Packaging and Changelog notes; `build-natives.sh --target linux-musl-x64` in the build commands; the resolver's musl detection under the P/Invoke layer notes; test count and platforms in "When Making Changes".
- [x] `CHANGELOG.md` 2.1.0 entry: new `PdfiumWrapper.runtime.linux-musl-x64` package, Alpine support, the resolver change, and that older versions fail at first native call on Alpine rather than at restore.
- [ ] Bump `Version` to 2.1.0 in `PdfiumWrapper.csproj`, `PdfiumWrapper.runtime.csproj` and the Processing pin (`PinCoreDependencyVersion`, `[2.1.0]`), then release from `release/2.1.0` with `publish_processing` ticked, since Processing pins the exact core version.

### Phase 4 as built (2026-11-06)

- Docs: `README.md` (five runtime packages, musl in the requirements and platform list, `-r linux-musl-x64`, the new versions), `docs/TROUBLESHOOTING.md` (`linux-musl-x64` dropped from "gets no natives"; the pre-2.1.0 Alpine failure with the real loader message; the "Linux missing dependencies" entry rewritten: the natives need only the C library, a font package only for non-embedded fonts; the old `apk add fontconfig freetype` advice was not needed), `docs/HIGH-THROUGHPUT-PROCESSING.md` (Alpine numbers and an Alpine `Dockerfile`), `AGENTS.md`, `CHANGELOG.md` (2.1.0, unreleased).
- `Version` is 2.1.0 in `PdfiumWrapper.csproj`, `PdfiumWrapper.runtime.csproj` and `PdfiumWrapper.Processing.csproj`; Processing's pin follows the core version (`PinCoreDependencyVersion`). The solution builds with no warning.
- **Not done: the release.** The macOS natives are in (`0431c6b`). It waits for a green PR run including the new Alpine job, and the owner's go-ahead. Then: merge, create `release/2.1.0` from `main`, run `release.yml` with `publish_processing` ticked, and date the CHANGELOG entry.

## Risks and open questions

- **Font availability on Alpine.** PDFium bundles FreeType but finds system fonts by scanning directories. A bare Alpine image has none, so PDFs with non-embedded fonts render with PDFium's internal fallback. Document the font package; decide whether the test suite needs one (likely for text-render comparisons).
- **fontconfig.** `docs/TROUBLESHOOTING.md` recommends it on Alpine, but the bblanchon Linux builds do not link it. Verify with `readelf -d` on the musl `libpdfium.so` and correct the doc if it is not needed.
- **Thread stack size.** musl's default thread stack is 128 KB against glibc's 8 MB. .NET sets its own stack size for managed threads (1.5 MB default on musl, configurable with `DOTNET_DefaultStackSize`), and PDFium renders on the calling thread, so deep PDF object trees could overflow sooner than on glibc. The existing hostile-input tests are the check; if one aborts on Alpine only, document `DOTNET_DefaultStackSize` rather than change the library.
- **`Process.Start` of workers on musl**: nothing musl-specific is expected, but the pool tests are the proof.
- **Docker on the dev machine**: the Alpine build runs under `--platform linux/amd64` emulation on Apple silicon, as the Ubuntu one does today. Slow but known to work.
