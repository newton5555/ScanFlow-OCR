# ScanFlow-OCR

Cross-platform desktop OCR (Windows / Linux x64) built with Avalonia.

Extracted from the OCR path of private ScanFlow (barcode / DecodeP1 / Phenix / CoreHost / NativeLoading / WPF Desktop removed).

## Phase 1 scope

| Area | Choice |
|------|--------|
| UI | Avalonia (Win + Linux x64) |
| Camera | Vendored FlashCap — Windows Media Foundation, Linux V4L2; MJPEG-first |
| Still images | StbImageSharp + BitMiracle.LibTiff.NET (not TurboJPEG) |
| Camera JPEG | TurboJPEG **3.2.0** via `native/{win-x64\|linux-x64}/` (or `SCANFLOW_OCR_TURBOJPEG_PATH`) |
| OCR | In-process Sdcb.SimdPaddleOCR Chinese V6 Tiny (Small optional) |
| Keyboard | Windows `SendInput`; Linux self-wrapped `/dev/uinput` (ASCII phase 1) |
| Outputs | MQTT (MQTTnet), TCP client (optional TLS), keyboard — durable SQLite queue via `OutputCoordinator` |
| License | Apache-2.0 |

## Deferred

Separate OcrHost process, clipboard auto-output, AOT packing.

## Build

Requires .NET SDK 10 (`global.json`).

```bash
dotnet build ScanFlowOcr.slnx
dotnet run --project tests/ScanFlowOcr.SmokeTests
dotnet run --project src/ScanFlowOcr.App
```

Build succeeds on Linux **without** a camera attached. Live preview needs `/dev/video*` (Linux) or an MF camera (Windows) plus the TurboJPEG native libs (copied to output on App build).

### Native TurboJPEG (3.2.0 official binaries)

Shipped under:

- `native/win-x64/turbojpeg.dll`
- `native/linux-x64/libturbojpeg.so`

Source packages (SHA-256 and extraction notes in `native/ORIGIN.md`):

- Windows VC x64: https://github.com/libjpeg-turbo/libjpeg-turbo/releases/download/3.2.0/libjpeg-turbo-3.2.0-vc-x64.exe
- Linux amd64 deb: https://github.com/libjpeg-turbo/libjpeg-turbo/releases/download/3.2.0/libjpeg-turbo-official_3.2.0_amd64.deb

`ScanFlowOcr.App` copies these into `$(OutputDir)/native/{rid}/` on build. Override with `SCANFLOW_OCR_TURBOJPEG_PATH` if needed.

Still-image decode does **not** need TurboJPEG. Camera MJPEG preview / frame OCR does.

License: IJG + Modified BSD — see `THIRD-PARTY-NOTICES.md`. This software is based in part on the work of the Independent JPEG Group.

### Linux keyboard

`/dev/uinput` write permission required. Phase 1 types ASCII (+ Tab/Enter) only; non-ASCII returns `UnicodeUnsupported`.

### Linux camera

FlashCap `CaptureDevices` selects V4L2. Process needs access to `/dev/video*`. App UI: refresh devices → pick MJPEG mode → **启动扫描** (continuous OCR session) or **仅预览** → optional OCR current frame.

### MQTT / TCP outputs

`ScanFlowOcr.Outputs` provides:

- `MqttRoute` / `MqttOutputSink` — broker, port, TLS, client id, topic, QoS 0–2, optional username/password
- `TcpRoute` / `TcpOutputSink` — host, port, optional TLS; one UTF-8 JSON line per record (LF-terminated)
- `OutputCoordinator` — single-sink durable queue (SQLite under `%LOCALAPPDATA%/ScanFlowOcr` or `~/.local/share/ScanFlowOcr`)

Payloads are OCR-only JSON (`textLines`); no barcode fields. MQTT passwords use Windows DPAPI when available; on Linux the protected field stores plaintext.

## Layout

Matches `ScanFlowOcr.slnx`:

```
src/ScanFlowOcr.Contracts
src/ScanFlowOcr.Imaging
src/ScanFlowOcr.Capture.FlashCap (+ vendor/FlashCap)
src/ScanFlowOcr.Ocr.SimdPaddle
src/ScanFlowOcr.Outputs
src/ScanFlowOcr.Runtime      # OCR-only ScanSession (dedupe + ROI crop)
src/ScanFlowOcr.App          # Avalonia UI, settings, continuous scan
tests/ScanFlowOcr.SmokeTests
native/win-x64 native/linux-x64  (+ ORIGIN.md)
docs/LOCAL-AGENT-HANDOFF.md  # handoff for Windows local agent
```

## Status

Phase 1 source migration is in the tree and **builds on Linux** with .NET SDK 10:

- Avalonia packages pinned to **12.1.2**
- Official **libjpeg-turbo 3.2.0** TurboJPEG libs under `native/`
- `dotnet build ScanFlowOcr.slnx -c Release` — Contracts, Imaging, Capture.FlashCap (+ vendor), Ocr.SimdPaddle, Outputs, App, SmokeTests
- `dotnet run --project tests/ScanFlowOcr.SmokeTests` — lease / still-JPEG / keyboard+MQTT+TCP route / coordinator configure / SimdPaddle metadata checks pass
- Avalonia App: **settings persistence**; **continuous OCR scan session** (Runtime); **ROI overlay**; preview zoom/crosshair; results search/inspector; playlist thumbnails; still-image OCR; optional preview-only + frame OCR; MQTT/TCP/keyboard sinks

### Stubbed / deferred

- Linux keyboard: ASCII (+ Tab/Enter) via `/dev/uinput`; non-ASCII returns `UnicodeUnsupported`
- No OcrHost, clipboard, or AOT packing
- Full in-viewport ROI drag editor, playlist-as-session-source, installers — see `docs/LOCAL-AGENT-HANDOFF.md`
