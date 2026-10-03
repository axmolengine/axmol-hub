# 安装包

安装包由 [Velopack](https://velopack.io) 的 `vpk` 打包器生成：Windows 出一键安装器 + `Portable.zip`，macOS 出 `.pkg`（另有便携 zip），Linux 只出 `.AppImage`。一套脚本、三种产物，并且自带自动更新与增量包。Windows 安装器在打包后被改名为 `axmol-hub-<version>-<runtime>.exe`，见下。

## 前置：准备固定版本的打包器

`vpk` 是 .NET 全局工具，但这里不装到 PATH、也不做全局安装，而是按 `installer/packaging-manifest.json` 锁定的版本与 SHA-256 下载后装进工作区：

```powershell
dotnet run --project tests/AxmolHub.Checks -- artifacts/packaging-tools --prepare-packaging
```

该命令先校验 `.nupkg` 的 SHA-256，再从已校验的本地源安装，产出 `artifacts/packaging-tools/vpk/`。

## 构建与验收

```powershell
./installer/Build.ps1
./installer/Test.ps1 -Isolated
```

输出到 `artifacts/releases/win-x64/`：

| 文件 | 用途 |
| --- | --- |
| `axmol-hub-<version>-<runtime>.exe` | 一键安装器（**自定义名**），含 SHA-256 |
| `Axmol.Hub-win-Portable.zip` | 免安装版，含 SHA-256。**当前不上传 release 页** |
| `Axmol.Hub-<version>-full.nupkg` / `-delta.nupkg` | 自更新载荷 |
| `releases.win.json` | 更新索引，自动更新的唯一入口 |
| `assets.win.json` / `RELEASES` | vpk 内部用（`vpk upload` 的清单 / Squirrel 迁移），不上传 |

**文件名的两套规则**（都是 `vpk` 派生的，只有第一个被改掉了）：

- 安装包：`Build.ps1` 把 `vpk` 产出的 `{packId}-{channel}-Setup.exe` 改名为 `axmol-hub-<version>-<runtime>.exe`。原生名里既没有版本也没有架构，在 release 页上每次发布都重名，只能靠标题分辨。
- 更新载荷：`{packId}-<version>-full.nupkg` / `-delta.nupkg`，**不能改** —— `releases.win.json` 里硬引用了这个文件名。安装包改名与更新链路无关，两者互不影响。

版本号只有**一个来源**：仓库根的 `Directory.Build.props`（五个项目共用）。`Build.ps1` 读它，不再有散落的手工副本。

`Build.ps1` 按 RID 参数化（`-Runtime win-x64|linux-x64|osx-arm64|osx-x64`），脚本本身不区分平台。**当前只有 Windows 能出包**。P6 之后 App 已经是跨平台实现（Avalonia / `net8.0`），守卫的保留理由也随之变了：不再是"项目是 Windows 独占"，而是 **osx / linux 的发行通道（签名、公证、载荷格式）尚未跑通**。Core、Cli 与 App 都已经能为每个宿主构建。

`Test.ps1 -Isolated` 用一次性 `packId`、程序名与安装目录打包两个相邻版本（`x` 与 `x+1`），验证：

1. 静默安装到含中文与空格的路径
2. 安装载荷完整（自包含 Hub、清单、许可文件都在 `current\` 下）
3. 装好的自包含版能无头启动
4. **跨版本升级后用户设置与数据仍在**
5. 卸载移除应用载荷、开始菜单快捷方式与卸载注册项，**但保留用户数据**

结果写在 `artifacts/install-checks/<本次>/result.json`。不带 `-Isolated` 时要求机器上没有既有的 Hub 安装。

## 用户数据放在安装目录之外

这不是风格问题，是 Velopack 的硬约束：**更新会整体替换安装目录下的 `current\`，卸载会删除整个安装目录**。所以设置与数据根都在每用户目录下：

| 内容 | 位置 |
| --- | --- |
| 安装目录（Velopack 管理） | `%LocalAppData%\Axmol.Hub\` |
| 设置与数据根 | `%LocalAppData%\AxmolHub\` |

引擎与工具链是 GB 级的，因此放 `LocalApplicationData` 而不是漫游 `AppData`。`--data-root` 与 `--preferences` 仍可覆盖，验收测试正是靠它们把数据指到工作区内。

`packId`（`Axmol.Hub`，带点）与用户数据目录（`AxmolHub`，不带点）刻意不同名，避免看着像同一个目录。

## 与原 Inno 链路的差异（有意接受）

- **没有向导。** Velopack 的 Windows 安装器是一键式的：不提供安装目录选择页，也不提供安装语言选择。目录可用 `Setup.exe --installto <DIR>` 覆盖；安装语言不再存在，界面语言由应用内设置决定，冷启动默认 `en-US`（可切到 `zh-CN`；默认值单点定义在 `HubTexts.DefaultLanguage`）。
- **桌面快捷方式固定为不创建。** Inno 里它是个默认不勾选的选项，而一键安装没有界面承载这个选项，因此打包时固定 `--shortcuts StartMenuRoot`。
- **没有 MSVC/工具链相关行为变化**：安装包仍然只含自包含 Hub、清单与许可文件，不捆绑引擎、工具链、Debug CRT 或用户设置。

## 发布到 GitHub Release

`Publish.ps1` 走 `download → pack → upload` 三步，分两个 `-Stage`，CI 可以把安装验收插在中间：

```powershell
./installer/Publish.ps1 -Stage Build    # 拉上一版 + 打包（产出 delta）
./installer/Publish.ps1 -Stage Upload   # 裁剪 feed + gh release create/upload + 校验
```

不带 `-Stage` 时两步连着跑。`Upload` 阶段需要 `GH_TOKEN`（CI 里就是 `GITHUB_TOKEN`）和 `gh`。

上传清单是**显式列出**的，只有四样：安装包、`安装包.sha256`、本版 `-full.nupkg`（有 `-delta.nupkg` 时也带上）、`releases.win.json`。用 `gh release upload` 而不是 `vpk upload github`，是因为后者按 `assets.win.json` 枚举文件，而那里记的是 `vpk` 原生名 —— 安装包一改名就对不上了。

**`releases.win.json` 必须先裁剪再上传。** `GithubSource` 会把最近 10 个 release 各自的 `releases.win.json` 合并，然后到**该 feed 所属的 release** 里按文件名找 nupkg。`vpk download` 会把上一版的条目带进新 feed，但上一版的 nupkg 在旧 release 里 —— 不裁剪，更新检查会直接抛错。裁剪规则是只留 `Version == 当前版本` 的条目；旧版本仍可通过它自己那个 release 的 feed 被发现。

**tag 必须与 `Directory.Build.props` 的版本一致**（`v0.2.1` ↔ `0.2.1`），`Publish.ps1` 会拦。feed、nupkg 名、安装包名都带版本号，而 release 页是按 tag 组织的，两者漂移会静默地让更新对不上。

上传后会立刻回读 release 的资产清单，确认每一件都在 —— 这是唯一能证明「更新源真的可用」的检查。

**保留策略尚未实现。** `gh release upload` 只控制这一次传什么，历史 release 的资产不会自己消失。每版约 190 MB（安装包 94 + full 87 + delta），仓库存储软限制 1 GB，够放 4–5 版；之后需要一个「删旧 release 资产」的步骤。

## 增量包

`Publish.ps1 -Stage Build` 先用 `vpk download github` 把上一版取回输出目录，再以 `-NoClean` 调 `Build.ps1`，`vpk` 因此能生成 `-delta.nupkg`。首个发布没有可比对象，只有 full —— 这不算失败，脚本会警告后继续。

## 代码签名

尚未接入，先跑通机制流程。

- **Windows**：计划走 **SignPath Foundation** 的免费 OSS 代码签名（Authenticode），见 [研发计划 §4 D5](../docs/hub-development-plan.md)。未签名会触发 SmartScreen 警告。
- **macOS**：SignPath **不支持** —— 官方签名格式表里没有 `.app` / `.pkg` / `.dmg`，Gatekeeper 只认 Apple 签发的 Developer ID + 公证。macOS 通道接入时只有两条路：Apple Developer Program，或在下载页给出 `xattr -r -d com.apple.quarantine` 的指引。
- **Linux**：SignPath 支持 `.rpm` / `.deb`（GPG 签名），但 AppImage 场景没有等价的「未知发布者」拦截，自己生成 GPG 密钥即可。

`Build-Hosts.ps1` 发布各宿主的自包含 CLI；`Build-Icon.ps1` 从源 PNG 生成 16–256 像素多尺寸 ICO。修改图标后先重新生成 ICO，再构建应用。
