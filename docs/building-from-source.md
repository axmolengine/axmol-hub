# 从源码构建、验收开关与检查

本文是 README 中「从源码运行」「CLI」「检查与打包」的详细版。

## 从源码运行界面

界面在 `src/AxmolHub/`（`net8.0`，目标三平台）。开发机需要装 **.NET 10 SDK**（最稳妥：`net8.0` 目标框架下，Avalonia 12.1.3 的分析器/源生成器是按编译器 4.14 编译的，而 SDK 8 的旧补丁——例如 Ubuntu 上 `apt install dotnet-sdk-8.0` 拿到的 8.0.1xx——只带编译器 4.8，源生成器整个不产出，报一排 `CS0103: The name 'InitializeComponent' does not exist`。SDK 8 只有够新的补丁、编译器 ≥ 4.14 才够，.NET 10 一定够）。**SDK 版本要求就是 10.0.x**，构建命令各平台一致，差别只在怎么装 SDK。

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
# Ubuntu 24.04 / 25.04 / 25.10 —— .NET 已在 Ubuntu 自己的源里，不用加任何第三方源
sudo apt-get update && sudo apt-get install -y dotnet-sdk-10.0 dotnet-runtime-8.0

# Ubuntu 22.04 —— .NET 10 只来自 Canonical 的 backports PPA
sudo add-apt-repository ppa:dotnet/backports
sudo apt-get update && sudo apt-get install -y dotnet-sdk-10.0 dotnet-runtime-8.0
```

不要再按老教程去加 `packages.microsoft.com`：微软**已不再**通过它为 Ubuntu 提供包（且该源只有 x64），和 Ubuntu 源混用正是 .NET 包冲突（package mix-up）报错的来源。arm64 主机请用 Ubuntu 源或上面的安装脚本。

然后在本仓库根目录执行：

```powershell
dotnet build src/AxmolHub/AxmolHub.csproj -c Release
dotnet run --project src/AxmolHub -- --data-root ./data
```

**四个页面（项目 / 引擎 / 工具链 / 设置）与四个对话框窗口**：左侧 218px 导航、三行内容区（页面 / 状态栏 / 日志面板）、统计卡、表格、设备条、操作按钮行的位置与文案都与原 WPF 版对齐；与原版只有三处刻意差异，原因写在 [avalonia-migration-plan.md](avalonia-migration-plan.md) §5.7。业务逻辑全在 `AxmolHub.Core`（界面项目只负责装配），编排在 `Services/HubWorkspace.cs`。

**切换数据根已可用**：只重建工作区并丢弃页面缓存，**窗口本身不动** —— 因此它不依赖外壳形态。新根落盘失败时会在旧状态上就地报错，不会留下"界面指向新根、设置文件还写着旧根"的错位。

默认数据根是每用户的 `%LocalAppData%\AxmolHub\data`，设置保存在 `%LocalAppData%\AxmolHub\hub-settings.json` —— 不能放在程序目录旁边，因为 Velopack 更新时整体替换安装目录、卸载时删除整个安装目录。可以指定独立配置：

```powershell
dotnet run --project src/AxmolHub -- --data-root ./data --preferences ./artifacts/dev-preferences.json
```

`--data-root` 优先于保存的设置；`--preferences` 可用于隔离开发或检查配置。不要将设置、数据目录或下载的工具链提交到 Git。

## 命令行

```powershell
dotnet run --project src/AxmolHub.Cli -- <动词> [参数]
```

只构建 Hub 时用本机 .NET SDK；它**不会**被当作游戏构建工具链。游戏所需工具由引擎自己的 `setup.ps1` 准备。

## 运行期验收开关

界面项目自带几个开关。它们读的是**运行期真实对象**，不是"看代码对不对" —— 原因见 [avalonia-migration-plan.md](avalonia-migration-plan.md) §3.1：Avalonia 的样式与模板写错**不会报错**，只会静默退化。

```powershell
# 外壳、本地化与数据根切换的自检（923 条断言）
dotnet run --project src/AxmolHub -- --data-root ./data --verify-shell ./tmp/shell-check.txt
# 主题层（46 条）/ 基础件（26 条）
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

上面的 `--data-root ./data` **不要省**：省了就用每用户的真实数据根，自检会读到你自己的引擎、项目与凭据（原因见上一节）。括号里的断言数**只是那一刻的实测值**——`--verify-shell` 的总数按场景扇出，改前改后都要自己跑一遍拿数，别拿算术去预测它。

`--verify-ops` 与另外三个的分工：它们验**界面**（跑在夹具上），它验**操作**（用真实引擎树）。它只跑**不下载、不编译**的那部分 —— 工具链探测、引擎导入/校验/设为默认/移除、状态落盘重载、无效输入拒绝；装引擎、装工具链、构建、运行、Android 打包会拉 GB 级数据或依赖完整工具链，因此**显式记为跳过**并附原因（报告里的 `SKIP`）。不传引擎目录时依赖引擎的那组会自动降级为跳过，所以在一台没有引擎的机器上它也能正常退出而不是失败。

> 临时产物统一写仓库的 `tmp/`（`cache/` 留给缓存类产物），两者都已在 `.gitignore` 里。
> 自检自己生成的夹具与截图也落在那里 —— 见 `Services/ScratchDirectory.cs`。

具体各读什么：`--verify-shell` / `--verify-theme` / `--verify-foundation` 读可视化树与渲染帧，`--verify-ops` 读**操作真的执行之后**的状态与磁盘内容。`--verify-shell` 还会扫描 `**/*.axaml` 里的每个 `{DynamicResource X}`，确认它真的能解析 —— 少一个 key 同样不报错，只显示空白。

两个截图开关各带一道**防假绿**的判据，因为"进程退出 0"是躺着也能过的：`--smoke` 要求那帧不是纯色（`SmokeCapture.FrameStats.IsBlank`）；`--smoke-pages` 在那之上再数一遍**不同内容**的张数 —— 八张全出来但只有 2 张不同，说明导航静默失效；只剩 4 张，说明切语言静默失效。两种都能让"非空白"全绿。另外它会真切语言并落盘，所以结束时**切回原语言**；跑之前把 `--preferences` 指向临时文件即可不碰你的真实设置。

界面文案是**单一定义**的：中英两份都在 `AxmolHub.Core/HubTexts.cs`，界面项目只留一层"把文案灌进 Avalonia 资源字典"的薄适配。设置页因此同时是本地化管线的**活体验收台** —— 切换语言要就地重灌资源字典并让**已经存在的控件**换文字，这件事读代码判断不了，只能实跑（见上面的 `--verify-shell`）。

`--verify-shell` 有三条值得单独说的断言：

AI 助手的外壳检查还覆盖了计划审批卡、批准后以 Agent 模式继续、修改/拒绝、待审批计数、通知过滤（含窗口失焦与最小化）与会话深链激活。任务栏/Dock 徽标统计所有至少有一项未解决审批的会话（包括当前打开的会话），同一会话的多项审批只计一次；决定完成后计数随之更新，任务完成本身不计入。「用户看得见这条会话」要三件事同时成立：窗口在前台（既未失焦也未最小化）、停在 Assistant 页、且该页正显示这条会话——三项同时成立时，审批卡已经在眼前，因此不重复弹系统通知；最小化或 Alt-Tab 走开后，即使走开的正是当前会话，也会提醒。Windows 任务栏 overlay 和 macOS Dock 支持数字徽标；Linux 桌面环境没有统一的任务栏数字徽标接口，所以只使用该桌面实际提供的通知能力。自检会禁用真实系统通知和原生任务栏/Dock 徽标；Windows Toast、macOS 通知中心及 Linux 通知守护进程的显示与点击行为仍须在对应桌面环境实测。

**Windows 通知与任务栏标记手动诊断**：使用单独的数据目录启动应用，可立即请求一个示例 Toast 和 30 秒任务栏标记，不会创建或修改真实会话。开发版：

```powershell
dotnet run --project src\AxmolHub -- --data-root .\tmp\attention-test-data --preferences .\tmp\attention-test-preferences.json --test-system-attention
```

安装版可用同一参数启动安装目录中的稳定启动器（将路径替换为本机实际位置）：

```powershell
& "$env:LOCALAPPDATA\dev.axmol.hubapp\Axmol Hub.exe" --data-root .\tmp\attention-test-data --preferences .\tmp\attention-test-preferences.json --test-system-attention
```

应用日志位于 `.\tmp\attention-test-data\logs\`，筛选 `[System attention]` 可检查开始菜单快捷方式的 AppUserModelID、通知子进程结果与任务栏 overlay 的 HRESULT。成功特征：数字徽标是 `The diagnostic numeric taskbar badge was requested successfully.`，Windows toast 是 `Started Windows toast helper …` 紧跟 `Windows toast helper completed … Toast.Show completed.`。失败特征同样直白：`Could not update the taskbar badge: CoCreateInstance(CLSID_TaskbarList, IID_ITaskbarList3) returned 0x80004002`，那个 `0x80004002`(E_NOINTERFACE) 就是 0.8.x 整段时间红点不亮的原因——它不是 Windows 的行为，而是代码里的 `IID_ITaskbarList3` 字面量末段抄错（`…9E9F8A5EEA84`，真值 `…9E9F8A5EEFAF`），这个值在 `explorerframe.dll` 里一次都没有、`HKCR\Interface` 里也没有；`--verify-shell` 现在把两个标识符连同四个易混近邻一起钉住。Windows 设置中还需允许 Axmol Hub 通知，并关闭勿扰/专注助手后重测。Toast 需要开始菜单中的 `Axmol Hub.lnk` 带有匹配的 AppUserModelID（同机的 Electron 客户端只靠这一条就够，不需要再写 `HKCU\Software\Classes\AppUserModelId`）；诊断会报告快捷方式缺失或写入失败。开发版只有在已有匹配快捷方式（通常由安装版创建）时才能完成 Toast 展示验证；任务栏徽标诊断不依赖安装版。若生产日志出现 `No system notification for approval ...`，表示会话仍正在前台显示审批卡，按策略不会重复弹系统通知；若出现 Toast 成功而桌面未显示，则检查 Windows 通知权限和勿扰/专注模式；若没有 `Started Windows toast helper`，则先查审批事件与通知闸门。自检模式不会弹真实通知，`--test-system-attention` 才是桌面手测入口。**但它证明的是投递，不是触发**：诊断直接调 `Show()`，绕过 `ShouldNotifyApproval`/`ShouldNotifyRun` 闸门，所以它全绿也说明不了"最小化后会不会提醒"——那一半要按上一节的三条件手动走一遍：停在当前会话、起一个 run、把窗口最小化或 Alt-Tab 走开（不要切页、不要换会话）。

- **真切一次语言再切回来**，然后去读**早于切换就已建好**的控件上的文字（外壳导航项、设置页说明、引擎页表头）。它同时证明"已存在的控件跟着换文字"和"切回中文也生效"，并把设置文件落在临时目录里 —— 自检**不会**碰到你 `%LocalAppData%\AxmolHub\` 下的真实设置。去掉 `HubStrings.Apply` 那一行，构建照样 0 error，自检报 6 条 FAIL、退出 1。
- **切换一次数据根再切回来**：换根、落盘、旧页面被丢弃、引擎列表跟着变空、拒绝盘符根、重复切换是无操作。漏掉"丢弃旧页面"的后果是"界面看着正常，一点按钮就在读一个已经不属于当前会话的工作区"——去掉 `_pages.Clear()` 一行，构建照样 0 error，自检报 3 条 FAIL、退出 1。
- **逐页真实渲染**并逐个核对帧非空白（PNG 落在 `tmp/shell-render/`）。断言看得懂"有没有内容"，看不懂"有没有压在一起"；逐页截图补上了这一点 —— 本轮就是靠肉眼核对截图发现"显示 A 页、左侧高亮 B 页"，随后补了对应断言。

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

每次复验使用新的输出目录，避免已有测试工具影响"组件缺失"检查。行为检查现共 **139 条断言**：主流程 **105 条**（覆盖进程参数、取消、下载校验、路径边界、metadata、配置隔离、资源发布及 Android 打包/设备参数等）+ CLI `--json` 契约 **34 条**；不替代真实设备或干净宿主验收。

其中 CLI 契约那 34 条**不依赖引擎与工具链**，因此已接进 CI（[ci.md](ci.md) §2.6），可单独跑：

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

每组只打 `PASS:` / `FAIL:` 行、不打汇总，退出码非 0 即有失败。2026-10-10 实测（出网第一期 + 托管搜索第二期 2a 之后）：33 / 36 / 20 / 6 / 7 / 5 / 19 / 41 / 7 / 1 / 55 / 11 = **241 条**。

**图片有四条入口，收到同一套准入里**：composer 的「+ → 添加图片…」、Ctrl+V 粘贴截图、把文件拖到输入框上，以及模型自己调 `capture_screen`。准入只看**文件头**（PNG / JPEG / GIF / WebP），扩展名不算数；单张上限 8 MiB、一条消息最多 4 张，**按大小先拒再读字节**，所以一次拖进一整文件夹的原图也不会先把窗口卡住。四条入口的图都落 `data-root/ai/sessions/{会话 id}/`（**不进工作区**，所以不会被文件工具当项目文件读到），并在消息边界以 user 角色发出去 —— `tool` 结果在 OpenAI 协议里带不了图。抓屏在 Windows 上是 GDI `PrintWindow`，黑帧不入库也不发送；macOS / Linux 尚无抓取后端，`capture_screen` 会明确拒答而不是给一张假图。**派生子会话**（`spawn_session`）默认关，需在「设置 → 工具权限」卡片里勾上「允许助手派生子会话」才可用。

**服务端自己跑的搜索（`web_search`）出厂时谁都没声明**：它不是 Hub 的工具 —— 没有句体、没有沙箱、没有审批卡，请求只是递一句「你可以自己搜」，什么时候真搜由服务商在生成中途决定。唯一的入口是 `ai/providers.json`（或内置清单）里那份 `serverTools`，写 `["web_search"]` 就是声明、删掉就没有，翻它不需要重新构建。`--check-ai-web-search` 能证的是**声明落在哪条线的哪个字段**（chat 线写顶层 `web_search_options`，responses 线写 `tools[]` 里一条 `{"type":"web_search"}`；两条都在 127.0.0.1 上把桥序列化出去的字节读回来比，且 needle 由 Core 的预测推导），证不了**某个网关会不会真去搜** —— 那只有一次真人登录加一次真请求能定，所以出厂全空并由断言钉住。它共用「允许助手抓取网页」这一个开关：关掉它连声明都不发，开着它则**没有任何审批卡拦得住一次服务端搜索**。搜到的东西记在回复那一轮上（`ChatTurn.WebSearch`）**只记不回放**，因为实测把带搜索项的历史喂回两条桥，一条把那条 assistant 消息序列化成空 `content`（正文一起没了），另一条干脆把它从 `input` 里丢掉，两者都不报错。回复下面那行来源 chips 也归 `--verify-shell` 管：整行只花 64 字的额度、每条胶囊 34 字封顶，放不下的折进 `+N` 且 tooltip 里按原顺序一条不丢 —— 判据是 inspector 开着时聊天列只剩 560px，四条长主机画出去就是 charter 说的「溢出而不是裁切」。

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
