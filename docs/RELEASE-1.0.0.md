# ScanFlow-OCR v1.0.0 Release Notes

> 发布日期：2026-09-20  
> 支持平台：Windows x64 / Linux x64（Ubuntu 22.04+ / Debian 12+ 等 glibc 环境）  
> 运行时：.NET 10.0（自包含单文件发布时，目标机无需预装 .NET）

ScanFlow-OCR v1.0.0 是首个跨平台发布基线。产品定位为 **单 OCR**：USB 相机连续识别与静态图 OCR，配合预览、图像缓冲区预算与可持久化输出队列。延迟、内存和现场送达能力需按目标设备与通道验证。

---

## 功能概览

### 1. 跨平台桌面 UI（Avalonia）
- Avalonia 桌面应用，覆盖 Windows 与 Linux（X11 / Wayland）
- 深色/浅色主题、矢量图标、会话 HUD（帧率、处理帧等）
- 百分比 ROI、实时识别结果与历史列表

### 2. 进程内中文 OCR（SimdPaddleOCR）
- `Sdcb.SimdPaddleOCR`，默认 Chinese V6 **Tiny**，可选 **Small**
- 本地推理，不依赖外网 OCR API
- 静态图（PNG/JPEG/BMP/TIFF）与相机连续识别、会话去重

### 3. 相机与 JPEG 管线（FlashCap + TurboJPEG）
- FlashCap：Windows Media Foundation / DirectShow / VFW；Linux V4L2
- 官方 libjpeg-turbo 3.2.0（TurboJPEG）原生 CPU/SIMD 解码 MJPEG
- `ImageLease` size-class 池化，默认 `RetainedByteLimit` 128 MiB，缓解连续扫描内存上涨

### 4. 虚拟键盘输出
- **Windows**：`SendInput`，支持 Unicode；可按目标进程名匹配前台窗口（如 `notepad.exe`）
- **Linux**：`/dev/uinput` 虚拟键盘（Phase 1：ASCII + Tab/Enter）
  - 需要 `/dev/uinput` 写权限（`input` 组或 udev / `chmod`）
  - **不会按进程名切换焦点**；键事件进入**当前焦点窗口**。输出到 gedit 等编辑器前，请先点选文档并使光标在编辑区
  - 全角 ASCII 会规范为半角；`\r\n` 合并为单次回车，避免 gedit 等出现多余空行

### 5. 输出队列
- 通道：键盘、MQTT（QoS 0/1/2）、TCP JSON Lines
- SQLite 持久化队列、失败重试、结构化投递日志；不同通道的 broker/服务端确认语义不同，队列不等同于端到端零丢失保证

---

## 数据与日志路径

| 平台 | 目录 |
|------|------|
| Linux | `~/.local/share/ScanFlowOcr/` |
| Windows | `%LOCALAPPDATA%\ScanFlowOcr\` |

常见文件：`logs/scanflow-ocr-YYYYMMDD.log`、`outputs.db`、回退 `appsettings.json`。

---

## 发布包与运行

| 包名 | 目标 | 主要内容 | 运行方式 |
|------|------|----------|----------|
| `ScanFlowOcr-win-x64.zip` | Windows 10/11 x64 | `ScanFlowOcr.App.exe`、TurboJPEG、配置等 | 双击主程序 |
| `ScanFlowOcr-linux-x64.tar.gz` | Linux x86_64（glibc 2.31+） | `ScanFlowOcr.App`、`native/linux-x64/libturbojpeg.so`、`run.sh` / `setup-permissions.sh` | `bash ./run.sh` |

**Linux 键盘权限提示：**  
确保对 `/dev/uinput` 可写，例如加入 `input` 组后重新登录，或按发行版配置 udev 规则。`chmod 666` 只适合临时诊断，不建议作为部署方案。Linux 键盘输出写入当前焦点窗口，不负责按进程名切换焦点。

---

## 许可

- 本项目：**Apache-2.0**（见 `LICENSE`）
- 第三方组件：见 `THIRD-PARTY-NOTICES.md`
