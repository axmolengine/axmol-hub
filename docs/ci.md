# 持续集成与打包

日期：2026-10-02
关联：`.github/workflows/ci.yml`、`.github/workflows/release-windows.yml`、[installer/README.md](../installer/README.md)

---

## 1. 两个 workflow 的分工

| workflow | 触发 | 目的 | 是否阻塞合并 |
| --- | --- | --- | --- |
| `ci.yml` | push 到 `master`、PR、手动 | 三平台构建健康度 | **是** |
| `release-windows.yml` | tag `v*`、手动 | Windows 安装包（Velopack）与安装验收 | 否（发布用） |

分开发的理由：安装包链路要下载固定版 Velopack CLI、发布自包含 App、再做真实安装/升级/卸载验收，属于发布动作；把它塞进每个 PR 会让 CI 变慢、变脆，而且失败信息（打包器、签名之类）与"代码是否可构建"无关。

---

## 2. `ci.yml`：三平台矩阵

矩阵为 `windows-latest` / `macos-latest` / `ubuntu-latest`，每个平台做四件事：

1. **构建 Core + CLI** —— 两者都是 `net8.0`，不含任何 Windows 专属 API（无 `Registry`、无 P/Invoke），全部跨平台差异走 `OperatingSystem.IsWindows()` 运行时分支，因此三平台都应编译通过。
2. **断言宿主检测** —— 见下节。
3. **发布自包含 CLI** 到对应 RID（`win-x64` / `osx-arm64` / `linux-x64`）。
4. **在目标宿主上真的执行一次刚发布的二进制** —— 交叉发布本身不构成证据，能跑才算。

Windows 额外做两件事：构建 `AxmolHub.App` 与 `AxmolHub.Checks`，以及跑 `--smoke` 无头截图（断言退出码为 0 且 PNG 大于 8KB）。

### 2.1 为什么唯一的行为断言是"宿主检测"

Core 里唯一按操作系统分叉的逻辑就是 `BuildTargets.Host`，而它直接决定 GUI 会告诉用户"哪些目标能在本机构建"。断言方式是跑 `AxmolHub.Cli targets`，然后检查每个目标的 `current=` 值：

| 宿主 | 必须为 `current=True` | 必须为 `current=False` |
| --- | --- | --- |
| Windows | `windows-x64`、`uwp-x64`、`android-*`、`wasm32` | `linux-x64`、`macos-*`、`ios-*` |
| macOS | `macos-arm64`、`macos-x64`、`ios-*`、`tvos-*`、`android-*`、`wasm32` | `windows-x64`、`uwp-x64`、`linux-x64` |
| Linux | `linux-x64`、`android-*`、`wasm32` | `windows-x64`、`uwp-x64`、`macos-*`、`ios-*` |

这套断言不依赖工具链、不依赖引擎源码、不依赖网络，是当前唯一能在三平台都跑起来的行为检查，因此放在必跑路径上。

> **实现细节**：CLI 在 Windows 上输出 CRLF，`grep -E '...current=True$'` 会因行尾残留的 `\r` 匹配失败。管道里统一加了 `tr -d '\r'`。这一条已在本机用 Git Bash 正反两向实测（错误宿主断言返回 1、正确宿主返回 0）。

### 2.2 为什么 GUI 构建被限定在 Windows

`AxmolHub.App` 是 `net8.0-windows` + `UseWPF=true`，macOS/Linux 上根本无法编译。相应的步骤都带 `if: matrix.host == 'windows'` 并附了注释：

```yaml
# App 目前是 net8.0-windows + WPF，只能在 Windows 构建；Checks 依赖真实工具链同样留在 Windows。
# Avalonia 迁移完成后删掉这两步的 if，GUI 即进入三平台矩阵。
```

**Avalonia 迁移每推进一步，就删掉一个 `if`，矩阵自动扩大。** 详见 [avalonia-migration-plan.md](avalonia-migration-plan.md)。

### 2.3 为什么没有加 `.sln`

仓库没有解决方案文件，`dotnet build` 一次只能接一个 csproj。加一个 `.sln` 能让本地"一条命令构建全部"，但会让 CI 变差：`dotnet build Hub.sln` 在 macOS/Linux 上遇到被排除的 WPF 项目会直接失败，反而失去按平台裁剪的能力。所以 CI 里保持逐项目显式构建，与 `installer/*.ps1` 现有风格一致。

### 2.4 SDK 版本

CI 固定 `8.0.x`（`README` 要求的就是 .NET 8 SDK）。开发机可能装的是更高版本（例如只有 SDK 10），这本身是额外信号：能同时通过 8 与 10 说明没有依赖新 SDK 行为。

### 2.5 action 版本策略

所有官方 action 取**浮动主版本标签**（`actions/checkout@v7`、`actions/setup-dotnet@v6`、`actions/cache@v6`、`actions/upload-artifact@v7`），不钉到补丁版本：安全修复与运行时不升级是拿不到补丁的，钉死反而会攒出一次大跨度升级。

需要注意的约束是**运行时**：这几个主版本都跑在 Node.js 24 上，要求 **Actions Runner ≥ 2.327.1**。GitHub 托管 runner 满足；若将来迁到自托管 runner，必须先升级 runner。历史上 Node.js 20 的 action 已于 2026-06 被强制移除 —— 这正是"浮动主版本 + 定期跟进"优于"长期钉死"的原因。

仓库**尚未配置 Dependabot**，action 与 NuGet 的版本更新目前靠人工检查。

---

## 3. 为什么 102 项行为检查**没有**进 CI

`tests/AxmolHub.Checks` 不是单元测试套件，而是**维护者验收工具**，它需要：

- 一个已经准备好的 data root（引擎已安装、工具链已就位）；
- 真实的 Axmol 2.11.5 完整源码；
- 托管工具链（CMake / Ninja / MSVC / Android SDK / JDK 等），且明确**不允许回退到系统工具**；
- 若干子模式（`--prepare-release-check`、`--check-release-receipt`、`--check-android-verification`、`--prepare-packaging` 等）彼此有先后依赖，要按顺序跑。

MSVC 的安装需要微软官方安装器与 UAC，GitHub 托管 runner 上无法按 Hub 的规则完成。因此把它塞进托管 CI，只会得到一条长期红着的必跑项，或者一堆 `continue-on-error` —— 两种都会让 CI 失去意义。

**结论**：102 项检查继续按 `README` 的方式在维护者机器上跑；CI 承担的是"三平台构建与跨平台行为"这一层，两者互补而非替代。真要把它接进 CI，前置条件是**把主机无关的断言从这套验收工具里分出来**，而不是想办法让 MSVC 装进 runner。

`AxmolHub.Checks` 项目本身是 `net8.0-windows`，因为 Core 的 `WindowsToolchainInstaller` 相关断言在 macOS/Linux 上无意义。这也是它当前只能待在 Windows 矩阵里的原因之一。

---

## 4. `release-windows.yml`

链路（已在本机完整跑通，见 §5）：

1. `--prepare-packaging`：按 `installer/packaging-manifest.json` 下载 Velopack CLI **1.2.161** 的 `.nupkg`，校验 SHA-256（`ac9be738…`），再从已校验的本地源把 `vpk` 装进 `artifacts/packaging-tools/vpk`，**不修改 PATH**。
2. `installer/Build.ps1`：发布自包含 App → `vpk pack` 产出 `Setup.exe` / `Portable.zip` / `.nupkg` / `releases.win.json` → 给用户可下载的产物写 `.sha256`。版本号读自 `AxmolHub.App.csproj`，脚本里没有第二份副本。
3. 上传上述产物与更新索引。
4. `installer/Test.ps1 -Isolated`：一次性 packId / 程序名 / 安装目录 + 两个相邻版本，验证静默安装、载荷完整、自包含启动、中文与空格路径、跨版本升级保留用户数据、卸载移除载荷与注册项且保留用户数据，结束后自行清理。
5. 上传验收证据到 `artifacts/install-checks/`。

---

## 5. 本机验证记录（2026-10-02）

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
