# ScanFlow-OCR · Avalonia UI 样式对齐与改进计划

> 对照基准：设计原型 `F:\Projects\ScanFlow\prototype\`（设计源）、`F:\Projects\ScanFlow\src\ScanFlow.Desktop`（WPF 已实现参考）、当前 `src\ScanFlowOcr.App`（Avalonia 待改进）。
> 产品范围：本仓库是 **OCR-only** 分支。仅移植视觉语言与交互样式，**不得**引入条码 / 码制 / DecodeP1 / Phenix / CoreHost / MemoryModule 相关 UI（见 `docs/LOCAL-AGENT-HANDOFF.md`）。

---

## 1. 结论摘要

Avalonia 版的功能骨架（工作台、ROI、连续会话、结果列表、设置四页、输出配置）已由 `abf4ed5` 一轮补齐，**结构与 WPF 基本同构**；差距集中在**样式层**，且是三层叠加的问题：

| 层级 | 现状 | 后果 |
|---|---|---|
| 窗口外壳 | 未自绘标题栏，用系统原生标题栏 | **双标题栏**（原生 + 42px 应用头部），与 WPF 差异最大、最显眼 |
| 控件模板 | 无任何 ControlTheme，直接吃 `FluentTheme` 默认样式 | 按钮/输入框/下拉/滚动条/选项卡是"通用 Fluent 现代风"，不是 ScanFlow 工业风 |
| 设计令牌 | 只有颜色/画刷（2×~5KB），无圆角/字号/尺寸令牌 | 明暗主题色值与原型存在系统性漂移，字号出现 9/10px 违规 |

WPF 侧对应资产是 **2×1033 行**主题（含 15 类控件模板）+ WindowChrome 自绘标题栏 + 16 个 SVG 图标源 + `--ui` 离屏渲染校验；Avalonia 侧主题合计约 **10KB 且零模板**、无图标资产、无渲染校验。

因此改进计划的主线是：**先立验证护栏（否则改样式无回归保护）→ 再补令牌层 → 再补控件模板层 → 再自绘标题栏 → 最后按原型补齐工作台与设置页的具体组件。**

预计 **12–15 人日**，分 8 个阶段（P0–P7），每阶段可独立验收。

---

## 2. 现状事实（Avalonia，已核实）

### 2.1 样式相关文件

| 文件 | 行数 / 大小 | 内容 |
|---|---:|---|
| `App.axaml` | 212 行 | `FluentTheme` + ~40 条内联 `<Style>`（类选择器）+ 25 个 `StreamGeometry` 图标 + 主题字典挂载 |
| `Themes/LightTheme.axaml` | ~5 KB | 仅 Color / SolidColorBrush / RadialGradientBrush |
| `Themes/DarkTheme.axaml` | ~5 KB | 同上 |
| `MainWindow.axaml` | 571 行 | 工作台：头部 42px / 提示条 / 工具栏 / 视口+结果 / 日志抽屉 / 页脚 28px |
| `SettingsWindow.axaml` | 344 行 | 4 个 TabItem（相机与采集 / OCR 算法 / 预览与去重 / 数据输出） |
| `Assets/` | **不存在** | 无 SVG 设计源、无 `.ico`、csproj 无 `<ApplicationIcon>` |

统计：XAML 内 `x:Name` 144 个、`DynamicResource` 引用 154 处、`CornerRadius` 仅用到 2/4/6/12/13 五种、`FontSize` 用到 9/10/11/12/13/14/15/16 八档。

### 2.2 窗口外壳

`MainWindow.axaml` / `SettingsWindow.axaml` 只设置 `Title=`，**没有** `ExtendClientAreaToDecorationsHint`、`ExtendClientAreaChromeHints`、`SystemDecorations` 相关设置（已全仓 grep 确认 0 命中），也没有最小化/最大化/关闭按钮与 `GeoWindowMinimize/Maximize/Restore/Close` 图标。
→ 实际呈现为 **系统标题栏 + 应用内 42px 头部** 两层。

### 2.3 验证设施

`tests/ScanFlowOcr.SmokeTests` 只有一个 `Program.cs`（13.5 KB），**没有 UI 渲染校验**。
对照 WPF 的 `tests/ScanFlow.SmokeTests/UiChecks.cs`（386 行）：在 `960/1240` 两种宽度、`Dark/Light` 两种主题下 `Measure/Arrange/Render` 主窗口与设置窗口到 `artifacts/ui/*.png`，并断言关键控件不越界。

---

## 3. 差距分析

### 3.1 能力层：WPF 有、Avalonia 无

| # | 能力 | WPF 实现 | Avalonia 现状 | 影响 |
|---|---|---|---|---|
| 1 | 自绘标题栏 | `WindowStyle=None` + `WindowChrome`，自绘最小化/最大化还原/关闭，按钮垂直拉满 42px（菲茨定律），交互控件 `IsHitTestVisibleInChrome`，未用 `AllowsTransparency` | 无 | **高**：双标题栏，观感与品牌感断裂 |
| 2 | 控件模板 | 1033 行/主题，覆盖 Button(含 Primary/Danger/Outline/Ghost/PillCard/CaptionButton/CaptionClose 变体)、TextBox、CheckBox、RadioButton、ScrollBar(Thumb/PageButton)、TabControl、TabItem、ComboBox(+ToggleButton)、ComboBoxItem、ListViewItem、ContextMenu、MenuItem、ToolTip、GridViewColumnHeader、Thumb | 0 个模板，全部走 FluentTheme | **高**：四态（hover/pressed/focus/disabled）与圆角、细滚动条、指示线 Tab 全部缺失 |
| 3 | 图标资产 | `Assets/Icons/*.svg` 16 个设计源 + `ScanFlow.ico` + 窗口按钮几何图形 | 25 个内联几何图形，**无窗口按钮图形**、无 SVG 源、无 ico | 中：P3 被阻塞；品牌资产不可追溯 |
| 4 | 设置页深度 | 866 行：算法参数动态卡片、实时搜索框、分级下拉、恢复默认、参数依赖置灰、ROI X1/Y1/X2/Y2 | 344 行：纯 TextBox，无搜索/分级/折叠，ROI 为 X/Y/W/H | 中：样式语言（分组卡/滑块/折叠/校验定位）需移植，**业务面板不得移植** |
| 5 | 离屏渲染校验 | `--ui` 渲染 960/1240 × 明暗 + 设置窗 + 边界断言 | 无 | **高**：样式改造无回归网 |
| 6 | 窗口位置记忆 | `WindowPlacementHelper.cs`（3.4 KB，多屏钳制） | `MainWindow.axaml.cs:198-216` 内联简版 | 低 |

### 3.2 令牌层：与原型的设计令牌漂移

原型 `prototype/styles.css` 是唯一权威令牌源（`:root/[data-theme="dark"]` 与 `[data-theme="light"]`）。

**暗色**

| Token | 原型（目标） | Avalonia 现值 | 偏差 |
|---|---|---|---|
| `--bg-app` | `#111315` | `#121518` | 轻 |
| `--bg-surface` | `#171a1d` | `#1b1e23` | 中 |
| `--bg-surface-subtle` | `#1f2327` | `#1f2327` | 一致 |
| `--bg-surface-hover` | `#262b30` | `#242930` | 轻 |
| `--bg-viewport` | `#0c0e10` | `#0c0e10` | 一致 |
| `--border-subtle` | `#292f36` | `#2f3540` | 中 |
| `--border-medium` | `#3b434d` | `#3b434d` | 一致 |
| `--border-focus` | `#5279a4` | `#5279a4` | 一致 |
| `--text-primary` | `#e6edf3` | `#f1f3f5` | 中 |
| `--text-secondary` | `#9aa2ae` | `#adb5bd` | 中 |
| `--text-muted` | `#656d79` | `#6c757d` | 轻 |
| `--color-brand` | **`#3b719f`** | **`#339af0`** | **重（见决策 D1）** |
| `--color-brand-hover` | `#4a84b5` | `#4dabf7` | 重 |
| `--color-brand-soft` | `#1a2733` | `#1a2733` | 一致 |
| `--color-brand-border` | `#29445f` | `#1f385c` | 中 |
| success / warning / danger / duplicate | `#10b981` / `#f59e0b` / `#ef4444` / `#818cf8` | 同 | 一致 |
| 视口遮罩 `--bg-overlay` | `rgba(12,14,16,0.88)` | `#e00c0e10` | 近似 |

**亮色**

| Token | 原型（目标） | Avalonia 现值 | 偏差 |
|---|---|---|---|
| `--bg-app` | `#f4f6f8` | `#f4f6f9` | 轻 |
| `--bg-viewport` | `#e9ecf0` | `#eaedf2` | 中 |
| `--border-subtle` | `#d0d7de` | `#e2e6eb` | **重**（对比度不足） |
| `--border-focus` | `#0969da` | `#1864ab` | 重 |
| `--text-primary` | `#1f2328` | `#1a1f26` | 轻 |
| `--text-secondary` | `#4a5462` | `#495057` | 轻 |
| `--text-muted` | `#768392` | `#868e96` | 中 |
| `--color-brand` | `#0969da` | `#1864ab` | 重 |
| `--color-brand-hover` | `#0858b7` | `#14538c` | 重 |
| `--color-brand-border` | `#b8d5f7` | `#b9d9fc` | 轻 |
| `--color-success` | `#1a7f37` | `#2b8a3e` | 中 |
| `--color-warning` | `#9a6700` | `#e67700` | 重 |
| `--color-danger` | `#cf222e` | `#c92a2a` | 中 |
| `--color-duplicate` | `#6e5494` | `#6e5494` | 一致 |

**几何与字号**

| Token | 原型 | Avalonia | 处置 |
|---|---|---|---|
| `--header-height` | `46px` | 42px（硬编码两处） | 统一为令牌 |
| `--radius-xs/sm/md/lg` | `3 / 4 / 6 / 8` | 3 / 4 / 6（**无 lg**），且散落 2/12/13 | 补 `RadiusLg`，清理散值 |
| 字号 | 11 / 12 / 13 为主，10 为微标 | 9 / 10 / 11 / 12 / 13 / 14 / 15 / 16 | **违规**：`doc/ui-design-spec.md` §2.2 明确"界面 XAML 不使用 10 DIP 字号"，现值有 2 处 9px、6 处 10px；15px 标题也不在五档内 |

### 3.3 组件层：原型有、Avalonia 缺

| 原型组件（CSS 类） | 用途 | Avalonia 现状 | 优先级 |
|---|---|---|---|
| `.viewport-overlay` + `.overlay-box/icon-wrap/title/desc` | 视口 5 态遮罩：相机掉线 / 未选择相机 / 已暂停 / 已停止 / 预览关闭 | 仅一个通用 `PanelPlaceholder` + `CameraErrorText` | P1 |
| `.filter-tabs` / `.filter-tab` | 结果分类筛选（全部/接纳/重复/失败） | 无 | P1 |
| `.result-item` 系列 + `.dup-counter-tag` + `.status-pill-accepted/duplicate/rejected` | 结果条目状态色、重复计数 ×N | 有卡片，但无状态胶囊/重复计数 | P1 |
| `.results-footer` | 结果区底部输出通道状态条 | 仅页脚一行文本 | P2 |
| `.search-box/.search-icon/.search-input` | 带前置放大镜的搜索框（28px，左内边距 26） | 纯 `TextBox` + `PlaceholderText` | P1 |
| `.viewport-metrics-bar` + `.metric-item/label/value/footnote` | 视口底部 32px 指标条 | 30px chips，无 label/value/footnote 结构 | P2 |
| `.pulse-indicator` | 运行中脉冲点 | 静态 `Ellipse` | P2 |
| `.hud-badge` / `.hud-badge-secondary` | 视口 HUD 胶囊（等宽字体） | 已有近似实现 | — |
| `.settings-group` / `.settings-group-title` / `.settings-note` | 设置分组卡层级 | 用 `panel-card` 近似，层级不统一 | P1 |
| `.drawer-nav` / `.drawer-nav-btn` | 设置导航（选中 2px 品牌底边） | `TabItem` 默认 Fluent 样式 | P1 |
| `.form-range` / `.range-header` / `.range-num` | 滑块 + 数字读数 | 无 `Slider` 用法 | P1 |
| `.advanced-details` / `.advanced-summary` | 高级参数折叠 | 无 | P2 |
| `.device-capability-card` + `.cap-row/key/val` | 设备能力展示卡 | 无 | P2 |
| `.sample-preview-box` + `.preview-payload-dump/format-tag/qos-note` | 输出消息样例预览 | 无 | P2 |
| `.modal-card` + `.detail-row/key/val/raw-dump` | 记录详情弹窗 | 用内联 inspector 卡片替代 | P3 |
| `.checkbox-grid` / `.checkbox-tag` / `.select-inline` / `.switch-label` | 表单控件变体 | 无 | P2 |
| 细滚动条（WPF 3px 圆角滑块、透明轨槽） | 全局滚动条 | 仅 `AllowAutoHide=True` | P1 |
| `.banner-alert-warning` / `.banner-alert-danger` | 提示横幅 | 已有 `BannerNotice` / `BannerError` | — |

---

## 4. 改进计划

### P0 · 验证护栏与构建解阻（0.5–1 人日）

**P0.1 解除构建阻塞（前置，必须先做）**

当前本机 `dotnet build ScanFlowOcr.slnx -c Release` **失败**，与 UI 代码无关：`obj/`、`bin/` 下存在由**上一个沙箱账户** `seuic_yanki\CodexSandboxOffline` 拥有的残留产物，当前用户 `seuic_yanki\newto` 无法覆盖写入。典型报错：

```
AvaloniaBuildTasks.targets(117,5): error MSB4018: System.UnauthorizedAccessException:
  Access to the path '...\src\ScanFlowOcr.App\obj\Release\net10.0\Avalonia\resources' is denied.
Microsoft.Common.CurrentVersion.targets(3883,5): error MSB3491:
  未能向文件"obj\Debug\net10.0\ScanFlowOcr.App.csproj.CoreCompileInputs.cache"写入行。
```

在**沙箱外**（普通 PowerShell，非受限令牌）执行一次清理后重建：

```powershell
Get-ChildItem -Path F:\Projects\ScanFlow-OCR -Include obj,bin -Recurse -Directory |
  Where-Object { $_.FullName -notmatch '\\vendor\\' } | Remove-Item -Recurse -Force
dotnet build F:\Projects\ScanFlow-OCR\ScanFlowOcr.slnx -c Release
```

（`obj`/`bin` 均为可再生产物；不需要动源码。）

**P0.2 新增离屏 UI 渲染校验（对应 WPF `--ui`）**

- 新增 `tests/ScanFlowOcr.UiChecks`（或并入 SmokeTests，加 `--ui` 开关），引用 `Avalonia.Headless` + `Avalonia.Skia`。
- 用 `AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }` + `RenderTargetBitmap` 渲染：
  - `MainWindow`：`960 / 1240` 宽 × `Light / Dark`
  - `SettingsWindow`：`740×680`（并滚动到每个 Tab） × `Light / Dark`
  - 输出到 `artifacts/ui/{theme}-{width}.png`
- 断言（照搬 WPF `UiChecks` 的思路）：关键控件不越界 —— `StartScanButton`、`StopScanButton`、`ResultsList`、`ResultSearchBox`、`SettingsTabControl`、`TxtZoomLevel`。
- 目标：**任何样式改动都能一键出图并回归**，这是后续 P1–P6 的前提。

**P0.3 令牌单一来源**

新增 `src/ScanFlowOcr.App/Themes/Tokens.axaml`（Light/Dark 两个 `ResourceDictionary` 各一份），集中定义：颜色、画刷、`RadiusXs/Sm/Md/Lg`、`HeaderHeight`、字号档位。`LightTheme.axaml` / `DarkTheme.axaml` 退化为合并引用。**后续所有改动只允许改令牌文件或控件模板文件，禁止在页面 XAML 里写死颜色/字号/圆角。**

**验收**：Release 构建绿；`--ui` 产出 8 张图；`artifacts/ui` 入库忽略。

---

### P1 · 设计令牌对齐（1 人日）

1. 按 §3.2 表格把令牌全量替换为原型值（暗色与亮色两套）。
2. 补齐 `RadiusLg=8`；把散落的 `CornerRadius="2/12/13"` 归位到 3/4/6/8（HUD 胶囊保留胶囊形语义，用命名令牌 `RadiusPill`）。
3. 字号归一：**去掉 9px 与 10px**（`MainWindow.axaml` 共 8 处：L273/275/342/366/458/460/461/515 → 11px）；15px 标题并入 16（窗口标题）或 14（区块标题）。
4. `--header-height` 42 → 46，主窗口与设置窗口共用同一令牌。
5. 亮暗对照：每个语义色在暗色下用 `Bg/Text/Border` 三件套（沿用现有 `SuccessBg/Text/Border` 命名），补齐 `Warning*/Duplicate*` 在亮色的可用值。

**验收**：`--ui` 出图对比原型；全仓 grep `FontSize="9"`、`FontSize="10"` 为 0；无硬编码 `#RRGGBB` 出现在页面 XAML（仅令牌文件允许）。

---

### P2 · 控件模板层（2–3 人日）

新增 `src/ScanFlowOcr.App/Themes/Controls.axaml`，把 `App.axaml` 里的 ~40 条内联样式迁移为完整 `ControlTheme`，并补全 WPF 已覆盖的控件类型：

| 控件 | 要点（对齐原型 / WPF） |
|---|---|
| `Button` 基类 | 圆角 4、内边距 5,12、字号 12、四态：hover 底色、pressed 加深、focus 品牌描边、disabled 降不透明度 |
| `Button.primary` | 品牌底 + 白字 + 同色描边；hover 用 `BrandHover` |
| `Button.outline` | `SurfaceSubtle` 底 + `BorderMedium` 描边；hover 转 `SurfaceHover` + `BorderFocus` |
| `Button.ghost` | 透明底；hover `SurfaceHover` |
| `Button.danger` / `danger-soft` | 实心 / 柔和两档 |
| `Button.icon` | 28×28、圆角 4 |
| `Button.caption` / `caption-close` | 标题栏按钮：拉满标题栏高、hover 用中性/危险底色（P3 使用） |
| `TextBox` | 圆角 4、`SurfaceInput` 底、focus 品牌描边、watermark 前景用 `TextMuted`、disabled 态 |
| `ComboBox` + `ComboBoxItem` | 自绘 `ToggleButton` 模板、圆角 4、下拉弹层与选中态 |
| `CheckBox` / `RadioButton` | 品牌色勾选/圆点、四态 |
| `Slider`（新增） | 6px 轨道 + 品牌色进度 + 圆点手柄，供 P5 设置页数值项使用 |
| `ScrollBar` | 透明轨槽 + 3px 圆角滑块、无箭头按钮、hover 高亮 |
| `TabControl` / `TabItem` | 工业指示线导航：选中 2px 品牌底边、未选中次要文字 + hover、底部全局分割线 |
| `ListBox` / `ListBoxItem` | 结果卡容器：hover `SurfaceHover`、选中品牌柔和底 + 品牌描边 |
| `ToolTip` / `MenuFlyout` / `ContextMenu` / `MenuItem` | 圆角 6、`SurfaceCard` 底、`BorderSubtle` 描边、阴影 |
| `GridSplitter` | hover 品牌色细线 |

硬性要求：全部用 `DynamicResource`；明暗两主题逐一目视；`TreatWarningsAsErrors=true` 下不得引入警告。

**验收**：`--ui` 出图中按钮/输入/下拉/滚动条/选项卡与原型一致；键盘 Tab 焦点可见；disabled 态可辨认。

---

### P3 · 自绘标题栏与窗口 chrome（1.5–2 人日）

对齐 WPF `wpf-ui.md` 的窗口约定：

1. 两个窗口加：
   ```xml
   ExtendClientAreaToDecorationsHint="True"
   ExtendClientAreaChromeHints="NoChrome"
   ExtendClientAreaTitleBarHeightHint="-1"
   ```
   并保持 `SystemDecorations="Full"`（不用 `AllowsTransparency`，避免渲染性能损失）。
2. 头部 46px 区域作为拖动区：`PointerPressed` → `BeginMoveDrag(e)`；双击 → 最大化/还原切换。
3. 新增 4 个 `StreamGeometry`：`GeoWindowMinimize` / `GeoWindowMaximize` / `GeoWindowRestore` / `GeoWindowClose`（可直接取 WPF `DarkTheme.xaml` L149-152 的路径数据）。
4. 头部右侧加最小化 / 最大化(还原) / 关闭三键，用 P2 的 `caption` 样式，**垂直拉满 46px**（菲茨定律：屏幕顶边点击可命中）；最大化时图标切 `GeoWindowRestore`。
5. 交互控件（主题切换、设置、状态胶囊内的按钮）**不得被拖动区吞点击** —— Avalonia 中不给这些控件挂拖动处理器，必要时用 `e.Handled = true` 阻断冒泡。
6. 屏幕工作区边界钳制（`Screens` API）与多显示器；最大化外边距。
7. `SettingsWindow` 同样处理（`CenterOwner` 保持）。
8. **跨平台降级**：Linux 各 WM 对 `ExtendClientArea` 支持不一致。加运行时判断（`OperatingSystem.IsLinux()` 或检测平台实现），不支持时回退为 `SystemDecorations="Full"` + 隐藏自绘按钮，避免出现"无边框且无法移动"的窗口。

**验收**：Windows 上无双标题栏；拖动/双击/三键/吸附/最大化还原正常；960 与 1240 宽下三键不被裁切；Linux 回退路径可启动。

---

### P4 · 工作台视口与结果区（2 人日）

1. **视口 5 态遮罩**：把现有 `PanelPlaceholder` 拆为可切换状态（图标 + 标题 + 说明 + 可选主操作按钮）：
   `相机掉线（可重连）` / `未选择相机` / `扫描已暂停` / `扫描已停止（保留记录，提供"启动扫描"）` / `预览已关闭（采集与识别仍在运行）`。
   语义对齐 `prototype/README.md` 第 3 节与 `ui-settings-round3.md` 第 6 条。
2. **结果筛选标签**：结果区标题下加 `全部 / 接纳 / 重复 / 失败` 分段控件，与搜索框联动；筛选仅影响展示，不影响去重状态机。
3. **结果条目**：补状态胶囊（`accepted/duplicate/rejected`）、重复计数 `×N`、时间、置信度；保留现有 `OnCopyResultItem`。
4. **结果区底部输出通道状态条**（`.results-footer`）：把页脚的 `TxtOutputQueueStatus` 语义在结果区内可视化。
5. **搜索框**：加前置放大镜图标（28px 高、左内边距 26）。
6. **视口指标条**：30 → 32px，改为 `label + value + footnote` 结构（取景 FPS / 丢帧 / 算法版本，版本信息降为次要）。
7. **脉冲指示点**：运行中给 HUD 与页脚的状态点加脉冲动画（`Animation` + `KeyFrame`），停止时静止。
8. 头部品牌区：`ScanFlow-OCR` + 副标题 chip，字号归位到 P1 档位。

**验收**：5 种遮罩可逐一触发；筛选与搜索联动正确；`--ui` 出图与原型对照通过。

---

### P5 · 设置窗口视觉对齐（1.5–2 人日）

保持 `SettingsWindow`（独立窗口，符合桌面惯例且与 WPF 一致），只移植**视觉与交互样式**，不移植条码/码制面板：

1. 分组卡统一为 `.settings-group` 结构：标题 13–14 + 说明 11–12 + 内容区，卡片圆角 6、内边距 14。
2. Tab 导航换 P2 的指示线样式（选中 2px 品牌底边）。
3. 数值项改 `Slider` + 数字读数（`.form-range/.range-header/.range-num`）：取景帧率、冷却间隔、队列容量、OCR 超时。
4. **高级参数折叠**（`.advanced-details`）：默认收起，展开后显示次要项。
5. **校验失败定位**：非法输入（端口越界、ROI 越界、MQTT 主题含 `+/#`）时自动切到对应 Tab、聚焦并高亮字段 —— 对齐 `ui-settings-round3.md` 第 4/5 条。当前 Avalonia 只有底部 `BannerError`。
6. **设备能力卡**（`.device-capability-card`）：显示当前设备的 Provider / 分辨率列表 / 支持的控制项，不支持的控件置灰并给出原因。
7. **消息样例预览卡**（`.sample-preview-box`）：按当前草稿实时渲染 JSON/纯文本样例，标注"样例，不真实外发"。
8. ROI 输入从 `X/Y/W/H` 改为 `X1/Y1/X2/Y2`（与 WPF 一致）；**必须保持 `appsettings.json` 向后兼容**（旧配置以起点+宽高存储，读取时换算）。
9. 草稿/取消/应用机制保持不变（现有实现已正确）。

**验收**：4 个 Tab 明暗均无遮挡；非法输入能定位并高亮；ROI 旧配置可读；`--ui` 出图。

---

### P6 · 图标与品牌资产（1 人日）

1. 新增 `src/ScanFlowOcr.App/Assets/Icons/*.svg`，以 WPF `Assets/Icons` 为设计源，**剔除条码相关图标**（`barcode` 等）。
2. 生成 `Assets/ScanFlow.ico`（16/24/32/48/256），csproj 加 `<ApplicationIcon>Assets\ScanFlow.ico</ApplicationIcon>` 与 `<AvaloniaResource Include="Assets\**" />`。
3. 窗口图标 `Icon="/Assets/ScanFlow.ico"`（两窗口）。
4. 校验 SVG 源、ICO 与界面内几何图形三者一致。

**验收**：任务栏/窗口图标正确；`Assets` 进包；图标在各尺寸下清晰。

---

### P7 · 验收与文档（1 人日）

验收矩阵：

| 维度 | 取值 |
|---|---|
| 窗口宽度 | 960 / 1240 DIP |
| 主题 | 亮 / 暗 |
| DPI | 100% / 150% |
| 状态 | 无来源 / 扫描中 / 多结果 / 错误 / 禁用 / 键盘焦点 |
| 场景 | 相机实时 / 图片轮播 / 设置草稿与应用 |

- 回归：`--ui` 出图 + 关键控件边界断言。
- 真机：相机预览与连续会话、ROI、去重三模式、MQTT/TCP/键盘端到端。
- 文档：新增 `docs/ui-style-guide.md`（令牌表 + 控件规范 + 验收清单），更新 `README.md` 的 UI 段落。

---

## 5. 排期与依赖

```
P0 护栏与解阻 ──► P1 令牌 ──► P2 控件模板 ──► P3 标题栏
                                    │
                                    ├──► P4 工作台 ──┐
                                    └──► P5 设置页 ──┼──► P7 验收
                                                     │
                                        P6 图标资产 ──┘
```

| 阶段 | 人日 | 依赖 |
|---|---:|---|
| P0 | 0.5–1 | — |
| P1 | 1 | P0 |
| P2 | 2–3 | P1 |
| P3 | 1.5–2 | P2（caption 按钮样式） |
| P4 | 2 | P2 |
| P5 | 1.5–2 | P2 |
| P6 | 1 | P1 |
| P7 | 1 | 全部 |
| **合计** | **12–15** | |

---

## 6. 风险与约束

| 风险 | 说明 | 缓解 |
|---|---|---|
| 构建阻塞 | `obj/bin` 残留产物属上一个沙箱账户，本机当前无法构建 | P0.1 先在沙箱外清理；**这是执行本计划的前置条件** |
| 跨平台窗口 chrome | Linux WM 对 `ExtendClientArea` 支持不一致 | P3.8 运行时降级 |
| 无回归网时改样式 | 目前无 UI 渲染校验，改样式全靠肉眼 | P0.2 必须先落地 |
| 令牌全量替换的视觉回归 | 品牌色/边框色变化会改变整体观感 | P1 出图与原型逐项对照；变更集中在令牌文件，可一键回退 |
| 契约漂移 | ROI 输入语义从 X/Y/W/H 改 X1/Y1/X2/Y2 | P5.8 保持 `appsettings.json` 向后兼容 + 单测 |
| 字号归一影响布局 | 9/10 → 11px 会撑大徽标 | 同步调整内边距；`--ui` 960 宽下检查截断 |
| `TreatWarningsAsErrors` | 样式/资源改动可能触发分析器告警 | 每次改动静默构建校验 |

**明确不做**（`docs/LOCAL-AGENT-HANDOFF.md` 永久排除）：DecodeP1 / Phenix / CoreHost 条码路径 / MemoryModule / 码制 UI / 条码参数面板 / Clipboard 自动输出。原型的码制多选、期望码数、AIM ID 徽标等**一律不移植**。

---

## 7. 待决策项

**D1 · 品牌主色以谁为准？（影响面最大）**

| 方案 | 暗色品牌 | 亮色品牌 | 说明 |
|---|---|---|---|
| A（推荐）以原型为准 | `#3b719f` | `#0969da` | 原型注释明确"消除蓝紫科技感、发光浮夸装饰"，亚光钢蓝更贴工业工具；与用户"对照设计原型"的要求一致 |
| B 沿用 WPF 现值 | `#339af0` | `#1864ab` | WPF 主题（2026-09-16 更新）与 `doc/ui-design-spec.md` 记录的基准；视觉更亮、更"科技" |

注：原型 `styles.css` 为 2026-09-12、`index.html` 为 09-14，WPF 主题为 09-16 —— WPF 在原型改版**之后**仍保留亮蓝，可能是刻意选择，也可能是漏改。建议由产品方确认。因 P0.3 已把令牌收敛到单一文件，**无论选哪个都是一处改动**。

**D2 · 默认主题**：Avalonia 现为 `RequestedThemeVariant="Light"`（与 WPF `wpf-ui.md`"首次启动使用浅色"一致），但原型 `styles.css` 是"暗色优先"。建议维持**亮色默认**，暗色作为可切换项。

**D3 · 日志抽屉**：Avalonia 多出 WPF/原型都没有的"运行日志"抽屉（`DrawerLog`）。建议保留（对现场排障有用），但降到页脚次级入口，避免与结果区争夺注意力。

**D4 · 设置形态**：原型用侧边抽屉，WPF/Avalonia 用独立窗口。建议**维持独立窗口**（桌面惯例 + 与 WPF 一致），只移植抽屉的视觉语言。

**D5 · 结果详情**：原型用模态弹窗，Avalonia 用内联 inspector。建议保留内联（不遮挡视口），仅在需要原始 JSON 时提供弹窗。

---

## 8. 一句话执行顺序

> 先清 `obj/bin` 恢复构建 → 建离屏出图护栏 → 收敛令牌到单文件并对齐原型 → 补控件模板 → 自绘标题栏 → 按原型补齐工作台与设置页组件 → 补图标资产 → 按 960/1240 × 明暗 × DPI 矩阵验收。
