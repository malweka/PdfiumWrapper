#!/usr/bin/env bash
set -euo pipefail

# =============================================================================
# Build/download native libraries for macOS and Linux.
#
# Builds/downloads every native dependency: PDFium (download), libtiff,
# tiff_shim, libjpeg-turbo, and the self-contained pdfium_png shim
# (zlib-ng + libpng statically linked).
# =============================================================================

# Library versions (override via env or CLI flag)
: "${LIBTIFF_VERSION:=4.7.1}"
: "${LIBJPEG_TURBO_VERSION:=3.1.4.1}"
: "${ZLIB_NG_VERSION:=2.2.4}"
: "${LIBPNG_VERSION:=1.6.56}"
: "${PDFIUM_VERSION:=latest}"

# Source URLs (templated; rarely overridden)
LIBTIFF_URL="https://download.osgeo.org/libtiff/tiff-${LIBTIFF_VERSION}.zip"
LIBJPEG_TURBO_URL="https://github.com/libjpeg-turbo/libjpeg-turbo/archive/refs/tags/${LIBJPEG_TURBO_VERSION}.zip"
ZLIB_NG_URL="https://github.com/zlib-ng/zlib-ng/archive/refs/tags/${ZLIB_NG_VERSION}.zip"
LIBPNG_URL="https://github.com/pnggroup/libpng/archive/refs/tags/v${LIBPNG_VERSION}.zip"
PDFIUM_BASE="https://github.com/bblanchon/pdfium-binaries/releases"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/../.." && pwd)"
BUILD_DIR="${ROOT_DIR}/_native_build"
LIBS_DIR="${ROOT_DIR}/src/libs"

TARGET="host"
ONLY="all"
CLEAN=0
NO_PDFIUM=0
PHASE="init"

usage() {
    cat <<'USAGE'
Usage: build-natives.sh [--target <rid>] [--only <lib>[,<lib>...]] [--clean] [--no-pdfium]
                        [--libtiff-version X] [--libjpeg-turbo-version X]
                        [--zlib-ng-version X] [--libpng-version X] [--pdfium-version Y]

  --target      osx-arm64 | osx-x64 | linux-x64 | host (default: host)
  --only        pdfium,libtiff,tiff_shim,libjpeg_turbo,pdfium_png (default: all)
  --clean       wipe _native_build before starting
  --no-pdfium   skip pdfium download step
  -h, --help    show this help
USAGE
}

log() {
    local message="$*"
    while IFS= read -r line; do
        printf '[%s] %s\n' "${PHASE}" "${line}"
    done <<< "${message}"
}

require_command() {
    local command_name="$1"

    if ! command -v "${command_name}" >/dev/null 2>&1; then
        die "required command not found: ${command_name}"
    fi
}

codesign_macos_library() {
    local path="$1"

    [[ -f "${path}" ]] || return

    if command -v xattr >/dev/null 2>&1; then
        xattr -d com.apple.quarantine "${path}" >/dev/null 2>&1 || true
    fi

    require_command codesign
    codesign --force --sign - "${path}" >/dev/null
}

die() {
    log "ERROR: $*"
    exit 1
}

parse_args() {
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --target)
                [[ $# -ge 2 ]] || die "--target requires a value"
                TARGET="$2"
                shift 2
                ;;
            --only)
                [[ $# -ge 2 ]] || die "--only requires a value"
                ONLY="$2"
                shift 2
                ;;
            --clean)
                CLEAN=1
                shift
                ;;
            --no-pdfium)
                NO_PDFIUM=1
                shift
                ;;
            --libtiff-version)
                [[ $# -ge 2 ]] || die "--libtiff-version requires a value"
                LIBTIFF_VERSION="$2"
                LIBTIFF_URL="https://download.osgeo.org/libtiff/tiff-${LIBTIFF_VERSION}.zip"
                shift 2
                ;;
            --libjpeg-turbo-version)
                [[ $# -ge 2 ]] || die "--libjpeg-turbo-version requires a value"
                LIBJPEG_TURBO_VERSION="$2"
                LIBJPEG_TURBO_URL="https://github.com/libjpeg-turbo/libjpeg-turbo/archive/refs/tags/${LIBJPEG_TURBO_VERSION}.zip"
                shift 2
                ;;
            --zlib-ng-version)
                [[ $# -ge 2 ]] || die "--zlib-ng-version requires a value"
                ZLIB_NG_VERSION="$2"
                ZLIB_NG_URL="https://github.com/zlib-ng/zlib-ng/archive/refs/tags/${ZLIB_NG_VERSION}.zip"
                shift 2
                ;;
            --libpng-version)
                [[ $# -ge 2 ]] || die "--libpng-version requires a value"
                LIBPNG_VERSION="$2"
                LIBPNG_URL="https://github.com/pnggroup/libpng/archive/refs/tags/v${LIBPNG_VERSION}.zip"
                shift 2
                ;;
            --pdfium-version)
                [[ $# -ge 2 ]] || die "--pdfium-version requires a value"
                PDFIUM_VERSION="$2"
                shift 2
                ;;
            -h|--help)
                usage
                exit 0
                ;;
            *)
                die "unknown argument: $1"
                ;;
        esac
    done
}

detect_host_rid() {
    local kernel
    local machine

    kernel="$(uname -s)"
    machine="$(uname -m)"

    case "${kernel}:${machine}" in
        Darwin:arm64) printf 'osx-arm64' ;;
        Darwin:x86_64) printf 'osx-x64' ;;
        Linux:x86_64|Linux:amd64) printf 'linux-x64' ;;
        *) die "unsupported host platform: ${kernel} ${machine}" ;;
    esac
}

normalize_target() {
    if [[ "${TARGET}" == "host" ]]; then
        TARGET="$(detect_host_rid)"
    fi

    case "${TARGET}" in
        osx-arm64|osx-x64|linux-x64) ;;
        *) die "unsupported target: ${TARGET}" ;;
    esac
}

ensure_dirs() {
    if [[ "${CLEAN}" -eq 1 ]]; then
        log "Removing ${BUILD_DIR}"
        rm -rf "${BUILD_DIR}"
    fi

    mkdir -p "${BUILD_DIR}" "${LIBS_DIR}/${TARGET}"
}

rid_out_dir() {
    local rid="$1"
    printf '%s/%s\n' "${LIBS_DIR}" "${rid}"
}

rid_libtiff_name() {
    local rid="$1"

    case "${rid}" in
        osx-*) printf 'libtiff.dylib' ;;
        linux-*) printf 'libtiff.so' ;;
        *) die "unsupported libtiff target: ${rid}" ;;
    esac
}

rid_tiff_shim_name() {
    local rid="$1"

    case "${rid}" in
        osx-*) printf 'libtiff_shim.dylib' ;;
        linux-*) printf 'libtiff_shim.so' ;;
        *) die "unsupported tiff_shim target: ${rid}" ;;
    esac
}

rid_libturbojpeg_name() {
    local rid="$1"

    case "${rid}" in
        osx-*) printf 'libturbojpeg.dylib' ;;
        linux-*) printf 'libturbojpeg.so' ;;
        *) die "unsupported libjpeg-turbo target: ${rid}" ;;
    esac
}

rid_pdfium_png_name() {
    local rid="$1"

    case "${rid}" in
        osx-*) printf 'libpdfium_png.dylib' ;;
        linux-*) printf 'libpdfium_png.so' ;;
        *) die "unsupported pdfium_png target: ${rid}" ;;
    esac
}

macos_arch_for_rid() {
    local rid="$1"

    case "${rid}" in
        osx-arm64) printf 'arm64' ;;
        osx-x64) printf 'x86_64' ;;
        *) die "not a macOS target: ${rid}" ;;
    esac
}

macos_deployment_target_for_rid() {
    local rid="$1"

    case "${rid}" in
        osx-arm64) printf '11.0' ;;
        osx-x64) printf '10.15' ;;
        *) die "not a macOS target: ${rid}" ;;
    esac
}

download_zip_source() {
    local name="$1"
    local url="$2"
    local source_dir="$3"
    local archive="$4"

    PHASE="${name}"

    if [[ -d "${source_dir}" ]]; then
        log "Using existing source at ${source_dir}"
        return
    fi

    require_command curl
    require_command unzip

    if [[ ! -f "${archive}" ]]; then
        log "Downloading ${url}"
        curl -fL --retry 3 -o "${archive}" "${url}"
    else
        log "Using existing archive at ${archive}"
    fi

    log "Extracting ${archive}"
    unzip -qo "${archive}" -d "${BUILD_DIR}"

    [[ -d "${source_dir}" ]] || die "expected source directory not found after extract: ${source_dir}"
}

ensure_libtiff_source() {
    download_zip_source \
        "libtiff" \
        "${LIBTIFF_URL}" \
        "${BUILD_DIR}/tiff-${LIBTIFF_VERSION}" \
        "${BUILD_DIR}/tiff-${LIBTIFF_VERSION}.zip"
}

docker_run_linux_build() {
    local script="$1"

    require_command docker

    docker run --rm --platform linux/amd64 \
        -v "${ROOT_DIR}:/src" \
        -w /src \
        -e "HOST_UID=$(id -u)" \
        -e "HOST_GID=$(id -g)" \
        ubuntu:22.04 \
        bash -lc "${script}"
}

selected_libs() {
    if [[ "${ONLY}" == "all" ]]; then
        printf '%s\n' pdfium libtiff tiff_shim libjpeg_turbo pdfium_png
        return
    fi

    local value
    local -a libs=()
    IFS=',' read -r -a libs <<< "${ONLY}"

    for value in "${libs[@]}"; do
        case "${value}" in
            pdfium|libtiff|tiff_shim|libjpeg_turbo|pdfium_png)
                printf '%s\n' "${value}"
                ;;
            "")
                die "--only contains an empty library name"
                ;;
            *)
                die "unsupported --only library: ${value}"
                ;;
        esac
    done
}

is_selected() {
    local needle="$1"
    local lib

    while IFS= read -r lib; do
        [[ "${lib}" == "${needle}" ]] && return 0
    done < <(selected_libs)

    return 1
}

pdfium_archive_url() {
    local rid="$1"
    local archive_rid

    case "${rid}" in
        osx-arm64) archive_rid="mac-arm64" ;;
        osx-x64) archive_rid="mac-x64" ;;
        linux-x64) archive_rid="linux-x64" ;;
        *) die "unsupported PDFium target: ${rid}" ;;
    esac

    if [[ "${PDFIUM_VERSION}" == "latest" ]]; then
        printf '%s/latest/download/pdfium-%s.tgz\n' "${PDFIUM_BASE}" "${archive_rid}"
    else
        printf '%s/download/%s/pdfium-%s.tgz\n' "${PDFIUM_BASE}" "${PDFIUM_VERSION}" "${archive_rid}"
    fi
}

pdfium_lib_name() {
    local rid="$1"

    case "${rid}" in
        osx-*) printf 'libpdfium.dylib' ;;
        linux-*) printf 'libpdfium.so' ;;
        *) die "unsupported PDFium target: ${rid}" ;;
    esac
}

download_pdfium() {
    local rid="$1"
    local url
    local archive
    local extract_dir
    local lib_name
    local source_lib
    local destination_lib

    PHASE="pdfium"
    url="$(pdfium_archive_url "${rid}")"
    archive="${BUILD_DIR}/pdfium-${rid}.tgz"
    extract_dir="${BUILD_DIR}/pdfium-${rid}"
    lib_name="$(pdfium_lib_name "${rid}")"
    source_lib="${extract_dir}/lib/${lib_name}"
    destination_lib="${LIBS_DIR}/${rid}/${lib_name}"

    if [[ -d "${extract_dir}" ]]; then
        log "Using existing extracted archive at ${extract_dir}"
    else
        log "Downloading ${url}"
        curl -fL --retry 3 -o "${archive}" "${url}"

        log "Extracting ${archive}"
        mkdir -p "${extract_dir}"
        tar -xzf "${archive}" -C "${extract_dir}"
    fi

    [[ -f "${source_lib}" ]] || die "expected PDFium library not found: ${source_lib}"

    mkdir -p "$(dirname "${destination_lib}")"
    cp "${source_lib}" "${destination_lib}"
    if [[ "${rid}" == osx-* ]]; then
        codesign_macos_library "${destination_lib}"
    fi
    log "Installed ${destination_lib}"
}

fix_macos_libtiff_install_names() {
    local rid="$1"
    local out_dir
    local libtiff_path
    local shim_path

    [[ "${rid}" == osx-* ]] || return

    require_command install_name_tool

    out_dir="$(rid_out_dir "${rid}")"
    libtiff_path="${out_dir}/libtiff.dylib"
    shim_path="${out_dir}/libtiff_shim.dylib"

    if [[ -f "${libtiff_path}" ]]; then
        install_name_tool -id "@rpath/libtiff.dylib" "${libtiff_path}"
        codesign_macos_library "${libtiff_path}"
    fi

    if [[ -f "${shim_path}" ]]; then
        install_name_tool -id "@rpath/libtiff_shim.dylib" "${shim_path}"
        install_name_tool -change "${out_dir}/libtiff.dylib" "@rpath/libtiff.dylib" "${shim_path}"
        install_name_tool -change "libtiff.dylib" "@rpath/libtiff.dylib" "${shim_path}"
        install_name_tool -change "@rpath/libtiff.6.dylib" "@rpath/libtiff.dylib" "${shim_path}"
        install_name_tool -change "libtiff.6.dylib" "@rpath/libtiff.dylib" "${shim_path}"
        codesign_macos_library "${shim_path}"
    fi
}

libtiff_cmake_flags() {
    cat <<'FLAGS'
-DBUILD_SHARED_LIBS=ON
-Dtiff-tools=OFF
-Dtiff-tests=OFF
-Dtiff-docs=OFF
-Djpeg=OFF
-Djbig=OFF
-Dlerc=OFF
-Dlzma=OFF
-Dwebp=OFF
-Dzstd=OFF
-Dlibdeflate=OFF
FLAGS
}

build_libtiff_macos() {
    local rid="$1"
    local arch
    local deployment_target
    local source_dir
    local build_dir
    local out_dir
    local source_lib
    local destination_lib
    local -a cmake_args=()
    local flag

    PHASE="libtiff"
    require_command cmake
    require_command install_name_tool

    arch="$(macos_arch_for_rid "${rid}")"
    deployment_target="$(macos_deployment_target_for_rid "${rid}")"
    source_dir="${BUILD_DIR}/tiff-${LIBTIFF_VERSION}"
    build_dir="${source_dir}/build-${rid}"
    out_dir="$(rid_out_dir "${rid}")"
    source_lib="${build_dir}/libtiff/libtiff.dylib"
    destination_lib="${out_dir}/$(rid_libtiff_name "${rid}")"

    while IFS= read -r flag; do
        cmake_args+=("${flag}")
    done < <(libtiff_cmake_flags)

    cmake_args+=("-DCMAKE_BUILD_TYPE=Release")
    cmake_args+=("-DCMAKE_OSX_ARCHITECTURES=${arch}")
    cmake_args+=("-DCMAKE_OSX_DEPLOYMENT_TARGET=${deployment_target}")

    log "Configuring libtiff ${LIBTIFF_VERSION} for ${rid}"
    cmake -S "${source_dir}" -B "${build_dir}" "${cmake_args[@]}"

    log "Building libtiff ${LIBTIFF_VERSION} for ${rid}"
    cmake --build "${build_dir}" --config Release

    [[ -f "${source_lib}" ]] || die "expected libtiff output not found: ${source_lib}"

    mkdir -p "${out_dir}"
    cp -f "${source_lib}" "${destination_lib}"
    fix_macos_libtiff_install_names "${rid}"
    log "Installed ${destination_lib}"
}

build_libtiff_linux() {
    local source_dir
    local build_dir
    local out_dir
    local source_lib
    local destination_lib
    local docker_script

    PHASE="libtiff"

    source_dir="/src/_native_build/tiff-${LIBTIFF_VERSION}"
    build_dir="${source_dir}/build-linux-x64"
    out_dir="/src/src/libs/linux-x64"
    source_lib="${build_dir}/libtiff/libtiff.so"
    destination_lib="${out_dir}/libtiff.so"

    docker_script="
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y build-essential cmake zlib1g-dev libjpeg-dev curl unzip patchelf
cmake -S '${source_dir}' -B '${build_dir}' \
    -DCMAKE_BUILD_TYPE=Release \
    -DBUILD_SHARED_LIBS=ON \
    -Dtiff-tools=OFF \
    -Dtiff-tests=OFF \
    -Dtiff-docs=OFF \
    -Djpeg=OFF \
    -Djbig=OFF \
    -Dlerc=OFF \
    -Dlzma=OFF \
    -Dwebp=OFF \
    -Dzstd=OFF \
    -Dlibdeflate=OFF
cmake --build '${build_dir}' --config Release
test -f '${source_lib}'
mkdir -p '${out_dir}'
cp -f '${source_lib}' '${destination_lib}'
patchelf --set-soname libtiff.so '${destination_lib}'
chown -R \"\${HOST_UID}:\${HOST_GID}\" /src/_native_build /src/src/libs/linux-x64
"

    log "Building libtiff ${LIBTIFF_VERSION} for linux-x64 in Docker"
    docker_run_linux_build "${docker_script}"
    log "Installed ${ROOT_DIR}/src/libs/linux-x64/libtiff.so"
}

build_libtiff() {
    local rid="$1"

    ensure_libtiff_source

    case "${rid}" in
        osx-*) build_libtiff_macos "${rid}" ;;
        linux-x64) build_libtiff_linux ;;
        *) die "unsupported libtiff target: ${rid}" ;;
    esac
}

build_tiff_shim_macos() {
    local rid="$1"
    local arch
    local deployment_target
    local source_dir
    local build_dir
    local out_dir
    local libtiff_path
    local destination_lib
    local -a cc_args=()

    PHASE="tiff_shim"
    require_command cc
    require_command install_name_tool

    arch="$(macos_arch_for_rid "${rid}")"
    deployment_target="$(macos_deployment_target_for_rid "${rid}")"
    source_dir="${BUILD_DIR}/tiff-${LIBTIFF_VERSION}"
    build_dir="${source_dir}/build-${rid}"
    out_dir="$(rid_out_dir "${rid}")"
    libtiff_path="${out_dir}/libtiff.dylib"
    destination_lib="${out_dir}/$(rid_tiff_shim_name "${rid}")"

    [[ -f "${libtiff_path}" ]] || die "libtiff must be built before tiff_shim: ${libtiff_path}"
    [[ -f "${build_dir}/libtiff/tiffconf.h" ]] || die "libtiff build headers not found: ${build_dir}/libtiff/tiffconf.h"

    fix_macos_libtiff_install_names "${rid}"

    cc_args=(
        -shared
        -fPIC
        "-arch" "${arch}"
        "-mmacosx-version-min=${deployment_target}"
        "-Wl,-install_name,@rpath/libtiff_shim.dylib"
        -o "${destination_lib}"
        "${SCRIPT_DIR}/tiff_shim.c"
        -I "${source_dir}/libtiff"
        -I "${build_dir}/libtiff"
        -L "${out_dir}"
        -ltiff
    )

    if [[ "${rid}" == "osx-x64" ]]; then
        cc_args=(-target x86_64-apple-macos10.15 "${cc_args[@]}")
    fi

    log "Building tiff_shim for ${rid}"
    cc "${cc_args[@]}"

    fix_macos_libtiff_install_names "${rid}"
    log "Installed ${destination_lib}"
}

build_tiff_shim_linux() {
    local source_dir
    local build_dir
    local out_dir
    local libtiff_path
    local destination_lib
    local docker_script

    PHASE="tiff_shim"

    source_dir="/src/_native_build/tiff-${LIBTIFF_VERSION}"
    build_dir="${source_dir}/build-linux-x64"
    out_dir="/src/src/libs/linux-x64"
    libtiff_path="${out_dir}/libtiff.so"
    destination_lib="${out_dir}/libtiff_shim.so"

    docker_script="
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y build-essential zlib1g-dev libjpeg-dev patchelf
test -f '${libtiff_path}'
test -f '${build_dir}/libtiff/tiffconf.h'
cc -shared -fPIC -o '${destination_lib}' /src/src/native/tiff_shim.c \
    -I '${source_dir}/libtiff' \
    -I '${build_dir}/libtiff' \
    -L '${out_dir}' \
    -ltiff
patchelf --replace-needed libtiff.so.6 libtiff.so '${destination_lib}'
patchelf --set-rpath '\$ORIGIN' '${destination_lib}'
chown -R \"\${HOST_UID}:\${HOST_GID}\" /src/_native_build /src/src/libs/linux-x64
"

    log "Building tiff_shim for linux-x64 in Docker"
    docker_run_linux_build "${docker_script}"
    log "Installed ${ROOT_DIR}/src/libs/linux-x64/libtiff_shim.so"
}

build_tiff_shim() {
    local rid="$1"

    ensure_libtiff_source

    case "${rid}" in
        osx-*) build_tiff_shim_macos "${rid}" ;;
        linux-x64) build_tiff_shim_linux ;;
        *) die "unsupported tiff_shim target: ${rid}" ;;
    esac
}

ensure_libjpeg_turbo_source() {
    download_zip_source \
        "libjpeg_turbo" \
        "${LIBJPEG_TURBO_URL}" \
        "${BUILD_DIR}/libjpeg-turbo-${LIBJPEG_TURBO_VERSION}" \
        "${BUILD_DIR}/libjpeg-turbo-${LIBJPEG_TURBO_VERSION}.zip"
}

ensure_zlib_ng_source() {
    download_zip_source \
        "zlib_ng" \
        "${ZLIB_NG_URL}" \
        "${BUILD_DIR}/zlib-ng-${ZLIB_NG_VERSION}" \
        "${BUILD_DIR}/zlib-ng-${ZLIB_NG_VERSION}.zip"
}

ensure_libpng_source() {
    # GitHub tag archive extracts as libpng-${LIBPNG_VERSION}/ (NOT lpng1656/).
    download_zip_source \
        "libpng" \
        "${LIBPNG_URL}" \
        "${BUILD_DIR}/libpng-${LIBPNG_VERSION}" \
        "${BUILD_DIR}/libpng-${LIBPNG_VERSION}.zip"
}

build_libjpeg_turbo_macos() {
    local rid="$1"
    local arch
    local deployment_target
    local source_dir
    local build_dir
    local out_dir
    local source_lib
    local destination_lib

    PHASE="libjpeg_turbo"
    require_command cmake
    require_command install_name_tool

    arch="$(macos_arch_for_rid "${rid}")"
    deployment_target="$(macos_deployment_target_for_rid "${rid}")"
    source_dir="${BUILD_DIR}/libjpeg-turbo-${LIBJPEG_TURBO_VERSION}"
    build_dir="${source_dir}/build-${rid}"
    out_dir="$(rid_out_dir "${rid}")"
    source_lib="${build_dir}/libturbojpeg.dylib"
    destination_lib="${out_dir}/$(rid_libturbojpeg_name "${rid}")"

    if [[ "${rid}" == "osx-x64" ]] && ! command -v nasm >/dev/null 2>&1; then
        log "WARNING: nasm not found; building x86_64 libturbojpeg without SIMD acceleration. Install via 'brew install nasm'."
    fi

    log "Configuring libjpeg-turbo ${LIBJPEG_TURBO_VERSION} for ${rid}"
    cmake -S "${source_dir}" -B "${build_dir}" \
        -DCMAKE_BUILD_TYPE=Release \
        -DBUILD_SHARED_LIBS=ON \
        -DENABLE_STATIC=OFF \
        -DWITH_TURBOJPEG=ON \
        -DREQUIRE_SIMD=OFF \
        -DCMAKE_OSX_ARCHITECTURES="${arch}" \
        -DCMAKE_OSX_DEPLOYMENT_TARGET="${deployment_target}"

    log "Building libjpeg-turbo ${LIBJPEG_TURBO_VERSION} for ${rid}"
    cmake --build "${build_dir}" --config Release

    [[ -f "${source_lib}" ]] || die "expected libturbojpeg output not found: ${source_lib}"

    mkdir -p "${out_dir}"
    cp -f "${source_lib}" "${destination_lib}"
    install_name_tool -id "@rpath/libturbojpeg.dylib" "${destination_lib}"
    codesign_macos_library "${destination_lib}"
    log "Installed ${destination_lib}"
}

build_libjpeg_turbo_linux() {
    local source_dir
    local build_dir
    local out_dir
    local source_lib
    local destination_lib
    local docker_script

    PHASE="libjpeg_turbo"

    source_dir="/src/_native_build/libjpeg-turbo-${LIBJPEG_TURBO_VERSION}"
    build_dir="${source_dir}/build-linux-x64"
    out_dir="/src/src/libs/linux-x64"
    source_lib="${build_dir}/libturbojpeg.so"
    destination_lib="${out_dir}/libturbojpeg.so"

    docker_script="
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y build-essential cmake nasm patchelf
cmake -S '${source_dir}' -B '${build_dir}' \
    -DCMAKE_BUILD_TYPE=Release \
    -DBUILD_SHARED_LIBS=ON \
    -DENABLE_STATIC=OFF \
    -DWITH_TURBOJPEG=ON \
    -DREQUIRE_SIMD=OFF
cmake --build '${build_dir}' --config Release
test -f '${source_lib}'
mkdir -p '${out_dir}'
cp -f '${source_lib}' '${destination_lib}'
patchelf --set-soname libturbojpeg.so '${destination_lib}'
chown -R \"\${HOST_UID}:\${HOST_GID}\" /src/_native_build /src/src/libs/linux-x64
"

    log "Building libjpeg-turbo ${LIBJPEG_TURBO_VERSION} for linux-x64 in Docker"
    docker_run_linux_build "${docker_script}"
    log "Installed ${ROOT_DIR}/src/libs/linux-x64/libturbojpeg.so"
}

build_libjpeg_turbo() {
    local rid="$1"

    ensure_libjpeg_turbo_source

    case "${rid}" in
        osx-*) build_libjpeg_turbo_macos "${rid}" ;;
        linux-x64) build_libjpeg_turbo_linux ;;
        *) die "unsupported libjpeg-turbo target: ${rid}" ;;
    esac
}

# pdfium_png is a self-contained shim: zlib-ng (static) -> libpng (static, linked
# against that zlib-ng) -> the shim links both .a archives so nothing but system
# libraries remain at runtime. The three steps run as one unit because each
# consumes the previous one's output.
build_pdfium_png_macos() {
    local rid="$1"
    local arch
    local deployment_target
    local zlib_src
    local zlib_build
    local zlib_install
    local png_src
    local png_build
    local png_lib
    local out_dir
    local destination_lib

    PHASE="pdfium_png"
    require_command cmake
    require_command clang
    require_command install_name_tool

    arch="$(macos_arch_for_rid "${rid}")"
    deployment_target="$(macos_deployment_target_for_rid "${rid}")"
    zlib_src="${BUILD_DIR}/zlib-ng-${ZLIB_NG_VERSION}"
    zlib_build="${zlib_src}/build-${rid}"
    zlib_install="${BUILD_DIR}/zlib-ng-install-${rid}"
    png_src="${BUILD_DIR}/libpng-${LIBPNG_VERSION}"
    png_build="${png_src}/build-${rid}-static"
    png_lib="${png_build}/libpng16.a"
    out_dir="$(rid_out_dir "${rid}")"
    destination_lib="${out_dir}/$(rid_pdfium_png_name "${rid}")"

    log "Configuring zlib-ng ${ZLIB_NG_VERSION} (static) for ${rid}"
    cmake -S "${zlib_src}" -B "${zlib_build}" \
        -DCMAKE_BUILD_TYPE=Release \
        -DBUILD_SHARED_LIBS=OFF \
        -DZLIB_COMPAT=ON \
        -DZLIB_ENABLE_TESTS=OFF \
        -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
        -DCMAKE_OSX_ARCHITECTURES="${arch}" \
        -DCMAKE_OSX_DEPLOYMENT_TARGET="${deployment_target}"
    cmake --build "${zlib_build}" --config Release
    cmake --install "${zlib_build}" --prefix "${zlib_install}"

    log "Configuring libpng ${LIBPNG_VERSION} (static) for ${rid}"
    cmake -S "${png_src}" -B "${png_build}" \
        -DCMAKE_BUILD_TYPE=Release \
        -DBUILD_SHARED_LIBS=OFF \
        -DPNG_TESTS=OFF \
        -DPNG_TOOLS=OFF \
        -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
        -DCMAKE_OSX_ARCHITECTURES="${arch}" \
        -DCMAKE_OSX_DEPLOYMENT_TARGET="${deployment_target}" \
        -DZLIB_INCLUDE_DIR="${zlib_install}/include" \
        -DZLIB_LIBRARY="${zlib_install}/lib/libz.a"
    cmake --build "${png_build}" --config Release

    [[ -f "${png_lib}" ]] || die "expected libpng static archive not found: ${png_lib}"

    log "Building pdfium_png shim for ${rid}"
    mkdir -p "${out_dir}"
    clang -shared \
        -arch "${arch}" \
        "-mmacosx-version-min=${deployment_target}" \
        -O2 -fPIC -fvisibility=hidden \
        -I "${png_src}" \
        -I "${png_build}" \
        -I "${zlib_install}/include" \
        "${SCRIPT_DIR}/pdfium_png.c" \
        "${png_lib}" \
        "${zlib_install}/lib/libz.a" \
        -Wl,-install_name,@rpath/libpdfium_png.dylib \
        -o "${destination_lib}"

    codesign_macos_library "${destination_lib}"
    log "Installed ${destination_lib}"
}

build_pdfium_png_linux() {
    local zlib_src
    local zlib_build
    local zlib_install
    local png_src
    local png_build
    local out_dir
    local destination_lib
    local docker_script

    PHASE="pdfium_png"

    zlib_src="/src/_native_build/zlib-ng-${ZLIB_NG_VERSION}"
    zlib_build="${zlib_src}/build-linux-x64"
    zlib_install="/src/_native_build/zlib-ng-install-linux-x64"
    png_src="/src/_native_build/libpng-${LIBPNG_VERSION}"
    png_build="${png_src}/build-linux-x64-static"
    out_dir="/src/src/libs/linux-x64"
    destination_lib="${out_dir}/libpdfium_png.so"

    docker_script="
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y build-essential cmake
cmake -S '${zlib_src}' -B '${zlib_build}' \
    -DCMAKE_BUILD_TYPE=Release \
    -DBUILD_SHARED_LIBS=OFF \
    -DZLIB_COMPAT=ON \
    -DZLIB_ENABLE_TESTS=OFF \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON
cmake --build '${zlib_build}' --config Release
cmake --install '${zlib_build}' --prefix '${zlib_install}'
cmake -S '${png_src}' -B '${png_build}' \
    -DCMAKE_BUILD_TYPE=Release \
    -DBUILD_SHARED_LIBS=OFF \
    -DPNG_TESTS=OFF \
    -DPNG_TOOLS=OFF \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
    -DZLIB_INCLUDE_DIR='${zlib_install}/include' \
    -DZLIB_LIBRARY='${zlib_install}/lib/libz.a'
cmake --build '${png_build}' --config Release
test -f '${png_build}/libpng16.a'
mkdir -p '${out_dir}'
gcc -shared -O2 -fPIC -fvisibility=hidden -o '${destination_lib}' \
    -I '${png_src}' \
    -I '${png_build}' \
    -I '${zlib_install}/include' \
    /src/src/native/pdfium_png.c \
    '${png_build}/libpng16.a' \
    '${zlib_install}/lib/libz.a' \
    -lm
chown -R \"\${HOST_UID}:\${HOST_GID}\" /src/_native_build /src/src/libs/linux-x64
"

    log "Building pdfium_png shim for linux-x64 in Docker"
    docker_run_linux_build "${docker_script}"
    log "Installed ${ROOT_DIR}/src/libs/linux-x64/libpdfium_png.so"
}

build_pdfium_png() {
    local rid="$1"

    ensure_zlib_ng_source
    ensure_libpng_source

    case "${rid}" in
        osx-*) build_pdfium_png_macos "${rid}" ;;
        linux-x64) build_pdfium_png_linux ;;
        *) die "unsupported pdfium_png target: ${rid}" ;;
    esac
}

main() {
    parse_args "$@"
    normalize_target

    PHASE="setup"
    log "Root: ${ROOT_DIR}"
    log "Target: ${TARGET}"
    log "Only: ${ONLY}"
    ensure_dirs

    if is_selected pdfium; then
        if [[ "${NO_PDFIUM}" -eq 1 ]]; then
            PHASE="pdfium"
            log "Skipping PDFium because --no-pdfium was supplied"
        else
            download_pdfium "${TARGET}"
        fi
    fi

    if is_selected libtiff; then
        build_libtiff "${TARGET}"
    fi

    if is_selected tiff_shim; then
        build_tiff_shim "${TARGET}"
    fi

    if is_selected libjpeg_turbo; then
        build_libjpeg_turbo "${TARGET}"
    fi

    if is_selected pdfium_png; then
        build_pdfium_png "${TARGET}"
    fi

    PHASE="done"
    log "Build complete"
}

main "$@"
