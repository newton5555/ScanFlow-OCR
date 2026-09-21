# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed
- **TCP 输出移除 TLS 选项**：当前 TCP 通道使用产线内网裸套接字推送，`TcpRoute` 不再包含 `Tls` 参数，设置界面移除「安全连接 (TLS)」行；MQTT 的 TLS 不受影响。
- **阅读方向箭头固定为红色**：新增 `ReadingAxisArrowBrush` 主题令牌（深色 `#ff4d4f` / 浅色 `#d92d20`），箭头不再复用品牌色，避免与逐框配色混淆。

### Added
- **OCR 框逐框配色**：同一帧内每个 OCR 框按索引取不同强调色（`AnnotationPalette`，8 色循环，不含红色），浮动气泡与右侧结果列表行使用同一颜色，便于对照定位；结果行新增左侧色条与同色「OCR」标签。
- **帧耗时可视化**：连续扫描时 HUD 与底部指标栏显示 **OCR 引擎实际推理耗时**（指数平滑）与**输入到分析结果的延迟**；结果详情新增「本帧算法耗时」。引擎耗时取自 `StageAnalysis.EngineTime`（只含推理，不含 JPEG 解码与 ROI 拷贝），分析延迟取自 `ScanAnalysis.CompletedTimestamp - FrameStamp.ReceivedTimestamp`（含应用可观测的解码、ROI 拷贝、坐标映射与排队等待，不包含 Avalonia 最终绘制完成时间）。同一帧的所有结果行共享该帧的引擎耗时。

## [1.0.1] - 2026-09-20

### Fixed
- **OCR reading-axis overlay**: arrow now reproduces the current SimdPaddleOCR crop semantics (perspective warp, vertical clockwise rot when height/width >= 1.5, then CLS `AppliedRotationDegrees`). It shows the orientation selected by the algorithm; if CLS is wrong, the arrow follows that wrong choice.

### Added
- `OcrLine.ReadingAngleDegrees` and overlay center/arrow visualization for debugging orientation (including when CLS is wrong).
## [1.0.0] - 2026-09-20

### Added
- **Cross-Platform Avalonia UI**: Desktop application supporting Windows and Linux (X11 / Wayland) with dark/light themes and HUD performance counters.
- **In-Process SimdPaddleOCR**: Chinese V6 Tiny (default) and Small models with local in-process inference; output adapters may still use the network when configured.
- **Camera Pipeline**: Integrated FlashCap for video capture and libjpeg-turbo (TurboJPEG 3.2.0) native CPU/SIMD decompression.
- **Memory-Bounded Image Pooling**: `ImageLease` allocator with size-class pooling and 128 MiB retained-byte limit for continuous scan.
- **Cross-Platform Virtual Keyboard Output**:
  - Windows: Win32 `SendInput` with Unicode and optional foreground target-process matching.
  - Linux: `/dev/uinput` virtual keyboard (ASCII + Tab/Enter in phase 1). Types into the **currently focused** window; process-name focus switching is not implemented yet.
- **Persistent Output Queue**: SQLite-backed retry queue for Keyboard, MQTT, and TCP sinks; end-to-end confirmation depends on each sink protocol.
- **CI / CD Automated Release**: GitHub Actions workflow for cross-platform single-file builds and GitHub Release publishing.
