# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-09-20

### Added
- **Cross-Platform Avalonia UI**: Desktop application supporting Windows and Linux (X11 / Wayland) with dark/light themes and HUD performance counters.
- **In-Process SimdPaddleOCR**: Chinese V6 Tiny (default), Small, and Medium models with local zero-network inference.
- **Low-Latency Camera Pipeline**: Integrated FlashCap for video capture and libjpeg-turbo (TurboJPEG 3.2.0) hardware-accelerated decompression.
- **Memory-Bounded Image Pooling**: `ImageLease` memory allocator with size-class pooling and 128 MiB idle retention limit to eliminate memory leaks during continuous scanning.
- **Cross-Platform Virtual Keyboard Output**:
  - Windows: Win32 `SendInput` supporting Unicode and foreground window protection.
  - Linux: `/dev/uinput` kernel virtual device with persistent singleton lifetime, libinput debounce delays, full-width ASCII normalization, and CRLF newline merging.
- **Reliable Outputs**: SQLite-backed persistent retry queue for Keyboard, MQTT, and TCP sinks.
- **CI / CD Automated Release**: GitHub Actions workflow for cross-platform single-file binary building and automated GitHub Release publishing.
