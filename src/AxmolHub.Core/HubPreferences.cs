namespace AxmolHub.Core;

public sealed class HubPreferences
{
    /// <summary>Cold-start language: the value used when there is no settings file, defined in a single place by <see cref="HubTexts.DefaultLanguage"/> (English).</summary>
    public string Language { get; set; } = HubTexts.DefaultLanguage;

    /// <summary>界面主题：跟随系统 / 浅色 / 深色，取值见 <see cref="HubTheme"/>。</summary>
    public string Theme { get; set; } = HubTheme.DefaultTheme;

    public string? DataRoot { get; set; }
    public string? ProjectDirectory { get; set; }

    /// <summary>
    /// Where engine release packages are downloaded from: <see cref="DownloadSources.GitHubId"/> /
    /// <see cref="DownloadSources.AtomGitId"/> / <see cref="DownloadSources.CustomId"/>.
    /// Affects **engine installs only** — it is not the engine's own dependency mirror (that lives in
    /// the engine tree, see <see cref="EngineMirror"/>).
    /// </summary>
    public string DownloadSource { get; set; } = DownloadSources.GitHubId;

    /// <summary>The URL for <see cref="DownloadSources.CustomId"/>: a <c>{version}</c> template or a bare https origin.</summary>
    public string? CustomDownloadSource { get; set; }

    /// <summary>
    /// When true, an available update is downloaded in the background as soon as a check finds it,
    /// so the only thing left for the user is a restart. **Download only** — applying (which
    /// restarts the app) stays an explicit action.
    /// </summary>
    public bool AutoDownloadUpdates { get; set; } = true;

    /// <summary>Which Hub releases to search for: stable only, or include GitHub pre-releases.</summary>
    public string UpdateChannel { get; set; } = UpdateChannels.DefaultChannel;

    /// <summary>
    /// Default permission mode for the assistant's tools: <see cref="ToolApprovalModes.Ask"/> /
    /// <see cref="ToolApprovalModes.Auto"/> / <see cref="ToolApprovalModes.Full"/>. A session can override it
    /// (<see cref="Conversation.ApprovalMode"/>), which is the only way one risky project gets tightened
    /// without tightening everything else.
    /// </summary>
    public string ToolApprovalMode { get; set; } = ToolApprovalModes.Ask;

    /// <summary>
    /// Tools the user told Hub to stop asking about, for <b>every</b> session: the app-wide half of the grant a
    /// card's 「总是允许」 writes, kept beside the default mode because both are answers to "how much do I let this
    /// assistant do without me". A session still has its own shorter list
    /// (<see cref="Conversation.AutoApprovedTools"/>); the gate reads the union. Names are the wire spelling
    /// (<c>run_command</c>), and a grant buys nothing on the tier that reaches past the sandbox — see
    /// <see cref="ToolApprovalPolicy"/>.
    /// </summary>
    public List<string> TrustedTools { get; set; } = [];

    /// <summary>左侧导航/会话侧栏展开时的宽度（px），范围 240–420；由侧栏拖拽手柄调整。</summary>
    public double SidebarWidth { get; set; } = 300;

    /// <summary>侧栏是否处于收起状态；由 ☰ 按钮或拖拽到阈值以下切换。</summary>
    public bool SidebarCollapsed { get; set; }

    /// <summary>右侧审阅面板展开时的宽度（px），范围 300–520；由面板左缘的拖拽手柄调整，使用时再夹取，
    /// 所以设置文件里一个越界的旧值只会读到被夹过的结果。</summary>
    public double InspectorWidth { get; set; } = 360;

    /// <summary>右侧审阅面板是否处于打开状态。只存"打开与否"，不存看的是哪一页——看哪一页是瞬时决定，
    /// 存下来反而会在会话没有计划时打开一个空标签。</summary>
    public bool InspectorOpen { get; set; }

    /// <summary>
    /// 会话侧栏各分组展开还是折起，键取自 <see cref="SessionGroupKey"/>（<c>ws:&lt;目录&gt;</c> / <c>workspaces</c> /
    /// <c>recent</c> / <c>archived</c>），值为<b>用户的明确选择</b>。字典而不是布尔列表，是因为默认并不统一：工作区
    /// 和「对话」默认展开，已归档默认折起，而「折起的那条我打开了」和「这条我根本没动过」必须是两回事。文案改了
    /// 键不改——<c>recent</c> 同时是用户记下的折叠偏好，换字符串就是把那份偏好变成孤儿。分组是会话派生出来的，今天有
    /// 明天可能就没了，所以认不出的键只是渲染不到，不必清理——留着也不会挡路。
    /// </summary>
    public Dictionary<string, bool> SidebarGroupExpanded { get; set; } = [];

    /// <summary>
    /// The strongest reasoning tier Hub's per-request routing (<see cref="ChatRouting.Auto"/>) may pick, whatever
    /// the task looks like. It is a ceiling and not a target: the point of the setting is that the expensive
    /// tiers are a decision a person made once on purpose, not something a classifier reaches for on its own.
    /// Unknown values fall back to <see cref="ChatReasoningEfforts.XHigh"/> — see <see cref="ModelRouting.Ceiling"/>.
    /// </summary>
    public string MaxAutoEffort { get; set; } = ChatReasoningEfforts.XHigh;

    /// <summary>
    /// Whether the assistant may start a child session of its own (<c>spawn_session</c>). <b>Off by default</b>,
    /// because each child is a model call nobody clicked and the run registry only has three slots. The reason to
    /// turn it on is context isolation — sending one reader out over a huge file and taking back a few lines of
    /// conclusion costs less than reading that file into the parent — and that reason is worth a switch rather
    /// than a silent default.
    /// </summary>
    public bool AllowSpawnedSessions { get; set; }

    /// <summary>
    /// Whether the assistant may read a web page it was pointed at (<c>web_fetch</c>). <b>On by default</b>, which
    /// is the deliberate opposite of <see cref="AllowSpawnedSessions"/>: fetching a URL the user or the model named
    /// is what every assistant of this shape does, and a switch that ships off would just mean the capability does
    /// not exist for anyone who never finds the setting. It is still a switch rather than nothing, because the call
    /// leaves this machine for a server nobody here chose.
    ///
    /// <para>This is only whether the tool <i>can</i> run at all. Whether it has to ask first is the approval
    /// tier's question (<see cref="ToolApprovalPolicy"/>), and a grant on that side does not open this one — see
    /// <see cref="WebFetch.Decide"/> for the order they are checked in.</para>
    ///
    /// <para>Read per tool call rather than once per request, the same way the spawn permission is, so turning it
    /// off in Settings takes effect on the next call instead of the next conversation.</para>
    /// </summary>
    public bool AllowOutboundWebFetch { get; set; } = true;
}

public sealed class PreferencesStore(string path)
{
    public HubPreferences Load()
    {
        var preferences = File.Exists(path) ? System.Text.Json.JsonSerializer.Deserialize<HubPreferences>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Empty Hub preferences.") : new();
        // The list of supported languages is defined in exactly one place, HubTexts: duplicating the
        // literal here would create a silent fork point where "a language was added but this spot was
        // forgotten".
        if (!HubTexts.IsSupported(preferences.Language)) preferences.Language = HubTexts.DefaultLanguage;
        // Same reason for the theme: HubTheme owns the supported values, this spot only falls back.
        preferences.Theme = HubTheme.Normalize(preferences.Theme);
        // Same for the download source: DownloadSources owns the known ids. An unknown value (hand
        // edited, or written by a newer Hub) must not become "download from nowhere".
        preferences.DownloadSource = DownloadSources.Normalize(preferences.DownloadSource);
        preferences.UpdateChannel = UpdateChannels.Normalize(preferences.UpdateChannel);
        // Same for the assistant's permission mode, and the fallback matters more here: an unrecognized value
        // has to come back as "ask", never as "let it through".
        preferences.ToolApprovalMode = ToolApprovalModes.Normalize(preferences.ToolApprovalMode);
        // The trust list is read with the same care: a hand-edited or newer-Hub file can put anything in it, and
        // the shape rules belong to ToolTrust so the store, the card and the settings page cannot disagree about
        // what a stored grant means. Unknown names are kept rather than dropped — they match no call, and a
        // reader that deleted what it did not recognise would silently eat a grant a newer Hub wrote.
        preferences.TrustedTools = ToolTrust.Normalize(preferences.TrustedTools);
        // The routing ceiling is a cost guard: a value this build cannot read falls back to the shipped ceiling
        // rather than to "no ceiling", which is the one way a hand-edited settings file could spend past the
        // strongest tier anyone agreed to.
        preferences.MaxAutoEffort = ModelRouting.Ceiling(preferences.MaxAutoEffort);
        return preferences;
    }
    public void Save(HubPreferences preferences) => StateStore.WriteJson(path, preferences);
    public static string VerifyDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        if (fullPath == Path.GetPathRoot(fullPath)) throw new IOException("Choose a dedicated Hub directory, not a drive root.");
        Directory.CreateDirectory(fullPath);
        var probe = Path.Combine(fullPath, ".hub-write-check-" + Guid.NewGuid().ToString("N"));
        try { File.WriteAllText(probe, ""); }
        finally { if (File.Exists(probe)) File.Delete(probe); }
        return fullPath;
    }
}
