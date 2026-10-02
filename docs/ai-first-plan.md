# Axmol Hub · AI 优先融入方案

> 目标：在项目里打开一个工程后，进入一个**专属 Axmol 游戏开发**的 AI 会话。
> 本文只描述方案，不含实现。可整体搬入 `axmolengine/axmol-hub`。

---

## 0. 核心判断

「专属感」**不来自聊天框**。聊天框是最容易被复制的一层——任何通用 agent 套个壳就有。

真正的差异来自三件通用 agent 拿不到的东西：

| 能力 | 通用 agent | Axmol Hub |
|---|---|---|
| 知识来源 | 训练数据（cocos2d-x 与 axmol v2 混杂，且有截止日期） | 磁盘上**已安装的这份引擎源码** |
| 构建能力 | 只能调系统里碰巧存在的工具，环境不可复现 | Hub 私有锁定的 CMake/Ninja/MSVC/SDK/axslcc/NDK |
| 反馈信号 | 写完即结束，正误由用户判断 | 能真实构建、真实运行、拿到真实编译器输出 |

**结论：先做「地基」（工具契约 + 上下文 + 索引），再做聊天框。**
顺序反了，做出来的就是「一个通用 AI 旁边放着 Axmol 项目」，这正是要避免的。

---

## 1. 架构：一个工具注册表，三种客户端

### 1.1 单一来源原则

工具（Tool）只定义**一次**，用 `Microsoft.Extensions.AI` 的 `AIFunction` 表达，然后同时投影到三个客户端：

```
                  ┌──────────────┬──────────────┬──────────────┐
  客户端           │ GUI 聊天面板 │  MCP Server  │  CLI --json  │
                  └──────┬───────┴──────┬───────┴──────┬───────┘
                         └──────────────┼──────────────┘
                                        ▼
                        工具注册表 · AIFunction 单一定义
                                        ▲
                  ┌──────────────┬──────┴───────┬──────────────┐
  上下文           │ ProjectDigest│ EngineIndex  │  BuildOutput  │
                  └──────────────┴──────────────┴──────────────┘
                   护栏：写文件需 diff 确认 · 构建需批准 · 路径不越项目根 · 引擎目录只读
```

这不是理想化设计，而是 .NET 生态里现成的事实：
MCP C# SDK 与 `Microsoft.Extensions.AI` 共用 `AIFunction` 抽象，
同一份 `[Description]` + 参数签名可以同时产出 MCP tool schema 和模型函数调用 schema。
三处不会漂移。

### 1.2 项目布局（新增，不动现有 Core）

| 项目 | TFM | 职责 |
|---|---|---|
| `AxmolHub.Core` | net8.0 | **新增** `ProjectDigest`、`EngineIndex`（宿主无关） |
| `AxmolHub.Agent` | net8.0 | 工具注册表 + `IChatClient` 管道 + 会话管理 |
| `AxmolHub.Mcp` | net8.0+ | MCP server（stdio / streamable HTTP），暴露注册表 |
| `AxmolHub.Cli` | net8.0 | 增加全局 `--json` |
| `AxmolHub.App` | — | 聊天面板（Avalonia 迁移后接入，见另案） |

`AxmolHub.Agent` **不引用** `AxmolHub.App`，保证 CLI/MCP 可在无 GUI 环境（CI、服务器、远程 agent）运行。

---

## 2. 上下文层：专属感的来源

### 2.1 ProjectDigest —— 机器可读的项目快照

```json
{
  "project": {
    "name": "MyGame",
    "path": "D:/dev/MyGame",
    "type": "cpp",
    "platform": "windows-x64",
    "configuration": "Debug",
    "buildStatus": "Failed"
  },
  "engine": {
    "version": "2.11.5",
    "channel": "local",
    "root": "D:/axmol/2.11.5",
    "headersIndexed": true,
    "templates": ["cpp", "lua"]
  },
  "toolchain": {
    "cmake": "4.2.1", "ninja": "1.13.2", "axslcc": "1.14.0",
    "msvc": "14.43", "sdk": "10.0.26100"
  },
  "sources": { "count": 12, "files": ["Classes/AppDelegate.cpp", "..."] },
  "assets":  { "contentRoot": "Content", "count": 34, "shaders": 7 },
  "lastBuild": {
    "exitCode": 2,
    "errors": [{ "file": "Classes/HelloWorldScene.cpp", "line": 42, "message": "..." }],
    "logPath": "..."
  },
  "modules": ["windows", "android"]
}
```

来源全部是现有代码已有数据：`StateStore.ReadProject` / `HubState` / `ToolchainDetector` / `EngineModules` / `HubLog`。
成本主要在**聚合与增量缓存**，不在采集。

### 2.2 EngineIndex —— 版本键控的引擎符号索引

对**已安装引擎源码树**建立索引：

1. **符号表**：解析 `core/`、`extensions/` 下头文件 → `类名 / 方法签名 / 文件:行号`。确定性、零成本、零幻觉。
2. **语义检索**：`tests/` 示例、`templates/`、README、头文件注释切块后做嵌入检索。
3. **版本键控**：`{engineVersion}` 为索引命名空间，多版本引擎共存互不污染；引擎升级时重建即可。

**这是 axmol v3 的救命稻草。** v3 发布后网络上没有任何可靠语料，通用模型一定答错。
而本地源码索引在 v3 装上的那一刻就是准确的——这是通用 agent 结构性做不到的事。

### 2.3 提示词分层（越往下越稳定）

| 层 | 内容 | 注入方式 |
|---|---|---|
| L0 | Axmol 人设与规则：`ax::` 命名空间、两段式构造、引用计数、`Director/Scene/Sprite`、Action、EventDispatcher；**明确禁止 cocos2d-x 写法** | 恒定 |
| L1 | 引擎源码索引（检索结果，附 `文件:行号`） | 按需 |
| L2 | ProjectDigest | 每轮 |
| L3 | 构建/运行真实输出（编译器原文，不改写） | 按需 |
| L4 | 用户项目当前改动中的文件与 diff | 按需 |

**硬规则**：回答里出现的任何引擎 API，必须能在 L1 索引里找到，否则模型必须先调 `engine.symbol` 工具核实。
找不到就明说「你装的 2.11.5 里没有这个 API」。宁可承认不知道，不许编。

---

## 3. 工具清单

### 只读（安全，默认开放）

| 工具 | 说明 |
|---|---|
| `project.info` | ProjectDigest |
| `project.tree` / `file.read` / `source.search` | 项目内文件访问 |
| `engine.symbol` | 精确查符号（类名/方法），返回签名 + 文件:行号 |
| `engine.search` | 引擎源码/示例语义检索 |
| `engine.sample` | 按主题取官方示例片段 |
| `targets` / `verify` / `devices` | 构建目标、工具链状态、设备列表 |

### 动作（需批准）

| 工具 | 说明 |
|---|---|
| `build` / `configure` | 走 Hub 锁定工具链 |
| `run` / `serve` / `deploy` | 运行、WASM 预览、按 serial 部署 |
| `package.android` | APK/AAB 打包与签名校验 |

### 写入（双重确认 + diff 预览）

| 工具 | 说明 |
|---|---|
| `file.write` | 必须先出 diff，用户确认后落盘 |
| `project.create` | 走 `ProjectService.CreateAsync` |

### 统一返回契约

```json
{ "ok": true, "exitCode": 0, "data": {}, "log": "...", "logPath": "...", "artifacts": [] }
```

与 `verify` 现有的 `exit code 2` 语义保持一致。

---

## 4. 护栏

- **路径不越界**：沿用现有 `PackageInstaller.SafePath`，一切文件操作限制在项目根内。
- **引擎目录只读**：AI 永不修改 `engine.root`。
- **写入必须 diff**：`ApprovalRequiredAIFunction`（`Microsoft.Extensions.AI` 内置）拦住 `file.write`。
- **构建需批准**：`build`/`deploy` 默认弹确认，可设为「本会话内自动批准」。
- **迭代有上限**：自动修复循环最多 N 轮（建议 3），超出即停下把日志交回用户。
- **离线可用**：支持本地 Ollama；云端为 BYO-key，且**按项目显式开启**后才外发代码。索引与嵌入**永不上云**。
- **拒绝越界**：非 Axmol 问题直接说明「这与 Axmol 无关」，不做通用助手。

---

## 5. 杀手级场景（按价值/成本排序）

| # | 场景 | 为什么通用 agent 做不到 |
|---|---|---|
| 1 | **构建失败 → 「问 AI」**：自动带上完整编译器输出 + 出错文件 + 相关引擎头文件 | 通用 agent 看不到你的编译器输出 |
| 2 | **API 版本核对**：「`Sprite::setTexture` 在 2.11.5 里是什么签名」→ 引 `core/2d/Sprite.h:142` | 训练数据里 axmol v2/cocos2d-x 混杂，必错 |
| 3 | **闭环修复**：AI 改码 → Hub 真实构建 → 真实报错 → AI 再改 → 直到通过 | 通用 agent 没有可复现的构建环境 |
| 4 | **脚手架**：「加一个带触摸的精灵场景」按真实项目布局生成 | 不知道 `.axproj` / `Classes/` / `Content/` 结构 |
| 5 | **着色器与资产**：axslcc 编译错误、Content 树、16KB 页对齐、Play 上架要求 | 领域知识，通用语料里几乎没有 |

---

## 6. 分期

| 阶段 | 产出 | 收益 |
|---|---|---|
| **P0 工具契约** | 所有 CLI 命令支持 `--json`，统一返回契约 | Hub 立刻可被脚本/CI 驱动 |
| **P1 MCP Server** | `AxmolHub.Mcp`，stdio transport | Cursor / Claude Code / CodeBuddy 直接驱动 Hub。**先验证道具有没有价值，再投 UI** |
| **P2 上下文与索引** | `ProjectDigest` + `EngineIndex`（版本键控） | 专属感的来源 |
| **P3 聊天面板** | 项目页内嵌，流式输出 + 工具调用卡片 + diff 预览 | 用户看得见的形态 |
| **P4 闭环** | 构建失败卡片 →「问 AI」→ 自动修复 → 自动重建 | 最直观的差异化体验 |
| **P5 场景化 Skills** | 新建场景 / 接入物理 / 打包 Android / Lua 绑定 等 playbook | 覆盖面 |

**P0–P2 是地基，不要跳过。**

---

## 7. 技术选型（2026-10 核实）

- **`Microsoft.Extensions.AI`**（10.x，`IChatClient`，GA 于 2025-05，兼容 .NET 8 LTS+）
  - `AIFunctionFactory.Create` + `[Description]` 声明工具
  - `UseFunctionInvocation()` 自动跑工具循环
  - `ApprovalRequiredAIFunction` 做危险工具的人机确认
  - `FunctionInvokingChatClient` 提供 `MaximumIterationsPerRequest` / `MaximumConsecutiveErrorsPerRequest` 等安全旋钮
  - 提供 `OpenAI / Azure OpenAI / Ollama / Anthropic` 适配器，换模型不改业务代码
- **MCP C# SDK**（v2.x，实现 MCP spec 2026-07-28；默认无状态，支持 MRTR）
  - 包拆分：`ModelContextProtocol.Core` / `ModelContextProtocol` / `ModelContextProtocol.AspNetCore`
  - 直接消费 `AIFunction`，与上面共享同一份工具定义
- **嵌入**：引擎源码量级不大，本地 ONNX 模型或云嵌入均可；先做**符号表**（零成本、零幻觉），语义检索后补

---

## 8. 面向 axmol v3 的三条硬约束

1. **索引必须可重建、按版本命名空间化。** v3 装上即可重建，不需要改代码。
2. **L0 人设不要写死 v2 约定。** 抽成 `EngineProfile`（按引擎版本选择规则集），v3 加一份即可。
3. **先解掉单引擎版本绑定。** `module-manifest.json` 只有一个 2.11.5 profile，Android 打包硬编码 `engine.Version != "2.11.5"` 就拒绝——v3 一发布立刻阻塞。这项改动小，且**与 AI 方案正交，应先行**。

---

## 9. 明确不做

- **不训练/微调模型。** v3 一发布，微调成果全部作废；本地索引没有这个问题。
- **不做通用聊天。** 越界问题明确拒绝或重定向。
- **不先做 UI。** 地基没建好就上聊天框，等于做一个通用 AI 外壳。
- **不把索引/嵌入上传云端。**
- **不让 AI 修改引擎目录。**
