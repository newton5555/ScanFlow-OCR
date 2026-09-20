# ScanFlow-OCR 发布脚本使用指南

本项目提供面向 **Windows x64** 与 **Linux x64** 的全自动化发布脚本。

---

## 1. 快速命令

### Windows x64 发布
```powershell
# 方式 1: 直接在根目录执行 CMD 包装（自动检测 pwsh / powershell）
./publish-win-x64.cmd

# 方式 2: 使用 PowerShell 7 执行（默认：依赖框架 + 单 EXE 打包，体积小巧轻量）
pwsh -File ./scripts/Publish-Win-x64.ps1

# 方式 3: 切换为自包含模式（无需目标机安装 .NET 运行时，独立执行）
pwsh -File ./scripts/Publish-Win-x64.ps1 -SelfContained

# 方式 4: 实验性 Native AOT 模式（需机器安装 Visual Studio C++ x64 编译工具链）
pwsh -File ./scripts/Publish-Win-x64.ps1 -Aot
```

### Linux x64 发布
```bash
# 方式 1: 在 Windows 或 Linux 上通过 PowerShell 交叉编译发布 Linux 包（默认：自包含单文件）
pwsh -File ./scripts/Publish-Linux-x64.ps1

# 方式 2: 在 Linux 原生终端中执行 Bash 脚本
bash ./publish-linux-x64.sh

# 方式 3: Linux 本地 Native AOT 原生编译模式（需宿主机安装 clang, zlib, glibc）
bash ./publish-linux-x64.sh --aot
```

---

## 2. 模式与技术边界说明

| 发布平台 | 默认模式 | 单文件打包 | 体积特点 | 运行环境要求 |
| :--- | :--- | :--- | :--- | :--- |
| **Windows x64** | **依赖框架 (Framework-Dependent)** | 是 (`ScanFlowOcr.App.exe`) | **约 50MB**（剔除 BCL 基础运行时） | 目标机器需已安装 `.NET 10 Desktop Runtime x64` |
| **Windows x64 (自包含)** | 自包含 (`-SelfContained`) | 是 (`ScanFlowOcr.App.exe`) | 约 130MB | 无需安装任何 .NET，在 Windows 10/11 x64 直接双击运行 |
| **Linux x64** | **自包含 (Self-Contained)** | 是 (`ScanFlowOcr.App`) | 约 120MB | 无需安装 .NET 运行时，赋予 `chmod +x` 后直接运行 |

> [!NOTE]
> **关于裁剪 (Trimming) 与 Native AOT 说明**：
> 1. 在 .NET 官方规范中，`PublishTrimmed=true`（程序集裁剪优化）**强制要求必须是自包含应用**，框架依赖模式不支持裁剪（触发 NETSDK1102）。
> 2. 框架依赖模式本身已不携带 .NET BCL 基础类库，其体积已处于极佳水平。
> 3. Native AOT (`PublishAot=true`) 本质永远是独立原生二进制（自带 AOT 微内核，天然自包含）。如需开启 Native AOT，在脚本后追加 `-Aot` 或 `--aot` 即可。

---

## 3. 发布交付目录结构

发布成功后产物默认直接输出至仓库根目录的 `publish/` 目录下（已被 `.gitignore` 自动忽略）：

### Windows x64 产物结构 (`F:\Projects\ScanFlow-OCR\publish\`)
```
publish/
├── ScanFlowOcr.App.exe       # 主可执行文件（所有托管程序集单文件打包，约 50MB）
├── appsettings.json          # 应用配置
├── e_sqlite3.dll             # SQLite 原生引擎（输出持久化队列）
├── libSkiaSharp.dll          # Skia 绘图引擎（Avalonia UI 渲染）
├── libHarfBuzzSharp.dll      # 字体排印引擎
├── av_libglesv2.dll          # OpenGLES 图形库
└── native/
    └── win-x64/
        └── turbojpeg.dll     # libjpeg-turbo 硬件加速解压库
```

### Linux x64 产物结构 (`F:\Projects\ScanFlow-OCR\publish\`)
```
publish/
├── ScanFlowOcr.App           # Linux ELF 独立可执行程序（需 chmod +x，约 120MB）
├── appsettings.json          # 应用配置
├── libe_sqlite3.so           # SQLite 原生动态库
├── libSkiaSharp.so           # Linux Skia 动态库
├── libHarfBuzzSharp.so       # 字体排印动态库
└── native/
    └── linux-x64/
        └── libturbojpeg.so   # Linux libjpeg-turbo 动态库
```
