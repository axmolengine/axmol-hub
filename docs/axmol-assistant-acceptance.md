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
