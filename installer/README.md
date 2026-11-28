# 安装包

安装包由 [Velopack](https://velopack.io) 的 `vpk` 打包器生成：Windows 出一键安装器 + `Portable.zip`，macOS 出 `.pkg`（另有便携 zip），Linux 只出 `.AppImage`。一套脚本、三种产物，并且自带自动更新与增量包。本地 Windows 打包默认输出 `AxmolHub.exe`；发布流水线使用带版本和 RID 的文件名，见下。

## 前置：准备固定版本的打包器

`vpk` 是 .NET 全局工具，但这里不装到 PATH、也不做全局安装，而是按 `installer/packaging-manifest.json` 锁定的版本与 SHA-256 下载后装进工作区：

```powershell
dotnet run --project tests/AxmolHub.Checks -- cache/packaging-tools --prepare-packaging
```

该命令先校验 `.nupkg` 的 SHA-256，再从已校验的本地源安装，产出 `cache/packaging-tools/vpk/`。

## 构建与验收

```powershell
./installer/Build.ps1
./installer/Test.ps1 -Isolated
```

输出到 `artifacts/releases/win-x64/`：

| 文件 | 用途 |
| --- | --- |
| `AxmolHub.exe` | 本地打包的一键安装器（**自定义名**），含 SHA-256；CI 发布时改为 `axmol-hub-<version>-<runtime>.exe` |
| `dev.axmol.hubapp-<runtime>-Portable.zip` | 免安装版，含 SHA-256。**当前不上传 release 页** |
| `dev.axmol.hubapp-<version>-<runtime>-full.nupkg` / `-delta.nupkg` | 自更新载荷（打包器产出名；上传时改名为 `axmol-hub-…`，见下） |
| `releases.<runtime>.json` | 更新索引，自动更新的唯一入口 |
| `assets.<channel>.json` / `RELEASES-<channel>` | vpk 内部用（`vpk upload` 的清单 / Squirrel 迁移），不上传 |

**文件名的两套规则**（都由 `vpk` 派生）：

- 安装包：名字规则只写在 `installer/AssetNames.ps1` 一处 —— `Build.ps1` 用它改名，`Publish.ps1` 与 `Publish-All.ps1` 按同一个名字找回文件，规则各拼一遍就是"改了一处、发布 job 在另一处红"。Windows 本地 `Build.ps1` 默认把 `vpk` 产出的 `{packId}{-channel}-Setup.exe` 改名为 `AxmolHub.exe`，发布时改为 `axmol-hub-<version>-<runtime>.exe`；macOS 一律 `axmol-hub-<version>-<runtime>.pkg`。**Linux 不带版本段**，恒为 `axmol-hub-linux-x64.AppImage`：AppImage 不是装完即弃的安装器，而是用户留下并反复运行的那个文件，名字一变每次发布都得重发链接、书签和脚本。版本仍可从标题栏（`Axmol Hub v0.8.3`）、桌面入口的 `X-AppImage-Version` 与随包 `.sha256` 三处任一确认。
- 更新载荷：`vpk` 产出 `{packId}-<version>{-channel}-{full|delta}.nupkg`，`Publish-All.ps1` 上传时把它改名为与既有 release 命名一致的 `axmol-hub-<version>-<runtime>-{full|delta}.nupkg`，**并同步改写 feed 里的 `FileName`** —— 否则客户端按 feed 找不到包。公开资产前缀由 `assetPrefix` 固定，因此修改安装身份不会改变安装包或更新载荷的既有名称。
- Windows 包内主程序固定为 `current\AxmolHub.exe`（与程序集同名；2026-10-08 之前叫 `AxmolHub.App.exe`，旧名只留在 MuiCache 的清理名单里）；Velopack 稳定启动器为 `Axmol Hub.exe`，位于安装根目录 `%LocalAppData%\dev.axmol.hubapp\`，桌面与开始菜单快捷方式均为 `Axmol Hub.lnk`。这三样都由 vpk 按 `--packTitle`（`Axmol Hub`）原生生成 —— 安装器 exe 的 ProductName / FileDescription 同样取自 `--packTitle` —— 安装后不再改名。用户下载的 Setup 本地名为 `AxmolHub.exe`，位于安装目录之外。

其中 `{-channel}` 段：`vpk` **只在 Windows 且 channel 恰为平台默认值 `win` 时省略**，其余一律带 `-<channel>`（macOS 出 `-osx-…`、Linux 出 `-linux-…`）。本项目 channel = **完整 RID**（`win-x64` / `osx-arm64` / `osx-x64` / `linux-x64`，见 `Build.ps1`），因 `win-x64 ≠ win`，**四个平台（含 Windows）的 nupkg 都带 `-<rid>-` 段**。feed 名同理 = `releases.<rid>.json`。

版本号只有**一个来源**：仓库根的 `Directory.Build.props`（五个项目共用）。`Build.ps1` 读它，不再有散落的手工副本。

`Build.ps1` 按 RID 参数化（`-Runtime win-x64|linux-x64|osx-arm64|osx-x64`），脚本本身不区分平台，**没有平台守卫**：四个平台都在各自原生 runner 上出包（Windows `Setup.exe` / macOS `.pkg` / Linux `.AppImage`）。P6 之后 App 是跨平台实现（Avalonia / `net8.0`），Core、Cli 与 App 都能为每个宿主构建；**签名 / 公证尚未接入**，因此产物目前都是未签名状态。

## Linux Wayland helper

Linux 原生 runner 构建前安装 `gcc pkg-config libglib2.0-dev libpipewire-0.3-dev libspa-0.2-dev`。
`dotnet build/publish` 自动构建并携带 `axmolhub-portal-helper`，仅动态链接系统 GIO/PipeWire；
不打包 PipeWire、SPA 插件或 Portal 服务。Windows/macOS 输出不带 helper；Linux 发布要求原生 Linux runner。
运行时需要桌面提供 RemoteDesktop、ScreenCast WINDOW 源及 PipeWire，缺少能力时明确拒绝，不回退到全局注入。
详细授权边界和待验收项见 `docs/computer-use.md`。

## 官网安装链接

Hub 注册 `axmolhub:` 自定义 URI Scheme。官网可用以下链接请求安装清单中**精确匹配**的 Axmol 版本：

```text
axmolhub://install?version=2.11.5&source=atomgit
```

`version` 必须是 Hub 当前引擎目录中可安装的版本；`source` 必须是 `github`、`atomgit` 或 `custom`。`custom` 使用用户已在 Hub 设置中配置的 HTTPS 下载源，官网链接不能传入下载 URL。来源仅对此次安装生效，不会覆盖 Hub 的默认下载源。安装仍使用引擎清单里的文件摘要校验；未配置的自定义源、无效链接或不可用版本会明确报错，不会静默改装其他版本。

Windows 安装时为当前用户注册协议，应用启动时刷新，卸载时移除；每次安装或更新后也会尝试从当前用户的 MuiCache 中清理当前 Axmol Hub 安装目录对应的缓存值，不影响其他路径，且不需要管理员权限。Linux 在 Hub 首次启动时写入当前用户的 desktop handler；macOS 安装包将 `axmolhub` 写入 Hub app bundle 的 `CFBundleURLTypes`。官网应同时保留各平台 Hub 安装包的回退链接：用户尚未安装 Hub、浏览器拒绝唤起或唤起超时时，提示其安装/打开 Hub 后重试。网站需把版本和来源作为 URL 参数编码，且不要在链接中加入自定义镜像地址。

`Test.ps1 -Isolated` 用一次性 `packId`、程序名与安装目录打包两个相邻版本（`x` 与 `x+1`），验证：

1. 静默安装到含中文与空格的路径
2. 安装载荷完整（自包含 Hub、清单、许可文件都在 `current\` 下）
3. 装好的自包含版能无头启动
4. **跨版本升级后用户设置与数据仍在**
5. 卸载移除应用载荷、开始菜单快捷方式、卸载注册项与 `axmolhub:` 协议注册，**但保留用户数据**

结果写在 `artifacts/install-checks/<本次>/result.json`。不带 `-Isolated` 时要求机器上没有既有的 Hub 安装。

## 用户数据放在安装目录之外

这不是风格问题，是 Velopack 的硬约束：**更新会整体替换安装目录下的 `current\`，卸载会删除整个安装目录**。所以设置与数据根都在每用户目录下：

| 内容 | 位置 |
| --- | --- |
| 安装目录（Velopack 管理） | `%LocalAppData%\dev.axmol.hubapp\` |
| 设置、引擎/项目登记、提供商与鉴权文件 | `%LocalAppData%\AxmolHub\` |
| 新安装默认业务数据根 | `%USERPROFILE%\.axmolhub\`（旧安装保留原业务数据位置；鉴权独立迁移） |

引擎与工具链是 GB 级的，因此放 `LocalApplicationData` 而不是漫游 `AppData`。`--data-root` 与 `--preferences` 仍可覆盖，验收测试正是靠它们把数据指到工作区内。

`packId`（`dev.axmol.hubapp`）是安装与更新身份，必须跨版本保持不变，Velopack 才会将后续安装识别为现有应用的更新；公开下载资产前缀则由独立的 `assetPrefix`（`axmol-hub`）控制。设置与鉴权文件目录仍为 `%LocalAppData%\AxmolHub\`；新安装的业务数据默认在 `%USERPROFILE%\.axmolhub\`，旧安装的业务数据不迁移，鉴权一次性复制到固定用户目录，原文件不删除。两者均独立于 Velopack 会整体替换或删除的安装目录。

这次将已发布应用身份从 `Axmol.Hub` 切换为 `dev.axmol.hubapp`，Velopack 会把它视为另一个应用，旧安装不会被原位升级；安装前建议先从 Windows“已安装的应用”中卸载旧版。卸载旧版不会删除 `%LocalAppData%\AxmolHub\` 用户数据。此后必须保持 `dev.axmol.hubapp` 不变；`assetPrefix=axmol-hub` 继续保证公开下载资产名不变。

## 与原 Inno 链路的差异（有意接受）

- **没有向导。** Velopack 的 Windows 安装器是一键式的：不提供安装目录选择页，也不提供安装语言选择。目录可用 `Setup.exe --installto <DIR>` 覆盖；安装语言不再存在，界面语言由应用内设置决定，冷启动默认 `en-US`（可切到 `zh-CN`；默认值单点定义在 `HubTexts.DefaultLanguage`）。
- **桌面与开始菜单快捷方式都创建。** 一键安装没有界面承载“是否建桌面快捷方式”这个选项，因此打包时固定 `--shortcuts Desktop,StartMenuRoot`，两处各建一个 `Axmol Hub.lnk`（名带空格，由 `--packTitle` 决定）。
- **没有 MSVC/工具链相关行为变化**：安装包仍然只含自包含 Hub、清单与许可文件，不捆绑引擎、工具链、Debug CRT 或用户设置。

## 发布到 GitHub Release

发布分两个 workflow，语义与 axslcc 的 `build` + `dist` 一致：

1. **`build.yml`** —— `on: push`（master）+ `pull_request` + `workflow_dispatch`。内部两类 job：
   - `verify`：三平台矩阵，验证代码正确性（编译、宿主判定、CLI JSON 契约、下载契约），**任何提交都跑**。
   - `package-*`：各 RID 在原生 runner 上 `Publish.ps1 -Stage Build`（拉上一版 → 打包出 delta → 改名 + sha256），`upload-artifact` 上产物。**只在提交标题为 `ver: x.y.z`、`ver: x.y.z-beta`、`ver: x.y.z (Preview)` 或手动触发时跑**，普通提交跳过打包。工作流按同一规则将预发布构建标记编入安装包。Windows 还多跑一步 `Test.ps1 -Isolated` 安装验收。
2. **`dist.yml`** —— `on: workflow_run`（监听 build 完成）+ `workflow_dispatch`。以完整 Git 历史检出本次构建提交，解析上述提交标题决定是否发版；`0.y.z`、带 `-beta` 等预发布后缀、或标题以 `(Preview)` 结尾的 release 会在 GitHub 标记为 Pre-release。`(Preview)` 仅为发布标记，不进入产品版本、tag 或产物名。从最近一个可达 tag 到本次提交生成英文逐提交发布日志（每条只取提交标题即信息首行，正文不进日志；**`ver:` 开头且只改 `Directory.Build.props` 的提交不进日志**，夹带其他文件时仍保留并警告；区间内全是纯版本提交时给出英文“无用户可见变更”说明，不回填版本提交），再下载三平台产物，用 `Publish-All.ps1` 合并上传到一个 release。tag 明确创建在本次构建 SHA 上；重跑已有 release 时会刷新同一份发布日志及 Pre-release 状态。

日常发版：只把版本号修改写进 `Directory.Build.props`，提交标题写 `ver: x.y.z`（如 `ver: 0.19.0`），push 到 master。预览版可使用 `ver: x.y.z (Preview)`；`0.y.z` 或 `-beta` 版本也自动作为预发布版。没有上述版本标记的普通提交只验证、不打包、不发布。旧的 `Version ...`、`misc: Version ...` 和 `release: ...` 格式不再触发发版。在启用 `WINDOWS_SIGNED_RELEASES=true` 前，`Dist` 仍自动发布；启用后改由下方的 Windows 签名流程发布。

Hub 的默认提交摘要前缀为 `feat:, fix:, docs:, misc:, ver:`；原先的默认四项配置会自动升级，已保存的自定义前缀保持不变。在工作区菜单中打开「Git 更改」，先暂存要提交的内容，再输入标题并提交。手动输入的标题不受前缀限制，只要求单行、不超过 120 字符。提交不会修改 `Directory.Build.props` 或自动推送；需要发布到当前分支的上游时，选择「推送」或「提交并推送」。

安装版在设置页的「软件更新」中可选择 Stable（默认）或 Preview；Preview 会纳入 GitHub Pre-release。预发布构建始终使用 Preview 更新流，并在左侧 `Axmol Hub` 标题旁显示主题自适应的黄色 `Preview` 标记。

六个 RID 的产物装进**同一个 tag 的同一个 release**：

| 平台 | runner | 安装器 | 主程序参数 |
| --- | --- | --- | --- |
| Windows x64 / arm64 | `windows-latest` / `windows-11-arm`，签名切换后为本地 Windows 签名机 | `axmol-hub-<v>-win-{x64,arm64}.exe` | `--mainExe` |
| macOS arm64 / x64 | `macos-15`（按 RID 发布） | `axmol-hub-<v>-osx-{arm64,x64}.pkg` | `--mainExe` |
| Linux x64 / arm64 | `ubuntu-24.04` / `ubuntu-24.04-arm` | `axmol-hub-linux-{x64,arm64}.AppImage`（**不带版本段**，理由见上） | `--mainExe` |

Linux 那一行有两个只有把 AppImage 解开才看得见的细节（`unsquashfs` 在这份文件上找不到 superblock，因为它是 runtime + squashfs 拼接；用 `./xxx.AppImage --appimage-extract`）。其一，`--icon` 给的那张 PNG 被 vpk 一次写成三份：AppDir 根的 `dev.axmol.hubapp.png`、`.DirIcon`、以及 `usr/share/icons/hicolor/scalable/apps/dev.axmol.hubapp.png` —— 所以 `--icon` 的尺寸直接决定这三处的观感，1254 原图在这里毫无收益。其二，生成的 `dev.axmol.hubapp.desktop` 里 `Exec=` 取 `--mainExe`，而 `Icon=` 与 `StartupWMClass=` 都取 **packId**（`dev.axmol.hubapp`），与窗口真实的 WM_CLASS（`axmol-hub`，见 `Services/LinuxDesktopIdentity`）不是一个字符串；Velopack 的运行时库在 Linux 上既不装菜单也不铺图标（`src/lib-csharp` 只有 locator，shell 链接那套是 Windows 专属），所以**桌面匹配不能指望 AppDir 里这份** —— 生效的是 App 每次启动自己写的 `~/.local/share/applications/axmol-hub.desktop`。副作用：若宿主装了 AppImageLauncher 一类集成工具，它会把 AppDir 那份也注册进去，"显示应用"里于是出现两个 Axmol Hub 图块，只有 `StartupWMClass` 对得上的那个会跟着窗口高亮。

单平台调试仍可用 `Publish.ps1`：

```powershell
./installer/Publish.ps1 -Stage Build -Runtime win-x64     # 拉上一版 + 打包（产出 delta）
# 签名切换前的单平台上传入口；不要用它发布正式签名版。
./installer/Publish.ps1 -Stage Upload -Runtime win-x64
```

多平台合并上传由 `Publish-All.ps1` 承担。旧 `Dist` / 单平台上传需要 `GH_TOKEN`（CI 里是 `GITHUB_TOKEN`）；本地签名流程可使用 `gh auth login` 的发布账号。

**`releases.<channel>.json` 必须先裁剪再上传。** `GithubSource` 会把最近 10 个 release 各自的 feed 合并，然后到**该 feed 所属的 release** 里按文件名找 nupkg。`vpk download` 会把上一版的条目带进新 feed，但上一版的 nupkg 在旧 release 里 —— 不裁剪，更新检查会直接抛错。裁剪规则是只留 `Version == 当前版本` 的条目。一个 tag 下每个平台一个 channel（= 完整 RID），feed 名 `releases.<rid>.json`（`releases.win-x64.json` / `releases.osx-arm64.json` / `releases.osx-x64.json` / `releases.linux-x64.json`）天然唯一、无需额外后缀；nupkg 名也带 `-<rid>-` 段并与 feed 引用保持同步。

**tag 必须与 `Directory.Build.props` 的版本一致**（`v0.2.1` ↔ `0.2.1`）。feed、nupkg 名、安装包名都带版本号，而 release 页是按 tag 组织的，两者漂移会静默地让更新对不上。

上传后会回读资产清单核对文件名、大小和上传状态。签名流程在 draft 阶段核对，通过后才公开；差分是否实际被客户端使用仍须按下节验收。

**保留策略尚未实现。** `gh release upload` 只控制这一次传什么，历史 release 的资产不会自己消失。每版三平台合计远超单平台 190 MB，仓库存储软限制 1 GB，很快会需要「删旧 release 资产」的步骤。

## 增量包

`Publish.ps1 -Stage Build` 从空输出目录开始（重试可用 `-OutputDir` 指定新目录）。预发布构建给 `vpk download github` 加 `--pre`，允许选择 Pre-release 基包；稳定版只看正式发布。基包由 `vpk` 对照 feed 校验摘要，脚本再检查包 ID、RID、channel 和版本。只有没有可用的上一版时才允许只生成 full；下载失败、摘要校验失败或已有基包却未生成 delta 都会停止打包。

macOS 验收：先确认新 release 有该 RID 的 full、delta，且 `releases.<rid>.json` 中两者均指向同一 release 的真实资产。从 `.pkg` 全新安装通常没有本地 full 基包，第一次应用内更新可能全量下载。先完成一次应用内升级，再从有基包的安装升级到下一版；在 Velopack 日志确认请求了 `-delta.nupkg`、没有回退到 full，并重启确认版本与用户数据。

## Windows USB Token 签名发布

**到货前无需改 GitHub 设置。** Windows 开发包仍默认不签名。到货后，在 Windows 交互会话安装厂商中间件和 Windows SDK SignTool，确认代码签名证书在 `Cert:\CurrentUser\My`。先用一次性测试 EXE 验证厂商给出的 RFC 3161 URL、PIN 提示与时间戳；PIN 只在厂商中间件窗口输入，不进入命令行、仓库或 CI。

```powershell
& $signTool sign /sha1 $thumbprint /fd SHA256 /tr $timestampUrl /td SHA256 $testExe
& $signTool verify /pa /all /tw $testExe
```

在不上传的情况下，从同一提交试打两个 RID：先按上文准备固定版 `vpk`，再对 `win-x64`、`win-arm64` 分别执行 `Build.ps1 -Runtime <RID> -SigningThumbprint $thumbprint -TimestampUrl $timestampUrl -OutputDir <test-dir>`。用 `Test-WindowsReleaseSignature.ps1 -SetupPath <exe> -PackagePath <full.nupkg> -Thumbprint $thumbprint -SignToolPath $signTool` 验证安装器和包内主程序、启动器、更新器。分别在对应架构的 Windows 上，从已经安装的无签名版本原位升级，检查应用能够启动且设置和数据未被覆盖。有效 Authenticode 签名不保证 SmartScreen 立即放行。

**实机验收完成后才切换**：确认没有旧 `Dist` 正在上传，在 GitHub Actions 仓库变量中设置 `WINDOWS_SIGNED_RELEASES=true`，然后再推下一条 `ver:` 提交。此后 GitHub runner 保留验证和 Windows x64 隔离安装检查，只有 macOS/Linux 的四个 RID 上传发布产物；`Dist` 不再公开发布。

在 Windows 签名机检出该 Build run 的完整 SHA，保持工作区干净，准备 `ORCAROUTER_APP_ID` 和 `GH_OAUTH_CLIENT_ID`（与 CI 一致）和有发布权限的 `gh` 登录。选择这个 SHA 对应的成功 Build run ID 后执行：

```powershell
./installer/Release-WindowsSigned.ps1 -BuildRunId $buildRunId -Commit $commitSha `
    -SigningThumbprint $thumbprint -TimestampUrl $timestampUrl -SignToolPath $signTool
```

脚本只从指定 run 下载四个非 Windows RID，直接在同一提交本机打包两个 Windows RID。Velopack 在写入更新包时签内部二进制，生成安装器后签外层；脚本最后写 `.sha256`。两个 Windows RID 任一验签或时间戳失败、六个 RID 任一缺件、tag/SHA 不匹配，都会阻止公开。已公开的 release 不覆盖；上传失败保留 draft。不要在流程运行期间手动编辑或公开同一个 draft。

## 其他平台代码签名
- **macOS**：Gatekeeper 只认 Apple 签发的 Developer ID + 公证，`.app` / `.pkg` / `.dmg` 没有免费 OSS 签名通路。macOS 通道接入时只有两条路：Apple Developer Program，或在下载页给出 `xattr -r -d com.apple.quarantine` 的指引。
- **macOS 本地开发**：`Sign-DevBuild.ps1` 用本机已有身份签开发产物，让钥串授权跨构建沿用（背景见 [building-from-source.md](../docs/building-from-source.md#macos-开发构建钥串提示与本地签名)）。
- **Linux**：`.rpm` / `.deb` 走 GPG 签名；AppImage 场景没有等价的「未知发布者」拦截，自己生成 GPG 密钥即可。

`Build-Hosts.ps1` 发布各宿主的自包含 CLI；`Build-Icon.ps1` 从源 PNG 一次生成 16–256 像素多尺寸 ICO **与 256 / 512 两档单尺寸 PNG**（Linux 用：512 交给 `vpk` 的 `--icon`，256 随载荷走、给窗口图标与图标主题）。修改图标后先重跑这个脚本再构建应用；脚本用 System.Drawing，**只能在 Windows 上跑**。
