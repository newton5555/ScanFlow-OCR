# ScanFlow-OCR

Cross-platform desktop OCR (Windows / Linux x64) built with Avalonia.

Extracted from the OCR path of private ScanFlow (barcode / DecodeP1 / Phenix / CoreHost / NativeLoading / WPF Desktop removed).

## Phase 1 scope

| Area | Choice |
|------|--------|
| UI | Avalonia (Win + Linux x64) |
| Camera | Vendored FlashCap — Windows Media Foundation, Linux V4L2; MJPEG-first |
| Still images | StbImageSharp + BitMiracle.LibTiff.NET (not TurboJPEG) |
| Camera JPEG | TurboJPEG via `native/{win-x64\|linux-x64}/` (or `SCANFLOW_OCR_TURBOJPEG_PATH`) |
| OCR | In-process Sdcb.SimdPaddleOCR Chinese V6 Tiny (Small optional) |
| Keyboard | Windows `SendInput`; Linux self-wrapped `/dev/uinput` (ASCII phase 1) |
| License | Apache-2.0 |

## Deferred

Separate OcrHost process, MQTT/TCP sinks, clipboard auto-output, AOT packing.

## Build

Requires .NET SDK 10 (`global.json`).

```bash
dotnet build ScanFlowOcr.slnx
dotnet run --project tests/ScanFlowOcr.SmokeTests
dotnet run --project src/ScanFlowOcr.App
```

### Native TurboJPEG

Place binaries under:

- `native/win-x64/turbojpeg.dll`
- `native/linux-x64/libturbojpeg.so`

Still-image decode does not need TurboJPEG.

### Linux keyboard

`/dev/uinput` write permission required. Phase 1 types ASCII (+ Tab/Enter) only; non-ASCII returns `UnicodeUnsupported`.

### Linux camera

FlashCap `CaptureDevices` selects V4L2. Process needs access to `/dev/video*`.

## Layout

```
src/ScanFlowOcr.Contracts
src/ScanFlowOcr.Imaging
src/ScanFlowOcr.Capture.FlashCap (+ vendor/FlashCap)
src/ScanFlowOcr.Ocr.SimdPaddle
src/ScanFlowOcr.Outputs
src/ScanFlowOcr.App
tests/ScanFlowOcr.SmokeTests
native/win-x64 native/linux-x64
```

## Status

Phase 1 source migration is in the tree and **builds on Linux** with .NET SDK 10:

- `dotnet build ScanFlowOcr.slnx` — Contracts, Imaging, Capture.FlashCap (+ vendor), Ocr.SimdPaddle, Outputs, App, SmokeTests
- `dotnet run --project tests/ScanFlowOcr.SmokeTests` — lease / still-JPEG / keyboard route / SimdPaddle metadata checks pass

### Stubbed / deferred

- TurboJPEG native binaries not shipped (`native/{rid}/` placeholders; set `SCANFLOW_OCR_TURBOJPEG_PATH`)
- Avalonia UI: camera enumerate + OCR/keyboard hooks only; no live preview/OCR pipeline UI yet
- Linux keyboard: ASCII (+ Tab/Enter) via `/dev/uinput`; non-ASCII returns `UnicodeUnsupported`
- No OcrHost, MQTT/TCP, clipboard, or AOT packing
