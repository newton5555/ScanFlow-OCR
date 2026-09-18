# ScanFlow-OCR

Cross-platform desktop OCR (Windows / Linux x64) built with Avalonia.

> Work in progress. Extracted from the OCR path of private [ScanFlow](https://github.com/newton5555/ScanFlow) (barcode stack removed).

## Goals (phase 1)

- Avalonia UI
- Camera capture via FlashCap (Windows Media Foundation + Linux V4L2), MJPEG-first
- Local still images via Stb / LibTiff (same approach as ScanFlow)
- Camera JPEG decode via TurboJPEG (`native/win-x64`, `native/linux-x64`)
- In-process SimdPaddle OCR (Chinese V6 Tiny; Small optional)
- Keystroke output: Windows `SendInput`, Linux `/dev/uinput` (self-wrapped)
- License: Apache-2.0

## Non-goals (for now)

Separate OcrHost process, MQTT/TCP sinks, clipboard auto-output, AOT packing of proprietary barcode engines.

## Status

Repository scaffold only. Source migration from ScanFlow is in progress.
