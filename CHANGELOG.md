# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.1] - 2026-09-20

### Fixed
- **OCR reading-axis overlay**: arrow now follows SimdPaddleOCR crop semantics (perspective warp, vertical clockwise rot when height/width >= 1.5, then CLS `AppliedRotationDegrees`), so the on-image arrow matches the orientation the recognizer actually used.

### Added
- `OcrLine.ReadingAngleDegrees` and overlay center/arrow visualization for debugging orientation (including when CLS is wrong).
## [1.0.0] - 2026-09-20

### Added
- **Cross-Platform Avalonia UI**: Desktop application supporting Windows and Linux (X11 / Wayland) with dark/light themes and HUD performance counters.
- **In-Process SimdPaddleOCR**: Chinese V6 Tiny (default) and Small models with local zero-network inference.
- **Low-Latency Camera Pipeline**: Integrated FlashCap for video capture and libjpeg-turbo (TurboJPEG 3.2.0) hardware-accelerated decompression.
- **Memory-Bounded Image Pooling**: `ImageLease` allocator with size-class pooling and 128 MiB retained-byte limit for continuous scan.
- **Cross-Platform Virtual Keyboard Output**:
  - Windows: Win32 `SendInput` with Unicode and optional foreground target-process matching.
  - Linux: `/dev/uinput` virtual keyboard (ASCII + Tab/Enter in phase 1). Types into the **currently focused** window; process-name focus switching is not implemented yet.
- **Reliable Outputs**: SQLite-backed persistent retry queue for Keyboard, MQTT, and TCP sinks.
- **CI / CD Automated Release**: GitHub Actions workflow for cross-platform single-file builds and GitHub Release publishing.