# Third-party notices

This repository redistributes or depends on the following third-party components.
Retain upstream license texts when shipping binaries.

## FlashCap (Apache-2.0)

Vendored under `src/ScanFlowOcr.Capture.FlashCap/vendor/FlashCap/`.
See `LICENSE` and `ORIGIN.md` in that directory.
Copyright (c) Kouji Matsui (@kekyo) and contributors.

## Sdcb.SimdPaddleOCR and model packages (Apache-2.0)

- Sdcb.SimdPaddleOCR
- Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny / ChineseV6Small
Upstream PP-OCR / Paddle model notices must be retained when redistributing models.

## StbImageSharp / StbImageWriteSharp

Public-domain STB image libraries via managed wrappers (see NuGet package licenses).

## BitMiracle.LibTiff.NET

BSD-style LibTiff .NET binding. See package license.

## libjpeg-turbo (TurboJPEG)

IJG / zlib / BSD-style terms as applicable to the native binaries you ship under `native/`.
Not redistributed in this repository by default (placeholders only).

## Avalonia

Avalonia UI (MIT). See NuGet package licenses.

## Microsoft.Data.Sqlite / SQLitePCLRaw (optional future)

Not referenced in phase 1 keyboard-only Outputs; listed for planned durable queue work.
When added, include SQLite blessing / PCLRaw notices.
