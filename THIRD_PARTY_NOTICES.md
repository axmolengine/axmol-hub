# 第三方声明

本项目的 MIT 许可证只适用于 Axmol Hub 自有代码。此仓库不捆绑 Axmol 引擎源码、微软工具链、Android / Apple SDK 或下载后的开发工具；下载清单不更改这些组件的许可。

## 随仓库保留的文件

`licenses/Velopack.txt` 是 Velopack 的 MIT 许可原文（版权方 Caelan Sayler 与 Velopack Ltd.），原样保留：

- [上游许可](https://github.com/velopack/velopack/blob/master/LICENSE)

安装包、便携包与更新包由 [Velopack](https://velopack.io) 的 `vpk` 打包器生成（MIT）。本项目的 MIT 不替代 Velopack 或其组件的条款。打包器按 `installer/packaging-manifest.json` 锁定版本与 SHA-256，装进工作区运行，不复用系统上已装的工具。

**为什么不是 Inno Setup**：Inno Setup 是 Windows 独占，且自 6.7 起要求商业使用购买许可。Velopack 同时提供 Windows / macOS / Linux 产物、自动更新与增量包，许可是 MIT，因此取代了原来的 Inno 链路。

## 下载与发布的组件

| 组件 | 来源与许可边界 |
| --- | --- |
| Axmol 2.11.5 | [上游源码](https://github.com/axmolengine/axmol/tree/v2.11.5)，固定下载地址见 manifests；引擎及其依赖保留下载包中的 LICENSE 和通知 |
| .NET / WPF | Microsoft .NET SDK 自包含发布；保留发布输出内 LICENSE / ThirdPartyNotices，参见 [runtime](https://github.com/dotnet/runtime) 与 [WPF](https://github.com/dotnet/wpf) |
| MSVC Build Tools、Windows SDK | 微软官方安装器 / NuGet 包；使用和再分发遵循组件条款，Debug CRT 不随 Hub 安装包分发 |
| Android NDK / SDK / OpenJDK / Gradle | 固定下载来源见 manifests；各下载包许可与 SDK 授权条件独立适用 |
| Emscripten / Node / Python / CMake / Ninja / Git / axslcc | 清单锁定版本及摘要，原许可与通知随工具保留，不以 Hub 的 MIT 重新许可 |
| Apple Xcode 与 SDK | 由对应宿主准备，遵循 Apple 条款，不随本仓库分发 |

下载地址、版本和摘要见 `manifests/` 与 `installer/packaging-manifest.json`。创建的游戏项目还包含引擎模板及自己的依赖，发布游戏时应保留相应许可和通知。

## 图标

`src/AxmolHub.App/Assets/hub-icon.png` 由内置 imagegen 生成并修正，ICO 由同一原图转换，未复制其他 Hub 的品牌图标。
