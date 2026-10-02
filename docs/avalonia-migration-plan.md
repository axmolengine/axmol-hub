# UI 层迁移规划：WPF → Avalonia

状态：草案，待评审
日期：2026-10-02
关联文档：[ADR-0001 Hub 技术栈与定位](adr/0001-hub-tech-stack-and-positioning.md)、[CI 设计](ci.md)
关联基础设施：`.github/workflows/ci.yml`

---

## 1. 目标与范围

**目标**：GUI 宿主从 Windows 独占的 WPF 切换到 Avalonia，使 `AxmolHub.App` 能在 Windows / macOS / Linux 三平台构建与运行。

**范围**：只动 App 层。

**"Core 一行不改"为什么是结构性保证**：依赖方向是 App → Core、Cli → Core、Checks → Core，Core 从不反向引用 App。核对 `src/AxmolHub.Core/` 全部 15 个文件后确认：**没有任何 WPF 类型引用**，也没有 `Microsoft.Win32`、`Registry`、P/Invoke。跨平台差异全部走 `OperatingSystem.IsWindows()` 运行时分支。因此换 UI 不触动 Core。

**明确不在范围内**（避免把两件事混成一件）：

- P0 `--json` 工具契约与 P1 MCP server —— 与 UI 无关，按 ADR-0001 的顺序排在 UI 之后。
- macOS / Linux 的**引擎构建**宿主后端（`PlatformBuildService` 里的 CMake / Ninja / pwsh 工具链）。**GUI 能在 macOS 启动 ≠ 能在 macOS 构建游戏**，前者是本文档的目标，后者是 Core 的另一条独立工作线。
- Windows 上已有的 Android / WASM 构建闭环。

---

## 2. 迁移面实测

### 2.1 规模

| 部位 | 文件 | 行数 | 性质 |
| --- | --- | --- | --- |
| 应用入口 + 无头模式 | `App.xaml` / `App.xaml.cs` | 23 / 74 | 入口机械，烟雾截图需 spike |
| 主窗口 | `MainWindow.xaml` / `.xaml.cs` | 76 / 823 | **主体工作量** |
| 纯代码构建的窗口 | `ModuleWindow.cs` | 171 | 机械但量大 |
| 同上 | `BuildProgressWindow.cs` | 97 | 机械 |
| 同上 | `AndroidReleaseWindow.cs` | 86 | 机械 |
| 文案 | `Texts.cs` | 206 | 机械 |
| **合计** | 8 个文件 | **1556** | XAML 仅 99 行 |

**关键结构事实**：4 个窗口里有 **3 个完全没有 XAML**，界面在 C# 里用 `new TextBlock()` / `new StackPanel()` 之类逐句搭出来。所以"XAML 只有 99 行"是假象 —— 真正的迁移面是 1556 行里的绝大多数。

### 2.2 耦合点分类

| 耦合点 | 数量 | 迁移性质 |
| --- | --- | --- |
| `App.xaml` 全量 ControlTemplate 主题 | 14 个控件样式 | **重写**（语义不同，见 §3.1） |
| `MessageBox` | 23 处 | **需自建替代**，Avalonia 无内置 |
| `RoutedEventHandler` / `RoutedEventArgs` | 40 处 | 机械（改名 `RoutedEventArgs`→`RoutedEventArgs`，命名空间变） |
| `SelectionChangedEventArgs` | 5 处 | 机械 |
| `DispatcherPriority`（界面线程让渡） | 13 处 | 机械（`Dispatcher.UIThread.InvokeAsync`） |
| `System.Windows.Media` / `Shapes` | 27 处 | 机械（画刷 / 颜色 / 几何） |
| 文件与文件夹选择 | 4 处（`OpenFolderDialog` 等） | **异步化**（`StorageProvider`） |
| `Segoe MDL2 Assets` 图标字体 | 4 处字形 + XAML `FontFamily` | **换资源**，macOS/Linux 无此字体 |
| `RenderTargetBitmap` + `PngBitmapEncoder` | 2 处（`--smoke` 与 `Capture()`） | **需 spike**，见 §3.5 |
| `DataGrid` | 35 处引用，1 处构造 | 需单独 NuGet 包或改 `TableView` |
| `{DynamicResource}` + `Application.Current.Resources` | 183 个文案键 | 机械（Avalonia 同名机制存在） |
| `IValueConverter` | 1 个 | 机械（签名一致） |
| 控件构造 | `TextBlock` 15 / `StackPanel` 11 / `Button` 7 / `Border` 4 / … | 机械 |

### 2.3 隐藏前置项：`Invoke-Axmol.ps1` 的错位归属

`Invoke-Axmol.ps1` **物理上住在 `src/AxmolHub.App/`，逻辑上属于 Core**。`ProjectService` 的构造函数接收它的路径，而引用它的地方横跨四个项目：

| 引用方 | 位置 | 形态 |
| --- | --- | --- |
| App | `MainWindow.xaml.cs:79` | `AppContext.BaseDirectory/Invoke-Axmol.ps1` |
| Cli | `Program.cs:45`、`:87` | 同上 |
| Cli 项目文件 | `AxmolHub.Cli.csproj:11` | **跨项目文件 include**：`../AxmolHub.App/Invoke-Axmol.ps1` |
| Checks | `Program.cs` 57/162/224/277 行 | **按仓库相对路径**引用 App 项目目录 |
| 安装检查 | `installer/Test.ps1:29` | 断言安装目录里存在该文件 |

**结论**：只要重命名或替换 App 项目，这些引用会同时断掉，其中 4 处还在检查程序里、1 处还在安装验收脚本里。

**处置（必须排在 Phase 2 之前）**：把该脚本移到中立位置（建议 `assets/axmol/invoke-axmol.ps1` 或 `src/AxmolHub.Core/assets/`），App 与 Cli 各自用 `Content Include` + `Link` 搬进输出目录，Checks 改用中立路径。这一步是纯重构、可独立提交、不涉及 UI。

---

## 3. 五个必须"重写"而不是"替换"的部位

### 3.1 主题层：`Trigger` 语义在 Avalonia 里不存在

`App.xaml` 把 Button / TextBox / PasswordBox / ComboBox / ComboBoxItem / CheckBox / RadioButton / DataGrid(+Header/Cell/Row) / ScrollBar / Expander / TextBlock 全部重新套了 `ControlTemplate`，并且用 `ControlTemplate.Triggers` + `Setter TargetName=` 表达悬停 / 选中 / 禁用 / 获取焦点状态。

Avalonia 没有 `Trigger`。等价物是**样式选择器 + 伪类**：

```xml
<!-- WPF：Trigger + TargetName -->
<Trigger Property="IsMouseOver" Value="True">
  <Setter TargetName="Frame" Property="BorderBrush" Value="#6AB6EB"/>
</Trigger>

<!-- Avalonia：选择器定位到模板内的具名部件 -->
<Style Selector="Button:pointerover /template/ Border#Frame">
  <Setter Property="BorderBrush" Value="#6AB6EB"/>
</Style>
```

伪类名也不同：`IsChecked`→`:checked`、`IsEnabled=False`→`:disabled`、`IsHighlighted`→`:pointerover`、`IsKeyboardFocused`→`:focus`、`IsExpanded`→`:expanded`。`FocusVisualStyle="{x:Null}"` 要换成 Avalonia 的 `FocusAdorner`。

**这是全项目最大的单项工作，且无法机械转换。** 好消息是它集中在 `App.xaml` 一个文件里，并且是纯声明式的 —— 可以先在隔离环境里对着 Avalonia 官方 dark 模板逐控件替换，再一次性切过来。

**顺带完成两件本来就要做的事**：把 `#1E1E1E`/`#E4E4E4` 这类散落的硬编码颜色收成一套语义化资源（`Hub.Background` / `Hub.Surface` / `Hub.Border` / `Hub.Accent`…），并借 Avalonia 的 `ThemeVariant` 补上浅色主题 —— 目前应用只有深色，而这在 macOS 上会显得格格不入。

### 3.2 `MessageBox` 的 23 处调用

Avalonia 不提供 `MessageBox`。三个选项：

| 方案 | 代价 | 评价 |
| --- | --- | --- |
| 自建 `HubDialog` 窗口（复用主题） | ~120 行 + 23 处调用点改写 | **推荐**：仓库本来就手写全部控件样式，自建与既有风格一致，且不引入外部依赖 |
| 引入 `MessageBox.Avalonia` 等第三方包 | 一个长期依赖 + 一套独立视觉 | 与"手写主题"的既有取向冲突 |
| 全部改成内联错误条 | 23 处交互语义全变 | 过度设计，会改变用户已习惯的反馈方式 |

注意现有调用有一处需要单独处理：`App.xaml.cs:70` 用 MessageBox 报启动失败 —— 此时窗口可能还没建起来，自建对话框需要能独立于主窗口显示。

### 3.3 文件与文件夹选择：必须异步化

`PickFolder()` 现在是同步的：

```csharp
var dialog = new OpenFolderDialog { Title = ..., Multiselect = false };
return dialog.ShowDialog() == true ? dialog.FolderName : null;
```

Avalonia 用 `TopLevel.StorageProvider.OpenFolderPickerAsync` / `OpenFilePickerAsync`，**只有异步签名**。这会沿调用链向上传染：`PickFolder` 的调用点、`OpenProject`、`ChooseProjectDirectory` 等事件处理器要改成 `async void`（已有先例，项目里 `async void` 事件处理器很常见，风格一致）。

同时注意返回值语义变化：WPF 返回 `string?`，Avalonia 返回 `IStorageFolder`/`IStorageFile`，且路径要用 `.TryGetLocalPath()` 取回本地路径 —— 而 Hub 的语义本来就是"本地路径"，所以要用 `TryGetLocalPath()` 并在为 null 时（用户选了云端/虚拟位置）明确拒绝。

### 3.4 图标字体

`MainWindow.xaml` 用 `Segoe MDL2 Assets` 的 4 个私有区字形做导航图标（`&#xE8A5;` 等），字体族是 `Segoe UI, Microsoft YaHei UI`。macOS / Linux 上两者都不存在，会退化成方块或直接不显示。

处置：换成**随应用分发的图标字体**（内嵌 `.ttf` 作 `AvaloniaResource`）或直接把 4 个图标换成矢量路径。只有 4 个图标，用 SVG path 最省事，也顺带解决 HiDPI。正文与等宽字体也要给三平台各自的回退链 —— 中文字形在 macOS（PingFang SC）和 Linux（Noto Sans CJK，且并非所有发行版预装）上都不是同一套。

### 3.5 `--smoke` 无头截图是 CI 的依赖项

`App.xaml.cs:56-61` 用 `RenderTargetBitmap` + `PngBitmapEncoder` 渲染主窗口存成 PNG。**这不是调试功能**：`installer/Test.ps1` 用 `--smoke` 验证自包含版能启动，新加的 CI 也用它做 GUI 烟雾测试。

Avalonia 侧两条路：

1. `Avalonia.Controls.RenderTargetBitmap` —— 最接近现有写法，但需要真实的渲染后端（Windows 上 Win32 后端可用；Linux 需要 X11 或 Xvfb）。
2. `Avalonia.Headless` 包 —— 提供无显示环境下的渲染与帧捕获。要在无 GPU / 无显示器的 CI 上跑三平台 GUI 截图，这条路更可靠。

**必须在动主窗口之前用 spike 定下来**，因为迁移后的安装验收脚本和 CI 都挂在它上面。建议：Windows 安装包验收继续用真实后端截图（保持现有证据强度），三平台 CI 的 GUI 截图走 Headless。

---

## 4. 平台语义变更（不是 API 替换，是设计变更）

这几项会改变**用户可见行为**，需要在动手前定稿，建议各写一条 ADR。

### 4.1 设置与数据目录的位置

现状：设置写在 **exe 同级**的 `hub-settings.json`，默认数据目录是 `AppContext.BaseDirectory/data`。这在 Windows 便携式安装下是合理的（也是安装脚本明确保护的语义：升级保留设置、卸载保留数据）。

到 macOS 就行不通：`.app` 是一个**只读的 bundle**，往里面写设置会失败或触发签名失效。Linux 上把数据放 `/usr/lib/...` 同理不可写。

三个选项：

| 选项 | Windows | macOS | Linux |
| --- | --- | --- | --- |
| A. 各平台用各自惯例 | exe 同级（不变） | `~/Library/Application Support/AxmolHub` | `~/.config/axmolhub` + `~/.local/share/axmolhub` |
| B. 统一到用户目录 | 改变现有行为 | 同上 | 同上 |
| C. 三平台都保持便携 | 不变 | 无法做到 | 无法做到 |

**推荐 A**：Windows 语义完全不动（不破坏安装脚本已有的"升级保留设置、卸载保留数据"验收），macOS/Linux 走 XDG / macOS 惯例。代价是"数据目录"这个概念在两个平台上含义不同，`--data-root` / `--preferences` 参数继续作为统一覆盖手段。

### 4.2 macOS 的代码签名与公证

`README.md` 已经写明"发布包暂未配置发布者代码签名"。macOS 比 Windows 更硬：**未签名的 .app 在用户机器上会被 Gatekeeper 直接拦下**，而且不是"点一下继续"就能过的级别（需要右键打开或 `xattr` 清除隔离属性）。这意味着：

- 要有 Apple Developer ID（年费），并走 `codesign --deep --options runtime` + `notarytool` 公证；
- 或者明确宣布 macOS 版"仅从源码构建"，不提供二进制。

**这一项决定 macOS 是否算"真的支持"。** 建议在 Phase 0 就明确表态，不要拖到 Phase 6。

### 4.3 Linux 的分发形态

Avalonia 12.1 已支持原生 Wayland（X11 仍支持）。但"能在 Linux 构建"和"Linux 用户能装上"是两件事，需要选：`.deb` / `.rpm` / AppImage / Flatpak。AppImage 与"自包含"的现有取向最接近，Flatpak 沙箱则与 Hub 需要调用外部工具链（MSVC 不可用，但 cmake/ninja/adb 要能跑）存在冲突。**建议第一版只发 AppImage + tarball**，把发行版打包交给社区。

### 4.4 打包工具链已先于 UI 迁移重选完毕

**本节原结论已执行完毕（2026-10-02）**：三平台分发形态一旦并列，Windows 侧的安装包工具链也要一并重新评估 —— 因为原选择本身带一个未决问题：固定的 Inno Setup 6.7.3 是**从该版本起要求商业许可**的版本，而仓库里的第三方许可文本仍是 6.7 之前的旧条款。连同 macOS 需要 `.pkg`、Linux 需要 AppImage，**"用哪套工具做三平台打包"已作为一次决策完成**，结果是 **Velopack**（MIT，一套 `vpk` 覆盖三平台，自带自动更新与增量包）。

这条与 UI 迁移的接口只剩一处：`installer/Build.ps1` 已按 RID 参数化，但**对非 Windows RID 有一条显式守卫**，因为 `AxmolHub.App` 仍是 `net8.0-windows` + WPF。**Avalonia 迁移完成后删掉那条守卫，三平台产物即自动可用**，打包脚本不需要再改。详见 [hub-development-plan.md](hub-development-plan.md) §4 D3 与 [installer/README.md](../installer/README.md)。

---

## 5. 分阶段步骤

每个阶段都以"可合并、可回退"为界。**Phase 1 之后是双 UI 并行**，WPF 版始终可用直到 Phase 6 删除。

| 阶段 | 内容 | 验收标准 | CI 变化 |
| --- | --- | --- | --- |
| **P0** | 三平台 CI 落地（已完成，见 `ci.yml`）；跑 §7 的 spike | 三平台矩阵绿；spike 有结论 | 已加 |
| **P1** | 把 `Invoke-Axmol.ps1` 移到中立位置（§2.3）；清除 App 与 Core 之间的文件归属错位 | `dotnet build` 三个项目全绿；Checks 与 `Test.ps1` 仍通过 | 不变 |
| **P2** | 新建 `AxmolHub.App.Avalonia` 项目骨架（`net8.0`，非 windows）：只有一个空窗口 + 主题资源 | 在 macOS / Linux 上**能构建并显示空窗口** | **App 进三平台矩阵**（届时删掉 `ci.yml` 里 `if: matrix.host == 'windows'` 的构建步骤） |
| **P3** | 主题层重写（§3.1）：14 个控件样式 → Avalonia 选择器；补浅色主题；换掉图标字体（§3.4） | 一个"控件画廊"窗口在三平台观感一致 | 不变 |
| **P4** | 基础件：`HubDialog`（替 23 处 MessageBox，§3.2）、`StorageProvider` 异步包装（§3.3）、`--smoke` 迁移（§3.5） | 三平台各自能截图存 PNG；CI 的 GUI 烟雾步骤扩展到三平台 | GUI 烟雾进三平台矩阵 |
| **P5** | 逐窗口迁移，顺序建议 `ModuleWindow` → `BuildProgressWindow` → `AndroidReleaseWindow` → `MainWindow`（由简到繁，前三个是纯代码构建，改起来最"机械"） | 每个窗口在三平台人工过一遍；`MainWindow` 收尾 | 不变 |
| **P6** | 平台化设置/数据目录（§4.1）、删除 WPF `AxmolHub.App`、更新 `README` 与 `installer/*.ps1` 的文件清单 | Windows 安装验收仍通过；macOS/Linux 产出可运行包 | 移除所有 `matrix.host == 'windows'` 特判 |

**为什么 P3 排在 P5 前面**：主题是全局的。如果先把窗口一个个迁过来，每个窗口都会带着临时样式，最后还要再统一改一遍。先把 14 个控件样式定死，后面每个窗口的迁移就变成纯粹的机械替换。

**为什么 P2 要建新项目而不是原地改**：`AxmolHub.App` 保持可构建、可发布、可安装，直到最后一刻。这样 Windows 用户在整个迁移期间都有可用版本，CI 也始终有一条绿线。

---

## 6. 与 CI 的衔接

CI 已经按"迁移会逐步打开闸门"的形状写好了：`.github/workflows/ci.yml` 里 App 的构建与烟雾步骤都带 `if: matrix.host == 'windows'` 并附注释，Avalonia 迁移每推进一步就删掉一个条件，矩阵自动扩大。

`release-windows.yml`（安装包 + 隔离安装验收）与 UI 无关，迁移期间保持不动 —— 这恰好是它的价值：**它是 Windows 侧"没有被迁移破坏"的独立证据**，每一步都能反证。

---

## 7. 动工前必须回答的 spike（Phase 0）

1. **`--smoke` 走哪条路**：真实后端 `RenderTargetBitmap` 还是 `Avalonia.Headless`？三平台无显示环境下能否稳定产出 PNG？（决定 CI 与安装验收能否继续工作）
2. **`DataGrid` 用什么**：`Avalonia.Controls.DataGrid` 包，还是 12.1 新增的内置 `TableView`？现有 35 处引用（含列定义与单元格模板）改造量差别很大。
3. **中文 IME 输入**：macOS / Linux 后端下的输入法候选窗、组合输入在 `TextBox`/`PasswordBox` 上是否正常。这是之前就挂着的待实测项，现在是硬需求 —— Hub 有大量中文文案与中文路径。
4. **CJK 字体回退**：目标 Linux 发行版是否预装 Noto Sans CJK？若否，是自带字体（增加体积）还是声明依赖？
5. **Avalonia 版本**：建议 pin **12.1.x**（2026-09-23 发布 12.1.3，支持 `net8.0`，MIT）。若团队更看重社区样例与第三方控件存量，`11.3.x` 仍是文档最丰富的版本，但需重估 `TableView`、Wayland 等新能力是否可用。

---

## 8. 一条必须先接受的代价：仓库将不再"零依赖、可离线冷构建"

当前仓库 `PackageReference` 数量为 **0**，`dotnet restore` 在 100ms 内完成，冷机器完全离线即可构建 —— 这是现状里一个真实且不多见的优点，`ci.yml` 的缓存策略与 `README` 的"从源码运行"一节都建立在它之上。

引入 Avalonia 会带来**第一个 `PackageReference`**，随之而来：

- 冷构建需要 NuGet 网络访问；
- Avalonia 及其传递依赖需要进 `THIRD_PARTY_NOTICES.md`（MIT，需署名）；
- 构建时间与 CI 缓存键都要重估。

**影响范围可控**：`AxmolHub.Core` 与 `AxmolHub.Cli` 保持零依赖不变 —— Avalonia 只进 GUI 项目。也就是说，"CLI 与 Core 仍可离线构建"这个性质可以保住，丢掉的只是 GUI 那一条。

这不是反对迁移的理由（换跨平台本就要付代价），但它应该被显式记下来，而不是在某个 PR 里无声发生。
