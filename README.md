# ScanFlow-OCR

跨平台 **单 OCR** 桌面应用（Windows / Linux x64）。

连续相机识别与静态图识别：ROI、去重会话、结果列表，以及键盘 / MQTT / TCP 输出。本仓库只做 OCR。

## 主要技术依赖

| 技术 | 用途 |
|------|------|
| **Avalonia 12.1.2** | 跨平台桌面 UI |
| **Sdcb.SimdPaddleOCR** | 进程内中文 OCR（Chinese V6 Tiny 默认，Small 可选） |
| **libjpeg-turbo 3.2.0（TurboJPEG）** | 相机 MJPEG 预览与帧 OCR；原生库在 `native/win-x64`、`native/linux-x64` |
| **FlashCap**（仓库内 vendor） | 相机采集（Windows MF / 回退 DShow·VFW；Linux V4L2） |

构建需要 **.NET SDK 10**（见 `global.json`）。静态图另用 StbImageSharp / LibTiff；输出侧有 MQTTnet、SQLite 等，细节见 `Directory.Packages.props` 与 `THIRD-PARTY-NOTICES.md`。

TurboJPEG 来源与校验：`native/ORIGIN.md`。可选环境变量：`SCANFLOW_OCR_TURBOJPEG_PATH`、`SCANFLOW_OCR_CAMERA_BACKEND`（`mediafoundation` / `directshow` / `vfw`）。

### 平台注意

- **Windows**：相机需能提供 MJPEG（或可回退后端）；键盘输出使用 `SendInput`。
- **Linux**：预览需 `/dev/video*`；键盘输出需 `/dev/uinput` 写权限（Phase 1 仅 ASCII + Tab/Enter）。

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

发布打包（产物默认位于 `publish/win-x64`、`publish/linux-x64`）：

```bash
# Windows x64 自包含发布 (PowerShell 7)
pwsh scripts/Publish-Win-x64.ps1 -SelfContained

# Linux x64 自包含发布 (Bash)
bash scripts/publish-linux-x64.sh
```

## 仓库结构

```
src/ScanFlowOcr.Contracts
src/ScanFlowOcr.Imaging
src/ScanFlowOcr.Capture.FlashCap   # + vendor/FlashCap
src/ScanFlowOcr.Ocr.SimdPaddle
src/ScanFlowOcr.Outputs
src/ScanFlowOcr.Runtime
src/ScanFlowOcr.App
tests/ScanFlowOcr.SmokeTests
native/win-x64
native/linux-x64
docs/MEMORY-OPTIMIZATION.md
docs/RELEASE-1.0.0.md
scripts/
.github/workflows/release.yml
CHANGELOG.md
```

## 许可

Apache-2.0 — 见 `LICENSE`。