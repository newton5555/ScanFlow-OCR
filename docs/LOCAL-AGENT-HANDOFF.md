# ScanFlow-OCR — 本地 Windows Agent 交接说明 / Local Agent Handoff

**仓库:** https://github.com/newton5555/ScanFlow-OCR  
**分支:** `main`  
**产品定位:** 私有 ScanFlow 的 **OCR-only 公开分支**。**仅移除条码解码**（DecodeP1 / Phenix / CoreHost 条码路径 / MemoryModule / 码制 UI / 条码 IPC）。其余仍对 OCR 有意义的产品能力应保留或追平。

**对照源:**
- 私有仓库: `newton5555/ScanFlow`（GitHub，private）
- 本地机（可选）: `F:\Projects\ScanFlow` on machine `298b697c-9396-413b-865d-0c428a28a28a`
- UI 原型: 私有库 `prototype/` + `doc/wpf-ui.md`

---

## 关键 commits（特征补齐相关）

| SHA | 说明 |
|-----|------|
| `abf4ed5` | **feat:** settings 持久化、ROI、连续 OCR ScanSession、去重、结果搜索/详情、缩略图列表、预览缩放/准星 |
| `8a38e2d` | UI 工作台对齐 desktop/prototype 视觉骨架 |
| `316311b` | **重要:** 相机预览 UI 更新必须 marshal 到 Avalonia `UIThread` |
| `479841f` | TurboJPEG 3.2.0 natives + 相机预览 |
| `4262375` | Avalonia 静态图 OCR + sinks |
| `ca9214a` | MQTT / TCP + OutputCoordinator |
| `b6faa7b` | Phase 1 Avalonia + FlashCap + SimdPaddle + keyboard |

以 `git log --oneline -15` 为准；推送后远端 SHA 可能多一个 handoff commit。

---

## 产品范围（务必遵守）

### 永久排除（Do-not-touch）
- DecodeP1 / Phenix SDK
- CoreHost 条码路径 / 条码 IPC（`IpcBarcodeReader*`）
- MemoryModule / NativeLoading 注入加载
- 码制（symbology）UI、条码参数面板
- Clipboard 自动输出（用户此前明确 **deferred**，除非为非条码功能对等需要）

### 应有能力（OCR 路径）
- 设置窗口 + `appsettings.json` 持久化（相机偏好、OCR Tiny/Small、ROI、去重、预览、MQTT/TCP/键盘）
- ROI 叠加层 + 启用开关；会话 OCR 时裁剪 ROI
- **连续扫描会话**（`ScanFlowOcr.Runtime.ScanSession`）+ 去重：`Session` / `Cooldown` / `UntilAbsent` / `IntraFrame`
- 预览 chrome：缩放、准星/辅助线、会话状态 HUD
- 结果列表：搜索/过滤、详情 inspector
- 图像 playlist + 缩略图
- 输出：MQTT / TCP / keyboard（已有）

---

## 远端已落地（本轮 `abf4ed5`）

| 能力 | 状态 |
|------|------|
| `ScanFlowOcr.Runtime.ScanSession` OCR-only | ✅ 从私有 Runtime 移植，去掉条码路径 |
| Settings UI + SettingsManager 持久化 | ✅ `SettingsWindow` + `appsettings.json`（`ScanFlowOcr` 节） |
| ROI 百分比叠加 + 会话裁剪 | ✅ 设置启停；叠加为视口百分比近似（非完整拖拽编辑器） |
| 连续扫描 Start/Stop | ✅ 消费 `ReadPreviewAsync` / `ReadEventsAsync` |
| 去重 Session/Cooldown/UntilAbsent/IntraFrame | ✅ Runtime + 设置 UI |
| 预览缩放 / 准星 | ✅ 滚轮、±、双击复位；准星开关 |
| 结果搜索 + inspector | ✅ |
| Playlist 缩略图 | ✅ Avalonia `Bitmap.DecodeToWidth` |
| 仅预览 + 单帧 OCR | ✅ 保留 `CameraPreviewController`（316311b 线程安全） |
| MQTT/TCP/keyboard | ✅ 此前已有；设置与工具栏快捷应用 |

**构建:** `dotnet build ScanFlowOcr.slnx -c Release` 绿；smoke 通过。

---

## 相对私有 ScanFlow / prototype 仍缺失（留给本地 Agent）

1. **视觉对等** — 对照 `F:\Projects\ScanFlow` WPF Desktop + `prototype/`：viewport chrome、ROI **拖拽编辑器**、结果 toast、状态 pill 配色和 FPS/丢帧 HUD 已补齐；后续只需按真机截图继续微调。
2. **真机相机验证** — Linux CI 无摄像头；Windows 默认先试 MF MJPEG，无可用设备时回退 FlashCap 旧 Windows 后端（DirectShow / VfW）；仍需在真机验证 TurboJPEG → 连续 OCR → 去重 → 输出。
3. **键盘打入业务窗口** — 验证 `SendInput` 打入记事本/业务 App；Linux `/dev/uinput` 权限与 ASCII 限制。
4. **ROI 编辑体验** — 已支持画面内拖拽框选、区域移动、四角调整、全画面/取消/保存、Esc/Enter 快捷键；设置仍保留百分比入口。
5. **Playlist 作为“本地图像源设备”** — 私有 `DesktopCameraProvider.ImportPlaylistAsync` 可进会话循环；公开版仍是静态图列表 + 手动「识别图片」。
6. **UntilAbsent 等高级 HUD** — 去重模式已有；FPS/丢帧计时器、主窗口位置/尺寸/最大化记忆已补齐，仍可继续丰富 HUD 细节。
7. **打包** — Windows folder publish 已验证路径并随包复制 `native/win-x64/turbojpeg.dll`；安装包 / 单文件发布仍未做。
8. **Clipboard** — 保持 deferred。

---

## 本地 Agent 建议任务清单

1. `git pull` `main`，Release 构建并启动 App。
2. 对照 `F:\Projects\ScanFlow` + `prototype/index.html` 做视觉与交互对等（**仅 OCR 相关面板**）。
3. 接真摄像头跑「启动扫描」：确认预览线程安全、ROI 生效、去重模式、MQTT/TCP/键盘。
4. 实现或完善 ROI 画面内编辑（可移植私有 RoiEditor 思路到 Avalonia）。
5. （可选）把图片 playlist 接入会话源，接近私有 Replay/LocalImage 行为。
6. 准备 Windows 打包（含 `native/win-x64/turbojpeg.dll`）。
7. **不要**引入 Phenix / DecodeP1 / CoreHost / MemoryModule / 码制 UI。

---

## 构建 / 运行

```bash
# SDK 10（见 global.json）
dotnet build ScanFlowOcr.slnx -c Release
dotnet run --project tests/ScanFlowOcr.SmokeTests -c Release
dotnet run --project src/ScanFlowOcr.App -c Release
```

### TurboJPEG
- `native/win-x64/turbojpeg.dll`
- `native/linux-x64/libturbojpeg.so`
- 或环境变量 `SCANFLOW_OCR_TURBOJPEG_PATH`
- App 构建时复制到输出目录 `native/{rid}/`
- **静态图 OCR 不需要** TurboJPEG；**相机 MJPEG / 连续扫描需要**

### Windows camera backend
默认优先 Media Foundation；若没有可用 MJPEG 描述符，会回退 FlashCap 默认 Windows 后端集合（DirectShow / Video for Windows）。诊断驱动时可设置 `SCANFLOW_OCR_CAMERA_BACKEND=mediafoundation`、`directshow` 或 `vfw`（`videoforwindows`）。

### 线程安全（相机 UI）
相机预览与会话预览帧的 `WriteableBitmap` / `Image.Source` 更新 **必须** 在 Avalonia `Dispatcher.UIThread` 上（见 commit `316311b` 与 `CameraPreviewController` / `ConsumePreviewAsync` → `ProcessPendingPreview`）。不要在 FlashCap 回调线程直接碰 UI。

### 配置路径
- 优先: 程序目录 `appsettings.json`
- 回退: `%LOCALAPPDATA%\ScanFlowOcr\appsettings.json`（Windows）或等效 LocalApplicationData

### 解决方案项目（`ScanFlowOcr.slnx`）
```
src/ScanFlowOcr.Contracts
src/ScanFlowOcr.Imaging
src/ScanFlowOcr.Capture.FlashCap (+ vendor FlashCap)
src/ScanFlowOcr.Ocr.SimdPaddle
src/ScanFlowOcr.Outputs
src/ScanFlowOcr.Runtime          ← OCR ScanSession
src/ScanFlowOcr.App              ← Avalonia UI
tests/ScanFlowOcr.SmokeTests
```

---

## English summary

Public **ScanFlow-OCR** is an OCR-only fork: barcode decode stacks are gone forever; everything else useful for OCR (settings, ROI, continuous session + dedupe, preview chrome, results, playlist) should exist. Remote work landed Runtime + settings + session loop + chrome in `abf4ed5`. Local Windows agent should polish visual parity vs private Desktop/prototype, validate real cameras/keyboard/packaging, and optionally port full ROI editor / playlist-as-source — without touching DecodeP1/Phenix/CoreHost/MemoryModule.
