# 从源码构建、验收开关与检查

本文是 README 中「从源码运行」「CLI」「检查与打包」的详细版。

## 从源码运行界面

界面在 `src/AxmolHub.App/`（`net8.0`，目标三平台）。开发机需要装 **.NET 10 SDK**（最稳妥：`net8.0` 目标框架下，Avalonia 12.1.3 的分析器/源生成器是按编译器 4.14 编译的，而 SDK 8 的旧补丁——例如 Ubuntu 上 `apt install dotnet-sdk-8.0` 拿到的 8.0.1xx——只带编译器 4.8，源生成器整个不产出，报一排 `CS0103: The name 'InitializeComponent' does not exist`。SDK 8 只有够新的补丁、编译器 ≥ 4.14 才够，.NET 10 一定够）。**SDK 版本要求就是 10.0.x**，构建命令各平台一致，差别只在怎么装 SDK。

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
sudo apt-get update && sudo apt-get install -y dotnet-sdk-10.0

# Ubuntu 22.04 —— .NET 10 只来自 Canonical 的 backports PPA
sudo add-apt-repository ppa:dotnet/backports
sudo apt-get update && sudo apt-get install -y dotnet-sdk-10.0
```

不要再按老教程去加 `packages.microsoft.com`：微软**已不再**通过它为 Ubuntu 提供包（且该源只有 x64），和 Ubuntu 源混用正是 .NET 包冲突（package mix-up）报错的来源。arm64 主机请用 Ubuntu 源或上面的安装脚本。

然后在本仓库根目录执行：

```powershell
dotnet build src/AxmolHub.App/AxmolHub.App.csproj -c Release
dotnet run --project src/AxmolHub.App -- --data-root ./data
```

**四个页面（项目 / 引擎 / 工具链 / 设置）与四个对话框窗口**：左侧 218px 导航、三行内容区（页面 / 状态栏 / 日志面板）、统计卡、表格、设备条、操作按钮行的位置与文案都与原 WPF 版对齐；与原版只有三处刻意差异，原因写在 [avalonia-migration-plan.md](avalonia-migration-plan.md) §5.7。业务逻辑全在 `AxmolHub.Core`（界面项目只负责装配），编排在 `Services/HubWorkspace.cs`。

**切换数据根已可用**：只重建工作区并丢弃页面缓存，**窗口本身不动** —— 因此它不依赖外壳形态。新根落盘失败时会在旧状态上就地报错，不会留下"界面指向新根、设置文件还写着旧根"的错位。

默认数据根是每用户的 `%LocalAppData%\AxmolHub\data`，设置保存在 `%LocalAppData%\AxmolHub\hub-settings.json` —— 不能放在程序目录旁边，因为 Velopack 更新时整体替换安装目录、卸载时删除整个安装目录。可以指定独立配置：

```powershell
dotnet run --project src/AxmolHub.App -- --data-root ./data --preferences ./artifacts/dev-preferences.json
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
# 外壳、本地化与数据根切换的自检（706 条断言）
dotnet run --project src/AxmolHub.App -- --data-root ./data --verify-shell ./tmp/shell-check.txt
# 主题层（46 条）/ 基础件（26 条）
dotnet run --project src/AxmolHub.App -- --data-root ./data --verify-theme ./tmp/theme.txt
dotnet run --project src/AxmolHub.App -- --data-root ./data --verify-foundation ./tmp/foundation.txt
# 无头截图：主窗口一张
dotnet run --project src/AxmolHub.App -- --data-root ./data --smoke ./tmp/smoke.png
# 无头截图：四个页面 × 中英两种语言，共 8 张（README 里的页面图由此生成）
dotnet run --project src/AxmolHub.App -- --data-root ./data --smoke-pages ./tmp/pages
# 真操作验收：在真实引擎源码树上跑引擎管理链路（后面跟若干个引擎目录）
dotnet run --project src/AxmolHub.App -- --data-root ./data --verify-ops ./tmp/ops-check.txt <引擎目录> [更多引擎目录...]
```

上面的 `--data-root ./data` **不要省**：省了就用每用户的真实数据根，自检会读到你自己的引擎、项目与凭据（原因见上一节）。括号里的断言数**只是那一刻的实测值**——`--verify-shell` 的总数按场景扇出，改前改后都要自己跑一遍拿数，别拿算术去预测它。

`--verify-ops` 与另外三个的分工：它们验**界面**（跑在夹具上），它验**操作**（用真实引擎树）。它只跑**不下载、不编译**的那部分 —— 工具链探测、引擎导入/校验/设为默认/移除、状态落盘重载、无效输入拒绝；装引擎、装工具链、构建、运行、Android 打包会拉 GB 级数据或依赖完整工具链，因此**显式记为跳过**并附原因（报告里的 `SKIP`）。不传引擎目录时依赖引擎的那组会自动降级为跳过，所以在一台没有引擎的机器上它也能正常退出而不是失败。

> 临时产物统一写仓库的 `tmp/`（`cache/` 留给缓存类产物），两者都已在 `.gitignore` 里。
> 自检自己生成的夹具与截图也落在那里 —— 见 `Services/ScratchDirectory.cs`。

具体各读什么：`--verify-shell` / `--verify-theme` / `--verify-foundation` 读可视化树与渲染帧，`--verify-ops` 读**操作真的执行之后**的状态与磁盘内容。`--verify-shell` 还会扫描 `**/*.axaml` 里的每个 `{DynamicResource X}`，确认它真的能解析 —— 少一个 key 同样不报错，只显示空白。

两个截图开关各带一道**防假绿**的判据，因为"进程退出 0"是躺着也能过的：`--smoke` 要求那帧不是纯色（`SmokeCapture.FrameStats.IsBlank`）；`--smoke-pages` 在那之上再数一遍**不同内容**的张数 —— 八张全出来但只有 2 张不同，说明导航静默失效；只剩 4 张，说明切语言静默失效。两种都能让"非空白"全绿。另外它会真切语言并落盘，所以结束时**切回原语言**；跑之前把 `--preferences` 指向临时文件即可不碰你的真实设置。

界面文案是**单一定义**的：中英两份都在 `AxmolHub.Core/HubTexts.cs`，界面项目只留一层"把文案灌进 Avalonia 资源字典"的薄适配。设置页因此同时是本地化管线的**活体验收台** —— 切换语言要就地重灌资源字典并让**已经存在的控件**换文字，这件事读代码判断不了，只能实跑（见上面的 `--verify-shell`）。

`--verify-shell` 有三条值得单独说的断言：

AI 助手的外壳检查还覆盖了计划审批卡、批准后以 Agent 模式继续、修改/拒绝、后台审批未读状态、通知过滤与会话深链激活。自检会禁用真实系统通知和任务栏/Dock 标记；Windows Toast、macOS 通知中心及 Linux 通知守护进程的显示与点击行为仍须在对应桌面环境实测。

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

### AI 助手的十组检查

助手层**不需要引擎、不需要网络、不需要 API key**：它经 `ChatWorkspace.ClientOverride` 注入一个脚本化的 `IChatClient`，跑的是真的 Core/Agent 代码。所以这十组在任何机器上都能单独跑，也是不需要工具链的那部分 AI 验收；界面那一半由上面的 `--verify-shell` 负责（同一套代码的活对象）。

```powershell
foreach ($g in 'providers','sessions','context','workspace','tool-policy','memory','tools','cross-session','images','routing') {
  dotnet run --project tests/AxmolHub.Checks -- "--check-ai-$g"
}
```

每组只打 `PASS:` / `FAIL:` 行、不打汇总，退出码非 0 即有失败。2026-10-07 实测：33 / 27 / 4 / 6 / 5 / 2 / 9 / 40 / 7 / 1 = **134 条**。

**图片有四条入口，收到同一套准入里**：composer 的「+ → 添加图片…」、Ctrl+V 粘贴截图、把文件拖到输入框上，以及模型自己调 `capture_screen`。准入只看**文件头**（PNG / JPEG / GIF / WebP），扩展名不算数；单张上限 8 MiB、一条消息最多 4 张，**按大小先拒再读字节**，所以一次拖进一整文件夹的原图也不会先把窗口卡住。四条入口的图都落 `data-root/ai/sessions/{会话 id}/`（**不进工作区**，所以不会被文件工具当项目文件读到），并在消息边界以 user 角色发出去 —— `tool` 结果在 OpenAI 协议里带不了图。抓屏在 Windows 上是 GDI `PrintWindow`，黑帧不入库也不发送；macOS / Linux 尚无抓取后端，`capture_screen` 会明确拒答而不是给一张假图。**派生子会话**（`spawn_session`）默认关，需在「设置 → 工具权限」卡片里勾上「允许助手派生子会话」才可用。

**助手页的交互也有断言**，因为这一类 bug 在编译期完全静默：空状态的四条建议 chip 存在、点一条只把话填进输入框（**不替人按下发送**）；拖文件到输入框上会亮起 accent 环、拖开或落下都收回，非文件的拖拽不亮；消息与草稿里的缩略图点得开，预览是**面板内的覆盖层而不是 Popup**（Popup 有自己的顶层，`--smoke-pages` 与像素判据都看不见它），打开与关闭都用帧里该图独有的两个恒定通道计数来证明真的画上了；Esc 先收预览、再停正在写的回复，空闲时按 Esc 什么都不做；↑ 在本轮窗口已发送的话里往回走（被引导吞掉的草稿也算），框里有字时让位给光标；发送后键盘回到输入框。滚动一侧：长回复写完停在最后一条、提示行活过一次整段重画（比对行实例，"还在"与"从没被清掉"是两件事）、切会话落在最新一条而不是继承上一处的读数。视觉一侧：纯文本与 markdown 正文同字号（只有最近 40 条走 markdown，否则消息会随滚动换字号），关闭叉号与代码块都用 `Hub.Font.Ui` / `Hub.Font.Mono` 那一份栈，浅色变体下第一次比较"实际会贴在一起"的表面配对（气泡 vs 页面、输入框 vs 页面、胶囊 vs 框底）。

发布自包含图形版（RID 换成 `osx-arm64` / `osx-x64` / `linux-x64` 即可交叉发布，但只有 Windows 那一条实测过）：

```powershell
dotnet publish src/AxmolHub.App/AxmolHub.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/app
```

> **Linux 另需一个中文字体。** 最小化安装的 Ubuntu 不带任何 CJK 字体，中文界面会整片显示成方框 ——
> 界面语言默认是英文就是为了这个。Hub 会在启动时与切到中文时自行探测（判据是渲染器能不能匹配到
> 汉字字形，不是"装没装某个包"），探测不到就提示 `sudo apt install fonts-noto-cjk`。
> 任何 CJK 字体都行（思源黑体、文泉驿……），装完重启 Hub 即可。

**Linux 上的密钥存储用本机加密文件**（AES-256-GCM，数据密钥在 `~/.config/AxmolHub/ai-secret.key`，权限 600，故意放在 data root 之外）；设置页的提供商卡片会如实显示当前后端，不会含糊地说"系统凭据存储"。freedesktop 密钥环档尚未实现，见 [ADR-0003 §6](adr/0003-linux-secret-store.md)。浏览器登录按 `$BROWSER → xdg-open → gio open` 的顺序启动；都启动不了时，除了显示链接还会**让你粘贴回调地址**（WSL2 / 容器里浏览器在宿主机，回调打不到 Hub 的回环端口，这是唯一的出路）。macOS 仍无后端，会直接说明。

不用打开 GUI 也能验证这台机器到底存得了存不了：

```bash
dotnet src/AxmolHub.App/bin/Release/net8.0/AxmolHub.App.dll --check-secrets
```

无头（在 Avalonia 启动之前就返回），只写临时目录，末行打印 `backend=` 与档名。三平台 CI 矩阵都跑这一条。密钥存储的契约断言另有 `dotnet run --project tests/AxmolHub.Checks -- artifacts/checks --check-secret-store`（2026-10-07 实测 52 条 PASS，只能在 Windows 上跑，原因见 `docs/ci.md` §2.7）。

同一台机器上的 Linux 真跑已经做过：`linux-x64` **框架依赖**产物在 WSL2 Ubuntu-24.04（.NET 8.0.31）打出 `backend=encryptedfile` 全绿；上面那条 `dotnet publish` 的**自包含**产物与 AppImage 仍未实测。

构建 Windows 安装包并进行隔离安装检查：

```powershell
dotnet run --project tests/AxmolHub.Checks -- artifacts/packaging-tools --prepare-packaging
./installer/Build.ps1
./installer/Test.ps1 -Isolated
```

安装包由 [Velopack](https://velopack.io) 生成，输出到 `artifacts/releases/win-x64/`：`Axmol.Hub-win-Setup.exe`、免安装的 `-Portable.zip`、自更新载荷 `.nupkg` 与更新索引。安装器是一键式的，等级为当前用户，无需管理员。安装检查会临时打包并登记自己的程序身份与快捷方式，验证安装、自包含启动、中文目录、跨版本升级保留数据及卸载保留数据，结束后卸载测试实例、保留现有 Hub。打包工具说明见 [installer/README.md](../installer/README.md)。

发布其他宿主的自包含 CLI：

```powershell
./installer/Build-Hosts.ps1
```
