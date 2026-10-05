# 持续集成与打包

日期：2026-10-02
关联：`.github/workflows/build.yml`、`.github/workflows/dist.yml`、[installer/README.md](../installer/README.md)

> **后续变更（2026-10-03）**：本文 §2 落地记录里出现的 `module-manifest.json` 已改名 `recipe-manifest.json`、
> `EngineModules` 已改名 `PackagingRecipes`（模块概念整体移除）。历史叙述保留当时命名，不再逐字回改。

---

## 1. 两个 workflow 的分工

| workflow | 触发 | 目的 | 是否阻塞合并 |
| --- | --- | --- | --- |
| `build.yml` | push 到 `master`、PR、手动 | 验证（三平台构建健康度）+ 打包（仅 `Version` 提交） | **是** |
| `dist.yml` | build 完成后（`workflow_run`）、手动 | 三平台安装包合并发布到一个 release | 否（发布用） |

分开发的理由：安装包链路要下载固定版 Velopack CLI、发布自包含 App、再做真实安装/升级/卸载验收，属于发布动作；把它塞进每个 PR 会让 CI 变慢、变脆，而且失败信息（打包器、签名之类）与"代码是否可构建"无关。因此打包只在提交信息为 `Version x.y.z` 时触发，普通提交只跑验证。

---

## 2. `build.yml` 的 `verify` job：三平台矩阵

矩阵为 `windows-latest` / `macos-latest` / `ubuntu-latest`，每个平台做五件事：

1. **构建 Core + CLI** —— 两者都是 `net8.0`，不含任何 Windows 专属 API（无 `Registry`、无 P/Invoke），全部跨平台差异走 `OperatingSystem.IsWindows()` 运行时分支，因此三平台都应编译通过。
2. **构建 Avalonia 版 GUI**（`src/AxmolHub.App/`）—— `net8.0`、无 `-windows` 目标框架，三平台同构，**没有 `if` 条件**。
3. **断言宿主检测** —— 见下节。
4. **发布自包含 CLI** 到对应 RID（`win-x64` / `osx-arm64` / `linux-x64`）。
5. **在目标宿主上真的执行一次刚发布的二进制** —— 交叉发布本身不构成证据，能跑才算。

### 已删除的步骤：上传 CLI 产物（2026-10-03）

矩阵里曾多出一步 `Upload CLI host`，把 `artifacts/cli/<rid>` 作为 artifact `cli-<rid>` 上传。现已删除，理由三条：

1. **没有消费方。** 发布链路的 App 产物由 `build.yml` 里的 `installer/Build.ps1` 自己发布，从不读 CI 的 CLI 产物；也没有"人工下载一份三平台 CLI 来用"的用法。
2. **它想证明的事已经由别处证明。** "自包含发布是否可用"由上面第 4、5 步回答（发布成功 + 在目标宿主真的跑起来）。artifact 只是把同一条结论再存一份 —— 下载回来仍然是"CI 当时是绿的"。
3. **不是零成本。** 三平台各上传一份几十 MB 的自包含产物，长期占 artifact 配额，过期前一直挂在 Actions 页面上。

注意删掉的只是上传：**第 4、5 步都保留**，交叉发布的产物仍要在目标宿主上执行一次才算通过。

Windows 额外做两件事：

1. 构建 WPF 版 `AxmolHub.App` 与 `AxmolHub.Checks`（只做编译验证，不启动 GUI —— 原无头截图步骤已去掉，理由见 §2.2）。这两个项目仍是 `net8.0-windows`，**P6 删除 WPF 版时这一步随之消失**。
2. 跑一次 `AxmolHub.Checks --check-cli-json`（见 §2.6）—— 这是**第一件真正进 CI 的行为断言**，因为它不需要引擎与工具链。

### 2.1 行为断言：宿主检测 + CLI JSON 契约

Core 里唯一按操作系统分叉的逻辑就是 `BuildTargets.Host`，而它直接决定 GUI 会告诉用户"哪些目标能在本机构建"。断言方式是跑 `AxmolHub.Cli targets`，然后检查每个目标的 `current=` 值：

| 宿主 | 必须为 `current=True` | 必须为 `current=False` |
| --- | --- | --- |
| Windows | `windows-x64`、`uwp-x64`、`android-*`、`wasm32` | `linux-x64`、`macos-*`、`ios-*` |
| macOS | `macos-arm64`、`macos-x64`、`ios-*`、`tvos-*`、`android-*`、`wasm32` | `windows-x64`、`uwp-x64`、`linux-x64` |
| Linux | `linux-x64`、`android-*`、`wasm32` | `windows-x64`、`uwp-x64`、`macos-*`、`ios-*` |

这套断言不依赖工具链、不依赖引擎源码、不依赖网络，是当前唯一能**在三平台都**跑起来的行为检查，因此放在必跑路径上。（第二件行为断言是 §2.6 的 CLI JSON 契约，它同样不依赖引擎，但受 `AxmolHub.Checks` 的 TFM 限制只能跑在 Windows。）

> **实现细节**：CLI 在 Windows 上输出 CRLF，`grep -E '...current=True$'` 会因行尾残留的 `\r` 匹配失败。管道里统一加了 `tr -d '\r'`。这一条已在本机用 Git Bash 正反两向实测（错误宿主断言返回 1、正确宿主返回 0）。

### 2.2 GUI 只有一条线：Avalonia 走全矩阵

P2–P6 期间仓库里曾**同时存在两个 GUI 项目**，CI 里也对应两步（Avalonia 三平台无条件、WPF 仅 Windows 带 `if`）。**P6 删掉 WPF 版后只剩一步，那个 `if` 随之消失**——这正是"每推进一步删一个 `if`"的终态，而不是靠手改条件来达成：

```yaml
# 三平台，无条件；这是唯一的 GUI 构建步骤
- name: Build Avalonia app
  run: dotnet build src/AxmolHub.App/AxmolHub.App.csproj -c ${{ env.CONFIGURATION }}
```

`AxmolHub.App` 是 `net8.0`、不带 `-windows`，三平台同构。注意它是**纯编译**：Avalonia 的三个平台后端（`Avalonia.Win32` / `Avalonia.X11` / `Avalonia.Native`）都是被 `Avalonia.Desktop` 无条件拉进来的托管包，所以一次 `dotnet build` 就已覆盖三平台的编译面。**"能显示窗口"仍未被 CI 验证** —— 那需要 `xvfb-run`（Linux）之类的显示环境；在 CI 从未真实跑过之前不引入这类易红步骤。

仍带 `if: matrix.host == 'windows'` 的只有 **`AxmolHub.Checks`**（`net8.0-windows`，含 Core 里 Windows 特有的断言）与 `--check-cli-json`，两者都不涉及 GUI。

#### 无头 GUI 截图步骤已移除（2026-10-02）

原步骤在 Windows 上用 `--smoke` 启动 `AxmolHub.App.exe`，产出 `smoke.png` 并断言"退出码为 0 且 PNG 大于 8KB"，再把截图作为 artifact 上传。现已整段删除，理由三条：

1. **服务对象已消失。** 它验证的是 WPF 版 App，而这个项目已被 Avalonia 版整体取代并删除；macOS/Linux 上又根本跑不了，等于为一个死掉的平台独占项目长期维护一条独占步骤。
2. **它给出的绿灯容易被误读。** 断言只覆盖"进程能启动并退出 0"，而首帧的触发时机一变就可能截到空白窗口却照样通过（详见 [avalonia-migration-plan.md §3.5](avalonia-migration-plan.md)）。证据强度低于它看起来的样子。
3. **真正需要它的地方不在 CI。** `installer/Test.ps1` 用 `--smoke` 验证"安装后的自包含产物能否启动"，那是发布链路的验收项，跑在 `build.yml` 的 Windows 打包 job 与维护者本机。

**`--smoke` 模式本身没有被删除**，Avalonia 版已按 §3.5 重做（`ContentRendered` 在 Avalonia 不存在，改挂 `Opened` + `DispatcherPriority.Loaded`），并补了一道"帧非空白"的断言。**是否把它加回三平台矩阵仍未决定** —— 需要常驻 headless harness，属待办。

一并交代随 WPF 删除的三个无人值守模式：`--smoke-run` / `--smoke-build` 由 `--verify-ops` 完整接管（后者真的执行操作、有断言、出报告）；`--smoke-all` 里"逐页截图"那一半改由 `--smoke-pages` 承担（四页 × 中英双语 = 8 张，并额外核对"不同内容的张数"，防止导航或切语言静默失效），"截构建进度 / 模块 / Android 发布三个对话框"那一半依赖真实设备与签名配置，无人值守下拿不到稳定画面，**不再复刻**。

### 2.3 为什么没有加 `.sln`

仓库没有解决方案文件，`dotnet build` 一次只能接一个 csproj。加一个 `.sln` 能让本地"一条命令构建全部"，但会让 CI 变差：`dotnet build Hub.sln` 在 macOS/Linux 上遇到 `net8.0-windows` 的 `AxmolHub.Checks` 会直接失败，反而失去按平台裁剪的能力。所以 CI 里保持逐项目显式构建，与 `installer/*.ps1` 现有风格一致。

（P6 删掉 WPF 后 GUI 项目本身已不再有平台限制，但 `AxmolHub.Checks` 仍是 `net8.0-windows`，所以这条结论不变。）

### 2.4 SDK 版本

CI 固定 `8.0.x`（`setup-dotnet` 在托管 runner 上会解析到足够新的 8.0.x 补丁，编译器 ≥ 4.14，能跑 Avalonia 生成器）。开发机**推荐**装 .NET 10 SDK（README 已说明：`net8.0` 目标框架下 Avalonia 12.1.3 的源生成器是按编译器 4.14 编译的，SDK 8 只有够新的补丁才满足，而 Ubuntu `apt install dotnet-sdk-8.0` 拿到的 8.0.1xx 只有编译器 4.8、源生成器不产出、报 `CS0103`；.NET 10 一定满足）。开发机装的是更高版本（例如只有 SDK 10）这本身是额外信号：能同时通过 8 与 10 说明没有依赖新 SDK 行为。

### 2.5 action 版本策略

所有官方 action 取**浮动主版本标签**（`actions/checkout@v7`、`actions/setup-dotnet@v6`、`actions/cache@v6`、`actions/upload-artifact@v7`），不钉到补丁版本：安全修复与运行时不升级是拿不到补丁的，钉死反而会攒出一次大跨度升级。

需要注意的约束是**运行时**：这几个主版本都跑在 Node.js 24 上，要求 **Actions Runner ≥ 2.327.1**。GitHub 托管 runner 满足；若将来迁到自托管 runner，必须先升级 runner。历史上 Node.js 20 的 action 已于 2026-06 被强制移除 —— 这正是"浮动主版本 + 定期跟进"优于"长期钉死"的原因。

仓库**尚未配置 Dependabot**，action 与 NuGet 的版本更新目前靠人工检查。

### 2.6 CLI `--json` 契约进 CI（2026-10-02 新增）

```yaml
- name: Verify CLI JSON contract
  if: matrix.host == 'windows'
  shell: bash
  run: |
    dotnet run --project tests/AxmolHub.Checks -c ${{ env.CONFIGURATION }} -- \
      artifacts/checks --check-cli-json src/AxmolHub.Cli/bin/Release/net8.0/AxmolHub.Cli.dll
```

`--json` 是 Hub 对外契约的一部分（[cli-json-contract.md](cli-json-contract.md)），而它**唯一的失败模式**是"stdout 不再是恰好一份 JSON 文档"。这一条编译期测不到，纯形状断言（序列化后检查字段名）同样测不到——**开发过程中真的漏过一次**：`help` 分支忘了判 `asJson`，于是 stdout 变成"help 文本 + 信封"，任何严格解析器都会当场失败，而当时所有形状断言都是绿的。

因此这个模式的判据是**整段 stdout 必须能被 `JsonDocument.Parse` 吃下**，多一个字符都不行；解析失败时把 stdout 原文倒出来再失败。共 34 条断言，覆盖：

| 覆盖点 | 为什么值得断言 |
| --- | --- |
| `targets --json` 的信封（schema / command / ok / exitCode / data） | 契约的基线形状 |
| `--json targets` 与 `targets --json` **逐字节相同** | `--json` 声明为全局标志，位置无关必须成立 |
| 递归断言**没有任何 PascalCase 成员** | 命名策略一旦退回 `System.Text.Json` 的默认值，契约会静默地与文档分叉 |
| `help --json` 只出信封 | 就是上面那次真错的回归 |
| 不带 `--json` 时人读文本原样不动 | 防止 JSON 反向污染人读模式 |
| 失败仍然出 JSON（`ok:false` + `error.type/message`，无 `data`） | 契约的核心目的：消费方永远不必解析 stderr |
| `verify` 在组件缺失时 `ok:false` + `exitCode:2` 但 `data` 仍在 | 刻意的例外——"组件缺失"是数据不是异常 |
| 未知动词的失败信封 | 参数错误与运行时错误走同一条失败路径 |

**为什么只能放 Windows**：`AxmolHub.Checks` 是 `net8.0-windows`（它带一批 Windows 专属断言，例如 PowerShell 子进程与 vswhere 检测，见 §3）。**不是因为这个契约只在 Windows 成立** —— CLI 本身三平台都发布，契约三平台一致。想让它在三平台都跑，得把契约断言拆成一个主机无关的独立项目；在那之前，一个 Windows 上的绿灯好过零个。

**它是怎么被验证的**：本机实测 **34/34 通过**，并做了一次**负向对照** —— 故意把 `help` 的 `!asJson` 守卫去掉、重编译，检查确实在 `help --json writes exactly one parseable JSON document` 上失败并打印了 stdout 原文；恢复守卫后重跑回到 34/34。没有这一步，"34 条全绿"完全可能只是 34 条永不失败的断言。

**前置**：只需 `Build CLI` 那一步（三平台都跑）。`--check-cli-json` 自带 `return`，在触碰任何引擎/工具链逻辑之前就退出，所以在干净 runner 上可跑。它是**端到端**的：真的起 CLI 子进程、只看 stdout，不引用任何内存里的产品类型。

---

## 3. 为什么完整的行为检查**没有**进 CI

先划清范围，避免读成"什么都没进"：**34 条 CLI JSON 契约断言已经在 CI 里跑**（§2.6），因为它们不需要引擎与工具链。下面说的"没进 CI"指的是**剩下的 105 条** —— 也就是 `tests/AxmolHub.Checks` 的主流程。

`tests/AxmolHub.Checks` 不是单元测试套件，而是**维护者验收工具**，它需要：

- 一个已经准备好的 data root（引擎已安装、工具链已就位）；
- 真实的 Axmol 2.11.5 完整源码；
- 引擎树内的工具链（由引擎自己的 `setup.ps1` 装进 `<engine>/tools/external`）；
- 若干子模式（`--prepare-release-check`、`--check-release-receipt`、`--check-android-verification`、`--prepare-packaging` 等）彼此有先后依赖，要按顺序跑。

`setup.ps1` 会写用户级 PATH / AX_ROOT，并可能请求提权；工具链下载是 GB 级。因此把它塞进托管 CI，只会得到一条长期红着的必跑项，或者一堆 `continue-on-error` —— 两种都会让 CI 失去意义。

**结论**：105 项主流程检查继续按 `README` 的方式在维护者机器上跑；CI 承担的是"三平台构建与跨平台行为"这一层，两者互补而非替代。真要把它接进 CI，前置条件是**把主机无关的断言从这套验收工具里分出来**，而不是想办法让 MSVC 装进 runner —— **§2.6 的 `--check-cli-json` 就是这条路走通的第一步**：它不是"让 CI 跑整套"，而是"把不依赖引擎的那一段切出来，做成自带 `return` 的独立模式"。同一个手法可以继续用于其余任何一段主机无关的断言。

`AxmolHub.Checks` 项目本身是 `net8.0-windows`，因为它带一批 Windows 专属断言（PowerShell 子进程、vswhere 探测 VS）。这也是它当前只能待在 Windows 矩阵里的原因之一。

#### 本机实跑发现的三处环境绑定（2026-10-02）

在此之前这套检查从未在本机跑到底过。实际跑一次后，**9 项前置断言全过**（含"通过真实 `axmol.ps1 new` 创建工程"这条，调用栈明确经过 P1 挪动后的 `src/AxmolHub.Core/Scripts/Invoke-Axmol.ps1`，**证明脚本挪位生效**），但随后的断言撞上三处**与被测代码无关、只与机器/目录有关**的绑定。三条都必须先解开，才谈得上"接 CI"：

1. **`Build PATH excludes D drive tools` 隐含"工作根不在 D 盘"**（**已于同日改写**，见下）。`Checks.exe` 的 `args[0]` 决定 `toolsRoot`，`BuildEnvironment` 把它拼进 `PATH`，于是断言 `!PATH.Any(p => p.StartsWith("D:"))` 在工作根位于 D: 时**必红**。注意 **GitHub 的 Windows runner 工作目录就是 `D:\a\<repo>\<repo>`** —— 这条若原样接进 CI，是一条当场必红的线。<br>改写方式：断言的原意是"**不继承宿主开发环境**"，而"不含 D 盘"只是它的一个代理判断。改为直接表达原意且与机器无关：**子环境 PATH 的每一项都必须能归到「受管工具根」或「操作系统目录」里**，归不进去的就是继承来的。另加一条反退化断言（受管目录确实在起作用），避免 PATH 为空时上一条空过。同一文件里 `wasmEnvironment["PATH"].Contains("D:")` 是同类问题，一并改写。
2. **`Real official CLI creates project and exact version lock` 把版本写死为 `2.11.5`**（**已于同日修正**：改为比对 `engine.Version`，见下）。当时本机唯一的完整引擎树是 **2.11.6**（`core/axmolver.h.in` 的 `AX_VERSION_PATCH = 6`），因此该断言在这台机器上无法通过，与代码改动无关。这也是"单引擎版本绑定"阻塞点的**活样本**：Hub 明明能为 2.11.6 创建工程、`ValidateEngine` 也放行，但 `module-manifest.json` 只有 2.11.5 一个 profile。
3. **经真实 `axmol.ps1 new` 创建工程会撞 MAX_PATH，这决定了检查套件能在哪个目录下跑**。目标路径
   `…\projects <guid>\HelloAxmol\proj.ios_mac\ios\targets\tvos\Images.xcassets\Brand Assets.brandassets\App Icon - App Store.imagestack\Middle.imagestacklayer\Content.imageset\Contents.json`
   实测 **260 字符**，`CopyDirectoryInfoItem` 抛 `DirectoryNotFoundException`。减去 guid(32) 与固定后缀(178)，**工作根的绝对路径必须 ≤ 49 字符**。成因是 Axmol 模板 iOS/tvOS 资产的嵌套深度，不是 Hub 的问题，但它是一条硬约束。

**结论仍不变**：这套检查的价值在"主机无关的断言"那一半。要接 CI，应先做一次"断言分拣"——把上面那类机器绑定断言改成显式注入（工作根、引擎版本从参数来），而不是让它们继续依赖开发机的盘符与目录深度。

#### A1 修复后重跑（同日）

解多引擎版本绑定时顺带修掉了全部三类机器绑定，重跑结果从 **9 项推进到 87 项**（断言总数由 102 增至 105）：

| 改动 | 效果 |
| --- | --- |
| 工程创建与状态持久化两条断言从写死 `"2.11.5"` 改为比对 `engine.Version` | 不再因本地引擎版本不同而失败。它们要测的是"版本锁定语义"，本来就不该钉住某个具体版本号 |
| 两条 PATH 断言（`BuildEnvironment` 与 WASM `CreateEnvironment`）从"不含 D 盘"改为"每一项都归入受管根或系统目录" + 反退化断言 | **在 D: 盘工作根下也通过**（实测 `leaked:` 为空、受管目录 5 条），即不再依赖仓库/工作根在哪块盘上 —— 这正是 CI 上 `D:\a\...` 需要的性质 |
| 新增 2 条配方断言（已声明版本通过 / 未声明版本拒绝） | 把"A1 解的是代码级绑定"这件事本身纳入回归 |
| `tests/AxmolHub.Checks.csproj` 增加 `manifests/*` 的 `Content` 复制 | 行为检查与三个 exe 现在看到**同一份部署布局**；此前 Checks 是唯一不部署清单的可执行项目，而 `PackagingRecipes` 从 `AppContext.BaseDirectory/manifests` 读验证边界 |

**现在停在哪里，以及为什么这是对的。** 修复后最先失败的是 `EngineModules.Plan(engine, ["android","web"])`（`Program.cs:505`），异常为 `No verified module profile for Axmol 2.11.6.` —— 即本地引擎树的版本在 `module-manifest.json` 里**没有 profile**。这是**刻意保留的失败关闭边界**，不是待修的 bug：解 A1 只把"**改代码**"那一半解掉了，为 2.11.6 / v3 建 profile 仍然需要有人真的在那个版本上验证过模块与打包配方。**没有为了让检查跑完而去添加 profile。**

**第 3 条（MAX_PATH）的性质与另两条不同**：它不是断言写法问题，而是 Axmol 模板 iOS/tvOS 资产的真实嵌套深度，只约束"工作根绝对路径要多短"。实测 `D:\dev\simdsoft\axmol-hub\artifacts\checks-d`（44 字符）已可跑通；49 字符时正好触到 260。CI 上 `D:\a\<repo>\<repo>` 很短，不构成问题，因此**不需要改写，只需要记录**。

**仍需注意**：`Check()` 是**失败即抛**，所以上面每一轮数字都是"走到第一个失败为止"，不是"总共只有这些断言失败"。要从"一次暴露一个失败"变成"一次暴露全部"，得改断言收集方式 —— 那是接 CI 之前更值得先做的一步。

---

## 4. 发布（`build.yml` + `dist.yml`）

2026-10-03 起改为两个 workflow，语义对齐 axslcc 的 `build` + `dist`（验证与打包合进 `build.yml`，旧的 `ci.yml` 与 `release-windows.yml` 已删）：

**`build.yml`**（`on: push` master + `pull_request` + `workflow_dispatch`）—— 两类 job：

- `meta`：解析提交信息是否为 `^Version x.y.z$`，输出 `should_package`（普通提交只验证、不打包）。
- `verify`：三平台矩阵（§2 的验证职责，任何提交都跑）。
- `package-*`：四个 job，`needs: meta` + `if: should_package == 'true'`，各在原生 runner 上打包：
  1. `--prepare-packaging`：按 `installer/packaging-manifest.json` 下载 Velopack CLI **1.2.161** 的 `.nupkg`，校验 SHA-256（`ac9be738…`），再从已校验的本地源把 `vpk` 装进 `artifacts/packaging-tools/vpk`，**不修改 PATH**。
  2. `installer/Publish.ps1 -Stage Build -Runtime <rid>`：`vpk download github` 拉回上一版（供 delta）→ `installer/Build.ps1 -NoClean` 发布自包含 App 并 `vpk pack`，产出安装器 / `.nupkg` / `releases.<channel>.json`，安装包改名为 `axmol-hub-<version>-<runtime>.<ext>` 并写 `.sha256`。版本号读自仓库根的 `Directory.Build.props`，脚本里没有第二份副本。
  3. `upload-artifact` 上传产物，artifact 名 = 平台目录名（`win-x64` 等），供 dist 的 `Publish-All.ps1` 按目录定位。
  4. Windows job 额外跑 `installer/Test.ps1 -Isolated`（一次性身份 + 相邻版本，验证安装/升级/卸载/数据保留）。**排在发布之前**：验收不过就不该有新 release。

**`dist.yml`**（`on: workflow_run` 监听 build + `workflow_dispatch`）：

5. 以完整 Git 历史检出触发 build 的提交，解析提交信息 `^Version x.y.z$`（接受 `x.y.z-beta`）→ 决定 `release_ver`；匹配不到则用手动输入的 `version`，两者皆无则跳过全部后续步骤。
6. 用最近一个可达 tag（排除本次 `v<version>`，兼容重跑）到本次构建提交的 `git log` 生成英文发布日志；每项包含短 SHA、提交链接和原始标题，并附英文完整 compare 链接。首次发布没有旧 tag 时收集截至当前提交的全部历史。
7. `dawidd6/action-download-artifact` 下载三平台产物。
8. `installer/Publish-All.ps1`：逐平台裁剪 feed 只留本版 → 收集安装包 + sha256 + full/delta nupkg（`vpk` 原名带 `-<channel>-` 段，上传时改名为小写连字符 `axmol-hub-<version>-<rid>-{full|delta}.nupkg`，**并同步改写 feed 的 `FileName`**）+ 按 channel（= 完整 RID）命名的 feed `releases.<rid>.json` → `gh release create --target <本次构建 SHA> --notes-file <发布日志>` / `gh release upload --clobber` → 回读资产清单确认每一件都在。重跑已有 release 时用同一份日志更新正文。需要 `permissions: contents: write` 与 `GH_TOKEN`。

产物平台对照：Windows `win-x64.exe`、macOS `osx-{arm64,x64}.pkg`、Linux `linux-x64.AppImage`；主程序参数三平台统一用 `--mainExe`（`--exeName` 是 1.2.161 之后未发布的新名）。三个平台装进同一个 tag 的同一个 release。

---

## 5. 本机验证记录（2026-10-02）

> 下表是 **P6 删除 WPF 版之前**的记录，因此出现 `AxmolHub.App` / `AxmolHub.App.exe`。
> P6 之后这些路径指向 `AxmolHub.App` / `AxmolHub.App.exe`，
> 安装链路本身未经重跑 —— **发布前必须重跑 §4 全链路**。保留原记录是因为它是"这条链路真的走通过"的证据。

| 验证项 | 结果 |
| --- | --- |
| 三平台 CI 里的构建命令 | Windows 本地 `Build succeeded. 0 Warning 0 Error`（Core / Cli / App / Checks 四个项目） |
| 宿主检测断言脚本 | 正反双向通过（Windows 断言 exit 0，错误宿主断言 exit 1） |
| `--smoke` 无头截图 | exit 0，产出 53964 字节 PNG |
| Velopack 钩子拦截 | `AxmolHub.App.exe --veloapp-install 0.1.6` exit 0，**不弹 GUI**（自定义 `Main` 生效） |
| 默认每用户数据根 | 不带 `--data-root` 启动即创建 `%LocalAppData%\AxmolHub\data` |
| `--prepare-packaging` | `.nupkg` SHA-256 校验通过；`vpk` 1.2.161 装进 `artifacts/packaging-tools/vpk`，exit 0 |
| `installer/Build.ps1` | vpk 打包成功，产出 `Axmol.Hub-win-Setup.exe`（83,730,340 B，SHA-256 `2fe5efa9…`）、`Axmol.Hub-win-Portable.zip`（76,064,070 B，SHA-256 `ea864d58…`）、`Axmol.Hub-0.1.6-full.nupkg`、`releases.win.json` |
| `installer/Test.ps1 -Isolated` | **9 项断言全过**：静默安装、载荷完整、自包含启动、中文与空格路径、**真实升级 0.1.6 → 0.1.7 生效**、升级保留用户数据、卸载移除载荷、卸载移除注册项与快捷方式、卸载保留用户数据；`ResidualInstallDirectory=false` |
| 两份 workflow YAML | 结构校验通过 |

**尚未验证**：macOS / Linux 上的实际执行（本机是 Windows，只能靠静态核对确认无 Windows 专属 API；且非 Windows RID 目前有显式守卫）；**签名链路**（`vpk` 报 `No signing parameters provided, 464 file(s) will not be signed`，属预期 —— 凭据尚未申请到）；**增量包**（需要在发布目录里保留上一版产物）。

---

## 6. 原阻塞项：Inno Setup 的许可 —— 以换栈解决

**现象（2026-10-02 首次发现）**：固定版 Inno Setup 6.7.3 编译器在构建时打出 `Non-commercial use only`；但仓库内 `licenses/Inno-Setup.txt` 的文本写的是 "Permission is granted to anyone to use this software for any purpose, **including commercial applications**"。

**核实**：Inno Setup 6.7.3 的发布说明与官方下载页都明确写着 **"Using Inno Setup commercially? Please purchase a license."**，该要求自 6.7 起引入。仓库固定的正好是"开始要求商业许可"的那个版本，而第三方声明里放的是 6.7 之前的旧条款 —— 两者不一致。

**处置：打包栈整体换成 Velopack（MIT）**，而不是购买 Inno 商业许可。理由不只是许可：

| | Inno Setup | Velopack |
| --- | --- | --- |
| 许可 | 6.7 起商业使用需付费许可 | MIT |
| 平台 | Windows 独占 | Windows `Setup.exe` / macOS `.pkg` / Linux `.AppImage` |
| 自动更新 | 无 | 自带，更新源是静态文件（`releases.<channel>.json`） |
| 增量包 | 无 | 有（`-delta.nupkg`） |

换栈同时消除了"先在 Windows 付一次许可成本、再为 macOS/Linux 各引一套工具链"的重复决策，也移除了第三方声明里唯一一处**带商业许可条件**的组件 —— 这一点对 SignPath 的 OSS 签名申请是前置条件（其条款要求"所有组件都是无商业双许可的 OSI 许可"），见 [hub-development-plan.md §4 D5](hub-development-plan.md)。

**代价（已接受并记入 installer/README.md）**：Velopack 的 Windows 安装器是一键式的，没有安装目录选择页与安装语言选择；桌面快捷方式固定为不创建。另外它更新时整体替换安装目录下的 `current\`、卸载时删除整个安装目录，因此 **Hub 的设置与数据根必须移到安装目录之外**（`%LocalAppData%\AxmolHub\`）。

已删除：`installer/AxmolHub.iss`、`installer/compiler-manifest.json`、`installer/ChineseSimplified.isl`、`installer/Verify-CompilerSignature.ps1`、`licenses/Inno-Setup.txt`。
已新增：`installer/packaging-manifest.json`、`licenses/Velopack.txt`。
