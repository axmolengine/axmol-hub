# 安装包

安装包由 [Velopack](https://velopack.io) 的 `vpk` 打包器生成：Windows 出 `Setup.exe` + `Portable.zip`，macOS 出 `.pkg`，Linux 出 `.AppImage`。一套脚本、三种产物，并且自带自动更新与增量包。

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
| `Axmol.Hub-win-Setup.exe` | 一键安装器，含 SHA-256 |
| `Axmol.Hub-win-Portable.zip` | 免安装版，含 SHA-256 |
| `Axmol.Hub-<version>-full.nupkg` | 自更新载荷 |
| `releases.win.json` / `assets.win.json` / `RELEASES` | 更新索引，静态文件即可作为更新源 |

版本号只有**一个来源**：`src/AxmolHub.App/AxmolHub.App.csproj` 的 `<Version>`。`Build.ps1` 读它，不再有散落的手工副本。

`Build.ps1` 按 RID 参数化（`-Runtime win-x64|linux-x64|osx-arm64|osx-x64`），脚本本身不区分平台。**当前只有 Windows 能出包**：`AxmolHub.App` 还是 `net8.0-windows` + WPF，`Build.ps1` 里对此有一条显式守卫，随 Avalonia 迁移一并删除。Core 与 Cli 已经能为每个宿主构建。

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

- **没有向导。** Velopack 的 Windows 安装器是一键式的：不提供安装目录选择页，也不提供安装语言选择。目录可用 `Setup.exe --installto <DIR>` 覆盖；安装语言不再存在，界面语言由应用内设置决定，默认 `zh-CN`。
- **桌面快捷方式固定为不创建。** Inno 里它是个默认不勾选的选项，而一键安装没有界面承载这个选项，因此打包时固定 `--shortcuts StartMenuRoot`。
- **没有 MSVC/工具链相关行为变化**：安装包仍然只含自包含 Hub、清单与许可文件，不捆绑引擎、工具链、Debug CRT 或用户设置。

## 增量包

`vpk` 会在 `--outputDir` 里存在上一版本时生成 `-delta.nupkg`。`Build.ps1` 每次清空发布目录，因此当前只在同一目录保留多版本时才会产出增量；发布流水线要用上它，需要把上一版产物恢复到发布目录。

## 代码签名

尚未接入。计划走 **SignPath Foundation** 的免费 OSS 代码签名（Windows Authenticode），见 [研发计划 §4 D5](../docs/hub-development-plan.md)。未签名的 Windows 安装器会触发 SmartScreen 警告。

`Build-Hosts.ps1` 发布各宿主的自包含 CLI；`Build-Icon.ps1` 从源 PNG 生成 16–256 像素多尺寸 ICO。修改图标后先重新生成 ICO，再构建应用。
