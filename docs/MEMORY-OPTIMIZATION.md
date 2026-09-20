# 单进程内存优化 / Single-process memory optimization

## 2026-09-20（本轮）连续扫描工作集仍上涨 — size class + 周期 Trim + 更低上限

**现象：** 连续相机 OCR 时工作集持续爬升，停扫才回落。根因是：

1. **ImageAllocator 按精确 `length` 建空闲桶** — MJPEG 每帧 JPEG 大小抖动 → 大量几乎用不到的 exact-size idle 块堆在 `CommittedBytes` 里，直到 `Stop`/`Reconfigure` 才 `TrimExcess`。
2. **`TrimExcess` 只在停止/重配置时调用** — 长跑期间 idle 只增不减。
3. **默认 `RetainedByteLimit` ≈ 400MB**（`5120²×4×4`）— 给 idle 留了过大的天花板。

**本轮改动：**

| 区域 | 变更 |
|------|------|
| `ImageLease.cs` / `ImageAllocator` | 请求长度上取整到 **size class**（下一 2 的幂；≥64KiB 也走 2 的幂）。按 **capacity** 入闲置桶；lease 仍只暴露 `length` 字节。 |
| `ScanSession.RecognizeOcrAsync` | 成功 OCR 后 `MaybeTrimIdle()`：当 `IdleBytes > RetainedByteLimit/4` 或 `> 2×MaxFrameBytes` 时 `TrimExcess()`，避免每帧抖动。停止/重配置路径的 Trim 保留。 |
| `AppSettings.ToSessionProfile` | `RetainedByteLimit` 默认改为 **128 MiB**（仍 ≥ `MaxFrameBytes` ≈100MiB）。 |

**真机验证：** Release 构建后开连续扫描 + 预览，Task Manager / `dotnet-counters` 看工作集 5–10 分钟应升到平台期后稳定；停扫后 idle 释放。可选开 ROI 确认坐标仍正确。

---

## 更早（同日）已落地

- 启动连续扫描前释放闲置静态图 OCR reader，避免两套模型同时常驻。
- ImageAllocator 使用非托管缓冲；`LiveBytes` / `CommittedBytes` / `IdleBytes`；停止时 `TrimExcess`；不再用共享 ArrayPool 像素桶。
- JPEG 连续扫描和独立预览支持 TurboJPEG 原生 1/2、1/4、1/8 缩放；默认预览上限 1280×720；OCR 仍原图解码。
- BGR ROI 复用 offset/stride；日志正文最多 64K 字符。

构建和 smoke 覆盖：缓冲引用计数、size-class 复用、TrimExcess、ROI 共享像素、JPEG 缩放、JPEG/BGRA OCR 格式转换。

尚待真实相机长跑与 Private Bytes / 工作集对照；本文件不虚构节省 MB 数值。

Out of scope: DecodeP1 / Phenix / CoreHost / MemoryModule / 条码 UI。
