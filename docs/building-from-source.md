# 从源码构建、验收开关与检查

本文是 README 中「从源码运行」「CLI」「检查与打包」的详细版。

## 从源码运行界面

界面在 `src/AxmolHub/`（`net8.0`，目标三平台）。开发机需要装 **.NET 10 SDK**（最稳妥：`net8.0` 目标框架下，Avalonia 12.1.6 的分析器/源生成器是按编译器 4.14 编译的，而 SDK 8 的旧补丁——例如 Ubuntu 上 `apt install dotnet-sdk-8.0` 拿到的 8.0.1xx——只带编译器 4.8，源生成器整个不产出，报一排 `CS0103: The name 'InitializeComponent' does not exist`。SDK 8 只有够新的补丁、编译器 ≥ 4.14 才够，.NET 10 一定够）。**SDK 版本要求就是 10.0.x**，构建命令各平台一致，差别只在怎么装 SDK。

### 安装 .NET 10 SDK

装完先确认：

```bash
dotnet --version        # 期望 10.0.x
dotnet --list-sdks      # 列出已装的 SDK
```

**Windows**

```powershell
winget install Microsoft.DotNet.SDK.10
```

**macOS**

需要 macOS 14（Sonoma）或更新。Apple Silicon（M1–M5）选 **Arm64**，Intel 选 **x64**。

```bash
# 方式 A —— 官方 .pkg 安装包（推荐）
# https://dotnet.microsoft.com/download/dotnet/10.0 → SDK → macOS

# 方式 B —— 安装脚本，无安装界面、无管理员弹窗（装到 ~/.dotnet）
curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 10.0 --architecture arm64   # Intel 用 x64

# 脚本不会改 shell 配置，此刻 PATH 里还没有 dotnet：
echo 'export DOTNET_ROOT=$HOME/.dotnet' >> ~/.zshrc
echo 'export PATH=$PATH:$DOTNET_ROOT:$DOTNET_ROOT/tools' >> ~/.zshrc
exec zsh

# 方式 C —— Homebrew
brew install --cask dotnet-sdk
```

**Ubuntu**

```bash
# Ubuntu 24.04 / 25.04 / 25.10 / 26.04 —— .NET 已在 Ubuntu 自己的源里，不用加任何第三方源
sudo apt-get update && sudo apt-get install -y dotnet-sdk-10.0

# Ubuntu 22.04 —— .NET 10 只来自 Canonical 的 backports PPA
sudo add-apt-repository ppa:dotnet/backports
sudo apt-get update && sudo apt-get install -y dotnet-sdk-10.0
```

**这里只装 SDK，不再装 `dotnet-runtime-8.0`。** 产物是 `net8.0`，但仓库在 `Directory.Build.props` 里声明了 `RollForward=Major`：运行期要的是**地板 8.0**，本机装的 10.0 运行时就够（`dotnet --list-runtimes` 只列出 10.0.x 也能跑）。过去这里多写一个 `dotnet-runtime-8.0`，是因为默认可滚前策略（`Minor`）不跨主版本——而 Ubuntu 26.04 起源里已经没有这个包，.NET 8 也已于 2026-11-10 出保。若你仍想让它跑在 8.0 上，装上它同样可以：声明的是地板，不是钉。已经构建好的树不想重编，也可以临时 `DOTNET_ROLL_FORWARD=Major dotnet src/AxmolHub/bin/Release/net8.0/AxmolHub.dll ...`。

不要再按老教程去加 `packages.microsoft.com`：微软**已不再**通过它为 Ubuntu 提供包（且该源只有 x64），和 Ubuntu 源混用正是 .NET 包冲突（package mix-up）报错的来源。arm64 主机请用 Ubuntu 源或上面的安装脚本。

然后在本仓库根目录执行：

```powershell
dotnet build src/AxmolHub/AxmolHub.csproj -c Release
dotnet run --project src/AxmolHub -- --data-root ./data
```

macOS 本地构建会在当前编译的 `obj/` 目录下生成 `macos/Axmol Hub.app`，`dotnet run` 自动运行其中的程序。开发包的 `Info.plist` 明确指定 `Axmol Hub` 显示名称及应用图标，供 Dock 使用；启动参数和工作目录保持不变。`--no-build` 复用上次生成的开发包，因此首次运行或代码更新后需要先构建。这不改变发布安装包的打包流程。开发包的 `CFBundleIdentifier` 是 `org.axmol.hub.development`：macOS 的通知授权按这个身份记录，所以开发包与安装版各有一份独立的通知权限。

**四个页面（项目 / 引擎 / 工具链 / 设置）与四个对话框窗口**：左侧 218px 导航、三行内容区（页面 / 状态栏 / 日志面板）、统计卡、表格、设备条、操作按钮行的位置与文案都与原 WPF 版对齐；与原版只有三处刻意差异，原因写在 [avalonia-migration-plan.md](avalonia-migration-plan.md) §5.7。业务逻辑全在 `AxmolHub.Core`（界面项目只负责装配），编排在 `Services/HubWorkspace.cs`。

**切换数据根已可用**：只重建工作区并丢弃页面缓存，**窗口本身不动** —— 因此它不依赖外壳形态。新根落盘失败时会在旧状态上就地报错，不会留下"界面指向新根、设置文件还写着旧根"的错位。

默认数据根是每用户的 `%LocalAppData%\AxmolHub\data`，设置保存在 `%LocalAppData%\AxmolHub\hub-settings.json` —— 不能放在程序目录旁边，因为 Velopack 更新时整体替换安装目录、卸载时删除整个安装目录。可以指定独立配置：

```powershell
dotnet run --project src/AxmolHub -- --data-root ./data --preferences ./artifacts/dev-preferences.json
```

`--data-root` 优先于保存的设置；`--preferences` 可用于隔离开发或检查配置。不要将设置、数据目录或下载的工具链提交到 Git。

### macOS 开发构建：钥串提示与本地签名

密钥与登录令牌在登录钥串里只用**一条**条目（服务名 `dev.axmol.hub.<数据根的 sha256>`，账户 `hub-secrets`，值是「凭据 id → 密钥」的 JSON）。旧版本按凭据各存一条，读取时会被折叠进这条并删掉旧的——所以升级后每个凭据第一次被读到时仍会弹一次，之后不再弹。

如果每次重新构建后都再弹一遍，变了的是**签名身份**：未签名的二进制以 cdhash 作为身份，每次构建都不同，「始终允许」记的授权对不上。构建后用证书签一次开发产物，身份就变成「identifier + 证书」，可以跨构建沿用：

```bash
pwsh installer/Sign-DevBuild.ps1
```

脚本默认用登录钥串里第一个可用的代码签名身份（开发机通常已有 `Apple Development: …`），签 `src/AxmolHub/bin/Debug/net8.0/AxmolHub` 并打印 designated requirement，好让你确认下次启动匹配到的就是它。一个身份也没有时，脚本会说明怎么建：钥串访问 → 证书助理 → 创建证书…，类型选「代码签名」。

## 命令行

```powershell
dotnet run --project src/AxmolHub.Cli -- <动词> [参数]
```

只构建 Hub 时用本机 .NET SDK；它**不会**被当作游戏构建工具链。游戏所需工具由引擎自己的 `setup.ps1` 准备。

## 运行期验收开关

界面项目自带几个开关。它们读的是**运行期真实对象**，不是"看代码对不对" —— 原因见 [avalonia-migration-plan.md](avalonia-migration-plan.md) §3.1：Avalonia 的样式与模板写错**不会报错**，只会静默退化。

最常用的启动人工验证命令: `dotnet run --project src/AxmolHub -c Release -- --wire-log /Users/simdsoft/oss/axmol-hub/data/wire-1105`

```powershell
# 外壳、本地化与数据根切换的自检
dotnet run --project src/AxmolHub -- --data-root ./data --verify-shell ./tmp/shell-check.txt
# 主题层 / 基础件
dotnet run --project src/AxmolHub -- --data-root ./data --verify-theme ./tmp/theme.txt
dotnet run --project src/AxmolHub -- --data-root ./data --verify-foundation ./tmp/foundation.txt
# 无头截图：主窗口一张
dotnet run --project src/AxmolHub -- --data-root ./data --smoke ./tmp/smoke.png
# 无头截图：四个页面 × 中英两种语言，共 8 张（README 里的页面图由此生成）
dotnet run --project src/AxmolHub -- --data-root ./data --smoke-pages ./tmp/pages
# 真操作验收：在真实引擎源码树上跑引擎管理链路（后面跟若干个引擎目录）
dotnet run --project src/AxmolHub -- --data-root ./data --verify-ops ./tmp/ops-check.txt <引擎目录> [更多引擎目录...]
# 唯一一条**真的出网**的验收：拿生产句体与生产桥抓一个被点名的 https 页，无头，退出码即失败断言数
dotnet run --project src/AxmolHub -- --check-webfetch https://www.lua.org/manual/5.4/readme.html --preferences ./tmp/probe.json
```

`--check-webfetch` 与其余开关的分工要说清：其余全部跑在桩替上（`ClientOverride` 的脚本模型、`WebFetchHttp` 的记录 handler），
所以它们能证"策略与措辞对不对"，永远证不了"DNS 解析了吗、TLS 握手了吗、gzip 拆了吗、GBK 页猜对了吗、
服务器会不会拒绝我们这个 User-Agent"。这一条把那一跳变成可重跑的，而不必有人开着窗口试；它只发一个 GET、不写任何东西，
且先按 `--preferences` 指的那份设置读 `AllowOutboundWebFetch`——所以缺设置文件时它顺带证的就是"出厂即开"。
`result=ok` 之外，输出里必须看到的是**页面正文**：标题在最前，脚本样式与菜单不在。

上面的 `--data-root ./data` **不要省**：省了就用每用户的真实数据根，自检会读到你自己的引擎、项目与凭据（原因见上一节）。括号里的断言数**只是那一刻的实测值**——`--verify-shell` 的总数按场景扇出，改前改后都要自己跑一遍拿数，别拿算术去预测它。2026-10-22 实测：**1622/1622 通过，83.2 秒**（Debug、`dotnet run`；上限是 `SuiteCeiling` 的 180 秒，报告末行自己带着这两个数）。

`--verify-ops` 与另外三个的分工：它们验**界面**（跑在夹具上），它验**操作**（用真实引擎树）。它只跑**不下载、不编译**的那部分 —— 工具链探测、引擎导入/校验/设为默认/移除、状态落盘重载、无效输入拒绝；装引擎、装工具链、构建、运行、Android 打包会拉 GB 级数据或依赖完整工具链，因此**显式记为跳过**并附原因（报告里的 `SKIP`）。不传引擎目录时依赖引擎的那组会自动降级为跳过，所以在一台没有引擎的机器上它也能正常退出而不是失败。

> 临时产物统一写仓库的 `tmp/`（`cache/` 留给缓存类产物），两者都已在 `.gitignore` 里。
> 自检自己生成的夹具与截图也落在那里 —— 见 `Services/ScratchDirectory.cs`。

具体各读什么：`--verify-shell` / `--verify-theme` / `--verify-foundation` 读可视化树与渲染帧，`--verify-ops` 读**操作真的执行之后**的状态与磁盘内容。`--verify-shell` 还会扫描 `**/*.axaml` 里的每个 `{DynamicResource X}`，确认它真的能解析 —— 少一个 key 同样不报错，只显示空白。

两个截图开关各带一道**防假绿**的判据，因为"进程退出 0"是躺着也能过的：`--smoke` 要求那帧不是纯色（`SmokeCapture.FrameStats.IsBlank`）；`--smoke-pages` 在那之上再数一遍**不同内容**的张数 —— 八张全出来但只有 2 张不同，说明导航静默失效；只剩 4 张，说明切语言静默失效。两种都能让"非空白"全绿。另外它会真切语言并落盘，所以结束时**切回原语言**；跑之前把 `--preferences` 指向临时文件即可不碰你的真实设置。

界面文案是**单一定义**的：中英两份都在 `AxmolHub.Core/HubTexts.cs`，界面项目只留一层"把文案灌进 Avalonia 资源字典"的薄适配。设置页因此同时是本地化管线的**活体验收台** —— 切换语言要就地重灌资源字典并让**已经存在的控件**换文字，这件事读代码判断不了，只能实跑（见上面的 `--verify-shell`）。

`--verify-shell` 有三条值得单独说的断言：

AI 助手的外壳检查还覆盖了计划审批卡、批准后以 Agent 模式继续、修改/拒绝、待审批计数、通知过滤（含窗口失焦与最小化）与会话深链激活。任务栏/Dock 徽标统计所有至少有一项未解决审批的会话（包括当前打开的会话），同一会话的多项审批只计一次；决定完成后计数随之更新，任务完成本身不计入。「用户看得见这条会话」要三件事同时成立：窗口在前台（既未失焦也未最小化）、停在 Assistant 页、且该页正显示这条会话——三项同时成立时，审批卡已经在眼前，因此不重复弹系统通知；最小化或 Alt-Tab 走开后，即使走开的正是当前会话，也会提醒。Windows 任务栏 overlay 和 macOS Dock 支持数字徽标；Linux 桌面环境没有统一的任务栏数字徽标接口，所以只使用该桌面实际提供的通知能力。自检会禁用真实系统通知和原生任务栏/Dock 徽标；Windows Toast、macOS 通知中心及 Linux 通知守护进程的显示与点击行为仍须在对应桌面环境实测。macOS 走 UserNotifications 的 `UNUserNotificationCenter`（`NSUserNotification` 自 macOS 11 起 deprecated）：第一次真要发通知时才请求授权，被拒后后续通知会重试（用户在系统设置里重新打开后无需重启）；无头部分由 `--check-macos-notifications` 钉住 block 布局、delegate 编码与呈现选项，授权框、横幅与点击仍需手测。

**Linux 的三条点击路由**：`notify-send` 那一条命令同时埋了三个入口，参数形状由 `--check-linux-integration` 钉住（`SystemAttentionService.BuildLinuxNotificationArguments` 是纯函数，无显示器也能断言）：正文末尾的 `<a href="axmolhub://sessions/{id}">` 锚点——GNOME 对正文链接调 `Gio.app_info_launch_default_for_uri`，命中已注册的 `x-scheme-handler/axmolhub`，应用没在跑也有效；具名动作 `axmolhub-open`——会派发 `ActionInvoked` 的桌面把键回报给还在等的子进程，KDE 等走这条；`--hint=string:desktop-entry:axmol-hub`——让 Shell 能把通知归属到本应用，因为点横幅主体时 GNOME 只**前置已运行的窗口**，不重跑 `Exec`，也就没有第二个进程、没有深链，此时靠「最近通知的那条会话」补一跳（`MainWindow.OnWindowActivated`，TTL 90 秒、取一次即清）。两个本机实测出来的坑：`--action` 的语法是 `[NAME=]Text`，旧的 `default,Open` 没有等号，动作键退化成序号 `0`，点击因此永远认不出来；而**不能**拿规范里的 `default` 当动作名——GNOME 46 在横幅约 5 秒超时、无人点击时会自己回报 `default`，通知就会凭空把视图挪到那条会话上。对照实验：`--action=default=…` 8 秒内退出并打印 `default`，`--action=axmolhub-open=…` 静默 30 秒不打印。窗口侧同样实测过：启动那次 `Activated` 在通知 arm 之后约 13 毫秒就到，比任何点击都早，所以只有「确实离开过前台」之后的回归才被当作一次点击（`_foregroundHasLeftWindow`，`--verify-shell` 直接钉住这两步）。

**Windows 通知与任务栏标记手动诊断**：使用单独的数据目录启动应用，可立即请求一个示例 Toast 和 30 秒任务栏标记，不会创建或修改真实会话。开发版：

```powershell
dotnet run --project src\AxmolHub -- --data-root .\tmp\attention-test-data --preferences .\tmp\attention-test-preferences.json --test-system-attention
```

安装版可用同一参数启动安装目录中的稳定启动器（将路径替换为本机实际位置）：

```powershell
& "$env:LOCALAPPDATA\dev.axmol.hubapp\Axmol Hub.exe" --data-root .\tmp\attention-test-data --preferences .\tmp\attention-test-preferences.json --test-system-attention
```

应用日志位于 `.\tmp\attention-test-data\logs\`，筛选 `[System attention]` 可检查开始菜单快捷方式的 AppUserModelID、通知子进程结果与任务栏 overlay 的 HRESULT。成功特征：数字徽标是 `The diagnostic numeric taskbar badge was requested successfully.`，Windows toast 是 `Started Windows toast helper …` 紧跟 `Windows toast helper completed … Toast.Show completed.`。失败特征同样直白：`Could not update the taskbar badge: CoCreateInstance(CLSID_TaskbarList, IID_ITaskbarList3) returned 0x80004002`，那个 `0x80004002`(E_NOINTERFACE) 就是 0.8.x 整段时间红点不亮的原因——它不是 Windows 的行为，而是代码里的 `IID_ITaskbarList3` 字面量末段抄错（`…9E9F8A5EEA84`，真值 `…9E9F8A5EEFAF`），这个值在 `explorerframe.dll` 里一次都没有、`HKCR\Interface` 里也没有；`--verify-shell` 现在把两个标识符连同四个易混近邻一起钉住。Windows 设置中还需允许 Axmol Hub 通知，并关闭勿扰/专注助手后重测。Toast 需要开始菜单中的 `Axmol Hub.lnk` 带有匹配的 AppUserModelID（同机的 Electron 客户端只靠这一条就够，不需要再写 `HKCU\Software\Classes\AppUserModelId`）；诊断会报告快捷方式缺失或写入失败。开发版只有在已有匹配快捷方式（通常由安装版创建）时才能完成 Toast 展示验证；任务栏徽标诊断不依赖安装版。若生产日志出现 `No system notification for approval ...`，表示会话仍正在前台显示审批卡，按策略不会重复弹系统通知；若出现 Toast 成功而桌面未显示，则检查 Windows 通知权限和勿扰/专注模式；若没有 `Started Windows toast helper`，则先查审批事件与通知闸门。自检模式不会弹真实通知，`--test-system-attention` 才是桌面手测入口。**但它证明的是投递，不是触发**：诊断直接调 `Show()`，绕过 `ShouldNotifyApproval`/`ShouldNotifyRun` 闸门，所以它全绿也说明不了"最小化后会不会提醒"——那一半要按上一节的三条件手动走一遍：停在当前会话、起一个 run、把窗口最小化或 Alt-Tab 走开（不要切页、不要换会话）。

**macOS 通知手动诊断**：同一个 `--test-system-attention` 可以走开发包：

```bash
dotnet run --project src/AxmolHub -- --data-root ./tmp/attention-test-data --preferences ./tmp/attention-test-preferences.json --test-system-attention
```

第一次真要发通知时 macOS 会弹系统授权框；允许后横幅才出现。日志同样在 `tmp/attention-test-data/logs/`，筛选 `[System attention]`：`Requested macOS notification authorization.` → `macOS notification authorization was granted.` / `... was denied; enable Axmol Hub in System Settings > Notifications` → `Submitted a macOS notification for conversation …` 分别是请求、结果与投递；`macOS notifications unavailable: …` 说明当前进程不在 `.app` 里（裸 `AxmolHub.dll` 就是这样，属预期），`Ignored a macOS notification action that is not the default open action.` 说明用户关闭了横幅而不是点击。和 Windows 一样，诊断直接调 `Show()`，绕过 `ShouldNotifyApproval`/`ShouldNotifyRun` 闸门，所以它证明投递，不证明触发——触发要按三条件手动走一遍。点击横幅应回到对应会话；关闭横幅不应跳转；拒绝后可在“系统设置 → 通知 → Axmol Hub”重新打开再完成一次 run，验证自愈。

- **真切一次语言再切回来**，然后去读**早于切换就已建好**的控件上的文字（外壳导航项、设置页说明、引擎页表头）。它同时证明"已存在的控件跟着换文字"和"切回中文也生效"，并把设置文件落在临时目录里 —— 自检**不会**碰到你 `%LocalAppData%\AxmolHub\` 下的真实设置。去掉 `HubStrings.Apply` 那一行，构建照样 0 error，自检报 6 条 FAIL、退出 1。
- **切换一次数据根再切回来**：换根、落盘、旧页面被丢弃、引擎列表跟着变空、拒绝盘符根、重复切换是无操作。漏掉"丢弃旧页面"的后果是"界面看着正常，一点按钮就在读一个已经不属于当前会话的工作区"——去掉 `_pages.Clear()` 一行，构建照样 0 error，自检报 3 条 FAIL、退出 1。
- **导航遍历四页**后汇总验证内容宿主与侧栏高亮都落在目标页，同时保留页面实例和缓存行为检查；普通页面的逐帧截图不再作为重复自动断言。

## CLI

```powershell
dotnet run --project src/AxmolHub.Cli -- help
dotnet run --project src/AxmolHub.Cli -- targets
dotnet run --project src/AxmolHub.Cli -- plan ./data ./data/projects/MyGame Release
dotnet run --project src/AxmolHub.Cli -- build ./data ./data/projects/MyGame Release
dotnet run --project src/AxmolHub.Cli -- run ./data ./data/projects/MyGame
```

构建示例要求资料库、引擎、项目与托管工具已经准备好。省略配置时使用项目保存的配置，旧项目默认 Debug。macOS / Linux 必须在对应宿主准备私有工具。

`--json` 是全局标志，可放在任意位置；加上它之后 **stdout 里恰好只有一份 JSON 信封**（退出码与不加时一致，日志与错误详情仍走 stderr），供脚本、CI、MCP Server 与 Axmol Editor 消费：

```powershell
dotnet run --project src/AxmolHub.Cli -- targets --json
dotnet run --project src/AxmolHub.Cli -- --json verify ./data windows-x64
```

契约、各动词的 `data` 形状与两个刻意保留的例外见 [cli-json-contract.md](cli-json-contract.md)。

## 检查与打包

行为检查运行于 Windows，使用真实 Axmol CLI 创建临时项目。将 **Axmol 2.11.5 完整源码**放在仓库旁边的 `../axmol-2.11.5`，或将第二个参数替换为自己的引擎目录：

```powershell
dotnet run --project tests/AxmolHub.Checks -c Release -- ./artifacts/checks ../axmol-2.11.5
```

每次复验使用新的输出目录，避免已有测试工具影响"组件缺失"检查。行为检查现共 **140 条断言**：主流程 **105 条**（覆盖进程参数、取消、下载校验、路径边界、metadata、配置隔离、资源发布及 Android 打包/设备参数等）+ CLI `--json` 契约 **35 条**（2026-10-15 重数：新增一条「信封内部换行是 LF」）；不替代真实设备或干净宿主验收。

其中 CLI 契约那 35 条**不依赖引擎与工具链**，因此已接进 CI（[ci.md](ci.md) §2.6），可单独跑：

```powershell
dotnet run --project tests/AxmolHub.Checks -c Release -- ./artifacts/checks --check-cli-json src/AxmolHub.Cli/bin/Release/net8.0/AxmolHub.Cli.dll
```

> 注：此前 README 写"112 项"，与 `hub-development-plan.md` §3 与 `ci.md` §3 的 105 对不上，是不同时间点用不同数法留下的。现已统一为上面的分解。

### AI 助手的十二组检查

助手层**不需要引擎、不需要网络、不需要 API key**：模型那一侧经 `ChatWorkspace.ClientOverride` 注入一个脚本化的 `IChatClient`，出网那一侧经 `ChatWorkspace.WebFetchHttp` 注入一个从内存里答复的 `HttpMessageHandler`（`--verify-shell` 里 `web_fetch` 走的就是它），托管工具的线形状那一侧经 `ChatClientFactory` 的 transport 缝注入同一个记录用的 handler，地址写 `127.0.0.1`（`--check-ai-web-search` 读的正是它序列化出去的字节）——自检期间没有一个字节离开机器，跑的是真的 Core/Agent 代码。所以这十二组在任何机器上都能单独跑，也是不需要工具链的那部分 AI 验收；界面那一半由上面的 `--verify-shell` 负责（同一套代码的活对象）。这句话只描述自检，不描述产品：真正运行的 Hub 会经这两个缝之外的一条出网，验收断言里出现的"零网络"是桩替出来的结果，不是发布的口径 —— 发布口径在 README 的 Privacy policy 一节。

```powershell
foreach ($g in 'providers','sessions','context','workspace','tool-policy','memory','tools','cross-session','images','routing','copilot','web-search') {
  dotnet run --project tests/AxmolHub.Checks -- "--check-ai-$g"
}
```

每组只打 `PASS:` / `FAIL:` 行、不打汇总，退出码非 0 即有失败。2026-10-15 实测（统一 LF 那一轮之后）：providers 49 / copilot 67 / sessions 47 / context 22 / workspace 9 / tool-policy 19 / memory 5 / tools 31 / cross-session 41 / images 7 / routing 1 / web-search 11 = **309 条**。

**图片有四条入口，收到同一套准入里**：composer 的「+ → 添加图片…」、Ctrl+V 粘贴截图、把文件拖到输入框上，以及模型自己调 `capture_screen`。准入只看**文件头**（PNG / JPEG / GIF / WebP），扩展名不算数；单张上限 8 MiB、一条消息最多 4 张，**按大小先拒再读字节**，所以一次拖进一整文件夹的原图也不会先把窗口卡住。四条入口的图都落 `data-root/ai/sessions/{会话 id}/`（**不进工作区**，所以不会被文件工具当项目文件读到），并在消息边界以 user 角色发出去 —— `tool` 结果在 OpenAI 协议里带不了图。

**文本那一条通道有第二个入口：单个文件**（「+ → 添加本地文件…」，或把文件拖到输入框上）。同一个手势先问图片准入、再问文本通道：`.png` 仍是图片，`.svg` 这种**没有图片文件头**的矢量文档则作为**标记文本**随那一轮进请求 —— 视觉接口只收位图，所以 SVG 只有这一条路能到模型；chip 上的矢量封面由 `Svg.Controls.Avalonia`（命名空间 `Avalonia.Svg`，`SvgImage` 是 `IImage`）按控件尺寸现画，不生成、不落盘、也不上线的任何栅格图。文本通道的规则是**扩展名白名单**（与 `read_file`/`edit_file` 同一张表，`.svg` 已加入）加**单文件 1 MiB 上限**，同样只 stat 不读；超限或非文本各有一句点明规则的话。附件发出后 chip 清空 —— 它变成了提示词里的 `Attached file: {名字}` 块，会话里只留下那段文本。抓屏在 Windows 上是 GDI `PrintWindow`，黑帧不入库也不发送；macOS / Linux 尚无抓取后端，`capture_screen` 会明确拒答而不是给一张假图。**派生子会话**（`spawn_session`）默认关，需在「设置 → 工具权限」卡片里勾上「允许助手派生子会话」才可用。

**计划的正文不进会话文件**：plan 模式答完的那一刻，那份 markdown 落到 `data-root/ai/plans/<guid>.md` —— 扁平、所有会话共用一个目录 —— 而会话 JSON 里那一轮只留**文件名**，外加当时冻下来的标题、字数与 token 价：计划卡与上下文圆环因此一个字节都不读盘。批准时那句 `Implement the following plan…` 的用户轮引用**同一份**文件，正文在请求边界由 `ChatWorkspace.MaterializePlans` 拼回去，所以发给模型的字节与搬走之前逐字相同，而一份计划不会在同一个 JSON 里出现两遍。压缩请求走同一个拼装 —— 不接它的那一半，摘要会总结一条空的 assistant 轮，计划之后就从每份会话摘要里无声消失。目录按 64 份 / 8 MiB 自我淘汰（与 `ai/undo` 同一口径），删会话**不**删计划文件，因为派生会话与源会话指的是同一份文档，而 `data/` 是文件工具的受保护根、模型自己也打不开它 —— 跨会话读计划只有 `read_session` 这一道门。**正文不在时处处照实说**：右栏点名是哪一个文件（不是退回「还没有生成计划」），批准它以 `ChatPlanBodyGone` 拒绝而不是发一句没有计划的指令，历史里的计划轮再上线时换成一句「文件已不在」的说明，绝不把空消息递给模型。写盘失败、没有数据根、或正文长过 20 万字时，那一轮仍把正文留在 `Text` 里 —— 这与升级前的老会话文件是**同一种形状**，所以没有迁移代码，也没有第二条读取路径（`PlanStore.BodyOf` 是唯一的规则）。（`--verify-shell` 的计划组把「JSON 里只剩文件名 + `ai/plans` 里那份字节 + 线上仍是全文」三条一起钉住；存储本身的往返、拒绝畸形名、淘汰上界与老文件加载在 `--check-ai-sessions`，`EstimateTokens(盖章轮) == EstimateTokens(正文拼回去的副本)` 在 `--check-ai-context`。）

**`web_search` 的三层来源**，按顺序取第一个可用的：① DeepSeek 预设的官方 Search/Fetch API；② 服务商托管搜索（见下）；③ Hub 本地搜索。总开关是设置里的「允许联网搜索」，三层共用。第 ③ 层由 `LocalWebSearch`（Core）实现：自动使用 DuckDuckGo HTML 页面（`html.duckduckgo.com/html/`），无需选择搜索源、填写地址或 API Key，也不提供独立的本地搜索设置。结果与 DeepSeek 共用同一份输出格式（`LocalWebSearch.Format`），传输复用 `web_fetch` 的 `WebBridge`（超时、字节上限、`https`），不跟随重定向。DuckDuckGo 是尽力而为：验证码页、非 200 和解析不出结果都会明确说成「搜索失败」，不会被当作「没有结果」，并提示模型不要立刻重试。本地搜索不记入 DeepSeek 用量。Claude 的原生 `web_search_20250305` 需要 Anthropic 原生协议，Hub 目前只有 chat 和 responses 两种协议，因此尚未支持；经 OpenAI 兼容网关使用 Claude 时，是否能搜取决于网关，仍需在 `serverTools` 里显式声明。

**服务端自己跑的搜索（`web_search`）**不是 Hub 的函数工具：请求只声明能力，没有 Hub 的沙箱或审批卡，是否搜索由服务商在生成过程中决定。通常由 `ai/providers.json`（或内置清单）的 `serverTools` 声明 `web_search`；例外是 OpenAI 官方 ChatGPT/Codex OAuth，它固定走 Responses API，因此 Hub 会在总开关开启时自动提供其原生搜索。OpenAI API Key 和兼容网关仍需显式声明，不能仅凭服务商名称推断能力。`--check-ai-web-search` 检查两条线的序列化字段（chat 顶层 `web_search_options`、Responses `tools[]` 中的 `{"type":"web_search"}`），并验证 OAuth、API Key、总开关和来源记录逻辑，不访问真实模型服务。该能力共用「允许联网搜索」开关：关闭时不发送搜索声明；开启后服务端可自行决定搜索时机，Hub 无法逐次审批。搜索记录保存在回复轮次（`ChatTurn.WebSearch`）但不回放，因为历史消息中的搜索记录无法由当前两种桥接安全重放。回复下方来源 chips 的布局由 `--verify-shell` 检查：来源过多时折叠到 `+N`，tooltip 仍保留完整地址。

**助手页的交互也有断言**，因为这一类 bug 在编译期完全静默：空状态的四条建议 chip 存在、点一条只把话填进输入框（**不替人按下发送**）；拖文件到输入框上会亮起 accent 环、拖开或落下都收回，非文件的拖拽不亮；消息与草稿里的缩略图点得开，预览是**面板内的覆盖层而不是 Popup**（Popup 有自己的顶层，`--smoke-pages` 与像素判据都看不见它），打开与关闭都用帧里该图独有的两个恒定通道计数来证明真的画上了；Esc 先收预览、再停正在写的回复，空闲时按 Esc 什么都不做；↑ 在本轮窗口已发送的话里往回走（被引导吞掉的草稿也算），框里有字时让位给光标；发送后键盘回到输入框。滚动一侧：长回复写完停在最后一条、提示行活过一次整段重画（比对行实例，"还在"与"从没被清掉"是两件事）、切会话落在最新一条而不是继承上一处的读数。视觉一侧：纯文本与 markdown 正文同字号（只有最近 40 条走 markdown，否则消息会随滚动换字号），关闭叉号与代码块都用 `Hub.Font.Ui` / `Hub.Font.Mono` 那一份栈，浅色变体下第一次比较"实际会贴在一起"的表面配对（气泡 vs 页面、输入框 vs 页面、胶囊 vs 框底）。

发布自包含图形版（RID 换成 `osx-arm64` / `osx-x64` / `linux-x64` 即可交叉发布，但只有 Windows 那一条实测过）：

```powershell
dotnet publish src/AxmolHub/AxmolHub.csproj -c Release -r win-x64 --self-contained true -o artifacts/app
```

> **带 RID 的手工发布会把 `src/AxmolHub/packages.lock.json` 收成两个节点**（`net8.0` 与刚才那一个 RID），因为带 RID 的还原在重写锁文件时只写它自己那一档。重写只发生在锁文件被判定过期时，所以「什么都没升」的普通发布不会动这个文件 —— 一旦看见它的 diff，就说明这一次真的重写过。`installer/Build.ps1` 已经在 publish 之后自带一次无 RID 的 `dotnet restore` 把 5 节点形状写回来；手工跑上面这条命令就要自己补一次，提交前确认 `git status` 里锁文件没有输出。形状、策略与理由记在根目录 `Directory.Build.props`。

> **Linux 另需一个中文字体。** 最小化安装的 Ubuntu 不带任何 CJK 字体，中文界面会整片显示成方框 ——
> 界面语言默认是英文就是为了这个。Hub 会在启动时与切到中文时自行探测（判据是渲染器能不能匹配到
> 汉字字形，不是"装没装某个包"），探测不到就提示 `sudo apt install fonts-noto-cjk`。
> 任何 CJK 字体都行（思源黑体、文泉驿……），装完重启 Hub 即可。

**Linux 上的密钥存储用本机加密文件**（AES-256-GCM，数据密钥在 `~/.config/AxmolHub/ai-secret.key`，权限 600，故意放在 data root 之外）；设置页的提供商卡片会如实显示当前后端，不会含糊地说"系统凭据存储"。freedesktop 密钥环档尚未实现，见 [ADR-0003 §6](adr/0003-linux-secret-store.md)。浏览器登录按 `$BROWSER → xdg-open → gio open` 的顺序启动；都启动不了时，除了显示链接还会**让你粘贴回调地址**（WSL2 / 容器里浏览器在宿主机，回调打不到 Hub 的回环端口，这是唯一的出路）。macOS 仍无后端，会直接说明。

不用打开 GUI 也能验证这台机器到底存得了存不了：

```bash
dotnet src/AxmolHub/bin/Release/net8.0/AxmolHub.dll --check-secrets
```

无头（在 Avalonia 启动之前就返回），只写临时目录，末行打印 `backend=` 与档名。三平台 CI 矩阵都跑这一条。密钥存储的契约断言另有 `dotnet run --project tests/AxmolHub.Checks -- artifacts/checks --check-secret-store`（2026-10-07 实测 52 条 PASS，只能在 Windows 上跑，原因见 `docs/ci.md` §2.7）。

**Linux 真跑记录（2026-10-08 更新）**：`linux-x64` **框架依赖**产物在 WSL2 Ubuntu-24.04（.NET 8.0.31）打出 `backend=encryptedfile` 全绿；同日在原生 Ubuntu（.NET 10.0.112 + `squashfs-tools`）补上了此前"仍未实测"的那半条 —— `installer/Build.ps1 -Runtime linux-x64` 产出 `linux-x64` 的 AppImage（自包含，约 50 MB；名字规则见 `installer/README.md`）与 full nupkg，`--appimage-extract` 解开后确认 `usr/bin/AxmolHub`、`.DirIcon`（512 PNG）、`usr/bin/Assets/hub-icon-{256,512}.png` 都在位；GUI 在真实 X11 会话里起得来，`xprop` 读到 `WM_CLASS = "…, \"axmol-hub\"` 与一份 `_NET_WM_ICON`。**未实测**的仍是签名/公证之外的东西：跨改名的增量更新（见下）与 macOS 通道。

**改名带来的两个一次性代价**（`AxmolHub.App` → `AxmolHub`）：Windows 会把它当成**新的通知发送者**，用户对该应用已设的通知开关与历史归零一次（after-install 钩子会重新给 `Axmol Hub.lnk` 打 AppUserModelID 戳，投递本身不断）；而包内多数路径同时改名，**首个跨改名的更新包几乎没有 delta**，那一次用户下载的是接近全量的包。随后本次安装身份从 `Axmol.Hub` 切换为 `dev.axmol.hubapp`，会被 Velopack 视为另一款应用而非旧版升级；旧安装应先卸载，但 `%LocalAppData%\AxmolHub\` 用户数据保留。用户可见的启动路径为安装根目录下的 `Axmol Hub.exe` 与桌面/开始菜单的 `Axmol Hub.lnk`（见 `installer/README.md`）。

Windows 的 Velopack 安装身份为 `dev.axmol.hubapp`；安装目录为 `%LocalAppData%\dev.axmol.hubapp\`，安装根启动器为 `Axmol Hub.exe`，桌面与开始菜单快捷方式保留空格为 `Axmol Hub.lnk`（安装器 exe 的 ProductName / FileDescription 也是 `Axmol Hub`），本地下载的 Setup 文件则叫 `AxmolHub.exe`（名称不同，但在安装目录之外）。应用设置与数据继续存于 `%LocalAppData%\AxmolHub\`。Windows 不单独保存可复用的 secret key 文件：provider 凭据密文位于数据根的 `ai\secrets`，由当前用户 DPAPI 保护。

Linux 的桌面身份（窗口类 / `.desktop` 文件名 / 图标名同为 `axmol-hub`）与图标铺设由 App 自己在每次启动时完成，因此**不用打开 GUI 也能验证这台机器上图标到底铺不铺得下去**：

```bash
dotnet src/AxmolHub/bin/Release/net8.0/AxmolHub.dll --check-linux-integration ./tmp/hub-integration
```

无头（同样在 Avalonia 启动之前返回），`XDG_DATA_HOME` 被指进 scratch 目录、绝不写真实的 `~/.local/share`；断言桌面入口可解析、`Icon=`/`StartupWMClass=` 与窗口类同名、两档 hicolor PNG 的 IHDR 尺寸与桶名相符、重复注册不重写文件，退出码即失败条数。要看真效果：正常启动一次（非 `--smoke` 等自动化模式，它们刻意跳过注册），然后在 GNOME 里搜 `Axmol`。

macOS 的通知通道不用打开 GUI 也能验证一半：

```bash
dotnet src/AxmolHub/bin/Release/net8.0/AxmolHub.dll --check-macos-notifications
```

无头（Avalonia 启动前返回），不请求授权、不画通知；断言 global block 的 invoke 偏移、delegate 类与两个方法编码、`willPresent` 交回的 24、以及通知标识符的接受/拒绝表。CI 的 macOS job 跑这一条：裸 dll 没有 bundle 身份，会打印 `SKIP`，属预期；开发包（`dotnet run`）会打印 `bundle=org.axmol.hub.development bundled=yes`，并真的把 delegate 设到 `UNUserNotificationCenter` 上。

**边界**：`.AppImage` **文件本身**在文件管理器里的图标不在这里解决 —— vpk 不用 `appimagetool`，只把 `.DirIcon` 压进镜像，纯净的 GNOME/Nautilus 不读镜像内部，需要 appimaged / Gearlever / AppImageLauncher 这类 DE 集成守护进程。运行窗口与"显示应用"的图标是本次修的部分。

构建 Windows 安装包并进行隔离安装检查：

```powershell
dotnet run --project tests/AxmolHub.Checks -- cache/packaging-tools --prepare-packaging
./installer/Build.ps1
./installer/Test.ps1 -Isolated
```

安装包由 [Velopack](https://velopack.io) 生成，输出到 `artifacts/releases/win-x64/`：`AxmolHub.exe`、免安装的 `dev.axmol.hubapp-...-Portable.zip`、自更新载荷 `.nupkg` 与更新索引。安装器是一键式的，等级为当前用户，无需管理员。安装检查会临时打包并登记自己的程序身份与快捷方式，验证安装、自包含启动、中文目录、跨版本升级保留数据及卸载保留数据，结束后卸载测试实例、保留现有 Hub。打包工具说明见 [installer/README.md](../installer/README.md)。

发布其他宿主的自包含 CLI：

```powershell
./installer/Build-Hosts.ps1
```
