# Axmol Hub 研发计划

- **状态**：工作稿
- **日期**：2026-10-02
- **范围**：仅 Axmol Hub（不含 Axmol Editor 自身的开发计划；Editor 只在"边界"一节出现）
- **性质**：本会话讨论的收敛结果。数据与结论均来自对源码的核对，非复述 README
- **上游文档**：
  - `docs/adr/0001-hub-tech-stack-and-positioning.md` —— 技术栈与定位决策（已接受）
  - `docs/ai-first-plan.md` —— AI 能力层方案（P0–P5 明细、工具清单、护栏）
  - `docs/avalonia-migration-plan.md` —— WPF → Avalonia 分阶段迁移步骤
  - `docs/ci.md` —— 三平台 CI 与打包链路的设计与实测记录

> 本文档是**索引与排期**，不重复上述文档的论证。遇到分歧以 ADR-0001 为准。

---

## 0. 一句话

Hub 从「Windows 上跑得通的 WPF 工具」推进为「官方组织下的跨平台**环境与构建服务**」，把能力以 **CLI `--json` + MCP** 暴露，供 GUI、外部 agent、Axmol Editor 共用同一份实现。

---

## 1. 定位与边界

**Hub = 环境与构建服务**

| 归属 Hub | 归属 Axmol Editor |
|---|---|
| 引擎生命周期：下载 / 导入 / 校验 / 修复 / 卸载 | 场景 / 资源 / 动画编辑与预览 |
| 模块安装（按引擎版本勾选平台） | 内容创作工作流 |
| 工具链：**读取**引擎自带 `1k/build.profiles` + 探测 `tools/external` 显示状态（安装交还引擎 `setup.ps1`） | 引擎内实时预览 |
| 项目创建、目标选择 | 自举 / dogfooding（Editor 自身即 Axmol 应用） |
| 构建 / 运行 / 部署 / 设备管理 | |
| **CLI `--json` 与 MCP 接口（单一定义）** | |

**明确不做**（沿用创始设想稿的排除项）：代码编辑器、Scene / Asset / Animation Editor、Git GUI、完整 Terminal、IDE 替代。

**硬约束（三条）**

1. **AI 能力层全局只有一份实现** —— Hub 与 Editor 不得各写一套
2. **Hub 是服务方，Editor 是客户端之一**（与 GUI / CLI / MCP 并列）
3. **Hub 不是 Axmol 自举应用** —— 自举与 dogfooding 由 Editor 承担，Hub 不必为此负责

---

## 2. 技术栈与架构

| 项 | 决定 |
|---|---|
| 语言 / 运行时 | **C# / .NET 8+** |
| UI | **WPF → Avalonia**（WPF 是 Windows 独占，两个候选分支里都必须换，故不是决策变量） |
| 分层 | `Core`（业务，唯一资产）/ `Cli`（薄派发）/ `App`（UI） |
| 新增项目 | `AxmolHub.Agent`（工具注册表 + `IChatClient` 管道）、`AxmolHub.Mcp`（MCP Server）。**两者都不引用 App**，保证可在 CI / 服务器 / 远程 agent 中运行 |
| 数据驱动 | 引擎发行清单、模块/配方声明在 `manifests/`；**工具版本不在这里** —— 真源是引擎自带的 `1k/build.profiles` |
| 三条贯穿原则 | ~~固定版本（绝不回退系统工具）~~ **已由 ADR-0002 取代**、环境隔离（`ProcessRunner` 先 `Environment.Clear()`）、先校验再落地（staging + 凭据文件） |

> **工具链自持与「绝不回退系统工具」已被 [ADR-0002](adr/0002-toolchain-and-build-delegated-to-engine-cmdline.md) 取代（2026-10-03，已落地）**：
> 构建/运行/部署委派 `axmol build|run|deploy`，工具链安装委派官方 `setup.ps1`（落点 `<engine>/tools/external`），
> 版本真源 = `<engine>/1k/build.profiles`。Hub 侧 `ToolchainDetector` / `WindowsToolchainInstaller` /
> 四份工具链清单已删除；`ProcessRunner` 的「传入环境即完整环境」语义保留，但**构建进程改为继承父环境**，
> 因为工具链由引擎自己去找（系统 VS + 引擎树内工具）。
>
> 环境隔离仍适用于 **Hub 自己的产物**（staging + 凭据 + `data-root` 内的缓存/日志），
> 不再声称「工具链隔离」—— 全局副作用（用户 PATH / AX_ROOT / 执行策略）是引擎官方流程的一部分，已明确接受。

**四客户端共享一份工具定义**

```
       AIFunction（单一定义）
              │
   ┌──────────┼──────────┬──────────┐
 GUI(Avalonia) CLI(--json) MCP  Editor
```

原理：MCP C# SDK 与 `Microsoft.Extensions.AI` 共用同一个 `AIFunction` 抽象 —— 同一份签名同时产出 MCP tool schema 与模型函数调用 schema，四处不会漂移。

---

## 3. 当前基线（起点）

| 项目 | 规模 | 目标框架 |
|---|---|---|
| `AxmolHub.Core` | 17 文件 / 2347 行 | `net8.0` |
| `AxmolHub.Cli` | 1 文件 / 259 行，**12 个动词** | `net8.0` |
| `AxmolHub.App` | 6 文件 / 1312 行 C# + 2 文件 / 99 行 XAML | `net8.0-windows`（WPF） |
| `src/AxmolHub.App` | 15 文件 / 2539 行 C# + 12 文件 / 1256 行 XAML | `net8.0`（P2 骨架 → P4 基础件 → **P5 外壳 + 引擎页 + 设置页**；P6 取代 WPF 版） |
| `tests/AxmolHub.Checks` | 815 行 / **139 处断言**（主流程 105 + `--check-cli-json` 34） | `net8.0-windows` |

> 数字随 P5 推进而变，别当成常量读；口径是"排除 `obj`/`bin` 的源码行数"。
> Core 变大的一大块是 `HubTexts.cs`（190 条界面文案，从 WPF 的 `Texts.cs` 移过来 —— 见 §4.B 的进度说明）。

CLI 动词：`targets` `verify` `create` `select` `plan` `configure` `build` `run` `serve` `devices` `deploy` `install-tools`。
已有约定：`plan` 输出 JSON；**stdout 出数据 / stderr 出日志**；`verify` 失败返回 exit code 2。

**完整度分层**

| 档 | 目标 | 状态 |
|---|---|---|
| 一 | **Windows x64** | 下载 → 构建 → 运行 → 产物校验，**闭环** |
| 二 | Android | 签名 / 校验链完整，真机未验 |
| 三 | WASM | 构建 + 本地预览跑通，浏览器场景未验 |
| 四 | macOS / iOS / tvOS / Linux | 仅 CMake 命令计划，`packages` 为空数组 |
| — | UWP / Xbox | 主动抛 `PlatformNotSupportedException` |

**可移植性**

- **可移植**：Core 大部分、105 项行为检查、`manifests/` 数据、CLI 设计（`--json` / stdout-stderr 分离 / exit code 语义）、踩坑清单
- **唯一不可移植**：**已验证的 Windows 端到端运行证据**

> 105 项行为检查描述的是**需求**而非实现，换任何技术栈都能当验收清单用 —— 这是仓库里最值得先抢救的资产。

---

## 4. 工作项

### A. 地基与阻塞项（优先级最高）

| # | 工作项 | 说明 |
|---|---|---|
| **A1** | **解多引擎版本绑定** —— **代码级已解，2026-10-02**，见下 | `module-manifest.json` 只有 `2.11.5` 一个 profile；Android 打包硬编码 `engine.Version != "2.11.5"` 即拒绝。**v3 发布当天变成阻塞**，改动小，应最先做 |
| **A2** | **v3 目录结构 / 构建系统耦合** —— **已实测证实，2026-10-02**，见下 | v3 **确实**改了目录：`core/` → `axmol/`，`ValidateEngine` 的 4 个标志文件在 v3 上不成立。改动小但**必须同时接受两种布局** |
| **A3** | **P0：CLI 统一 `--json` 契约** | 统一 `{ok, exitCode, data, log, artifacts}`，替换目前"多数命令输出文本"的状态 |
| **A4** | **P1：MCP Server（stdio）** | 先用 Cursor / Claude Code 驱动 Hub 验证价值，再决定投入 UI |

> A3 / A4 在定位决策后**优先级上升**：它们不再只是"为 AI 做的结构化输出"，而是 **Hub 与 Editor 之间的正式接口**，应排在任何 UI 工作之前，且与 UI 栈无关。

#### A1 落地记录（2026-10-02）

**做了什么。** 原先的版本绑定其实是三件事，只有一件是代码：

| 位置 | 性质 | 处置 |
| --- | --- | --- |
| `AndroidPackageService.cs` 与 `PlatformBuildService.cs` 各一处 `engine.Version != "2.11.5"` | **代码级**，新增版本要改两处 C# 且两处会漂移 | 已改为查清单 |
| `module-manifest.json` 单一 profile | 数据级 | **未动**（见下） |
| `engine-manifest.json` 单一引擎 | 数据级 | **未动** |

具体：`ModuleProfile` 增加 `verifiedRecipes`（当前值 `["android-packaging"]`），两处硬编码闸门改为调用 `PackagingRecipes.RequireVerified(engine, PackagingRecipes.AndroidPackaging)`。于是**为 v3 放行 = 在 `module-manifest.json` 加一条 profile 并写上已验证的配方，不改任何 C#**。

**刻意没有放宽的性质。** 这**不是**"让 v3 能跑"，而是把**验证边界从代码搬进数据**。三条失败关闭的纪律原样保留，并且都有断言守着：

- 未声明的引擎版本一律拒绝，**不会回落到别的版本 profile**（`EngineModules.ForEngine`，断言 `Unverified engine version cannot use another version module profile`）；
- 配方未对该版本声明 → 同样拒绝（新增断言）；
- 清单文件读不到 → **也算验不过**（这是验证闸门，不能因为读不到证据就放行）。

**因此没有加 2.11.6 profile。** 加 profile = 声称"这个版本上验过那条配方"，而在 v3 / 2.11.6 上真的验证 Android 打包是**人的验证工作**，不是代码工作。A1 能解掉的是"改代码"这一半。

**顺带查出一个 v3 当天会让 GUI 直接抛异常的 bug**：`manifest.Packages.Single()`（原 `src/AxmolHub.App/MainWindow.xaml.cs:557`）。`engine-manifest.json` 一旦出现第二个引擎（v3 与 2.11.5 并存就是这样），这行抛 `InvalidOperationException: Sequence contains more than one element` —— 即"点『安装官方引擎』按钮即崩"。CLI 不读 `engine-manifest.json`，所以只影响 GUI。**处置：不在 WPF 版修**（P6 已删掉它），改在 P5 迁移 `MainWindow` 时一并解决 —— **已落地**：`HubWorkspace.InstallEngineAsync`（`src/AxmolHub.App/Services/HubWorkspace.cs:356`）改成"在 `official-lts` 通道里取版本最新的一个"，清单列多个版本不再抛异常。

**另一半仍未做**：按钮文案还是硬编码的 `InstallOfficial` = "安装 Axmol 2.11.5 LTS"。正确做法是让按钮反映它**实际会装**的版本（从清单算），否则 v3 进清单那天按钮就在说谎。这与下面的 A1/A2 是同一个根因。

#### A2 实测：v3 的目录结构确实变了（2026-10-02）

`--verify-ops` 第一次真跑就把它撞出来了 —— 这件事实测一次就够，读代码看不出来（代码里只写死了路径，
没有 v3 的样本可比）。本机两套引擎源码树：

| 目录 | 版本 | 四个标志文件 |
| --- | --- | --- |
| `D:\dev\simdsoft\axmol2` | **2.11.6**（`git describe` 为 `v2.11.5-3-g07d8cef1b`） | 齐全 |
| `D:\dev\simdsoft\axmol3` | 3.0.0-alpha33 | **缺 `core/axmolver.h.in`** |

原因：**v3 把 `core/` 目录改名为 `axmol/`** —— 版本头现在位于 `axmol/axmolver.h.in`。
另外三个标志文件（`tools/cmdline/axmol.ps1`、`templates/cpp/axproj-template.json`、`1k/1kiss.ps1`）位置未变。

**含义：v3 支持的第一个障碍不是 module profile，而是 `ValidateEngine` 的标志文件清单。**
在它修好之前，v3 引擎连"被导入"这一步都过不去，也就谈不上选模块或构建 —— 而 A1 那套
`verifiedRecipes` 机制是**导入之后**才用到的。修它时要注意两点：① 必须**同时接受两种布局**
（v3 与 2.11.x 会长期并存），不能简单地把 `core/` 换成 `axmol/`；② 版本头的路径一旦可变，
"从哪里读版本"本身就是配置，不该再散在代码里。

顺带一个正面结果：拒绝理由**点名了**缺失的具体文件（`Incomplete Axmol engine: missing core/axmolver.h.in`），
这条在真跑时是有意义的 —— 否则用户只会看到一句"无效引擎"，无从下手。

### B. UI 迁移

| # | 工作项 | 说明 |
|---|---|---|
| **B1** | 新建 `AxmolHub.App`，引用现有 Core，**Core 一行不改** | 先搬项目页 / 引擎页 / 工具链页，跑通 Windows，验证迁移路径是否成立 |
| **B2** | 主窗口形态 | 现为**单窗口 + 四页导航**；创始设想为**三栏 docking + 右侧常驻 AI 面板**。**建议与 B1 合并做**，否则 AI 面板没有自然落点，先塞一页会返工 |

**进度（2026-10-02）**：B1 已推进到 **P5** —— P1（两个 PowerShell 包装归位）、P2（Avalonia 骨架）、P3（主题层，`--verify-theme` 37/37）、P4（基础件：`HubDialog` / 选择器包装 / `--smoke`，`--verify-foundation` 25/25）均已落地；**C 的 P0（CLI `--json` 契约）也已落地并接进 CI**（`docs/ci.md` §2.6）。
**P5 已完成四个增量**：① 本地化单一定义（`Core/HubTexts.cs`，现 190 条）、Avalonia 外壳（左侧导航 + 页面宿主 + 状态栏）、迁移引擎页；② 迁移设置页 —— 它同时是**本地化管线的活体验收台**（真切一次语言、读回已存在控件的文字）；③ **四页 1:1 复刻 WPF 版**（项目 / 引擎 / 工具链 / 设置 + 四个对话框窗口），编排整体搬进 `Services/HubWorkspace.cs`；④ **切换数据根**——做法改为"只重建工作区、不重建窗口"，因此不再依赖 B2。`--verify-shell` 从 31 条扩到 **68 条**。三次反向对照（去掉资源字典重灌 / 去掉导航高亮同步 / 去掉页面缓存丢弃）都证明这类失效**构建期拦不住**（0 error）而运行期断言能拦（6 / 3 / 3 条 FAIL）。详见 `docs/avalonia-migration-plan.md` §5.5–§5.8。

**P5 第五个增量：真操作验收（`--verify-ops`）**。前面那些断言验的是**界面**，跑的却是 fixture；`HubWorkspace` 九百多行里真正被执行过的只有列表刷新、导入、选择这类轻量操作。于是新增 `--verify-ops <报告> <引擎目录>...`，在**真实引擎源码树**上跑不下载、不编译的那部分真操作：工具链探测、引擎导入/校验/设为默认/移除、状态落盘重载、三类无效输入。首次运行 **22/22 通过、4 条显式跳过、exit 0**，并**撞出了 A2 的真实形态**（见上）。跳过项（装引擎 / 装工具链 / 构建 / 运行 / Android 打包）写在报告的 `SKIP` 里并附原因 —— 一份让人以为跑过了的报告比没有报告更糟。详见 `docs/avalonia-migration-plan.md` §5.9。

**P5 期间浮现的三件事（都影响后续排期）**：

1. **B2 的问题变小了。** 侦察 WPF 版 `MainWindow.xaml` 后确认：**它的形态本来就是"单窗口 + 左侧 218px 固定导航 + 内容区四页"**，靠 `Visibility` 切换页面，**不是三栏 docking**；也没有独立的"设备页"（Android 设备面板嵌在项目页里）。所以 B2 的真实分歧不是"要不要重构外壳"，而是"**要不要在现有形态上追加一个 AI 面板**"。这使推荐结论从"设计偏好"变成"保留已交付并被验证过的 UX"。
2. **本地化是 P5 的前置项，原计划没列。** WPF 的页面文字全走 `{DynamicResource}`，由 `Texts.cs`（206 行）在启动时灌进资源字典；Avalonia 侧必须先有等价机制，页面才谈得上"复制过来就能用"。已按"单一定义、多客户端"处理（数据进 Core，两侧各留一层薄适配），因此它不是障碍，但**它是页面迁移的前置条件**，排在页面之前。
3. **"逐页截图的肉眼核对"是必要工序，不是可选调试。** 迁移设置页时它抓到了一个**所有断言都绿灯**的真错：`NavigateTo` 忘了同步左侧导航高亮，于是"显示 A 页、左边高亮 B 页"。编译期、绑定期、以及当时全部导航断言都发现不了它。结论：产品级验收 = 运行期断言 + 逐页 PNG + 人工看一眼，三者缺一不可。

**仍未完成**：`--smoke-all/-run/-build` 三种无人值守模式的迁移、P6 删除 WPF 版；**CI 的三平台 GUI 烟雾仍未接线**——`Avalonia.Headless` 已实测可行（无显示沙箱里能渲染真实主窗口并产出非空白帧），缺的只是常驻 harness 项目与 `ci.yml` 的改动；`--verify-shell` 因此目前只能在开发机上跑。

**P5 复刻的完成度该怎么说（2026-10-02 核对）**：WPF `MainWindow.xaml` 上的 **39 个 `Click=` 处理器，38 个已有对应接线**，唯一缺口是切换数据根（已在 §5.8 补齐）→ 现在 **39/39**。但"接线完成"只是**代码层面**的判断。`--verify-ops`（§5.9）把便宜的那部分补成了运行证据（引擎管理链路 + 工具链探测 + 无效输入），**仍然没跑过的是**：装引擎、装工具链、构建、运行、Android 打包 —— 它们要联网下载 GB 级数据或依赖完整工具链，成本不属于"便宜"。这与 ADR-0001 里"踩坑知识可移植、运行证据不可移植"是同一条：**P6 删 WPF 版之前，这几条至少要有一次真跑**，否则等于把 WPF 版那份"用过"的经验换成了没跑过的代码。

### C. AI 能力层

分期明细见 `docs/ai-first-plan.md` §6，此处只列骨架：

| 阶段 | 产出 |
|---|---|
| P0 | CLI 全命令 `--json` —— **已落地（2026-10-02）**：契约见 `docs/cli-json-contract.md`，类型在 `src/AxmolHub.Core/CliContract.cs`，12 个动词全部接上；**34 条端到端契约断言已进 CI**（`--check-cli-json`，见 `docs/ci.md` §2.6）。这就是 Hub 与 Editor/MCP 之间的正式接口 |
| P1 | MCP Server（stdio） |
| P2 | `ProjectDigest` + 版本键控 `EngineIndex`（解析 `core/`、`extensions/` 头文件成符号表） |
| P3 | 项目页内嵌聊天面板：流式 + 工具调用卡片 + diff 预览 |
| P4 | 构建失败 → 问 AI → 改码 → **真实重建**（闭环） |
| P5 | 场景化 Skills（新建场景 / 物理 / 打包 / Lua 绑定） |

**两条排期纪律**

1. **P0–P2 是地基，不要跳过。**
2. **反对先做聊天框。** 专属感的来源不在聊天框（那是最容易被复制的一层），而在这三件事：知识来自**磁盘上已安装的引擎源码**（零幻觉）、能调用 Hub **私有的固定工具链**（可复现）、能**真实构建并拿到编译器原文**。

### D. 跨平台后端与分发

#### D-1 宿主后端

| # | 工作项 | 说明 |
|---|---|---|
| **D1** | macOS 宿主后端 | 已**大幅简化**（ADR-0002）：环境准备与构建都交还引擎，Hub 侧只需 `EngineToolchain` 的 macOS 分支 + 系统 Xcode 检测。**仍需在真机上验证** |
| **D2** | Linux 宿主后端 | 同上（发行版自带 gcc/g++，引擎只检测不安装） |

#### D-2 打包与签名（**安装器栈必须跨平台**）

| # | 工作项 | 说明 |
|---|---|---|
| **D3** | ~~打包栈替换为跨平台方案~~ **已完成 2026-10-02** | 已从 Inno Setup 换成 **Velopack**。触发原因不只是"Inno 是 Windows 独占"：Inno **自 6.7 起商业使用需付费许可**，而仓库固定的正好是 6.7.3，第三方声明里却放着 6.7 之前的旧条款。详见 `docs/ci.md` §6 |
| **D4** | 自动更新 | Velopack 自带：更新源是静态文件（`releases.<channel>.json`），GitHub Releases 直接可用，不用自建服务端。**能力已具备，但 UI 侧的更新检查与入口尚未接入**，属独立工作项 |
| **D5** | 签名与公证 | **Windows：决定申请 SignPath Foundation 的免费 OSS 代码签名**（细则见下）。**macOS：公证仍需付费的 Apple Developer 账号（$99/年），SignPath 不覆盖**；Linux：一般无需签名 |

**Velopack 选型（已定）**

- `vpk` 一套命令产出三平台产物；自带自动更新与增量包；Rust 核心 + .NET CLI + C# 客户端库，与 Hub 栈契合；本地化 39 种语言
- 版本与摘要照旧锁定：`installer/packaging-manifest.json` 固定 `vpk` **1.2.161** + SHA-256，`--prepare-packaging` 校验后装进 `artifacts/packaging-tools`，不改 PATH
- **代价：这是仓库第一个 `PackageReference`**（App 项目引用 `Velopack` 客户端库，用于拦截安装/更新/卸载钩子）。冷构建因此不再完全离线，且它要进第三方声明。**Core 与 Cli 保持零依赖**，"CLI 与 Core 仍可离线构建"这个性质保得住
- **两处旧描述有误，已更正**：① Linux 产物**只有 `.AppImage`**，没有 `.deb` / `.rpm` —— Velopack 明确不做传统包管理格式，理由是 AppImage 才能一次构建跨发行版；② 此前引用的"健康度 43/100"其实是它的 **Security 子分**，综合指数是 **84/100（Excellent）**（Vitality 90），采购判断因此反了过来
- **新发现的连带改动（必须做）**：Velopack 更新时整体替换安装目录下的 `current\`，卸载时删除整个安装目录 → **Hub 的设置与数据根必须移出安装目录**，落到 `%LocalAppData%\AxmolHub\`。这条不是风格选择：不移就等于每次升级都清掉用户的引擎与工具链（GB 级）
- 代价：Windows 安装器变成一键式，**失去安装目录选择页与安装语言选择**；桌面快捷方式固定为不创建（Inno 里它是默认不勾选的选项）
- 仍未用上的能力：**增量包**需要 `--outputDir` 里保留上一版产物，当前每次清空发布目录，因此只有同一目录留多版本时才会产出

> **与 §5 排序的关系**：D3 原本排在第 11 步（D1/D2 之后）。现在提前做了，因为 Inno 的许可问题**在入仓阶段就已经是阻塞项**（正式发行任何二进制前必须先解决），而它与 UI 迁移无关。D1/D2 与 D5 的 macOS 公证不受影响，仍在原位执行。


#### D5 细则：SignPath Foundation 免费代码签名（**已决定申请**）

**为什么用它**：Windows 上未签名的安装器会触发 SmartScreen 警告，这是直接的支持负担。商业 OV 证书约两百美元/年且需身份核验；**SignPath Foundation 对 OSS 项目免费提供 OV 级证书**（证书签发给 SignPath Foundation 而非项目，私钥存于其 HSM），并提供 GitHub Actions 集成。

**门槛（逐条对照本仓库）**

| 条件 | 本仓库状态 |
|---|---|
| OSI 许可，且**所有组件都无商业双许可** | ✅ MIT —— 换掉 Inno 6.7 之后，第三方组件里已无带商业许可条件的项。**这正是 D3 换栈的一个附加收益** |
| 不含专有/闭源组件 | ✅ 自有代码全 MIT；引擎与微软/Android/Apple SDK 不随仓库分发 |
| 项目活跃维护 | ✅ |
| **已按待签名的形态发布过** | ❌ **未满足** —— 仓库至今没有任何已发布、可下载的二进制 |
| 功能在下载页有说明 | ⚠️ 部分满足：README 有说明，但没有 Release 页/下载页 |
| 在主页与下载页公布 **Code signing policy** | ❌ 未做 |
| 指定 Authors / Reviewers / Approvers 且全部启用 MFA | ❌ 未做（应与 CLA / CODEOWNERS 同批） |

**义务（是接受约束，不是纯好处）**

- 证书的**发布者是 SignPath Foundation**：它们定义并执行技术约束，违反其行为准则时可暂停订阅、**立即或追溯吊销证书**
- **每一次签名请求都要人工批准** —— 发布链路里始终有一个人工点击，无法完全自动化
- 只能签**自己仓库构建**的产物；上游库要签名得请上游自己申请
- 未来可能被要求提供 SBOM 等额外最佳实践

**申请顺序（有硬前置）**：① 做出第一个可下载的 Windows 发布（GitHub Release）→ ② 公布 Code signing policy 与三人角色 → ③ 提申请。审核无公布 SLA，社区经验为**数天到数周**。

**集成方式不是一条命令（推断，待落地实测）**：`vpk` 的 `--signTemplate` / `--signParams` 是**打包过程中逐文件同步调用**的签名命令，而 SignPath 是**异步 + 逐次人工批准**的服务，两者节奏不同。实际形态应是**分两次签**：先签 publish 输出里的应用二进制，再 `vpk pack`，最后签 `Setup.exe`。在没有签名凭据之前，`release-windows.yml` 保持不签名，CI 不因缺少凭据而变红。

> **D1/D2 的工作量与 UI 迁移同量级。不要以为换了 Avalonia 就跨平台了** —— Avalonia 只解决 UI 层，D1/D2 在当前代码里完全不存在。

### E. 工程化与发布

| # | 工作项 | 说明 |
|---|---|---|
| **E1** | 105 项行为检查接 CI | 不依赖任何测试框架，接入近乎零成本，官方仓库必须要有 |
| **E2** | README 顶部标注 **Windows-first** 初始范围 | 否则 macOS / Linux 用户会直接报"跑不起来" |
| **E3** | 代码签名 | **已并入 D5**（签名属分发环节；跨平台后需同时处理 Windows 签名与 macOS 公证） |
| **E4** | ~~`THIRD_PARTY_NOTICES.md` 补 Inno 许可说明~~ **已完成 2026-10-02** | Inno 已整体移除（含 `licenses/Inno-Setup.txt`），第三方声明改为 Velopack（MIT）+ `licenses/Velopack.txt`。原先那句悬置的"请检查商业使用要求"随之消失 |

---

## 5. 实施顺序

`ADR-0001 §8` 的展开（原文第 8 步含三项，此处拆开）：

| 步 | 工作项 | 对应 |
|---|---|---|
| 1 | `git init`，**以原作者身份**提交首个 commit | 当前目录无 git 历史，这是保留 authorship 的唯一机会 |
| 2 | LICENSE 改 Axmol house style + 新建 `AUTHORS.md` | 用 `(see AUTHORS.md)` 承载原作者归属 |
| 3 | `CONTRIBUTING.md`（指向 CLA）、`CODE_OF_CONDUCT.md`、`SECURITY.md`、PR / Issue 模板 | |
| 4 | 105 项检查接 CI | E1 |
| 5 | README 标注 Windows-first | E2 |
| 6 | **解多引擎版本绑定** | A1 |
| 7 | **Avalonia 迁移（Core 一行不改）** + 主窗口形态 | B1 / B2 |
| 8 | CLI 统一 `--json` 契约 | A3（P0） |
| 9 | MCP Server | A4（P1） |
| 10 | macOS / Linux 宿主后端 | D1 / D2 |
| 11 | ~~跨平台打包栈~~ **签名与公证（含自动更新接入）** | **D3 已完成**（Velopack）；D4 的更新入口与 D5 的签名仍在本步 |
| 12 | `ProjectDigest` + `EngineIndex` → 聊天面板 → 闭环修复 → Skills | C 的 P2–P5 |

**排序理由（四条关键）**

- **第 6 步的时间窗最窄** —— v3 一发布立刻阻塞
- **第 7 步风险最高** —— 它验证的是"Core 到底要不要动"这个前提，越早证伪越省事
- **第 8–9 步是 Editor 能否安全接入的前提** —— 边界契约必须先有接口才能定
- **第 11 步必须排在第 10 步之后** —— 没有 macOS / Linux 的可构建产物，就没有东西可打包；而它一旦完成，Windows 侧也顺带获得自动更新

> 第 1–5 步同时是"进官方仓库"的入场券，与功能开发可以并行。

---

## 6. 待决项（开工前需要答案）

| # | 议题 | 影响面 | 备注 |
|---|---|---|---|
| 1 | **项目内写入策略**：`.axmol-hub.json` 保留 / 改回零写入 | `EngineIndex`、模块状态、图形后端注入都依赖它；也决定 UI 迁移时要不要动 Core | 牵动最多，**建议最先定** |
| 2 | 主窗口形态 | 已收窄：WPF 版本来就是**单窗口 + 左侧导航 + 页面宿主**（2026-10-02 核实），所以分歧只剩"**要不要追加 AI 面板**"。**结论：不引入 docking，AI 做成可折叠右侧抽屉**（页面是 `UserControl`，与宿主无关，故此决定不阻塞页面迁移） | 牵动 AI 面板落点 |
| 2b | **本地化归属**（2026-10-02 新列） | 已定：文案数据进 `Core/HubTexts.cs`，WPF 与 Avalonia 各留一层薄适配。**是否继续维持中英双语**仍可再议 —— 目前两侧都保留，键数 183 | 迁移期两版并存，复制两份文案会让中英静默分叉 |
| 3 | Graphics backend 建模 | `BuildTarget` 当前无图形后端字段，创始设想的上下文项无法满足 | 它是配置期 / 运行期选择，还随宿主平台变，需先建模 |
| 4 | **Hub ↔ Editor 能力契约** | 决定 A3 / A4 的接口形状 | 需先问清 Editor 的 5 件事（`ADR-0001 §6`），否则边界只能靠猜 |

---

## 7. 风险登记

| 风险 | 等级 | 说明 |
|---|---|---|
| **Hub ↔ Editor 范围冲突** | **高** | 两者都会想做引擎管理、项目状态、工具链、构建运行、AI；由同一人的同一套 AI 流程产出、互不知情 → 会产生"两个半成品 Hub"。**只能靠契约约束，不能靠约定俗成** |
| Core 里的 Windows 分支 | 低（已解） | ADR-0002 之前是实质技术债（`ToolchainDetector` 硬编码 `bin/Hostx64/x64/cl.exe`、`WindowsToolchainInstaller` 整文件 Windows 专属）。两者均已删除：探测按引擎树布局 + vswhere，安装交还 `setup.ps1` |
| 组织新增 C# 维护责任 | 中 | `axmolengine` 此前只有 C++。**必须指定维护者**，否则成为孤儿代码 |
| 单贡献者容量 | 中 | 同一人同时维护 Hub 与 Editor，属容量风险而非技术风险 |
| **SignPath 的角色要求撞上单维护者** | 中 | 其行为准则要求指定 **Authors / Reviewers / Approvers** 三个角色且全部启用 MFA —— 单一维护者可以一人兼三角，但"每次签名请求需一名受团队信任的成员批准"意味着**发布链路里始终有一个人工步骤，且这个人不能出事**。与 CLA/CODEOWNERS 同批处理 |
| v3 耦合 | 中 | 目录结构 / 构建系统变更会打断 `ValidateEngine` 与 `manifests/` |
| App 层 code-behind 风格 | 低 | `MainWindow` 823 行，非严格 MVVM。规模小时无妨，但与 Core 的严谨度有反差；**迁移时是重构窗口** |

---

## 8. 范围说明

| 事项 | 状态 |
|---|---|
| 贡献者补偿 / 赞助政策 | **不在计划内** —— 用户决定暂不制定；仓库内不放相关内容 |
| **安装器跨平台化** | **已完成 2026-10-02**，见 §4 D3。提前于 §5 的第 11 步执行：Inno 的许可问题在入仓阶段就已是阻塞项，且与 UI 迁移无关 |
| **用 NSIS 替代 Inno** | **已评估并排除。** NSIS 同样是 Windows 独占（`makensis` 仍是 32 位），许可也更杂（zlib/libpng + bzip2 + CPL-1.0 + LGPL-2.1，需向 `THIRD_PARTY_NOTICES.md` 补的条目反而比现在多）。它是"换一个 Windows 装器"，不是跨平台方案。D3 最终选的是 Velopack，此条保留作为"考虑过、为何不选"的记录 |
| CLA / 许可证法律细节 | **不在计划内** —— 属入场流程，另处理。唯一现在生效的约束：**CLA 不得掺入对价** |
| Axmol Editor 的开发计划 | **不在本文档范围** |

---

## 9. 可安装引擎列表的取数来源（2026-10-02 实测）

用户给出并确认已上线的接口：

```
https://axmol.dev/versions/index.json        （本机实测 200，3474 字节，application/json）
https://local.axmol.dev/versions/index.json  （同一份内容的本地镜像，用于本地联调）
```

**本机实测的两个坑（先记下来，免得下次重踩）**：

- `curl` 直接拉会失败在 `CRYPT_E_NO_REVOCATION_CHECK (0x80092012)` —— Windows schannel
  查不到吊销列表。加 `--ssl-no-revoke` 即可拿到 200。**这不是服务端的问题**，
  换一台能查吊销的机器或加参数都能过；写进客户端的取数代码时也要考虑这类中间层差异。
- 域名解析走的是内网 DNS（`172.16.23.168`），`nslookup` 第一次超时、重试才出结果。

**当前索引的实际内容（2026-10-02 抓取）**：

| 字段 | 实测值 |
|---|---|
| `schemaVersion` | `1.0`（`$schema` 指向 `https://axmol.dev/versions/schema/v1.json`） |
| `project` | `axmol` |
| `channels` | 只有一条：`lts`（`label=LTS`、`line=2.11`、`latest=2.11.5`） |
| `versions` | 6 条：`2.11.5 / 2.11.4 / 2.11.3 / 2.11.2 / 2.11.1 / 2.11.0`，全部 `channel=lts`、`prerelease=false` |
| 每条含 | `version` `tag` `channel` `line` `prerelease` `releaseDate` `releaseUrl` `archives{zip,tarball}` `artifacts[]` |
| **`artifacts`** | **6 条全是空数组** |

**对 Hub 意味着什么（关键差异）**：这份索引回答的是"**有哪些版本**"，而 Hub 的安装链需要的是
"**装到哪、哈希是什么**" —— `PackageInstaller.InstallAsync` 的入参是
`PackageEntry`（含 `url` / `sha256` / `destination` / `archiveRoot`），**索引里没有 `sha256`**。

所以"直接照这份索引装引擎"目前**做不到**，缺的是带哈希的制品元数据。两条可行路径：

1. **索引补 `artifacts`（含 `sha256`）** —— 最干净：Hub 侧只多一个"清单来源"，
   安装链路一行不改。当前 `artifacts` 为空，说明这份数据是"申请中"的缺口而非设计冲突。
2. Hub 侧在下载后自行计算并**首次信任** —— 会引入 TOFU 语义，与现有
   "先校验哈希再解包"的设计相反，不建议。

**已埋的接缝**：`HubWorkspace.InstallEngineAsync` 里取包的顺序改成了"官方 LTS 通道里版本最新的一个"
（WPF 版写的是 `Packages.Single()`，清单多一个包就抛异常），并把接入点标在那一行。
`manifests/engine-manifest.json` 仍是**真源**（它带哈希）；索引将来做的是**给清单补条目**，
不是取代清单。**注意**：这条只解了"列得出哪些版本"，A1 的
`module-manifest.json` 缺少 v3 profile 是另一件事，仍需人工验证。

