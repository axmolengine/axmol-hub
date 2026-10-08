# ADR-0003：模型提供商密钥的 Linux 存储后端与验证路径

- **状态**：已接受（密钥环档为"已决定、待落地"，见 §6）
- **日期**：2026-10-07
- **决策人**：Axmol 作者（`axmolengine` 组织维护者）
- **相关**：`src/AxmolHub.Core/ISecretStore.cs`、`src/AxmolHub.Agent/SecretStoreFactory.cs`、`docs/ci.md` §2.1–2.2、`.agents/memory/topics/ai-module.md` §8
- **命名注记**：正文里的 `AxmolHub.App` 是写作当时的 GUI 项目名；2026-10-08 该项目连目录、程序集与产物一并改名为 `AxmolHub`，记录按原样保留。

---

## 1. 背景

Hub 对外是三平台产品（`AxmolHub.App` 为 `net8.0`、RIDs 含 `linux-x64`，CI 的 verify 矩阵在 Windows / macOS / Linux 上都构建 GUI），但**鉴权这条路径在 Linux 上是整条断的**，而且断得很安静：

- `SecretStoreFactory.Create()` 在非 Windows 上 `throw PlatformNotSupportedException`（原第 19 行的 TODO 写着 macOS Keychain / Linux Secret Service）
- `ChatWorkspace` 捕获后把 `_secrets` 置空，`CanStoreSecrets` 变 false，于是"添加凭据 / OAuth 登录 / 校验密钥 / 轮换密钥"五处闸门全部静默返回 null 或 Unsupported
- 设置页据此硬拦并打印"当前平台尚不支持安全保存密钥（macOS/Linux 待补）"
- 即便绕过硬拦，OAuth 也只能走"把 URL 复制到浏览器"的手动兜底：`TryOpenBrowser` 只有 Windows 与 macOS 分支，Linux 直接 `return false`

净结果：Linux 用户只能用免密钥的 Ollama，"模型提供商"卡片整体不可用；而 `docs/` 与安装产物都在说三平台。

约束条件（都是既有的、不是本次新加的）：

1. **绝不明文落盘**。`ISecretStore` 的注释与 `CredentialStore` 的 `[JsonIgnore] Secret` 一起构成这条承诺，并且工厂**故意**选择抛异常，而不是"先写个文件再说"
2. **Core 零 NuGet**，可离线冷构建；AI 与 OS 凭据 API 的依赖只进 Agent
3. `AxmolHub.Cli` 只引用 Core（把 Agent 拉进 CLI 就等于把 `Microsoft.Extensions.AI` 塞进自包含发布产物）
4. `tests/AxmolHub.Checks` 是 **`net8.0-windows`**（Windows 特有断言需要它），因此 Windows 之外的行为检查进不了它
5. `docs/ci.md` §2.2 明确否决 xvfb：**"在 CI 从未真实跑过之前不引入这类易红步骤"**

## 2. 决策

**Linux 采用"OS 凭据服务优先 + 本机加密文件回退"的分层，任何一层都不写明文；后端由存储自己声明，界面据实说它是哪一档。**

| 档 | 后端 | 现在 | 防住 | 防不住 |
|---|---|---|---|---|
| Windows | DPAPI `CurrentUser` | 已实现 | 其他用户、data 目录被整机拷走 | **同用户进程**（`ProtectedData.Unprotect` 只是一个调用） |
| Linux A | freedesktop Secret Service（gnome-keyring / KWallet，经 D-Bus） | **待落地**（§6） | 其他用户、data 目录被拷走 | 同用户进程（与 DPAPI 同一水位） |
| Linux B | AES-256-GCM blob + 独立数据密钥（0600，且在 data root 之外） | **已实现** | 其他用户、误入日志/转录、只拷 data 目录 | 整个 home 被拷走、同用户进程 |
| Linux C | `HUB_SECRET_KEY_FILE` 只改**密钥文件位置** | **已实现**（B 的密钥来源） | 同 B | 同 B |
| macOS | 无 | 继续抛异常 | — | — |

配套决定：

1. **blob 自描述**：magic `AXSK` + 版本字节 + nonce + tag，AAD 绑 `版本 ‖ provider id`。DPAPI 存量 blob 不加头（加了会毁掉所有 Windows 安装的既有密钥），改为**按形状识别**
2. **数据密钥放在 `~/.config/AxmolHub/`（`SpecialFolder.ApplicationData`），不放 data root**。data root 是会被搬动、备份、`--data-root ./data` 指来指去的那个目录；密钥跟着它走，"拷目录"就变成"拷凭据"
3. **`ISecretStore` 增加默认接口成员 `Descriptor`**（`Kind` / `Location` / `DegradedReason`），不做 `Available` 探针、不改 `CanStoreSecrets` 签名 —— 六处闸门原样生效
4. **读不出来返回 null 并记录原因**，不抛异常：`CredentialStore.Load()` 在应用启动路径上重水合每一条凭据，一处抛异常会让整个 provider 列表打不开
5. **macOS 不顺带走文件档**：它的正规后端是 Keychain，用文件冒充会把"OS 凭据存储"这个承诺说虚；`SecretStoreResolver` 保持平台中性，接 Keychain 时是加分支而不是重写
6. **Linux 浏览器启动走 `$BROWSER → xdg-open → gio open`，`UseShellExecute = false`**；打不开仍回落到"显示链接"。OAuth 增加**粘贴回调**兜底（WSL2 / 容器里浏览器在宿主机，回调永远打不到 Hub 的回环端口）

## 3. 为什么"加密文件"不违反第 1 条承诺

原注释拒绝的是**明文回退**，不是**次强回退**。文件档里密钥是 AES-256-GCM 密文，`credentials.json` 因为 `[JsonIgnore]` 从来不含密钥，日志侧 `SensitiveValues()` 照旧把命中串替换成 `[REDACTED]`。

真正的差别是**威胁模型**，所以决定不是"要不要说"，而是"必须说"：`ApiKeyHint` 原来写"密钥保存在操作系统的凭据存储中"，在文件档上是个错话，已改成只声明两档都成立的事实；卡片下方单独一行只在**文件档**时出现（DPAPI 是既有承诺，不再重复声明），并有 `--check-secrets` 打印 `backend=encryptedfile` 作为机器可读的落点证据。

## 4. 被拒的方案

- **明文或固定密钥回退** —— 破坏 §1 约束 1，工厂宁可抛
- **`/etc/machine-id` (+ uid) 派生数据密钥** —— `machine-id` 全局可读，机器上任何进程都能推出密钥，这是**混淆不是保护**；且重装系统会静默毁掉全部凭据且无从诊断
- **P/Invoke `libsecret-1.so.0`** —— 连带要 marshal glib 的 `GHashTable` / `GError` / 变参，而 KDE 环境常常不装 libsecret，覆盖面反而比 D-Bus 直连更窄
- **手写最小 D-Bus 客户端** —— 数百行安全相关的编组与 SASL 握手，换来的是"少一个 MIT 包"
- **把 `AxmolHub.Checks` 改成多目标框架 / 或让 CLI 引用 Agent** —— 前者要为 Linux 断言拆掉 `-windows` TFM 并给既有 Windows 断言加 `#if`；后者违反约束 3。改用 App 的**无头自检**（约束 4/5 都不碰）
- **xvfb 跑 GUI 断言** —— 违反约束 5，且鉴权这条路径不需要窗口也能验证到底
- **macOS 顺带放开文件档** —— 见 §2 第 5 条；本次边界只做 Linux

## 5. 验证（已做到的部分）

三处互补，各覆盖另两边到不了的地方：

1. **`AxmolHub.App --check-secrets`**（新增）：在 `Program.Main` 里、Avalonia 启动之前返回，所以不需要显示服务器。跑**生产入口** `SecretStoreFactory.Create()`：写→读回→篡改一字节必须读不出→`credentials.json` 不含密钥→（文件档）数据密钥 0600 且由 `HUB_SECRET_KEY_FILE` 决定位置→`$BROWSER` 被以 exec 方式调用。macOS 走 `backend=none` 的 PASS 分支。
   **实测**：Windows `backend=dpapi` 8 PASS；WSL2 Ubuntu-24.04（.NET 8.0.31，框架依赖产物）`backend=encryptedfile` 12 PASS，含 0600 与浏览器启动两条只能在真 Unix 上断的。
   **兼容实测**：仓库 `data/ai/secrets/` 里重构**之前**由旧代码写下的 4 个 DPAPI blob（`cred-*.bin` 与 `orcarouter.bin`，262/278 字节），用改造后的 `DpapiSecretStore` 逐个读回 35/51 字符的密钥，`CredentialStore.Load()` 两条凭据全部带密钥 —— 目录、文件名、熵值与原子写没有因为逻辑抽离而变位；同一批 blob 的形状也被 `Classify` 认成 `DpapiLikely`（真字节，不是自造样本）。
2. **`--check-secret-store`**（`AxmolHub.Checks`，52 条 PASS）：纯函数与注入式假传输，所以在 Windows 上就能断到 Linux 分支 —— AAD 绑定、外来 DPAPI blob 识别（**含用本机 `ProtectedData` 真产出的 blob**，不是只断自造的 `01 00 00 00` 形状）、版本字节、路径净化（`../../evil`、`/etc/passwd` 出不了 secrets 目录）、`ResolvePath` 优先级、后端选择表（含"macOS 无档"）、`ReadCallback` 的 state 先于 code、`$BROWSER` 参数表。
3. **`--verify-shell`**：5 条中文断言，覆盖"闸门不再拦"和"后端那一行的**规则**"。规则断言写成 `显示与否 == (档 == 加密文件)`，因此宿主无关。
   **反向对照已做**：把 DPAPI 也改成显示 → 该行断言与"卡片间不插额外控件"断言同时 FAIL（3 处），随后还原。

CI：`build.yml` 的 verify 矩阵新增 `Verify secret store on this host`（三平台，linux job 还钉住 `backend=encryptedfile`）与 Windows 侧的 `Verify secret store contract`。

## 6. 尚未做到的部分（必须如实记录）

**密钥环档（A）没有实现，也没有本地证据。** 本机 WSL2 Ubuntu-24.04 有会话总线（`/run/user/1000/bus`）、有 `xdg-open` / `gio` / `dbus-run-session`，但 `org.freedesktop.secrets` 的 `NameHasOwner` 返回 **False**，且没有 gnome-keyring / kwalletd 可装（装它需要改用户的 WSL 环境）。GitHub runner 同理：CI 的 Linux job 永远只会走文件档。

因此约定：**A 档落地时必须是独立提交，并至少具备**

1. 一个只读采纳闸门 —— 只有当 `Open` + `SearchItems` 真跑通才选 A 档（这一步顺带验过 variant 的读写编组，编组错误的实现会在闸门处自动降级到 B 档而不是接管的密钥写入）
2. 假传输断言进 `--check-secret-store`（属性命名、锁定/无提供者可操作提示、`Prompt` 不当作成功、错误映射）
3. **一次真实 GNOME 桌面手测**（解锁态 / 锁定态 / 无守护进程态三况），并把结果写进本文档
4. `HubTexts` 增加 `AuthSecretBackendKeyring` 一行；descriptor 的 `SecretStoreKind.SecretService` 分支进工厂

在 1–4 齐全之前，Linux 一律用 B 档 —— 它是唯一有真机证据的一档。

## 7. 后果

- Linux 用户现在能粘贴密钥、能跑完浏览器登录（含 WSL 粘贴兜底），能校验密钥与刷新模型列表
- `docs/ci.md` 里"Core 里唯一按操作系统分叉的逻辑就是 `BuildTargets.Host`"与"这是唯一能在三平台都跑起来的行为检查"两句**不再成立**，已改正
- 数据密钥丢失 = 已存凭据不可恢复，表现为"未鉴权"加一行原因；不猜测恢复、不自动删除密文
- Windows 存量安装不受影响：DPAPI 的目录、文件名、熵值与原子写全部保持原样（逻辑抽到 `SecretBlobFiles` 后行为逐字对齐，并由 `--check-secrets` 在 Windows 上真跑一遍）
