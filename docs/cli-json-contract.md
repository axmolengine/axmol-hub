# CLI `--json` 契约（P0）

> 状态：**已定稿并已实现**（2026-10-02）。这是 `ADR-0001` 里 Hub 对外暴露的正式接口之一，
> 与 MCP 并列。Hub ↔ Editor 之间、以及脚本 / CI 与 Hub 之间都按这份文档通信。
>
> 实现：`src/AxmolHub.Core/CliContract.cs`（契约类型与序列化）+ `src/AxmolHub.Cli/Program.cs`（12 个动词接线）。
>
> 相关：`docs/ai-first-plan.md` §1（工具只定义一次，三种/四种客户端共享）、§6（P0 是地基）、
> `docs/ci.md` §2.6（34 条端到端断言的执行方式）。

## 1. 为什么需要它

现状是**每个动词各写各的**：`plan` 输出 JSON，`targets`/`verify`/`devices` 输出给人看的文本，
`configure`/`build` 输出一句英文，失败时异常栈直接写 stderr 并返回 1。

后果是**任何自动化都拿不到结构化结果**，更糟的是**失败无法解析**：脚本能解析成功路径的文本，
却必须去猜 stderr 的格式才能知道为什么失败。所以契约的核心不只是"成功时给 JSON"，
而是**失败时同样给 JSON**。

## 2. 信封（envelope）

`--json` 是**全局标志**，可出现在参数列表的任意位置，**所有 12 个动词都接受**。

成功：

```json
{
  "schema": 2,
  "command": "verify",
  "ok": true,
  "exitCode": 0,
  "data": { }
}
```

失败：

```json
{
  "schema": 2,
  "command": "build",
  "ok": false,
  "exitCode": 1,
  "error": {
    "type": "InvalidDataException",
    "message": "Project must contain CMakeLists.txt."
  }
}
```

| 字段 | 含义 |
|---|---|
| `schema` | 整数，**破坏性变更时递增**。消费方应先校验它再解析 `data` |
| `command` | 动词名（`targets` / `verify` / …） |
| `ok` | **这次调用是否正常完成**（见 §5 的例外） |
| `exitCode` | 进程退出码，与不带 `--json` 时**完全一致** |
| `data` | 动词各自的载荷；`ok:false` 且是异常失败时**不存在** |
| `error` | 仅在异常失败时出现：`type`（异常类型名）+ `message` |

## 3. 三条硬规则

1. **`--json` 模式下 stdout 有且只有一份 JSON 文档，别的什么都没有。**
   原本打到 stdout 的结果行（`targets` / `verify` / `devices` / `create` / `select` 的文本）
   在 JSON 模式下**一律不再输出**。
   日志仍然走 stderr（这条是既有约定，不变）。
   理由：stdout 混进一行人类文本，严格解析器就直接失败。
2. **退出码不变。** JSON 是**新增**的通道，不是替换退出码语义：

   | 退出码 | 含义 |
   |---|---|
   | `0` | 成功 |
   | `1` | 一般失败（异常） |
   | `2` | **仅 `verify`**：存在 Missing / Broken 的组件 |
   | `130` | 被 Ctrl-C 取消 |

3. **失败也给 JSON。** 抛异常时 `ok:false` + `error`，且退出码照旧。
   消费方**永远不需要去解析 stderr**。

## 4. 各动词的 `data` 形状

| 动词 | `data` |
|---|---|
| `targets` | `{ "targets": [ { id, name, family, architecture, hosts[], simulator, current } ] }` |
| `verify <root> <target>` | `{ "target": "...", "components": [ { name, status, details, executable } ] }` |
| `create <root> <name> <parent> [cpp\|lua]` | `{ "project": { …ProjectEntry… } }` |
| `select <root> <project> <target>` | `{ "project": { …ProjectEntry… } }`（已含新平台） |
| `plan <root> <project> [cfg]` | `{ "plan": { "subCommand": "build", "arguments": [ … ] } }` |
| `configure` / `build` | `{ "project": {…}, "status": "Configured"\|"Succeeded", "executable": "…"\|null }` |
| `run` / `serve` / `deploy` | `{ "exitCode": <子进程退出码> }` |
| `devices <root> <target>` | `{ "devices": [ { serial, state, details } ] }` |
| `install-tools <root> [platform]` | `{ "engine": "…", "platform": "…", "outcome": "…", "exitCode": 0 }` |
| `help` | `{ "commands": [ … ] }` |

`…ProjectEntry…` = `{ name, path, version, channel, platform, configuration, projectType, lastOpened, buildStatus }`。
`plan` 的载荷是**引擎 cmdline 调用**（`subCommand` + `arguments`）：构建已委派给 `axmol build`，
不是 Hub 自己拼的 CMake 命令计划 —— 见 docs/adr/0002。

## 5. 两个必须说清的例外

### 5.1 `verify`：失败是**数据**，不是异常

`verify` 的"有组件缺失"**不是**异常，而是一次成功的查询得到了坏结果。因此：

```json
{ "schema": 1, "command": "verify", "ok": false, "exitCode": 2,
  "data": { "target": "windows-x64", "components": [ … ] } }
```

`ok:false` 且 `exitCode:2`，但 **`data` 仍然存在** —— 缺失清单正是调用方要的东西。
不要把它当成"没有结果"。

### 5.2 `run` / `serve` / `deploy`：`ok` 与 `exitCode` 可以不同向

这三个动词的退出码**故意就是被拉起程序的退出码**（既有行为），所以可能出现：

```json
{ "schema": 1, "command": "run", "ok": true, "exitCode": 3, "data": { "exitCode": 3 } }
```

含义是：**Hub 正常完成了它的工作**（`ok:true`）——
拉起、运行、等它退出；而被拉起的程序自己返回了 3。
判"Hub 有没有干好"看 `ok`，判"那个程序什么结果"看 `exitCode` / `data.exitCode`。

## 6. 一处刻意保留的不对称

**`plan` 的裸 JSON 形状已在 `schema 2` 退役。** 不带 `--json` 时它和别的动词一样输出人读文本
（一行「平台 · 配置」+ 一行等价的 `axmol <subcmd> args`），不再往 stdout 打一份裸 JSON。

这条不对称是 `schema 1` 时期的遗留（早期 `plan` 直接序列化 `PlatformBuildPlan`）。
退役它的同时，载荷也从「Hub 自拼的 CMake 命令计划」换成了引擎 cmdline 调用 —— 同一处破坏性变更，
一并进 `schema 2`。

## 7. 兼容性与实现位置的约定

- **形状只在 `schema` 递增时破坏。** 新增字段不算破坏（消费方应忽略未知字段）。
- **实现位置**：信封类型与序列化放在 `AxmolHub.Core`（宿主无关、零新依赖），
  **不放在 `AxmolHub.Cli`**。理由就是 `docs/ai-first-plan.md` §1.1 的"工具只定义一次"：
  P1 的 MCP Server 要序列化**同一批载荷类型**，契约类型放 Core 才不会出现两份会漂移的定义。
- **不新增 NuGet 依赖**：`System.Text.Json` 在框架内，Core 的零依赖性质（可离线冷构建）必须保持。
- **`--json` 不改变任何业务行为**：不加这个标志时，除了 §6 说明的那处，行为逐字节不变。

## 8. 这份契约是怎么被验证的

**不是靠"写完看一眼输出"。** 契约里最容易错的一条是规则 §3.1 —— "stdout 里恰好只有一份 JSON 文档"，
而它**在开发过程中真的错过一次**：`help` 分支忘了判 `asJson`，于是 stdout 变成"help 文本 + 信封"。
当时的序列化形状完全正确，任何"把对象序列化后检查字段名"的断言都是绿的。

因此验证方式是**端到端**的，落在 `tests/AxmolHub.Checks` 的 `--check-cli-json` 模式里，
共 34 条断言，判据是"整段 stdout 必须被 `JsonDocument.Parse` 吃下"。本机跑：

```bash
dotnet run --project tests/AxmolHub.Checks -c Release -- \
  artifacts/checks --check-cli-json src/AxmolHub.Cli/bin/Release/net8.0/AxmolHub.Cli.dll
```

CI 里同样是这一条命令（`docs/ci.md` §2.6）。它**自带 `return`**，在触碰引擎/工具链之前就退出，
所以不需要 data root 与真实引擎 —— 这是它能进 CI 而主流程 105 条不能的原因。

关键覆盖点（详见 `docs/ci.md` §2.6 的表）：信封基线形状、`--json` 位置无关、
**递归断言无 PascalCase 成员**、`help --json` 只出信封、失败仍出 JSON、
`verify` 在缺失组件时 `ok:false` + `exitCode:2` 但 `data` 仍在、人读模式不被污染。

**并且做过负向对照**：故意去掉 `help` 的 `!asJson` 守卫后重编译，断言确实失败并打印出 stdout 原文；
恢复后回到 34/34。没有这一步，"全绿"无法与"断言永不失败"区分开。
