# UI 层迁移规划：WPF → Avalonia

状态：草案，待评审
日期：2026-10-02
关联文档：[ADR-0001 Hub 技术栈与定位](adr/0001-hub-tech-stack-and-positioning.md)、[CI 设计](ci.md)
关联基础设施：`.github/workflows/ci.yml`

> **后续变更（2026-10-03）**：本文档中出现的 `ModuleWindow.cs`（「添加模块」窗口）已删除 ——
> 模块概念整体移除，平台/架构由 `BuildTargets` 表达、工具链准备由引擎 `setup.ps1` 承担。
> 历史叙述保留当时的窗口清单，不再逐字回改。

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
| 文案 | `Texts.cs` | 206 | 机械（**已做**：数据移入 `Core/HubTexts.cs`，WPF 侧只剩 37 行适配） |
| **合计** | 8 个文件 | **1556** | XAML 仅 99 行 |

> `MainWindow.xaml` 的 76 行是**压缩成单行**的写法（整段 `StackPanel`/`Style` 挤在一行里），
> 因此它的"行数"严重低估了真实结构。P5 侦察时按元素数重数了一遍：左侧导航 4 项、
> 内容区 4 个页面、底部状态栏 + `LogPanel`，其中引擎页与项目页各带 `DataGrid`。

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

**关于三个纯代码构建的窗口**（`ModuleWindow` 171 + `BuildProgressWindow` 97 + `AndroidReleaseWindow` 86 = 354 行）：它们用到的 WPF 类型**几乎都有 Avalonia 同名对应物** —— `Thickness`(25) / `TextBlock`(8) / `StackPanel`(8) / `Button`(5) / `SolidColorBrush`(4) / `GridLength`(4) / `Grid`(3) / `DockPanel`(3) / `CheckBox`(3) / `ScrollViewer`(2) / `PasswordBox`(2) / `Expander`(2) / `Border`(2) / `WrapPanel`(1) / `TextBox`(1)。**真正的 WPF 专有例外只有三类**：

- `SaveFileDialog` → `StorageProvider` 异步（§3.3）
- `RenderTargetBitmap` + `PngBitmapEncoder` + `BitmapFrame.Create` → Avalonia `RenderTargetBitmap` + `Save()`（§3.5）
- `BrushConverter` → Avalonia 无此类，改 `Brush.Parse`

**这就是 P5 把这 3 个窗口排在最前的原因：354 行里 WPF 专有 API 的调用点加起来不到 10 处。**

### 2.3 已完成前置项：两个 PowerShell 包装的错位归属（P1）

> 本节与 2.2 记录的是 **P6 之前**的仓库状态（当时 `src/AxmolHub.App/` 是 WPF 版，P6 已删除）。
> 保留原样是因为它们解释了"为什么脚本归 Core"这个至今有效的结论。

`Invoke-Axmol.ps1` **物理上住在 `src/AxmolHub.App/`，逻辑上属于 Core** —— `ProjectService`（`Core/ProjectService.cs:11`）把它当 `wrapper` 参数收下，`:135` 用它去调官方 `axmol new`。同目录的 `Verify-MicrosoftSignature.ps1` 是同一类错位：它校验 MSVC 安装器的微软签名，交给 `WindowsToolchainInstaller`，也是 Core 级资产。引用横跨四个项目：

| 引用方 | 位置（搬迁前） | 形态 |
| --- | --- | --- |
| App | `MainWindow.xaml.cs:79`、`:86` | `AppContext.BaseDirectory/<脚本名>` |
| Cli | `Program.cs:45`、`:87` | 同上（仅 `Invoke-Axmol.ps1`） |
| Cli 项目文件 | `AxmolHub.Cli.csproj:11` | **跨项目文件 include**：`../AxmolHub.App/Invoke-Axmol.ps1` |
| Checks | `Program.cs` 57/167/229/282 行 + `:196` | **按仓库相对路径**引用 App 项目目录 |
| 安装检查 | `installer/Test.ps1` 载荷清单 | 断言安装目录里存在该文件 |

**结论**：只要重命名或替换 App 项目，这些引用会同时断掉，其中 5 处还在检查程序里，载荷清单还在安装验收脚本里。

**处置（P1，2026-10-02 完成）**：两个脚本一并搬到 `src/AxmolHub.Core/Scripts/`，让"逻辑归属 Core"从描述变成字面事实。**输出布局保持逐字节不变** —— `Link` 把文件名拍平到输出目录根，因此所有 `AppContext.BaseDirectory/<脚本名>` 调用点与 `installer/Test.ps1` 的载荷断言一行都不用改。

| 改动 | 内容 |
| --- | --- |
| 文件 | `git mv` 两个脚本 → `src/AxmolHub.Core/Scripts/`（保留 CRLF 与历史） |
| App / Cli csproj | `<Content Include="../AxmolHub.Core/Scripts/*.ps1" Link="%(Filename)%(Extension)" ...>` —— 与 `manifests/`、`licenses/` 完全同一种写法 |
| Checks | 5 处硬编码路径改指 `src/AxmolHub.Core/Scripts/` |
| `installer/Test.ps1` | 载荷清单**新增** `Verify-MicrosoftSignature.ps1` 断言（原先漏了这一项） |

**验收**：四个项目 `dotnet build` 全绿（0 Warning / 0 Error）；App 与 Cli 的输出目录根都出现两个 `.ps1`；`installer/Test.ps1 -Isolated` 10 项断言全过、`ResidualInstallDirectory=false`。

> **踩到的坑，值得单独记一笔**：App 的 `Content Include` 一开始写成 `../../AxmolHub.Core/Scripts/*.ps1`。App 在 `src/AxmolHub.App/`，`../../` 已经跳到仓库根，通配符匹配不到任何文件 —— 而 MSBuild **不会**因为通配符零匹配而报错，构建照样打印 `Build succeeded`，只是输出目录里静悄悄地少了两个文件。**新增跨项目 `Content Include` 之后必须去输出目录看一眼**，这类失败没有任何编译期信号。

**后续（P6，已做）**：删掉 WPF `AxmolHub.App` 时，这两个脚本已与 App 目录无关，Cli / Checks / installer 的引用**一处都没动**——这正是 P1 要买的东西。实际改动只有 `README` 的仓库结构图、`installer/*.ps1` 与两个 workflow 里的项目路径、以及文档里的历史引用。

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

#### 已落地（2026-10-02，见 §5.3）

上面的判断在实施中被证实，也被修正了几处。**修正的部分比预计的多**，值得逐条记下来，因为每一条都是"照 WPF 经验写会静默出错"的类型：

| WPF 里的东西 | 直接搬会怎样 | 实际可行的写法 |
| --- | --- | --- |
| `TextBox` 模板里的 `ScrollViewer#PART_ContentHost` | **编译期硬报错** `AVLN2205: Required template part with name 'PART_TextPresenter' must be defined` —— 这是少数会当场告诉你错了的 | 必须放 `TextPresenter Name="PART_TextPresenter"`（外嵌 `ScrollViewer Name="PART_ScrollViewer"`），与官方 Fluent 模板一致 |
| `HorizontalScrollBarVisibility` / `VerticalScrollBarVisibility` | `AVLN2000: Unable to resolve ... on TextBox` | 它们是**附加属性**，要写 `ScrollViewer.VerticalScrollBarVisibility` |
| `ControlTemplate.Triggers` + `Setter TargetName=` | 静默不生效（Avalonia 根本没有 `Trigger`） | `Style Selector="Button:pointerover /template/ Border#Frame"` —— 伪类 + `/template/` 部件定位 |
| `IsChecked` / `IsEnabled=False` / `IsKeyboardFocused` / `IsHighlighted` | — | `:checked` / `:disabled` / `:focus` / `:pointerover`。另注意 `:selected`（列表选中）与 `:checked`（可切换）是两回事 |
| `FocusVisualStyle="{x:Null}"` | — | `FocusAdorner="{x:Null}"` |
| `Setter Property="RenderTransform" Value="rotate(90deg)"` | 断言若照 WPF 写成 `is RotateTransform` 会**永远假失败** | 它解析成 `TransformOperations`（矩阵），比对要走 `.Value`（`Matrix`）。**注意属性名是 `.Value` 不是 `.Matrix`** —— 后者不存在，这个我试错了两次 |
| `PasswordBox` | 类型不存在 | `TextBox` + `PasswordChar`；视觉差异用类样式（`Classes="password"`）表达 |
| `DataGrid.CanUserAddRows` / `RowHeight` / `AlternatingRowBackground` / `SelectionUnit` | 部分属性在 Avalonia 的 DataGrid 上不存在 | 只用确实存在的属性；行悬停/选中色画在行模板内的 `Rectangle#BackgroundRectangle` 上，且**主题自带 Opacity**，覆盖时必须显式归 1 |
| 同名 `DataGrid` | 不是隐式可用 | `Avalonia.Controls.DataGrid` 是**独立包**（12.1.2，比核心 12.1.3 落后一个补丁，靠 `>=` 让 NuGet 正常解析），且要单独 `<StyleInclude Source="avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml" />` |
| `ThemeDictionaries` 里的资源查找 | 用可视化元素的 `TryFindResource(key, out _)` **一律解不出值**（看不到 `Application.Resources` 的 `ThemeDictionaries`） | 必须带变体：`((IResourceHost)Application.Current).TryGetResource(key, app.ActualThemeVariant, out _)`。这条是本轮最花时间的一个坑，见 §5.3 |
| 模板部件名（`PART_*`） | 猜错就静默失效 | 全部对着 12.1.3 的官方模板/源码核对过：ComboBox `PART_Popup` / `PART_ItemsPresenter` / `SelectionBoxItem`；ScrollBar `Track#PART_Track`（`Thumb`/`IncreaseButton`/`DecreaseButton` 都是可空属性 → 无翻页按钮的 Track 合法）；Expander `ToggleButton#ExpanderHeader` + `Border#ExpanderContent` / `PART_ContentPresenter`；ComboBoxItem `PART_ContentPresenter` |

**一个必须记住的操作性后果**：上表里只有 `PART_TextPresenter` 这一条会在**构建时**报错。其余全部是**运行期静默失效** —— 样式写了、编译过了、窗口里就是没变。所以 P3 的验收不能是"构建通过"，必须是在运行期从可视化树里把断言读出来（§5.3）。

### 3.2 `MessageBox` 的调用点

**实测数目（2026-10-02，P4 期间按源码重数）：`MessageBox.Show(` 共 8 处**（`MainWindow.xaml.cs` 5、`AndroidReleaseWindow.cs` 2、`App.xaml.cs` 1）。本节标题此前写的是"23 处"，是个未经核对的估计值；同一批文件里另有 4 处文件/文件夹选择器（`new OpenFileDialog` 2、`new SaveFileDialog` 1、`new OpenFolderDialog` 1，见 §3.3）。**这两个数字都按 `src/ --include=*.cs` 数出，不含 `bin/`、`obj/` 下的构建产物** —— 早先一次计数把它们算了进去，所以偏大。

Avalonia 不提供 `MessageBox`，所以这不是"换一个 API"，而是"要有一个东西"。三个选项：

| 方案 | 代价 | 评价 |
| --- | --- | --- |
| 自建 `HubDialog` 窗口（复用主题） | ~150 行 + 8 处调用点改写 | **已采纳**（2026-10-02，见 §5.4）：仓库本来就手写全部控件样式，自建与既有风格一致，且不引入外部依赖 |
| 引入 `MessageBox.Avalonia` 等第三方包 | 一个长期依赖 + 一套独立视觉 | 与"手写主题"的既有取向冲突 |
| 全部改成内联错误条 | 交互语义全变 | 过度设计，会改变用户已习惯的反馈方式 |

注意现有调用有一处需要单独处理：`App.xaml.cs:94` 用 MessageBox 报启动失败 —— 此时窗口可能还没建起来，自建对话框需要能独立于主窗口显示。（`HubDialog.ShowAsync` 的 `owner` 参数可空，正是为这条路径；见 §5.4。）

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

**已落地（2026-10-02）**：选了**矢量路径**这一条，理由不只是"省事"：

- 路径把"字体是否装得上"这个依赖整个删掉了。内嵌 `.ttf` 只解决了分发，没解决**字形本身的授权与体积**；而 4 个图标不值得开这个口子。
- 4 个私有区字形（`&#xE8A5;` / `&#xE7B8;` / `&#xE90F;` / `&#xE713;`）换成 7 个 `StreamGeometry` 资源（4 个导航图标 + 收缩箭头 + 对勾 + 右向箭头），统一放在 **0–24 坐标系**里，配合 `Stretch="Uniform"` 即可任意缩放 —— HiDPI 与三平台观感一致一并解决。
- 对勾那条尤其值得注意：WPF 版是**用「✓」字形**画的，而 `Path` 画的对勾与字号/字体解耦，不会再出现"某种字体下对勾偏移半个像素"的漂移。

字体回退链按 §3.4 的预判落地为两条资源（`Hub.Font.Ui` / `Hub.Font.Mono`），中文排在 `Segoe UI` 之后、`sans-serif` 之前：`Segoe UI, Microsoft YaHei UI, PingFang SC, Hiragino Sans GB, Source Han Sans SC, Noto Sans CJK SC, DejaVu Sans, sans-serif`。**这里有一个仍未解决的问题**：Linux 上 `Noto Sans CJK` 未必预装（对应 §7 第 4 条），链条最后落到 `sans-serif` 时能否出中文取决于发行版，**目前只有"链条写对了"的证据，没有"目标发行版上真出中文"的证据**。

### 3.5 `--smoke` 无头截图是安装验收的依赖项

`App.xaml.cs:80-86` 用 `RenderTargetBitmap` + `PngBitmapEncoder` 渲染主窗口存成 PNG。**这不是调试功能**：`installer/Test.ps1` 用 `--smoke` 验证自包含版能启动。（2026-10-02 起 CI 里已不再跑无头 GUI，见 [ci.md §2.2](ci.md) —— 所以它现在的唯一自动化消费者是安装验收脚本。）

这段代码还挂在一个 WPF 专有事件上：`window.ContentRendered`。**Avalonia 没有 `ContentRendered`**，等价物要自己搭（`Window.Opened` + `Dispatcher.UIThread.Post`，或 `RequestAnimationFrame` 等首帧真正渲染完成）。这里有个**会静默降低证据强度的坑**：截早了会得到一张空白窗口，而"进程能启动并退出 0"这个断言**照样通过** —— 错误不会被 CI 抓到。迁移时应顺手补一条"截图非空白"的断言（例如解码后检查像素方差）。

Avalonia 侧两条路：

1. `Avalonia.Controls.RenderTargetBitmap` —— 最接近现有写法，但需要真实的渲染后端（Windows 上 Win32 后端可用；Linux 需要 X11 或 Xvfb）。
2. `Avalonia.Headless` 包 —— 提供无显示环境下的渲染与帧捕获。要在无 GPU / 无显示器的 CI 上跑三平台 GUI 截图，这条路更可靠。

**必须在动主窗口之前用 spike 定下来**，因为迁移后的安装验收脚本和 CI 都挂在它上面。建议：Windows 安装包验收继续用真实后端截图（保持现有证据强度），三平台 CI 的 GUI 截图走 Headless。

**spike #1 结论（2026-10-02，P4 期间）**：

- **真实后端这条路在本机跑通了，且证据比 WPF 版强。** `--smoke` 已按 Avalonia 重写（`src/AxmolHub.App/Services/SmokeRunner.cs` + `SmokeCapture.cs`）：`RenderTargetBitmap.Render(window)` + `PngBitmapEncoderOptions.Default` 存 PNG。实测主窗口 **760x440，distinct=202，variance=583.13，退出码 0**。
- **首帧信号的自建方案**：`Opened` + `Dispatcher.UIThread.Post(..., DispatcherPriority.Loaded)`，之后 `UpdateLayout()` + `RunJobs()`。另外挂了 15 秒兜底定时器（自动化里"挂住"比"失败"更难查）。
- **补上了非空白断言，并且做了负向对照。** 判据是亮度方差（`FrameStats.IsBlank`）：实测**真实窗口图** distinct=489 / variance=614.65 → 放行；**纯色窗口图** distinct=1 / variance=0.0000 → 拦下。负向对照走的是同一条真实渲染管线（一个 `WindowDecorations.None` + 纯黑背景的窗口），不是合成的像素缓冲 —— 只证明判据会放行好图不算数，得证明它**真的会拦下坏图**。
  - 顺带修掉一个自造的误判：判据最初用"颜色种数 ≥ 8"当主条件，结果一张**只有两种颜色的图**会被判成空白。方差才是主判据，颜色种数降级为弱兜底（< 2）。**这个误判是自检跑出来的，不是推出来的。**
- **`Avalonia.Headless` 这条路已实测可行（2026-10-02）**，但它**还没有接进 CI**。用一次性 spike（`artifacts/spike-headless/`，**不进仓库**）渲染**真实的产品主窗口**验证：

  ```csharp
  AppBuilder.Configure<AxmolHub.App.App>()
      .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
      .UseSkia()
      .SetupWithoutStarting();
  var window = new MainWindow(); window.Show();
  Dispatcher.UIThread.RunJobs();
  window.CaptureRenderedFrame()   // → WriteableBitmap?；返回 null 表示什么都没渲染
  ```

  实测 **760x440、distinct=468、variance=481.29、退出码 0**，且**该 spike 是在无显示沙箱里跑通的**（不是"有桌面才算过"）。两个要点：
  - **`UseHeadlessDrawing = false` 是必需的** —— 只有它才走真实 Skia 渲染，`CaptureRenderedFrame` 才有像素可读；默认的 headless drawing 拿不到有内容的帧。
  - `CaptureRenderedFrame()` **返回 null 本身就是一种失败信号**（"什么都没渲染"），值得直接当断言用，而不是只看 PNG 是否存在。
  - 与真实后端的同一窗口对比：`--smoke` **17956 字节 / variance 583.13**，headless **16886 字节 / variance 481.29**，两者都是 760x440 且都非空白。差异来自两条渲染路径的字形栅格化，属预期。
- **`RenderTargetBitmap` 的一个 API 事实**（照 WPF 经验找会找错）：它**没有**公开的 `CopyPixels(PixelRect, IntPtr, ...)`。唯一可用的读像素入口是 `Bitmap.CopyPixels(ILockedFramebuffer)`，所以要开一个 `WriteableBitmap` 用 `Lock()` 拿帧缓冲。读回保存后的 PNG 再统计，断言的是**落到文件里的那张图**，编码器写坏也能发现。

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
| **P1** | 两个 PowerShell 包装移到 `src/AxmolHub.Core/Scripts/`（§2.3）—— **已完成 2026-10-02** | 四项目 `dotnet build` 全绿；App 与 Cli 输出目录根含两个 `.ps1`；`Test.ps1 -Isolated` 全过 | 不变 |
| **P2** | 新建 `AxmolHub.App` 骨架（`net8.0`、不带 `-windows`）—— **已完成 2026-10-02**，落地内容与实测见 §5.2 | 四平台 `dotnet build` 绿；本机实跑窗口标题为 `Axmol Hub (Avalonia)`；三个 RID 发布各自带上正确的原生资产 | 新增**无条件**构建步骤（WPF 那步的 `if` 保留到 P6） |
| **P3** | 主题层重写（§3.1）：14 个控件样式 → Avalonia 选择器；补浅色主题；换掉图标字体（§3.4）—— **已完成 2026-10-02**，落地内容与实测见 §5.3 | 一个"控件画廊"窗口在三平台观感一致 | 不变 |
| **P4** | 基础件：`HubDialog`（替 8 处 MessageBox，§3.2）、`StorageProvider` 异步包装（§3.3）、`--smoke` 迁移（§3.5）—— **三个基础件已完成 2026-10-02**，落地内容与实测见 §5.4；**CI 的三平台 GUI 烟雾尚未接线**（Headless 可行性已实测） | 三平台各自能截图存 PNG（**目前只有 Windows 有实测证据**）；CI 的 GUI 烟雾步骤扩展到三平台（**未完成**） | GUI 烟雾进三平台矩阵（**未完成**） |
| **P5** | 逐窗口迁移。**2026-10-02 起**：本地化前置项 + 外壳（§5.5）、设置页（§5.6）之后，**四页已 1:1 复刻 WPF 版并搬入 `HubWorkspace`（§5.7）** —— 项目 / 引擎 / 工具链 / 设置四页与四个对话框窗口（`BuildProgressWindow` / `BuildTargetDialog` / `ModuleWindow` / `AndroidReleaseWindow`）均已就位。**顺序修正**：原建议按窗口规模排（`ModuleWindow` → `BuildProgressWindow` → `AndroidReleaseWindow` → `MainWindow`），实际改为**先外壳与页面、后对话框窗口** —— 原因是页面是 `UserControl`、与外壳无关，可以独立迁移且不阻塞于 B2；而三个对话框窗口本身就是"从 MainWindow 调起来"的，先把宿主立住更省事。**仍缺**：`--smoke-all/-run/-build`（切换数据根已在 §5.8 补齐） | 每个页面在三平台人工过一遍 | 顺序调整（见 §5.5）；用户反馈"先复刻 WPF 版式与功能"（见 §5.7） |
| **P6** | 原计划两项：平台化设置/数据目录（§4.1）、删除 WPF `AxmolHub.App`。**2026-10-02 只做后一项**，因为它是 `--smoke-pages` 补位、图标与 Velopack 归位、目录回归 `AxmolHub.App`、以及 `README` / `installer/*.ps1` / 两个 workflow / 全部历史引用更新的前置（§5.10）。**前一项仍未做**：`PreferencesStore` 的三级优先已经平台无关，但默认目录仍写死 `%LocalAppData%`，macOS 上应该是 `~/Library/Application Support` | 四项目 `dotnet build` 全绿；五个验收开关全过；Windows 安装验收**需重跑**（见 [ci.md §5](ci.md) 的注） | 仓库只剩一个 GUI 项目，GUI 那一步的 `matrix.host == 'windows'` 特判随之消失 |

### 5.1 引入方式选型

四种可能的引入路径，逐条评估：

| 路径 | 做法 | 结论 |
| --- | --- | --- |
| **A. 原地换栈** | 直接改 `AxmolHub.App` 的 `TargetFramework` / `UseWPF`，逐文件换 API | **否**。迁移期间三平台全红，Windows 用户没有可用版本，CI 也失去那条绿线 |
| **B. 并行新项目** | 新建 `AxmolHub.App`，两套并存，逐窗口迁移，最后删 WPF | **选此**，即下文 P2–P6 |
| **C. XPF（商业 WPF 兼容层）** | 保留 WPF 源码，靠 Avalonia 的二进制兼容层跑在 macOS / Linux | **否**，见下 |
| **D. 混合宿主**（WPF 壳嵌 Avalonia 内容） | — | **不成立**：Avalonia 没有官方 WPF 互操作方案 |

**为什么排除 XPF。** 它是商业产品，官方定价：Internal 档 **€9,500/年**（仅组织内使用）、Business 档 **€29,500/年**（对外发行应用）、移动 / WebAssembly 起 **€124,500/年**（以上为厂商公开报价，非我方推断）。Hub 要分发到用户机器，对应 Business 档；而 Hub 是 MIT 免费项目，当前全仓唯一的三方运行库只是 Velopack 一个 MIT 包、零成本。更要紧的是**它只解决渲染，不解决分发** —— `.dmg` / 公证、AppImage、三平台 CI 验证，这些工程量在 B 与 C 两条路上都得做一遍。等于花钱买下"重写 UI"一项，其余照付。

**B 的骨架用官方模板起步**（`dotnet new install Avalonia.Templates` → `dotnet new avalonia.app`），但按仓库规矩改造：

- 模板 csproj 给的是 4 个包：`Avalonia` / `Avalonia.Desktop` / `Avalonia.Themes.Fluent` / `Avalonia.Fonts.Inter`，外加仅 Debug 生效的 `AvaloniaUI.DiagnosticsSupport`。**实际采纳时去掉了 `Avalonia.Fonts.Inter`**（理由见下），版本全部写死。
- **`Avalonia.Fonts.Inter` 没有采纳**：Inter 不含 CJK 字形，而 Hub 是中文优先的界面。模板会顺手生成 `.WithInterFont()`，等于把"默认字体渲染不出中文、靠系统回退兜"这个错误默认值固化下来。字体选型本来就是 P3（§3.4）要处理的事，此处不引入。少一个包，不需要了再按 §3.4 的结论加。
- **runtime DevTools 的包名已变，这是最容易踩的一个坑**：`Avalonia.Diagnostics` 停留在 **11.3.22**，**12.x 一个版本都没发**；12.1.3 官方模板用的是 **`AvaloniaUI.DiagnosticsSupport` 2.2.3**，入口是 `AppBuilder.WithDeveloperTools()`。照旧资料写 `Avalonia.Diagnostics` 会在 Avalonia 12 上拿到错的包。
- 模板默认 `.WithInterFont()`，而 **Inter 不含 CJK 字形** —— 中文界面不能只靠它，见 §3.4 与 §7 第 4 条。
- 模板的 `Program.cs` 已经是 `[STAThread] static void Main` → **正好是 Velopack 需要的形式**。这一项比 WPF 侧省事：WPF 为了让 `VelopackApp.Build().Run()` 能拦住生命周期钩子，不得不把 `App.xaml` 从 `ApplicationDefinition` 改成 `Page` 并设 `StartupObject`；Avalonia 侧天生自带 `Main`，只需在 `BuildAvaloniaApp().StartWithClassicDesktopLifetime(args)` 之前插一行 `VelopackApp.Build().Run()`。

### 5.2 P2 实际落地内容（2026-10-02）

`src/AxmolHub.App/`，7 个文件：

| 文件 | 说明 |
| --- | --- |
| `AxmolHub.App.csproj` | `net8.0`、`WinExe`、`RootNamespace=AxmolHub.App`、`AssemblyName=AxmolHub.App`、`ApplicationManifest=app.manifest` |
| `Program.cs` | `[STAThread] Main` → `BuildAvaloniaApp().StartWithClassicDesktopLifetime(args)`；`UsePlatformDetect()` + `LogToTrace()`，`#if DEBUG` 下加 `WithDeveloperTools()` |
| `App.axaml` / `App.axaml.cs` | `<FluentTheme />`，`RequestedThemeVariant="Default"`；`OnFrameworkInitializationCompleted` 里挂 `MainWindow` |
| `MainWindow.axaml` / `MainWindow.axaml.cs` | 骨架窗口，只有两段说明文字，不含业务逻辑 |
| `app.manifest` | 模板原样保留；DPI / 透明窗口行为依赖它，非 Windows 构建时被 SDK 忽略 |

**三个不那么显然的选择**：

1. **`RootNamespace` 与 WPF 版共用 `AxmolHub.App`**。P5 逐窗口迁移时源码可以原样搬过来，`namespace` 与 `x:Class` 都不用改；两个程序集在并行期互不引用，同名命名空间不冲突。`AssemblyName` 才区分（`AxmolHub.App`），避免 CI 产物重名。
2. **刻意不加 `-windows` / `UseWPF`**，也不加 `Avalonia.Fonts.Inter`（见 §5.1）。
3. **刻意还不接 Velopack**。骨架里没有 `VelopackApp.Build().Run()`、没有 `--smoke`、没有 data root —— 那是 P4 的宿主装配工作。P2 要回答的问题只有一个：「Avalonia + Core 在三平台是否构得起来」，把打包与宿主混进来会让这个问题的答案变模糊。接 Velopack 只需在 `Program.cs` 插一行，是纯增量。

**内容清单与 WPF 版逐行对齐**：`LICENSE` / `THIRD_PARTY_NOTICES.md` / `licenses/*.txt` / `Core/Scripts/*.ps1` / `manifests/*`，写法与 `AxmolHub.App.csproj` 完全相同。**图标资源刻意没搬**：`Assets/` 当时属于 `AxmolHub.App`，跨项目引用正是 P1 刚拆掉的那类耦合，等 P6 删除 WPF 版时一并搬过来 —— **已在 P6 落地**（`git mv` 搬入本项目，并加了 `ApplicationIcon`）。

**实测证据（2026-10-02，本机）**：

| 验证项 | 结果 |
| --- | --- |
| `dotnet build -c Release` / `-c Debug` | 均 `Build succeeded. 0 Warning 0 Error`（Debug 才走 `WithDeveloperTools()` 分支，两个配置都验过） |
| `dotnet publish -r linux-x64 / osx-arm64 / win-x64 --self-contained false` | 三个 RID 全部发布成功 |
| 各 RID 的原生资产 | linux-x64：`libSkiaSharp.so`、`libHarfBuzzSharp.so`；osx-arm64：外加 `libAvaloniaNative.dylib`；win-x64：`av_libglesv2.dll`（ANGLE）。**每个 RID 拿到的正是该平台该有的那几个** |
| 内容文件 | 三个 RID 输出目录均含两个 `.ps1`、`LICENSE`、`THIRD_PARTY_NOTICES.md`、`licenses/`、`manifests/` |
| 实际启动 | 进程存活；主窗口标题 **`Axmol Hub (Avalonia)`**，窗口句柄非零；`CloseMainWindow` 后 exit 0 |
| NuGet 闭包（实测，非估算） | **30 个包**（§8 原估算为 31 —— 因为去掉了 `Fonts.Inter` 且原估算含它）。其中 `Avalonia.BuildServices/11.3.2` 会被 12.1.3 拉进来，属构建期包 |
| Core / Cli / App 的闭包 | 仍为 0 / 0 / 1（`Velopack`），**Core 与 Cli 的离线冷构建未被破坏** |

**尚未做（后续阶段）**：Velopack 生命周期钩子、`--smoke` 与 data root、浅色主题、图标、窗口业务逻辑。**"能在 macOS/Linux 显示窗口"仍只有编译与发布级证据，没有运行级证据** —— 那要靠 P4 的 `--smoke` 或 CI 里的 `xvfb-run`。

### 5.3 P3 实际落地内容（2026-10-02）

`src/AxmolHub.App/` 新增 6 个文件，共 **1286 行**：

| 文件 | 行数 | 内容 |
| --- | --- | --- |
| `Theme/HubTokens.axaml` | 113 | `ThemeDictionaries` 下 `Dark` / `Light` 两套，各 **35 个**语义 token（合计 70 条资源登记） |
| `Theme/HubIcons.axaml` | 31 | **7 个** `StreamGeometry`（4 导航图标 + Chevron + Tick + ChevronRight），统一 0–24 坐标系 |
| `Theme/HubControls.axaml` | 483 | **10 个** `ControlTheme`：Button / TextBox / ComboBox / ComboBoxItem / CheckBox / RadioButton / ToggleButton（`HubExpanderHeader`）/ Expander / Thumb（`HubScrollThumb`）/ ScrollBar |
| `Theme/HubStyles.axaml` | 132 | **14 条**选择器：`Window` / `TextBlock` / `TextBlock.muted` / `TextBox.mono` / `TextBox.password` / `Border.card` / `Button.primary` / `Button.quiet` / DataGrid+ColumnHeader+Cell+Row / 2 条 `Rectangle#BackgroundRectangle` 模板覆盖 |
| `Views/ControlGalleryWindow.axaml` | 192 | 控件画廊窗口（P3 的验收件），每个被断言的元素都带 `x:Name` |
| `Views/ControlGalleryWindow.axaml.cs` | 335 | **37 条**运行期断言 + 报告落盘 |

覆盖的控件面与 WPF 版 `App.xaml` 一致：Button、TextBox（含 PasswordBox 等价物 `Classes="password"`）、ComboBox、ComboBoxItem、CheckBox、RadioButton、DataGrid(+Header/Cell/Row)、ScrollBar、Expander、TextBlock、Window。

**设计上的两个决定**：

1. **先有 token，再有样式。** WPF 版的 `#1E1E1E` / `#E4E4E4` 之类硬编码颜色是散在模板里的。这次先立一套语义词表（`Hub.Background` / `Hub.Surface` / `Hub.TextPrimary` / `Hub.Accent` / `Hub.RowSelected` …，共 35 个），10 个 `ControlTheme` 与 14 条选择器**只引用 token、不写字面颜色**。浅色主题因此是**给每个 token 填第二组值**，而不是"再写一套样式"——这是浅色主题能一次做成的唯一原因。
2. **`ControlTheme` + 类样式，而不是 WPF 的 `BasedOn` 叠加。** 按 `{x:Type T}` 键入的 `ControlTheme` 会**替换**控件的整套主题；`Button.primary` 这类类样式再在其上叠加，不需要也不应该写 `BasedOn`。这与 WPF 的"隐式 Style + 局部 Style 覆盖"是同一效果的两种表达。

**验收方式（本轮最值得保留的一条经验）**：§3.1 表里那些"静默失效"决定了验收不能靠肉眼看窗口。画廊窗口带一个 `verify-theme <reportPath>` 模式：它在 `Opened` + `Dispatcher.UIThread.Post(..., DispatcherPriority.Background)` 之后跑 37 条断言，**直接读可视化树**（模板部件是否存在、某个 `Rectangle` 的 `Fill` 是不是某 token 的值、`ScrollBar.Width` 是否等于 10），把结果写进报告文件，然后以 `failed == 0 ? 0 : 1` 退出。有一个 10 秒 `DispatcherTimer.RunOnce` 兜底，防止窗口没起来时挂死。

**实测证据**：

| 验证项 | 结果 |
| --- | --- |
| `--verify-theme` | **37/37 通过**，报告 `artifacts/probe/theme-verify.txt`，退出码 0 |
| 五个项目 `dotnet build -c Release` | 全部 `Build succeeded. 0 Warning 0 Error` |
| 三 RID 发布（Cli 与 Avalonia App 各 3 个） | win-x64 / linux-x64 / osx-arm64 **全部成功** —— 且这是**先有 linux-x64 的 Avalonia 发布成功，才有 Linux 观感一致的任何依据** |
| DataGrid 选中色覆盖 | 实测 `#ff3b3b3b` == `Hub.RowSelected`，确认覆盖住了 Fluent 的默认选中色 |
| 浅色主题 | 运行期切 `RequestedThemeVariant` 后窗口底色 `#ff1e1e1e → #fff4f5f7`，且与新变体下 `Hub.Background` 的值相符 |

**调试过程中被否掉的"假失败"（值得记住，因为它们会重复出现）**：

- 首轮 19/35。其中 **16 条**其实是**断言自身的 bug**：用了可视化元素的 `TryFindResource(key, out _)`，而它**看不到 `Application.Resources` 的 `ThemeDictionaries`**（缺变体参数）→ 每个 token 都解成 null。改走 `((IResourceHost)Application.Current).TryGetResource(key, app.ActualThemeVariant, out _)` 后立刻变绿。**这个坑没有编译期信号，值得单独立一条备忘。**
- ComboBox 的 `PART_ItemsPresenter` 一开始永远找不到 —— 因为 **Popup 的内容在未展开时根本没有实例化**。断言里要先 `IsDropDownOpen = true` 再找。
- ScrollBar 那条一度报"宽度不是 10"，原因是取到了**横向**那一条（它只设了 `Height`）。按 `Orientation` 过滤后正常。
- Expander 箭头"没旋转"，是把 `TransformOperations` 强转 `RotateTransform` 造成的假失败（见 §3.1 表）。

**仍未解决（不属 P3 范围）**：三平台**观感**一致目前只有"同一份 XAML + 三 RID 都能发布"这一级证据；真正的运行期证据（三个系统上真把画廊窗口跑起来）要等 P4 的 `--smoke` / CI `xvfb-run`。**中文 IME 输入（§7 第 3 条）本轮完全未触碰。**

---

**为什么 P3 排在 P5 前面**：主题是全局的。如果先把窗口一个个迁过来，每个窗口都会带着临时样式，最后还要再统一改一遍。先把 14 个控件样式定死，后面每个窗口的迁移就变成纯粹的机械替换。

---

### 5.4 P4 实际落地内容（2026-10-02，基础件部分）

P4 的三件基础件都在这一轮落地；**CI 的三平台 GUI 烟雾没有接**（见本节末尾"未完成"）。

| 交付物 | 内容 |
| --- | --- |
| `Views/HubDialog.axaml(.cs)` | 替 `MessageBox` 的对话框。`ShowAsync(owner, title, message, buttons, danger)`，`owner` 可空；按钮档 `Ok` / `OkCancel` / `YesNo`；结果枚举 `HubDialogResult` |
| `Services/Pickers.cs` | `StorageProvider` 的异步包装：`PickFolderAsync` / `PickFileAsync` / `SaveFileAsync`，统一返回 `PickResult(Cancelled / Picked / NotLocal)` |
| `Services/SmokeCapture.cs` | `--smoke` 的渲染与判据：`Capture` / `Measure` / `Analyze`，以及 `FrameStats.IsBlank` |
| `Services/SmokeRunner.cs` | 把 `--smoke` 挂到首帧之后，成败按退出码报告，另写一份 `<png>.evidence.txt` |
| `Services/HubHostOptions.cs` | 参数与宿主目录：`--data-root` / `--preferences` / `--smoke` / `--verify-theme` / `--verify-foundation` / `--gallery`，默认值与 WPF 版逐条对齐 |
| `Services/ThemeProbe.cs` | 从 `ControlGalleryWindow` 抽出的断言工具（`TokenColor` / `IsToken` / `Descendant` / `NamedDescendant` …），供 P3 画廊与 P4 自检共用 |
| `Views/FoundationCheckWindow.axaml(.cs)` | P4 的运行期自检窗口，**25 条断言** |
| `Theme/HubStyles.axaml` | 新增 `Button.danger`（WPF 版没有"危险操作"这一档，`MessageBoxImage` 只换图标） |
| `Program.cs` / `App.axaml.cs` | 参数解析前移到 `AppBuilder` 之前；四种模式各自选窗口，启动失败改走 `HubDialog` |

**实测结果（本机 Windows，Release）**：

| 验证 | 结果 |
| --- | --- |
| `--verify-foundation <report>` | **25/25 passed，退出码 0** |
| `--smoke <png>`（带 `--data-root` / `--preferences`） | **退出码 0**，PNG 17956 字节，`760x440 distinct=202 variance=583.13` |
| `--verify-theme`（P3 回归） | **37/37 passed，退出码 0** —— 抽取 `ThemeProbe` 没有改坏 P3 的断言 |
| 五个项目 `dotnet build -c Release` | 全部 `0 Warning(s) 0 Error(s)`；**无 NuGet 依赖变化，锁文件未变** |

自检里**值得单独记下的三条**：

1. **`HubDialogResult.Cancel` 必须是枚举的 0 值。** Avalonia 的 `ShowDialog<TResult>` 在窗口"没有带结果就关掉"（点标题栏 X）时返回 `default(T)`。把 `Ok` 放在 0，用户关窗就会被读成"点了确定" —— 在"卸载"确认框上这是个真错误。自检里有一条守着它。
2. **`NotLocal` 必须与 `Cancelled` 区分开。** Avalonia 的选择器返回 `IStorageFolder`/`IStorageFile`，本地路径要靠 `TryGetLocalPath()` 取；取不到（云端盘、虚拟位置）而把 null 当路径用，会静默地创建一个相对路径工程。判定逻辑抽成了纯函数 `Pickers.Resolve`，因此可以被断言 —— 真去弹选择器没法进自动化，而恰恰是这三种结果的区分最难靠人工点几次发现。另有一条用**真实** `StorageProvider` 跑本地文件往返，证明扩展方法这一环接得上。
3. **判据本身也会误判，且只能靠跑出来。** `IsBlank` 最初以"颜色种数 ≥ 8"为主条件，自检立刻报出一张**只有两种颜色**的图被判成空白（`distinct=2, variance=16256.25`）。改成以亮度方差为主、颜色种数降为弱兜底（< 2）才对。**这条是自检抓出来的，不是推理出来的** —— 也正因如此，"非空白"这条防线才配叫防线。

**负向对照**：自检里有一个 `WindowDecorations.None` + 纯黑背景的窗口，走**同一条真实渲染管线**截图，断言它**被判为空白**（`distinct=1, variance=0.0000`）。只证明判据会放行好图是不够的。

**顺带记录的 API 漂移**（照旧资料写会拿到过时 API）：12.1.3 里 `Window.SystemDecorations` 已标记过时，改名 `WindowDecorations`（枚举同名）；`Bitmap.Save(string)` 也过时了，PNG 要走 `Save(path, PngBitmapEncoderOptions.Default)`。`RenderTargetBitmap` **没有**公开的 `CopyPixels(PixelRect, IntPtr, ...)`，读像素的唯一入口是 `Bitmap.CopyPixels(ILockedFramebuffer)`。

**未完成 / 明确不在本轮**：

- **CI 的三平台 GUI 烟雾没有接。** 可行性已经实测掉了（`Avalonia.Headless` 在无显示沙箱里能渲染真实主窗口，见 §3.5），**但本轮没有建常驻 harness 项目、也没有改 `ci.yml`** —— 因为这两件事都会引入一个仓库级依赖与新项目，且**在此地无法验证 CI 上是否全绿**。所以 P4 验收标准里"三平台各自能截图存 PNG"目前**只有 Windows 一条证据**。留给下一步时要带的两个决定：① headless harness 是独立项目还是放进 `tests/`；② `Avalonia.Headless` 这个测试期依赖是否入锁文件。
- **`--smoke-all` / `--smoke-run` / `--smoke-build` 三种模式留在 WPF 侧**：它们依赖 `MainWindow` 的业务逻辑（`BuildEvidenceAsync` / `RunProjectAsync` / `RenderUiEvidenceAsync`），随 P5 一起迁。
- **`--data-root` / `--preferences` 已被解析，但 Avalonia 的 `MainWindow` 还是骨架窗口、尚未消费它们。** 参数契约先立住，取值随 P5 接。
- **8 处 `MessageBox` 与 4 处选择器的调用点改写属 P5。** 本轮交付的是替身与基础件，不是替换动作本身。

### 5.5 P5 实际落地内容（2026-10-02，第一个增量）

P5 一开始撞到的不是"窗口怎么搬"，而是两个**前置项**。先把它们说清楚，因为它们改变了排期。

#### 前置项一：`--data-root` / `--preferences` 真的接上了

P4 结束时这两个开关只是被解析、没人消费（§5.4 已注明）。现在 `App.Start` 会读 `PreferencesStore`、
应用语言、再按**三级优先**定数据根：**命令行 > 设置文件 > 默认目录**，与 WPF 版一致。

为此改了 `HubHostOptions`：原来它在解析阶段就把 `--data-root` 兜底成默认值，
那样一来**没法区分"用户显式传了"和"没传"**，设置文件里的值会被默认值永久压住。
现在只保留原样值（`DataRootArgument`，可为 null），解析留给 `ResolveDataRoot(preferences)`。

#### 前置项二：本地化必须先于页面

WPF 的页面文字全走 `{DynamicResource}`，由 `Texts.cs`（206 行）在启动时灌进 `Application.Resources`。
Avalonia 侧必须先有等价机制，页面才谈得上"复制过来就能用"。

处理方式是**单一定义、两个客户端各留一层薄适配**，与 ADR-0001 对"工具只定义一次"的要求同源：

| 位置 | 内容 |
|---|---|
| `src/AxmolHub.Core/HubTexts.cs` | **190 条文案的唯一副本** + `Get/Normalize/IsSupported/Keys`。只有数据与查表，不含任何 UI 框架类型，Core 的零依赖性质不受影响 |
| `src/AxmolHub.App/Texts.cs` | 从 206 行缩到 37 行的 WPF 适配层：把 `HubTexts.Keys` 灌进 `Application.Resources`，既有的 `{DynamicResource X}` 与 `LocalizedValueConverter` 调用点一字未改。**已随 WPF 版在 P6 删除** —— 迁移期它存在的唯一目的就是让两版共用一份文案，任务完成后没有留下的理由 |
| `src/AxmolHub.App/Localization/HubStrings.cs` | Avalonia 适配层，做同一件事（写 Avalonia 资源字典）+ 对应的 `IValueConverter` |

**为什么不直接把 190 条复制进 Avalonia 版**：迁移期两版并存，复制两份等于让中英在迁移期间静默分叉 ——
而迁移期恰恰是最容易顺手改文案的时候。

（起初是 183 条；第二个增量补了 7 条，把原先硬编码在 C# 里的界面文字收回 `HubTexts`，见 §5.6。）

#### 外壳与第一个页面

| 交付物 | 说明 |
|---|---|
| `MainWindow.axaml(.cs)` | 从 P2 的三行占位骨架换成真外壳：左侧 218px 导航（项目/引擎/工具链/设置）+ `ContentControl` 页面宿主 + 底部状态栏。**形态刻意与 WPF 版一致**，理由见文件顶部注释 |
| `Views/Pages/EnginesPage.axaml(.cs)` | 第一个迁移的页面：`DataGrid` over `StateStore` + 导入/设为默认/移出列表 + 空态文案。**只依赖 Core 与 `Pickers`/`HubDialog`，不认识宿主窗口** |
| `Views/ShellCheckWindow.axaml(.cs)` | `--verify-shell` 的运行期自检，第一个增量时是 **31 条断言**（当前 **68** 条，见 §5.8） |

**页面顺序为什么改**：原计划按窗口规模排（`ModuleWindow` → `MainWindow`），但页面是 `UserControl`、
与外壳无关，可独立迁移；而三个对话框窗口本来就是"从主窗口调起来"的，先把宿主立住更省事。
选引擎页当第一个也不是因为它最简单，而是因为它**只依赖 `StateStore`**，
能在**没有引擎、没有工具链**的机器上真跑一遍"导入 → 落盘 → 重新读出"这条写路径。

#### 实测结果（Windows x64，本机，第一个增量当次）

| 验收 | 结果 |
|---|---|
| 五项目 `dotnet build -c Release` | 全部 `0 Warning(s) 0 Error(s)` |
| `--verify-shell` | **31/31 passed**，exit 0（当时；当前 **68**，见 §5.8） |
| `--verify-theme`（P3 回归） | 37/37 |
| `--verify-foundation`（P4 回归） | 25/25 |
| `--smoke`（真实产品窗口） | exit 0，`1000x640 distinct=288 variance=218.46`，PNG 28151 字节 |

#### `--verify-shell` 断言了什么（以及为什么必须这么断言）

它要防的失效模式**全都是静默的**，构建期一条都拦不住：XAML 里 `{DynamicResource Foo}` 少了 key
只会显示空白；导航事件没接上只会"点了没反应"；页面被反复重建只会"选中项偶尔丢失"。

最关键的一条是**源码扫描**：枚举 `src/AxmolHub.App/**/*.axaml`（先摘掉 XML 注释，
免得注释里提到的 key 造成误报）里全部 `{DynamicResource X}`，要求

- `Hub.*` 开头的必须在运行时资源字典里解析得出；
- 其余必须在 `HubTexts` 里存在，**并且**已灌进运行时资源字典。

**已做负向对照**：把主窗口的 `{DynamicResource Subtitle}` 改成 `{DynamicResource SubtitlesTypo}`，
重编译**构建照样 0 error**（正说明这类错构建期拦不住），`--verify-shell` 则报 2 条 FAIL、exit 1；
改回后回到 31/31。

**另一条被"跑一遍"抓出来的**：第一版断言把"值恰好等于键"也当成缺项，于是 62 条正常文案被误报 ——
因为很多键的英文文案本来就和键同名（`Projects` / `Version` / `Channel` …）。
"值等于键"只在**中文**侧是缺项信号。修正后另加一条反退化断言（中英两侧确有超过半数的文案不同），
防的是"有人把英文那列整列粘成中文"这种界面能显示但没本地化的情况。

#### 未完成（第一个增量结束时）

- **P5 其余三个页面**（项目 / 工具链 / 设置）—— `NavigateTo` 目前对它们返回一个显式的"尚未迁移"占位页，
  刻意留成显眼状态而不是空白页（空白会被读成 bug）。
- **8 处 `MessageBox` 与 4 处选择器的调用点改写**。
- **`--smoke-all` / `--smoke-run` / `--smoke-build` 三种模式**（依赖 MainWindow 的业务逻辑）。
- **`--verify-shell` 进 CI**：与 P4 的 GUI 烟雾同一个前置 —— 常驻 headless harness 项目 + `ci.yml` 改动，
  目前只能在开发机上跑。
- **WPF 版 `Texts.cs` 的删除**：它在 P6 随 WPF 版一起消失。在那之前它是 `HubTexts` 的一层适配，
  两边不会分叉。

---

### 5.6 P5 第二个增量：设置页 + 本地化活体验收（2026-10-02）

第二个页面选**设置页**而不是工具链页，理由是它是**整条本地化管线的活体验收台**：
切语言要求就地重灌资源字典并让**已经存在的控件**换文字，而
"Avalonia 的 `DynamicResource` 会不会跟着资源字典变更重新解析"**只能实测**——
编译期、绑定期、甚至"文本 key 都存在"这类静态断言都证明不了它。

#### 交付物

| 交付物 | 说明 |
|---|---|
| `Views/Pages/SettingsPage.axaml(.cs)` | 语言 / 存储与项目 / 编辑器三张卡片。语言下拉的语言标记放在 **XAML 的 `Tag`** 上，而不是按下标取值 |
| `Localization/HubStrings.cs` | 增加 `Get` 与 `Apply`；`Apply` 就地重灌资源字典，`DynamicResource` 由此重解析 |
| `MainWindow.axaml.cs` | 接设置页；新增 `ApplyLanguage()`、`SyncNavigation()`、`OpenFolder()` |
| `HubTexts.cs` | 补 7 条：占位页说明、数据根说明、非本地路径、四种编辑器选择文案 —— 把原先硬编码在 C# 里的界面文字收回单一定义 |
| `Views/ShellCheckWindow.axaml.cs` | 断言 31 → **57** 条 |

#### 三个设计决定，以及为什么

1. **语言映射改由 `Tag` 驱动**。WPF 版把 `0 ↔ zh-CN / 1 ↔ en-US` 这个映射散在 4 处
   （`SelectedIndex = … ? 1 : 0` 写了四遍），多一种语言或调一下顺序就会静默错位。
   现在映射只有一处，就在数据本身旁边；`SelectedLanguage` 对未知/未选一律回落中文。
2. **`NavigateTo` 同时同步左侧高亮**。WPF 的 `SelectPage` 本来就把四个 `IsChecked` 一起置位 ——
   第一个增量漏了这一步，于是"显示 A 页、左边高亮 B 页"。
   **这个错编译期、绑定期、以及当时所有导航断言都发现不了**：本轮是靠**逐页截图的肉眼核对**抓到的，
   随后才补上"页面与导航一致"的断言。这也是把"逐页渲染 + 留 PNG"当成产品级验收的一部分、而不是调试技巧的原因。
3. **验收模式强制中文起步**。断言里写的是具体文案，若跟着用户的语言设置走，
   同一个二进制会在这台机器上过、在那台机器上挂。`App.Start` 因此在 `--verify-shell` 下跳过用户设置，
   自检自己负责真切一次语言再切回来。
   同理，自检用的**设置文件**也落在临时目录（数据根内部）—— 切语言要写盘，
   用默认路径就会改掉用户 `%LocalAppData%\AxmolHub\hub-settings.json` 里的语言。

#### 实测结果（Windows x64，本机）

| 验收 | 结果 |
|---|---|
| 五项目 `dotnet build -c Release` | 全部 `0 Warning(s) 0 Error(s)` |
| `--verify-shell` | **57/57 passed**，exit 0 |
| 逐页真实渲染 | 四页各自非空白：Projects 17999 B / Engines 33143 B / Toolchains 18546 B / Settings 49326 B，均 1000x640 |
| `--verify-theme`（P3 回归） | 37/37 |
| `--verify-foundation`（P4 回归） | 25/25 |
| `--smoke`（真实产品窗口） | exit 0，`1000x640 distinct=288 variance=218.46` |
| `--check-cli-json`（P0 回归） | exit 0 |

#### 反向对照（两次，都在最终修订版上重跑）

- **去掉 `HubStrings.Apply(language, Application.Current!)`** —— 构建照样 `0 error 0 warning`，
  自检报 **6 条 FAIL**、exit 1；三条"已存在的控件跟着换文字"的断言全在其中。改回后 57/57。
- **去掉 `SyncNavigation(name)`** —— 构建照样 `0 error`，自检报 **3 条 FAIL**、exit 1
  （"页面与导航不会各说各话"）；改回后 57/57。

两次都印证同一条：**这类失效构建期拦不住，只能靠运行期读真实控件**。

#### 本轮踩到的两个坑

- **XML 注释里不能出现 `--`**：新写的 `.axaml` 注释里写了 `--verify-shell`，
  构建报 `AVLN1001`（"An XML comment cannot contain '--'"）。改成"verify shell 自检"即可。
  这个坑和 §3.1 那条"样式写错不报错"正好相反 —— 它属于**报得很响**的一类。
- **断言自己写错**：重构导航断言时把"页面键"当成"文案键"用，于是引擎页的导航文案被期望成
  `Engines`（实际是 `Installs`，WPF 版沿用的词）。断言当场红了 —— 这正是断言该有的样子：
  写错的断言要**响亮地**错，而不是默默通过。

#### 仍未完成（第二个增量结束时）

- **切换数据根**：WPF 版的做法是重建整个窗口，与外壳形态耦合，当时判断要等 B2 定稿再做；
  设置页上那行说明文字如实写出了这一点。**（后已判定"耦合的是实现方式、不是需求"，见 §5.8）**
- **`--smoke-all/-run/-build`** 三种模式（依赖 MainWindow 的业务逻辑，本轮刚把逻辑搬进
  `HubWorkspace`，接线是下一步）。
- **`--verify-shell` 进 CI**：与 P4 的 GUI 烟雾同一个前置（常驻 headless harness + `ci.yml` 改动）。

---

### 5.7 P5 第三个增量：四页 1:1 复刻（2026-10-02）

用户看完第一个增量后给的判断是：**"布局和功能先复刻 WPF 的版本"** —— 界面里还留着
演示控件、版式也不对。这条修正改变了本轮的取舍：**先要"看起来一样、点下去一样"，
把验证脚手架的份量降下来**。

#### 结构上的一次搬迁

WPF 版把非视觉的东西全放在 `MainWindow.xaml.cs`（823 行）里 —— 四个页面是同一个窗口内的四个
`<Grid>`，字段天然共享，一个 `Refresh()` 能同时更新项目页的计数和工具链页的表格。
Avalonia 版把页面拆成 `UserControl` 之后，那些共享字段就失去了落点。于是新增
**`Services/HubWorkspace.cs`**，把 **WPF `MainWindow.xaml.cs` 非视觉的那一半整体搬过来**：
服务装配、当前选择、`ExecuteAsync`、以及每个按钮背后的操作。

好处不只是"能编译"：从 WPF 那种"一个窗口里的字段互相引用"变成
**页面捕获 `HubWorkspace.Changed` → 各自 `Reload()`**，跨页联动（选中项目 → 顶部平台卡 /
Android 设备条 / 工具链目标下拉）有了唯一落点。`Refresh()` 的"全量重画"语义**刻意保留**：
这些列表都很小，而增量通知要维护的对应关系比它省下的重画贵得多。

| 新增/改写 | 内容 |
|---|---|
| `Services/HubWorkspace.cs` | 服务装配 + 全部操作 + 事件（`Logged`/`Changed`/`StatusChanged`/`BusyChanged`/`Failed`/`ComponentsChanged`/`DevicesChanged`） |
| `MainWindow.axaml` | **逐格照抄** WPF：左 218px 导航（品牌 + 副标题 + 四个带图标的 RadioButton）、右内容区 `Margin=30,30,30,22`、三行（页面 / 状态栏 / **日志面板 Expander**） |
| `Views/Pages/ProjectsPage` | 统计卡 ×3、新建项目面板、项目表格、Android 设备条、九个操作按钮 |
| `Views/Pages/InstallsPage` | 取代 `EnginesPage`，补齐 WPF 的**七个**按钮（原实现只有三个） |
| `Views/Pages/ToolchainsPage` | 引擎版本条、模块概览、组件诊断（平台 / 工具表 / SDK 与 MSVC 按钮） |
| `Views/Dialogs/*` | `BuildProgressWindow`、`BuildTargetDialog`、`ModuleWindow`、`AndroidReleaseWindow` —— WPF 里它们**本来就是纯代码构建**，所以移植只是换类型 |
| `Services/ScratchDirectory.cs` | 自检产物统一落 `tmp/`（见下） |

#### 与 WPF 版的三处刻意差异

1. **导航图标用矢量 `Path` 而不是 Segoe MDL2 Assets 私有区字形** —— 那套字体只存在于 Windows，
   在 macOS / Linux 上会退化成方块（`HubIcons.axaml` 早就准备好了，这里才用上）。
   代价：WPF 那句"图标与文字的 Foreground 绑到 RadioButton"没法照抄（`Path` 用 `Fill` 不用
   `Foreground`），改成两条样式类，效果一致。
2. **左下角第二行按真实宿主机算**（`Windows x64` / `macOS arm64` / `Linux x64`），不写死
   `Windows x64`。第一行也从写死的 `AXMOL 2.11 LTS` 改成按默认引擎版本现算 ——
   写死版本号会在 v3 发布当天变成错的，正是 A1 那类问题。
3. **工具链页不再"进页就自动检测"**。WPF 的 `ShowToolchains` 会在切页时顺手跑一次
   `Verify toolchains`（子进程 + 网络）。那属于"用户看不见的代价"，改成就检测一次由
   「验证」按钮触发、选择平台变化时自动检测。

#### 临时产物落点改为仓库内 `tmp/`

用户给的约定：**临时文件写仓库的 `tmp/`，cache 类写 `cache/`，两者都已在 `.gitignore` 里**，
不要往外面的目录散落。`ScratchDirectory` 据此实现：从程序集位置往上找仓库根，找到就用
`<repo>/tmp`，找不到（安装后的自包含产物）才退回系统临时目录 —— 那条路径上没有 git 仓库可写，
自检也不该因为"找不到仓库"就失败。

#### 实测结果（Windows x64，本机）

| 验收 | 结果 |
|---|---|
| 五项目 `dotnet build -c Release` | 全部 `0 Warning(s) 0 Error(s)` |
| `--verify-shell` | **57/57 passed**，exit 0 |
| `--verify-theme` / `--verify-foundation` | 37/37、25/25（未被改坏） |
| `--check-cli-json` | exit 0 |
| 四页真实渲染 | 逐页非空白帧；**逐张人工看过**，版式与 WPF 版一致 |

#### 这一轮踩到的三个坑（都是"构建期不报、运行期才炸"的反例）

- **`DataGridTextColumn` 不继承所在页面的 `x:DataType`**：编译绑定会拿**页面类型**去找
  `Binding="{Binding Version}"`，构建期报 `AVLN2000`。每一列都要自己写 `x:DataType="core:ProjectEntry"`。
- **Avalonia 12 的 `IClipboard` 没有 `SetTextAsync`**：它变成了
  `Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(IClipboard, string)` 扩展方法，
  少一个 `using Avalonia.Input.Platform;` 就报"找不到方法"。同批变更还有
  `ScrollBarVisibility` 移到了 `Avalonia.Controls.Primitives`。
- **Avalonia 的 `TextBox` 没有 `ScrollBarVisibility` 属性**（滚动由内部 ScrollViewer 负责），
  从 WPF 照抄这两行会直接构建失败。

#### 这一轮暴露的一个自检自身的缺陷

本地化那组断言读的是 `RadioButton.Content?.ToString()`。导航项的内容从"一段文字"变成
"图标 + 文字"的 StackPanel 之后，`ToString()` 返回的是**类型名**，断言立刻从"有效"退化成
"恒假"。已改为 `NavLabel()` 从可视树里取文字。这类问题的共性是：
**断言依赖了控件的内部结构，而结构一变断言就悄悄失效** —— 好在它是响亮地失败，
而不是静默通过。

### 5.8 P5 第四个增量：切换数据根（2026-10-02）

四页复刻之后，唯一在**功能层面**仍缺的一块就是切换数据根。它此前被列为"与外壳形态耦合、
等 B2 定稿"，这个判断其实只对了一半 —— 耦合来自 WPF 的**实现方式**，不是需求本身。

#### 与 WPF 版的分歧：不重建窗口

| | WPF 版 | Avalonia 版 |
|---|---|---|
| 做法 | `new MainWindow(新根)` → `Show()` → `Close()` 旧的 | 只重建 `HubWorkspace`，窗口不动 |
| 页面 | 随窗口一起重建（构造函数里全部 new） | 清空页面缓存，按需重建 |
| 日志面板 | 新窗口天然是空的 | 显式清空（否则留着上一个根的日志，把人引到不看的目录） |
| 依赖外壳形态 | 是 | **否** |

换成"只重建工作区"之后，这件事不再依赖外壳形态，所以不必等 B2。代价是外壳要能承受
**工作区实例被替换**：`_workspace` 从 `readonly` 变成可变字段，事件在新实例上重新挂一遍
（因为事件属于实例，重复订阅不会让日志被追加两遍）。

#### 两条顺序上的硬要求

1. **先落盘，再换工作区**。换完就回不到旧实例了，所以写设置文件失败必须能**在旧状态上**
   就地报错；否则会留下"界面指向新根、设置文件还写着旧根"，下次启动又悄悄变回去 ——
   表现是"设置丢失"，而不是报错。
2. **页面缓存必须整批丢弃，不能只丢当前页**。四个页面各自持有工作区引用，
   只换当前页会让另外三页继续读一个已经 `Dispose` 的工作区。这一条漏掉时**构建照样
   0 error**，运行期也不一定立刻炸 —— 所以它是 §5.8 断言组的核心。

另外有操作在跑时禁止切换：操作持有旧根的下载器、日志与工具链目录，中途换根会让它往一个
已经不属于当前会话的目录里写。

#### 新增断言（`--verify-shell` 57 → 68）

| 断言 | 它防的是什么 |
|---|---|
| 设置页有"选择目录"按钮 | 防止它退回到一行"尚未迁移"的说明文字 |
| 切换后工作区数据根指向新目录 | 根真的换了，不是只改了界面文字 |
| 新数据根被真的建出来 | 只改字符串、不建目录，后续写入才会炸在别处 |
| 新根已写入设置文件 | 下次启动仍指向它（只换内存等于没换） |
| 当前页是**新建**的设置页实例 | 旧的持有已 `Dispose` 的工作区 |
| 重建出的设置页显示新根 | 重建了但读的还是旧值，同样是"看起来正常" |
| 切换后日志面板被清空 | 上一个根的日志留在界面上会误导 |
| 切换后引擎页显示空态 | 状态真的来自新根（新根是空的），而非残留旧根的两条 |
| 切到同一目录返回 false 且页面实例不变 | 重复确认不该白白重建一遍所有页面 |
| 拒绝把数据根切到盘符根 | 否则会在整个盘里建 `tools/` 与 `logs/` |
| 再切回原根同样有效 | 双向可逆，不是一次性的 |

#### 实测结果（Windows x64，本机）

| 验收 | 结果 |
|---|---|
| 五项目 `dotnet build -c Release` | 全部 `0 Warning(s) 0 Error(s)` |
| `--verify-shell` | **68/68 passed**，exit 0 |
| `--verify-theme` / `--verify-foundation` | 37/37、25/25（未被改坏） |
| `--smoke` / `--check-cli-json` | exit 0 / exit 0 |

**反向对照**：注释掉 `_pages.Clear()` 一行 → 构建 **0 error**（这类错构建期拦不住），
自检报 **3 条 FAIL**、exit 1（"当前页是新实例"、"重建出的设置页显示新根"、"引擎页显示空态"）。
改回后 68/68。

#### 仍未完成

- **`--smoke-all/-run/-build`** 三种无人值守模式。
- **`--verify-shell` 进 CI**：与 P4 的 GUI 烟雾同一个前置（常驻 headless harness + `ci.yml` 改动）。

---

### 5.9 P5 第五个增量：真操作验收 `--verify-ops`（2026-10-02）

触发点是一句诚实的自问：`HubWorkspace` 九百多行里，**到底哪些路径真的被执行过**？
答案让人不舒服 —— 只有列表刷新、导入、选择、页面切换这类轻量操作；
装引擎、装工具链、构建、运行、Android 打包**一次都没跑过**。前面四节的所有断言
验的都是**界面**，而且跑在夹具上。于是"功能齐了"一直只是**代码层面**的判断。

#### 成本边界是刻意划的

用户的原话是"挑最便宜的"。所以这一组的取舍写在最前面：**只跑不下载、不编译的操作**。
装引擎、装工具链、构建、运行、Android 打包会拉 GB 级数据或依赖完整工具链，
因此它们不是"验不过"，而是**明确记为跳过**并附上原因（报告里的 `SKIP`）。
一条让人以为跑过了的报告比没有报告更糟。

#### 跑什么

| 组 | 内容 |
|---|---|
| 闸门 | 数据根在隔离目录内、**不是**用户自己的 `%LocalAppData%\AxmolHub`、工具目录在数据根内部 |
| 工具链探测 | `DetectAsync` 全部 8 个组件；托管组件必须报 Missing |
| 引擎生命周期 | 导入 → 校验 → 设为默认 → 落盘 → 重新加载 → 移除（真引擎源码树） |
| 无效输入 | 空目录 / 一个文件 / 不存在的路径，三次都不得污染引擎列表 |
| 跳过声明 | 装引擎、装工具链、构建与运行、Android 打包，各附原因 |

#### 三条来自"不预设期望值"的设计

1. **不写死哪个引擎"应该成功"**。先看目录客观缺什么，再断言 Hub 的行为与之一致。
   写死期望值会让断言在换一台机器时变成噪音，而"Hub 怎么对待一个不完整的引擎"
   恰恰是这一组最想知道的。
2. **允许失败成为常态**。`ExecuteAsync` 刻意吞掉异常（UI 不该崩），
   所以验收只能通过 `LastError` 与状态观察；而"导入一个坏引擎"本身就是预期失败。
   为此给工作区加了 `SuppressDialogs` —— 失败弹窗在自动化里没人点，
   `HubDialog.ShowAsync` 的 Task 永不完成，**"真跑"会变成"挂死"**。它只关掉展示，
   置忙/日志/落盘/`LastError` 全部照旧。
3. **不传引擎目录时降级为跳过而不是失败**。这样它在一台没有引擎的机器上也能给出有意义的结果。

#### 实测结果（Windows x64，本机）

引擎用的是本机真实的两套源码树：`D:\dev\simdsoft\axmol2`（2.11.6）与 `axmol3`（3.0.0-alpha33）。

| 验收 | 结果 |
|---|---|
| `--verify-ops ./tmp/ops-check.txt <两个引擎目录>` | **22/22 passed, 4 skipped**，exit 0 |
| 同一命令不传引擎目录 | **10/10 passed, 5 skipped**，exit 0（正确降级，不是失败） |
| `--verify-shell` / `--verify-theme` / `--verify-foundation` | 68/68、37/37、25/25（未破） |
| `--smoke` / `--check-cli-json` | exit 0 / exit 0 |
| 五项目 `dotnet build -c Release` | 全部 `0 Warning(s) 0 Error(s)` |

两个正面结论：

- **"工具链不回退系统 PATH"这条策略真的落地了**。本机装着 VS 18、CMake 4.3.2、Ninja 1.12.1
  且都在 PATH 上，托管的 MSVC 与 Windows SDK 仍报 `Missing` —— 两者没有被合并。
  **但这条策略即将被反转**（用户 2026-10-02：Windows 上首选系统已安装的 Visual Studio，
  与 axmol 引擎自身选取 MSVC 的方式一致）。因此 `--verify-ops` **刻意没有**把
  "必须报 Missing"写成断言 —— 那会在规则改的那天以 FAIL 的形式报出，而它其实是规则变了
  不是代码坏了。它只断言"每项都给出了结论性状态"，实测值写在 `INFO` 里。
- **v3 的目录结构确实变了**：`axmol3` 被拒绝，理由是缺 `core/axmolver.h.in`，
  因为 **v3 把 `core/` 改成了 `axmol/`**。这是这一组撞出来的**真实缺陷**，
  也是 A2 从"待观察"变成"已证实"的依据（详见 `docs/hub-development-plan.md` 的 A2 实测）。
  读代码看不出来 —— 代码里只写死了路径，没有 v3 的样本可比。

#### 反向对照

把 `SuppressDialogs` 的闸门去掉（即恢复失败弹窗）→ 构建 **0 error**，
验收挂在"导入空目录"那一步等一个不会有人点的确认框，**12.7 秒后**由兜底计时器报
1 条 FAIL、exit 1。它证明那个开关不是多余的，也顺带修了兜底消息的措辞
（原先只写"窗口未打开"，会把"某个 await 没返回"误报成"窗口没打开"）。

#### 仍未完成

- **`--smoke-all/-run/-build`** 三种无人值守模式（去向已在 §5.10 交代）。
- **`--verify-shell` / `--verify-ops` 进 CI**：前者与 P4 的 GUI 烟雾同一个前置（常驻 headless harness）；
  后者还需要一台有引擎源码的机器 —— 它不是"没接线"，而是**当前不适合放 CI**。
  更适合它的位置是**发布前的手工验收清单**。

---

### 5.10 P6：删除 WPF 版（2026-10-02）

删除本身是一条命令，真正需要判断的是**删之前要不要先补上什么**。

#### 三个无人值守模式的去向

WPF 版 `App.xaml.cs` 有四个截图/无人值守开关。直接删会把其中三个一起带走，所以先逐个决定去向：

| WPF 开关 | 做什么 | 处置 |
| --- | --- | --- |
| `--smoke <png>` | 渲染主窗口一张图 | **已迁移**（P4，`Services/SmokeRunner.cs`），且补了"帧非空白"断言 |
| `--smoke-run <png>` | 跑一次运行、截图 | **由 `--verify-ops` 接管**：它真的跑、有断言、出报告，证据强度更高，不复刻截图版 |
| `--smoke-build <path>` | 跑一次构建、留证据 | 同上 |
| `--smoke-all <dir>` | 逐页 + 逐对话框截图，中英各一轮 | **拆开**：逐页那一半补成 `--smoke-pages`；对话框那一半（构建进度 / 模块 / Android 发布）依赖真实设备与签名配置，无人值守下拿不到稳定画面，**不复刻** |

#### 新增 `--smoke-pages <目录>`

`Services/PageShots.cs`，约 100 行。四页 × 中英双语 = **8 张 PNG**。两个细节值得记：

1. **它真切语言并落盘**（`DynamicResource` 是就地重解析的，不真切就看不到英文版面），
   所以结束时必须**切回原语言** —— 否则跑一次截图就把用户的界面语言悄悄改了。
   用默认设置文件时这会动到用户自己的 `hub-settings.json`，跑之前用 `--preferences` 指向临时文件。
2. **两道防假绿判据**。"进程退出 0"是躺着也能过的，所以第一道要求每帧非纯色
   （复用 `SmokeCapture.FrameStats.IsBlank`）；第二道数**不同内容的张数** ——
   八张全出来但只有 2 张不同 = 导航静默失效，只剩 4 张 = 切语言静默失效。
   这两种情况下第一道判据全绿，只有数哈希才抓得到。

**实测（本机，空数据根）**：`OK smoke-pages 8 张（不同内容 8 张）` exit 0。

**反向对照 ×2**（都验证"构建仍 0 error，只有运行期断言会红"这条性质）：

| 破坏方式 | 结果 |
| --- | --- |
| 注释掉 `window.NavigateTo(key)` | 8 张文件都在，**不同内容 2 张**，exit 1 |
| 注释掉 `window.UseLanguage(language)` | 8 张文件都在，**不同内容 4 张**，exit 1 |

两个数字与预测完全一致（2 = 每种语言一张，4 = 每页一张），说明这条判据真的在数东西。

#### 目录与项目名回归 `AxmolHub.App`

`AxmolHub.App.Avalonia` 里的 `.Avalonia` 后缀只在"与 WPF 版并行"期间有意义 —— 它唯一的作用是避免两个程序集的产物重名。WPF 版删掉后后缀就失去理由，于是 `git mv` 把目录与 csproj 都改回 `AxmolHub.App`。

**这次改名之所以能是纯重命名**：`RootNamespace` 一直是 `AxmolHub.App`（P2 就定下的，为了让源码原样搬过来），`.axaml` 里的 `x:Class` 与 `avares://` 也跟着它。所以**没有一处 namespace、x:Class 或资源 URI 需要改**，改的只有项目名与随之而来的产物名 `AxmolHub.App.exe`。

连带处理：

| 事项 | 变化 |
| --- | --- |
| `AssemblyName` | **删掉显式声明** —— 项目名已是 `AxmolHub.App`，它就是默认值；多写一份等于给将来的改名留第二处要同步的地方 |
| 产物名 | `AxmolHub.App.Avalonia.exe` → `AxmolHub.App.exe`（`installer/Build.ps1` 的 `--mainExe`、`installer/Test.ps1` 的载荷清单、两个 workflow 同步） |
| 反查仓库根 | `Services/ScratchDirectory.cs` 与 `Views/ShellCheckWindow.axaml.cs` 靠 `src/AxmolHub.App` 存在与否定位仓库根，路径同步更新 —— 这两处是"改目录名会静默失效"的地方 |
| `app.manifest` | `assemblyIdentity/@name` 同步 |

#### 删除范围

| 类别 | 内容 |
| --- | --- |
| 移动 | `git mv src/AxmolHub.App/Assets → src/AxmolHub.App.Avalonia/Assets`（目标目录当时还叫 `AxmolHub.App.Avalonia`，随后整体改名，见上） |
| 归位 | `<ApplicationIcon>Assets/hub-icon.ico</ApplicationIcon>` 与 `PackageReference Velopack` 进 Avalonia 项目；`VelopackApp.Build().Run()` 插到 `Program.cs` 的 `Main` 第一行 |
| 删除 | `src/AxmolHub.App/`（含 `bin` / `obj`） |
| 重指向 | `installer/Build.ps1`、`Build-Icon.ps1`、`Test.ps1`、`installer/README.md`、`ci.yml`、`release-windows.yml`、`README.md`、`THIRD_PARTY_NOTICES.md`、`Directory.Build.props`、`AxmolHub.Cli.csproj`、`MainWindow.axaml` 顶部注释、本文档的历史引用 |

**Velopack 钩子必须在 `Main` 的第一行**，连 `Console.OutputEncoding` 都要排在它后面 ——
这条路径会在安装/更新/卸载时被拉起，任何副作用都可能让安装过程弹出窗口。

**零额外改动的一处**：`Invoke-Axmol.ps1` / `Verify-MicrosoftSignature.ps1` 的引用一处未动。
P1 把它们从 App 目录搬进 `Core/Scripts/` 时买的就是这个 —— 删掉整个客户端项目，
Cli / Checks / `installer/Test.ps1` 的引用不受影响。

#### 仍未完成

- **Windows 安装链路必须重跑**：`installer/*.ps1` 的项目路径改了，但 `Test.ps1 -Isolated`
  自 P6 之后没再跑过。这条属发布前动作，见 [ci.md §5](ci.md) 的注。
- **GUI 的 `matrix.host == 'windows'` 特判**只剩 `AxmolHub.Checks` 与 `--check-cli-json` 两处，
  它们与 GUI 无关。

---

## 6. 与 CI 的衔接

P2–P5 期间 `ci.yml` 里是**两条 GUI 线并存**：

- `Build Avalonia app` —— **无 `if` 条件，三平台都跑**（P2 新增）。
- `Build WPF app and behavior checks` —— 带 `if: matrix.host == 'windows'`，仍然是 `net8.0-windows` 的编译验证。

所以"每推进一步删一个 `if`"的实现方式不是去改 WPF 那一步的条件，而是**让 Avalonia 那一步先在三平台亮起来**；P6 删掉 WPF 版时，带条件的那一步自然消失 —— **已发生**。现在只剩 `AxmolHub.Checks` 与 `--check-cli-json` 带 `if`，两者都与 GUI 无关（前者是 `net8.0-windows`，Core 里有 Windows 特有的断言）。细节见 [ci.md §2.2](ci.md)。

`release-windows.yml`（安装包 + 隔离安装验收）与 UI 无关，迁移期间保持不动 —— 这恰好是它的价值：**它是 Windows 侧"没有被迁移破坏"的独立证据**。P2 那一步改完时 `installer/Test.ps1 -Isolated` 重跑通过（10 项断言），证明打包链路未被新增项目影响。**但 P6 之后它必须重跑一次** —— 这一次它自己的项目路径改了（见 §5.10 末），不再属于"没被动过"。

---

## 7. spike 与待定项（P0 提出，逐条销项）

1. **`--smoke` 走哪条路 —— 两条都已定并已实测（2026-10-02，见 §3.5 与 §5.4）**：Windows 侧用真实后端 `RenderTargetBitmap`，实测主窗口 `760x440`、退出码 0、非空白判据带负向对照。CI 侧用 **`Avalonia.Headless`**：已实测能在**无显示沙箱**里渲染真实产品窗口并产出非空白帧（`760x440 distinct=468 variance=481.29`）。**仍未销项的是"接线"本身**：没有建常驻的 headless harness 项目、没有改 `ci.yml`、因此 **Linux / macOS runner 上的表现仍无证据**。
2. **`DataGrid` 用什么 —— 已定（2026-10-02）**：用 **`Avalonia.Controls.DataGrid`**（最新 **12.1.2**，比核心 12.1.3 落后一个补丁；用浮动版本区间 `>=` 让 NuGet 正常解析）。选它的理由是它**已被 P3 完整验证**：模板部件 `Rectangle#BackgroundRectangle`、列头、行选中都在运行期断言里跑通了，且行底色可以被主题覆盖（实测 `#ff3b3b3b` == `Hub.RowSelected`）。**内置 `TableView` 那条路本轮没有去核实** —— 换控件意味着 35 处引用（含列定义与单元格模板）全部重写，在"已有方案已跑通"的前提下不值得再开一个 spike。要注意 Avalonia 的 DataGrid 缺 WPF 版的若干部件/属性（`CanUserAddRows` / `RowHeight` / `AlternatingRowBackground` / `SelectionUnit`），P5 迁 DataGrid 密集窗口时要按实际存在的属性重写列定义。
3. **中文 IME 输入**：macOS / Linux 后端下的输入法候选窗、组合输入在 `TextBox`/`PasswordBox` 上是否正常。这是之前就挂着的待实测项，现在是硬需求 —— Hub 有大量中文文案与中文路径。**P3 完成时仍未触碰**（P3 只做样式，不涉输入语义）。
4. **CJK 字体回退**：目标 Linux 发行版是否预装 Noto Sans CJK？若否，是自带字体（增加体积）还是声明依赖？**P3 已把回退链写进 `Hub.Font.Ui` / `Hub.Font.Mono`（§3.4），但"目标发行版上真能出中文"仍无证据。**
5. **Avalonia 版本 —— 已定**：pin **12.1.3**（MIT，当前最新稳定版）。已核实其 nupkg `lib/` 同时提供 **`net8.0` 与 `net10.0`** → **迁移不升 TFM**，新项目就是 `net8.0`。选 12.x 时注意 §5.1 那条 DevTools 包名变更（`AvaloniaUI.DiagnosticsSupport` 2.2.3；`Avalonia.Diagnostics` 停在 11.3.22，12.x 一个都没发）。**P2 与 P3 都是在这个版本上实测通过的。**
6. **NuGet 源与版本锁定 —— 已落地（2026-10-02，见 §8）**：`NuGet.config`（`<clear/>` + 仅 nuget.org）+ `Directory.Build.props`（`RestorePackagesWithLockFile=true`）+ **`packages.lock.json`** 已随 P2/P3 一起进仓库（2026-10-04 起只保留 App 那一份，理由见下第 2 条）。**`RestoreLockedMode` 按原计划刻意没有打开**，理由正是下面这条：
   - **锁文件与 SDK 版本耦合**（这是必须在 CI 跑绿一次之后再动的唯一原因）：本机只有 SDK 10.0.401，`net8.0` 的 targeting pack 走 NuGet 还原；CI 用 SDK 8.0.x，targeting pack 来自 SDK 自带目录。两者可能产出**内容不同的 `packages.lock.json`**，此时若已开 `RestoreLockedMode`，CI 会在锁文件校验上直接红 —— 而在 CI 真实跑绿之前，这条红线无法当场验证。**顺序就按原计划执行：先落文件、不开锁定模式 → 等 CI 真绿一次 → 再决定是否切换。**
   - **第二个、更尖锐的耦合（2026-10-02 发现并已修）：锁文件内容取决于最后一次 restore 用的 RID。** 用 `dotnet publish -r osx-arm64` 还原会写进一段 `net8.0/osx-arm64` 专属原生资产，而随后一次不带 RID 的 `dotnet build` 会把它抹掉 —— 锁文件在两次操作之间自己变了。对策不是"接受抖动"，而是让它 RID 完整：会被 RID 发布的项目各自声明 `RuntimeIdentifiers`（Cli 四个、App 三个；P6 前另有 WPF 版一个，已随其删除），列出实际会发布的全集。**代价是新增/变更发布宿主时必须同步这两个属性**，否则锁文件退化成"只对某个 RID 正确"。实测已确认：RID 发布 → 无 RID 构建往返若干次，锁文件不再变化。
     **2026-10-04 更正：这条只对 Cli 成立，对 App 不成立。** 实测复现 —— 单次 `-r <rid>` 还原会把已声明的多个 RID 节点**剪成只剩那一个**（App 声明 3 个，跑一次 `-r win-x64` 就只剩 1 个），且会**顺带改写被引用项目的锁文件**（App 的 RID 还原会给 Core 加上 `net8.0/win-x64`，尽管 Core 自己没有任何 RID 声明）。`installer/Build.ps1` 走 `dotnet publish -r win-x64`，所以每打一次本地包，App 的锁文件必然被剪。最终对策改成"只跟踪确有依赖的项目"，并把"提交前跑一遍不带 RID 的 `dotnet restore` 即可复原"写进了 `Directory.Build.props`。
   - `NuGet.config` 里另有一处要说清的边界：本机实测 `api.nuget.org` 的响应会 302 到 `nuget.azure.cn`，说明网络上已有镜像/代理在起作用。因此 `<clear/>` 只是**固定逻辑源**（让不同开发机看到同一套包 ID 与版本），**并不改变实际网络路径**。


---

## 8. 一条必须先接受的代价：GUI 项目将不再能离线冷构建

**现状（2026-10-02 核实）**：`PackageReference` 总数是 **1** —— 当时是 `AxmolHub.App`（WPF）引 `Velopack` 1.2.161，且它**零传递依赖**（`project.assets.json` 里的 NuGet 闭包恰好就是这 1 个包）。P6 把这个引用连同打包职责一起搬进了 `AxmolHub.App`。`AxmolHub.Core` / `AxmolHub.Cli` / `AxmolHub.Checks` 仍是 **0**。仓库另有 **17 个「版本 + SHA-256」双钉包**，但那些走 manifest 运行时下载（进 `data-root/tools`），**不参与构建**，因此不影响离线冷构建。

引入 Avalonia 后，GUI 侧的 NuGet 闭包从 **1 个变成 30 个包** —— 这是 P2 完成后从 `project.assets.json` 里**数出来的实测值**，不是估算：4 个直接依赖（`Avalonia` / `Avalonia.Desktop` / `Avalonia.Themes.Fluent` / `AvaloniaUI.DiagnosticsSupport`）+ `Avalonia.Win32` / `X11` / `Native` / `FreeDesktop` / `FreeDesktop.AtSpi` / `Skia` / `HarfBuzz` / `Remote.Protocol` / `BuildServices`，往下是 `SkiaSharp 3.119.4`、`HarfBuzzSharp 8.3.1.3`、`MicroCom.Runtime`、`Tmds.DBus.Protocol`、`Microsoft.IO.RecyclableMemoryStream`、`System.IO.Pipelines`、两个 `Microsoft.Extensions.*.Abstractions`，以及 `SkiaSharp.NativeAssets.*` / `HarfBuzzSharp.NativeAssets.*` / `Avalonia.Angle.Windows.Natives`。（原先估算的 31 个含 `Avalonia.Fonts.Inter`，实际采纳时去掉了它 —— 见 §5.1。）

随之而来的三件事，**已于 2026-10-02 落地**（与 P2/P3 同一批提交）：

1. **`NuGet.config`（`<clear/>` + 仅留 nuget.org）**。落地前 restore 源继承开发机（实测两个：`api.nuget.org` 与 VS 内置本地源）。1 个零依赖包时无所谓，30 个传递依赖时不同的开发机就会给出不同结果。**边界**：`<clear/>` 只固定逻辑源，不改变实际网络路径（本机 `api.nuget.org` 实测会 302 到 `nuget.azure.cn`）。
2. **`packages.lock.json`**（`RestorePackagesWithLockFile`；落地时每个项目一份共 **5 份**，P6 删 WPF 版后 **4 份**；**2026-10-04 起只跟踪 App 那 1 份**）。这是仓库现有"版本 + SHA-256 双钉"哲学在 NuGet 侧的等价物。**边界要说清，不要夸大**：NuGet 全局包本身已有 SHA-512 校验，锁文件解决的是**版本漂移**，不是篡改。**`RestoreLockedMode` 刻意没有一起打开** —— 原因见 §7 第 6 条（锁文件与 SDK 版本耦合，CI 里直接开会在一条从未跑过的线上踩红）。分两步上：先落文件，等 CI 真绿一次再决定切不切。
   - **2026-10-04 收紧范围**：Core / Cli / Checks 三个项目**零 `PackageReference`**，锁文件里只有空的框架/RID 节点，钉不住任何东西，而内容会随「最后一次 restore 带不带 `-r`」来回变 —— 于是它们各自的 csproj 显式加 `<RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>` 并退出 git 跟踪。全仓唯一有依赖的 App 保留（也就只有它这一份是真的在钉版本）。**给零依赖项目加第一个 `PackageReference` 时要删掉那行。**
3. **`Directory.Build.props` 收敛版本号**。`0.1.6` 原先手工散在 `App.csproj` / `Cli.csproj` / `AxmolHub.App.csproj` / `README.md` 四处；P2 恰好演示了这个洞在扩大 —— 新增一个项目就要多抄一份。**落地时顺手查出一个真实缺陷**：`<Version>` 只有 3 个项目声明了，导致 **`AxmolHub.Core.dll` 报的版本是 `1.0.0`，而 `AxmolHub.Cli.dll` 报 `0.1.6`** —— 同一份构建里两个程序集版本不一致，且没有任何构建期信号。收敛后 5 个程序集统一为 `0.1.6`（P6 删 WPF 版后为 4 个）。`installer/Build.ps1` 与 `installer/Test.ps1` 的版本发现也从 `AxmolHub.App.csproj` 改指向 `Directory.Build.props`，保持了"脚本里没有第二份副本"这条既有性质。

另外：Avalonia 及其传递依赖需要进 `THIRD_PARTY_NOTICES.md`（以 MIT 为主，需署名；`Avalonia.Angle.Windows.Natives` 打包的是 ANGLE 原生二进制，许可需单独核对），构建时间与 CI 缓存键也要重估。**这两项仍未做。**


**影响范围可控**：`AxmolHub.Core` 与 `AxmolHub.Cli` 保持零依赖不变 —— Avalonia 只进 GUI 项目。也就是说，"CLI 与 Core 仍可离线构建"这个性质可以保住，丢掉的只是 GUI 那一条。**实测确认（2026-10-02，从 `project.assets.json` 数出，含传递依赖）**：P2 之后是 Core `0` / Cli `0` / App `1`（`Velopack`） / 新 GUI 项目 `30`；`Avalonia.Controls.DataGrid` 是 P5 迁页面时才加的，于是 Avalonia 闭包变成 `31`；**P6 把 `Velopack` 搬进来后，全仓库的 NuGet 依赖集中在 `AxmolHub.App` 一个项目里：`32` 个包**（31 个 Avalonia 闭包 + 1 个 `Velopack`）。Core / Cli / Checks 均为 `0`。这也是为什么**不要让任何其他项目引 Avalonia 相关包**。

这不是反对迁移的理由（换跨平台本就要付代价），但它应该被显式记下来，而不是在某个 PR 里无声发生。
