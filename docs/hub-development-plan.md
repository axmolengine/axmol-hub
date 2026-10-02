# Axmol Hub 研发计划

- **状态**：工作稿
- **日期**：2026-10-02
- **范围**：仅 Axmol Hub（不含 Axmol Editor 自身的开发计划；Editor 只在"边界"一节出现）
- **性质**：本会话讨论的收敛结果。数据与结论均来自对源码的核对，非复述 README
- **上游文档**：
  - `docs/adr/0001-hub-tech-stack-and-positioning.md` —— 技术栈与定位决策（已接受）
  - `docs/ai-first-plan.md` —— AI 能力层方案（P0–P5 明细、工具清单、护栏）
  - `docs/avalonia-migration-plan.md` —— WPF → Avalonia 分阶段迁移步骤
  - `docs/ci.md` —— 三平台 CI 与打包链路的设计与实测记录

> 本文档是**索引与排期**，不重复上述文档的论证。遇到分歧以 ADR-0001 为准。

---

## 0. 一句话

Hub 从「Windows 上跑得通的 WPF 工具」推进为「官方组织下的跨平台**环境与构建服务**」，把能力以 **CLI `--json` + MCP** 暴露，供 GUI、外部 agent、Axmol Editor 共用同一份实现。

---

## 1. 定位与边界

**Hub = 环境与构建服务**

| 归属 Hub | 归属 Axmol Editor |
|---|---|
| 引擎生命周期：下载 / 导入 / 校验 / 修复 / 卸载 | 场景 / 资源 / 动画编辑与预览 |
| 模块安装（按引擎版本勾选平台） | 内容创作工作流 |
| 工具链自持（固定版本，不复用系统工具） | 引擎内实时预览 |
| 项目创建、目标选择 | 自举 / dogfooding（Editor 自身即 Axmol 应用） |
| 构建 / 运行 / 部署 / 设备管理 | |
| **CLI `--json` 与 MCP 接口（单一定义）** | |

**明确不做**（沿用创始设想稿的排除项）：代码编辑器、Scene / Asset / Animation Editor、Git GUI、完整 Terminal、IDE 替代。

**硬约束（三条）**

1. **AI 能力层全局只有一份实现** —— Hub 与 Editor 不得各写一套
2. **Hub 是服务方，Editor 是客户端之一**（与 GUI / CLI / MCP 并列）
3. **Hub 不是 Axmol 自举应用** —— 自举与 dogfooding 由 Editor 承担，Hub 不必为此负责

---

## 2. 技术栈与架构

| 项 | 决定 |
|---|---|
| 语言 / 运行时 | **C# / .NET 8+** |
| UI | **WPF → Avalonia**（WPF 是 Windows 独占，两个候选分支里都必须换，故不是决策变量） |
| 分层 | `Core`（业务，唯一资产）/ `Cli`（薄派发）/ `App`（UI） |
| 新增项目 | `AxmolHub.Agent`（工具注册表 + `IChatClient` 管道）、`AxmolHub.Mcp`（MCP Server）。**两者都不引用 App**，保证可在 CI / 服务器 / 远程 agent 中运行 |
| 数据驱动 | 工具版本、URL、SHA-256、宿主声明全在 `manifests/`，代码内不散落版本号 |
| 三条贯穿原则 | 固定版本（绝不回退系统工具）、环境隔离（`ProcessRunner` 先 `Environment.Clear()`）、先校验再落地（staging + 凭据文件） |

**四客户端共享一份工具定义**

```
       AIFunction（单一定义）
              │
   ┌──────────┼──────────┬──────────┐
 GUI(Avalonia) CLI(--json) MCP  Editor
```

原理：MCP C# SDK 与 `Microsoft.Extensions.AI` 共用同一个 `AIFunction` 抽象 —— 同一份签名同时产出 MCP tool schema 与模型函数调用 schema，四处不会漂移。

---

## 3. 当前基线（起点）

| 项目 | 规模 | 目标框架 |
|---|---|---|
| `AxmolHub.Core` | 15 文件 / 1992 行 | `net8.0` |
| `AxmolHub.Cli` | 1 文件 / 118 行，**12 个动词** | `net8.0` |
| `AxmolHub.App` | 1457 行 C# + 99 行 XAML | `net8.0-windows`（WPF） |
| `tests/AxmolHub.Checks` | 583 行 / **102 处断言** | `net8.0-windows` |

CLI 动词：`targets` `verify` `create` `select` `plan` `configure` `build` `run` `serve` `devices` `deploy` `install-tools`。
已有约定：`plan` 输出 JSON；**stdout 出数据 / stderr 出日志**；`verify` 失败返回 exit code 2。

**完整度分层**

| 档 | 目标 | 状态 |
|---|---|---|
| 一 | **Windows x64** | 下载 → 构建 → 运行 → 产物校验，**闭环** |
| 二 | Android | 签名 / 校验链完整，真机未验 |
| 三 | WASM | 构建 + 本地预览跑通，浏览器场景未验 |
| 四 | macOS / iOS / tvOS / Linux | 仅 CMake 命令计划，`packages` 为空数组 |
| — | UWP / Xbox | 主动抛 `PlatformNotSupportedException` |

**可移植性**

- **可移植**：Core 大部分、102 项行为检查、`manifests/` 数据、CLI 设计（`--json` / stdout-stderr 分离 / exit code 语义）、踩坑清单
- **唯一不可移植**：**已验证的 Windows 端到端运行证据**

> 102 项行为检查描述的是**需求**而非实现，换任何技术栈都能当验收清单用 —— 这是仓库里最值得先抢救的资产。

---

## 4. 工作项

### A. 地基与阻塞项（优先级最高）

| # | 工作项 | 说明 |
|---|---|---|
| **A1** | **解多引擎版本绑定** | `module-manifest.json` 只有 `2.11.5` 一个 profile；Android 打包硬编码 `engine.Version != "2.11.5"` 即拒绝。**v3 发布当天变成阻塞**，改动小，应最先做 |
| **A2** | **v3 目录结构 / 构建系统耦合** | v3 若改目录或构建系统，`ValidateEngine` 的 4 个标志文件与 `manifests/` 需同步跟进 |
| **A3** | **P0：CLI 统一 `--json` 契约** | 统一 `{ok, exitCode, data, log, artifacts}`，替换目前"多数命令输出文本"的状态 |
| **A4** | **P1：MCP Server（stdio）** | 先用 Cursor / Claude Code 驱动 Hub 验证价值，再决定投入 UI |

> A3 / A4 在定位决策后**优先级上升**：它们不再只是"为 AI 做的结构化输出"，而是 **Hub 与 Editor 之间的正式接口**，应排在任何 UI 工作之前，且与 UI 栈无关。

### B. UI 迁移

| # | 工作项 | 说明 |
|---|---|---|
| **B1** | 新建 `AxmolHub.App.Avalonia`，引用现有 Core，**Core 一行不改** | 先搬项目页 / 引擎页 / 工具链页，跑通 Windows，验证迁移路径是否成立 |
| **B2** | 主窗口形态 | 现为**单窗口 + 四页导航**；创始设想为**三栏 docking + 右侧常驻 AI 面板**。**建议与 B1 合并做**，否则 AI 面板没有自然落点，先塞一页会返工 |

### C. AI 能力层

分期明细见 `docs/ai-first-plan.md` §6，此处只列骨架：

| 阶段 | 产出 |
|---|---|
| P0 | CLI 全命令 `--json` |
| P1 | MCP Server（stdio） |
| P2 | `ProjectDigest` + 版本键控 `EngineIndex`（解析 `core/`、`extensions/` 头文件成符号表） |
| P3 | 项目页内嵌聊天面板：流式 + 工具调用卡片 + diff 预览 |
| P4 | 构建失败 → 问 AI → 改码 → **真实重建**（闭环） |
| P5 | 场景化 Skills（新建场景 / 物理 / 打包 / Lua 绑定） |

**两条排期纪律**

1. **P0–P2 是地基，不要跳过。**
2. **反对先做聊天框。** 专属感的来源不在聊天框（那是最容易被复制的一层），而在这三件事：知识来自**磁盘上已安装的引擎源码**（零幻觉）、能调用 Hub **私有的固定工具链**（可复现）、能**真实构建并拿到编译器原文**。

### D. 跨平台后端与分发

#### D-1 宿主后端

| # | 工作项 | 说明 |
|---|---|---|
| **D1** | macOS 宿主后端 | `ToolchainDetector` 的 macOS 版 + 对应的**工具链安装器**。**从零写、从零验证** |
| **D2** | Linux 宿主后端 | 同上 |

#### D-2 打包与签名（**安装器栈必须跨平台**）

| # | 工作项 | 说明 |
|---|---|---|
| **D3** | ~~打包栈替换为跨平台方案~~ **已完成 2026-10-02** | 已从 Inno Setup 换成 **Velopack**。触发原因不只是"Inno 是 Windows 独占"：Inno **自 6.7 起商业使用需付费许可**，而仓库固定的正好是 6.7.3，第三方声明里却放着 6.7 之前的旧条款。详见 `docs/ci.md` §6 |
| **D4** | 自动更新 | Velopack 自带：更新源是静态文件（`releases.<channel>.json`），GitHub Releases 直接可用，不用自建服务端。**能力已具备，但 UI 侧的更新检查与入口尚未接入**，属独立工作项 |
| **D5** | 签名与公证 | **Windows：决定申请 SignPath Foundation 的免费 OSS 代码签名**（细则见下）。**macOS：公证仍需付费的 Apple Developer 账号（$99/年），SignPath 不覆盖**；Linux：一般无需签名 |

**Velopack 选型（已定）**

- `vpk` 一套命令产出三平台产物；自带自动更新与增量包；Rust 核心 + .NET CLI + C# 客户端库，与 Hub 栈契合；本地化 39 种语言
- 版本与摘要照旧锁定：`installer/packaging-manifest.json` 固定 `vpk` **1.2.161** + SHA-256，`--prepare-packaging` 校验后装进 `artifacts/packaging-tools`，不改 PATH
- **代价：这是仓库第一个 `PackageReference`**（App 项目引用 `Velopack` 客户端库，用于拦截安装/更新/卸载钩子）。冷构建因此不再完全离线，且它要进第三方声明。**Core 与 Cli 保持零依赖**，"CLI 与 Core 仍可离线构建"这个性质保得住
- **两处旧描述有误，已更正**：① Linux 产物**只有 `.AppImage`**，没有 `.deb` / `.rpm` —— Velopack 明确不做传统包管理格式，理由是 AppImage 才能一次构建跨发行版；② 此前引用的"健康度 43/100"其实是它的 **Security 子分**，综合指数是 **84/100（Excellent）**（Vitality 90），采购判断因此反了过来
- **新发现的连带改动（必须做）**：Velopack 更新时整体替换安装目录下的 `current\`，卸载时删除整个安装目录 → **Hub 的设置与数据根必须移出安装目录**，落到 `%LocalAppData%\AxmolHub\`。这条不是风格选择：不移就等于每次升级都清掉用户的引擎与工具链（GB 级）
- 代价：Windows 安装器变成一键式，**失去安装目录选择页与安装语言选择**；桌面快捷方式固定为不创建（Inno 里它是默认不勾选的选项）
- 仍未用上的能力：**增量包**需要 `--outputDir` 里保留上一版产物，当前每次清空发布目录，因此只有同一目录留多版本时才会产出

> **与 §5 排序的关系**：D3 原本排在第 11 步（D1/D2 之后）。现在提前做了，因为 Inno 的许可问题**在入仓阶段就已经是阻塞项**（正式发行任何二进制前必须先解决），而它与 UI 迁移无关。D1/D2 与 D5 的 macOS 公证不受影响，仍在原位执行。


#### D5 细则：SignPath Foundation 免费代码签名（**已决定申请**）

**为什么用它**：Windows 上未签名的安装器会触发 SmartScreen 警告，这是直接的支持负担。商业 OV 证书约两百美元/年且需身份核验；**SignPath Foundation 对 OSS 项目免费提供 OV 级证书**（证书签发给 SignPath Foundation 而非项目，私钥存于其 HSM），并提供 GitHub Actions 集成。

**门槛（逐条对照本仓库）**

| 条件 | 本仓库状态 |
|---|---|
| OSI 许可，且**所有组件都无商业双许可** | ✅ MIT —— 换掉 Inno 6.7 之后，第三方组件里已无带商业许可条件的项。**这正是 D3 换栈的一个附加收益** |
| 不含专有/闭源组件 | ✅ 自有代码全 MIT；引擎与微软/Android/Apple SDK 不随仓库分发 |
| 项目活跃维护 | ✅ |
| **已按待签名的形态发布过** | ❌ **未满足** —— 仓库至今没有任何已发布、可下载的二进制 |
| 功能在下载页有说明 | ⚠️ 部分满足：README 有说明，但没有 Release 页/下载页 |
| 在主页与下载页公布 **Code signing policy** | ❌ 未做 |
| 指定 Authors / Reviewers / Approvers 且全部启用 MFA | ❌ 未做（应与 CLA / CODEOWNERS 同批） |

**义务（是接受约束，不是纯好处）**

- 证书的**发布者是 SignPath Foundation**：它们定义并执行技术约束，违反其行为准则时可暂停订阅、**立即或追溯吊销证书**
- **每一次签名请求都要人工批准** —— 发布链路里始终有一个人工点击，无法完全自动化
- 只能签**自己仓库构建**的产物；上游库要签名得请上游自己申请
- 未来可能被要求提供 SBOM 等额外最佳实践

**申请顺序（有硬前置）**：① 做出第一个可下载的 Windows 发布（GitHub Release）→ ② 公布 Code signing policy 与三人角色 → ③ 提申请。审核无公布 SLA，社区经验为**数天到数周**。

**集成方式不是一条命令（推断，待落地实测）**：`vpk` 的 `--signTemplate` / `--signParams` 是**打包过程中逐文件同步调用**的签名命令，而 SignPath 是**异步 + 逐次人工批准**的服务，两者节奏不同。实际形态应是**分两次签**：先签 publish 输出里的应用二进制，再 `vpk pack`，最后签 `Setup.exe`。在没有签名凭据之前，`release-windows.yml` 保持不签名，CI 不因缺少凭据而变红。

> **D1/D2 的工作量与 UI 迁移同量级。不要以为换了 Avalonia 就跨平台了** —— Avalonia 只解决 UI 层，D1/D2 在当前代码里完全不存在。

### E. 工程化与发布

| # | 工作项 | 说明 |
|---|---|---|
| **E1** | 102 项行为检查接 CI | 不依赖任何测试框架，接入近乎零成本，官方仓库必须要有 |
| **E2** | README 顶部标注 **Windows-first** 初始范围 | 否则 macOS / Linux 用户会直接报"跑不起来" |
| **E3** | 代码签名 | **已并入 D5**（签名属分发环节；跨平台后需同时处理 Windows 签名与 macOS 公证） |
| **E4** | ~~`THIRD_PARTY_NOTICES.md` 补 Inno 许可说明~~ **已完成 2026-10-02** | Inno 已整体移除（含 `licenses/Inno-Setup.txt`），第三方声明改为 Velopack（MIT）+ `licenses/Velopack.txt`。原先那句悬置的"请检查商业使用要求"随之消失 |

---

## 5. 实施顺序

`ADR-0001 §8` 的展开（原文第 8 步含三项，此处拆开）：

| 步 | 工作项 | 对应 |
|---|---|---|
| 1 | `git init`，**以原作者身份**提交首个 commit | 当前目录无 git 历史，这是保留 authorship 的唯一机会 |
| 2 | LICENSE 改 Axmol house style + 新建 `AUTHORS.md` | 用 `(see AUTHORS.md)` 承载原作者归属 |
| 3 | `CONTRIBUTING.md`（指向 CLA）、`CODE_OF_CONDUCT.md`、`SECURITY.md`、PR / Issue 模板 | |
| 4 | 102 项检查接 CI | E1 |
| 5 | README 标注 Windows-first | E2 |
| 6 | **解多引擎版本绑定** | A1 |
| 7 | **Avalonia 迁移（Core 一行不改）** + 主窗口形态 | B1 / B2 |
| 8 | CLI 统一 `--json` 契约 | A3（P0） |
| 9 | MCP Server | A4（P1） |
| 10 | macOS / Linux 宿主后端 | D1 / D2 |
| 11 | ~~跨平台打包栈~~ **签名与公证（含自动更新接入）** | **D3 已完成**（Velopack）；D4 的更新入口与 D5 的签名仍在本步 |
| 12 | `ProjectDigest` + `EngineIndex` → 聊天面板 → 闭环修复 → Skills | C 的 P2–P5 |

**排序理由（四条关键）**

- **第 6 步的时间窗最窄** —— v3 一发布立刻阻塞
- **第 7 步风险最高** —— 它验证的是"Core 到底要不要动"这个前提，越早证伪越省事
- **第 8–9 步是 Editor 能否安全接入的前提** —— 边界契约必须先有接口才能定
- **第 11 步必须排在第 10 步之后** —— 没有 macOS / Linux 的可构建产物，就没有东西可打包；而它一旦完成，Windows 侧也顺带获得自动更新

> 第 1–5 步同时是"进官方仓库"的入场券，与功能开发可以并行。

---

## 6. 待决项（开工前需要答案）

| # | 议题 | 影响面 | 备注 |
|---|---|---|---|
| 1 | **项目内写入策略**：`.axmol-hub.json` 保留 / 改回零写入 | `EngineIndex`、模块状态、图形后端注入都依赖它；也决定 UI 迁移时要不要动 Core | 牵动最多，**建议最先定** |
| 2 | 主窗口形态：四页导航 / 三栏 docking | 决定 AI 面板落点 | 与 B1 合并做 |
| 3 | Graphics backend 建模 | `BuildTarget` 当前无图形后端字段，创始设想的上下文项无法满足 | 它是配置期 / 运行期选择，还随宿主平台变，需先建模 |
| 4 | **Hub ↔ Editor 能力契约** | 决定 A3 / A4 的接口形状 | 需先问清 Editor 的 5 件事（`ADR-0001 §6`），否则边界只能靠猜 |

---

## 7. 风险登记

| 风险 | 等级 | 说明 |
|---|---|---|
| **Hub ↔ Editor 范围冲突** | **高** | 两者都会想做引擎管理、项目状态、工具链、构建运行、AI；由同一人的同一套 AI 流程产出、互不知情 → 会产生"两个半成品 Hub"。**只能靠契约约束，不能靠约定俗成** |
| Core 里的 Windows 分支 | 中高 | 是**实质技术债**（`ToolchainDetector` 硬编码 `bin/Hostx64/x64/cl.exe`；`WindowsToolchainInstaller` 整文件 Windows 专属），非仅代码风格。macOS / Linux 需新增对应 detector + installer |
| 组织新增 C# 维护责任 | 中 | `axmolengine` 此前只有 C++。**必须指定维护者**，否则成为孤儿代码 |
| 单贡献者容量 | 中 | 同一人同时维护 Hub 与 Editor，属容量风险而非技术风险 |
| **SignPath 的角色要求撞上单维护者** | 中 | 其行为准则要求指定 **Authors / Reviewers / Approvers** 三个角色且全部启用 MFA —— 单一维护者可以一人兼三角，但"每次签名请求需一名受团队信任的成员批准"意味着**发布链路里始终有一个人工步骤，且这个人不能出事**。与 CLA/CODEOWNERS 同批处理 |
| v3 耦合 | 中 | 目录结构 / 构建系统变更会打断 `ValidateEngine` 与 `manifests/` |
| App 层 code-behind 风格 | 低 | `MainWindow` 823 行，非严格 MVVM。规模小时无妨，但与 Core 的严谨度有反差；**迁移时是重构窗口** |

---

## 8. 范围说明

| 事项 | 状态 |
|---|---|
| 贡献者补偿 / 赞助政策 | **不在计划内** —— 用户决定暂不制定；仓库内不放相关内容 |
| **安装器跨平台化** | **已完成 2026-10-02**，见 §4 D3。提前于 §5 的第 11 步执行：Inno 的许可问题在入仓阶段就已是阻塞项，且与 UI 迁移无关 |
| **用 NSIS 替代 Inno** | **已评估并排除。** NSIS 同样是 Windows 独占（`makensis` 仍是 32 位），许可也更杂（zlib/libpng + bzip2 + CPL-1.0 + LGPL-2.1，需向 `THIRD_PARTY_NOTICES.md` 补的条目反而比现在多）。它是"换一个 Windows 装器"，不是跨平台方案。D3 最终选的是 Velopack，此条保留作为"考虑过、为何不选"的记录 |
| CLA / 许可证法律细节 | **不在计划内** —— 属入场流程，另处理。唯一现在生效的约束：**CLA 不得掺入对价** |
| Axmol Editor 的开发计划 | **不在本文档范围** |
