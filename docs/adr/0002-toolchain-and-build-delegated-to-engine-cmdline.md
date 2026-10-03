# ADR-0002：工具链与构建职责交还 Axmol 引擎 cmdline

- **状态**：已接受（2026-10-03）
- **取代**：部分取代 [ADR-0001](0001-hub-tech-stack-and-positioning.md) 里「工具链自持」这条定位

> 本文档是**索引与排期**，不重复上述文档的论证。遇到分歧以 ADR-0001 为准。

---

## 1. 背景

ADR-0001 把 Hub 定位成「环境与构建服务」，其中一条是**工具链自持**：Hub 在自己的数据目录里
维护一套固定版本的工具（CMake / Ninja / axslcc / NuGet / MSVC / Windows SDK / Android SDK / JDK…），
自己下载、自己校验 SHA-256、自己拼构建环境（`INCLUDE` / `LIB` / `PATH` 代替 vcvars），
并且**绝不回退系统工具**。落地形态是 `ToolchainDetector` + `WindowsToolchainInstaller` +
`manifests/toolchain-manifest.json` 等四份清单，构建由 `ProjectService` / `PlatformBuildService`
直接调 `cmake` / `ninja` / `gradle` 完成。

这套实现是初版（引擎用户用 AI 开发）的产物，与 Axmol 引擎自身已有的机制**平行重复**，而且有一个
更根本的问题：**它与引擎版本没有绑定**。工具的版本真源其实一直在引擎树里 —— `1k/build.profiles`，
每个引擎版本自带一份：

| 键 | 2.11.6 | 3.0.0 |
|---|---|---|
| `ndk` | r23d | r27d |
| `gradle` | 9.2.1 | 9.8.0 |
| `agp` | 8.11.1 | 9.4.1 |
| `buildtools` | 35.0.0 | 36.0.0 |
| `target_sdk` | 36 | 37 |
| `vs` | 17.0+ | 17.9+ |
| `axslcc` | 1.14.0 | 3.99.2 |
| `min_sdk` | 17 | 23 |

而引擎侧的机制是完备的：`1k/1kiss.ps1` 负责「检测 → 缺失则安装」，`setup.ps1` 与 `axmol build`
两条路径最终都汇入它；**除 Visual Studio 与 Xcode 只检测（VS 缺失仅告警、Xcode 缺失抛错）外，
其余工具一律自动安装**。

## 2. 决策

1. **构建、运行、部署全部委派给引擎 cmdline**：Hub 调 `axmol build|run|deploy <args>`，
   调用规则是 `axmol <subcmd> args`。Hub 不再拼 CMake 参数、不再调 cmake/ninja/gradle。
2. **工具链安装委派给官方 `setup.ps1`**，落点是引擎树内的 `<engine>/tools/external`
   （即 `setup.ps1` 的 `-prefix`）。每个引擎版本各一套。
3. **接受 `setup.ps1` 的全局副作用**：它写用户级 `AX_ROOT`、把 `<engine>/tools/cmdline`
   插进用户 `PATH`、必要时把执行策略设为 `Bypass`（弹 UAC）。这与引擎官方流程完全一致。
4. **Hub 不再自持工具链**：`WindowsToolchainInstaller`、四份工具链包清单、`ToolchainDetector`
   整体退役。
5. **`1k/build.profiles` 是工具版本唯一的真源**。Hub 只读它，用于**显示期望版本**。
6. **工具链页只显示状态**：解析 `build.profiles` + 按 axmol 自身的安装规则探测
   `<engine>/tools/external`，给出「期望 vs 实装」。安装需要用户主动点「运行引擎 setup.ps1」。

## 3. 真源表

| 关注点 | 真源 |
|---|---|
| 工具版本 | `<engine>/1k/build.profiles` |
| 工具落点 | `<engine>/tools/external`（`setup.ps1` 的 `-prefix`） |
| 工具安装 | `<engine>/setup.ps1` → `1k/1kiss.ps1 -setupOnly -prefix …` |
| 构建/运行/部署 | `<engine>/tools/cmdline/axmol.ps1`（`axmol <subcmd> args`） |
| 引擎导入判据 | `StateStore.FindEngineCoreDirectory` / `StateStore.MissingEngineMarkers`（v2 `core/`、v3 `axmol/`） |
| 构建目录 | **引擎决定**，Hub 只发现：先读引擎生成的 `run.bat` 的 `BUILD_DIR`，再扫 `build*`（`EngineBuildLayout`） |
| 产物位置 | `<buildDir>/bin/<App>/<Config>/<App>[.exe]`（引擎 `run.bat` 的口径） |
| 预编译引擎库 | **引擎自己的 CMake 构建目录**（含 `CMakeCache.txt`），由项目 CMake 配置时以 `-DAX_PREBUILT_DIR=<相对引擎根>` 消费 |

## 3.0 预编译引擎库（仅 Windows 构建目标）

Axmol 的「预编译引擎」不是一份单独导出的 SDK，而是**直接复用引擎自己的 CMake 构建目录**：
`templates/common/cmake/modules/AXGameEngineSetup.cmake:22-30` 在
`WIN32 OR LINUX` 且 `${AX_ROOT}/${AX_PREBUILT_DIR}` 是目录时置 `_AX_USE_PREBUILT=TRUE`，
于是**跳过 `add_subdirectory(axmol)`**，改为 `load_cache` 该目录并链接其中的
`lib/<Config>`、`bin/<Config>`、`runtime/axslc`；**头文件仍来自引擎源码树**。

- **产出它的命令**：在引擎根跑 `axmol build -p win32 -a x64 [-O3]`（聚合目标 `axmol-sdk`）。
  与项目构建的区别只有一点：**不带 `-d`**（CI 黄金路径即如此）。
- **消费它的口径**：值必须是**相对引擎根**的路径（`Path.GetRelativePath(engine, buildDir)`），
  由 `ProjectBuildOptions` 在项目 CMake 配置时拼进 `-xc`。
- **范围收窄**：引擎本身也允许 `LINUX` 消费，但**本项目只做 Windows 目标** ——
  闸门只在 `EnginePrebuilt.Supported` 一处，别处不得各写一份。
- **必须由 Hub 自己判准**：目录不合格时引擎会**静默退回源码构建、不报错**，
  所以只有 `PrebuiltStatus.Ready` 才把 `-DAX_PREBUILT_DIR` 交给 CMake，否则明确失败并指向引擎页。
- **多配置陷阱**：引擎侧链接用的是 `lib/${CMAKE_BUILD_TYPE}`，而 VS 多配置下真实目录是 `lib/<Config>` ——
  Hub 一律**发现 `lib/*`** 并报出实际存在的配置名，绝不假定目录名。
- 构建记录存在 Hub 数据根（`prebuilt/<引擎身份哈希>.json`），不在引擎树里 ——
  这样引擎被移出列表再加入仍能命中，且不会往第三方引擎树写入。

## 3.1 工具链状态判定：照抄 `1kiss`，不是"扫目录"

一个容易想当然的错：以为「装到 `tools/external` 才算就绪」。**不是**。引擎的 `setup_*` 会先找
**系统已装**的同名工具，**版本满足要求就直接用、不往引擎树里装**；只有不满足（或没装）才装到
`<prefix>`。所以 Hub 的判定必须同时镜像两件事：

**① 查找顺序**（差别来自 `find_prog` 有没有传 `-path` / `-mode`）：

| 顺序 | 工具 |
|---|---|
| 系统 PATH 优先 | `cmake`、`ninja`、`jdk(javac)`、`llvm(clang)`、`emsdk(emcc)` |
| 引擎树优先（`-mode BOTH`） | `axslcc`、`nuget`、`nasm` |
| 只在引擎树内 | `cmdlinetools(sdkmanager)`，路径是 `<sdk>/cmdline-tools/<preferred>/bin` |
| 只检测不安装 | Visual Studio（vswhere）、Xcode（`xcodebuild`） |

**② 版本要求语义**（`find_prog` 的 `$checkVerCond` 分支，逐条对应）：

| `build.profiles` 写法 | 引擎的判定 |
|---|---|
| `*` | 任意版本 |
| `21.1.8` | **字符串相等**（不是数值相等，`22.0` 不接受 `22.0.0`） |
| `5.5.1.*` | 通配（PowerShell `-like`） |
| `17.9+` | `>= 17.9`（4 段数值比较，缺位补 0） |
| `4.2.0~4.4.3+` | **`+` 结尾时区间上界失效**，退化成 `>= 4.2.0` —— 引擎的实际行为，Hub 照抄 |
| `17.0.10~17.0.20.1` | 真正的闭区间 |

版本号从工具 `--version` 的**第一行**用 `(\d+\.)+(\*|\d+)` 抠出；抠不出则退回**文件版本**并格式化成
`Major.Minor.Build`（引擎也是这么兜底的）。

由此得到三种状态：

- **Installed** —— 找到且满足要求（说明里写明来自 system PATH 还是 engine tree）；
- **UpdateAvailable** —— 找到但**不满足**：引擎会去装 `preferred` 那一份，Hub 据此提示；
- **Missing** —— 没找到。

**SDK 组件的判定与 `setup_android_sdk` 一致**：SDK 根按
`1k/.env` 的 `android_sdk_root` → `ANDROID_HOME` → `ANDROID_SDK_ROOT` → `<prefix>/android-sdk`
→ `<prefix>/adt/sdk` 解析；`platform-tools` / `platforms/android-<api>` / `build-tools/<ver>`
看 `source.properties` 在不在（`target_sdk >= 37` 且没写小版本时引擎会补 `.0`）；
NDK 把代号换算成 revision 前两段（`r27d` → `27.3`）再比 `ndk/*/source.properties` 的 `Pkg.Revision`。

**这条改动的实际价值**（本机实测）：改造前 Hub 只看 `tools/external`，于是把
「系统装了 CMake 4.3.2」判成 `Missing`（因为那棵树里的 `cmake/bin/cmake` 恰是 Linux 二进制）；
改造后与引擎一致地判为 `Installed (system PATH)`。同时暴露出两个**引擎真的会去重装**的项：
`LLVM`（树内 21.1.1，要求精确 21.1.8）与 `Android cmdline-tools`（树内 19.0/20.0，要求精确 22.0）。

## 4. 参数口径（来自引擎自己的 CI，不是猜的）

`.github/workflows/build.yml` 里的真实调用决定了 Hub 的映射：

```
axmol -p win32 -a x64 -xc '-DAX_ENABLE_VR=ON,-DAX_ENABLE_OPENXR=ON' -O3 -t cpp-tests
axmol -d .\HelloCpp -xc '-DAX_PREBUILT_DIR=build' -O3
axmol run -p win32 -a arm64 -t unit-tests -O3
./tools/cmdline/axmol -p ios -a arm64 -sdk simulator -t cpp-tests
```

两条易错点：

- **`-xc` 收的是「一个逗号分隔的字符串」**，不是多个参数。
- **Release 用 `-O3`**，不是 `-DCMAKE_BUILD_TYPE`（Windows 用 VS 生成器，是多配置，那个变量无效）。
- iOS 模拟器必须显式加 `-sdk simulator`（引擎不会从 arm64 猜到）。

## 5. 对 ADR-0001 的取代范围

- **被取代**：「工具链自持（固定版本，不复用系统工具）」这条定位，以及「三条贯穿原则」里的
  「绝不回退系统工具」。环境隔离仍用于 **Hub 自己的产物**（staging + 凭据文件）；
  `ProcessRunner` 的 `Environment.Clear()` 语义保留，但**构建进程改为继承父环境**，
  因为工具链由引擎自己找。
- **不变**：C#/.NET + Avalonia、四客户端共享一份 Core、CLI `--json` 契约、
  「AI 能力层只有一份实现」、「Hub 不是自举应用」。
- **数据驱动那条原则收窄**：`manifests/` 继续承载**引擎发行清单**与**模块/配方声明**，
  但不再承载工具版本、URL、SHA-256。

## 6. Hub 保留的增值

委派不等于什么都不做，以下仍然由 Hub 负责：

- **Windows 控制台日志捕获**：官方入口是 GUI 子系统，Hub 在入口未被用户改动时经
  `-xc -DCMAKE_PROJECT_INCLUDE=…` 注入一个把子系统改成控制台的补丁，运行日志才能进日志面板。
- **运行期发布与校验**：`PublishWindowsRuntime` 把 exe/资源/编译好的着色器复制到独立运行目录，
  `ValidateWindowsRuntimeAssets` 在启动前校验资源与着色器不缺失、不过期。
- **构建收据**：`<proj>/.hub/build.json` 记录本次构建对应的引擎安装与目标/配置，
  引擎重装或改配置后不允许用旧产物直接 Run。
- **Android 签名与设备管理**：`AndroidSigningService`（release keystore 校验）与
  `AndroidDeviceService`（adb 设备枚举），工具取自引擎树。
- **打包资产的安全落地**：`PackageInstaller` / `DownloadManager` 的 SHA-256 + staging + 凭据
  （仍用于引擎发行包与打包器）。
- **卡顿记录器** `UiStallWatch`、CLI `--json` 契约与验收工具。

## 7. 风险与已知缺口

| # | 风险 / 缺口 | 现状 |
|---|---|---|
| R1 | 每个引擎树各一份 GB 级工具 | 只对实际要构建的平台跑 `setup -p`；引擎导入**不自动** setup；UI 明示体积 |
| R2 | `setup.ps1` 改全局环境 | 执行前显式确认（工具链页 / 模块窗口）；文档不再声称工具链隔离 |
| R3 | 开发者模式未开时 `setup.ps1` **`exit 0`** 却什么都没装 | `EngineSetupService` 解析引擎原文判 `DeveloperModeBlocked`，按失败处理 |
| R4 | 无网络 / 受限环境 | 失败关闭；`build.profiles` 与探测本身不联网 |
| R5 | `axmol build` 的构建目录与 Hub 旧 `build-hub*` 不同 | 改为**发现**（`EngineBuildLayout`）；`build-hub*` 只剩 Hub 自己的 Android 暂存在用 |
| R6 | Android Release 签名尚未与 `axmol build -p android` 对接 | **未闭合**：签名材料与校验仍是 Hub 侧能力，等对接后重新定位 |
| R7 | `manifests/android-gradle-verification.xml` 不再有再生成脚本 | **未闭合**：原再生成路径依赖已删除的 Hub gradle 编排 |
| R8 | 真跑 `axmol build` / `run` | 需 GB 级下载与长时编译，由维护者手动执行 |

## 8. 影响面

**新增**：`Core/BuildProfile.cs`、`Core/EngineToolchain.cs`、`Core/EngineCommandLine.cs`、
`Core/AxmolCommandMap.cs`、`Core/EngineBuildLayout.cs`、`Core/EngineSetupService.cs`、
`Core/WindowsShell.cs`、`Core/Scripts/Invoke-AxmolSetup.ps1`

**删除**：`Core/ToolchainDetector.cs`、`Core/WindowsToolchainInstaller.cs`、
`Core/Scripts/Verify-MicrosoftSignature.ps1`、`manifests/toolchain-manifest.json`、
`manifests/android-native-toolchain-windows.json`、`manifests/android-packaging-toolchain-windows.json`、
`manifests/web-toolchain-windows.json`

**改写**：`Core/ProjectService.cs`、`Core/PlatformBuildService.cs`、`Core/EngineModules.cs`、
`Core/CliContract.cs`（`SchemaVersion` 1→2）、`Cli/Program.cs`、`App/Services/HubWorkspace.cs`、
`App/Views/Pages/ToolchainsPage.*`、`App/Views/Dialogs/ModuleWindow.cs`、`App/Views/OpsCheckWindow.cs`、
`tests/AxmolHub.Checks/Program.cs`、`manifests/module-manifest.json`
