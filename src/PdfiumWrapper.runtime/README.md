# PdfiumWrapper native runtime

<img src="https://raw.githubusercontent.com/malweka/PdfiumWrapper/main/icon.png" alt="PdfiumWrapper Icon" width="64" height="64" align="left" />

Native libraries for [PdfiumWrapper](https://www.nuget.org/packages/PdfiumWrapper), one package per platform: `PdfiumWrapper.runtime.win-x64`, `linux-x64`, `osx-x64` and `osx-arm64`.

**Do not reference this package directly.** Install `PdfiumWrapper` instead:

```bash
dotnet add package PdfiumWrapper
```

`PdfiumWrapper` depends on all four runtime packages at exactly its own version. A portable build (no `RuntimeIdentifier`) gets every platform under `runtimes/<rid>/native` and loads the matching one; a RID-specific build or publish copies only its own platform.

## Contents

Each package holds five libraries under `runtimes/<rid>/native`, and no managed code:

| Library | Built from |
|---|---|
| `pdfium` | PDFium chromium/8076 ([pdfium-binaries](https://github.com/bblanchon/pdfium-binaries)) |
| `tiff` | libtiff 4.7.2 |
| `tiff_shim` | PdfiumWrapper's libtiff shim |
| `turbojpeg` | libjpeg-turbo 3.2.0 |
| `pdfium_png` | PdfiumWrapper's PNG shim, with libpng 1.6.59 and zlib-ng 2.3.3 linked in |

## License

The packaging and the shims are MIT. The third-party libraries keep their own licenses; their texts are in `THIRD-PARTY-NOTICES.txt` in this package. Building the natives is described in [BUILDING-NATIVE-LIBS.md](https://github.com/malweka/PdfiumWrapper/blob/main/docs/BUILDING-NATIVE-LIBS.md).
