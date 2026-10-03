# ADR-0001：Hub 采用 C# / Avalonia，定位为「环境与构建服务」

- **状态**：已接受
- **日期**：2026-10-02
- **决策人**：Axmol 作者（`axmolengine` 组织维护者）
- **相关**：`docs/hub-development-plan.md`（排期索引）、`docs/ai-first-plan.md`、`docs/avalonia-migration-plan.md`

> **后续变更（2026-10-03）**：本文档正文里「模块安装」的定位已删除 —— Hub 不再有独立的「模块」层，
> 平台/架构约束由 `BuildTargets` 表达、工具链准备由引擎 `setup.ps1` 承担。历史落地记录中出现的
> `module-manifest.json` 已改名为 `recipe-manifest.json`（承载 Android 打包的配方验证闸门），
> `EngineModules` 类已改名为 `PackagingRecipes`。下文的历史叙述保留当时的命名，不再逐字回改。

---

## 1. 背景

需要决定 Axmol Hub 的长期形态，并在 `axmolengine/axmol-hub`（空仓库）上启动共建。有三个候选：

1. **在现有外部项目上继续**——C# / WPF，Windows x64 已闭环，Android 链路完整
2. **重写为 C++ / ImGui**——照创始设想稿（`Axmol Hub.md`，仓库外文档，未入仓）的 UI 与工程结构设想，并成为 Axmol 自举应用
3. **保留 C# 但换 UI 栈**

评估过程中确认的既成事实：

- WPF 是 Windows 独占，**在任何分支里都必须换掉**，因此"换 UI"不是决策变量
- UI 层的跨平台在两个分支里成本相近：C++ 一侧有 Axmol 自带的 ImGui 1.92.9-docking（`AX_ENABLE_EXT_IMGUI`，axmolengine 自维护 fork），C# 一侧有 Avalonia
- 现有仓库的**不可移植资产**只有一项：**已验证的 Windows 端到端闭环**（下载→构建→运行→产物校验）。其余（1992 行 Core、102 项行为检查、manifests 数据、踩坑清单）都可移植
- **关键新输入**：同一位贡献者已做出 **Axmol Editor 初版，技术方案为 Axmol 自举，观感良好**

## 2. 决策

1. **Hub 技术栈 = C# / .NET 8+，UI 层由 WPF 迁移到 Avalonia**（取得 Windows / macOS / Linux 三平台 UI）
2. **Hub 不是 Axmol 自举应用。** Axmol 自举由 **Axmol Editor** 承担；Hub 不需要为此负责
3. **Hub 定位 = 「环境与构建服务」**：引擎生命周期（下载 / 导入 / 校验 / 修复 / 卸载）、工具链准备、项目创建、目标选择、构建、运行、部署、设备管理；对外以 **CLI `--json` + MCP** 暴露
   （**「工具链自持」一条已被 [ADR-0002](0002-toolchain-and-build-delegated-to-engine-cmdline.md) 取代**：工具链与构建交还引擎 cmdline，`1k/build.profiles` 为版本真源；
   **「模块安装」已删除（2026-10-03）**：平台/架构约束由 `BuildTargets` 表达，工具链准备由引擎 `setup.ps1` 承担，不再有独立的「模块」层）
4. **Editor 是 Hub 的一个客户端**，与 GUI / CLI / MCP 并列。**AI 能力层全局只有一份实现**，不允许 Hub 与 Editor 各写一套
5. **Hub 引入 `axmolengine/axmol-hub` 进行组织内共建**

## 3. 理由

**（1）Editor 的出现把语言问题从战略问题降级为工程问题。** 它同时接走了三件事：dogfooding / 自举示范、"一套语言一套 CI"、以及"Hub 将来要渲染引擎内容"的可能性。前两项是 C++ 分支的主要理由，第三项是它唯一的结构性优势——现在都有了归属。

**（2）因此决策退回成纯工程比较，而已验证资产在其中占优。** 保留 Core 意味着不必重新挣一遍 Windows 运行证据；而**这部分正是 AI 无法替你挣的**（可枚举的坑已被文档化成可移植知识，只有真跑出来的证据不可移植）。

**（3）"Hub 用 C#" 不再损害组织。** `axmolengine` 的自举能力由 Editor 提供，C# 只是多一种维护语言，不是取代 C++。

**（4）服务边界让 Editor 可复用而非重复实现。** 引擎管理、工具链、构建这三件事 Editor 迟早也要用。把 Hub 做成服务、Editor 做成客户端，是唯一能避免"两份漂移实现"的架构——而且它本来就是 `ai-first-plan.md` 里"工具只定义一次、多客户端共享"的形状。

## 4. 后果

### 正向

- 保留 1992 行 Core 与已验证的 Windows 闭环；Android 签名/校验链、安全加固、着色器产物校验等踩坑知识不重写
- App 层 1457 行 C# + 99 行 XAML 大体可平移到 Avalonia（手写 `ControlTemplate`、`DataGrid`、MVVM 风格 DataContext 都在 Avalonia 有对应）
- Hub 与 Editor 只通过 `--json` / MCP 解耦，可独立演进、独立发版
- 生态借用面大：AI 聊天面板所需的 Markdown 渲染、代码高亮、跨行选择、复制、对话历史都有现成控件

### 负向 / 明确接受的风险

- **组织需要长期维护一套 C#**（此前只有 C++）。这是新增能力，必须指定维护者，否则会成为孤儿代码
- **macOS / Linux 的宿主后端仍需从零写、从零验证**——与本决策无关，两个分支都一样，不要以为"选对了语言就跨平台了"
- **Hub ↔ Editor 的范围冲突是本决策最大的风险**：两者都会想做引擎管理、构建、AI。必须靠契约约束，不能靠约定俗成
- 需要处理 .NET 运行时分发与**代码签名**（未签名的发行版会让官方仓库的首次运行体验很差）

### 被本决策排除

- 不再评估 C++ / ImGui 作为 Hub 的 UI 栈。若将来要改，须**新开 ADR**，并承担"重写 UI + 重写宿主后端"的成本
- Hub 不再承担自举 / dogfooding 职能

## 5. 对 AI 方案的影响（重要）

`docs/ai-first-plan.md` 的"工具只定义一次、多客户端共享"由**三客户端扩为四客户端**：

```
       AIFunction（单一定义）
              │
   ┌──────────┼──────────┬──────────┐
 GUI(Avalonia) CLI(--json) MCP  Editor
```

于是 **P0（CLI 统一 `--json` 契约）与 P1（MCP Server）的优先级上升**：它们不再是"为 AI 顺便加的结构化输出"，而是 **Hub 与 Editor 之间的正式接口**。这两项应当排在任何 UI 工作之前——它们不改 UI、与栈无关，且是 Editor 能否安全接入的前提。

## 6. 尚未回答（阻塞边界契约定稿）

关于 Editor，下列问题必须先有答案，否则边界只能靠猜：

1. 技术栈与构建方式确认（Axmol 自举 + `1k`？）
2. **它是否也有引擎版本管理 / 工具链安装 / 构建运行？** 若有，立刻定"谁是真源"
3. **它是否也有 AI 助手？** 若有，必须共用能力层
4. 许可证、第三方依赖、是否有非公开托管资源
5. **同一位贡献者能否同时维护两个仓库？**（容量风险，非技术风险）

## 7. 同批待决（bucket C 剩余）

| # | 议题 | 说明 |
|---|---|---|
| 1 | **多引擎版本绑定** —— **代码级已解（2026-10-02）**，见 `docs/hub-development-plan.md` A1 落地记录 | `module-manifest.json` 只有 `2.11.5` 一个 profile，Android 打包硬编码 `engine.Version != "2.11.5"` 即拒绝。**与 v3 发布直接冲突，建议最先解掉** |
| 2 | 项目内写入策略 | `.axmol-hub.json` 保留 / 改回零写入。`EngineIndex`、模块状态、图形后端注入都依赖此决定 |
| 3 | 主窗口形态 | 四页导航 / 三栏 docking（原稿 §13 设想）——建议与 Avalonia 迁移合并做 |
| 4 | Graphics backend 建模 | `BuildTarget` 目前无图形后端字段，原稿 §9 的上下文项无法满足 |

> 注：第 1 项**与 AI 方案、与本 ADR 都正交**，但它会在 v3 发布当天变成阻塞，且改动很小。它应该是所有人手上的第一件事。
> **2026-10-02 更新：其中的代码级部分已解。** 两处硬编码的版本闸门改为查 `module-manifest.json` 的 `verifiedRecipes`，于是为 v3 放行只需改数据、不用改 C#。**但"未验证的版本一律拒绝"这条纪律原样保留，并且没有添加任何新 profile** —— 在 v3 上真的验证 Android 打包仍是一件必须由人完成的验证工作，它不在代码范畴内。细节与顺带查出的 GUI `.Single()` 缺陷见 `docs/hub-development-plan.md` A1。

## 8. 实施顺序（引入 org 共建）

1. `git init`，**以原作者身份**提交首个 commit（当前目录无 git 历史，这是保留 authorship 的唯一机会）
2. LICENSE 改为 Axmol house style + 新建 `AUTHORS.md`（用 `(see AUTHORS.md)` 承载原作者归属）
3. `CONTRIBUTING.md`（指向 CLA）、`CODE_OF_CONDUCT.md`、`SECURITY.md`、PR / Issue 模板
   - CLA 只做著作权授权，**不得掺入任何对价安排**；将来若发生付费，另签独立协议。此约束与是否制定补偿政策无关，且**现在就适用于本步**
4. 把 102 项行为检查接进 CI（它不依赖任何测试框架，接入近乎零成本，但官方仓库必须要有）
5. README 顶部声明**初始版本范围：Windows-first**，避免 macOS / Linux 用户直接报"跑不起来"
6. **解掉多引擎版本绑定**（v3 阻塞项）
7. 第一项功能工作：**Avalonia 迁移，Core 一行不改**——用于验证迁移路径，产出一个能跑的新 UI
8. 此后：P0 `--json` 契约 → P1 MCP Server → macOS / Linux 宿主后端

> 顺序的理由：第 6 项的时间窗最窄（v3 一发布就阻塞），第 7 项风险最高（决定 Core 是否真的不用动），第 8 项是 Editor 接入的前置条件。
