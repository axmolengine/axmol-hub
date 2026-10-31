namespace AxmolHub.Core;

/// <summary>
/// The **single definition** of Hub's UI copy.
///
/// Why it lives in Core rather than in one app: during the migration the WPF and Avalonia builds
/// **coexisted** (WPF was only removed at P6), and both sides needed localising. Copying the 190 key/value
/// pairs into two files would let Chinese/English silently fork during the migration — precisely the period
/// when tweaking copy is most tempting. Same reasoning as ADR-0001's "define a tool once": the data goes in
/// Core and each client keeps only a thin adapter (WPF writes Application.Resources, Avalonia writes an
/// Avalonia resource dictionary); neither side owns the copy itself.
///
/// This holds **only data and lookups**, with no UI framework types, so Core's zero-dependency,
/// offline cold-build properties are unaffected. The copy is framework-agnostic; the framework only
/// decides "how it gets fed into a resource dictionary".
/// </summary>
public static class HubTexts
{
    public const string ChineseLanguage = "zh-CN";
    public const string EnglishLanguage = "en-US";

    /// <summary>
    /// The language used when nothing is set, or the settings hold an unrecognised value.
    ///
    /// **Changed from <c>zh-CN</c> to <c>en-US</c> on 2026-10-03**: when the starting language is Chinese,
    /// a Linux without CJK fonts (the default on a minimal Ubuntu install) renders the whole interface as
    /// blanks/boxes, and the user cannot even "switch to English in Settings" — the entry in the language
    /// dropdown on the Settings page is itself written in Chinese.
    /// English displays on every system, so it is the cold-start fallback.
    ///
    /// Note that it is **not the same as "Chinese"**. To test "is the current language Chinese" use
    /// <see cref="ChineseLanguage"/>: these two constants used to hold the same value, so mixing them did
    /// not matter; now their values differ, and mixing them puts hard-coded English copy on a Chinese interface.
    /// </summary>
    public const string DefaultLanguage = EnglishLanguage;

    /// <summary>Unknown languages fall back to <see cref="DefaultLanguage"/> rather than throwing: the language comes from the settings file, i.e. user data.</summary>
    public static string Normalize(string? language)
        => language == ChineseLanguage ? ChineseLanguage : DefaultLanguage;

    public static bool IsSupported(string? language) => language is ChineseLanguage or EnglishLanguage;

    /// <summary>Returns the key itself when the key is missing, matching the WPF build's existing behaviour (absent copy is surfaced explicitly instead of rendering blank).</summary>
    public static string Get(string key, string? language) => Values.TryGetValue(key, out var value)
        ? (Normalize(language) == ChineseLanguage ? value.Chinese : value.English)
        : key;

    /// <summary>All keys. The adapter layers use it to pour the copy into their own resource dictionaries.</summary>
    public static IReadOnlyCollection<string> Keys => Values.Keys;

    private static readonly Dictionary<string, (string Chinese, string English)> Values = new()
    {
        ["BuildConfiguration"] = ("构建配置", "Build configuration"),
        ["ConfigurationHint"] = ("Debug：调试、断言与详细日志。Release：优化构建。两种配置的产物分别保存。", "Debug: debugging, assertions and detailed logs. Release: optimized build. Outputs are stored separately."),
        ["AndroidReleasePending"] = ("Release 使用项目发行密钥，生成已签名 APK/AAB；继续后配置包名、版本和密钥。", "Release uses your project key to produce signed APK/AAB. Configure application ID, version and signing after continuing."),
        ["AndroidReleaseSettings"] = ("Android 发行设置", "Android release settings"),
        ["LastBuildPlatform"] = ("当前构建目标", "Current build target"),
        ["ChooseBuildPlatform"] = ("选择构建平台", "Choose build platform"),
        ["BuildPlatformHint"] = ("项目源码可用于多个平台。每次构建可选择目标；Apple 平台需 macOS，Linux 需 Linux 宿主。", "Project sources support multiple platforms. Choose a target for this build; Apple platforms require macOS, Linux requires a Linux host."),
        ["Continue"] = ("继续", "Continue"),
        ["DownloadSize"] = ("下载大小", "Download size"),
        ["InstallSelected"] = ("安装", "Install"),
        ["Back"] = ("返回", "Back"),
        ["ComponentDetails"] = ("组件诊断", "Component diagnostics"),
        ["LocalHost"] = ("可在本机配置与构建", "Configure and build on this host"),
        ["RequiresHost"] = ("构建宿主：", "Build host:"),
        ["PlatformToolsNote"] = ("工具只从 Hub 目录检测；SDK 文件存在仍需通过配置验证。", "Tools are checked only in Hub directories; SDK files still require configure verification."),
        ["AndroidNativeNote"] = ("支持 Debug / Release APK 和 AAB；Release 使用项目密钥，设备运行需选择已授权设备。", "Supports Debug/Release APKs and AABs. Release uses your project key; select an authorized device to run the APK."),
        ["OpenOutputs"] = ("打开产物", "Open outputs"),
        ["Open build outputs"] = ("打开构建产物", "Open build outputs"),
        ["AndroidDevice"] = ("Android 设备", "Android device"),
        ["RefreshDevices"] = ("刷新设备", "Refresh devices"),
        ["Refresh Android devices"] = ("查询 Android 设备", "Query Android devices"),
        ["AndroidDeviceHint"] = ("连接设备并开启 USB 调试；在设备上允许 Hub 的调试授权。", "Connect a device with USB debugging enabled and approve Hub's debugging authorization on the device."),
        ["AndroidNoDevices"] = ("未检测到设备", "No devices detected"),
        ["SelectAndroidDevice"] = ("请刷新并选择已授权的 Android 设备。", "Refresh and select an authorized Android device."),
        ["device"] = ("已授权", "Authorized"),
        ["unauthorized"] = ("等待调试授权", "Awaiting debugging authorization"),
        ["offline"] = ("离线", "Offline"),
        ["Change target"] = ("切换目标平台", "Change target platform"),
        ["UwpPending"] = ("UWP 工具隔离与打包待完成", "UWP isolation and packaging pending"),
        ["Projects"] = ("项目", "Projects"),
        ["Installs"] = ("引擎", "Engines"),
        ["Toolchains"] = ("工具链", "Toolchains"),
        ["Settings"] = ("设置", "Settings"),
        ["Subtitle"] = ("开发，从这里开始", "Your development workspace"),
        ["ProjectsHint"] = ("创建、构建并运行你的 Axmol 项目。", "Create, build and run your Axmol projects."),
        ["NewProject"] = ("新建项目", "New project"),
        ["OpenExisting"] = ("添加已有项目", "Add existing project"),
        ["Search"] = ("搜索", "Search"),
        ["SearchProjects"] = ("按名称或路径搜索项目", "Search projects by name or path"),
        ["Actions"] = ("操作", "Actions"),
        ["BuildTarget"] = ("构建目标", "Build target"),
        ["Open"] = ("打开", "Open"),
        ["MoreActions"] = ("更多项目操作", "More project actions"),
        ["AllEngines"] = ("全部引擎", "All engines"),
        ["AllPlatforms"] = ("全部平台", "All platforms"),
        ["ProjectResultCount"] = ("显示 {0} / {1} 个", "Showing {0} of {1}"),
        ["NoProjectsMatch"] = ("没有匹配的项目。", "No projects match your search."),
        ["SearchEngines"] = ("按版本、通道或目录搜索引擎", "Search engines by version, channel or directory"),
        ["AllChannels"] = ("全部通道", "All channels"),
        ["EngineResultCount"] = ("显示 {0} / {1} 个", "Showing {0} of {1}"),
        ["NoEnginesMatch"] = ("没有匹配的引擎。", "No engines match your search."),
        ["SortEngineVersionDescending"] = ("版本 ↓", "Version ↓"),
        ["SortEngineVersionAscending"] = ("版本 ↑", "Version ↑"),
        ["SortEnginePath"] = ("按目录排序", "Sort by directory"),
        ["Default"] = ("默认", "Default"),
        ["EngineDirectory"] = ("引擎目录", "Engine directory"),
        ["PrebuiltOnThisHost"] = ("预编译库（本机）", "Prebuilt libraries (this host)"),
        ["ClearSearch"] = ("清除搜索和筛选", "Clear search and filters"),
        ["SortRecentDescending"] = ("最近打开 ↓", "Last opened ↓"),
        ["SortRecentAscending"] = ("最近打开 ↑", "Last opened ↑"),
        ["SortByName"] = ("按名称排序", "Sort by name"),
        ["NeverOpened"] = ("从未打开", "Never opened"),
        ["TodayAt"] = ("今天", "Today"),
        ["ProjectName"] = ("项目名称", "Project name"),
        ["Location"] = ("项目父目录", "Parent directory"),
        ["EngineVersion"] = ("引擎版本", "Engine version"),
        ["Template"] = ("官方 2D · Hello World 模板", "Official 2D · Hello World template"),
        ["Scripting"] = ("脚本方式", "Scripting"),
        ["cpp"] = ("仅 C++", "C++ only"),
        ["lua"] = ("C++ + Lua", "C++ + Lua"),
        ["ScriptingHint"] = ("Lua 项目包含 C++ 原生入口、Lua 绑定与脚本。项目创建后按选定方式开发。", "Lua projects include a C++ entry point, Lua bindings and scripts. The choice sets the initial project structure."),
        ["Create"] = ("创建项目", "Create project"),
        ["Browse"] = ("选择目录", "Browse"),
        ["Name"] = ("名称", "Name"),
        ["Version"] = ("版本", "Version"),
        ["BuildStatus"] = ("构建状态", "Build status"),
        ["LastOpened"] = ("最近打开", "Last opened"),
        ["Path"] = ("目录", "Directory"),
        ["Configure"] = ("配置", "Configure"),
        ["Build"] = ("构建", "Build"),
        ["Run"] = ("运行", "Run"),
        ["OpenFolder"] = ("打开目录", "Open folder"),
        ["RemoveFromList"] = ("移出列表", "Remove from list"),
        ["ProjectsCount"] = ("项目", "Projects"),
        ["EnginesCount"] = ("已安装引擎", "Installed engines"),
        ["Platform"] = ("目标平台", "Target platform"),
        ["EnginesHint"] = ("版本独立安装，项目始终锁定自己的引擎。", "Separate installations. Each project keeps its engine version."),
        // The version number is deliberately not hard-coded: the manifest now carries six versions, and a button
        // reading "Install 2.11.5" would become a false statement after a manifest change — with the version picker right beside it.
        ["InstallOfficial"] = ("安装官方引擎", "Install official engine"),
        // The picker's window title. It says what the dialog ends up doing, not what step you are on: the
        // old "选择引擎版本" named a step, while the button that closes the dialog has always said 安装.
        ["InstallEngineTitle"] = ("安装 Axmol 引擎", "Install Axmol engine"),
        ["EngineVersionHint"] = ("每个版本独立安装到 Hub 数据目录，项目始终锁定创建时的版本。", "Each version installs independently into Hub storage. Projects keep the version they were created with."),
        ["EngineVersionNone"] = ("清单里没有可安装的引擎版本。", "The manifest declares no installable engine release."),
        ["EngineIndexRemote"] = ("版本列表来自 axmol.dev 索引。", "Version list from the axmol.dev index."),
        ["EngineIndexBuiltIn"] = ("未能获取 axmol.dev 索引，正在使用 Hub 内置列表。", "axmol.dev index unavailable. Using the built-in list."),
        ["EngineRecipeUnverified"] = ("该版本尚无经过验证的打包配方：引擎可用，但 Android 打包会被拒绝。", "This version has no verified packaging recipe: the engine works, but Android packaging is refused."),
        ["DownloadSource"] = ("引擎下载源", "Engine download source"),
        ["DownloadSourceHint"] = ("引擎发布包从哪里下载。镜像提供的是同一个文件，下载后仍按 SHA-256 校验；只影响安装引擎，不改变引擎自己拉取依赖的镜像。", "Where engine release packages are downloaded from. Mirrors serve the same file and SHA-256 verification still applies. This affects engine installs only, not the mirror the engine itself fetches from."),
        ["DownloadSourceGitHub"] = ("GitHub（官方）", "GitHub (official)"),
        ["DownloadSourceAtomGit"] = ("AtomGit（镜像）", "AtomGit (mirror)"),
        ["DownloadSourceCustom"] = ("自定义", "Custom"),
        ["CustomDownloadSource"] = ("自定义下载地址", "Custom download URL"),
        ["CustomDownloadSourceHint"] = ("可填主机（例如 https://atomgit.com，只替换官方地址的域名），或含 {version} 的完整模板（例如 https://example.com/axmol/axmol-{version}.zip）。", "Enter an origin (e.g. https://atomgit.com, which replaces only the official host) or a full template containing {version} (e.g. https://example.com/axmol/axmol-{version}.zip)."),
        ["CustomUrlRequired"] = ("选择了自定义下载源，但地址为空。", "Custom download source is selected, but the URL is empty."),
        ["CustomUrlInvalid"] = ("自定义下载源无效：请填写 https 主机地址，或含 {version} 的 https 模板。", "Invalid custom download source: enter an https origin, or an https template containing {version}."),
        ["Mirror"] = ("镜像", "Mirror"),
        ["SwitchMirror"] = ("切换镜像", "Switch mirror"),
        ["SwitchEngineMirror"] = ("切换引擎镜像", "Switch engine mirror"),
        ["MirrorHint"] = ("镜像决定引擎拉取依赖与工具的来源，写在引擎目录里：v3 写 1k/.env 的 active_mirror（可选值来自 1k/sources.json），v2 只支持用空文件 1k/.gitee 切到 gitee。切换后对随后的 setup 与构建生效。", "The mirror decides where the engine fetches dependencies and tools, and it is stored inside the engine tree: v3 writes active_mirror in 1k/.env (the choices come from 1k/sources.json), v2 only supports switching to gitee via an empty 1k/.gitee file. The change applies to later setup and builds."),
        ["MirrorStatusFormat"] = ("镜像：{0}", "Mirror: {0}"),
        ["MirrorNoEngine"] = ("未选择引擎", "No engine selected"),
        ["MirrorUnknown"] = ("无法识别该引擎的镜像方式（需要 1k/.env 或 1k/.gitee）。", "This engine's mirror mechanism is not recognized (1k/.env or 1k/.gitee required)."),
        ["MirrorUnknownLabel"] = ("镜像未知", "Unknown mirror"),
        ["MirrorDeclaredUnknownFormat"] = ("未知来源：{0}。引擎声明了此来源，但 Hub 暂无说明。", "Unknown source: {0}. This engine declares it, but Hub has no description for it."),
        ["MirrorLabelOfficial"] = ("GitHub 官方", "GitHub official"),
        ["MirrorLabelAtomGit"] = ("AtomGit", "AtomGit"),
        ["MirrorLabelGitee"] = ("Gitee", "Gitee"),
        ["MirrorLabelTencent"] = ("腾讯云 · 仅 Gradle", "Tencent Cloud · Gradle only"),
        ["MirrorFallbackNote"] = ("某些依赖没有配置该镜像时，引擎会自动回退到 origin。", "The engine falls back to origin for dependencies that do not declare this mirror."),
        ["MirrorOrigin"] = ("origin（GitHub 官方源）", "origin (GitHub, official)"),
        ["MirrorAtomGit"] = ("atomgit（AtomGit 镜像）", "atomgit (AtomGit mirror)"),
        ["MirrorGitee"] = ("gitee（Gitee 镜像）", "gitee (Gitee mirror)"),
        ["MirrorGithub"] = ("github（官方源）", "github (official)"),
        ["MirrorTencent"] = ("tencent（腾讯云镜像）", "tencent (Tencent Cloud mirror)"),
        // Extra clause appended to a mirror's label when the engine only honors it for one thing.
        // Parentheses differ per language on purpose: Chinese uses full-width ones with no leading
        // space, English uses half-width ones preceded by a space.
        ["MirrorNoteAndroidGradle"] = ("（仅用于 Android Gradle）", " (Android Gradle only)"),
        ["MirrorStoragePrefix"] = ("写入位置", "Writes"),
        ["Apply"] = ("应用", "Apply"),
        ["ImportEngine"] = ("导入引擎", "Import engine"),
        ["Channel"] = ("通道", "Channel"),
        ["SetDefault"] = ("设为默认", "Set default"),
        ["VerifyEngine"] = ("验证", "Verify"),
        ["Repair"] = ("修复", "Repair"),
        ["Uninstall"] = ("卸载", "Uninstall"),
        // Confirmation prompts and consequence hints. The uninstall wording used to be a C# inline
        // ternary; it lives here now so both languages are editable in one place.
        ["UninstallPrompt"] = ("卸载 Axmol {0}？安装文件会保留到数据目录的 trash 中，项目文件不受影响。", "Uninstall Axmol {0}? Installation files are retained in the data directory's trash folder. Project files are preserved."),
        ["RepairPrompt"] = ("重新下载并覆盖 Axmol {0} 的引擎文件？使用此引擎的项目会被重置为“未构建”。", "Re-download and overwrite the Axmol {0} engine files? Projects using this engine are reset to Not built."),
        ["RepairHint"] = ("重新下载并覆盖整棵引擎树，使用此引擎的项目会回到“未构建”", "Re-downloads and overwrites the whole engine tree; projects using it go back to Not built"),
        ["RemoveProjectHint"] = ("把项目移出 Hub 列表，磁盘上的项目文件保留", "Removes the project from Hub's list; the project files on disk are kept"),
        ["MoveProjectToTrash"] = ("移动到回收站…", "Move to trash…"),
        ["MoveProjectToTrashPrompt"] = ("此操作会将整个项目目录（包括源码、资源、构建产物及其他文件）移到系统回收站。你可以从回收站恢复。", "This moves the entire project folder, including source, assets, build output and other files, to the system trash. You can restore it from the trash."),
        ["MoveProjectToTrashFailed"] = ("无法将项目移到系统回收站。项目仍保留在列表中。\n{0}", "Could not move the project to the system trash. It remains in the list.\n{0}"),
        ["ProjectMovedButListSaveFailed"] = ("项目已移到系统回收站，但 Hub 未能保存列表。重新启动后该记录可能仍会显示。", "The project was moved to the system trash, but Hub could not save the list. Its entry may reappear after restart."),
        ["ProjectNoLongerRegistered"] = ("此项目已不在 Hub 列表中，请刷新后重试。", "This project is no longer registered in Hub. Refresh and try again."),
        ["UnsafeProjectTrashPath"] = ("为保护系统、用户和 Hub 文件，此目录不能移到回收站。", "This folder cannot be moved to the trash because it contains protected system, user or Hub files."),
        ["ProjectContainsRegisteredProject"] = ("此目录还包含已登记的项目“{0}”，不能整体移到回收站。", "This folder also contains the registered project “{0}” and cannot be moved as a whole."),
        ["StopProjectPreviewFirst"] = ("请先停止此项目的 Web 预览，再移到回收站。", "Stop this project's web preview before moving it to the trash."),
        ["AnotherProjectPreviewRunning"] = ("另一个项目的 Web 预览仍在运行，请先停止它。", "Another project's web preview is still running. Stop it first."),
        ["RemoveEngineHint"] = ("把引擎移出 Hub 列表，磁盘上的引擎目录保留", "Removes the engine from Hub's list; the engine directory on disk is kept"),
        ["SwitchDataRootPrompt"] = ("把数据目录切换到 {0}？Hub 会重建工作区并清空当前页面缓存。", "Switch the data directory to {0}? Hub rebuilds the workspace and clears the current page cache."),
        ["ToolsTitle"] = ("平台构建支持", "Platform build support"),
        ["ToolsHint"] = ("按引擎版本管理平台模块和配套工具。", "Manage platform modules and tools for each engine version."),
        ["ToolsNote"] = ("MSVC 使用微软官方安装器，需要 Windows 管理员授权。", "MSVC uses Microsoft's installer and requires Windows administrator approval."),
        ["Component"] = ("组件", "Component"),
        ["Status"] = ("状态", "Status"),
        ["Details"] = ("版本与目录", "Version and location"),
        ["Verify"] = ("重新检测", "Verify"),
        ["InstallTools"] = ("安装基础工具", "Install tools"),
        ["RunEngineSetup"] = ("运行引擎 setup.ps1", "Run engine setup.ps1"),
        ["EngineSetupHint"] = ("工具链由引擎自己安装到引擎目录的 tools/external。Hub 会用 -hub 调用 setup.ps1；AX_ROOT 与 PATH 只在当前 setup 进程中设置，不写入用户环境或 shell profile。Windows 上仍可能持久化修改当前用户的 PowerShell 执行策略，并请求提权（UAC）。不支持 -hub 的旧引擎脚本会被拒绝执行，请先更新引擎。", "The engine installs its own toolchain into <engine>/tools/external. Hub calls setup.ps1 with -hub; AX_ROOT and PATH are set only in the setup process, not persisted to the user environment or shell profiles. On Windows, setup may still persistently change the current user's PowerShell execution policy and request elevation (UAC). Older engine scripts without -hub support are refused; update the engine first."),
        // Host PowerShell 7. This is the one thing Hub installs outside the engine tree, and only because the
        // engine's own setup.ps1 refuses to run without pwsh. The state sentences are formatted with the version
        // (and the floor) by ToolchainsPage, then the executable path is appended in code — a path is not copy.
        // No title label is added on purpose: every sentence already begins with PowerShell or pwsh, and the
        // button names it too ("say a thing once").
        ["HostShellStateReady"] = ("PowerShell {0} 已就绪", "PowerShell {0} is ready"),
        ["HostShellStateTooOld"] = ("PowerShell {0} 低于引擎要求的 {1}", "PowerShell {0} is older than the {1} the engine requires"),
        ["HostShellStateMissing"] = ("这台机器还没有 pwsh —— 引擎的 setup.ps1 跑不起来", "This host has no pwsh, so the engine's setup.ps1 cannot run"),
        ["HostShellStateUnknown"] = ("找到了 pwsh，但版本还没确认", "pwsh was found, but its version is unconfirmed"),
        ["InstallPowerShell7"] = ("安装 PowerShell 7", "Install PowerShell 7"),
        ["HostShellMethodWinget"] = ("用 winget 安装 Microsoft.PowerShell，提权由 winget 自己发起。", "Installs Microsoft.PowerShell through winget, which raises its own elevation prompt."),
        ["HostShellMethodMsi"] = ("winget 不在 PATH：改从 PowerShell 官方 GitHub 取最新 MSI，按该 release 自带的 SHA-256 校验后提权安装。", "winget is not on PATH, so the latest official MSI is taken from PowerShell's GitHub, verified against the SHA-256 that release ships, and installed elevated."),
        ["HostShellMethodTerminal"] = ("在系统终端里执行引擎自带的 1k/pwshi.sh —— 它会要求输入 sudo 密码。", "Runs the engine's own 1k/pwshi.sh in a terminal window, which will ask for your sudo password."),
        ["HostShellConfirmWindows"] = ("这一步只改这台机器上的 PowerShell，不碰引擎目录里的任何东西。装完点重新检测：Hub 用自己的探测确认，不看安装器的退出码。", "This changes only the PowerShell on this machine and touches nothing in the engine tree. Press Re-check afterwards: Hub confirms with its own probe rather than trusting an installer's exit code."),
        ["HostShellConfirmUnix"] = ("那条脚本内部用 sudo，而 Hub 的子进程没有终端、读不到密码，所以它会开一个系统终端窗口。请在那个窗口里完成，再回 Hub 点重新检测。", "The script calls sudo, and Hub's child process has no terminal to read a password, so this opens a system terminal window. Finish it there, then press Re-check."),
        ["HostShellReady"] = ("PowerShell 7 已就位", "PowerShell 7 is in place"),
        ["HostShellInTerminal"] = ("安装已在终端窗口里开始，Hub 读不到它的输出；完成后点重新检测。", "The install started in a terminal window that Hub cannot read; press Re-check when it finishes."),
        ["HostShellUacDeclined"] = ("授权被取消，pwsh 没有安装。", "Elevation was declined, so pwsh was not installed."),
        ["HostShellNoTerminal"] = ("没有找到可用的终端程序。请在自己的终端里执行这一条：{0}", "No usable terminal was found. Run this in a terminal yourself: {0}"),
        ["HostShellFailed"] = ("安装没有完成，详情在日志里。", "The install did not complete; the log has the details."),
        ["Install PowerShell 7"] = ("安装 PowerShell 7", "Installing PowerShell 7"),
        ["Check PowerShell 7"] = ("重新检测 PowerShell 7", "Checking PowerShell 7"),
        ["BuildEngine"] = ("构建引擎库", "Build engine"),
        ["Build engine"] = ("构建引擎", "Build engine"),
        ["BuildEngineConfirm"] = ("开始构建引擎", "Build engine"),
        ["EngineBuildConfig"] = ("构建配置", "Build configuration"),
        ["EngineBuildCost"] = ("将把引擎编译成项目可复用的预编译库（Release 为优化构建）。这一步耗时数分钟到数十分钟并占用数 GB 磁盘；缺少工具链时可能触发引擎 setup。", "Compiles the engine into prebuilt libraries projects can reuse (Release is optimized). This takes many minutes and several GB of disk, and may trigger engine setup when tools are missing."),
        ["PrebuiltStatus"] = ("预编译引擎库", "Prebuilt engine libraries"),
        ["PrebuiltLabelReady"] = ("就绪", "Ready"),
        ["PrebuiltSupported"] = ("支持预编译", "Prebuilt supported"),
        ["PrebuiltSupportedHint"] = ("此主机支持构建和链接预编译引擎库。", "This host supports building and linking prebuilt engine libraries."),
        ["PrebuiltLabelNotBuilt"] = ("未构建", "Not built"),
        ["PrebuiltLabelUnsupported"] = ("本机不支持", "Host unsupported"),
        ["PrebuiltLabelTargetUnsupported"] = ("目标不支持", "Target unsupported"),
        ["PrebuiltLabelRebuild"] = ("需重建", "Rebuild required"),
        ["PrebuiltLabelTargetMismatch"] = ("目标不匹配", "Target mismatch"),
        ["PrebuiltLabelConfigurationMissing"] = ("缺少配置", "Configuration missing"),
        ["PrebuiltLabelIncomplete"] = ("不完整", "Incomplete"),
        ["PrebuiltNone"] = ("尚未构建", "Not built"),
        ["PrebuiltReadyFormat"] = ("{0} → {1}（就绪）", "{0} → {1} (ready)"),
        ["PrebuiltStaleFormat"] = ("{0} → {1}（需重新构建）", "{0} → {1} (rebuild required)"),
        ["PrebuiltUnsupportedHost"] = ("此宿主无法使用预编译库（仅 Windows 构建目标支持链接预编译引擎）。", "Prebuilt libraries are unavailable on this host (only Windows build targets can link the prebuilt engine)."),
        ["PrebuiltSettings"] = ("预编译库设置", "Prebuilt library settings"),
        ["UsePrebuilt"] = ("使用预编译引擎库", "Use prebuilt engine libraries"),
        ["PrebuiltHint"] = ("仅 Windows 构建目标可用；需先在引擎页构建引擎，且目标与配置必须一致。", "Available for Windows build targets only; build the engine on the Engines page first and keep the target and configuration identical."),
        ["PrebuiltReadyHint"] = ("该引擎已有预编译库，目标与配置一致时会直接链接。", "This engine has a prebuilt build; builds link it when the target and configuration match."),
        ["PrebuiltNotBuiltHint"] = ("该引擎尚未构建预编译库；勾选后构建会失败，直到在引擎页构建它。", "This engine has no prebuilt build yet; builds will fail until you build it on the Engines page."),
        ["PrebuiltUnavailable"] = ("此项目使用预编译引擎库，但引擎那一份还不可用。", "This project uses prebuilt engine libraries, but the engine's build is not usable."),
        ["PrebuiltUnavailableFormat"] = ("目标 {0} · 配置 {1}", "Target {0} · configuration {1}"),
        ["PrebuiltUnavailableAction"] = ("请到引擎页为同一目标与配置构建引擎，或关闭该项目的预编译库选项。", "Build the engine on the Engines page for the same target and configuration, or turn off prebuilt libraries for this project."),
        ["PrebuiltReasonNotBuilt"] = ("该引擎尚未构建。", "The engine has not been built."),
        ["PrebuiltReasonConfiguration"] = ("引擎构建里没有该配置的库。", "The engine build has no libraries for this configuration."),
        ["PrebuiltReasonPlatform"] = ("该目标平台不支持预编译库（仅 Windows）。", "Prebuilt libraries are not supported for this target (Windows only)."),
        ["PrebuiltReasonEngineChanged"] = ("引擎安装已变更，预编译库可能过期。", "The engine installation changed; its prebuilt libraries may be stale."),
        ["PrebuiltReasonContents"] = ("引擎构建目录不完整。", "The engine build directory is incomplete."),
        ["PrebuiltReasonTarget"] = ("引擎构建的目标与当前项目不一致。", "The engine was built for a different target than this project."),
        ["PrebuiltSaved"] = ("预编译库设置已保存；请重新构建项目。", "Prebuilt settings saved; rebuild the project."),
        ["OpenEnginesPage"] = ("打开引擎页", "Open Engines page"),
        ["InstallSdk"] = ("安装 Windows SDK", "Install Windows SDK"),
        ["InstallMsvc"] = ("安装 MSVC", "Install MSVC"),
        // The settings card's rail. Six of the seven rows are copy keys that already existed as card titles:
        // a category that holds one section has no title inside it any more (the rail row IS the heading), so
        // the row took the old title's key rather than the title's text being written a second time under a
        // new name. Only 外观与语言 needed a key of its own, because it is the one group that holds two
        // sections and so needs a name that is neither of them.
        ["SettingsCatAppearance"] = ("外观与语言", "Appearance & language"),
        ["SettingsOverlayClose"] = ("关闭设置", "Close settings"),
        ["Language"] = ("语言", "Language"),
        ["LanguageHint"] = ("切换立即生效，重启后保留。", "Applies immediately and is remembered."),
        ["Appearance"] = ("外观", "Appearance"),
        ["Theme"] = ("主题", "Theme"),
        ["ThemeHint"] = ("跟随系统时会随系统主题变化；也可以固定为浅色或深色。", "Follows the system theme, or pin light or dark."),
        ["ThemeSystem"] = ("跟随系统", "Follow system"),
        ["ThemeLight"] = ("浅色", "Light"),
        ["ThemeDark"] = ("深色", "Dark"),
        ["Storage"] = ("存储与项目", "Storage and projects"),
        ["DataDirectory"] = ("Hub 数据目录", "Hub data directory"),
        ["DataHint"] = ("引擎、工具链、缓存与日志存放于此。切换目录不会移动现有文件。", "Engines, tools, cache and logs. Changing directories does not move existing files."),
        ["DefaultProjectDirectory"] = ("默认项目目录", "Default project directory"),
        ["ProjectDirectoryHint"] = ("创建新项目时默认使用此目录。", "Default parent directory for new projects."),
        ["Editors"] = ("代码编辑器", "Code editor"),
        ["SelectEditor"] = ("选择编辑器", "Choose editor"),
        ["NotSelected"] = ("尚未选择", "Not selected"),
        ["EditorHint"] = ("用于打开项目的编辑器。列表是本机探测到的编辑器（macOS 上是 /Applications 里的应用），不在列表里的用「浏览…」；项目页可以为单个项目另选。", "The editor that opens projects. The list holds what was found on this machine — on macOS, the applications in /Applications; use Browse… for one installed elsewhere. A project can override it from the projects page."),
        ["BrowseEditor"] = ("浏览", "Browse"),
        ["DetectAgain"] = ("重新探测", "Detect again"),
        ["NoEditorFound"] = ("没有可用的编辑器，请在设置中选择。", "No editor available — choose one in Settings."),
        ["OpenInEditor"] = ("用编辑器打开", "Open in editor"),
        ["NoEditorConfigured"] = ("请先在设置中配置可用的代码编辑器", "Configure an available code editor in Settings first"),
        ["DefaultEditor"] = ("设为默认编辑器", "Set as default editor"),
        ["OpenWith"] = ("打开方式…", "Open with…"),
        ["EditorUseOnce"] = ("仅本次", "This time only"),
        ["EditorForProject"] = ("设为该项目默认", "Default for this project"),
        ["EditorForAll"] = ("设为全局默认", "Default everywhere"),
        ["EditorFollowDefault"] = ("跟随全局默认", "Follow the global default"),
        ["DefaultEngine"] = ("默认引擎", "Default engine"),
        ["NoDefault"] = ("尚未安装引擎", "No engine installed"),
        ["LogTitle"] = ("活动与日志", "Activity and output"),
        ["LogActions"] = ("日志操作", "Log actions"),
        ["CopyError"] = ("复制错误", "Copy error"),
        ["CopyCode"] = ("复制代码", "Copy code"),
        ["OpenLogs"] = ("打开日志", "Open logs"),
        ["ClearLog"] = ("清空日志", "Clear log"),
        ["Cancel"] = ("取消", "Cancel"),
        // Dialog buttons. These three used to be **hard-coded Chinese** inside HubDialog, so opening any dialog in
        // the English UI gave Chinese buttons — and the "missing CJK font" notice is closed by exactly this button (see CjkFontNotice).
        ["Ok"] = ("确定", "OK"),
        ["Yes"] = ("是", "Yes"),
        ["No"] = ("否", "No"),
        ["Ready"] = ("准备就绪", "Ready"),
        ["Done"] = ("已完成", "completed"),
        ["Cancelled"] = ("已取消", "cancelled"),
        ["Failed"] = ("失败", "Failed"),
        ["Working"] = ("处理中", "Working"),
        ["Download"] = ("下载中", "Downloading"),
        ["EmptyProjects"] = ("还没有项目。创建第一个项目，开始开发。", "No projects yet. Create your first project to get started."),
        ["EmptyEngines"] = ("还没有引擎。安装或导入 Axmol。", "No engines yet. Install or import Axmol."),
        ["Installed"] = ("已安装", "Installed"),
        ["Missing"] = ("未安装", "Missing"),
        ["Broken"] = ("需要修复", "Broken"),
        ["Unknown"] = ("未检测", "Unknown"),
        ["Checking"] = ("检测中", "Checking"),
        ["Installing"] = ("安装中", "Installing"),
        ["UpdateAvailable"] = ("有更新", "Update available"),
        ["Not built"] = ("未构建", "Not built"),
        ["Succeeded"] = ("构建成功", "Succeeded"),
        ["Configured"] = ("已配置", "Configured"),
        ["Configuring"] = ("配置中", "Configuring"),
        ["Building"] = ("构建中", "Building"),
        ["Select a project first."] = ("请先选择项目。", "Select a project first."),
        ["Select an engine first."] = ("请先选择引擎。", "Select an engine first."),
        ["Install or import an engine first."] = ("请先安装或导入引擎。", "Install or import an engine first."),
        ["Build successfully before Run."] = ("请先成功构建，再运行。", "Build successfully before Run."),
        // The game holds its own output directory open, so the compiler's sharing-conflict sentence is really
        // about a window the user can just close. Say which, and say it before the build fails.
        ["ProjectStillRunning"] = ("项目还在运行，先关掉它再构建。", "The project is still running. Close it before building."),
        // The only two run failures Hub reports: the process never came up (an exception), and this — it came
        // up and dropped immediately. A game the user closed later is not an error.
        ["GameExitedEarly"] = ("{0} 启动后立即退出，没有真正跑起来。", "{0} exited right after it started and never came up."),
        ["Select VS Code executable in Settings first."] = ("请先在设置中选择 VS Code。", "Select VS Code executable in Settings first."),
        ["Select Visual Studio devenv.exe in Settings first."] = ("请先在设置中选择 Visual Studio。", "Select Visual Studio devenv.exe in Settings first."),
        ["Verify toolchains"] = ("检测工具链", "Verify toolchains"),
        ["Import engine"] = ("导入引擎", "Import engine"),
        ["Install official engine"] = ("安装官方引擎", "Install official engine"),
        ["Create project"] = ("创建项目", "Create project"),
        ["Open project"] = ("添加项目", "Add project"),
        ["Configure CMake"] = ("配置 CMake", "Configure CMake"),
        ["Build project"] = ("构建项目", "Build project"),
        ["Run project"] = ("运行项目", "Run project"),
        // The setup script's own row title. The key was missing, so the status line printed it raw — a row in
        // the popover makes that user-visible instead of a glance at the strip, so it gets its copy here.
        ["Run engine setup"] = ("运行引擎 setup", "Run engine setup"),
        ["Install managed tools"] = ("安装基础工具", "Install tools"),
        ["Install Windows SDK"] = ("安装 Windows SDK", "Install Windows SDK"),
        ["Install MSVC Build Tools"] = ("安装 MSVC", "Install MSVC"),
        ["Set default engine"] = ("设置默认引擎", "Set default engine"),
        ["Verify engine"] = ("验证引擎", "Verify engine"),
        ["Remove engine from list"] = ("移出引擎列表", "Remove engine from list"),
        ["Remove project from list"] = ("移出项目列表", "Remove project from list"),
        ["Open project folder"] = ("打开项目目录", "Open project folder"),
        ["Open engine folder"] = ("打开引擎目录", "Open engine folder"),
        ["Select Axmol engine root"] = ("选择 Axmol 引擎目录", "Select Axmol engine root"),
        ["Select Axmol project folder"] = ("选择 Axmol 项目目录", "Select Axmol project folder"),
        ["Select parent location"] = ("选择项目父目录", "Select parent directory"),
        ["Select data directory"] = ("选择 Hub 数据目录", "Select Hub data directory"),
        ["Select default project directory"] = ("选择默认项目目录", "Select default project directory"),
        ["MsvcActive"] = ("微软安装器运行中，请批准 Windows 授权；取消请使用安装器界面。", "Microsoft installer is running. Approve Windows UAC; cancel in the installer."),
        ["Cancelling"] = ("正在取消，请等待进程停止后再关闭。", "Cancelling. Close again when the process stops."),
        ["ErrorHint"] = ("操作失败，详细原因已记录在日志中。", "Operation failed. Details are recorded in the log."),
        ["OperationFailed"] = ("操作失败", "Operation failed"),
        ["DeepLinkTitle"] = ("官网安装请求", "Website install request"),
        ["DeepLinkInvalid"] = ("此安装链接无效或不受支持。请从 Axmol 官网重新打开链接。", "This install link is invalid or unsupported. Open it again from the Axmol website."),
        ["DeepLinkBusy"] = ("Hub 正在执行其他操作，请完成或取消后再试。", "Hub is busy. Finish or cancel the current operation, then try again."),
        // The same refusal, said about the holder that has no row to point at: an assistant command holds the
        // change gate without joining the list, because the ✕ that stops it is the one on the reply. Naming the
        // assistant is the difference between "cancel the current operation" and something the person can find.
        ["DeepLinkBusyAssistant"] = ("AI 助手正在改动这里，请等它跑完或在回答处停掉它。", "The AI assistant is making a change here. Wait for it to finish, or stop it from the reply."),
        // The stage words a build prints about itself. They outrank the phase word on a task's row, and since
        // the progress window went away nothing else needs the window's own title, hint or "preparing" state.
        ["BuildConfiguring"] = ("配置项目", "Configuring project"),
        ["BuildCompiling"] = ("编译代码", "Compiling code"),
        ["BuildPackaging"] = ("打包应用", "Packaging application"),
        ["BuildVerifying"] = ("校验产物", "Verifying output"),
        ["BuildElapsed"] = ("已用时间", "Elapsed"),
        ["ProjectAlreadyExists"] = ("项目目录已存在，无法创建同名项目。", "The project destination already exists. Cannot create a project at the same location."),
        ["ProjectAlreadyExistsAction"] = ("请修改项目名称或选择其他目录。若要使用现有项目，请点击“添加已有项目”。", "Change the project name or choose another directory. To use the existing project, click Add existing project."),
        ["EngineMissing"] = ("项目所需引擎未安装，请前往引擎页面安装或导入。", "Required engine is missing. Install or import it on the Engines page."),
        ["Managed MSVC v143"] = ("MSVC v143", "MSVC v143"),
        ["Managed Windows SDK"] = ("Windows SDK", "Windows SDK"),
        ["Axmol shader compiler"] = ("着色器编译器", "Shader compiler"),
        ["Python (optional for Windows C++)"] = ("Python（Windows C++ 可选）", "Python (optional for Windows C++)"),
        ["Repair engine"] = ("修复引擎", "Repair engine"),
        ["Uninstall engine"] = ("卸载引擎", "Uninstall engine"),
        ["DataChanged"] = ("数据目录已切换", "Data directory changed"),
        ["DefaultProjectsChanged"] = ("默认项目目录已保存", "Default project directory saved"),
        ["PageNotMigrated"] = ("该页面尚未迁移到 Avalonia 版（P5 进行中）。", "This page has not been migrated to the Avalonia build yet (P5 in progress)."),
        ["WaitForOperation"] = ("请先等当前操作结束或取消它，再切换数据目录。", "Wait for the active operation to finish or cancel it before changing the data directory."),
        ["LocalPathRequired"] = ("选中的位置没有本地路径，请选择本机磁盘上的位置。", "The selected location has no local path. Choose a location on this PC."),
        ["StartupFailed"] = ("Axmol Hub 启动失败", "Axmol Hub failed to start"),
        // Update check (Velopack self-update). The "Check for updates" button lives on the Settings page; startup also runs one silent check.
        ["Updates"] = ("软件更新", "Software update"),
        ["UpdatesHint"] = ("检查 Axmol Hub 的新版本。只有安装版才会自动更新。", "Check for a new version of Axmol Hub. Only installed copies update automatically."),
        ["UpdateChannel"] = ("更新通道", "Update channel"),
        ["UpdateChannelHint"] = ("Stable 仅接收正式版；Preview 也会检查预发布版本。切换后可点击「检查更新」立即检查；预发布版 Hub 始终使用 Preview。", "Stable receives stable releases only; Preview also checks pre-releases. Click Check for updates after switching; Preview builds always use Preview."),
        ["UpdateChannelStable"] = ("稳定版", "Stable"),
        ["UpdateChannelPreview"] = ("预览版", "Preview"),
        ["CurrentVersion"] = ("当前版本", "Current version"),
        ["CheckForUpdates"] = ("检查更新", "Check for updates"),
        ["CheckingUpdate"] = ("正在检查更新…", "Checking for updates…"),
        ["UpdateUpToDate"] = ("已是最新版本。", "You're up to date."),
        ["UpdateNotInstalled"] = ("当前是开发版或便携版，无法自动更新。", "This is a development or portable copy, so it can't update itself."),
        ["UpdateFailed"] = ("检查更新失败，请稍后重试。", "Couldn't check for updates. Try again later."),
        ["UpdateCheckHint"] = ("点「检查更新」查看是否有新版本。", "Click \"Check for updates\" to see if a newer version exists."),
        ["AutoDownloadUpdates"] = ("自动下载更新", "Auto-download updates"),
        // The switch above decides *when* the transfer starts and nothing else, and the two things a person
        // would otherwise have to guess are both in here: that the bytes move in the strip's task queue rather
        // than on this page, and that finishing a download is not the same as installing it. Written as the
        // switch's own hint rather than as a phase line, because it is true in every phase.
        ["AutoDownloadUpdatesHint"] = (
            "开着时，检查到新版本就立刻在后台下载，下载会在底部任务栏里排队显示；它只负责下载，重启安装仍要你自己按下「重启完成更新」。",
            "On: a new version is downloaded in the background as soon as a check finds it, and the transfer "
            + "queues up in the task bar at the bottom. It only downloads — installing still waits for your "
            + "\"Restart to update\"."),
        ["AllowSpawnSessions"] = ("允许助手派生子会话", "Let the assistant spawn helper sessions"),
        ["AllowAssistantQuestions"] = ("允许助手在需要时询问我", "Let the assistant ask me when needed"),
        ["AllowAssistantQuestionsHint"] = (
            "全局设置，适用于所有会话。开启后，助手遇到无法自行判断、需要你决定的选择时可以暂停并询问；关闭后则不提供询问功能。",
            "A global setting for every session. When on, the assistant may pause and ask you to settle a choice it cannot determine; when off, the question tool is unavailable."),
        ["AllowSpawnSessionsHint"] = (
            "允许派生不代表自动拆分或并行执行计划任务；普通编辑、构建和测试仍由当前会话完成。"
            + "只有大量阅读会挤占当前上下文、且只需带回简短结论时，助手才应主动派生，默认只读；"
            + "只有你明确要求委派实现、并行处理或派生执行时，才使用可编辑的子会话。"
            + "每一条都是要花钱的模型调用，并发槽也只有三个，所以默认关闭；一次回答最多派生一个，子会话自己不许再派生。",
            "Allowing spawning does not request automatic splitting or parallel execution of plan tasks; "
            + "ordinary edits, builds and tests stay in the current session. Proactive spawning is only for "
            + "substantial reading that would crowd the current context when only a short conclusion is needed "
            + "back, and defaults to read-only. An editing helper is only for your explicit request to delegate "
            + "implementation, work in parallel, or execute in a spawned session. Each child is a paid model "
            + "call, and there are three answer slots in total, so this ships off. One answer spawns at most "
            + "one helper, and a helper cannot spawn further."),
        // The outbound switch differs from the one above in its default: fetching a page the user named is a basic action of this kind
        // of assistant, so shipping it closed makes the feature nonexistent for anyone who never finds the setting. The copy therefore
        // has to say that "on" is the default, and which class of action a switch-off refuses.
        ["AllowWebFetch"] = ("允许助手抓取网页", "Let the assistant read web pages"),
        ["AllowWebFetchHint"] = (
            "默认开启。抓取只走 https、只允许公网主机，重定向不许跨出 https，一次最多读 1 MiB；把脚本、样式和导航"
            + "去掉之后，正文才交回模型。关掉它只是让这条通道不存在——命令沙箱里的 curl 从来不受这个开关约束，"
            + "「询问审批」档下每一次抓取都照样要过一张卡。",
            "On by default. Fetching is https only, to public hosts, with a redirect never allowed to leave "
            + "https, and at most 1 MiB is read; scripts, styles and navigation are stripped before the page text "
            + "reaches the model. Turning this off removes the channel — it has never governed curl inside the "
            + "command sandbox — and in 「询问审批」 every fetch still asks for approval on a card."),
        ["AllowWebSearch"] = ("允许联网搜索", "Allow web search"),
        ["AllowWebSearchHint"] = (
            "默认开启。Hub 按提供商配置自动选择搜索方式：DeepSeek 预设使用官方 Search/Fetch API，可能按成功请求次数收费；其他声明支持联网搜索的提供商使用其托管搜索，也可能产生服务商费用。DeepSeek 调用次数和估算费用会显示在当前会话。不想产生这笔费用就关掉它——之后需要联网的问题，助手只能直接说明它查不了。",
            "On by default. Hub chooses the search route from the provider configuration: the DeepSeek preset uses its official Search/Fetch API, which may charge per successful request; other providers that declare hosted search use that route and may charge as well. DeepSeek call counts and estimated cost appear in the current session. Turn it off if you do not want the spend — a question that needs the web then gets a plain answer saying it could not search."),
        ["DeepSeekWebUsageFormat"] = ("DeepSeek 网页工具：搜索 {0} 次（{1} 次计费），抓取 {2} 次（{3} 次计费）；估算费用 ¥{4:F3}", "DeepSeek web tools: {0} searches ({1} billable), {2} fetches ({3} billable); estimated cost ¥{4:F3}"),
        // The one switch on the appearance pane that is not about appearance. It sits there rather than under
        // permission because it decides what a person reads, never what the assistant may do — and its hint has
        // to say that turning it off buys no tokens back, since the obvious guess about a hidden chain of
        // thought is that hiding it also stops the model from thinking.
        ["ChatDisplay"] = ("对话显示", "Chat display"),
        ["ShowThinking"] = ("显示模型的思考过程", "Show the model's thinking"),
        ["ShowThinkingHint"] = (
            "默认开启。思考的那一段在回答上方实时出现，正文一开始就自动收起，之后点开才再看得到。"
            + "关掉它只是不上屏：思考仍然会生成、仍然按 token 计费，也仍然要原样回发给服务商——"
            + "带工具调用的请求少了这一段会被直接拒绝。",
            "On by default. The thinking appears above the answer as it arrives, then folds away once the "
            + "answer starts — open it to read it again. Switching this off only stops the drawing: the thinking "
            + "is still generated, still billed by the token, and still sent back to the provider verbatim, and "
            + "a request that carries tool calls is rejected without it."),
        ["UpdateReady"] = ("发现新版本 {0}。", "Version {0} is available."),
        // The settings card's phase line: the line above names the version and stays true through every phase,
        // and this one says where the transfer is and which control the user still owes a press. It is a
        // separate line rather than a prefix on the version so that the number the user just read never moves
        // and never disappears while the bytes are moving somewhere else. The card keeps no percentage of its
        // own: the strip's queue row is what measures the transfer, and two measuring surfaces would be one
        // fact told twice.
        ["UpdatePendingDownloadHint"] = (
            "这个版本还没开始下载：用侧栏的「下载更新」开始，进度会出现在底部任务栏。",
            "This version is not downloaded yet. Start it with \"Download update\" in the sidebar; progress "
            + "appears in the task bar."),
        ["UpdateDownloadingHint"] = (
            "更新正在后台下载，进度和取消在底部任务栏。",
            "The update is downloading in the background; progress and cancel are in the task bar."),
        // The version the restart will install rides on the strip slot's tooltip: the label there counts, so
        // this is where the number has to be said. The download itself is a queue row now, and the settings
        // card keeps only the line above — which therefore stays true through every phase and no longer needs
        // a prefix composed per phase.
        ["UpdateRestartActionTip"] = ("重启并安装 {0}", "Restart and install {0}"),
        // ── Task queue ──
        // A queue row names the thing it is fetching, and the version rides behind that name **unformatted**:
        // a version number is not a localized word, and a format key would print a bare "{0}" on the one path
        // that has no version yet (install the latest). See TaskQueue.Item.Subject.
        ["TaskEnginePackage"] = ("Axmol 引擎", "Axmol engine"),
        ["TaskHostShell"] = ("PowerShell 7 安装程序", "PowerShell 7 installer"),
        // The app's own update. The version rides behind the name like every other row, so this is the one
        // product name in the table that is deliberately not translated.
        ["TaskHubUpdate"] = ("Axmol Hub", "Axmol Hub"),
        // Said by a row (and by the install button) whose bytes are already on their way.
        ["TaskInFlightSuffix"] = ("正在进行", "In progress"),
        // The slot's other action mode: an update is known, auto-download is off, and nobody has fetched it.
        // The tip says what the press does and what it deliberately does not — with auto-download off there
        // was no way to ask at all, and the only place to ask used to be the settings page.
        ["TaskDownloadAction"] = ("下载更新", "Download update"),
        ["TaskDownloadSlotTip"] = ("发现新版本 {0}，下载完由你决定何时重启。", "Version {0} is available. Download it, then choose when to restart."),
        // The queue row's tooltip for a transport that reports a percentage and no bytes: what its owner
        // estimated the transfer to be. A prefix, not a sentence — the rate is appended with " · " exactly
        // as the measured column does it.
        ["TaskEstimateSize"] = ("约 ", "About "),
        // ── The strip's download slot and the queue popover ──
        // The slot is one sentence in a 28px strip, so it counts rather than narrates. The percentage only
        // appears when a single transfer is moving: averaging two rates into one number would be a figure
        // that describes neither download.
        ["TaskSlotFormat"] = ("任务 {0}/{1}", "Tasks {0}/{1}"),
        ["TaskSlotPercentFormat"] = ("任务 {0}/{1} · {2}%", "Tasks {0}/{1} · {2}%"),
        ["TaskSlotFailed"] = ("{0} 个任务失败", "{0} task(s) failed"),
        ["TaskListTitle"] = ("任务列表", "Tasks"),
        ["TaskSlotTip"] = ("查看正在跑的任务", "Show what is running"),
        // On a change's row: the stage is here, the detail is in the log drawer, and this is the only press
        // that leaves the popover to go read it.
        ["TaskLogAction"] = ("看日志", "View log"),
        // The slot's action mode: the bytes have landed and only a process swap is missing. The armed form
        // is the second tap of a restart pressed while something is still working — the word on the button
        // is the warning, same shape as a steer waiting for its second tap.
        ["TaskRestartAction"] = ("重启完成更新", "Restart to update"),
        ["TaskRestartArmed"] = ("再点一次重启", "Tap again to restart"),
        // A failed row is the only settled state with an answer still left to give, and it gets both: try
        // that transfer again, or say you have read it so the strip can go quiet about it.
        ["TaskRetry"] = ("重试", "Retry"),        ["TaskDismiss"] = ("忽略", "Dismiss"),
        // The one question the queue is allowed to interrupt with, and it says only what the press does:
        // stopping the transfers. That the bytes already down survive is a property of DownloadManager's
        // .partial + Range transport, not something the app acts on by itself — a restart empties the list
        // and the next install is what picks the bytes back up — so the dialog does not hint at one.
        ["QuitDuringDownloadsTitle"] = ("任务还没结束", "Tasks still running"),
        // Says only that quitting now stops the work — never that a later launch resumes it, which is the
        // ruling this copy carries. The noun is 任务 because the slot counts tasks; the transfers remain the
        // only thing that can bring this dialog up, a change being cancelled rather than asked about.
        ["QuitDuringDownloadsBody"] = ("还有 {0} 个任务在跑。现在退出会中断它们。", "{0} task(s) are still running. Quitting now stops them."),
        ["TaskQueued"] = ("等待中", "Queued"),
        ["TaskTransferring"] = ("下载中", "Downloading"),
        // A change that is running but has not named a stage yet. Not 下载中: a compile is running nothing down.
        ["TaskRunning"] = ("进行中", "In progress"),
        // The bytes have arrived and the rest is local work — unzipping an engine, patching an update. One
        // word covers both because the row cannot promise which it is doing.
        ["TaskApplying"] = ("正在处理", "Applying"),
        ["TaskDone"] = ("完成", "Done"),
        ["TaskFailed"] = ("失败", "Failed"),
        ["TaskCancelled"] = ("已取消", "Cancelled"),
        // A transfer with no measurable progress still shows this rather than an empty space, so the row
        // never reads as a stalled number.
        ["TaskNoMeasure"] = ("—", "—"),
        // Said by every engine operation whose own download has not landed yet: the transfer is replacing the
        // directory the operation is about to read or delete.
        ["EngineBusyTransferring"] = ("这个引擎正在下载，等它结束后再操作。", "This engine is still downloading. Wait for the transfer to finish."),
        // Built-in AI assistant. The protocol baseline is OpenAI-compatible; the first phase's providers = OrcaRouter + custom.
        ["Assistant"] = ("AI 助手", "AI assistant"),
        ["AssistantHint"] = ("向助手提问 Axmol 相关问题。模型由你配置的 provider 提供。", "Ask the assistant about Axmol. Answers come from the provider you configure."),
        ["AssistantEmpty"] = ("开始新的对话。", "Start a new session."),
        ["NewConversation"] = ("新建对话", "New session"),
        ["DeleteConversation"] = ("删除对话", "Delete session"),
        ["SearchConversations"] = ("搜索对话", "Search sessions"),
        ["NoSearchResults"] = ("没有匹配的对话。", "No matching sessions."),
        ["ConversationActions"] = ("对话操作", "Session actions"),
        ["Conversation"] = ("对话", "Session"),
        ["NoConversations"] = ("还没有对话。", "No sessions yet."),
        ["NoAvailableChatModels"] = ("请先在设置中鉴权并添加可用模型。", "Authenticate a provider and add an available model in Settings first."),
        ["Send"] = ("发送", "Send"),
        ["Stop"] = ("停止", "Stop"),
        ["ChatSteer"] = ("引导", "Steer"),
        ["ChatSteering"] = ("正在引导…", "Steering"),
        // The keyboard used to be the only thing this hint named, and a screenshot arriving from Explorer or
        // Ctrl+V is the interaction people reach for most. It is one string, so the composer says it once.
        ["InputPlaceholder"] = ("输入消息，Enter 发送，Shift+Enter 换行；截图可以粘贴或拖进来",
            "Type a message. Enter to send, Shift+Enter for a newline. Paste or drop a screenshot"),
        ["ChatModeAsk"] = ("提问", "Ask"),
        ["ChatModePlan"] = ("计划", "Plan"),
        ["ChatModeAgent"] = ("代理", "Agent"),
        ["ChatModeGoal"] = ("目标", "Goal"),
        ["ChatModeResetHint"] = ("点击恢复默认 Agent 模式", "Click to return to the default Agent mode"),
        ["ChatGoalDraftResetHint"] = ("取消这条消息的目标标记", "Remove the goal marker from this message"),
        ["ChatGoalSentAsGoal"] = ("作为目标发送", "Sent as goal"),
        ["ChatGoalActive"] = ("进行中的目标", "Active goal"),
        ["ChatGoalPaused"] = ("已暂停的目标", "Paused goal"),
        ["ChatGoalBudgetLimited"] = ("已达到本次自动续作次数上限", "Automatic continuation limit reached"),
        ["ChatGoalNoActivity"] = ("目标已暂停：连续两次未调用工具", "Goal paused: no tool activity in two runs"),
        ["ChatGoalBlocked"] = ("目标已暂停：需要处理阻碍", "Goal paused: action needed"),
        ["ChatGoalLimitTip"] = ("自动续作次数已用完，不代表模型或账户额度耗尽。可查看最近回复后继续。", "The automatic continuation count was exhausted, not the model or account quota. Review the latest reply before resuming."),
        ["ChatGoalNoActivityTip"] = ("连续两次运行没有工具结果；这不一定代表没有进展。请查看回复后继续或补充指示。", "Two runs produced no tool results; this does not necessarily mean no progress. Review the replies before resuming or adding instructions."),
        ["ChatGoalBlockedTip"] = ("助手报告需要外部条件或用户操作。请查看最近回复。", "The assistant reported an external prerequisite or user action. Review its latest reply."),
        ["ChatGoalCleared"] = ("已清除的目标", "Cleared goal"),
        ["ChatGoalPause"] = ("暂停目标", "Pause goal"),
        ["ChatGoalResume"] = ("继续目标", "Resume goal"),
        ["ChatGoalClear"] = ("清除目标", "Clear goal"),
        ["ChatReasoningLow"] = ("推理：低", "Reasoning: Low"),
        ["ChatReasoningMedium"] = ("推理：中", "Reasoning: Medium"),
        ["ChatReasoningHigh"] = ("推理：高", "Reasoning: High"),
        ["ChatReasoningXHigh"] = ("推理：超高", "Reasoning: Extra high"),
        ["ChatReasoningMax"] = ("推理：最大", "Reasoning: Max"),
        ["ChatReasoningUltra"] = ("推理：极致", "Reasoning: Ultra"),
        ["ChatAutoReasoning"] = ("自动推理", "Auto reasoning"),
        ["ChatEffortLow"] = ("低", "Low"),
        ["ChatEffortMedium"] = ("中", "Medium"),
        ["ChatEffortHigh"] = ("高", "High"),
        ["ChatEffortXHigh"] = ("超高", "Extra high"),
        ["ChatEffortMax"] = ("最大", "Max"),
        ["ChatEffortUltra"] = ("极致", "Ultra"),
        ["ChatReasoningLevelFormat"] = ("推理：{0}", "Reasoning: {0}"),
        ["ChatReasoningUnavailable"] = ("此模型不支持推理档位", "This model does not support reasoning effort"),
        // The short chip label names automatic reasoning only; it does not imply that the gateway model is auto-selected.
        ["ChatRoutingMenu"] = ("档位由谁决定", "Who picks the tier"),
        ["ChatRoutingManual"] = ("我手动选", "I pick manually"),
        ["ChatRoutingAuto"] = ("自动（按任务强度）", "Auto (by task strength)"),
        ["ChatRoutingAutoChip"] = ("自动", "Auto"),
        ["ChatContextEstimateFormat"] = ("预计上下文：{0} / {1} tokens（{2}%），仅为本地估算", "Estimated context: {0} / {1} tokens ({2}%), local estimate only"),
        // The other reading of the same ring: the model itself said what the last request cost, so the number is
        // no longer a guess. Two keys because two different claims, and "Estimated" over a measured figure is a lie.
        ["ChatContextReportedFormat"] = ("上下文：{0} / {1} tokens（{2}%），上次请求实测", "Context: {0} / {1} tokens ({2}%), measured from the last request"),
        ["ChatContextEstimateHint"] = ("按字符数估算，实际 token 用量可能不同。", "Estimated from text length; actual token usage may differ."),
        ["ChatContextWindow"] = ("上下文窗口", "Context window"),
        ["ChatContextPopoverHint"] = ("展示当前任务的上下文占用情况；压缩会摘要早期内容，需等待片刻并消耗少量积分。", "Shows how much of the current task's context is in use. Compression summarizes earlier messages and may take a moment and use a small amount of credits."),
        // The meter's rows. Each one is something a person can act on: the schemas by changing the mode, the
        // attachments by un-adding a folder, the messages by compressing. A row nobody can act on is decoration.
        ["ChatContextCatSystem"] = ("系统提示", "System prompt"),
        ["ChatContextCatCharter"] = ("项目规则", "Project rules"),
        ["ChatContextCatSummary"] = ("历史摘要", "Earlier summary"),
        ["ChatContextCatMemory"] = ("记忆索引", "Memory index"),
        ["ChatContextCatTools"] = ("工具声明", "Tool schemas"),
        ["ChatContextCatMessages"] = ("消息·调用·结果·推理", "Messages, calls, results, reasoning"),
        ["ChatContextCatAttachments"] = ("附件文本", "Attached text"),
        ["ChatContextCatImages"] = ("图片", "Images"),
        ["ChatContextCatReserve"] = ("回复预留", "Held for the reply"),
        ["ChatContextCatFree"] = ("空闲", "Free space"),
        // Where the denominator came from. The window number decides when a session compacts, so a reading that
        // cannot say whether 128 000 was reported or assumed cannot be argued with.
        ["ChatContextSourceFormat"] = ("窗口 {1} tokens，来源：{0}", "Window of {1} tokens, from {0}"),
        ["ChatContextMeasuredNote"] = ("{0} tokens 是上次请求的实测值，它之后的新增按估算补上。",
            "{0} tokens is what the last request measured; the turns since then are estimated on top."),
        // What the answer <i>cost</i>, as opposed to how much of the window it fills — the rows above are an
        // estimate of the latter, and the note above is one request's measured input. These two lines are the
        // measured spend: every request of the round summed, because a tool loop pays for the transcript again
        // each time it re-sends it. Wording keeps 占用 and 消耗 apart on purpose; the two numbers disagree and
        // both are right.
        // The generation speed of the session, quoted the way DeepSeek Harness's stats line quotes it:
        // decodeTokens / decodeMs, summed over every answer rather than re-derived from a rolling window. No
        // placeholder wording exists on purpose — a session that has decoded nothing shows no speed at all, and
        // "waiting for output" over a stream that is visibly producing text is the one thing this must never say.
        ["ChatRequestSpeedFormat"] = ("{0} tok/s", "{0} tok/s"),
        ["ChatRequestSpeedHint"] = ("本会话的生成速度：输出 tokens ÷ 解码耗时（不含等待工具与审批的时间）",
            "Generation speed for this session: output tokens ÷ decode time, excluding tool and approval waits"),
        ["ChatSessionTokensFormat"] = ("{0} tok", "{0} tok"),
        ["ChatSessionCacheFormat"] = ("缓存命中 {0}%", "cache hit {0}%"),
        ["ChatContextBurnSendLabel"] = ("本轮实测消耗（tokens）：", "This reply measured (tokens): "),
        ["ChatContextBurnSessionLabel"] = ("本会话累计（tokens）：", "Session total (tokens): "),
        ["ChatContextBurnInFormat"] = ("输入 {0}", "input {0}"),
        ["ChatContextBurnOutFormat"] = ("输出 {0}", "output {0}"),
        // Its own parentheses, because the clause sits inside the output it is a part of and the two languages
        // bracket it differently.
        ["ChatContextBurnReasoningFormat"] = ("（含思考 {0}）", " ({0} of them reasoning)"),
        ["ChatContextBurnCachedFormat"] = ("缓存输入 {0}", "{0} cached"),
        // The share of the input the endpoint did not have to re-bill. Its own key rather than a suffix on the
        // cached count: a ratio over summed input is a different claim from that one request's cached figure.
        ["ChatContextBurnCacheHitFormat"] = ("缓存命中率 {0}%", "cache hit {0}%"),
        // The same ratio as the panel's first figure, where the label and the number are drawn apart: a headline
        // reading with its own two operands underneath, because a percentage nobody can decompose is a number to
        // trust rather than to check. Not the burn line's wording — there the ratio trails a sentence, here it is
        // the sentence.
        ["ChatContextCacheHitTitle"] = ("缓存命中率", "Cache hit rate"),
        ["ChatContextCacheHitDetailFormat"] = ("总输入 {0} · 命中 {1}", "{0} input · {1} cached"),
        ["ChatContextBurnRequestsFormat"] = ("{0} 次请求", "{0} requests"),
        // Both numbers, because the two are not the same claim: the provider published one, this session's own
        // response proved the other. A reader who sees only the smaller figure would look for a setting nobody set.
        ["ChatContextDriftNote"] = ("这条会话的实测把窗口从 {0} 收窄到 {1}。",
            "This session's own reading corrected the window from {0} to {1}."),
        ["ChatContextDroppedFormat"] = ("最早 {0} 轮没有发出去，只发了最近的部分", "{0} oldest turns were not sent — only the newest part goes out"),
        ["ChatContextOverfull"] = ("已经超出这个会话可用的窗口：先压缩，或去掉一个附件。",
            "Past the room this session has: compress it, or drop an attachment."),
        ["ChatCompressContext"] = ("压缩上下文", "Compress context"),
        ["ChatCompressingContext"] = ("正在压缩上下文…", "Compressing context…"),
        // The run goes silent while the summariser works: no token, no tool call, nothing that would prove it is
        // alive. A state rather than the button's verb above, because nobody is being asked to do anything — it
        // says what the session is doing, not what a control would do. Drawn on the bubble's status line while a
        // run is on screen, and in the strip below the transcript when none is.
        ["ChatCompactingContext"] = ("上下文压缩中…", "Compacting context…"),
        ["ChatContextCompressUnavailable"] = ("当前对话没有足够的早期内容可供压缩。", "There is not enough earlier session content to compress yet."),
        ["ChatContextCompressFailed"] = ("压缩上下文失败：", "Could not compress context: "),
        ["ChatContextCompressing"] = ("上下文压缩完成前，暂时不能发送或重试消息。", "Sending or retrying is unavailable until context compression finishes."),
        ["ChatAddContext"] = ("添加上下文", "Add context"),
        ["ChatAddLocalFolder"] = ("添加本地文件夹…", "Add local folder…"),
        ["ChatAddLocalFile"] = ("添加本地文件…", "Add local file…"),
        // Pictures are a second channel next to the text attachments: a folder becomes bytes of text in the
        // prompt, a picture stays a picture. Same composer, same chips, never the same reader.
        ["ChatAddImage"] = ("添加图片…", "Add image…"),
        ["ChatPickImageTitle"] = ("选择要发给助手的图片", "Choose an image to send to the assistant"),
        ["ChatImageEmpty"] = ("没有可附上的图片内容。", "There are no image bytes to attach."),
        ["ChatImageUnrecognized"] = ("这不是 PNG / JPEG / GIF / WebP 位图，Hub 没有可发送的媒体类型。矢量或源码文件（例如 SVG）请走文本通道：把它所在的文件夹作为上下文添加，或让助手直接读它的源码。",
            "That is not a PNG, JPEG, GIF or WebP bitmap, so Hub has no media type to send. A vector or source file such as SVG belongs in the text channel: add its folder as context, or let the assistant read it as source."),
        ["ChatImageTooLarge"] = ("这张图片太大，Hub 单张最多发送 {0} MiB。", "That image is too large; Hub sends at most {0} MiB per picture."),
        ["ChatImageTooMany"] = ("一条消息最多带 {0} 张图片，其余的请在下一条发送。",
            "A message carries at most {0} images. Send the rest in the next one."),
        ["ChatImageGone"] = ("这张附图已不在会话目录里。", "This attached image is no longer in the session directory."),
        // A pasted capture has no file name, and the chip still needs words under the thumbnail when the picture
        // cannot be decoded — naming the gesture is the only thing that identifies which attachment it was.
        ["ChatPastedImageName"] = ("剪贴板图片", "Pasted image"),
        ["ChatImageNotLocal"] = ("那个位置读不到本地文件，请从磁盘上选一张图片。",
            "That location could not be read as a local file. Pick a picture from disk."),
        // The preview's two buttons and its receipt. The copy is confirmed in words because a clipboard write is
        // invisible: without a line saying so, "nothing happened" and "it is in there" look identical.
        ["ChatPictureCopy"] = ("复制这张图", "Copy this picture"),
        ["ChatPictureClose"] = ("关闭预览", "Close preview"),
        ["ChatPictureCopied"] = ("图片已复制到剪贴板。", "The picture is on the clipboard."),
        ["ChatAddHubProject"] = ("添加 Axmol Hub 工程", "Add Axmol Hub project"),
        ["ChatNoHubProjects"] = ("Hub 中尚未登记工程", "No projects registered in Hub"),
        ["ChatPickFolderTitle"] = ("选择要作为上下文的文件夹", "Choose a folder to use as context"),
        ["ChatFolderNotLocal"] = ("所选位置无法作为本地文件夹读取。", "The selected location cannot be read as a local folder."),
        ["ChatPickFileTitle"] = ("选择要作为上下文的文件", "Choose a file to use as context"),
        ["ChatFileNotLocal"] = ("所选位置无法作为本地文件读取。", "The selected location cannot be read as a local file."),
        // The file channel's two refusals. Both are about one file a person pointed at, so both name the rule that
        // fired and the way round it: the extension is what decides, and a large file is read in a window instead.
        ["ChatFileNotText"] = ("这不是 Hub 能作为上下文读取的文本文件（按扩展名判定）。源码与文本文件可以直接附上；二进制文件请让助手用 run_command 处理。",
            "That is not a text file Hub can read as context (the extension is what decides). Source and text files attach directly; for a binary, have the assistant use run_command."),
        ["ChatFileTooLarge"] = ("这个文件太大，作为上下文单文件最多 {0} MiB。用 fs_read 分窗口读它，或改附它所在的文件夹。",
            "That file is too large; one context file is capped at {0} MiB. Read it in windows with fs_read, or attach its folder instead."),
        // The session sandbox. Distinct from "add a folder as context": that one attaches text to a message, this
        // one decides where the assistant is allowed to write and run commands.
        ["ChatWorkspaceNone"] = ("未设工作目录", "No workspace"),
        ["ChatWorkspaceChipHint"] = ("助手可以读写文件、运行命令的目录。未设置时这些工具一律拒答。", "The directory the assistant may read, write and run commands in. Until one is set, those tools refuse."),
        ["ChatWorkspaceChipHintFormat"] = ("助手被限制在这个目录内：{0}", "The assistant is confined to this directory: {0}"),
        ["ChatWorkspacePickTitle"] = ("选择助手的工作目录", "Choose the assistant's workspace"),
        ["ChatWorkspaceMenuChoose"] = ("选择工作目录…", "Choose a workspace folder…"),
        ["ChatWorkspaceMenuClear"] = ("清除工作目录", "Clear the workspace"),
        ["ChatWorkspaceCleared"] = ("已清除工作目录，文件与命令工具将拒答。", "Workspace cleared. The file and command tools will now refuse."),
        ["ChatWorkspaceRejected"] = ("这个目录不能用作工作目录：", "That directory cannot be used as a workspace: "),
        ["ChatWorkspaceProtected"] = ("这是 Hub 的数据目录、引擎安装目录或 .git，不能作为助手的工作目录。要在会话里读它，用「授权一个目录…」——那是只读的。", "That is Hub's data directory, an engine installation or a .git directory, so it cannot be the assistant's workspace. To read one in a session, use “Grant a folder…” — that grant is read-only."),
        // The directories beyond the workspace that this session was granted on a card. Their own words, because a
        // grant is a permission the user gave and must be able to take back — and the count is on the line so the
        // chip's tooltip answers "how much did I open" without being read to the end.
        ["ChatWorkspaceGrantedHintFormat"] = ("\n另有 {0} 个已授权目录：{1}", "\nAlso granted to this session ({0}): {1}"),
        // The other list on the same chip. A protected location is granted for reading, and a tooltip that folded
        // the two together would promise the writing the file tools refuse.
        ["ChatWorkspaceReadOnlyHintFormat"] = ("\n另有 {0} 个只读授权目录：{1}", "\nGranted read-only to this session ({0}): {1}"),
        ["ChatWorkspaceGrantedMenu"] = ("已授权的目录", "Granted to this session"),
        ["ChatWorkspaceRevokeFormat"] = ("撤销对 {0} 的授权", "Revoke access to {0}"),
        ["ChatWorkspaceRevokeReadOnlyFormat"] = ("撤销对 {0} 的只读授权", "Revoke read-only access to {0}"),
        ["ChatWorkspaceRevoked"] = ("已撤销该目录的授权，助手不再读写它。", "That directory is no longer granted; the assistant cannot read or write there now."),
        // The single files a card opened by name. Their own heading and their own hint line, because "granted" means
        // one exact path here and not the folder it happens to sit in — a tooltip that read them as directories would
        // re-offer listing and searching, which that card never gave.
        ["ChatWorkspaceGrantedFilesHintFormat"] = ("\n另有 {0} 个已授权文件：{1}", "\nAlso granted as single files ({0}): {1}"),
        ["ChatWorkspaceGrantedFilesMenu"] = ("已授权的文件", "Granted files"),
        ["ChatWorkspaceFileReadOnlySuffix"] = ("（只读）", " (read-only)"),
        ["ChatWorkspaceFileRevokeFormat"] = ("撤销对 {0} 的授权", "Revoke access to {0}"),
        ["ChatWorkspaceFileRevokeReadOnlyFormat"] = ("撤销对 {0} 的只读授权", "Revoke read-only access to {0}"),
        ["ChatWorkspaceFileRevoked"] = ("已撤销该文件的授权，助手不再读写它。", "That file is no longer granted; the assistant cannot read or write it now."),
        // The person's own door to the same grant: the menu reads a folder and records it exactly as an approved
        // card would, so a task that needs a second directory does not depend on the assistant being willing to ask.
        // Each refusal below is the panel's own words for a `GrantVerdict` — the tool's sentences are for the model.
        ["ChatWorkspaceMenuGrant"] = ("授权一个目录…", "Grant a folder…"),
        ["ChatWorkspaceGrantPickTitle"] = ("选择要授权给本会话的目录", "Choose a folder to grant to this session"),
        ["ChatWorkspaceGrantDoneFormat"] = ("已授权「{0}」，助手现在可以读写它。", "“{0}” is granted: the assistant can read and write there now."),
        // The same answer, narrower, for the three kinds of protected location: the grant is a reading one, and the
        // notice says so at the moment the person makes it rather than at the first refused write.
        ["ChatWorkspaceGrantReadOnlyFormat"] = ("已授权「{0}」（只读）：这是 Hub 的数据目录、引擎安装目录或 .git，助手可以读它，但不能写。", "“{0}” is granted read-only: that is Hub's data directory, an engine installation or a .git, so the assistant can read it and never write it."),
        ["ChatWorkspaceGrantSameFormat"] = ("「{0}」就是本会话的工作目录，不需要授权。", "“{0}” is this session's workspace already, so it needs no grant."),
        ["ChatWorkspaceGrantInsideFormat"] = ("「{0}」在工作目录里面，相对路径已经能到它。", "“{0}” is inside the workspace, so a relative path already reaches it."),
        ["ChatWorkspaceGrantAlreadyFormat"] = ("「{0}」已经授权给本会话了。", "“{0}” is already granted to this session."),
        ["ChatWorkspaceGrantInsideGrantFormat"] = ("「{0}」已经在已授权的「{1}」里面。", "“{0}” is already inside “{1}”, which this session holds."),
        ["ChatWorkspaceGrantContainsWorkspace"] = ("这个目录包含本会话的工作目录，授权它会打开比你要的更多的地方。", "That folder contains this session's workspace, so granting it would open more than the folder you asked about."),
        ["ChatWorkspaceGrantContainsGrantFormat"] = ("这个目录包含已单独授权的「{0}」，请选一个更窄的目录。", "That folder contains “{0}”, which was granted on its own; pick a narrower one."),
        ["ChatWorkspaceGrantUnusable"] = ("这个目录不能用：它不存在，或读不到。", "That folder cannot be used: it is missing, or it cannot be read."),
        ["ChatWorkspaceGrantSessionGone"] = ("这个会话已经不在了，什么都没记下来。", "That session is gone, so nothing was recorded."),
        ["ChatWorkspaceOutside"] = ("这个目录不在本会话允许的范围内（工作目录或已授权目录）。", "That directory is not inside this session's allowed directories — its workspace or one granted to it."),
        ["ChatAttachmentFailed"] = ("读取聊天上下文失败：", "Could not read chat context: "),
        ["ChatFolderPrefix"] = ("文件夹：", "Folder: "),
        ["ChatFilePrefix"] = ("文件：", "File: "),
        ["ChatProjectPrefix"] = ("工程：", "Project: "),
        ["ChatProjectContextFormat"] = ("Axmol Hub project context — engine {0}; target {1}; configuration {2}; build status {3}.", "Axmol Hub project context — engine {0}; target {1}; configuration {2}; build status {3}."),
        ["ChatToolProjects"] = ("项目", "projects"),
        ["ChatToolEngines"] = ("引擎", "engines"),
        ["ChatToolchains"] = ("工具链状态", "toolchain status"),
        ["ChatToolRunningFormat"] = ("正在读取{0}", "Viewing {0}"),
        ["ChatToolCompletedFormat"] = ("已读取{0}，正在继续", "Viewed {0}; continuing"),
        ["Provider"] = ("模型提供商", "Provider"),
        // Title and description of the AI model provider management card on the Settings page.
        ["ModelProviders"] = ("模型提供商", "Model providers"),
        ["ModelProvidersHint"] = ("助手使用的模型接口。「添加提供商」可从内置预设中挑选（OrcaRouter、OpenAI、DeepSeek、Ollama…），已配置的提供商在此管理密钥与模型；助手页只负责选择用哪一个。", "The endpoints the assistant can use. \"Add provider\" picks from built-in presets (OrcaRouter, OpenAI, DeepSeek, Ollama …); keys and models for the ones you configured are managed here, while the assistant page only chooses between them."),
        // Model hint on the assistant page; {0} = provider name, {1} = model name.
        ["ActiveModelFormat"] = ("当前模型：{0} · {1}", "Current model: {0} · {1}"),
        ["NoProvider"] = ("尚未配置模型提供商。", "No model provider is configured yet."),
        ["ApiKey"] = ("API 密钥", "API key"),
        // The wording states only what is true on both backend tiers. "Stored in the operating system's credential store" is wrong on the
        // Linux file backend, and the backend line (AuthSecretBackendFile) is what makes "where this key actually went" clear.
        ["ApiKeyHint"] = ("密钥只保存在本机，不会写入配置文件或日志。", "The key stays on this machine and is never written to config files or logs."),
        ["ApiKeyPlatformUnsupported"] = ("当前平台不支持安全保存密钥。", "Secure key storage is not available on this platform."),
        // Backend disclosure: the three tiers guard against different things, and one blanket "safely stored" would flatten the difference, so the store itself reports which tier it is.
        ["AuthOpenAiTitle"] = ("使用 ChatGPT 登录", "Sign in with ChatGPT"),
        ["AuthOpenAiHint"] = ("在浏览器中完成登录。如果无法自动返回，请复制浏览器地址栏中的完整回调 URL 并粘贴到下方。", "Complete sign-in in your browser. If it cannot return automatically, copy the full callback URL from the browser address bar and paste it below."),
        ["AuthOpenAiOpen"] = ("打开浏览器", "Open browser"),
        ["AuthOpenAiCopy"] = ("复制登录链接", "Copy sign-in link"),
        ["AuthOpenAiCallback"] = ("粘贴完整回调 URL", "Paste the full callback URL"),
        ["AuthOpenAiSubmit"] = ("完成登录", "Complete sign-in"),
        ["AuthOpenAiInvalid"] = ("回调地址无效、已处理或不属于本次登录，请重新复制。", "The callback is invalid, already handled, or belongs to another sign-in. Copy it again."),
        ["AuthSecretBackendKeychain"] = ("密钥和登录令牌保存在 macOS 钥匙串中。", "Keys and sign-in tokens are stored in the macOS Keychain."),
        ["AuthSecretBackendFile"] = ("此平台的密钥保存在本机加密文件中（数据密钥在用户配置目录，权限 600），而非系统凭据存储。", "On this platform keys are kept in an encrypted local file (data key in your profile directory, mode 600) rather than the OS credential store."),
        ["AuthSecretBackendDegraded"] = ("已存储的密钥有无法读取的项，重新输入即可覆盖：", "A stored key could not be read; entering it again replaces it: "),
        ["AuthNoBrowserLink"] = ("无法打开浏览器，请手动访问：", "No browser could be opened. Visit this link by hand: "),
        ["SaveApiKey"] = ("保存密钥", "Save key"),
        // Affiliate disclosure: OrcaRouter takes part in the OSS programme; registering through this project's referral link lets Simdsoft earn a commission, and all of that income funds development of the Axmol engine and its tooling (see planning D5).
        ["AffiliateDisclosure"] = ("通过本项目的推荐链接注册 {0}，Simdsoft 可获得分成，所得收入将全部用于 Axmol 引擎及相关工具的研发。", "Signing up for {0} through this project's referral link earns Simdsoft a commission, all of which goes toward developing the Axmol engine and its tooling."),
        ["ViewReferral"] = ("了解详情", "Learn more"),
        ["ChatFailed"] = ("助手请求失败：", "Assistant request failed: "),
        ["ChatConnectionFailed"] = ("连接模型服务失败：", "Could not connect to the model provider: "),
        ["ChatRateLimited"] = ("模型服务在限流，稍后再试：", "The model provider is rate-limiting requests, try again shortly: "),
        // A spent allowance is not a rate limit: waiting seconds fixes one and not the other, and the difference
        // is hours, so the sentence has to say what is actually owed instead of "try again shortly".
        ["ChatUsageExhausted"] = ("套餐额度已用尽，等额度窗口恢复后再试（这不是限流）：", "This plan's usage allowance is spent; wait for it to reset — this is not rate limiting: "),
        // Three endings that used to share one sentence, because they are three different things to do about: a
        // stream that went quiet mid-reply, a round the provider never opened, and a round that never opened
        // because the provider kept saying "not yet". Saying "check your network" for all three sent people to
        // diagnose a cable that was fine, on a 180 000-token transcript that was simply still being read.
        ["ChatTimedOut"] = ("模型在输出途中停了太久，本轮结束。可以再问一次。", "The model went quiet mid-reply for too long, so this round ended. Ask again."),
        ["ChatNoFirstFrame"] = ("模型这一轮始终没有回应，是没开始，不是中断。可以再问一次。", "This round never got any response from the model — it never started, rather than stopping. Ask again."),
        ["ChatTimedOutAfterRetries"] = ("模型服务一直在限流，等完它的等待后这一轮仍未开始。稍后再试。", "The provider kept rate-limiting, and after sitting out its waits this round still never started. Try again shortly."),
        // The fourth ending, and the only one that is not about the model being slow: something below Hub cancelled
        // the request. Said apart because a timeout sentence about a wait Hub never armed would send a person to
        // check a clock that was innocent.
        ["ChatStreamInterrupted"] = ("模型请求被客户端流水线中断，不是 Hub 的等待窗口到期。", "The model request was cancelled by the client pipeline, not by a waiting deadline Hub set."),
        // The status line while the pipeline obeys a Retry-After: the wait is the provider's instruction, so it
        // is said as work in progress on the row the tool and the search already use, not as a second label.
        ["ChatRetryWaitFormat"] = ("模型服务在限流，第 {0}/{1} 次尝试，等待 {2} 秒", "Rate-limited by the provider: attempt {0}/{1}, waiting {2}s"),
        ["ChatNoResponse"] = ("端点这次什么都没返回，也没有给出结束原因——通常是服务端临时故障或连接提前断开，先直接重试。", "The endpoint returned nothing at all, and no finish reason — usually a transient server fault or a connection closed early. Retrying is the first thing to try."),
        ["ChatReasoningOnly"] = ("模型只返回了思考过程，没有给出答复。可以重试。", "The model returned reasoning but no answer. You can retry."),
        // The ending a withheld tool used to produce silently: the response named a call, nothing ran it, and the
        // sentence the person got blamed the model's silence. The tool's name arrives in the detail appended after
        // this line, so the copy stays one sentence and names no tool of its own.
        ["ChatUndeclaredTool"] = ("模型要求调用本次请求没有声明的工具，于是一次也没有执行。可以重试，或在设置里打开对应能力。", "The model asked for a tool this request never declared, so nothing ran. Retry, or switch that capability on in settings."),
        ["ChatResponseTruncated"] = ("答复达到模型输出上限，当前内容可能不完整。点击继续，让模型从这里接着写。", "The response reached the model's output limit and may be incomplete. Continue to ask the model to pick up here."),
        ["ChatResponseFiltered"] = ("模型服务因内容过滤结束了答复。请调整或改写请求后再试。", "The provider stopped the response because of content filtering. Rephrase the request and try again."),
        ["ContinueTruncated"] = ("继续未完成的答复", "Continue the incomplete response"),
        ["ContinueTruncatedPrompt"] = ("请从刚才停止的位置继续完成答复，不要重复已经写出的内容。", "Continue the response from where it stopped. Do not repeat the content already written."),
        // A session that names a model its provider no longer offers. Saying "no models are available" would be
        // false — the picker is full — and would hide the one fact the person can act on: which name went missing,
        // and that the chip is where to put a new one. {0} = the model name the session carries.
        ["ChatSessionModelGoneFormat"] = ("这条会话指定的模型 {0} 现在不在这个提供商可用的模型里。请在模型胶囊里选一个再问。", "The model this session names, {0}, is not among the provider's usable models. Pick another in the model chip and ask again."),
        // Said when the model asked for the same call twice after being refused, and the turn was ended rather
        // than paid for a third time. Not a failure and not silence: everything it found is above this line.
        ["ChatToolSpinStopped"] = ("模型反复请求同一个调用，本轮到此为止——上面已有的结果都还在。换一种问法或直接给出指示即可继续。",
            "The model kept asking for the same call, so this turn ended here — everything it found is above. Ask differently, or give an instruction, to continue."),
        ["ChatCancelled"] = ("已停止。", "Stopped."),
        // Not an error and not silent: the earlier turns were replaced by a summary, so a reply that seems to
        // have forgotten something has a reason the user can see.
        ["ChatContextCompacted"] = ("已自动压缩较早的上下文。", "Compacted the earlier context automatically."),
        // The plan is still on the transcript and its document is not on disk — Hub keeps only the most recent
        // plans, or somebody deleted the file. Approving would send an instruction with no plan under it, so the
        // card refuses and says which of its two halves is missing.
        ["ChatPlanBodyGone"] = ("这个计划的正文已经不在 Hub 保存的文件里，批准它只会发出一句读不到内容的指令；请重新生成计划。", "This plan's text is no longer in the file Hub kept for it, so approving it would issue an instruction with nothing to follow. Ask for a new plan."),
        // Refusals a send can get back now that several sessions answer at once. The cap is deliberately not
        // spelled out as a number: the limit lives in code, and a literal here would go stale silently.
        ["ChatSessionBusy"] = ("这个会话正在生成回复，请先停止它或等回复结束。", "This session is already answering. Stop it or wait for the reply to finish."),
        ["ChatParallelLimit"] = ("同时运行的会话已达上限，请先停止一个。", "That is as many sessions as can run at once. Stop one before starting another."),
        ["ChatSessionMissing"] = ("这个会话已被删除。", "This session has been deleted."),
        // A peer session's message reads as the user's own words unless it is labelled, and the label is the only
        // place that says so — the stored text deliberately carries no prompt scaffolding.
        ["ChatPeerOriginFormat"] = ("来自会话「{0}」", "From session 「{0}」"),
        ["ChatPeerOriginGone"] = ("来自一个已删除的会话", "From a session that no longer exists"),
        ["ConversationQueuedTip"] = ("另一个会话给它发了话，正等待空闲的回答位。", "Another session sent it something to answer; it starts when an answer slot frees."),
        ["SpawnedSessionCompleted"] = ("子会话已完成，结论已返回", "Helper completed and returned its result"),
        ["SpawnedSessionFailed"] = ("子会话未能完成", "Helper did not complete"),
        ["SpawnedSessionStopped"] = ("子会话已停止", "Helper was stopped"),
        ["SpawnedSessionInterrupted"] = ("子会话因 Hub 关闭而中断", "Helper was interrupted when Hub closed"),
        // The badge itself is a glyph with no number — a session can owe exactly one decision at a time — so the
        // count lives here, where it can say "two" if a later build ever can park two.
        ["PendingApprovalsTip"] = ("有 {0} 个操作在等你批准。", "{0} action(s) in this session are waiting for you."),
        ["PendingApprovalSessionsFormat"] = ("{0} 个会话等待审批", "{0} session(s) awaiting approval"),
        // The decision was already made, or a newer message took the call away while the question was on screen.
        ["ChatApprovalGone"] = ("这个待批准的调用已经不在了，可能已被处理或被新消息取代。", "This pending approval is no longer there — it was already decided, or a newer message took its place."),
        // Inline approval card. The card states what will happen rather than describing a permission level: the
        // tool name and its arguments are the whole question, and the answer is one of the three buttons.
        ["ChatApprovalQuestionFormat"] = ("要执行 {0} 吗？", "Run {0}?"),
        ["ChatApprovalArguments"] = ("参数", "Arguments"),
        ["ChatApprovalChangePreview"] = ("改动预览", "Change preview"),
        ["ApprovalAllow"] = ("批准", "Allow"),
        ["ApprovalAllowAlways"] = ("总是允许", "Always allow"),
        ["ApprovalDeny"] = ("拒绝", "Deny"),
        ["ComputerUseApprovalTitle"] = ("允许 AI 使用此应用？", "Allow AI to use this app?"),
        ["ComputerUseAllowConversation"] = ("允许此会话", "Allow this session"),
        ["ComputerUseAlwaysAllow"] = ("始终允许", "Always allow"),
        ["ComputerUseGrantHint"] = ("仅授权上述应用的截图和输入操作；窗口标题变化无需重复授权。", "Grants screenshot and input actions for the application above; title changes do not require another grant."),
        ["TrustedComputerAppsTitle"] = ("始终允许的桌面应用", "Always allowed desktop apps"),
        ["TrustedComputerAppsHint"] = ("这些应用可在没有重复审批的情况下接受 Computer Use 操作。", "These apps can receive Computer Use actions without another approval."),
        ["ChatPlanApprovalQuestion"] = ("是否按这个计划开始执行？", "Start implementing this plan?"),
        ["ChatPlanApprove"] = ("批准并执行", "Approve and implement"),
        ["ChatPlanRevise"] = ("退回修改", "Request revision"),
        ["ChatPlanReject"] = ("拒绝计划", "Reject plan"),
        ["ChatPlanRevisionPrompt"] = ("请按以下反馈修改计划：", "Revise the plan with this feedback:"),
        // The four wordings of the transcript's plan card. Each is a whole phrase naming both the thing and its
        // state, because the card says it once; the plan's title rides beside it.
        ["ChatPlanPending"] = ("计划待你审阅", "Plan awaiting your review"),
        ["ChatPlanApproved"] = ("计划已批准", "Plan approved"),
        ["ChatPlanRevisionRequested"] = ("计划已要求修改", "Plan revision requested"),
        ["ChatPlanRejected"] = ("计划已拒绝", "Plan rejected"),
        ["ChatPlanCardUntitled"] = ("无标题计划", "Untitled plan"),
        ["ChatPlanTaskSummaryFormat"] = ("任务 {0} 项 · {1} 已完成 · {2} 进行中 · {3} 待处理", "Tasks {0} · {1} completed · {2} in progress · {3} pending"),
        ["ChatPlanNotificationTitle"] = ("计划等待确认", "Plan awaiting approval"),
        ["ChatPlanNotificationBody"] = ("会话「{0}」中的计划需要你的决定。", "A plan in 「{0}」 needs your decision."),
        ["ChatRunCompletedNotificationTitle"] = ("助手任务已完成", "Assistant task completed"),
        ["ChatRunIncompleteNotificationTitle"] = ("助手任务未完成", "Assistant task incomplete"),
        ["ChatRunFailedNotificationTitle"] = ("助手任务失败", "Assistant task failed"),
        ["ChatRunTimedOutNotificationTitle"] = ("助手任务超时", "Assistant task timed out"),
        ["ChatRunNotificationBody"] = ("会话「{0}」中的助手任务已结束。", "The assistant task in 「{0}」 has finished."),
        ["ChatToolApprovalNotificationTitle"] = ("操作等待批准", "Action awaiting approval"),
        ["ChatToolApprovalNotificationBody"] = ("会话「{0}」需要你批准一项操作。", "Session 「{0}」 needs you to approve an action."),
        ["AttentionDiagnosticTitle"] = ("Axmol Hub 通知测试", "Axmol Hub notification test"),
        ["AttentionDiagnosticBody"] = ("这是系统通知测试；任务栏标记将在 30 秒后恢复。", "This is a system notification test; the taskbar badge will reset after 30 seconds."),
        // Linux notifications carry this twice: as the notify-send action label, and as the anchor text of the
        // open-in-Hub link appended to the body. It is the only click target that names the conversation exactly.
        ["NotificationActionOpen"] = ("打开会话", "Open session"),
        // After the decision the card collapses to one of these lines — the record stays in the transcript, the
        // buttons do not.
        ["ChatApprovalResolvedApproved"] = ("已批准", "Allowed"),
        ["ChatApprovalResolvedDenied"] = ("已拒绝", "Refused"),
        ["ChatApprovalResolvedSuperseded"] = ("未执行（换了话题）", "Not run (the session moved on)"),
        // The write's own record and its one exit. A call that never had to ask still changed the file, so the
        // line names the file; the button only rides along while there is a copy to go back to — and it stays
        // visible instead of waiting for a hover, because an undo nobody can find is a mistake that cannot be fixed.
        ["ChatWriteRecordFormat"] = ("写入 {0}", "Edit {0}"),
        ["ChatUndoButton"] = ("撤销", "Undo"),
        ["ChatUndoTip"] = ("把 {0} 恢复到这次写入之前的内容", "Put {0} back to what it held before this write"),
        // The one line a long session opens with instead of its whole history: the transcript paints its newest
        // stretch and this stands for everything above it. It names the count, because "earlier messages" alone
        // gives a reader no way to tell whether the click buys forty rows or four thousand.
        ["ChatLoadEarlier"] = ("加载更早的 {0} 条消息", "Load {0} earlier messages"),

        // ── The decision surfaces that live in the composer (2026-10-08) ──
        // A pending call and a pending plan are both a question the run is parked on, and both are now asked in
        // the composer rather than in a row the person has to scroll back to. The title names the tool because
        // "run file_write" is not the question — what it writes is, and that is the body.
        ["ChatApprovalWantsFormat"] = ("{0} 想要执行一个操作", "{0} wants to run an action"),
        // A model response can ask for several calls at once and the person answers that group as one, so the
        // subject of the title is the group, not a tool: naming one of them would read as a card about one call
        // while the buttons below it decide for all of them.
        ["ChatApprovalBatchWantsFormat"] = ("助手想要执行 {0} 个操作", "The assistant wants to run {0} actions"),
        ["ChatApprovalWaitingLine"] = ("{0} 正在等你批准，决定在输入区", "{0} is waiting for your decision in the composer"),
        ["ChatApprovalMoreWaitingFormat"] = ("还有 {0} 项在等待", "{0} more waiting"),
        ["ChatApprovalExpand"] = ("展开完整内容", "Expand"),
        ["ChatApprovalCollapse"] = ("收起", "Collapse"),
        // "Always allow" renamed to its consequence, twice over: the bare word promised more than the grant did, and
        // the grant now reaches past this session, so the line has to say where it lands and how to take it back.
        ["ApprovalAllowAlwaysForTool"] = ("以后都不问 {0}", "Stop asking about {0}"),
        ["ApprovalAllowAlwaysTip"] = ("记进「设置 → 工具权限」的信任清单，随时可以撤销；对越出工作目录的操作无效",
            "Remembered in Settings → Tool permission, where it can be revoked. Has no effect on anything that "
            + "reaches past the workspace."),
        // The third answer on a git write card, and the narrower one: it grants a single verb rather than the whole
        // run_command tool, so the tip has to say both what it does not cover and where it can be taken back.
        ["ApprovalAllowAlwaysForVerbTip"] = ("只对这一个命令动词生效，记进「设置 → 工具权限」，随时可以撤销；同一个动词直接写在命令行里才算，包在 shell 里的调用和一次写多个动词的行仍要问",
            "Applies to this one command verb only, remembered in Settings → Tool permission, and revocable there. "
            + "Only counts where the line spells that verb itself: a write hidden inside a shell, or a line writing "
            + "more than one verb, still asks."),
        // ── A command that went quiet while it is still running ──
        // The question Hub asks instead of killing the process on its own. Three answers because the three real
        // outcomes are different acts: wait, walk away, or stop it and try again. The tips are where the promises are
        // kept honest — "stop waiting" gives up the watch <b>and</b> the gate it was holding, and "stop it" reaches
        // the command tree Hub started, not the background build services that detach from it.
        ["StallQuestionFormat"] = ("{0} 已经 {1} 没有输出，进程还在跑。要怎么处理？",
            "{0} has produced no output for {1} and is still running. What should happen to it?"),
        ["StallPidFormat"] = ("进程 {0}", "process {0}"),
        ["StallOutputLabel"] = ("它安静之前打印的", "What it printed before going quiet"),
        ["StallKeepWaiting"] = ("继续等待", "Keep waiting"),
        ["StallLeaveRunning"] = ("取消等待", "Stop waiting"),
        ["StallKillRetry"] = ("停掉再跑一遍", "Stop it and run again"),
        ["StallKeepWaitingTip"] = ("不结束它。Hub 一直等到它自己出结果，不设时限",
            "Nothing is ended. Hub waits for the result as long as it takes, with no deadline."),
        ["StallLeaveRunningTip"] = ("不结束它，也不再等。Hub 从此不管这条进程，也不再替它占住改动的位置——它可能仍在写工作目录",
            "Nothing is ended and Hub stops waiting. The process is no longer watched and no longer holds the "
            + "change slot — it may still be writing to the workspace."),
        ["StallKillRetryTip"] = ("结束 Hub 起的那条命令树，然后重跑一次。后台编译服务（MSBuild 节点、VBCSCompiler）"
            + "可能不在这棵树上，停掉之后仍可能在跑",
            "Ends the command tree Hub started, then runs it once more. Background build services (MSBuild nodes, "
            + "VBCSCompiler) can sit outside that tree and may still be going afterwards."),
        ["ChatPlanReviewTitle"] = ("审阅计划", "Review plan"),
        ["ChatPlanReviewOpen"] = ("查看计划", "View plan"),
        ["ChatPlanOptionApprove"] = ("批准并开始", "Approve and start"),
        ["ChatPlanReviseInChat"] = ("在聊天中修改", "Revise in chat"),
        ["ChatPlanCancel"] = ("取消计划", "Cancel plan"),
        ["ChatPlanFeedbackPlaceholder"] = ("描述要调整的地方，也可以添加图片；发送后只更新计划，不开始实施。", "Describe changes or add images. Sending updates the plan without starting implementation."),
        ["ChatPlanChanged"] = ("待审计划已变化，本条意见尚未发送。请查看当前计划后重新发送。", "The plan under review has changed. Your feedback was not sent. Review the current plan before sending again."),
        // ── A question the model asked ──
        // The third thing the composer can be parked on. Its own title rather than 「{tool} 想要执行一个操作」
        // because nothing wants to run: the row above the box is a request for information, and reading it as a
        // request for permission is how a person approves something they were only meant to answer.
        ["ChatQuestionTitleFormat"] = ("助手在问：{0}", "The assistant is asking: {0}"),
        // The free-text line is Hub's, never the model's — its placeholder is the only place that says so, and it
        // has to, because a fifth option the model invented and a place to disagree with all four look the same.
        ["ChatQuestionFreeLabel"] = ("都不对，我自己写…", "None of these — I'll say in my own words…"),
        ["ChatQuestionSkip"] = ("跳过", "Skip"),
        ["ChatQuestionWaitingLine"] = ("{0} 在等你回答，答案在输入区", "{0} is waiting for your answer in the composer"),
        ["ChatQuestionResolved"] = ("已回答", "Answered"),
        ["ChatQuestionResolvedSuperseded"] = ("没回答（换了话题）", "Unanswered (the session moved on)"),
        // The card keeps 「继续」 dead until something is armed, so this is a race between a click and a repaint
        // rather than a thing a person does on purpose — which is why it is a notice and not a disabled button.
        ["ChatQuestionUnanswered"] = ("没有选中任何一项，也没有写答案", "Nothing was chosen and nothing was written"),
        ["ChatQuestionNotificationTitle"] = ("助手在等你回答", "The assistant has a question"),
        ["ChatQuestionNotificationBody"] = ("会话「{0}」中的助手需要你做一个选择。",
            "The assistant in 「{0}」 needs you to make a choice."),
        // ── Steering needs a second tap ──
        // A steer cancels the segment being generated and is the routing table's strongest signal, so the first
        // Enter only shows what would be sent. The consequence lives in the tooltip rather than as a second
        // line of text: the composer already says everything else.
        ["ChatSteerConfirmTip"] = ("这句会等当前这一步做完就接上，正在写的回复不会被掐断", "Your words wait for the step in flight and are answered next; the reply being written is not cut short"),
        ["ChatSteerEdit"] = ("改一改", "Edit it"),
        ["ChatSteerDiscard"] = ("丢弃", "Discard"),
        ["ChatSteerDraftMoved"] = ("还有一条没插出去", "One interjection was never sent"),
        ["ChatSteerPicturesSuffix"] = ("+ {0} 张图", "+ {0} picture(s)"),
        ["ChatSteerYieldNotice"] = ("先处理上面的请求", "Resolve the request above first"),
        // ── The window-level picture viewer ──
        ["PictureViewerPositionFormat"] = ("第 {0} / {1} 张", "{0} of {1}"),
        ["PictureViewerPrevious"] = ("上一张", "Previous picture"),
        ["PictureViewerNext"] = ("下一张", "Next picture"),
        ["PictureViewerCopy"] = ("复制图片", "Copy picture"),
        ["PictureViewerClose"] = ("关闭", "Close"),
        ["PictureViewerMissing"] = ("图片已不可读", "Picture no longer readable"),
        // ── The right-hand inspector ──
        ["InspectorTabPlan"] = ("计划", "Plan"),
        ["InspectorTabChanges"] = ("改动", "Changes"),
        ["InspectorOpenFile"] = ("打开当前工作区文件", "Open current workspace file"),
        ["InspectorCurrentFile"] = ("当前工作区文件", "Current workspace file"),
        ["InspectorOpenFileUnavailable"] = ("无法打开当前文件：文件可能已删除、不可读取，或不是受支持的文本文件。", "Cannot open the current file: it may be deleted, unreadable, or not supported text."),
        ["InspectorEditDiff"] = ("编辑差异", "Edit diff"),
        ["InspectorCancelEdit"] = ("取消", "Cancel"),
        ["InspectorSaveEdit"] = ("保存", "Save"),
        ["InspectorEditSaveFailed"] = ("保存失败，未保存的编辑已保留。", "Save failed; unsaved edits have been retained."),
        ["InspectorEditUnsupported"] = ("此文件不可编辑；可能已删除、为二进制、超出大小限制或没有写入权限。", "This file cannot be edited: it may be deleted, binary, too large, or outside writable scope."),
        ["InspectorChangesEmpty"] = ("这一轮没有改动文件", "This turn did not change any file"),
        ["InspectorPlanEmpty"] = ("还没有生成计划", "No plan yet"),
        // The plan exists and its document does not. "No plan yet" would be the wrong sentence to show somebody
        // who has one on the transcript, so the pane names the file it looked for and how long the plan was.
        ["InspectorPlanGoneFormat"] = ("这份计划（{0} 字）的正文已不在 ai/plans/{1} 里：Hub 只保留最近的计划文件，或者它被手工删掉了。", "This plan's text ({0} characters) is no longer in ai/plans/{1}: Hub keeps only the most recent plans, or the file was removed by hand."),
        // Reasons a historical diff can be unavailable, including records written by older Hub versions.
        ["InspectorDiffCreatedFile"] = ("新建的文件没有改动前", "A new file has no before"),
        ["InspectorDiffCopyGone"] = ("旧版会话的改动前副本已不可用", "This legacy session's pre-image is unavailable"),
        ["InspectorDiffPendingSnapshot"] = ("这次改动记录仍待核实，暂时无法显示", "This change record is still pending verification"),
        ["InspectorDiffMissingSnapshot"] = ("这次改动的历史快照已缺失", "The snapshot for this change is missing"),
        ["DiffInlineMode"] = ("统一视图", "Unified view"),
        ["DiffSplitMode"] = ("并排视图", "Split view"),
        ["DiffViewSection"] = ("视图", "View"),
        ["InspectorDiffCopySpent"] = ("那次写入已被撤销，副本用完", "The copy was spent by a revert"),
        ["InspectorDiffUnreadable"] = ("现在读不到这个文件，或它不是文本", "The file is gone or is not text"),
        ["InspectorDiffRefusedLine"] = ("没有改动", "No change"),
        ["InspectorAddedFormat"] = ("+{0}", "+{0}"),
        ["InspectorRemovedFormat"] = ("−{0}", "−{0}"),
        ["InspectorCountsUnavailable"] = ("无法读取此文件的行数变化", "Line counts are unavailable for this file"),
        ["InspectorExpandFile"] = ("展开全部行", "Show all lines"),
        ["InspectorCollapseFile"] = ("只显示改动", "Show changes only"),
        // ── The inspector's third tab: the repository itself ──
        // The pane already answers "what did this session do". This answers a different question — "what is
        // uncommitted here" — and the two must not be merged, because a person's week of own work would otherwise
        // read as the assistant's. Every state below is a sentence the tab can end on: each names why it stopped
        // rather than drawing an empty list, which is the same rule the four diff states above follow.
        // The two scopes moved off the header and into the review gear's menu, so the words lost their caret:
        // a menu row names an answer, it does not advertise that pressing it opens something else.
        ["InspectorSettingsTip"] = ("差异视图设置", "Diff view settings"),
        // The top bar's drawer button. Icon-only like the sidebar's, so the tooltip is the only place it has a
        // name — and it names both directions, because one glyph that opens and closes cannot say which it is
        // about to do without a second control that would repeat the column itself.
        ["InspectorToggleTip"] = ("显示或隐藏审阅面板", "Show or hide the review panel"),
        // The invitation on a run's +N −M chip. The count alone says how much, not where to look; this is the
        // same one word of affordance the plan card's 「查看计划」 gives, and the only place that says the number
        // can be pressed.
        ["InspectorOpenChanges"] = ("看这些改动", "See these changes"),
        // The band above the composer. The count is the sentence and the two diffs are the numbers, so the one
        // thing the person is being asked to trust is the one thing said once.
        ["InspectorRepoCaveat"] = ("仓库相对 HEAD 的全部改动，不只是本会话做的",
            "Every change the repository holds against HEAD, not only this session's"),
        ["InspectorRepoAheadBehindFormat"] = ("领先 {0} · 落后 {1}", "{0} ahead · {1} behind"),
        ["InspectorRepoReading"] = ("正在读仓库…", "Reading the repository…"),
        ["InspectorRepoEmpty"] = ("工作树与 HEAD 一致", "The working tree matches HEAD"),
        ["InspectorRepoTruncatedFormat"] = ("改动太多，这里只列前 {0} 个",
            "Too many changes; only the first {0} are listed"),
        ["InspectorRepoRefresh"] = ("重新读一次仓库", "Read the repository again"),
        ["InspectorRepoSessionAlsoTouched"] = ("本会话也改过这个文件", "this session touched this file too"),
        ["InspectorRepoNoDiff"] = ("读不到这个文件的改动内容", "No diff could be read for this file"),
        ["InspectorRepoNoWorkspace"] = ("尚未选择工作目录", "No workspace selected yet"),
        ["InspectorRepoNotGit"] = ("这个目录不是 git 仓库", "This directory is not a git repository"),
        ["InspectorRepoGitMissing"] = ("这台机器上没有 git，读不了仓库状态",
            "git is not on this machine, so the repository cannot be read"),
        ["InspectorRepoUnsafe"] = ("git 不信任这个目录的归属，要你亲自认下来它才让读",
            "git does not trust who owns this repository; it needs your own decision before Hub may read it"),
        ["InspectorRepoFailed"] = ("git 没能回答", "git did not answer"),
        // ── The Changes scope picker ──
        // The scope names the question the list below answers. They are short because the picker sits above the
        // list it governs and a sentence there would be read as the list's title rather than its question.
        ["InspectorScopeLastTurn"] = ("本轮修改", "Last turn"),
        ["InspectorScopeUncommitted"] = ("未提交", "Uncommitted"),
        ["InspectorScopeUnstaged"] = ("未暂存", "Unstaged"),
        ["InspectorScopeStaged"] = ("已暂存", "Staged"),
        ["InspectorScopeCommitted"] = ("已提交", "Committed"),
        ["InspectorScopeBranch"] = ("分支", "Branch"),
        ["InspectorScopeTip"] = ("选一处改动来看", "Choose which changes to read"),
        // The pair the Branch scope compares, named exactly as git names both sides: "master → origin/master" is a
        // sentence a reader can retype in a terminal, and "upstream" alone is not.
        ["InspectorBranchPairFormat"] = ("{0} → {1}", "{0} → {1}"),
        ["InspectorBranchHead"] = ("HEAD", "HEAD"),
        ["InspectorScopeUncommittedEmpty"] = ("没有未提交的改动", "Nothing is uncommitted"),
        ["InspectorScopeUnstagedEmpty"] = ("没有未暂存的改动", "Nothing is unstaged"),
        ["InspectorScopeStagedEmpty"] = ("没有已暂存的改动", "Nothing is staged"),
        // A command that edited files leaves no snapshot naming the turn, so those files are not counted under the
        // assistant's name — this is the sentence that points at the list they are in instead.
        ["InspectorTurnCommands"] = ("本轮还运行过命令，它改的文件在下方的 Git 范围里",
            "A command ran in this turn too; the files it changed are under the Git scopes below"),
        // The Committed scope reads the branch's own unpushed commits: after a push they belong to the remote, and
        // with no upstream there is no such set — the four sentences below are the four honest answers.
        ["InspectorCommitsNone"] = ("没有未推送的提交", "No unpushed commits"),
        ["InspectorCommitsNoUpstream"] = ("这个分支没有 upstream，无从判断哪些提交还没推送",
            "This branch has no upstream, so there is no way to tell which commits are unpushed"),
        ["InspectorCommitsPick"] = ("在菜单里选一个提交", "Pick a commit from the menu"),
        ["InspectorCommitsFailed"] = ("读不到提交列表", "The commit list could not be read"),
        ["InspectorCommitHeadFormat"] = ("{0} · {1}", "{0} · {1}"),
        ["InspectorCommitMergeFormat"] = ("合并提交 {0} · {1}（按第一父提交比较）",
            "Merge commit {0} · {1} (compared with its first parent)"),
        ["InspectorCommitEmpty"] = ("这个提交没有改动任何文件", "This commit changed no files"),
        ["InspectorBranchesReading"] = ("正在读分支…", "Reading branches…"),
        ["InspectorBranchesPick"] = ("选一个分支作基准", "Pick a branch to compare against"),
        ["InspectorBranchesNone"] = ("没有别的分支可以与它比较",
            "There is no other branch to compare against"),
        // ── The card a finished turn leaves in the transcript ──
        // It answers "what did that just do to my tree", so the count is the headline and the two numbers are the
        // detail. The fold names how many are still hidden: "show more" alone makes a reader open it to find out
        // whether it was worth opening.
        ["ChatReviewEditedFormat"] = ("编辑了 {0} 个文件", "Edited {0} files"),
        ["ChatReviewEditedOne"] = ("编辑了 1 个文件", "Edited 1 file"),
        ["ChatTurnFilesChangedOne"] = ("1 个文件已修改", "1 file changed"),
        ["ChatTurnFilesChangedFormat"] = ("{0} 个文件已修改", "{0} files changed"),
        ["ChangeCountsBudgetExceeded"] = ("计数超限", "Count budget exceeded"),
        ["ChangeCountsUnavailable"] = ("计数不可用", "Counts unavailable"),
        ["ChangeCountsBudgetDetail"] = ("比较超出资源预算，无法精确统计增删行数；替代对齐中的红绿行不代表实际改动。",
            "The comparison exceeded its resource budget. Exact line counts are unavailable; fallback red/green rows are not measured changes."),
        ["ChatReviewUndo"] = ("撤销", "Undo"),
        ["ChatReviewReview"] = ("审阅", "Review"),
        ["ChatReviewMoreFormat"] = ("再显示 {0} 个文件", "Show {0} more files"),
        ["ChatReviewFewer"] = ("收起文件列表", "Show fewer files"),
        // Both sentences are in the user's voice: the undo is their own act, and the assistant has to read that the
        // tree moved back before it anchors another edit on what it believes is still there.
        ["ChatReviewUndoneFormat"] = ("已把这一轮改动的 {0} 个文件还原", "Restored the {0} files this turn changed"),
        ["ChatReviewUndonePartialFormat"] = ("还原了 {0} 个文件；{1} 个在那之后又被改过，没有动",
            "Restored {0} files; {1} changed since and were left alone"),
        // The two empty-scope jumps. A list that is empty because the *other* scope is not leaves the reader at a
        // dead end, so the empty sentence offers the way across — and only ever with a number it has actually
        // verified: the changes tab offers this when a read has said the tree is dirty, the repository tab when
        // the transcript says this session wrote files.
        // The column's two states. The restore wording names Esc because the key is the only other way out of a
        // surface that has just covered the window, and a keyboard exit nobody announces is not a keyboard exit.
        ["InspectorExpand"] = ("撑开面板到整个窗口", "Expand the panel to the whole window"),
        ["InspectorRestore"] = ("还原成右边那一列（Esc）", "Back to the right-hand column (Esc)"),
        // Said once, at the end of the answer. The live bubble counts this number while the reply arrives and is
        // thrown away with it, so without this line the total exists only at the moment nobody is reading the
        // result — and the one surface that keeps anything afterwards is a hover-revealed action bar.
        ["ChatRunElapsedFormat"] = ("本次共 {0}", "Took {0}"),
        ["ChatRunEffortFormat"] = ("推理 {0}", "Reasoning {0}"),
        ["ChatReasoningDefault"] = ("默认", "Default"),
        // ── A run's activity, folded into one collapsible group ──
        // The head counts once there is more than one action to summarise. Naming the first of a long run reads
        // as a claim about what the run was for, and it is not — it is whichever call happened to come first. A
        // single call is still named, because then the name is the whole answer and a count would hide it behind
        // a click. See GroupSummary for where that line is drawn.
        ["ActivityGroupToolCountFormat"] = ("执行工具 {0} 次", "{0} tool call(s)"),
        ["ActivityGroupRunningFormat"] = ("正在执行中 · {0}", "Running · {0}"),
        ["ActivityGroupThoughtFormat"] = ("已思考 {0}", "Thought for {0}"),
        ["ActivityRowThought"] = ("已思考", "Thought"),
        ["ActivityRowRead"] = ("读取 {0}", "View {0}"),
        ["ActivityRowSearch"] = ("搜索 {0}", "Search {0}"),
        ["ActivityRowList"] = ("列出 {0}", "List {0}"),
        ["ActivityRowFind"] = ("查找 {0}", "Find {0}"),
        ["ActivityRowEditFile"] = ("编辑文件 {0}", "Edit {0}"),
        ["ActivityRowEditFilesFormat"] = ("编辑 {0} 个文件", "Edit {0} files"),
        ["ActivityRowDelete"] = ("删除 {0}", "Delete {0}"),
        ["ActivityRowMove"] = ("移动 {0} → {1}", "Move {0} → {1}"),
        ["ActivityRowRunCommand"] = ("执行命令", "Run command"),
        ["ActivityRowFetch"] = ("抓取 {0}", "Fetch {0}"),
        ["ActivityRowGeneric"] = ("调用 {0}", "Call {0}"),
        // A search the endpoint ran for itself, drawn as one live status line. It has no card and no result turn,
        // so this is the only place the person learns the answer came from outside — and "searching" is a state of
        // its own here rather than a variation of "search xxx", because the queries often are not known yet: the
        // start event arrives with nothing in it.
        ["ActivityRowWebSearching"] = ("正在联网搜索…", "Searching the web…"),
        ["ActivityRowWebSearched"] = ("联网搜索：{0}", "Searched the web for {0}"),
        // Under the reply, the addresses it read. Named "Sources" rather than "References" because a model that searched
        // and cited nothing has to read as unread, and a softer word would let that pass for an answer.
        ["MessageSources"] = ("来源", "Sources"),
        ["MessageSearchedFor"] = ("它自己查了：{0}", "it searched for: {0}"),
        // Under a folded action row, one expandable panel lays the exchange out in full: the arguments the call
        // was made with and the payload that came back. The collapsed row only names the action; this is where the
        // concrete operation becomes readable rather than one hover away.
        ["ActivityDetailInput"] = ("输入", "Input"),
        ["ActivityDetailOutput"] = ("输出", "Output"),
        ["ActivityDetailEmpty"] = ("无内容", "Nothing"),
        ["ActivityDetailTruncated"] = ("…（已截断）", "… (truncated)"),
        // A patch that deletes a file carries no lines for it — the envelope only names the path — so the fold says
        // in words what a diff would otherwise have to show as the whole file going red.
        ["PatchDeletedFile"] = ("整个文件被删除", "the whole file was deleted"),
        // ── the command that is running right now ──
        // The call is recorded before it runs, so this card exists while the command prints rather than after it
        // comes back — which is the whole point of it: a build that says nothing for five minutes used to say
        // nothing on the page either. "即将执行" covers the one breath between the call being filed and the tool
        // owning it; it is not a permission state, and the card that asks for permission is a different surface.
        ["ActivityRowCommandRunning"] = ("正在执行", "running"),
        ["ActivityRowCommandQueued"] = ("即将执行", "starting"),
        ["ActivityDetailWorkspace"] = ("工作目录", "Working directory"),
        ["ActivityDetailCommandOutput"] = ("输出（执行中）", "Output (running)"),
        ["ActivityDetailCommandSilent"] = ("还没有输出", "Nothing printed yet"),
        // Said rather than swallowed: the card keeps a bounded tail, and a reader who is shown fewer lines than the
        // command printed has to be told that is what happened.
        ["ActivityDetailCommandDroppedFormat"] = ("…更早的 {0} 行没有保留", "…{0} earlier lines not kept"),
        // The verdict of a command, in the number a person would ask for. The row's mark already says whether it
        // failed; this says what it exited with, which is the difference between "a check failed" and "nothing was
        // run at all".
        ["ActivityRowExitCodeFormat"] = ("退出码 {0}", "exit {0}"),
        // Recorded as the user's own turn, in their voice: it is the thing they just did by clicking, and the
        // assistant has to read it before it touches the same file again.
        ["ChatUndoNotifiedFormat"] = ("我已撤销那次写入：{0} 已恢复到写入之前的内容。",
            "I reverted that write: {0} is back to what it held before it."),
        // One sentence per way a revert can refuse. Each says what became of the file, because the person reading
        // it is deciding whether to fix something first.
        ["ChatUndoCopyMissing"] = ("已经没有那次写入前的副本（可能被清理了）。",
            "There is no copy left of what was there before that write — it may have been cleaned up."),
        ["ChatUndoChangedSince"] = ("这个文件在那次写入之后又被改过，直接恢复会覆盖后来的改动。",
            "This file has changed since that write, so restoring it would overwrite the later edits."),
        ["ChatUndoTargetMissing"] = ("文件已经不在了，Hub 不会替你重新创建。",
            "The file is gone, and Hub will not recreate it for you."),
        ["ChatUndoNotText"] = ("文件已经不是 UTF-8 文本，无法按文本恢复。",
            "The file is no longer UTF-8 text, so it cannot be restored as text."),
        ["ChatUndoRefusedPath"] = ("那次写入记录的路径已经不在本会话的工作区里，或落在受保护目录，拒绝恢复。",
            "That write's recorded path is no longer inside this session's workspace, or sits in a protected directory."),
        ["ChatUndoWriteFailed"] = ("副本是完好的，但写回失败了。", "The copy is intact, but writing it back failed."),
        // The compact composer menu has one question above the choices. Its risk detail remains available as a
        // tooltip on each choice rather than turning a three-mode picker into a wall of text.
        ["ChatToolPermissionMenuTitle"] = ("如何审批助手的操作？", "How should assistant actions be approved?"),
        // The permission tiers, spelled out in tooltips and settings: each hint says what that tier lets through
        // unasked, which is the only thing the three names cannot carry themselves.
        ["ToolApprovalAsk"] = ("询问审批", "Ask for approval"),
        ["ToolApprovalAskHint"] = ("除只读查询外，每个操作都先问一次，包括跑命令",
            "Everything except a read asks first, commands included"),
        ["ToolApprovalAuto"] = ("自动审批", "Auto-approve"),
        // Both hints name the deletion, not the build: the user ruled that reading an error, changing code and
        // building again is ordinary work, while throwing away what was already built is the one thing no mode speaks
        // for. A hint that said otherwise would be the layer that lies about the gate. The auto hint names the two
        // git verbs the same way and for the same reason: a person who read only "commands run on their own" would
        // be surprised by a card for a commit, and one who read only "git asks" would be surprised by a push that
        // went through, so both halves are spelled out rather than left to the button below.
        ["ToolApprovalAutoHint"] = ("工作目录里的写入和命令直接执行，构建也算；git 命令里 add 和 commit 这两条留在仓库里的写也直接执行，其余动词照旧要问（push、reset、clean 这些会离开这台机器或丢掉没提交的东西）；删除已构建的东西、抓屏、派生子会话、换工作目录仍要问",
            "Writes and commands inside the workspace run on their own, builds included; among git commands the two "
            + "that stay in the repository, add and commit, run on their own too, while the rest still ask — push, "
            + "reset and clean leave this machine or throw away work nothing can hand back; clearing what was built, "
            + "the screen, a child session and a new working directory still ask"),
        ["ToolApprovalFull"] = ("完全访问", "Full access"),
        ["ToolApprovalFullHint"] = ("除了删除已构建的东西，什么都不再问，包括抓屏和这个版本没登记过的工具",
            "Nothing asks any more except clearing what was built, including the screen and any tool this build "
            + "does not know"),
        ["ToolPermission"] = ("工具权限", "Tool permission"),
        // The picker's own name. It used to live only inside the hint sentence below, which made the first line of
        // the pane a definition of a concept instead of the name of the control underneath it.
        ["DefaultApprovalModeTitle"] = ("默认审批档位", "Default approval mode"),
        // What is left once the title carries the noun: only the override caveat, which cannot be read off the
        // control. Shown as the title's tooltip.
        ["ToolPermissionHint"] = ("单个会话可以在聊天里改这一档，不影响这里。", "A single session can override this in the chat without changing it here."),
        ["CommitSummaryPrefixesTitle"] = ("提交摘要前缀", "Commit summary prefixes"),
        ["CommitSummaryPrefixesHint"] = ("用逗号分隔。默认：feat:, fix:, docs:, misc:。点击提交或提交并推送后，Hub 会暂存仓库中的非忽略更改再提交；点击推送则只推送当前分支。无法本地安全处理时会启动正常 Agent 操作，并遵循现有工具审批权限，产生额外额度消耗。", "Comma-separated. Defaults: feat:, fix:, docs:, misc:. Commit and commit-and-push stage non-ignored repository changes before committing; push sends only the current branch. If local handling is unsafe, a normal Agent run takes over under the existing tool approval policy and incurs additional usage."),
        ["CommitSummaryAction"] = ("提交", "Commit"),
        ["GitActionExpired"] = ("该操作已过期或会话正忙，请使用当前消息的操作。", "This action is stale or the session is busy; use the current message's actions."),
        ["GitPushAuthorizationTip"] = ("点击即授权推送当前分支到已配置的上游。", "Click to authorize pushing the current branch to its configured upstream."),
        ["PushSummaryNothingToPush"] = ("当前分支没有待推送的提交。", "The current branch has no commits to push."),
        ["PushSummaryAction"] = ("推送", "Push"),
        ["CommitPushSummaryAction"] = ("提交并推送", "Commit and push"),
        ["PushSummarySucceeded"] = ("已推送当前分支。", "Pushed the current branch."),
        ["CommitPushSummarySucceeded"] = ("已提交仓库更改并推送当前分支。", "Committed repository changes and pushed the current branch."),
        ["PushSummaryFailed"] = ("推送失败；请检查远端与分支状态。", "Push failed; check the remote and branch state."),
        ["PushSummaryUpstreamUnavailable"] = ("当前分支没有已配置的上游分支，未推送。", "The current branch has no configured upstream; nothing was pushed."),
        ["CommitPushPartialFailure"] = ("本地提交已创建，但推送失败；请检查远端状态。", "The local commit succeeded, but push failed; check the remote state."),
        ["CommitSummaryWorkspaceChanged"] = ("会话工作目录已变化，未提交。", "The session workspace changed; nothing was committed."),
        ["CommitSummaryInvalid"] = ("提交摘要格式无效，未提交。", "The commit summary is invalid; nothing was committed."),
        ["GitCommitTitleInvalid"] = ("请使用 @hub commit \"fix: 提交标题\"；标题须符合已配置前缀、为单行且不超过 120 字符。", "Use @hub commit \"fix: commit title\"; the title must use a configured prefix, be one line, and contain at most 120 characters."),
        ["CommitSummaryGitMissing"] = ("未找到 Git，未提交。", "Git was not found; nothing was committed."),
        ["CommitSummaryBusy"] = ("仓库正被另一项 Hub 操作占用，未提交。", "The repository is busy with another Hub operation; nothing was committed."),
        ["CommitSummaryRepoUnavailable"] = ("无法重新验证 Git 仓库状态，未提交。", "Could not revalidate repository state; nothing was committed."),
        ["CommitSummaryBranchUnavailable"] = ("当前没有可提交的分支，未提交。", "There is no current branch to commit on; nothing was committed."),
        ["CommitSummaryConflict"] = ("暂存区有冲突，未提交。", "The index contains conflicts; nothing was committed."),
        ["CommitSummaryNothingStaged"] = ("没有可提交的仓库更改。", "There are no repository changes to commit."),
        ["CommitSummaryStageFailed"] = ("无法暂存仓库更改；未创建提交。", "Could not stage repository changes; no commit was created."),
        ["CommitSummaryFailed"] = ("Git 提交失败；请检查仓库状态。", "Git commit failed; check the repository state."),
        ["CommitSummarySucceeded"] = ("已暂存并提交仓库更改。", "Staged and committed repository changes."),
        // The app-wide trust ledger: what a card's "always allow" wrote, shown where it can be taken back. One
        // header, one empty line and one caveat for all three lists, because they answer one question — what will
        // not ask again — and three headers with three paragraphs over three mostly-empty lists read as three
        // broken settings. The caveat names what no grant reaches: work past the workspace, a write hidden inside a
        // shell, and a line that spells more than one verb.
        ["TrustGrantsTitle"] = ("以后都不问", "What it stops asking about"),
        ["TrustGrantsHint"] = ("在审批卡上点过「以后都不问」的条目，本机每个会话都不再问；越出工作目录的操作不受影响，包在 shell 里的调用和一次写多个动词的行仍要问。",
            "What was clicked on an approval card stops asking in every session on this machine. Nothing that "
            + "reaches past the workspace is covered, nor is a write hidden inside a shell or a line spelling more "
            + "than one verb."),
        ["TrustGrantsNone"] = ("还没有任何授权。在审批卡上点「以后都不问」会记在这里。",
            "Nothing is trusted yet. Clicking \"Stop asking\" on an approval card records it here."),
        ["TrustedToolsTitle"] = ("以后都不问的工具", "Tools it stops asking about"),
        ["RevokeTrustedTool"] = ("撤销", "Revoke"),
        ["RevokeAllTrustedTools"] = ("全部撤销", "Revoke all"),
        // The other half of the same ledger: the git write verbs a card's verb button wrote. A separate list rather
        // than one merged column, because a grant is either a whole tool or a single verb, and the revoke has to say
        // which one it takes back.
        ["TrustedCommandsTitle"] = ("以后都不问的命令动词", "Command verbs it stops asking about"),
        ["RunningSessionsFormat"] = ("{0} 个会话运行中", "{0} sessions running"),
        ["ChatPreparing"] = ("思考中...", "Thinking..."),
        ["ChatGenerating"] = ("正在生成...", "Generating..."),
        ["You"] = ("你", "You"),
        // Message-level actions (the action bar that appears when a bubble is hovered).
        ["CopyMessage"] = ("复制", "Copy"),
        ["ReplyToMessage"] = ("回复此消息", "Reply to this message"),
        ["ReplyUser"] = ("用户", "User"),
        ["ReplyAssistant"] = ("助手", "Assistant"),
        ["CancelReply"] = ("取消引用回复", "Cancel reply"),
        ["CopyMarkdown"] = ("复制Markdown", "Copy Markdown"),
        // The same verb where a *selection* is the thing being acted on: the menu a right-click puts on a
        // selection inside a message. 引用 hands that selection to the composer as a Markdown quote, which is the
        // one thing a reader wants to do with a paragraph worth answering.
        ["QuoteSelection"] = ("引用", "Quote"),
        // The composer's own clipboard verbs. Its menu is the only place these exist: Avalonia's TextBox ships no
        // context menu at all, so without them the input box has no right-click behaviour to speak of.
        ["CutText"] = ("剪切", "Cut"),
        ["PasteText"] = ("粘贴", "Paste"),
        // The tooltip is now the only place that warns editing drops the rest of the conversation: the
        // confirm dialog it used to precede went away when editing moved inline.
        ["EditMessage"] = ("编辑并重发（丢弃其后对话）", "Edit and resend (drops everything after)"),
        // The two exits of the inline editor, shown in the row's own action slot so they need not be guessed.
        ["ConfirmEdit"] = ("保存并重发", "Save and resend"),
        ["RegenerateMessage"] = ("重新生成", "Regenerate"),
        // The same ↻ on a question that never got an answer: the failure notice scrolls away and is never
        // written to the transcript, so this is the one thing that keeps a failed reply recoverable.
        ["RetryMessage"] = ("重试这条提问", "Retry this question"),
        ["BranchFromHere"] = ("从此处创建分支任务", "Branch a new task from here"),
        // Shown above a forked transcript; the line itself is the link back to the source session.
        ["ForkedFrom"] = ("分支自 {0}", "Forked from {0}"),
        ["MessageTimeJustNow"] = ("刚刚", "now"),
        ["MessageTimeMinutesAgo"] = ("{0} 分钟前", "{0}m ago"),
        ["MessageTimeHoursAgo"] = ("{0} 小时前", "{0}h ago"),
        ["MessageTimeDaysAgo"] = ("{0} 天前", "{0}d ago"),
        ["ScrollToBottom"] = ("回到底部", "Scroll to bottom"),
        // Session management (the session list in the left column).
        ["RenameConversation"] = ("重命名", "Rename"),
        ["RenameConversationTitle"] = ("重命名对话", "Rename session"),
        // The id is what the session's own file, a log line and an axmolhub://sessions/… link all key on, and it
        // is otherwise only readable off the disk. The copy says so in words because a clipboard write is invisible.
        ["CopyConversationId"] = ("复制会话 ID", "Copy session ID"),
        ["ConversationIdCopied"] = ("会话 ID 已复制到剪贴板。", "The session ID is on the clipboard."),
        ["CopyConversationDeepLink"] = ("复制会话深度链接", "Copy session deep link"),
        ["ConversationDeepLinkCopied"] = ("会话深度链接已复制到剪贴板。", "The session deep link is on the clipboard."),
        ["CopyConversationFilePath"] = ("复制会话文件路径", "Copy session file path"),
        ["ConversationFilePathCopied"] = ("会话文件路径已复制到剪贴板。", "The session file path is on the clipboard."),
        ["PinConversation"] = ("置顶", "Pin"),
        ["UnpinConversation"] = ("取消置顶", "Unpin"),
        // Session grouping: workspaces first, conversations with no working directory all go to the trailing "Chats" group, archived ones sink to the very bottom.
        // Time buckets gave way to directory grouping — conversations of one project scattered over "Today" and "Earlier" no longer read as one project.
        // "Chats" stays "Chats": it is the one group whose rows have no directory to work in, so the word "session" (a run inside a
        // workspace) would describe the opposite of what the group holds. A sweep renaming conversation to session must skip this key
        // and the ＋ tip below it, and the same holds for the Chinese pair, which already says 对话.
        ["GroupWorkspaces"] = ("工作区", "Workspace"),
        ["GroupChats"] = ("对话", "Chats"),
        ["GroupArchived"] = ("已归档", "Archived"),
        ["ToggleGroup"] = ("展开或折叠这一组", "Expand or collapse this group"),
        // The "+" at the right of a group header: the workspace root node picks a directory first, the other groups create a session or a chat per what that group means.
        ["GroupNewSessionTip"] = ("新建会话", "New session"),
        ["GroupNewChatTip"] = ("新建对话", "New chat"),
        ["GroupNewWorkspaceTip"] = ("新建工作区", "New workspace"),
        ["ArchiveConversation"] = ("归档对话", "Archive session"),
        ["RestoreConversation"] = ("恢复对话", "Restore session"),
        ["RestoreAllArchived"] = ("全部恢复", "Restore all archived"),
        ["ArchiveWorkspace"] = ("归档工作区", "Archive workspace"),
        ["ArchiveWorkspaceConfirmFormat"] = ("归档「{0}」和它的 {1} 个对话？它们会从列表里收起，记录一条都不删。",
            "Archive “{0}” and its {1} sessions? They fold out of the list; nothing is deleted."),
        ["EditWorkspacePath"] = ("编辑路径…", "Edit path…"),
        ["EditWorkspacePathTitle"] = ("这个工作区的新路径", "New path for this workspace"),
        ["WorkspaceActions"] = ("工作区操作", "Workspace actions"),
        // A workspace outlives the sessions in it: the group stays listed with this one line under it, because a
        // place the user named that quietly left the sidebar reads as a button that did nothing.
        ["NoWorkspaceSessions"] = ("暂无会话", "No sessions yet"),
        ["RemoveWorkspace"] = ("移除工作区", "Remove workspace"),
        ["WorkspaceRemovedFormat"] = ("「{0}」已从列表移除，对话记录一条都没删",
            "Removed “{0}” from the list; no session was deleted"),
        // The re-path of a workspace with no sessions left: nothing moved, so the sentence must not count anything.
        ["WorkspacePathUpdatedFormat"] = ("「{0}」现在指向：{1}", "“{0}” now points at: {1}"),

        ["WorkspaceMovedFormat"] = ("「{0}」的 {1} 个对话已改用：{2}",
            "{1} sessions in “{0}” now work in: {2}"),
        // Provider management (a custom provider = an endpoint the user brings; the main way to attach a local model).
        ["AddProvider"] = ("添加提供商", "Add provider"),
        ["EditProvider"] = ("编辑提供商", "Edit provider"),
        ["RemoveProvider"] = ("移除提供商", "Remove provider"),
        ["AddCustomProvider"] = ("添加自定义提供商", "Add custom provider"),
        ["EditProviderTitle"] = ("编辑提供商", "Edit provider"),
        ["ProviderName"] = ("名称", "Name"),
        ["ProviderBaseUrl"] = ("接口地址", "Base URL"),
        ["ProviderBaseUrlHint"] = ("OpenAI 兼容的接口地址，例如 https://api.openai.com/v1，本地模型如 http://localhost:11434/v1。", "OpenAI-compatible base URL, e.g. https://api.openai.com/v1; for a local model, http://localhost:11434/v1."),
        ["ProviderDisplayNameHint"] = ("显示在模型选择器中的名称", "Shown next to models in the picker."),
        ["ProviderWireApi"] = ("Wire API", "Wire API"),
        ["ProviderWireChat"] = ("Chat Completions", "Chat Completions"),
        ["ProviderWireResponses"] = ("Responses", "Responses"),
        ["ProviderWireApiHint"] = ("选择此端点支持的请求格式。大多数 OpenAI 兼容网关使用 Chat Completions；OpenAI GPT-5 系列使用 Responses。", "Choose the request format this endpoint supports. Most OpenAI-compatible gateways use Chat Completions; OpenAI's GPT-5 series uses Responses."),
        ["ProviderApiKeyOptional"] = ("API key（可选）", "API key (optional)"),
        ["ProviderApiKeyHint"] = ("如果端点不需要鉴权，请留空。密钥保存在系统凭据库中；不可用时使用加密存储。", "Leave blank if your endpoint doesn't require auth. The key uses your OS credential store, or encrypted local storage when unavailable."),
        ["ProviderCustomHeaders"] = ("自定义请求头（JSON，可选）", "Custom headers (JSON, optional)"),
        ["ProviderCustomHeadersHint"] = ("额外 HTTP 请求头，例如 {\"X-Org\":\"acme\"}。", "Additional HTTP headers, e.g. {\"X-Org\":\"acme\"}."),
        ["ProviderCustomHeadersInvalid"] = ("自定义请求头必须是 JSON 对象，且所有值都必须是字符串。", "Custom headers must be a JSON object with string values."),
        ["ProviderWireApiInvalid"] = ("请选择支持的请求线路。", "Choose a supported wire API."),
        ["AddProviderAction"] = ("添加提供商", "Add provider"),
        ["ModelsCount"] = ("模型（{0}）", "Models ({0})"),
        ["CurrentModelSummary"] = ("当前使用：{0}", "In use: {0}"),
        ["ToggleModelList"] = ("展开或折叠模型列表", "Expand or collapse model list"),
        ["ProviderNameRequired"] = ("请填写提供商名称。", "Enter a provider name."),
        ["ProviderBaseUrlInvalid"] = ("接口地址无效：请填写完整的 http/https 地址。", "Invalid base URL: enter a full http/https address."),
        ["ProviderBuiltInReadOnly"] = ("内置提供商不能移除；如需其他接口，请添加自定义提供商。", "Built-in providers can't be removed. To use another endpoint, add a custom provider."),
        ["ProviderRemoved"] = ("已移除提供商。", "Provider removed."),
        ["ProviderBuiltInLockedHint"] = ("内置提供商的接口地址与名称由清单固定，可修改模型和密钥。", "A built-in provider's base URL and name come from the manifest; its model and key can be changed."),
        ["RemoveProviderConfirm"] = ("移除后该提供商的密钥也会删除，且无法恢复。", "Removing it also deletes its stored API key. This cannot be undone."),
        // Provider preset picker (aligned with GitHub Copilot's "list + searchable add" interaction).
        ["ProviderPickerHint"] = ("选择要接入的模型提供商。可搜索名称或说明；接入清单外的私有接口请选「自定义接口」。", "Pick a model provider to add. Search by name or description; for a private endpoint that isn't listed, choose \"Custom endpoint\"."),
        ["ProviderSearchHint"] = ("搜索提供商", "Search providers"),
        ["ProviderSearchNoMatch"] = ("没有匹配的提供商。试试更短的关键词，或选择「自定义接口」。", "No matching provider. Try a shorter term, or choose \"Custom endpoint\"."),
        ["ProviderCustomRow"] = ("自定义接口", "Custom endpoint"),
        ["ProviderCustomRowHint"] = ("手动填写 OpenAI 兼容的接口地址与模型，适用于自建网关或局域网内的本地模型。", "Enter an OpenAI-compatible base URL and model by hand — for a self-hosted gateway or a local model on another machine."),
        ["ProviderAlreadyAdded"] = ("该提供商已在列表中。", "That provider is already in the list."),
        ["ProviderEnabledHint"] = ("在聊天模型选择器中显示或隐藏此提供商。", "Show or hide this provider in the chat model picker."),
        ["ProviderDisabledTag"] = ("已停用", "Disabled"),
        ["ProviderKeylessReady"] = ("无需密钥", "No key needed"),
        ["ProviderEditSaved"] = ("已保存对提供商的更改。", "Provider changes saved."),
        ["ProviderAdded"] = ("已添加提供商。", "Provider added."),
        ["ApiKeyKeepHint"] = ("留空则保留当前已保存的密钥。", "Leave blank to keep the saved key."),
        ["ModelsAuthHint"] = ("完成鉴权后可从接口拉取模型，也可手动添加模型名。", "Authenticate to fetch models from the endpoint, or add a model name by hand."),
        ["NoProviderGuide"] = ("点「添加提供商」从预设清单中选择，或添加自定义接口。", "Choose \"Add provider\" to pick from the presets, or add a custom endpoint."),
        ["ModelCatalogFetchedAt"] = ("目录更新于 {0}", "Catalog updated {0}"),
        ["ModelEditTitle"] = ("编辑模型", "Edit model"),
        ["ModelContextEditHint"] = ("覆盖该模型的上下文窗口；留空则回退为自动判定。", "Override this model's context window; leave blank to fall back to the auto value."),
        // Sign-in method and accounts (one credential = one account, whatever its source).
        ["AuthMethod"] = ("认证方式", "Sign-in method"),
        ["AuthMethodApiKey"] = ("API 密钥", "API key"),
        ["AuthMethodOAuth"] = ("浏览器登录", "Sign in with browser"),
        ["AuthMethodNone"] = ("无需认证", "No sign-in needed"),
        ["AuthMethodNoneHint"] = ("本地接口不需要密钥，可以直接使用。", "A local endpoint needs no key and works as-is."),
        ["AuthOAuthSignIn"] = ("使用 {0} 登录", "Sign in with {0}"),
        ["AuthOAuthHint"] = ("将在浏览器中打开 {0} 的授权页面；完成授权后密钥会自动保存，无需手动复制。", "Opens {0}'s authorization page in your browser. The key is saved automatically once you approve — nothing to copy."),
        ["AuthOAuthPending"] = ("正在等待浏览器授权…", "Waiting for authorization in the browser…"),
        ["AuthOAuthSucceeded"] = ("已登录并保存密钥。", "Signed in; the key has been saved."),
        ["OpenAiPlanWelcomeTitle"] = ("正在使用 ChatGPT 套餐", "You're using your ChatGPT plan"),
        ["OpenAiPlanWelcome"] = ("符合条件的 AI 请求将使用你的 ChatGPT 套餐。你可以在 ChatGPT 设置中管理用量。", "Eligible AI requests will use your ChatGPT plan. You can manage usage in ChatGPT settings."),
        ["OpenAiLegacyRelogin"] = ("旧版 ChatGPT 凭据需要重新登录", "Legacy ChatGPT credential — sign in again"),
        ["OpenAiPlanNotGranted"] = ("已登录，但未授权使用 ChatGPT 套餐。请重新登录并启用套餐用量。", "Signed in, but ChatGPT plan usage was not authorized. Sign in again and enable plan usage."),
        ["OpenAiEnablePlan"] = ("启用套餐用量", "Enable plan usage"),
        ["OpenAiResumePlan"] = ("恢复套餐请求", "Resume plan requests"),
        ["AuthOAuthFailed"] = ("登录失败：", "Sign-in failed: "),
        ["AuthOAuthCancelled"] = ("已取消登录。", "Sign-in cancelled."),
        ["AuthOAuthScopeRejected"] = ("授权返回的权限范围比请求的更宽（{0}），已拒绝该密钥。请在授权页面仅勾选接口访问权限。", "The granted scope ({0}) is wider than the one requested, so the key was refused. On the consent page, grant API access only."),
        ["AuthOAuthNoBrowser"] = ("无法自动打开浏览器，请手动访问下面的地址：", "Could not open a browser automatically; open this address by hand:"),
        ["AuthOAuthPlatformUnsupported"] = ("当前平台尚不支持安全保存密钥，无法完成登录。", "Secure key storage is not available on this platform yet, so sign-in cannot complete."),
        // Device-code sign-in (RFC 8628): the code is typed on a different device, so these instructions never say which machine the browser is on.
        // The address appears once only — the hint text does not repeat it; it is the clickable line below.
        ["AuthDeviceTitle"] = ("在浏览器中登录", "Sign in from your browser"),
        ["AuthDeviceHint"] = ("在任意设备的浏览器打开下面的地址，然后输入验证码。", "Open the address below in a browser on any device, then enter the code."),
        ["AuthDeviceWaiting"] = ("等待授权完成…关闭此窗口即取消。", "Waiting for you to approve… closing this window cancels."),
        ["AuthDeviceCopy"] = ("复制验证码", "Copy the code"),
        ["AuthDeviceCopied"] = ("验证码已复制，粘贴到授权页即可。", "Code copied — paste it into the browser."),
        // The separator copy between the two sign-in entries when both are shown.
        ["AuthOrSeparator"] = ("或", "or"),
        // "Active" survives the account list's removal because the *model* rows reuse it to mark the model in
        // use. The key kept its old name rather than being renamed to ModelActive: HubTexts is bilingual data,
        // not something to churn, and a rename would have touched two languages to say nothing.
        ["AccountActive"] = ("使用中", "Active"),
        ["Affiliate"] = ("推荐", "Referral"),
        ["Add"] = ("添加", "Add"),
        ["Save"] = ("保存", "Save"),
        // The grouped list (each configured provider its own group, all expanded) and the model sub-list inside it.
        ["ProviderInUse"] = ("当前", "Current"),
        // The wire line on a provider's header: which URL this provider's model will actually be sent to. It is
        // shown rather than left to be inferred because the one failure this line catches — the endpoint answers
        // only /v1/responses while Hub posts to /v1/chat/completions — arrives as a 400 that names a path, not a
        // setting. Chinese and English both say "wire" rather than "protocol": what a person is looking for is
        // where the request goes.
        ["ProviderWireFormat"] = ("请求线路 {0}", "Wire {0}"),
        ["ProviderBuiltInTag"] = ("内置", "Built-in"),
        ["ProviderCustomTag"] = ("自定义", "Custom"),
        ["ProviderNoCredential"] = ("此提供商无需密钥，可直接使用。", "This provider needs no key and works as-is."),
        ["RemoveProviderIcon"] = ("移除", "Remove"),
        ["Models"] = ("模型", "Models"),
        ["ModelsHint"] = ("同一提供商可保存多个模型，标为「使用中」的是默认模型；每个对话也可单独选择模型。", "A provider can hold several models; \"In use\" is its default, and each session can choose a model independently."),
        ["AddModel"] = ("添加模型", "Add model"),
        ["RefreshModels"] = ("刷新", "Refresh"),
        ["RefreshModelsHint"] = ("从提供商重新拉取可用模型列表。", "Fetch the available model list from the provider again."),
        // Fetch outcomes, written as three separate endings rather than one catch-all sentence: "asked and got none" and "never reached the
        // provider" are two completely different things for the user — the first means checking account permissions or the local service, the
        // second means checking the network; merged into one sentence they tell the user nothing to do.
        ["ModelsRefreshing"] = ("正在拉取模型列表…", "Fetching the model list…"),
        ["ModelsFetched"] = ("已拉取 {0} 个模型。", "Fetched {0} models."),
        ["ModelsFetchedEmpty"] = ("该提供商没有返回任何模型。", "The provider returned no models."),
        ["ModelsFetchFailed"] = ("未能拉取模型列表，保留原有列表。", "Could not fetch the model list; the existing list was kept."),
        ["ModelUse"] = ("使用此模型", "Use this model"),
        ["ModelEnabledHint"] = ("在聊天模型选择器中启用或隐藏此模型。", "Show or hide this model in the chat model picker."),
        ["ModelContextFormat"] = ("上下文 {0}", "context {0}"),
        // The row itself states only the number; where that number came from is a tooltip, because the same tag
        // is already on the chat's usage line and a settings row that repeats it says one thing twice. The
        // correction path is named here rather than shown as a control on purpose: `MaxContextTokens` is the
        // exact casing ProviderStore writes, so what this sentence advertises is what the file actually holds.
        ["ModelContextSourceHint"] = ("Hub 按这个数字决定这段对话什么时候压缩。来源：{0}。要纠正，编辑数据目录里的 ai/providers.json，给这个模型写 MaxContextTokens。",
            "Hub compacts a session against this number. Source: {0}. To correct it, edit ai/providers.json in the data folder and set MaxContextTokens on this model."),
        ["CapabilitySourceEndpoint"] = ("模型自报", "reported"),
        ["CapabilitySourceManifest"] = ("预设声明", "preset"),
        ["CapabilitySourceOverride"] = ("手动设置", "yours"),
        ["CapabilitySourceLearned"] = ("从拒绝学到", "learned"),
        ["CapabilitySourceFallback"] = ("无人报过，按默认", "assumed"),
        ["ModelRemove"] = ("移除该模型", "Remove this model"),
        ["ModelRemoveConfirm"] = ("将从本机列表移除该模型，提供商与密钥不受影响。", "Removes this model from the local list. The provider and its keys are unaffected."),
        ["ModelsRemoveAll"] = ("移除全部模型", "Remove all models"),
        ["ModelsRemoveAllConfirm"] = ("将从本机列表移除全部 {0} 个模型，提供商与密钥不受影响。", "Removes all {0} models from the local list. The provider and its keys are unaffected."),
        ["ModelsRemovedAll"] = ("已移除 {0} 个模型。", "Removed {0} models."),
        ["ModelAdded"] = ("已添加模型。", "Model added."),
        ["ModelRemoved"] = ("已移除模型。", "Model removed."),
        ["ModelDuplicated"] = ("该模型已在列表中。", "That model is already in the list."),
        ["ModelNameRequired"] = ("请填写模型名称。", "Enter a model name."),
        ["ModelNamePlaceholder"] = ("模型名称，例如 gpt-5.1-codex-mini", "Model name, e.g. gpt-5.1-codex-mini"),
        ["AddModelTitle"] = ("添加模型", "Add model"),
        ["OpenModelCatalog"] = ("浏览模型目录", "Browse catalog"),
        ["ModelCatalogTitle"] = ("{0} 模型目录", "{0} model catalog"),
        ["ModelCatalogHint"] = ("双击模型即可启用；可连续选择多个模型。", "Double-click models to enable them; you can choose several."),
        ["ModelCatalogSearchHint"] = ("搜索模型名称", "Search model names"),
        ["ModelCatalogEmpty"] = ("缓存目录为空。点击刷新从提供商获取模型。", "The cached catalog is empty. Refresh to fetch models from the provider."),
        ["ModelCatalogRefreshUnavailable"] = ("请先完成提供商鉴权后再刷新模型目录。", "Authenticate this provider before refreshing its model catalog."),
        ["ModelCatalogNoMatch"] = ("没有匹配的模型。", "No matching models."),
        ["ModelCatalogEnabled"] = ("已启用模型：{0}", "Enabled model: {0}"),
        ["ModelCatalogEnableFailed"] = ("无法启用该模型，请刷新目录后重试。", "Could not enable this model. Refresh the catalog and try again."),
        ["ModelCatalogEnabledState"] = ("已启用", "Enabled"),
        ["ModelCatalogDisabledState"] = ("双击启用", "Double-click to enable"),
        ["CloseModelCatalog"] = ("关闭", "Close"),
        ["NoEnabledModels"] = ("尚未启用模型。浏览模型目录，或手动添加模型。", "No models enabled. Browse the catalog or add a model manually."),
        ["NoModels"] = ("尚未添加模型。", "No models added yet."),
        // Authentication state and disconnecting. The checkmark = this provider is authenticated (it holds one credential, whatever its source). Terminology is unified on "authenticate / link".
        //
        // These strings were revised once after "one credential per provider": they used to say "all accounts", a leftover of the
        // multi-account model. A provider now holds a single credential, so saying "all" would make users think something else still needs clearing.
        ["ProviderConnected"] = ("已鉴权", "Authenticated"),
        ["ProviderNotConnected"] = ("未鉴权", "Not authenticated"),
        ["DisconnectProviderConfirm"] = ("将删除该提供商在本机保存的密钥，提供商本身保留，可随时重新鉴权。此操作无法恢复。", "Deletes the key saved for this provider on this machine. The provider itself stays and can be reauthenticated at any time. This cannot be undone."),
        ["ProviderDisconnected"] = ("已断开鉴权并清除本地密钥。", "Disconnected; local key cleared."),
        ["OpenAiPlanSignOutFirst"] = ("请先选择并退出每个已保存的 ChatGPT 套餐账号，再移除或断开提供商。", "Select and sign out of each saved ChatGPT plan account before removing or disconnecting the provider."),
        ["OpenAiPlanDisconnectConfirm"] = ("将退出当前 ChatGPT 账号并删除本机凭据。其他保存的账号不会被删除。", "Sign out of the current ChatGPT account and delete its local credential. Other saved accounts will be kept."),
        ["OpenAiPlanRevokeUnconfirmed"] = ("已清除本地凭据，但无法确认远端会话已撤销。请在 ChatGPT 设置中检查已连接的应用。", "Local credentials were cleared, but remote session revocation could not be confirmed. Check connected apps in ChatGPT settings."),
        ["ProviderNothingToDisconnect"] = ("该提供商没有已保存的密钥。", "This provider has no saved key."),
        ["ProviderPinnedHint"] = ("默认提供商不能移除（移除后会在下次启动时自动恢复）；可以断开鉴权清除本地密钥。", "The default provider can't be removed — it would be restored on the next launch. You can disconnect it to clear local keys."),

        // The summary line in the collapsed state. Only the model count is left: after the one-to-one change the account count is always 0 or 1,
        // and "1 account" is a number with no information in it — the connection state is told by the checkmark next to the name, no need for words to repeat it.
        ["SummaryModelCount"] = ("{0} 个已配置模型", "{0} configured models"),
        ["SummaryModelCountOne"] = ("1 个已配置模型", "1 configured model"),

        // ── The authentication dialog ──
        // The list carries a single button: it reads "Authenticate" when the provider is not authenticated and "Disconnect" when it is. The real
        // input happens inside the dialog, so the list page never shows a text field.
        ["Authenticate"] = ("鉴权", "Authenticate"),
        ["DisconnectProvider"] = ("断开鉴权", "Disconnect"),
        ["AuthDialogTitle"] = ("鉴权 {0}", "Authenticate {0}"),
        ["AuthDialogHint"] = ("该提供商支持以下鉴权方式，请选择一种。", "This provider supports the following methods. Pick one."),
        ["AuthMethodLoginOption"] = ("用 {0} 账号登录", "Sign in with a {0} account"),
        ["AuthMethodKeyOption"] = ("粘贴已有密钥", "Paste an existing key"),
        ["Next"] = ("下一步", "Next"),
        ["Back"] = ("上一步", "Back"),
        ["AuthKeyStepTitle"] = ("输入密钥", "Enter your key"),
        ["AuthKeyStepHint"] = ("密钥只保存在本机。确定前会向 {0} 校验一次。", "The key is stored on this machine only. It is checked against {0} before it is saved."),
        ["AuthKeyStepHintNoCheck"] = ("密钥只保存在本机。", "The key is stored on this machine only."),
        ["AuthKeyChecking"] = ("正在校验…", "Checking…"),
        ["AuthKeyRejected"] = ("{0} 拒绝了该密钥，请检查后重试。", "{0} rejected this key. Check it and try again."),
        // When the verification request itself fails (offline / endpoint unreachable) the key **is still saved**: calling "could not ask"
        // "key invalid" would leave a user on hotel Wi-Fi unable to connect no matter what they try. This distinction maps one-to-one onto the four states of KeyCheckOutcome.
        ["AuthKeyUnreachable"] = ("无法向 {0} 校验该密钥（网络或端点不可用），将直接保存。", "Could not check the key with {0} (network or endpoint unavailable); saving it as typed."),
        ["AuthKeyAccepted"] = ("密钥有效。", "The key is valid."),
        ["AuthDone"] = ("鉴权成功。", "Authenticated."),
        ["AuthDialogCancelled"] = ("已取消鉴权。", "Authentication cancelled."),
        ["AuthSecretsUnsupported"] = ("当前平台没有可用的密钥存储，无法保存鉴权信息。", "This platform has no secret store available, so authentication cannot be saved."),

        // Automatic disconnect when a provider is removed. The confirmation has to say the credentials go with it, otherwise "remove" looks like it only affects the list.
        ["RemoveProviderWithCredentials"] = ("将同时删除该提供商在本机保存的 {0} 个账号与密钥。此操作无法恢复。", "Also deletes the {0} account(s) and key(s) saved for this provider on this machine. This cannot be undone."),
        ["RemoveProviderIconHint"] = ("移除该提供商", "Remove this provider"),

        // The Copilot-style redesign: sidebar collapse, the empty-state greeting and suggestion chips, the model-changed notification.
        ["SidebarToggleTip"] = ("显示或隐藏导航栏", "Show or hide the navigation bar"),
        // The app draws its own title bar, so the three window controls are ours to name (they are icon-only,
        // so the tooltip is the only place their name appears). The platform convention's order and icons are
        // kept verbatim, including the Chinese forms Windows itself uses.
        ["MinimizeWindow"] = ("最小化", "Minimize"),
        ["MaximizeWindow"] = ("最大化", "Maximize"),
        ["RestoreWindow"] = ("向下还原", "Restore"),
        ["CloseWindow"] = ("关闭", "Close"),
        ["TrayToolTip"] = ("Axmol Hub", "Axmol Hub"),
        ["TrayShowWindow"] = ("显示主窗口", "Show main window"),
        ["TrayExit"] = ("退出", "Exit"),
        ["BottomMenuTip"] = ("设置、外观与语言", "Settings, appearance and language"),
        ["SearchConversationsTip"] = ("搜索对话", "Search sessions"),
        ["NewConversationTip"] = ("新建对话", "New session"),
        ["AssistantGreeting"] = ("你好，我是 Axmol 助手", "Hi, I'm your Axmol assistant"),
        // The subtitle is the scope of the whole assistant, so it must not name only engines: an Axmol-unrelated
        // question is in scope. The four chips below are the same promise stated as things to do.
        ["AssistantGreetingSubtitle"] = ("读代码、改代码、跑命令，都可以直接说", "Reading code, changing code, running commands — just ask"),
        // Each one is something the shipped tool set can actually finish: fs_read / fs_grep to look,
        // file_write to change, run_command to prove it worked. No engine-only phrasing — these are the general
        // entry points, and a chip that the assistant cannot honour teaches the user not to trust the empty state.
        ["AssistantSuggestReadCodebase"] = ("梳理这个项目的结构和入口", "Walk me through this project's structure and entry points"),
        ["AssistantSuggestFixBug"] = ("定位一个报错并修好它", "Find a bug and fix it"),
        ["AssistantSuggestWriteScript"] = ("写一个脚本并运行验证", "Write a script and run it to verify"),
        ["AssistantSuggestRefactor"] = ("把一段代码重构得更易读", "Refactor some code to be easier to read"),
        ["ModelChangedFormat"] = ("已切换模型：{0}", "Model changed to {0}"),
    };
}
