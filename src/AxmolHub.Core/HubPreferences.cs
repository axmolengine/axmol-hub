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

    /// <summary>左侧导航/会话侧栏展开时的宽度（px），范围 240–420；由侧栏拖拽手柄调整。</summary>
    public double SidebarWidth { get; set; } = 300;

    /// <summary>侧栏是否处于收起状态；由 ☰ 按钮或拖拽到阈值以下切换。</summary>
    public bool SidebarCollapsed { get; set; }

    /// <summary>
    /// 会话侧栏各分组展开还是折起，键取自 <see cref="SessionGroupKey"/>（<c>ws:&lt;目录&gt;</c> / <c>recent</c> /
    /// <c>archived</c>），值为<b>用户的明确选择</b>。字典而不是布尔列表，是因为默认并不统一：工作区和最近聊天默认展开，
    /// 已归档默认折起，而「折起的那条我打开了」和「这条我根本没动过」必须是两回事。分组是会话派生出来的，今天有
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
