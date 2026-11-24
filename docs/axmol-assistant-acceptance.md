# Axmol 游戏开发 skills：版本对照验收

## 复跑方法

基线 Hub：`9ab1012a`。本机对照的引擎是 **2.11.6**（`release/2.x`，`39a46146d67d430abfafcc89b46476e3a10d86db`）和 **3.0.0 开发树**（`dev`，`4f32c642ea6d0a4d67f581936ca5e29920819929`）。不是所有 2.x、3.x 的兼容性声明。

1. 各用对应引擎的 `axmol new` 在临时目录创建一个 `-l cpp` 和一个 `-l lua` 游戏。在本次检索对照中，项目的 `axmol/` 放同修订的完整源码归档（含 tests）；不要求用户的普通 isolated 项目带 tests。不要修改引擎安装。构建机另需本版工具链及第三方依赖。
2. 同一模型、同一 Agent 模式，每格一个独立会话、空历史、工作区选对应游戏。改动前后都用以下原题；共同后缀为「本轮仅在项目和内嵌 axmol 目录中只读查证，不改文件、不运行命令、不联网；编译与运行由验收人员进行。回答最后另起一个段落只写 AXCASEEND。」
   - **S**：给这个 C++ 游戏项目中的一个 Sprite 加可见的模糊 shader。以实际引擎为准，指出 API 声明、同版示例、shader 源文件和构建规则，给出可编译的最小改动。
   - **L**：给这个 Lua 游戏场景加点击移动。以实际引擎为准，指出 Lua 绑定、同版示例、回调和事件注册位置，给出可运行的最小改动。
3. 从会话的函数调用记录计 `fs_find` / `fs_grep` / `fs_read`（表中顺序）；结果行不重复计，地图/目录浏览不计，skill 调用单列。对照回答和本树的声明、用例，不能用相似代码替代编译。把答案应用到临时游戏，调用对应引擎 `axmol build -p osx -a arm64`（其他宿主换本机目标）；Lua 游戏还要启动，在两个不同位置按下并释放，确认同一个 Sprite 两次移动。另用**外部**引擎做授权→撤权→再次查询，不用内嵌引擎模拟撤权。

## 实测（macOS arm64）

| 版本 / 题面 | 改动前：是否串版；编译/运行；检索 | 加入 skill 后：是否串版；编译/运行；检索 |
|---|---|---|
| 2.11.6 / S | 无最终回答；无产物；3/14/24 = **41**（中断） | 未进行同模型复跑 |
| 2.11.6 / L | 无最终回答；未运行；5/11/13 = **29**（中断） | 未进行同模型复跑 |
| 3.0.0 dev / S | 回答使用本版 rhi、HLSL 和 `Source/Shaders`，未见串版；答案已在临时游戏中编译并打包；6/30/45 = **81** | 未完成同模型复跑 |
| 3.0.0 dev / L | 无最终回答；未运行；4/17/17 = **38**（中断） | 未进行同模型复跑 |

改动前四个产品会话均为 `deepseek-flash`；2.x 两题和 3.x Lua 收到 HTTP 402 `insufficient_balance_error`（测试预算用尽）。中断数是实际发生的调用，不是完成任务的成本，也不是引擎识别失败。3.x shader 的完整答案已持久化，虽然自动驱动未在窗口中匹配到结束标记。一次续跑中隔离会话被切换到 `deepseek-v4.1-flash`，读了 skill 但没有最终回答，因此不能算同模型对照。此后停止付费复测，也不换提供商补跑。

## 阶段 1：本地验证与剩余验收

四格离线调用均从本版外部引擎得到候选位置、读到 skill 指定的声明/示例，撤销授权后均不再返回源码。此项是宿主工具验证，**不是模型答案或检索收益对照**。两份 Markdown 是嵌入资源；Plan/Agent 有 `axmol_skill_read`，Ask 没有，错误 ID 不按路径读取。父目录搜索不能读未授权的引擎文件，窄子目录授权不扩到兄弟目录。请求在门控或同步读取期间取消时，不把源码结果交回模型。

| 临时游戏 | 来源与实际构建结果 | 运行结果 |
|---|---|---|
| Blur2 | 2.11.6 的 SpriteBlur 调用链和 `example_Blur.fsh`；macOS arm64 `RelWithDebInfo` 构建成功，`.app` 含 `custom/example_Blur_fs` | 未做目视比较 |
| Blur3 | 3.0.0 dev 的同版 HLSL/ProgramState；同目标构建成功；另将旧基线答案的 `blur3_fs.hlsl` 和 C++ 片段实编，`.app` 含 `custom/blur3_fs` | 未做目视比较 |
| Click2 | 2.11.6 手工绑定、`EventListenerTouchOneByOne` 与同版 ClickAndMoveTest；同目标构建成功 | **人工点击通过**；多组 `RELEASE` / `ARRIVED` 坐标匹配 |
| Click3 | 3.0.0 dev Lua manifest、`PointerEventListener` 与同版 ClickAndMoveTest；同目标构建成功 | 首次人工点击失败，仅 `READY`；MVC ViewBase 的零尺寸 Node 不能命中。补画布尺寸后**用户确认 Sprite 随点击移动**，且 `151.6,323.9`、`791.0,38.1` 的释放与到达坐标匹配 |

验证日志在 `tmp/ax-skills-accept/`：`runtime/*-build.log`、`runtime/baseline-Blur3-build.log`、`offline-cases.log`、`workspace-check.log`、`context-check.log`、`hub-release.log`。Lua 临时场景在输入释放和 MoveTo 完成时写 `runtime/Click2-input.log` / `Click3-input.log`；两处不同坐标都有 `RELEASE` 与匹配的 `ARRIVED` 才算通过。仅 `READY`、脚本解析和 C++ 构建均不算点击验收。

**发版验收仍未完成：**四题同模型答案对照因预算不足暂停；两份 Lua 已通过真实点击，shader 已实编并打包但未目视比较。阶段 2、3 保持未启动：本轮 Click3 的失败是“找对 API 却漏掉节点命中区域”，已据此完善 skill；现有记录不足以证明检索是瓶颈，不生成或发布 API 索引。

