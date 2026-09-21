# ScanFlow-OCR (视读单 OCR 系统)

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Avalonia UI](https://img.shields.io/badge/Avalonia-12.1.2-8A2BE2?logo=avalonia)](https://avaloniaui.net/)
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64%20|%20Linux%20x64-blue)](#构建与发布)
[![License](https://img.shields.io/badge/License-Apache%202.0-green.svg)](LICENSE)

**ScanFlow-OCR** 是面向工业视觉、单据识别与桌面自动化录入场景的**跨平台单 OCR 桌面应用**（当前提供 Windows 10/11 x64 与 Linux x64 构建目标）。

本项目专注于**进程内文本识别（OCR）**链路，包含连续相机帧处理、静态图识别、ROI、会话去重和多种输出适配器。文档中的延迟、内存和平台结论均以当前实现为准；具体数值仍需在目标机器、相机和模型组合上复测。

---

## 目录

- [核心特性](#-核心特性)
- [平台与设备边界](#-平台与设备边界)
- [系统架构与数据流](#-系统架构与数据流)
- [关键技术深度](#-关键技术深度)
  - [1. 进程内 OCR (SimdPaddleOCR)](#1-进程内-ocr-simdpaddleocr)
  - [2. 图像缓冲区治理 (ImageLease Size-Class)](#2-图像缓冲区治理-imagelease-size-class)
  - [3. 原生 SIMD 视频捕获与解码 (FlashCap + TurboJPEG)](#3-原生-simd-视频捕获与解码-flashcap--turbojpeg)
  - [4. 双维度耗时监控 (Engine vs Analysis)](#4-双维度耗时监控-engine-vs-analysis)
  - [5. 可视化交互与阅读朝向指示](#5-可视化交互与阅读朝向指示)
  - [6. 跨平台多通道输出队列](#6-跨平台多通道输出队列)
- [代码工程结构](#-代码工程结构)
- [配置文件指南 (appsettings.json)](#-配置文件指南-appsettingsjson)
- [本地数据与日志规范](#-本地数据与日志规范)
- [构建、测试与发布部署](#-构建测试与发布部署)
- [版本历史与演进](#-版本历史与演进)
- [文档索引](#-文档索引)
- [许可证](#-许可证)

---

## 🌟 核心特性

- 🎯 **专注单 OCR 任务**：面向工业视觉与桌面文字识别，提供相机与静态图两类输入。
- ⚡ **有界推帧管线**：相机采集与推理调度异步解耦，并对待处理帧设置容量限制；满载时会按当前策略丢弃旧帧，实际延迟仍取决于驱动、解码、模型和机器性能。
- 🧠 **纯本地推理**：集成 `Sdcb.SimdPaddleOCR` 1.4.2 和 Chinese V6 Tiny/Small 相关模型包，在进程内执行 OCR；应用本身不需要调用外部 OCR 服务。是否联网仍取决于用户配置的输出通道。
- 🛡️ **图像缓冲区治理**：基于 `ImageLease` 的非托管 Size-Class 分配器和闲置 Trim 策略，限制图像缓冲区的保留规模；这不是对整个进程工作集或所有第三方缓存的上限承诺，长时间稳定性需按目标设备验证。
- 🎨 **跨平台桌面 UI**：基于 Avalonia 12.1.2，提供相机预览、ROI 编辑、结果列表、详情和输出设置等界面。
- 🧭 **算法朝向可视化**：图上中心红色箭头（`ReadingAxisArrowBrush`）复现当前 OCR 裁剪、竖排旋转和 CLS 翻转路径计算出的朝向；若分类器判断错误，箭头也会跟随该判断，不应视为独立的真值检测器。
- 🌈 **OCR 检测框 8 色循环配色**：同一帧内多行文本采用 `AnnotationPalette` 独立配色；结果列表、详情和画面标注使用同一组颜色语义。
- ⏱️ **双维度耗时可视化**：HUD 与状态栏分别展示 OCR 引擎耗时和从输入帧到分析结果生成的延迟，便于定位性能瓶颈；后者不等同于 UI 已完成渲染。
- 📦 **多通道输出系统**：
  - **虚拟键盘**：Windows 平台支持 Win32 `SendInput` 及目标进程名匹配（如 `notepad.exe`）；Linux 平台支持 `/dev/uinput` 虚拟键盘直灌。
  - **MQTT**：支持标准 MQTT v3.1.1/v5 协议，可配置 QoS 0/1/2 与 TLS 证书加密。
  - **TCP 裸流**：提供 Raw TCP/JSON Lines 输出，当前 TCP 实现按消息建立连接并以本地写入完成作为接受状态，不包含应用层 ACK 语义。
  - **持久化队列**：内置 SQLite 暂存与重试队列；网络或目标应用不可用时保留待发送记录，但最终送达和重复发送仍受通道协议及现场服务端行为影响。
- 🖼️ **动态 ROI 与静态图分析**：支持交互式百分比 ROI，以及本地 PNG / JPEG / BMP / TIFF 等静态图输入；具体解码能力以当前平台和文件格式支持为准。

---

## 🖥️ 平台与设备边界

- **Windows 相机**：优先尝试 Media Foundation；无可用描述符时按配置回退到 FlashCap 的其他 Windows 后端。应用当前只接受 `JPEG`、`RGB24`、`RGB32` 三类输入描述符；设备只提供 YUYV/NV12 等格式时，可能不会出现在可用相机列表中。
- **Linux 相机**：使用 V4L2 访问 `/dev/video*`，进程需要有打开设备的权限（通常加入 `video` 组）。应用同样只自动接受 JPEG/RGB24/RGB32，驱动是否能协商到这些格式需要在目标设备验证。
- **相机格式转换**：JPEG/MJPEG 通过原生 TurboJPEG 解码；RGB24/RGB32 进入应用的 BGR/BGRA 输入转换路径。TurboJPEG 是 CPU/SIMD 原生库，不代表 GPU 硬件解码。
- **键盘输出**：Windows 使用 `SendInput`；Linux 使用 `/dev/uinput`，当前写入焦点窗口，不负责切换焦点，Phase 1 主要覆盖 ASCII、Tab 和 Enter。
- **可选环境变量**：`SCANFLOW_OCR_TURBOJPEG_PATH` 覆盖 TurboJPEG 动态库路径；`SCANFLOW_OCR_CAMERA_BACKEND` 可选择 `mediafoundation`、`directshow` 或 `vfw`（仅适用于存在对应后端的环境）。原生库来源和校验值见 [`native/ORIGIN.md`](native/ORIGIN.md)。

---

## 🏗️ 系统架构与数据流

ScanFlow-OCR 按分层和单向数据流组织，各模块通过不可变事件（Records）与通道（Channels）进行异步交互：

```mermaid
flowchart TD
    subgraph CaptureLayer["图像输入层"]
        CAM["USB 相机\n(FlashCap)"] -->|当前后端支持的压缩/像素帧| RC["IFrameReceiver\n(ScanSession)"]
        IMG["静态图片文件\n(PNG / JPG / BMP / TIFF)"] -->|文件流| SI["StillImageReader\n(Stb / LibTiff)"]
    end

    subgraph MemoryMgmt["内存池治理层"]
        AL["ImageAllocator\n(非托管连续内存)"] <-->|Size-Class 2^N 申请/回收| LEASE["ImageLease\n(引用计数/切片/共享像素)"]
        TRIM["MaybeTrimIdle\n(周期闲置释放/128MB分配器预算)"] -.-> AL
    end

    subgraph PipelineLayer["调度与处理管道 (ScanFlowOcr.Runtime)"]
        RC -->|有界待处理帧| DCH["Bounded Channel (Capacity=1)"]
        DCH --> DEC["TurboJPEG 原生 SIMD 解码\n(全分辨率原图 / 预览分流)"]
        LEASE -.-> DEC
        DEC --> ROI["ROI 动态区域裁剪与坐标映射"]
        ROI --> OCR["OCR 推理引擎\n(IOcrReader)"]
        SI --> OCR
    end

    subgraph OcrLayer["本地 OCR 推理层 (ScanFlowOcr.Ocr.SimdPaddle)"]
        OCR --> DET["文本行位置检测 (DBNet)"]
        DET --> CLS["文本行朝向分类 (CLS / 0° 或 180°)"]
        CLS --> REC["字符序列识别 (CRNN / SVTR)"]
        REC --> AXIS["ReadingAxisDegrees\n(朝向角度几何解算)"]
    end

    subgraph DedupeLayer["会话与防抖层"]
        AXIS --> DEDUP["DedupeCoordinator\n(Session 会话去重 / Cooldown 冷却时间)"]
    end

    subgraph OutputLayer["持久化与输出层 (ScanFlowOcr.Outputs)"]
        DEDUP --> OUTQ["OutputCoordinator\n(SQLite outputs.db 持久队列)"]
        OUTQ -->|Win32 SendInput / Linux uinput| KB["虚拟键盘 (前台焦点输入)"]
        OUTQ -->|MQTTnet (QoS 0/1/2)| MQTT["MQTT Broker"]
        OUTQ -->|Raw TCP Sockets| TCP["工控 MES/PLC TCP 接收端"]
    end

    subgraph UiLayer["用户呈现层 (ScanFlowOcr.App)"]
        DEC -->|Preview Frame| UI_PREV["Avalonia 实时视频预览"]
        AXIS -->|Quad + ReadingAngle| UI_BOX["8 色高亮框 + 红色朝向箭头"]
        REC -->|OcrResult| UI_LIST["右侧结果卡片列表"]
        RC & OCR -->|EngineTime + AnalysisLatency| UI_HUD["HUD 性能指标监视器"]
    end
```

---

## 🔬 关键技术深度

### 1. 进程内 OCR (SimdPaddleOCR)

- **组件选型**：基于 `Sdcb.SimdPaddleOCR` 1.4.2 封装。该组件在 .NET 进程内执行 PP-OCRv6 相关推理，使用 CPU/SIMD 优化路径；不是独立的 PaddleOCR Python 服务，也不应描述为 Paddle 官方原生运行时。
- **模型组合**：
  - 默认采用 **Chinese V6 Tiny** 模型包；可配置为 **Chinese V6 Small**。准确率和延迟需要按业务图片与目标设备实测。
- **线程与批处理调优**：
  - 通过 `OcrParameters` 暴露 `detIntraOpThreads`、`recBatchLines`、`lineWorkerCount` 等调节参数，具体配置效果需按模型和设备实测。
- **本地推理边界**：OCR 推理本身在宿主进程内完成，不需要部署 Python 环境或外部 HTTP/gRPC OCR 服务；输出到 MQTT/TCP 等通道时，应用仍会按配置访问网络。

### 2. 图像缓冲区治理 (ImageLease Size-Class)

连续推帧会产生较大的图像缓冲区。ScanFlow-OCR 对这部分缓冲区做了显式生命周期管理，但这不等于已经证明整个应用不存在泄漏或工作集永不增长：

1. **Size-Class 阶梯缓冲池**：
   - 摒弃以往针对单一字节精确匹配空闲缓冲的机制，所有图像缓冲区向上对齐至 $2^n$ 大小分类（Size-Class）。
   - 减少 MJPEG 编码帧大小变化导致的细碎闲置块；实际保留规模仍取决于输入尺寸、并发和使用路径。
2. **非托管内存引用计数**：
   - `ImageLease` 包装非托管指针，内部维护严格的引用计数。
   - 在同一份已解码 BGR 缓冲区上创建切片时，预览与 OCR 可以共享底层像素；JPEG 解码、格式转换、预览位图上传等路径仍可能产生拷贝。
3. **周期性动态 TrimExcess**：
   - 当空闲闲置内存超过设定阈值（如 `IdleBytes > RetainedByteLimit / 4`）时，在帧间隔安全时机自动向操作系统归还物理内存。
   - 默认图像分配器保留上限为 **128 MiB**。这是分配器的预算，不是整个进程的内存上限；目标设备的长跑结果应以 Release 实测为准。

### 3. 原生 SIMD 视频捕获与解码 (FlashCap + TurboJPEG)

- **采集跨平台后端**：
  - Windows 环境：优先尝试 `MediaFoundation`，并按实现配置回退到其他 FlashCap 后端。
  - Linux 环境：接入 `/dev/video*` V4L2 设备；设备是否可用取决于驱动和权限。
- **TurboJPEG 3.2.0 原生解码**：
  - 通过 P/Invoke 调用官方编译的 SIMD 优化版 `libturbojpeg` 原生库。
  - **预览分级下采样**：预览流可使用 TurboJPEG 的 1/2、1/4 或 1/8 IDCT 比例缩放，减少预览缓冲区和 UI 上传压力；这是 CPU/SIMD 原生解码路径，不是 GPU/硬件解码承诺。OCR 识别流按配置保留所需分辨率。

### 4. 双维度耗时监控 (Engine vs Analysis)

为了帮助定位图像处理节拍，系统在 UI 展示两个不同范围的耗时指标：

| 指标名称 | 字段来源 | 统计范围 | 呈现方式 |
| :--- | :--- | :--- | :--- |
| **OCR 引擎纯推理耗时** (Engine Time) | `StageAnalysis.EngineTime` | 纯 OCR 模型的检测、CLS 分类与字符识别耗时（不含图像解码、ROI 裁剪与跨线程排队） | 顶部 HUD `· OCR xx ms`、列表项详情 |
| **输入到分析结果延迟** (Analysis Latency) | `CompletedTimestamp - ReceivedTimestamp` | 从相机驱动投递原始帧、解码、ROI 处理、坐标变换到运行时分析结果生成；不包含 Avalonia 最终绘制完成时间 | 底部状态栏指标区、EMA 平滑滤波 |

### 5. 可视化交互与阅读朝向指示

- **8 色循环调色板 (`AnnotationPalette`)**：
  - 单帧画面内同时检出的多行文字，按索引循环赋予 8 种高对比度柔和颜色。
  - 结果列表、详情卡片和画面标注复用同一组颜色语义。当前画面框本身不作为独立的命中选择源，列表/结果卡片交互与画面高亮的联动以当前 UI 实现为准。
- **算法朝向红色箭头**：
  - 中心绘制的高对比度红色箭头（`ReadingAxisArrowBrush`）直接映射自 `OcrLine.ReadingAngleDegrees`。
  - 角度由当前实现的四点裁剪、长宽比判定（高度/宽度 $\ge 1.5$ 时顺时针旋转 90°）和 CLS 0°/180° 结果复原得到。它表示“算法采用的朝向”，不是独立的人工真值；CLS 判断错误时箭头也会跟随错误判断。

### 6. 跨平台多通道输出队列

- **Windows 虚拟键盘**：
  - 采用 Win32 `SendInput` API，原生支持全量 Unicode 字符投递。
  - 支持前台目标进程过滤（如配置 `notepad.exe`），只有在用户切至指定程序时才录入，避免向错误窗口误发。
- **Linux 虚拟键盘**：
  - 原生直写 `/dev/uinput` 虚拟输入设备，支持标准 ASCII 及 Tab/Enter 键。
  - 自动将全角字符规范化为半角，规整 `\r\n` 回车符，并写入当前焦点编辑器（如 gedit）。
- **工业 TCP 直推**：
  - 采用 Raw TCP Socket + JSON Lines 形式向工业 PLC / MES 采集网关发送；当前实现不提供 TLS 和应用层 ACK，服务端协议需由现场系统自行约定。
- **SQLite 断网离线重试 (`outputs.db`)**：
  - 输出记录先写入本地 SQLite 队列，再由对应发送器处理；失败或暂时不可用时可重试。MQTT 的 QoS 1/2 可使用 broker 确认，QoS 0 和键盘/TCP 不提供端到端送达证明，因此不能把该队列描述为绝对零丢失或绝对不重复。

---

## 📂 代码工程结构

项目采用现代 .NET 10 单解决方案组织，遵循接口隔离与单向依赖原则：

```
f:\Projects\ScanFlow-OCR\
├── ScanFlowOcr.slnx                     # 现代化 .NET 解决方案文件
├── Directory.Build.props                # 集中构建属性与编译参数
├── Directory.Packages.props             # 集中 NuGet 包依赖版本控制
├── global.json                          # 锁定 .NET SDK 10
├── native/                              # 原生 libjpeg-turbo 动态库
│   ├── win-x64/                         # Windows x64: turbojpeg.dll
│   └── linux-x64/                       # Linux x64: libturbojpeg.so
├── src/
│   ├── ScanFlowOcr.Contracts/           # 核心领域契约 (IOcrReader, IImageLease, ScanRecord, Quad)
│   ├── ScanFlowOcr.Imaging/             # 图像解码/格式转换/TurboJPEG 封装/ImageAllocator 内存池
│   ├── ScanFlowOcr.Capture.FlashCap/    # 跨平台 USB 相机采集实现 (Windows MF / Linux V4L2)
│   ├── ScanFlowOcr.Ocr.SimdPaddle/      # SimdPaddleOCR 适配器、模型加载与朝向角度几何解算
│   ├── ScanFlowOcr.Outputs/             # 虚拟键盘 (SendInput/uinput)、MQTT、TCP、SQLite 队列
│   ├── ScanFlowOcr.Runtime/             # 扫描会话编排 (ScanSession)、去重管理与流水线控制器
│   └── ScanFlowOcr.App/                 # Avalonia 12 跨平台桌面端应用 (MVVM、UI、ROI 编辑、主题)
├── tests/
│   └── ScanFlowOcr.SmokeTests/          # 自动化全套冒烟测试 (覆盖几何、内存池、解码、OCR、输出通道)
├── docs/                                # 项目详细技术与发布文档
│   ├── RELEASE-1.0.0.md                 # v1.0.0 正式版发布说明
│   ├── RELEASE-1.0.1.md                 # v1.0.1 修正版发布说明
│   ├── MEMORY-OPTIMIZATION.md           # 连续推帧内存治理与验证说明
│   └── PROJECT-OVERVIEW.md              # 架构设计与实现边界说明
└── scripts/                             # 自动化多平台发布脚本
    ├── Publish-Win-x64.ps1              # Windows 发布脚本 (Framework-Dependent / SelfContained / 实验性 AOT)
    ├── Publish-Linux-x64.ps1            # Linux 交叉发布脚本 (PowerShell)
    └── publish-linux-x64.sh             # Linux 原生 Bash 发布脚本
```

---

## ⚙️ 配置文件指南 (appsettings.json)

应用主配置文件位于程序目录下的 `appsettings.json`，各项关键参数释义如下：

```jsonc
{
  "ScanFlowOcr": {
    "// --- 区域与预览配置 ---": "",
    "EnableRoi": true,                   // 是否启用 ROI 区域限制识别
    "ShowPreviewGuides": true,           // 是否在画面上显示 ROI 边框与参考网格
    "RoiX": 10,                          // ROI 左上角 X 百分比 (0-100)
    "RoiY": 10,                          // ROI 左上角 Y 百分比 (0-100)
    "RoiWidth": 80,                      // ROI 宽度百分比 (0-100)
    "RoiHeight": 80,                     // ROI 高度百分比 (0-100)
    "PreviewEnabled": true,              // 是否开启 UI 实时预览流
    "PreviewMaxFps": 15,                 // 预览最大渲染帧率限制 (降低 UI 渲染负载)
    "PreviewMaxWidth": 1280,             // 预览画面最大宽度 (可启用 TurboJPEG IDCT 比例缩放)
    "PreviewMaxHeight": 720,             // 预览画面最大高度

    "// --- 会话与去重过滤 ---": "",
    "DedupeMode": "Session",             // 去重模式: Session(会话内完全去重) / Cooldown(时间窗口冷却) / UntilAbsent(离开视野重置)
    "DedupeIntervalMs": 1500,            // Cooldown 或 UntilAbsent 模式下的时间窗口阈值 (毫秒)
    "DedupeMaxEntries": 10000,           // 去重集合上限，防止内存无限累加

    "// --- OCR 引擎算法参数 ---": "",
    "OcrLayout": "TextBlock",            // 布局格式: TextBlock / SingleLine / SparseText
    "OcrTimeoutMs": 5000,                // 单帧 OCR 推理最大超时阈值 (毫秒)
    "OcrParameters": {
      "model": "tiny",                   // 模型等级: "tiny"(默认) 或 "small"(替代模型，性能需实测)
      "detectSideLength": "640",         // DBNet 检测图像最长边限制 (默认 640)
      "useDirectionClassification": "true", // 是否启用 CLS 文本方向分类 (0°/180° 自适应矫正)
      "lineWorkerCount": "1",            // 文本行识别并发 Worker 线程数
      "detIntraOpThreads": "2",          // 检测模型单算子并发线程数
      "recBatchLines": "1",              // 文本行识别 Batch 批大小
      "minConfidencePercent": "90"       // 结果置信度过滤阈值 (百分比)
    },

    "// --- 输出通道: 虚拟键盘 ---": "",
    "KeyboardEnabled": false,            // 是否启用虚拟键盘输出
    "KeyboardTargetProcess": "notepad.exe", // 目标进程名过滤 (仅 Windows 有效，留空则不限制)
    "KeyboardSuffix": "Enter",           // 文本录入后缀: "Enter" / "Tab" / "None"
    "KeyboardSendMode": "Combined",      // 发送模式: "Combined" (单帧拼接发送) 或 "PerLine" (逐行发送)
    "KeyboardSeparator": " | ",          // 多行文本拼接时的分隔符

    "// --- 输出通道: MQTT ---": "",
    "MqttEnabled": false,                // 是否启用 MQTT 输出
    "MqttBroker": "localhost",           // Broker 地址
    "MqttPort": 1883,                    // Broker 端口
    "MqttTls": false,                    // 是否启用 TLS 加密通信
    "MqttTopic": "scanflow-ocr/scans",   // 消息投递主题
    "MqttQos": 1,                        // 投递服务质量 (0, 1, 2)

    "// --- 输出通道: 工控 TCP ---": "",
    "TcpEnabled": false,                 // 是否启用工业 TCP 裸流输出
    "TcpHost": "127.0.0.1",              // 目标服务器 IP
    "TcpPort": 9100,                     // 目标端口 (Raw JSON Lines 格式)

    "// --- 队列与容灾缓冲 ---": "",
    "OutputQueueCapacity": 1000          // 发送调度容量相关配置，非端到端送达保证
  }
}
```

上例使用 JSONC 注释来解释字段；实际 `appsettings.json` 需要按应用配置格式提供，若解析器不接受注释请删除行尾注释和说明键。

---

## 📂 本地数据与日志规范

应用运行期间的动态数据、离线持久化重试库与滚动日志统一存放于操作系统标准数据目录下：

| 操作系统平台 | 数据与持久化存储路径 |
| :--- | :--- |
| **Windows x64** | `%LOCALAPPDATA%\ScanFlowOcr\`（例如 `C:\Users\<User>\AppData\Local\ScanFlowOcr\`） |
| **Linux x64** | `~/.local/share/ScanFlowOcr/` |

### 核心目录及文件说明

- `logs/scanflow-ocr-YYYYMMDD.log`：按天自动轮转的结构化文本日志，具备异常堆栈记录与自动归档。
- `outputs.db`：基于 SQLite 的本地待发送记录文件，负责失败重试和排队暂存；不同输出通道的确认语义不同。
- `appsettings.json`（回退配置）：当程序运行根目录不可写时，系统会使用此目录中的回退配置；它不表示主目录和回退目录始终同时写入。

---

## 🛠️ 构建、测试与发布部署

### 1. 开发环境要求

- **操作系统**：Windows 10/11 x64 或 Linux x64（发布脚本以 Ubuntu 22.04+ / Debian 12+ 等环境为目标；实际运行仍需验证 glibc、驱动、相机和输入设备权限）。
- **.NET SDK**：**.NET 10.0 SDK**（见根目录 `global.json`）。
- **C++ 运行库**：Windows 需具备 Visual C++ 2015-2022 Redistributable；Linux 需具备标准 glibc 与 libstdc++。

### 2. 编译与执行

```bash
# 1. 还原并构建整个解决方案
dotnet build ScanFlowOcr.slnx

# 2. 运行工程冒烟测试（检查数量随源码变化，以命令输出为准）
dotnet run --project tests/ScanFlowOcr.SmokeTests

# 3. 运行本地相机探测模式 (检测系统可用相机、格式与分辨率)
dotnet run --project tests/ScanFlowOcr.SmokeTests -- --camera

# 4. 启动 Avalonia 桌面主应用
dotnet run --project src/ScanFlowOcr.App
```

### 3. 多平台发布矩阵

仓库提供面向 Windows 与 Linux 的全自动化发布工具链（位于 `scripts/`）：

| 平台模式 | 发布命令 | 产物形态 | 目标环境要求 |
| :--- | :--- | :--- | :--- |
| **Windows 框架依赖 (推荐)** | `pwsh scripts/Publish-Win-x64.ps1` | `ScanFlowOcr.App.exe`（典型约 50MB，以本次产物为准） | 需预装 .NET 10 Desktop Runtime x64 |
| **Windows 自包含单文件** | `pwsh scripts/Publish-Win-x64.ps1 -SelfContained` | `ScanFlowOcr.App.exe`（典型约 130MB，以本次产物为准） | 无需安装 .NET；仍需验证目标机原生库和设备环境 |
| **Windows Native AOT（实验性）** | `pwsh scripts/Publish-Win-x64.ps1 -Aot` | 原生静态二进制 | 需 VS C++ x64 原生编译链；当前不作为默认验证路径 |
| **Linux 自包含单文件** | `bash scripts/publish-linux-x64.sh` | `ScanFlowOcr.App`（典型约 120MB） + `run.sh` | 无需装 .NET；仍需验证 glibc、原生库和设备权限 |
| **Linux Native AOT（实验性）** | `bash scripts/publish-linux-x64.sh --aot` | 原生静态 ELF | 需宿主机安装 clang, zlib, glibc-devel；当前不作为默认验证路径 |

> [!TIP]
> **Linux 键盘权限设置**：
> Linux 下虚拟键盘依赖 `/dev/uinput` 节点权限，建议执行：
> ```bash
> sudo usermod -aG input $USER
> # 发布目录也会生成 run.sh / setup-permissions.sh 处理文件执行权限；
> # /dev/uinput 建议用 input 组或 udev 规则管理，临时 chmod 只适合诊断。
> ```

---

## 📈 版本历史与演进

- **[Unreleased (当前最新代码)]**：
  - ✨ **OCR 检测框 8 色循环配色**：引入 `AnnotationPalette`，逐框独立着色，结果列表与画面标注复用颜色语义。
  - ⏱️ **双维度耗时呈现**：HUD 与状态栏拆分展示 OCR 纯推理耗时（`EngineTime`）与输入到分析结果的延迟（`CompletedTimestamp - ReceivedTimestamp`）。
  - 🎯 **阅读方向红箭头**：引入 `ReadingAxisArrowBrush`，朝向箭头固定为鲜明红色，避免与逐框配色混淆。
  - 🧹 **精简 TCP 输出**：TCP 路由不再暴露 TLS 配置项；MQTT TLS 保持不变。
- **[v1.0.1] - 2026-09-20**：
  - 🐛 **复原阅读方向路径**：按透视校正、宽高比竖排（$\ge 1.5$）顺时针转正及 CLS 翻转路径计算 `ReadingAngleDegrees`；该角度表示当前引擎采用的朝向，不是独立真值。
  - 暴露 `OcrLine.ReadingAngleDegrees` 并在详情界面直观展示角度值。
- **[v1.0.0] - 2026-09-20**：
  - 🚀 跨平台首发基线：Avalonia 桌面端、SimdPaddleOCR 中文模型包、FlashCap 相机驱动、TurboJPEG 原生 SIMD 解码、SQLite 重试队列与发布脚本。

详细变更请查阅 [CHANGELOG.md](CHANGELOG.md)。

---

## 📚 文档索引

- 📖 [项目架构与实现边界说明](docs/PROJECT-OVERVIEW.md)：说明单向数据管道、内存治理、几何计算与输出确认语义。
- 💾 [单进程内存优化记录](docs/MEMORY-OPTIMIZATION.md)：记录 Size-Class 分配器、周期 Trim、已完成验证与待验证项目。
- 📦 [自动化发布脚本使用指南](scripts/README.md)：Windows 与 Linux 各模式发布、打包与权限排查手册。
- 📝 [v1.0.1 修正版说明](docs/RELEASE-1.0.1.md) / [v1.0.0 正式版说明](docs/RELEASE-1.0.0.md)。

---

## 📄 许可证

本项目遵循 **Apache-2.0** 许可证开源，完整内容请参阅 [LICENSE](LICENSE)。
第三方开源组件使用授权与说明详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
