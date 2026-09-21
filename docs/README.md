# ScanFlow-OCR 文档目录

本目录包含 **ScanFlow-OCR**（单 OCR 视觉识别系统）的架构、图像内存治理、发布说明和变更记录。文档会区分源码已确认的行为与仍需在目标设备验证的性能结论。

---

## 核心技术与架构文档

- 📘 [PROJECT-OVERVIEW.md](PROJECT-OVERVIEW.md)<br>
  **项目架构与实现边界说明**：说明与主项目 ScanFlow 的技术边界、核心并发流水线、`ImageLease` Size-Class 图像缓冲治理、几何朝向解算、各输出通道的确认语义和当前 UI 交互。

- 💾 [MEMORY-OPTIMIZATION.md](MEMORY-OPTIMIZATION.md)<br>
  **单进程图像内存优化记录**：记录连续相机推帧场景下的工作集治理方案、Size-Class 阶梯空闲桶设计、周期性 `TrimExcess` 机制与 128 MiB 图像分配器保留预算；真实相机长跑仍需单独验证。

---

## 版本发布日志

- 🏷️ [RELEASE-1.0.1.md](RELEASE-1.0.1.md)<br>
  **v1.0.1 修正版发布说明**：按当前 SimdPaddleOCR 透视拉正、竖排宽高比判断及 CLS 旋转路径复原 `OcrLine.ReadingAngleDegrees`；该值表示算法采用的朝向。

- 🏷️ [RELEASE-1.0.0.md](RELEASE-1.0.0.md)<br>
  **v1.0.0 跨平台首发正式版发布说明**：Avalonia 桌面 UI、SimdPaddleOCR 模型包、FlashCap 相机驱动、TurboJPEG 3.2.0 原生 SIMD 解码和 SQLite 持久化重试队列。

---

## 其他相关文档

- 🚀 [scripts/README.md](../scripts/README.md)：自动化跨平台发布脚本使用指南（Windows / Linux 各模式、单文件发布与实验性 Native AOT 编译）。
- 📝 [CHANGELOG.md](../CHANGELOG.md)：版本演进全量变更记录。
- ⚖️ [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)：第三方开源组件许可与来源说明。
