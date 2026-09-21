# ScanFlow-OCR 发布脚本使用指南

本项目提供面向 **Windows x64** 与 **Linux x64** 的发布脚本。脚本能生成对应目标框架的发布目录；相机、窗口系统、输入设备和长跑性能仍需在目标系统单独验收。

---

## 1. 快速命令

### Windows x64 发布
```powershell
# 方式 1: 直接在根目录执行 CMD 包装（自动检测 pwsh / powershell）
./publish-win-x64.cmd

# 方式 2: 使用 PowerShell 7 执行（默认：依赖框架 + 单 EXE 打包）
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
| **Windows x64** | **依赖框架 (Framework-Dependent)** | 是 (`ScanFlowOcr.App.exe`) | 典型主程序约 50MB，需以本次产物复测 | 目标机器需已安装 `.NET 10 Desktop Runtime x64` |
| **Windows x64 (自包含)** | 自包含 (`-SelfContained`) | 是 (`ScanFlowOcr.App.exe`) | 典型主程序约 130MB，需以本次产物复测 | 无需安装 .NET；仍需验证原生库、驱动和 UI 环境 |
| **Linux x64** | **自包含 (Self-Contained)** | 是 (`ScanFlowOcr.App`) | 典型主程序约 120MB，需以本次产物复测 | 无需安装 .NET 运行时；仍需验证 glibc、原生库和设备权限 |

> [!NOTE]
> **关于裁剪 (Trimming) 与 Native AOT 说明**：
> 1. 在 .NET 官方规范中，`PublishTrimmed=true`（程序集裁剪优化）**强制要求必须是自包含应用**，框架依赖模式不支持裁剪（触发 NETSDK1102）。
> 2. 框架依赖模式本身已不携带 .NET BCL 基础类库，其体积已处于极佳水平。
> 3. Native AOT (`PublishAot=true`) 会尝试生成原生二进制，但当前脚本路径属于实验性选项，受 Avalonia、SQLite、反射和原生依赖兼容性影响，不是默认发布或 CI 验收路径。如需试验，再追加 `-Aot` 或 `--aot` 并自行验证。

---

## 3. 发布交付目录结构

发布成功后产物按平台分别输出至仓库根目录的 `publish/` 对应平台目录下（已被 `.gitignore` 自动忽略）：

### Windows x64 产物结构 (`F:\Projects\ScanFlow-OCR\publish\win-x64\`)
```
publish/win-x64/
├── ScanFlowOcr.App.exe       # 主可执行文件（所有托管程序集单文件打包；体积以本次构建为准）
├── appsettings.json          # 应用配置
├── e_sqlite3.dll             # SQLite 原生引擎（输出持久化队列）
├── libSkiaSharp.dll          # Skia 绘图引擎（Avalonia UI 渲染）
├── libHarfBuzzSharp.dll      # 字体排印引擎
├── av_libglesv2.dll          # OpenGLES 图形库
└── native/
    └── win-x64/
        └── turbojpeg.dll     # libjpeg-turbo 原生 CPU/SIMD 解压库
```

### Linux x64 产物结构 (`F:\Projects\ScanFlow-OCR\publish\linux-x64\`)
```
publish/linux-x64/
├── ScanFlowOcr.App           # Linux ELF 独立可执行程序（自包含，无需安装 .NET）
├── run.sh                    # 启动脚本（尝试赋予主程序执行权限并设置 LD_LIBRARY_PATH）
├── setup-permissions.sh      # 权限初始化脚本（为目录中所有程序、脚本与动态库赋予 755 权限）
├── appsettings.json          # 应用配置
├── libe_sqlite3.so           # SQLite 原生动态库
├── libSkiaSharp.so           # Linux Skia 动态库
├── libHarfBuzzSharp.so       # 字体排印动态库
└── native/
    └── linux-x64/
        └── libturbojpeg.so   # Linux libjpeg-turbo 原生 CPU/SIMD 解压库
```

#### Linux 运行方法：
```bash
# 推荐：直接使用启动脚本（会自动赋权并配置动态库路径）
bash ./run.sh

# 或者：先执行一次全局赋权，再直接运行二进制
bash ./setup-permissions.sh
./ScanFlowOcr.App
```
---

## 4. Linux 运行与键盘输出提示

- 推荐用发布目录内 `bash ./run.sh` 启动（会设置 `LD_LIBRARY_PATH`，并尝试为主程序补充执行权限）。生产环境仍建议使用用户/组或 udev 规则管理设备权限。
- 键盘输出依赖 `/dev/uinput`；用户宜加入 `input` 组。
- **Linux 不按进程名切焦点**，键入当前焦点窗口；测 gedit 时请先点进文档。
- 应用日志默认：`~/.local/share/ScanFlowOcr/logs/`。
