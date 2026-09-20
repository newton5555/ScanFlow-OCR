# ScanFlow-OCR v1.0.0 Release Notes

> 发布日期：2026-09-20  
> 支持平台：Windows x64 / Linux x64 (Ubuntu 22.04+ / Debian 12+)  
> 运行时基础：.NET 10.0 (Self-Contained 单文件自包含发布，目标机器无需预装 .NET 运行时)

ScanFlow-OCR v1.0.0 是首个跨平台正式发布版本。本项目专注于为工业、仓储与桌面办公场景提供**超低延迟连续 USB 相机与静态图像 OCR 识别**能力，具备高帧率预览、低显存/内存占用与可靠的自动化输出链路。

---

## 核心特性一览

### 1. 跨平台现代化桌面交互 (Avalonia)
- 基于 Avalonia UI 构建原生跨平台桌面应用，适配 Windows 与 Linux (X11 / Wayland) 显示环境。
- 现代化暗色/浅色自适应视觉体系、24x24 矢量图标规范、轻量级 HUD 实时性能指示（帧率、处理延迟、分辨率）。
- 相机交互式 ROI 动态调节，实时识别框投射与结果历史列表。

### 2. 高精度本地离线 OCR (SimdPaddleOCR)
- 进程内深度集成 `Sdcb.SimdPaddleOCR`（默认预置 Chinese V6 Tiny 模型，可选 Small / Medium）。
- 离线原生推理，单图检测+识别毫秒级响应，无需任何外部网络或云端 API 依赖。
- 支持静态图像（PNG/JPEG/BMP/TIFF）批量识别与连续相机视频流去重识别。

### 3. 超低延迟相机与图像解码管线 (TurboJPEG)
- 采用 FlashCap 统一封装相机采集（Windows MediaFoundation / DirectShow / VFW，Linux V4L2）。
- 深度集成原生 `libjpeg-turbo 3.2.0`（TurboJPEG），硬件指令集加速解压相机 MJPEG 流。
- **图像内存池优化**：自研 `ImageLease` 与分级大小桶缓冲机制，自动限制保留内存（RetainedByteLimit 128 MiB），彻底解决高帧率大分辨率连续扫码的内存堆积问题。

### 4. 强大的跨平台模拟键盘自动键入 (Virtual Keyboard)
- **Windows 平台**：通过 Win32 `SendInput` 发送 Unicode 击键，支持中文、西文与符号直接输入目标进程（如 `notepad.exe`），内置前台窗口焦点防串扰保护。
- **Linux 平台**：通过内核级 `/dev/uinput` 仿真物理硬件键盘驱动，完美适配 Wayland 与 X11：
  - 严格采用现代内核规范 `ioctl(UI_DEV_SETUP)` 与持久单例设备架构，杜绝频繁插拔导致的事件丢失；
  - 增加按键微毫秒级防抖时延（符合 libinput 标准）；
  - 全角标点、空格、数字、字母自动归一化为半角；
  - 支持 Windows CRLF (`\r\n`) 自动归一化合并，在 gedit 等编辑器中平滑输出单次回车，绝不多出空行。

### 5. 工业级多通道可靠输出机制
- **输出通道**：模拟键盘输出、MQTT (QoS 0/1/2)、TCP JSON Lines。
- **可靠性保障**：内置 SQLite 持久化队列、失败自动退避重试、重启自动恢复以及结构化生命周期审计日志。

---

## 发布产物与运行方式

| 交付包 | 适用环境 | 包含内容 | 启动方式 |
| :--- | :--- | :--- | :--- |
| **`ScanFlowOcr-win-x64.zip`** | Windows 10/11 x64 | `ScanFlowOcr.App.exe` (单文件自包含)<br>`libturbojpeg.dll`<br>`appsettings.json` | 双击 `ScanFlowOcr.App.exe` 启动 |
| **`ScanFlowOcr-linux-x64.tar.gz`** | Linux x86_64 (glibc 2.31+) | `ScanFlowOcr.App` (单文件自包含)<br>`native/linux-x64/libturbojpeg.so`<br>`run.sh` / `setup-permissions.sh` | 终端执行 `bash ./run.sh` |

> **Linux 运行前权限提示**：  
> 模拟键盘输出需对 `/dev/uinput` 具有读写权限。可执行 `sudo usermod -aG input $USER` 并配置 udev 规则，或使用临时命令 `sudo chmod 666 /dev/uinput`。

---

## 致谢与开源协议
- 本项目遵循 **Apache-2.0** 开源许可协议。
- 依赖组件及第三方许可声明参见根目录 `THIRD-PARTY-NOTICES.md`。
