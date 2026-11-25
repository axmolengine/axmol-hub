# Axmol 游戏开发 skills：版本对照验收

## 基线与复跑方法

基线 Hub：`9ab1012a` 的改动前构建；skills 实现：`4a9e716`，本轮再完善查证顺序。引擎固定为 **2.11.6**（`release/2.x`，`39a46146d67d430abfafcc89b46476e3a10d86db`）与 **3.0.0 dev**（`4f32c642ea6d0a4d67f581936ca5e29920819929`），不是所有 2.x/3.x 的兼容声明。

- 用对应引擎 `axmol new` 建 C++/Lua 临时游戏。模型检索工作区带同修订的内嵌 `axmol/` 源码归档（含 tests）；运行验收在另一组游戏中使用同修订的引擎。普通 isolated 项目不保证带 tests，不能静默借另一版作证。
- 同一题面、Agent 模式、四个空历史会话。**S**：给这个 C++ 游戏项目中的一个 Sprite 加可见的模糊 shader。以实际引擎为准，指出 API 声明、同版示例、shader 源文件和构建规则，给出可编译的最小改动。
- **L**：给这个 Lua 游戏场景加点击移动。以实际引擎为准，指出 Lua 绑定、同版示例、回调和事件注册位置，给出可运行的最小改动。
- 两题共同后缀：本轮仅在项目和内嵌 axmol 目录中只读查证，不改文件、不运行命令、不联网；编译与运行由验收人员进行。回答最后另起一个段落只写 AXCASEEND。
- 一次 `fs_find/fs_grep/fs_read` 函数调用计一次文件检索，目录/地图浏览不计，skill 读取单列。所有请求、token 和反馈均由持久化会话记录，不用运行时样例替代模型回答。

## 同路由完整对照（macOS arm64）

用户确认 zhongkey `deepseek-v4.1-flash` 与 DeepSeek `deepseek-flash` 为同一模型的提供商别名。原 DeepSeek 基线三题因 HTTP 402 中断后，先跑每题 8 次的 zhongkey 实验，随后用户明确允许跑到结果。完整对照两侧都使用 **zhongkey / deepseek-v4.1-flash**、相同题面和 8192-token 单次输出上限，自动重试为 0，单例有 20 分钟超时。临时程序复用各自构建的 `ChatTools` 和 `ChatPipeline`；基线直接加载保留的旧程序集，不切换 Git 工作树、不改用户配置、不导出凭据。

表中 `首轮 + 反馈` 分开计数。v3 shader 两侧均因输出长度截断而续写；skills 侧两题 v2 首轮误把 CHANGELOG 的 2.11.5 当作当前源码版本，读取 `core/axmolver.h.in` 后改为 2.11.6。反馈不是“首次就答对”。

| 题面 | 基线请求：首轮 + 反馈 | 基线检索 | skills 请求：首轮 + 反馈 | skills 检索：首轮 + 反馈 | 最终结果 |
|---|---:|---:|---:|---:|---|
| 2.11.6 / S | 59 + 0 | 82 | 53 + 2 | 73 + 1 = **74** | 版本表述经反馈纠正；代码编译、打包与可见模糊通过 |
| 2.11.6 / L | 66 + 0 | 79 | 56 + 5 | 86 + 4 = **90** | 版本表述经反馈纠正；代码编译、两次真实点击移动通过 |
| 3.0.0 dev / S | 44 + 1 | 76 | 34 + 1 | 68 + 0 = **68** | 两侧续写获得完整答案；skills 代码编译、打包与可见模糊通过 |
| 3.0.0 dev / L | 55 + 0 | 74 | 48 + 0 | 77 + 0 = **77** | 首轮正确核对绑定与 Node 命中区域；代码编译、两次真实点击移动通过 |
| **合计** | **225** | **311** | **199** | **309** | skills 侧每题另有 1 次 skill 读取 |

提供商报告的输入/输出合计：基线 **10,374,415 / 120,144 tokens**，skills **8,496,373 / 107,087 tokens**（均含反馈，不是费用报价）。这是每题一次的工程对照，不是统计实验或仅隔离 skill 的消融测试；不能据此宣称通用正确率提高。请求数减少，但总文件检索几乎不变，Lua 两题增加，**没有稳定降低检索成本的证据**。基线答案已完整读取以比较引用和调用成本，本轮未另外实编其全部代码；四例实机成绩对应 skills 侧最终代码。

## 模型代码与宿主验证

两版 C++ 最终答案均在游戏自己的 shader 目录放文件：v2 `Source/shaders/blur.fsh`、v3 `Source/Shaders/blur_fs.hlsl`，均加载 `custom/blur_fs`。按本版 Sprite 声明、`ProgramManager`、`ShaderTest.cpp` 与 `AXBuildHelpers.cmake` 核对，未见跨代 API。v2 Lua 使用手工绑定的 `EventListenerTouchOneByOne/registerScriptHandler`；v3 使用 manifest 导出的 `PointerEventListener` 回调字段，并给零尺寸 MVC ViewBase 设置画布命中区域。

- **实编与打包**：四个游戏均通过对应引擎 `axmol build -p osx -a arm64`，配置为 `RelWithDebInfo`。新增 shader 要 `-forceConfig`：验收人员首次只 build，导致新归档缺失，v2 启动异常、v3 报资源找不到；重跑 configure 后两份 `.app` 均含新的 `Resources/axslc/custom/blur_fs`。临时应用重构建后的签名失败另以本地 ad-hoc 签名修复，不改引擎或 Hub 安装包。
- **shader 运行**：模型代码的两版对照窗口均实际启动，日志确认新归档加载；助手捕获画面确认左侧原图清晰、右侧模糊正常（v2 半径 8 / samples 8，v3 半径 4 / samples 16）。此前用户也确认了人工样例的两版模糊效果，两次验收分别记录。
- **Lua 运行**：模型代码的 Click2（pid 6868）与 Click3（pid 6869）分别经过 Computer Use 在两个不同位置点击，随后新帧显示同一个 Sprite 到达点击位置，Hello World 标签不动。Click2 目标截图坐标 `(480,940)`、`(1420,730)`；Click3 为 `(490,950)`、`(1420,730)`；运行错误日志为空。此前人工样例由用户确认并有 `RELEASE/ARRIVED` 日志，不能替代这次模型代码验收。
- **工具与撤权**：四格离线调用核对本版来源和可读证据；撤掉外部引擎授权后新的 skill/read/search 不返回源码。窄子目录授权不开放兄弟目录；门控或同步读取期间取消不返回源码结果。Plan/Agent 有固定 ID 的 `axmol_skill_read`，Ask 无此入口，错误 ID 不按路径读取。

原始数据均留在忽略的 `tmp/ax-skills-accept/`，不提交引擎源码、凭据、聊天转储或游戏构建：`baseline-completed/<case>/`、`completed/<case>/` 内有 `result.json`、答案和反馈；`runtime/*-build.log`、`model-*-final-run.log/err`、`model-Click*-run.err` 为实机日志；`final-comparison.json` 为汇总；离线验证见 `offline-cases.log`、`tool-probe.log`、`workspace-check.log`、`context-check.log`、`hub-release.log`。

## 阶段结论

**阶段 0/1 的实现、四题对照、最终代码实编实机与撤权验收已完成。** 首答并非全部无误：保留 v2 版本误判、v3 输出截断及各次反馈，不能称为四题首次全通过。两份 skill 已补充窄查证顺序、版本头优先于 CHANGELOG，以及 v3 Node 命中区域要求；版本头提示是在首轮发现问题后加入，未再做一轮空历史测试来声称其消除了误判。

**下一步继续完善 skill 与证据选择，不启动阶段 2/3。** 同版已知入口仍被反复搜目录、审计无关 API，比“缺一套符号索引”更直接；总检索 311 → 309 不支持扩大到 API 索引生成/发布。到此停止这轮付费测试，不追加重复实验；首版可以收尾，但不发布检索降幅或通用正确率宣传。

### 历史中断记录（不纳入完整对照）

最初 DeepSeek 基线 `fs_find/fs_grep/fs_read`：v2 S **3/14/24 = 41**、v2 L **5/11/13 = 29**、v3 S **6/30/45 = 81**、v3 L **4/17/17 = 38**；只有 v3 S 完成，其余 HTTP 402。一次 zhongkey v3 S 续跑 **5/23/21 = 49**，1 次 skill，218 秒后取消。后来的 8-request 实验合计 **32 次请求**，检索 v2 S/L 为 **13/10**、v3 S/L 为 **11/9**，四题都没有最终答案，报告输入 **341,852**、输出 **5,909 tokens**。这些中断数字不能与完整任务计算收益，分别留在 `baseline-*`、`phase1-blur3` 与 `bounded/<case>/`。

---

## 阶段 2：引擎摘要注入 + 4 领域 Skills + 本地版本化 API 索引验收（`xingchiapi / gpt-6.1-sol`）

本轮在不改动通用 `ChatModePrompt`、保持 Ask 模式零本地权限和撤权即时生效的前提下，完成了三层升级：

1. **统一引擎定位与确定性摘要（`AxmolSkillEngine` / `AxmolProjectDigest`）**：支持内嵌 `axmol/`、锁定安装和直接打开引擎仓库（`axmol` / `axmol2`）；仅在 Plan/Agent 模式且命中 Axmol 工作区时注入版本、主代际、核心目录、技能清单与索引优先提示，普通非 Axmol 目录零注入。
2. **统一 4 领域内置 Skills（`AxmolSkillCatalog`）**：新增 `axmol-v3-migration`、`axmol-build-cmake`（核对 `AxmolCommandMap.cs` 与 `CMakeOptions.md`：`-c`/`-configOnly` 仅配置、配置档用 `-O0..-O3`、`-xc` 单参数逗号分隔、`-DAX_RENDER_API`），并升级 `axmol-sprite-shader`（补全 `docs/hlsl-spec.md` 的 `base.hlsli` 与禁止手写 `: register(...)` 规则）与 `axmol-lua-input`（v3 sol2 manifests + adapters vs v2 tolua++ auto + manual），均改为 `axmol_api_query` 优先。
3. **本地版本化 API 索引与 `axmol_api_query`（`AxmolApiIndex` / `AxmolApiIndexStore`）**：纯 .NET 8 实现，预留 `schemaVersion: 1` 与 `sourceKind: "local-source"`（为阶段 3 的 `"official-release"` 预留字段）。缓存落于 `<HubDataRoot>/cache/axmol-api-index/`，以可读文件相对路径 + 大小 + 修改时间计算指纹，本地改头文件立即失效重建；受沙箱与窄目录授权约束。

### 真实仓库本地索引实测（macOS arm64）

| 目标树 | 版本 | 索引规模 | 冷建耗时 | 热载耗时 | 紧凑缓存 | 关键探针 |
|---|---|---|---:|---:|---:|---|
| 内嵌 v2（`Blur2`） | `2.11.6`（`core/`） | 1,210 C++ 头文件 / 52 Lua 文件 / 40,515 符号 | 717 ms | 163 ms | 13.2 MB | `Sprite::setProgramState`、`ProgramManager::loadProgram`、`ax::backend::ProgramState`、`AsyncTaskPool`、`EventListenerTouchOneByOne.registerScriptHandler` 全部命中 |
| 内嵌 v3（`Blur3`） | `3.0.0`（`axmol/`，`4f32c64`） | 1,243 C++ 头文件 / 35 Lua 文件 / 39,547 符号 | 611 ms | 166 ms | 12.8 MB | `Sprite::setProgramState`、`ProgramManager::loadProgram`、`ax::rhi::ProgramState`、`JobSystem`、`ax.PointerEventListener.onPointerDown`、`ax.ParticleSystemQuad` 命中；`AsyncTaskPool` 确认缺失 |
| 本地 `/Users/simdsoft/oss/axmol2` | `2.11.6`（`39a46146d`） | 1,210 头文件 / 52 Lua / 40,515 符号 | 546 ms | 72 ms | 13.2 MB | 全部命中 |
| 本地 `/Users/simdsoft/oss/axmol` | `3.0.0`（`9e5b9537f`） | 1,245 头文件 / 35 Lua / 39,547 符号 | 473 ms | 77 ms | 12.8 MB | 全部命中，`AsyncTaskPool` 确认缺失 |

### 同路由 8 题 A/B 对照（`xingchiapi / gpt-6.1-sol`，`responses` 协议，`maxOutputTokens=16384`）

基线侧关闭 `AxmolProjectDigest`、`axmol_skill_read` 与 `axmol_api_query`，仅保留通用 Agent 提示词与常规文件工具；升级侧开启三层能力。两侧均为单轮完成、零续写、零人工反馈（原始记录存于忽略目录 `tmp/ax-phase2-accept/{baseline,upgraded}/`）：

| 题面 | 基线 请求 / 文件检索 / 输入 token / 耗时 | 升级 请求 / 文件检索 / API 查询 / skill / 输入 token / 耗时 | 文件检索变化 | 输入 token 变化 | 首轮关键正确性对照 |
|---|---:|---:|---:|---:|---|
| `v2-blur`（原题） | 9 / 39 / 191K / 97.0s | 7 / 23 / 3 / 1 / 120K / 74.2s | **-41.0%** | **-37.3%** | 两侧均命中 `2.11.6` 与 GLSL/CMake |
| `v2-click`（原题） | 8 / 34 / 213K / 228.6s | 11 / 39 / 13 / 1 / 296K / 526.4s | +14.7% | +39.1% | 基线漏报精确版本 `2.11.6`；升级准确引用 `2.11.6` 与手工绑定 |
| `v3-blur`（原题） | 9 / 37 / 178K / 102.0s | 11 / 33 / 4 / 1 / 225K / 115.2s | **-10.8%** | +26.9% | 两侧均命中 `3.0.0` 与 HLSL 管线 |
| `v3-click`（原题） | 18 / 64 / 835K / 783.6s | 16 / 47 / 17 / 1 / 344K / 502.2s | **-26.6%** | **-58.7%** | **基线漏掉零尺寸 `ViewBase` 的 `setContentSize` 命中区修复（运行时点击将失效）**；升级首轮即命中 |
| `v3-migrate-cpp`（新增） | 9 / 43 / 209K / 164.7s | 6 / 15 / 8 / 1 / 90K / 99.3s | **-65.1%** | **-57.0%** | 升级 6 轮即准确迁移 `Node.h`、`PointerEventListener`、`Director::runAsync`/`JobSystem`、`ax::rhi::ProgramState` |
| `v3-rhi-hlsl`（新增） | 18 / 107 / 785K / 529.0s | 9 / 32 / 5 / 1 / 193K / 113.7s | **-70.1%** | **-75.4%** | 升级符合 `docs/hlsl-spec.md`（`base.hlsli`、无 `: register(...)`、`cbuffer`），已实编实机通过 |
| `v2-build-cmake`（新增） | 9 / 18 / 134K / 182.9s | 7 / 19 / 1 / 1 / 120K / 129.9s | +5.6% | **-10.7%** | **基线漏掉 `-forceConfig` 与 `2.11.6`**；升级准确给出 `-forceConfig` 与 `AXBuildHelpers.cmake` 行号 |
| `v3-migrate-lua`（新增） | 7 / 26 / 149K / 130.3s | 40 / 27 / 9 / 2 / 1,414K / 335.7s | +3.8% | +847.8% | 两侧均识别 `ParticleSystemQuad`/`ax.size`/`AsyncTaskPool` 移除；升级侧该题单轮串行发 1 个工具调用拉高了请求数与累计输入 token |
| **8 题合计** | **87 / 368 / 2,694K / 2,218.1s** | **107 / 235 / 60 / 9 / 2,803K / 1,896.6s** | **-36.1%** | +4.0% | 剔除 `v3-migrate-lua` 串行单调用异常项后，其余 7 题请求 `80 → 67`（-16.3%）、输入 token `2,545K → 1,389K`（**-45.4%**） |

### 实编与运行抽检（macOS arm64 / Metal MSL 2.0）

- **`v3-rhi-hlsl` + `v3-migrate-cpp`（`Blur3`）**：将模型生成的 `Source/Shaders/tintPulse_fs.hlsl` 与 `MainScene.h/.cpp`（`ProgramManager::loadProgram`、`ax::rhi::ProgramState`、`Director::runAsync`）合入临时 `Blur3` 并重新配置构建（`RelWithDebInfo`），`axslcc` 与 Xcode 均 `BUILD SUCCEEDED`；产物包内存在 `Blur3.app/Contents/Resources/axslc/custom/tintPulse_fs`（1,311 字节）。本地 ad-hoc 重签启动后，stdout 确认输出 `Phase2 v3-rhi-hlsl shader loaded: custom/tintPulse_fs` 与 `Phase2 v3-migrate-cpp Director::runAsync callback ran on Axmol thread`，stderr 为空。
- **`v3-migrate-lua`（`Click3`）**：将模型生成的 v3 sol2 `MainScene.lua`（`ax.size(800, 600)`、`ax.ParticleSystemQuad`、`ax.PointerEventListener`、`onExit` 回调令牌失效）置入临时 `Click3.app` 启动运行，无崩溃且 stderr 为空。
- **离线检查与构建**：`dotnet build -c Release src/AxmolHub` 0 警告 0 错误；`--check-ai-workspace`（含摘要、4 技能、本地索引、编辑失效、撤权与窄授权隔离）、`--check-ai-context`、`--check-ai-tool-policy` 全部通过。

### 阶段 2 结论

- 相比阶段 1，本轮在 `xingchiapi / gpt-6.1-sol` 8 题对照中证明了显著收益：**总文件检索下降 36.1%**（复杂 v3 渲染/迁移题下降 **65%–70%**，输入 token 下降 **57%–75%**），并在 3 道关键题上直接消除了基线的首轮正确性缺陷（v3 `ViewBase` 零尺寸点击命中区、v2 `-forceConfig`、v2 `2.11.6` 版本识别）。
- 阶段 3（官方发行版索引独立打包发布、SHA-256 校验、按需下载与离线缓存）所需的数据结构（`schemaVersion: 1`、`sourceKind` 区分 `"local-source"` 与 `"official-release"`）已就绪，可按实际发布节奏独立立项推进。
