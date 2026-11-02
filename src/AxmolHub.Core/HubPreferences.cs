namespace AxmolHub.Core;

public sealed class HubPreferences
{
    /// <summary>Cold-start language: the value used when there is no settings file, defined in a single place by <see cref="HubTexts.DefaultLanguage"/> (English).</summary>
    public string Language { get; set; } = HubTexts.DefaultLanguage;

    /// <summary>UI theme: follow system / light / dark; see <see cref="HubTheme"/> for the values.</summary>
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
    /// card's "Always allow" writes, kept beside the default mode because both are answers to "how much do I let this
    /// assistant do without me". A session still has its own shorter list
    /// (<see cref="Conversation.AutoApprovedTools"/>); the gate reads the union. Names are the wire spelling
    /// (<c>run_command</c>), and a grant buys nothing on the tier that reaches past the sandbox — see
    /// <see cref="ToolApprovalPolicy"/>.
    /// </summary>
    public List<string> TrustedTools { get; set; } = [];

    /// <summary>
    /// Git write verbs the user told Hub to stop asking about, for <b>every</b> session: what the card's verb
    /// button ("以后都不问 git push") writes. Kept beside <see cref="TrustedTools"/> because both are answers to the
    /// same question, and <b>app-level only on purpose</b> — the tool list has a per-session copy because a grant
    /// there is a batch decision, while a verb grant is one git verb and a second, narrower list would only be a
    /// second place for the two to disagree.
    ///
    /// <para>The entries are the two-word spelling the card showed (<c>git push</c>), matched Ordinal by
    /// <see cref="CommandTrust"/>. This is not a command-text blacklist: the key comes from the same tokenizer that
    /// grades the command, so an entry can only ever name a verb the classifier itself found — see
    /// <see cref="GitCommandGrades.WriteVerbsOf"/>.</para>
    /// </summary>
    public List<string> TrustedCommands { get; set; } = [];

    /// <summary>Commit title prefixes accepted by the assistant's local commit-summary parser.</summary>
    public List<string> CommitSummaryPrefixes { get; set; } = [.. CommitSummary.DefaultPrefixes];

    /// <summary>Executable identities permanently authorized for Computer Use.</summary>
    [System.Text.Json.Serialization.JsonInclude]
    [System.Text.Json.Serialization.JsonPropertyName("trustedComputerApplications")]
    internal List<ComputerApplication> TrustedComputerApps { get; set; } = [];

    /// <summary>Expanded width of the left navigation/session sidebar in px, range 240–420; adjusted by the sidebar's drag handle.</summary>
    public double SidebarWidth { get; set; } = 300;

    /// <summary>Whether the sidebar is collapsed; toggled by the ☰ button or by dragging it below the threshold.</summary>
    public bool SidebarCollapsed { get; set; }

    /// <summary>Expanded width of the right-hand review panel in px, range 300–520; adjusted by the drag handle on the panel's left edge and
    /// clamped again when it is read, so an out-of-range old value in the settings file only ever yields a clamped result.</summary>
    public double InspectorWidth { get; set; } = 360;

    /// <summary>Whether the right-hand review panel is open. Only "open or not" is stored, never which tab is showing — which tab is a momentary
    /// decision, and persisting it would open an empty tab for a session that has no plan.</summary>
    public bool InspectorOpen { get; set; }

    /// <summary>
    /// Whether each group in the session sidebar is expanded or collapsed. Keys come from <see cref="SessionGroupKey"/> (<c>ws:&lt;directory&gt;</c> / <c>workspaces</c> /
    /// <c>recent</c> / <c>archived</c>); the value is <b>the user's explicit choice</b>. A dictionary rather than a list of booleans because the
    /// defaults are not uniform: workspaces and "Chats" start expanded, "Archived" starts collapsed, and "I opened this collapsed one" has to stay
    /// distinguishable from "I never touched this one". The copy changes but the keys do not — <c>recent</c> doubles as the stored collapse
    /// preference, so renaming the string would orphan that preference. A group can now outlive its sessions (the workspace registry), but it can
    /// still leave — removed, or re-pathed away — so the same rule holds: an unrecognised key simply never renders, no cleanup is needed, and
    /// leaving it costs nothing. What it does buy is a fold that survives an empty stretch, which is the one time a group's state is all there is.
    /// </summary>
    public Dictionary<string, bool> SidebarGroupExpanded { get; set; } = [];

    /// <summary>
    /// The strongest reasoning tier Hub's per-request routing (<see cref="ChatRouting.Auto"/>) may pick, whatever
    /// the task looks like. It is a ceiling and not a target: the point of the setting is that the expensive
    /// tiers are a decision a person made once on purpose, not something a classifier reaches for on its own.
    /// Unknown values fall back to <see cref="ChatReasoningEfforts.XHigh"/> — see <see cref="ModelRouting.Ceiling"/>.
    /// </summary>
    public string MaxAutoEffort { get; set; } = ChatReasoningEfforts.XHigh;

    /// <summary>Automatic goal runs. Zero removes only this ceiling, not token or progress guards.</summary>
    public int GoalContinuationLimit { get; set; } = 0;

    /// <summary>Which layout this file was written in. Bumped when a stored default has to be told apart from a
    /// decision: <see cref="Load"/> migrates anything older and writes the file back once.</summary>
    public int SettingsVersion { get; set; }

    /// <summary>The current layout. Version 1 turned <see cref="GoalTokenBudget"/> from "a million tokens" into
    /// "no ceiling"; version 2 raised <see cref="ProviderMaxRetries"/> from the two it shipped with. Both numbers
    /// were written into every file by the old defaults, so a stored value equal to one of them is a retired
    /// default rather than a decision — see the migration in <see cref="Load"/>.</summary>
    public const int CurrentSettingsVersion = 2;

    /// <summary>The value <see cref="GoalTokenBudget"/> carried as a shipped default before version 1. Recognised on
    /// load because the field was serialized into every file, so almost every install has it written down whether
    /// or not anybody chose it.</summary>
    public const long RetiredGoalTokenBudget = 1_000_000;

    /// <summary>The retry count <see cref="ProviderMaxRetries"/> shipped with before version 2 raised the default to
    /// <see cref="ProviderRetryDefaults.MaxRetries"/>. Recognised on load for the same reason as the number above:
    /// it was serialized into every file, so a stored two says nothing about what anybody wanted.</summary>
    public const int RetiredProviderMaxRetries = 2;

    /// <summary>Ceiling on the tokens a goal may spend in total. <b>Off by default</b> — zero means no ceiling.
    ///
    /// <para>A goal exists so an unstable provider cannot end a task: the run backs off through a 429 or a dropped
    /// stream and carries on, which is the whole reason the feature is here. A token sum is a different job — it
    /// bounds spending — and the two do not belong in one number that Hub picks without being asked. It also
    /// counted the whole transcript again on every round, so on a large window a single request is hundreds of
    /// thousands of tokens and this ceiling would have stopped a goal after two or three of them.</para>
    ///
    /// <para>A runaway is caught where it happens instead: <see cref="GoalNoProgressLimit"/> pauses a goal after
    /// three consecutive answers without progress, which is tighter than any token sum and blind to how large the
    /// conversation is. A person who wants a spend ceiling can still set one — <c>Conversation.GoalTokenBudget</c>
    /// wins over this — and reaching it is reported as a stop, never as completion.</para></summary>
    public long GoalTokenBudget { get; set; }
    public int ProviderMaxRetries { get; set; } = ProviderRetryDefaults.MaxRetries;
    public int GoalNoProgressLimit { get; set; } = 3;
    public int GoalCooldownLimit { get; set; } = 2;

    /// <summary>
    /// Whether the assistant may start a child session of its own (<c>spawn_session</c>). <b>Off by default</b>,
    /// because each child is a model call nobody clicked and the run registry only has three slots. The reason to
    /// turn it on is context isolation — sending one reader out over a huge file and taking back a few lines of
    /// conclusion costs less than reading that file into the parent — and that reason is worth a switch rather
    /// than a silent default.
    /// </summary>
    public bool AllowSpawnedSessions { get; set; }

    /// <summary>Whether the assistant may ask the user to resolve a choice, shared across all conversations.</summary>
    public bool AllowAssistantQuestions { get; set; } = true;

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

    /// <summary>
    /// Legacy storage for the global web-search switch. Kept to read existing preference files; Settings keeps
    /// this and <see cref="AllowDeepSeekWebApi"/> synchronized.
    ///
    /// <para><b>On by default.</b> This started as an opt-in, on the argument that search is the one capability
    /// with neither an approval card nor a run cap — the endpoint searches mid-reply and Hub cannot ask or stop
    /// it. The owner turned the default around on 2026-10-21: a tool nobody is told about is a tool nobody gets
    /// value from, and the switch still exists for the person who does not want the spend. The cost is stated in
    /// the setting's own hint rather than left to be discovered, and the DeepSeek route reports its call count
    /// and estimate in the session that spent it. A stored file keeps whatever it says, so this only decides
    /// what a fresh install gets.</para>
    /// </summary>
    public bool AllowHostedWebSearch { get; set; } = true;

    /// <summary>Legacy storage for the global web-search switch, kept for existing preference files. Same
    /// default and the same reasoning as <see cref="AllowHostedWebSearch"/>.</summary>
    public bool AllowDeepSeekWebApi { get; set; } = true;

    /// <summary>
    /// Whether a model's own thinking is put on screen. <b>On by default</b>, and deliberately in the same
    /// direction as <see cref="AllowOutboundWebFetch"/>: a reasoning model's chain of thought ships as part of
    /// the answer, every client people measure against shows it, and a switch that arrived off would mean most
    /// people never learn they have thinking at all. It is a switch rather than nothing because the text is long,
    /// it is not what anybody asked to read, and on a metered gateway it is a screenful of tokens standing
    /// between a person and the result they wanted.
    ///
    /// <para>Display only, read per turn as the transcript paints. The thinking is still stored, still priced
    /// against the context budget and still replayed to the gateway whatever this says — a tool session that
    /// withholds it is rejected outright. Turning the switch off hides the words, not the cost.</para>
    /// </summary>
    public bool ShowThinking { get; set; } = true;
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
        var removedRetiredTools = preferences.TrustedTools.RemoveAll(LegacyFileToolNames.Contains) > 0;
        // And the command-verb list with the same care and the same reason: the shape rules live in CommandTrust so
        // the store, the card's verb button and the settings page cannot disagree about what a stored verb means.
        preferences.TrustedCommands = CommandTrust.Normalize(preferences.TrustedCommands);
        var commitPrefixes = CommitSummary.NormalizePrefixes(preferences.CommitSummaryPrefixes);
        // Upgrade only the former shipped default; a user's custom prefix list remains authoritative.
        if (commitPrefixes.SequenceEqual(["feat:", "fix:", "docs:", "misc:"], StringComparer.OrdinalIgnoreCase))
            commitPrefixes = CommitSummary.DefaultPrefixes;
        preferences.CommitSummaryPrefixes = [.. commitPrefixes];
        preferences.TrustedComputerApps = ComputerUseGrants.Normalize(preferences.TrustedComputerApps);
        // The routing ceiling is a cost guard: a value this build cannot read falls back to the shipped ceiling
        // rather than to "no ceiling", which is the one way a hand-edited settings file could spend past the
        // strongest tier anyone agreed to.
        preferences.MaxAutoEffort = ModelRouting.Ceiling(preferences.MaxAutoEffort);
        preferences.GoalContinuationLimit = Math.Max(0, preferences.GoalContinuationLimit);
        // Version 1: the goal token budget stopped having a default. Until now every file carried 1 000 000 because
        // the field was serialized with the shipped default, so a stored 1 000 000 is that retired default rather
        // than a decision somebody made — and changing the default alone would never reach an existing install,
        // because the file wins for the life of the install. Anything that differs from the old number is a
        // deliberate ceiling and is left exactly as written.
        var upgraded = false;
        if (preferences.SettingsVersion < 1)
        {
            if (preferences.GoalTokenBudget == HubPreferences.RetiredGoalTokenBudget) preferences.GoalTokenBudget = 0;
            preferences.SettingsVersion = 1;
            upgraded = true;
        }
        // Version 2: the retry count used to ship as two and now ships as five. A stored two is that retired
        // default, not a decision; any other number is left alone, including zero, which means "do not retry".
        if (preferences.SettingsVersion < 2)
        {
            if (preferences.ProviderMaxRetries == HubPreferences.RetiredProviderMaxRetries)
                preferences.ProviderMaxRetries = ProviderRetryDefaults.MaxRetries;
            preferences.SettingsVersion = 2;
            upgraded = true;
        }
        preferences.GoalTokenBudget = Math.Max(0, preferences.GoalTokenBudget);
        preferences.ProviderMaxRetries = Math.Clamp(preferences.ProviderMaxRetries, 0, ProviderRetryDefaults.MaxRetriesLimit);
        preferences.GoalNoProgressLimit = Math.Clamp(preferences.GoalNoProgressLimit, 2, 20);
        preferences.GoalCooldownLimit = Math.Clamp(preferences.GoalCooldownLimit, 0, 5);
        if (removedRetiredTools || upgraded) Save(preferences);
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
