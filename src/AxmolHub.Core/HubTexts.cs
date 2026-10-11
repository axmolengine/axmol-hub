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
        ["BuildComplete"] = ("编译完成", "Build completed"),
        ["BuildConfiguration"] = ("构建配置", "Build configuration"),
        ["ConfigurationHint"] = ("Debug：调试、断言与详细日志。Release：优化构建。两种配置的产物分别保存。", "Debug: debugging, assertions and detailed logs. Release: optimized build. Outputs are stored separately."),
        ["AndroidReleasePending"] = ("Release 使用项目发行密钥，生成已签名 APK/AAB；继续后配置包名、版本和密钥。", "Release uses your project key to produce signed APK/AAB. Configure application ID, version and signing after continuing."),
        ["AndroidReleaseSettings"] = ("Android 发行设置", "Android release settings"),
        ["LastBuildPlatform"] = ("当前构建目标", "Current build target"),
        ["ChooseBuildPlatform"] = ("选择构建平台", "Choose build platform"),
        ["BuildPlatformHint"] = ("项目源码可用于多个平台。每次构建可选择目标；Apple 平台需 macOS，Linux 需 Linux 宿主。", "Project sources support multiple platforms. Choose a target for this build; Apple platforms require macOS, Linux requires a Linux host."),
        ["Continue"] = ("继续", "Continue"),
        ["DownloadSize"] = ("下载大小", "Download size"),
        ["InstallSelected"] = ("保存并安装所选", "Save and install selection"),
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
        ["ChooseEngineVersion"] = ("选择引擎版本", "Choose engine version"),
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
        ["SettingsHint"] = ("按你的习惯设置语言、存储目录与编辑器。", "Choose your language, storage and editors."),
        ["Language"] = ("界面语言", "Interface language"),
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
        ["Editors"] = ("代码编辑器", "Code editors"),
        ["SelectEditor"] = ("选择程序", "Choose executable"),
        ["NotSelected"] = ("尚未选择", "Not selected"),
        ["EditorHint"] = ("选择已安装的编辑器，用于打开项目。", "Choose an installed editor to open projects."),
        ["DefaultEngine"] = ("默认引擎", "Default engine"),
        ["NoDefault"] = ("尚未安装引擎", "No engine installed"),
        ["LogTitle"] = ("活动与构建日志", "Activity and build log"),
        ["CopyError"] = ("复制错误", "Copy error"),
        ["CopyCode"] = ("复制代码", "Copy code"),
        ["OpenLogs"] = ("打开日志", "Open logs"),
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
        ["Install managed tools"] = ("安装基础工具", "Install tools"),
        ["Install Windows SDK"] = ("安装 Windows SDK", "Install Windows SDK"),
        ["Install MSVC Build Tools"] = ("安装 MSVC", "Install MSVC"),
        ["Set default engine"] = ("设置默认引擎", "Set default engine"),
        ["Verify engine"] = ("验证引擎", "Verify engine"),
        ["Remove engine from list"] = ("移出引擎列表", "Remove engine from list"),
        ["Remove project from list"] = ("移出项目列表", "Remove project from list"),
        ["Open project folder"] = ("打开项目目录", "Open project folder"),
        ["Open engine folder"] = ("打开引擎目录", "Open engine folder"),
        ["Open VS Code"] = ("打开 VS Code", "Open VS Code"),
        ["Open Visual Studio"] = ("打开 Visual Studio", "Open Visual Studio"),
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
        ["BuildProgress"] = ("构建进度", "Build progress"),
        ["BuildPreparing"] = ("准备构建", "Preparing build"),
        ["BuildConfiguring"] = ("配置项目", "Configuring project"),
        ["BuildCompiling"] = ("编译代码", "Compiling code"),
        ["BuildPackaging"] = ("打包应用", "Packaging application"),
        ["BuildVerifying"] = ("校验产物", "Verifying output"),
        ["BuildElapsed"] = ("已用时间", "Elapsed"),
        ["BuildProgressHint"] = ("百分比显示当前编译阶段的进度；配置、打包和校验时显示活动进度。", "Percentage shows the current compilation stage. Configuration, packaging and verification use an activity indicator."),
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
        ["Select devenv.exe"] = ("选择 devenv.exe", "Select devenv.exe"),
        ["Select Code.exe"] = ("选择 Code.exe", "Select Code.exe"),
        ["Choose devenv.exe"] = ("请选择 devenv.exe。", "Choose devenv.exe."),
        ["Choose Code.exe"] = ("请选择 Code.exe。", "Choose Code.exe."),
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
        // "Update available" does not use a dialog: it lights a red dot on the Settings nav item (see Hub.NotificationDot). The dot's
        // only job is "go look at Settings"; the version number, the download progress and the actions all live in the update card on that page.
        ["UpdateDotTooltip"] = ("有可用更新", "Update available"),
        ["UpdateCheckHint"] = ("点「检查更新」查看是否有新版本。", "Click \"Check for updates\" to see if a newer version exists."),
        ["AutoDownloadUpdates"] = ("自动下载更新", "Auto-download updates"),
        ["AllowSpawnSessions"] = ("允许助手派生子会话", "Let the assistant spawn helper sessions"),
        ["AllowSpawnSessionsHint"] = (
            "子会话在自己的上下文里干活，只把结论送回本会话——读一个大文件只要带回几行，就不必把它塞满当前窗口。"
            + "每一条都是要花钱的模型调用，并发槽也只有三个，所以默认关闭；一次回答最多派生一个，子会话自己不许再派生。",
            "A spawned session works in a context of its own and sends only its conclusion back, so reading a "
            + "large file costs a few lines here instead of the whole file. Each one is a model call, and there "
            + "are three answer slots in total, so this ships off. One answer spawns at most one helper, and a "
            + "helper cannot spawn further."),
        // The outbound switch differs from the one above in its default: fetching a page the user named is a basic action of this kind
        // of assistant, so shipping it closed makes the feature nonexistent for anyone who never finds the setting. The copy therefore
        // has to say that "on" is the default, and which class of action a switch-off refuses.
        ["AllowWebFetch"] = ("允许助手抓取网页", "Let the assistant read web pages"),
        ["AllowWebFetchHint"] = (
            "默认开启。抓取只走 https、只允许公网主机，重定向不许跨出 https，一次最多读 1 MiB；把脚本、样式和导航"
            + "去掉之后，正文才交回模型。关掉它只是让这条通道不存在——命令沙箱里的 curl 从来不受这个开关约束，"
            + "「询问审批」档下每一次抓取都照样要过一张卡。provider 若在 ai/providers.json 里声明了 serverTools，"
            + "同一个开关也决定要不要让服务端自己联网搜索；那一类搜索由服务商执行，任何审批卡都拦不住它。",
            "On by default. Fetching is https only, to public hosts, with a redirect never allowed to leave "
            + "https, and at most 1 MiB is read; scripts, styles and navigation are stripped before the page text "
            + "reaches the model. Turning this off removes the channel — it has never governed curl inside the "
            + "command sandbox — and in 「询问审批」 every fetch still asks for approval on a card. If a provider "
            + "declares serverTools in ai/providers.json, the same switch decides whether its endpoint is offered "
            + "a search of its own; that kind of search runs inside the service, and no approval card can stop one "
            + "mid-reply."),
        ["UpdateReady"] = ("发现新版本 {0}。", "Version {0} is available."),
        // The status line of all three states — downloading / ready / download failed — has to carry the version number: once a download
        // starts, "Version x.y.z is available" is pushed out by the progress copy and the user can no longer see which version they are moving to.
        // {0} is the version number, prefixed to the status text.
        ["UpdateVersionPrefix"] = ("新版本 {0} · ", "Version {0} · "),
        // The red-dot tooltip carries the version. When there is none (nothing checked yet) the UpdateDotTooltip above is still used.
        ["UpdateDotTooltipVersion"] = ("有可用更新：{0}", "Update available: {0}"),
        ["DownloadAndRestart"] = ("下载并重启", "Download & restart"),
        ["UpdateDownloading"] = ("正在下载更新… {0}%", "Downloading update… {0}%"),
        // Hover tooltip for the progress bar / status line. {0} is an already-formatted rate ("2.4 MB/s"); the unit does not vary with the language.
        ["UpdateDownloadSpeed"] = ("下载速度：{0}", "Download speed: {0}"),
        ["UpdateReadyToRestart"] = ("更新已就绪，重启完成更新。", "Update ready — restart to finish."),
        // The two action buttons' tooltips name the version too: the status line says "ready", the button says "which version is being installed".
        ["UpdateDownloadActionTip"] = ("下载 {0} 并重启", "Download {0} and restart"),
        ["UpdateRestartActionTip"] = ("重启并安装 {0}", "Restart and install {0}"),
        ["RestartNow"] = ("立即重启", "Restart now"),
        // Deliberately without {0}: what gets appended after it is the raw exception text, which may contain braces, and string.Format would throw a FormatException.
        ["UpdateDownloadFailed"] = ("更新下载失败：", "Couldn't download the update: "),
        // Built-in AI assistant. The protocol baseline is OpenAI-compatible; the first phase's providers = OrcaRouter + custom.
        ["Assistant"] = ("AI 助手", "AI assistant"),
        ["AssistantHint"] = ("向助手提问 Axmol 相关问题。模型由你配置的 provider 提供。", "Ask the assistant about Axmol. Answers come from the provider you configure."),
        ["AssistantEmpty"] = ("开始新的对话。", "Start a new conversation."),
        ["NewConversation"] = ("新建对话", "New conversation"),
        ["DeleteConversation"] = ("删除对话", "Delete conversation"),
        ["SearchConversations"] = ("搜索对话", "Search conversations"),
        ["NoSearchResults"] = ("没有匹配的对话。", "No matching conversations."),
        ["ConversationActions"] = ("对话操作", "Conversation actions"),
        ["Conversation"] = ("对话", "Conversation"),
        ["NoConversations"] = ("还没有对话。", "No conversations yet."),
        ["NoAvailableChatModels"] = ("请先在设置中鉴权并添加可用模型。", "Authenticate a provider and add an available model in Settings first."),
        ["Send"] = ("发送", "Send"),
        ["Stop"] = ("停止", "Stop"),
        ["ChatSteer"] = ("引导回复", "Steer response"),
        ["ChatSteering"] = ("正在切换方向…", "Steering response…"),
        // The keyboard used to be the only thing this hint named, and a screenshot arriving from Explorer or
        // Ctrl+V is the interaction people reach for most. It is one string, so the composer says it once.
        ["InputPlaceholder"] = ("输入消息，Enter 发送，Shift+Enter 换行；截图可以粘贴或拖进来",
            "Type a message. Enter to send, Shift+Enter for a newline. Paste or drop a screenshot"),
        ["ChatModeAsk"] = ("提问", "Ask"),
        ["ChatModePlan"] = ("计划", "Plan"),
        ["ChatModeAgent"] = ("代理", "Agent"),
        ["ChatModeGoal"] = ("目标", "Goal"),
        ["ChatModeResetHint"] = ("点击恢复默认目标模式", "Click to return to the default Goal mode"),
        ["ChatReasoningDefault"] = ("推理：默认", "Reasoning: Default"),
        ["ChatReasoningLow"] = ("推理：低", "Reasoning: Low"),
        ["ChatReasoningMedium"] = ("推理：中", "Reasoning: Medium"),
        ["ChatReasoningHigh"] = ("推理：高", "Reasoning: High"),
        ["ChatReasoningXHigh"] = ("推理：超高", "Reasoning: Extra high"),
        ["ChatReasoningMax"] = ("推理：最大", "Reasoning: Max"),
        ["ChatReasoningUltra"] = ("推理：极致", "Reasoning: Ultra"),
        // Who picks the tier for this request. The wording deliberately avoids a bare "Auto": in this app that word can also be the
        // gateway's model id orcarouter/auto, while "Auto routing" on the chip has to carry exactly one meaning.
        ["ChatRoutingMenu"] = ("档位由谁决定", "Who picks the tier"),
        ["ChatRoutingManual"] = ("我手动选", "I pick manually"),
        ["ChatRoutingAuto"] = ("自动（按任务强度）", "Auto (by task strength)"),
        ["ChatRoutingAutoChip"] = ("自动路由", "Auto routing"),
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
        // Both numbers, because the two are not the same claim: the provider published one, this session's own
        // response proved the other. A reader who sees only the smaller figure would look for a setting nobody set.
        ["ChatContextDriftNote"] = ("这条会话的实测把窗口从 {0} 收窄到 {1}。",
            "This session's own reading corrected the window from {0} to {1}."),
        ["ChatContextDroppedFormat"] = ("最早 {0} 轮没有发出去，只发了最近的部分", "{0} oldest turns were not sent — only the newest part goes out"),
        ["ChatContextOverfull"] = ("已经超出这个会话可用的窗口：先压缩，或去掉一个附件。",
            "Past the room this conversation has: compress it, or drop an attachment."),
        ["ChatCompressContext"] = ("压缩上下文", "Compress context"),
        ["ChatCompressingContext"] = ("正在压缩上下文…", "Compressing context…"),
        ["ChatContextCompressUnavailable"] = ("当前对话没有足够的早期内容可供压缩。", "There is not enough earlier conversation to compress yet."),
        ["ChatContextCompressFailed"] = ("压缩上下文失败：", "Could not compress context: "),
        ["ChatContextCompressing"] = ("上下文压缩完成前，暂时不能发送或重试消息。", "Sending or retrying is unavailable until context compression finishes."),
        ["ChatAddContext"] = ("添加上下文", "Add context"),
        ["ChatAddLocalFolder"] = ("添加本地文件夹…", "Add local folder…"),
        // Pictures are a second channel next to the text attachments: a folder becomes bytes of text in the
        // prompt, a picture stays a picture. Same composer, same chips, never the same reader.
        ["ChatAddImage"] = ("添加图片…", "Add image…"),
        ["ChatPickImageTitle"] = ("选择要发给助手的图片", "Choose an image to send to the assistant"),
        ["ChatImageEmpty"] = ("没有可附上的图片内容。", "There are no image bytes to attach."),
        ["ChatImageUnrecognized"] = ("这不是 PNG / JPEG / GIF / WebP 图片，Hub 不知道它是什么格式，也就发不出去。",
            "That is not a PNG, JPEG, GIF or WebP image, so Hub has no media type to send."),
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
        ["ChatWorkspaceProtected"] = ("这是 Hub 的数据目录、引擎安装目录或 .git，助手在任何审批档位下都不能写它。", "That is Hub's data directory, an engine installation or a .git directory. The assistant cannot write there in any approval mode."),
        ["ChatAttachmentFailed"] = ("读取聊天上下文失败：", "Could not read chat context: "),
        ["ChatFolderPrefix"] = ("文件夹：", "Folder: "),
        ["ChatProjectPrefix"] = ("工程：", "Project: "),
        ["ChatProjectContextFormat"] = ("Axmol Hub project context — engine {0}; target {1}; configuration {2}; build status {3}.", "Axmol Hub project context — engine {0}; target {1}; configuration {2}; build status {3}."),
        ["ChatToolProjects"] = ("项目", "projects"),
        ["ChatToolEngines"] = ("引擎", "engines"),
        ["ChatToolchains"] = ("工具链状态", "toolchain status"),
        ["ChatToolRunningFormat"] = ("正在读取{0}", "Reading {0}"),
        ["ChatToolCompletedFormat"] = ("已读取{0}，正在继续", "Read {0}; continuing"),
        ["Provider"] = ("模型提供商", "Provider"),
        // Title and description of the AI model provider management card on the Settings page.
        ["ModelProviders"] = ("AI 模型提供商", "AI Model providers"),
        ["ModelProvidersHint"] = ("助手使用的模型接口。「添加提供商」可从内置预设中挑选（OrcaRouter、OpenAI、DeepSeek、Ollama…），已配置的提供商在此管理密钥与模型；助手页只负责选择用哪一个。", "The endpoints the assistant can use. \"Add provider\" picks from built-in presets (OrcaRouter, OpenAI, DeepSeek, Ollama …); keys and models for the ones you configured are managed here, while the assistant page only chooses between them."),
        // Model hint on the assistant page; {0} = provider name, {1} = model name.
        ["ActiveModelFormat"] = ("当前模型：{0} · {1}", "Current model: {0} · {1}"),
        ["NoProvider"] = ("尚未配置模型提供商。", "No model provider is configured yet."),
        ["ApiKey"] = ("API 密钥", "API key"),
        // The wording states only what is true on both backend tiers. "Stored in the operating system's credential store" is wrong on the
        // Linux file backend, and the backend line (AuthSecretBackendFile) is what makes "where this key actually went" clear.
        ["ApiKeyHint"] = ("密钥只保存在本机，不会写入配置文件或日志。", "The key stays on this machine and is never written to config files or logs."),
        ["ApiKeyPlatformUnsupported"] = ("当前平台尚不支持安全保存密钥（Windows 使用 DPAPI，Linux 使用本机加密文件；macOS 的 Keychain 待补）。", "Secure key storage is not available on this platform (Windows uses DPAPI, Linux uses an encrypted local file; the macOS Keychain backend is pending)."),
        // Backend disclosure: the three tiers guard against different things, and one blanket "safely stored" would flatten the difference, so the store itself reports which tier it is.
        ["AuthSecretBackendFile"] = ("此平台的密钥保存在本机加密文件中（数据密钥在用户配置目录，权限 600），而非系统凭据存储。", "On this platform keys are kept in an encrypted local file (data key in your profile directory, mode 600) rather than the OS credential store."),
        ["AuthSecretBackendDegraded"] = ("已存储的密钥有无法读取的项，重新输入即可覆盖：", "A stored key could not be read; entering it again replaces it: "),
        ["AuthNoBrowserLink"] = ("无法打开浏览器，请手动访问：", "No browser could be opened. Visit this link by hand: "),
        ["SaveApiKey"] = ("保存密钥", "Save key"),
        // Affiliate disclosure: OrcaRouter takes part in the OSS programme; registering through this project's referral link lets Simdsoft earn a commission, and all of that income funds development of the Axmol engine and its tooling (see planning D5).
        ["AffiliateDisclosure"] = ("通过本项目的推荐链接注册 {0}，Simdsoft 可获得分成，所得收入将全部用于 Axmol 引擎及相关工具的研发。", "Signing up for {0} through this project's referral link earns Simdsoft a commission, all of which goes toward developing the Axmol engine and its tooling."),
        ["ViewReferral"] = ("了解详情", "Learn more"),
        ["ChatFailed"] = ("助手请求失败：", "Assistant request failed: "),
        ["ChatConnectionFailed"] = ("连接模型服务失败：", "Could not connect to the model provider: "),
        ["ChatTimedOut"] = ("模型请求超时，请检查网络或服务状态后重试。", "The model request timed out. Check your connection or the provider status and try again."),
        ["ChatNoResponse"] = ("模型没有返回内容，请重试或检查模型配置。", "The model returned no content. Try again or check the model configuration."),
        ["ChatCancelled"] = ("已停止。", "Stopped."),
        // Not an error and not silent: the earlier turns were replaced by a summary, so a reply that seems to
        // have forgotten something has a reason the user can see.
        ["ChatContextCompacted"] = ("已自动压缩较早的上下文。", "Compacted the earlier context automatically."),
        // The model wrote its tool call out as text instead of sending one, so no tool ran and the reply is
        // markup. The block on screen already says which of the two it is not, and a reader who is not told
        // spends the evening wondering why the build never started.
        ["ChatToolCallLeaked"] = ("模型把工具调用当成了正文输出，这次调用没有执行。", "The model wrote its tool call as text instead of sending it, so nothing ran."),
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
        ["ChatPlanAwaitingApproval"] = ("请先处理待确认的计划，再继续发送消息。", "Resolve the pending plan before sending another message."),
        ["ChatPlanNotificationTitle"] = ("计划等待确认", "Plan awaiting approval"),
        ["ChatPlanNotificationBody"] = ("会话「{0}」中的计划需要你的决定。", "A plan in 「{0}」 needs your decision."),
        ["ChatRunCompletedNotificationTitle"] = ("助手任务已完成", "Assistant task completed"),
        ["ChatRunFailedNotificationTitle"] = ("助手任务失败", "Assistant task failed"),
        ["ChatRunTimedOutNotificationTitle"] = ("助手任务超时", "Assistant task timed out"),
        ["ChatRunNotificationBody"] = ("会话「{0}」中的助手任务已结束。", "The assistant task in 「{0}」 has finished."),
        ["ChatToolApprovalNotificationTitle"] = ("操作等待批准", "Action awaiting approval"),
        ["ChatToolApprovalNotificationBody"] = ("会话「{0}」需要你批准一项操作。", "A conversation 「{0}」 needs you to approve an action."),
        ["AttentionDiagnosticTitle"] = ("Axmol Hub 通知测试", "Axmol Hub notification test"),
        ["AttentionDiagnosticBody"] = ("这是系统通知测试；任务栏标记将在 30 秒后恢复。", "This is a system notification test; the taskbar badge will reset after 30 seconds."),
        // After the decision the card collapses to one of these lines — the record stays in the transcript, the
        // buttons do not.
        ["ChatApprovalResolvedApproved"] = ("已批准", "Allowed"),
        ["ChatApprovalResolvedDenied"] = ("已拒绝", "Refused"),
        ["ChatApprovalResolvedSuperseded"] = ("未执行（换了话题）", "Not run (the conversation moved on)"),
        // The write's own record and its one exit. A call that never had to ask still changed the file, so the
        // line names the file; the button only rides along while there is a copy to go back to — and it stays
        // visible instead of waiting for a hover, because an undo nobody can find is a mistake that cannot be fixed.
        ["ChatWriteRecordFormat"] = ("写入 {0}", "wrote {0}"),
        ["ChatUndoButton"] = ("撤销", "Undo"),
        ["ChatUndoTip"] = ("把 {0} 恢复到这次写入之前的内容", "Put {0} back to what it held before this write"),

        // ── The decision surfaces that live in the composer (2026-10-08) ──
        // A pending call and a pending plan are both a question the run is parked on, and both are now asked in
        // the composer rather than in a row the person has to scroll back to. The title names the tool because
        // "run file_write" is not the question — what it writes is, and that is the body.
        ["ChatApprovalWantsFormat"] = ("{0} 想要执行一个操作", "{0} wants to run an action"),
        ["ChatApprovalWaitingLine"] = ("{0} 正在等你批准，决定在输入区", "{0} is waiting for your decision in the composer"),
        ["ChatApprovalMoreWaitingFormat"] = ("还有 {0} 项在等待", "{0} more waiting"),
        ["ChatApprovalExpand"] = ("展开完整内容", "Expand"),
        ["ChatApprovalCollapse"] = ("收起", "Collapse"),
        // "Always allow" renamed to its consequence, twice over: the bare word promised more than the grant did, and
        // the grant now reaches past this session, so the line has to say where it lands and how to take it back.
        ["ApprovalAllowAlwaysForTool"] = ("以后都不问 {0}", "Stop asking about {0}"),
        ["ApprovalAllowAlwaysTip"] = ("记进「设置 → 工具权限」的信任清单，随时可以撤销；对越出工程沙箱的操作无效",
            "Remembered in Settings → Tool permission, where it can be revoked. Has no effect on anything that "
            + "reaches past the workspace."),
        ["ChatPlanReviewTitle"] = ("审阅计划", "Review plan"),
        ["ChatPlanReviewOpen"] = ("查看计划", "View plan"),
        ["ChatPlanReviewContinue"] = ("继续", "Continue"),
        ["ChatPlanOptionApprove"] = ("批准并开始实施", "Approve and start implementing"),
        ["ChatPlanOptionRevise"] = ("我要修改计划", "Suggest changes to the plan"),
        ["ChatPlanFeedbackPlaceholder"] = ("说说计划要改哪里…", "Tell me what to change…"),
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
        ["InspectorClose"] = ("关闭面板", "Close panel"),
        ["InspectorChangesEmpty"] = ("这个会话还没改动任何文件", "This session has not touched any file yet"),
        ["InspectorPlanEmpty"] = ("还没有生成计划", "No plan yet"),
        // Four honest ways a diff can be unavailable. Each names why it stopped instead of drawing nothing:
        // a created file has no before by construction, and a copy that aged out or was spent by a revert is
        // gone for good in a store bounded at 64 files / 8 MiB.
        ["InspectorDiffCreatedFile"] = ("新建的文件没有改动前", "A new file has no before"),
        ["InspectorDiffCopyGone"] = ("改动前的副本已经不在了", "The pre-image has aged out of Hub's bounded store"),
        ["InspectorDiffCopySpent"] = ("那次写入已被撤销，副本用完", "The copy was spent by a revert"),
        ["InspectorDiffUnreadable"] = ("现在读不到这个文件，或它不是文本", "The file is gone or is not text"),
        ["InspectorDiffRefusedLine"] = ("没有改动", "No change"),
        ["InspectorAddedFormat"] = ("+{0}", "+{0}"),
        ["InspectorRemovedFormat"] = ("−{0}", "−{0}"),
        // ── The inspector's third tab: the repository itself ──
        // The pane already answers "what did this session do". This answers a different question — "what is
        // uncommitted here" — and the two must not be merged, because a person's week of own work would otherwise
        // read as the assistant's. Every state below is a sentence the tab can end on: each names why it stopped
        // rather than drawing an empty list, which is the same rule the four diff states above follow.
        ["InspectorTabRepo"] = ("仓库", "Repository"),
        // The invitation on a run's +N −M chip. The count alone says how much, not where to look; this is the
        // same one word of affordance the plan card's 「查看计划」 gives, and the only place that says the number
        // can be pressed.
        ["InspectorOpenChanges"] = ("看这些改动", "See these changes"),
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
        ["InspectorRepoNoWorkspace"] = ("这个会话还没有工作目录", "This session has no workspace yet"),
        ["InspectorRepoNotGit"] = ("这个目录不是 git 仓库", "This directory is not a git repository"),
        ["InspectorRepoGitMissing"] = ("这台机器上没有 git，读不了仓库状态",
            "git is not on this machine, so the repository cannot be read"),
        ["InspectorRepoUnsafe"] = ("git 不信任这个目录的归属，要你亲自认下来它才让读",
            "git does not trust who owns this repository; it needs your own decision before Hub may read it"),
        ["InspectorRepoFailed"] = ("git 没能回答", "git did not answer"),
        // The two empty-scope jumps. A list that is empty because the *other* scope is not leaves the reader at a
        // dead end, so the empty sentence offers the way across — and only ever with a number it has actually
        // verified: the changes tab offers this when a read has said the tree is dirty, the repository tab when
        // the transcript says this session wrote files.
        ["InspectorChangesJumpRepoFormat"] = ("仓库里有 {0} 处未提交，去仓库页看",
            "the repository holds {0} uncommitted files — look at the Repository tab"),
        ["InspectorRepoJumpSessionFormat"] = ("本会话改了 {0} 个文件，去改动页看",
            "this session changed {0} files — look at the Changes tab"),
        // The column's two states. The restore wording names Esc because the key is the only other way out of a
        // surface that has just covered the window, and a keyboard exit nobody announces is not a keyboard exit.
        ["InspectorExpand"] = ("撑开面板到整个窗口", "Expand the panel to the whole window"),
        ["InspectorRestore"] = ("还原成右边那一列（Esc）", "Back to the right-hand column (Esc)"),
        // ── A run's activity, folded into one collapsible group ──
        ["ActivityGroupToolCountFormat"] = ("执行工具 {0} 次", "{0} tool call(s)"),
        // The collapsed head reads as a full-width sentence naming the first action, so a tools-only turn is a
        // line of content rather than a lonely pill stranded at the left edge with an empty middle.
        ["ActivityGroupToolSummaryFormat"] = ("{0} 以及另外 {1} 个工具调用", "{0} and {1} other tool calls"),
        ["ActivityGroupRunningFormat"] = ("正在执行中 · {0}", "Running · {0}"),
        ["ActivityGroupThoughtFormat"] = ("已思考 {0}", "Thought for {0}"),
        ["ActivityRowThought"] = ("已思考", "Thought"),
        ["ActivityRowRead"] = ("读取 {0}", "read {0}"),
        ["ActivityRowSearch"] = ("搜索 {0}", "search {0}"),
        ["ActivityRowList"] = ("列出 {0}", "list {0}"),
        ["ActivityRowFind"] = ("查找 {0}", "find {0}"),
        ["ActivityRowEditFile"] = ("编辑文件 {0}", "edit {0}"),
        ["ActivityRowRunCommand"] = ("执行命令", "run command"),
        ["ActivityRowFetch"] = ("抓取 {0}", "fetch {0}"),
        ["ActivityRowGeneric"] = ("调用 {0}", "call {0}"),
        // A search the endpoint ran for itself, drawn as one live status line. It has no card and no result turn,
        // so this is the only place the person learns the answer came from outside — and "searching" is a state of
        // its own here rather than a variation of "search xxx", because the queries often are not known yet: the
        // start event arrives with nothing in it.
        ["ActivityRowWebSearching"] = ("正在联网搜索…", "searching the web…"),
        ["ActivityRowWebSearched"] = ("联网搜索：{0}", "searched the web for {0}"),
        // Under the reply, the addresses it read. Named "Sources" rather than "References" because a model that searched
        // and cited nothing has to read as unread, and a softer word would let that pass for an answer.
        ["MessageSources"] = ("来源", "Sources"),
        ["MessageSearchedFor"] = ("它自己查了：{0}", "it searched for: {0}"),
        ["ActivityRowExpandThought"] = ("看思考全文", "Show the full thinking"),
        // Under a folded action row, one expandable panel lays the exchange out in full: the arguments the call
        // was made with and the payload that came back. The collapsed row only names the action; this is where the
        // concrete operation becomes readable rather than one hover away.
        ["ActivityDetailInput"] = ("输入", "Input"),
        ["ActivityDetailOutput"] = ("输出", "Output"),
        ["ActivityDetailEmpty"] = ("无内容", "Nothing"),
        ["ActivityDetailTruncated"] = ("…（已截断）", "… (truncated)"),
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
        // The permission tiers, spelled out where the choice is made: each hint says what that tier lets through
        // unasked, which is the only thing the three names cannot carry themselves.
        ["ChatToolPermissionFollowFormat"] = ("跟随默认（{0}）", "Follow the default ({0})"),
        // The tiers are per session; the default is the answer for somebody who is not deciding one project.
        ["ChatToolPermissionSetDefaultFormat"] = ("把「{0}」设为默认", "Make {0} the default"),
        ["ToolApprovalAsk"] = ("询问审批", "Ask for approval"),
        ["ToolApprovalAskHint"] = ("除只读查询外，每个操作都先问一次，包括跑命令",
            "Everything except a read asks first, commands included"),
        ["ToolApprovalAuto"] = ("自动审批", "Auto-approve"),
        ["ToolApprovalAutoHint"] = ("工程内的写入和工程目录里的命令直接执行；抓屏、派生子会话、换工作目录仍要问",
            "Writes and commands inside the workspace run on their own; the screen, a child session and a new "
            + "working directory still ask"),
        ["ToolApprovalFull"] = ("完全访问", "Full access"),
        ["ToolApprovalFullHint"] = ("什么都不再问，包括抓屏和这个版本没登记过的工具",
            "Nothing asks any more, including the screen and any tool this build does not know"),
        ["ToolPermission"] = ("工具权限", "Tool permission"),
        ["ToolPermissionHint"] = ("助手动手之前的默认严格程度。单个会话可以在聊天里改，不影响这里。", "How strict the assistant is by default before it acts. A single session can override this in the chat without changing it here."),
        // The app-wide trust list: what a card's "always allow" writes, seen where it can be taken back. The hint
        // carries the one caveat a list of verbs cannot — a grant never covers what leaves the workspace.
        ["TrustedToolsTitle"] = ("以后都不问的工具", "Tools it stops asking about"),
        ["TrustedToolsHint"] = ("在审批卡上点过「以后都不问」的工具，本机每个会话都不再问；越出工程沙箱的操作不受这条信任影响。",
            "Tools clicked on an approval card stop asking in every session on this machine. Nothing that reaches "
            + "past the workspace is covered by a grant."),
        ["TrustedToolsNone"] = ("还没有信任过任何工具。", "Nothing is trusted yet."),
        ["RevokeTrustedTool"] = ("撤销", "Revoke"),
        ["RevokeAllTrustedTools"] = ("全部撤销", "Revoke all"),
        ["RunningSessionsFormat"] = ("{0} 个会话运行中", "{0} sessions running"),
        ["ChatPreparing"] = ("正在准备回复", "Preparing a response"),
        ["ChatGenerating"] = ("正在生成回复", "Generating response"),
        ["You"] = ("你", "You"),
        // Message-level actions (the action bar that appears when a bubble is hovered).
        ["CopyMessage"] = ("复制", "Copy"),
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
        ["RenameConversationTitle"] = ("重命名对话", "Rename conversation"),
        ["PinConversation"] = ("置顶", "Pin"),
        ["UnpinConversation"] = ("取消置顶", "Unpin"),
        // Session grouping: workspaces first, conversations with no working directory all go to the trailing "Chats" group, archived ones sink to the very bottom.
        // Time buckets gave way to directory grouping — conversations of one project scattered over "Today" and "Earlier" no longer read as one project.
        ["GroupWorkspaces"] = ("工作区", "Workspace"),
        ["GroupChats"] = ("对话", "Chats"),
        ["GroupArchived"] = ("已归档", "Archived"),
        ["ToggleGroup"] = ("展开或折叠这一组", "Expand or collapse this group"),
        // The "+" at the right of a group header: the workspace root node picks a directory first, the other groups create a session or a chat per what that group means.
        ["GroupNewSessionTip"] = ("新建会话", "New session"),
        ["GroupNewChatTip"] = ("新建对话", "New chat"),
        ["GroupNewWorkspaceTip"] = ("新建工作区", "New workspace"),
        ["ArchiveConversation"] = ("归档对话", "Archive conversation"),
        ["RestoreConversation"] = ("恢复对话", "Restore conversation"),
        ["RestoreAllArchived"] = ("全部恢复", "Restore all archived"),
        ["ArchiveWorkspace"] = ("归档工作区", "Archive workspace"),
        ["ArchiveWorkspaceConfirmFormat"] = ("归档「{0}」和它的 {1} 个对话？它们会从列表里收起，记录一条都不删。",
            "Archive “{0}” and its {1} conversations? They fold out of the list; nothing is deleted."),
        ["EditWorkspacePath"] = ("编辑路径…", "Edit path…"),
        ["EditWorkspacePathTitle"] = ("这个工作区的新路径", "New path for this workspace"),
        ["WorkspaceActions"] = ("工作区操作", "Workspace actions"),

        ["WorkspaceMovedFormat"] = ("「{0}」的 {1} 个对话已改用：{2}",
            "{1} conversations in “{0}” now work in: {2}"),
        // Provider management (a custom provider = an endpoint the user brings; the main way to attach a local model).
        ["AddProvider"] = ("添加提供商", "Add provider"),
        ["EditProvider"] = ("编辑提供商", "Edit provider"),
        ["RemoveProvider"] = ("移除提供商", "Remove provider"),
        ["AddCustomProvider"] = ("添加自定义提供商", "Add custom provider"),
        ["EditProviderTitle"] = ("编辑提供商", "Edit provider"),
        ["ProviderName"] = ("名称", "Name"),
        ["ProviderBaseUrl"] = ("接口地址", "Base URL"),
        ["ProviderBaseUrlHint"] = ("OpenAI 兼容的接口地址，例如 https://api.openai.com/v1，本地模型如 http://localhost:11434/v1。", "OpenAI-compatible base URL, e.g. https://api.openai.com/v1; for a local model, http://localhost:11434/v1."),
        ["ProviderModel"] = ("模型名称", "Model"),
        ["ModelsCount"] = ("模型（{0}）", "Models ({0})"),
        ["CurrentModelSummary"] = ("当前使用：{0}", "In use: {0}"),
        ["ToggleModelList"] = ("展开或折叠模型列表", "Expand or collapse model list"),
        ["ProviderModelHint"] = ("提供商使用的默认模型，例如 gpt-4o-mini、orcarouter/auto、llama3。", "Default model for this provider, e.g. gpt-4o-mini, orcarouter/auto, llama3."),
        ["ProviderNameRequired"] = ("请填写提供商名称。", "Enter a provider name."),
        ["ProviderBaseUrlInvalid"] = ("接口地址无效：请填写完整的 http/https 地址。", "Invalid base URL: enter a full http/https address."),
        ["ProviderModelRequired"] = ("请填写模型名称。", "Enter a model name."),
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
        ["ProviderBuiltInTag"] = ("内置", "Built-in"),
        ["ProviderCustomTag"] = ("自定义", "Custom"),
        ["ProviderNoCredential"] = ("此提供商无需密钥，可直接使用。", "This provider needs no key and works as-is."),
        ["RemoveProviderIcon"] = ("移除", "Remove"),
        ["Models"] = ("模型", "Models"),
        ["ModelsHint"] = ("同一提供商可保存多个模型，标为「使用中」的是默认模型；每个对话也可单独选择模型。", "A provider can hold several models; \"In use\" is its default, and each conversation can choose a model independently."),
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
            "Hub compacts a conversation against this number. Source: {0}. To correct it, edit ai/providers.json in the data folder and set MaxContextTokens on this model."),
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
        ["BottomMenuTip"] = ("设置与外观", "Settings and appearance"),
        ["SearchConversationsTip"] = ("搜索对话", "Search conversations"),
        ["NewConversationTip"] = ("新建对话", "New conversation"),
        ["AssistantGreeting"] = ("你好，我是 Axmol 助手", "Hi, I'm your Axmol assistant"),
        // The subtitle is the scope of the whole assistant, so it must not name only engines: an Axmol-unrelated
        // question is in scope. The four chips below are the same promise stated as things to do.
        ["AssistantGreetingSubtitle"] = ("读代码、改代码、跑命令，都可以直接说", "Reading code, changing code, running commands — just ask"),
        // Each one is something the shipped tool set can actually finish: read_file / search_text to look,
        // file_write to change, run_command to prove it worked. No engine-only phrasing — these are the general
        // entry points, and a chip that the assistant cannot honour teaches the user not to trust the empty state.
        ["AssistantSuggestReadCodebase"] = ("梳理这个项目的结构和入口", "Walk me through this project's structure and entry points"),
        ["AssistantSuggestFixBug"] = ("定位一个报错并修好它", "Find a bug and fix it"),
        ["AssistantSuggestWriteScript"] = ("写一个脚本并运行验证", "Write a script and run it to verify"),
        ["AssistantSuggestRefactor"] = ("把一段代码重构得更易读", "Refactor some code to be easier to read"),
        ["ModelChangedFormat"] = ("已切换模型：{0}", "Model changed to {0}"),
    };
}
