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

## MQTTnet

MQTTnet (MIT) — MQTT client used by `MqttOutputSink`. See NuGet package license.

## Microsoft.Data.Sqlite / SQLitePCLRaw

SQLite durable output queue in `OutputCoordinator`. Include SQLite blessing / PCLRaw notices when shipping binaries.

## Serilog / Serilog.Sinks.File

Apache-2.0 / MIT — file logging used by `OutputCoordinator`. See NuGet package licenses.

## System.Security.Cryptography.ProtectedData

MIT — Windows DPAPI helper for MQTT password protection (optional on non-Windows).
