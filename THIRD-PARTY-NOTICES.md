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
- Sdcb.SimdPaddleOCR.Models.TextLineOrientation
Upstream PP-OCR / Paddle model notices must be retained when redistributing models.

## StbImageSharp / StbImageWriteSharp

Public-domain STB image libraries via managed wrappers (see NuGet package licenses).

## BitMiracle.LibTiff.NET

BSD-style LibTiff .NET binding. See package license.

## libjpeg-turbo 3.2.0 (TurboJPEG)

Official shared libraries are redistributed under `native/`:

| RID | File | Source package |
|-----|------|----------------|
| win-x64 | `turbojpeg.dll` | [libjpeg-turbo-3.2.0-vc-x64.exe](https://github.com/libjpeg-turbo/libjpeg-turbo/releases/download/3.2.0/libjpeg-turbo-3.2.0-vc-x64.exe) |
| linux-x64 | `libturbojpeg.so` | [libjpeg-turbo-official_3.2.0_amd64.deb](https://github.com/libjpeg-turbo/libjpeg-turbo/releases/download/3.2.0/libjpeg-turbo-official_3.2.0_amd64.deb) |

See `native/ORIGIN.md` for SHA-256 of the source packages and extraction notes.
Release tag: https://github.com/libjpeg-turbo/libjpeg-turbo/releases/tag/3.2.0

This software is based in part on the work of the Independent JPEG Group.

libjpeg-turbo is covered by the IJG License (libjpeg API) and the Modified
(3-clause) BSD License (TurboJPEG API). Both apply to the TurboJPEG shared
library. SIMD / zlib portions use the zlib License (subsumed in this context).

### Modified (3-clause) BSD License (TurboJPEG API)

Copyright (C)2009-2024 D. R. Commander. All Rights Reserved.
Copyright (C)2015 Viktor Szathmáry. All Rights Reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

- Redistributions of source code must retain the above copyright notice,
  this list of conditions and the following disclaimer.
- Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.
- Neither the name of the libjpeg-turbo Project nor the names of its
  contributors may be used to endorse or promote products derived from this
  software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS",
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDERS OR CONTRIBUTORS BE
LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.

Full upstream text: https://github.com/libjpeg-turbo/libjpeg-turbo/blob/3.2.0/LICENSE.md

Windows: the VC TurboJPEG DLL may require the Visual C++ 2015–2022 x64
redistributable at runtime on end-user machines.

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
