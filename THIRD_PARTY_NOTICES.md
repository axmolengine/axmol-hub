# 第三方声明

本项目的 MIT 许可证只适用于 Axmol Hub 自有代码。此仓库不捆绑 Axmol 引擎源码、微软工具链、Android / Apple SDK 或下载后的开发工具；下载清单不更改这些组件的许可。

## 随仓库保留的文件

`licenses/Velopack.txt` 是 Velopack 的 MIT 许可原文（版权方 Caelan Sayler 与 Velopack Ltd.），`licenses/ANGLE.txt` 是 ANGLE 的 BSD-3-Clause 原文，均按上游包内文本原样保留：

- [Velopack 上游许可](https://github.com/velopack/velopack/blob/master/LICENSE)
- [ANGLE 上游仓库](https://chromium.googlesource.com/angle/angle/+/refs/heads/main/LICENSE)

安装包、便携包与更新包由 [Velopack](https://velopack.io) 的 `vpk` 打包器生成（MIT）。本项目的 MIT 不替代 Velopack 或其组件的条款。打包器按 `installer/packaging-manifest.json` 锁定版本与 SHA-256，装进工作区运行，不复用系统上已装的工具。

**为什么不是 Inno Setup**：Inno Setup 是 Windows 独占，且自 6.7 起要求商业使用购买许可。Velopack 同时提供 Windows / macOS / Linux 产物、自动更新与增量包，许可是 MIT，因此取代了原来的 Inno 链路。

## 图形界面：Avalonia 依赖闭包

Avalonia 版界面（`src/AxmolHub/`）引入 **31 个** NuGet 包（从 `project.assets.json` 数出的完整传递闭包，含 `Avalonia.Controls.DataGrid`；同项目的 `Velopack` 另算一节）。**`Avalonia` / `Avalonia.Desktop` / `Avalonia.Themes.Fluent` 三项及其绝大多数传递依赖是 MIT**（`Avalonia.*` 12.1.3、`SkiaSharp` 3.119.4、`HarfBuzzSharp` 8.3.1.3、`MicroCom.Runtime`、`Tmds.DBus.Protocol` 等，均按包内 nuspec 的 `MIT` 表达式核对）。

**例外一个，必须单独声明**：

| 组件 | 许可 | 处置 |
| --- | --- | --- |
| `Avalonia.Angle.Windows.Natives` 2.1.27548.20260419 | **BSD-3-Clause**（ANGLE 项目自带的变体，第三条点名 TransGaming / Google / 3DLabs） | 包内不带 SPDX 表达式，只有一份 `LICENSE` 文件；Windows 输出里的 `av_libglesv2.dll` 就是这个包的原生二进制，**AS IS 条款要求随二进制分发时附上声明文本**，因此把原文保留为 `licenses/ANGLE.txt` 并随各客户端输出目录分发 |

**仍未核实的一处（不要当成已解决）**：`SkiaSharp` 包内只有 MIT 文本，但它捆绑的 `libSkiaSharp` 内含 **Skia 本体，而 Skia 是 BSD-3-Clause**。包内没有 Skia 的声明文本，所以上游的声明义务落在 SkiaSharp 还是各下游项目上，需要单独核对后再决定是否补一份声明。**这一条是识别出来的待办，不是已完成的合规动作。**

## 下载与发布的组件

| 组件 | 来源与许可边界 |
| --- | --- |
| Axmol 2.11.5 | [上游源码](https://github.com/axmolengine/axmol/tree/v2.11.5)，固定下载地址见 manifests；引擎及其依赖保留下载包中的 LICENSE 和通知 |
| .NET / WPF | Microsoft .NET SDK 自包含发布；保留发布输出内 LICENSE / ThirdPartyNotices，参见 [runtime](https://github.com/dotnet/runtime) 与 [WPF](https://github.com/dotnet/wpf) |
| Avalonia / SkiaSharp / HarfBuzzSharp / ANGLE | 经 NuGet 引入，不随仓库保留源码；许可以 MIT 为主，唯一例外是 ANGLE 的 BSD-3-Clause —— 逐项见上节 |
| MSVC Build Tools、Windows SDK | 微软官方安装器 / NuGet 包；使用和再分发遵循组件条款，Debug CRT 不随 Hub 安装包分发 |
| Android NDK / SDK / OpenJDK / Gradle | 固定下载来源见 manifests；各下载包许可与 SDK 授权条件独立适用 |
| Emscripten / Node / Python / CMake / Ninja / Git / axslcc | 清单锁定版本及摘要，原许可与通知随工具保留，不以 Hub 的 MIT 重新许可 |
| Apple Xcode 与 SDK | 由对应宿主准备，遵循 Apple 条款，不随本仓库分发 |

下载地址、版本和摘要见 `manifests/` 与 `installer/packaging-manifest.json`。创建的游戏项目还包含引擎模板及自己的依赖，发布游戏时应保留相应许可和通知。

## 图标

`src/AxmolHub/Assets/hub-icon.png` 参照 Axmol 引擎 logo 的五棱台节点线框形状重设计（中心增加轮毂节点与辐条以示区分），由脚本生成；ICO 由同一原图按 `installer/Build-Icon.ps1` 转换，未复制其他 Hub 的品牌图标。
