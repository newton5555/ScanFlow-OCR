# ScanFlow-OCR v1.0.1 Release Notes

> 发布日期：2026-09-20  
> 支持平台：Windows x64 / Linux x64（Ubuntu 22.04+ / Debian 12+ 等 glibc 发行版）  
> 运行时：.NET 10.0（自包含单文件；目标机无需预装 .NET）

ScanFlow-OCR v1.0.1 是 **1.0.0 之后的修正版**，重点对齐 OCR 阅读方向可视化与引擎真实朝向，便于对照 CLS / 裁剪链路问题。

---

## 本版变更

### 阅读方向箭头对齐引擎
- 图上「中心点 + 方向箭头」按 `Sdcb.SimdPaddleOCR` 裁剪语义计算：透视拉正 → 竖排（高宽比 ≥ 1.5）顺时针转正 → CLS `AppliedRotationDegrees`（0/180）
- 箭头与送入识别器的朝向一致：识别对则箭头对；CLS 翻错时箭头也会反，便于对照排查
- 对外透出 `OcrLine.ReadingAngleDegrees`；结果详情 Bounds 旁显示角度

### 继承 1.0.0
其余能力与 v1.0.0 相同（单 OCR、Avalonia UI、FlashCap + TurboJPEG、键盘 / MQTT / TCP 输出等）。详见 `docs/RELEASE-1.0.0.md`。

---

## 数据与日志路径

| 平台 | 目录 |
|------|------|
| Linux | `~/.local/share/ScanFlowOcr/` |
| Windows | `%LOCALAPPDATA%\ScanFlowOcr\` |

内容包括：`logs/scanflow-ocr-YYYYMMDD.log`、`outputs.db`、以及 `appsettings.json`。

---

## 下载与运行

| 包名 | 目标 | 主要内容 | 运行方式 |
|------|------|----------|----------|
| `ScanFlowOcr-win-x64.zip` | Windows 10/11 x64 | `ScanFlowOcr.App.exe`、TurboJPEG、内置模型 | 双击运行 |
| `ScanFlowOcr-linux-x64.tar.gz` | Linux x86_64（glibc 2.31+） | `ScanFlowOcr.App`、`native/linux-x64/libturbojpeg.so`、`run.sh` / `setup-permissions.sh` | `bash ./run.sh` |

**Linux 键盘权限提示：**  
确保对 `/dev/uinput` 可写（加入 `input` 组并重新登录，或临时 `sudo chmod 666 /dev/uinput`）。键盘输出写入**当前焦点窗口**，不会按进程名切焦点。

---

## 许可

- 本项目：**Apache-2.0**（见 `LICENSE`）
- 第三方组件清单见 `THIRD-PARTY-NOTICES.md`