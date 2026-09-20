# ScanFlow-OCR

跨平台 **单 OCR** 桌面应用（Windows / Linux x64），基于 Avalonia。

面向连续相机识别与静态图识别：ROI、去重会话、结果列表，以及键盘 / MQTT / TCP 输出。本仓库 **只做 OCR**，不含条码解码或其他业务栈。

## 依赖

### 构建与运行时

| 依赖 | 说明 |
|------|------|
| **.NET SDK 10** | 见 `global.json`（当前 `10.0.100`，`rollForward: latestFeature`） |
| **Avalonia 12.1.2** | UI（`Avalonia` / `Desktop` / `Fluent` / `Fonts.Inter`） |
| **Sdcb.SimdPaddleOCR** | 进程内中文 OCR；模型包 Tiny（默认）/ Small（可选） |
| **FlashCap**（仓库内 vendor） | 相机采集：Windows 优先 Media Foundation，可回退 DirectShow/VFW；Linux V4L2；MJPEG 优先 |
| **libjpeg-turbo 3.2.0（TurboJPEG）** | 相机 MJPEG 预览与帧 OCR；随仓库 `native/win-x64`、`native/linux-x64` 提供 |
| **StbImageSharp** + **BitMiracle.LibTiff.NET** | 静态图解码（不依赖 TurboJPEG） |
| **MQTTnet** | MQTT 输出 |
| **Microsoft.Data.Sqlite** | 输出队列持久化 |
| **Serilog** | 日志 |

第三方许可摘要见 `THIRD-PARTY-NOTICES.md`。TurboJPEG 来源与校验见 `native/ORIGIN.md`。

### 可选环境变量

| 变量 | 作用 |
|------|------|
| `SCANFLOW_OCR_TURBOJPEG_PATH` | 覆盖 TurboJPEG 原生库路径 |
| `SCANFLOW_OCR_CAMERA_BACKEND` | Windows 强制后端：`mediafoundation` / `directshow` / `vfw` |

### 平台注意

- **Windows**：相机需能提供 MJPEG（或可回退后端）；键盘输出使用 `SendInput`。
- **Linux**：预览需访问 `/dev/video*`；键盘输出需 `/dev/uinput` 写权限（Phase 1 仅 ASCII + Tab/Enter）。

## 构建与运行

```bash
dotnet build ScanFlowOcr.slnx
dotnet run --project tests/ScanFlowOcr.SmokeTests
dotnet run --project src/ScanFlowOcr.App
```

可选相机探测（Windows）：

```bash
dotnet run --project tests/ScanFlowOcr.SmokeTests -- --camera
```

发布（示例，TurboJPEG 会随 App 构建拷到输出目录）：

```powershell
dotnet publish src/ScanFlowOcr.App -c Release -r win-x64 --self-contained false -o publish/win-x64
```

无相机时也可在 Linux 上完成构建与 smoke；真机预览需要摄像头与对应原生库。

## 仓库结构

```
src/ScanFlowOcr.Contracts
src/ScanFlowOcr.Imaging
src/ScanFlowOcr.Capture.FlashCap   # + vendor/FlashCap
src/ScanFlowOcr.Ocr.SimdPaddle
src/ScanFlowOcr.Outputs
src/ScanFlowOcr.Runtime            # OCR ScanSession（去重、ROI）
src/ScanFlowOcr.App                # Avalonia UI、设置、连续扫描
tests/ScanFlowOcr.SmokeTests
native/win-x64
native/linux-x64
docs/MEMORY-OPTIMIZATION.md        # 内存相关说明（可选阅读）
```

## 功能概览

- 设置持久化、连续 OCR 会话、ROI、预览缩放/准星
- 结果搜索与详情、图库缩略图、静态图 OCR
- 仅预览 + 单帧 OCR；MQTT / TCP / 键盘输出（OCR JSON，`textLines`）

## 许可

Apache-2.0 — 见 `LICENSE`。