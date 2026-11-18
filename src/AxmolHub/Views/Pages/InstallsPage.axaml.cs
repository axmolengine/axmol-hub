using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AxmolHub.Core;

namespace AxmolHub;

public sealed record EngineListItem(EngineEntry Engine, bool IsDefault,
    string MirrorLabel, string MirrorTooltip, bool MirrorWarning,
    string PrebuiltLabel, string PrebuiltTooltip, bool PrebuiltWarning, bool IsCompact)
{
    public bool PrebuiltVisible => PrebuiltLabel.Length > 0;

    public bool CanSetDefault => !IsDefault;

    public string DisplayPath
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return Engine.Path;
            var relative = Path.GetRelativePath(home, Engine.Path);
            return relative == "." ? "~"
                : relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !Path.IsPathRooted(relative) ? "~" + Path.DirectorySeparatorChar + relative : Engine.Path;
        }
    }

}

public sealed record EngineFilterChoice(string Label, string Value)
{
    public override string ToString() => Label;
}

public partial class InstallsPage : UserControl
{
    private readonly HubWorkspace _workspace;
    private bool _ready;
    private bool _compact;
    private int _sortMode;

    public InstallsPage() { _workspace = null!; InitializeComponent(); }

    public InstallsPage(HubWorkspace workspace)
    {
        _workspace = workspace;
        InitializeComponent();

        _workspace.Changed += Reload;
        SearchBox.TextChanged += (_, _) =>
        {
            ClearSearchButton.IsVisible = !string.IsNullOrEmpty(SearchBox.Text);
            ApplyView();
        };
        ClearSearchButton.Click += (_, _) =>
        {
            SearchBox.Text = "";
            SearchBox.Focus();
        };
        ChannelFilter.SelectionChanged += (_, _) => ApplyView();
        SortButton.Click += (_, _) =>
        {
            _sortMode = (_sortMode + 1) % 3;
            UpdateSortCaption();
            ApplyView();
        };
        EnginesGrid.SelectionChanged += (_, _) =>
        {
            if (!_ready) return;
            if (EnginesGrid.SelectedItem is EngineListItem item)
                _workspace.SelectedEngine = item.Engine;
        };
        EnginesGrid.SizeChanged += (_, _) => SyncColumns();

        ImportButton.Click += OnImportEngine;
        InstallButton.Click += OnInstallEngine;
        Reload();
    }

    private async void OnImportEngine(object? sender, RoutedEventArgs e)
    {
        var picked = await PickFolderAsync(HubStrings.Get("Select Axmol engine root"));
        if (picked is null) return;
        var imported = await _workspace.ImportEngineAsync(picked);
        if (imported is not null) FocusEngine(imported);
    }

    private async void OnInstallEngine(object? sender, RoutedEventArgs e)
    {
        var installed = await _workspace.ChooseAndInstallEngineAsync();
        if (installed is not null) FocusEngine(installed);
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;
        var result = await Pickers.PickFolderAsync(topLevel, title);
        return result.Outcome == PickOutcome.Picked ? result.Path : null;
    }

    public void Reload()
    {
        if (_workspace is null) return;
        var state = _workspace.State;
        if (_workspace.SelectedEngine is { } selected
            && !state.Engines.Any(engine => SamePath(engine.Path, selected.Path)))
            _workspace.SelectedEngine = state.Engines.FirstOrDefault();
        else if (_workspace.SelectedEngine is null)
            _workspace.SelectedEngine = state.Engines.FirstOrDefault();

        var previousChannel = (ChannelFilter.SelectedItem as EngineFilterChoice)?.Value ?? "";
        ChannelFilter.ItemsSource = new[] { new EngineFilterChoice(HubStrings.Get("AllChannels"), "") }
            .Concat(state.Engines.Select(engine => engine.Channel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(channel => channel, StringComparer.CurrentCultureIgnoreCase)
                .Select(channel => new EngineFilterChoice(channel, channel)))
            .ToArray();
        ChannelFilter.SelectedItem = ChannelFilter.Items.Cast<EngineFilterChoice>()
            .FirstOrDefault(choice => choice.Value == previousChannel) ?? ChannelFilter.Items[0];

        ClearSearchButton.IsVisible = !string.IsNullOrEmpty(SearchBox.Text);
        UpdateSortCaption();
        ApplyView();
    }

    private void ApplyView()
    {
        if (_workspace is null) return;
        var needle = SearchBox.Text?.Trim() ?? "";
        var channel = (ChannelFilter.SelectedItem as EngineFilterChoice)?.Value ?? "";
        var items = _workspace.State.Engines
            .Where(engine => (needle.Length == 0
                    || engine.Version.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || engine.Channel.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || engine.Path.Contains(needle, StringComparison.OrdinalIgnoreCase))
                && (channel.Length == 0 || engine.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase)))
            .Select(engine =>
            {
                var mirror = MirrorStatus(engine);
                var prebuilt = GetPrebuiltStatus(engine);
                return new EngineListItem(engine,
                    SamePath(_workspace.State.DefaultEnginePath, engine.Path),
                    mirror.Label, mirror.Tooltip, mirror.Warning,
                    prebuilt.Label, prebuilt.Tooltip, prebuilt.Warning, _compact);
            })
            .ToArray();

        var ordered = _sortMode switch
        {
            1 => items.OrderBy(item => item.Engine, EngineVersionComparer.Instance),
            2 => items.OrderBy(item => item.Engine.Path, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Engine.Version, StringComparer.OrdinalIgnoreCase),
            _ => items.OrderByDescending(item => item.Engine, EngineVersionComparer.Instance),
        };
        var visible = ordered.ThenBy(item => item.Engine.Channel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Engine.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _ready = false;
        EnginesGrid.ItemsSource = visible;
        var selected = _workspace.SelectedEngine;
        EnginesGrid.SelectedItem = visible.FirstOrDefault(item => selected is not null
            && SamePath(item.Engine.Path, selected.Path));
        _ready = true;

        ResultCount.Text = string.Format(CultureInfo.CurrentCulture,
            HubStrings.Get("EngineResultCount"), visible.Length, _workspace.State.Engines.Count);
        var hasNoEngines = _workspace.State.Engines.Count == 0;
        EmptyEnginesPanel.IsVisible = visible.Length == 0;
        EmptyActions.IsVisible = hasNoEngines;
        ClearFiltersButton.IsVisible = !hasNoEngines;
        EmptyEngines.Text = HubStrings.Get(hasNoEngines ? "EmptyEngines" : "NoEnginesMatch");
    }

    private void SyncColumns()
    {
        if (EnginesGrid.Bounds.Width <= 0 || EnginesGrid.Columns.Count < 4) return;
        var compact = EnginesGrid.Bounds.Width < 840;
        if (compact == _compact) return;
        _compact = compact;
        EnginesGrid.Columns[0].Width = compact
            ? new DataGridLength(1, DataGridLengthUnitType.Star)
            : new DataGridLength(165);
        EnginesGrid.Columns[1].IsVisible = !compact;
        EnginesGrid.Columns[2].IsVisible = !compact;
        EnginesGrid.Columns[3].Width = new DataGridLength(compact ? 150 : 180);
        ApplyView();
    }

    private void UpdateSortCaption() => SortButton.Content = HubStrings.Get(_sortMode switch
    {
        1 => "SortEngineVersionAscending",
        2 => "SortEnginePath",
        _ => "SortEngineVersionDescending",
    });

    private void OnClearFilters(object? sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        if (ChannelFilter.ItemCount > 0) ChannelFilter.SelectedIndex = 0;
        SearchBox.Focus();
    }

    private void FocusEngine(EngineEntry engine)
    {
        SearchBox.Text = "";
        if (ChannelFilter.ItemCount > 0) ChannelFilter.SelectedIndex = 0;
        _workspace.SelectedEngine = engine;
        ApplyView();
    }

    private async void OnSetDefault(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is EngineEntry engine)
            await _workspace.SetDefaultEngineAsync(engine);
    }

    private async void OnEngineMenu(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not EngineEntry engine) return;
        var menu = new MenuFlyout();
        var mirror = new MenuItem { Header = HubStrings.Get("SwitchMirror"), IsEnabled = _workspace.MirrorOptionsOf(engine).Count > 0 };
        mirror.Click += async (_, _) => await SwitchMirrorAsync(engine);
        menu.Items.Add(mirror);
        var canBuildLibraries = EnginePrebuilt.BuildableTargets(engine).Count != 0;
        Add("BuildEngine", async () => await _workspace.BuildEngineAsync(engine), canBuildLibraries,
            canBuildLibraries ? null : HubStrings.Get("PrebuiltUnsupportedHost"));
        menu.Items.Add(new Separator());
        Add("OpenFolder", async () => await _workspace.OpenEngineFolderAsync(engine));
        Add("VerifyEngine", async () => await _workspace.VerifyEngineAsync(engine));
        menu.Items.Add(new Separator());
        Add("Repair", async () => await _workspace.RepairEngineAsync(engine));
        var uninstall = Add("Uninstall", async () => await _workspace.UninstallEngineAsync(engine));
        uninstall.Classes.Add("danger");
        var remove = Add("RemoveFromList", async () => await _workspace.RemoveEngineAsync(engine));
        remove.Classes.Add("danger");
        menu.ShowAt((Control)sender);

        MenuItem Add(string key, Func<Task> action, bool enabled = true, string? tip = null)
        {
            var item = new MenuItem { Header = HubStrings.Get(key), IsEnabled = enabled };
            if (tip is not null) ToolTip.SetTip(item, tip);
            item.Click += async (_, _) => await action();
            menu.Items.Add(item);
            return item;
        }
    }

    private async Task SwitchMirrorAsync(EngineEntry engine)
    {
        var options = _workspace.MirrorOptionsOf(engine);
        if (options.Count == 0)
        {
            await HubDialog.ShowAsync(TopLevel.GetTopLevel(this) as Window,
                HubStrings.Get("OperationFailed"), HubStrings.Get("MirrorUnknown"));
            return;
        }

        var picked = await EngineMirrorDialog.PickAsync(TopLevel.GetTopLevel(this) as Window, engine, options);
        if (picked is not null) await _workspace.ApplyEngineMirrorAsync(picked.Id, engine);
    }

    private (string Label, string Tooltip, bool Warning) MirrorStatus(EngineEntry engine)
    {
        var options = _workspace.MirrorOptionsOf(engine);
        var mirror = _workspace.MirrorOf(engine);
        if (options.Count == 0)
            return (HubStrings.Get("MirrorUnknownLabel"), HubStrings.Get("MirrorUnknown"), true);

        var labelKey = mirror switch
        {
            EngineMirror.OriginId or EngineMirror.GithubId => "MirrorLabelOfficial",
            EngineMirror.AtomGitId => "MirrorLabelAtomGit",
            EngineMirror.GiteeId => "MirrorLabelGitee",
            EngineMirror.TencentId => "MirrorLabelTencent",
            _ => "",
        };

        if (labelKey.Length == 0)
        {
            var unknown = string.Format(CultureInfo.CurrentCulture,
                HubStrings.Get("MirrorDeclaredUnknownFormat"), mirror);
            return (mirror, unknown, true);
        }

        var option = options.FirstOrDefault(candidate => candidate.Id.Equals(mirror, StringComparison.OrdinalIgnoreCase));
        var description = option is null
            ? HubStrings.Get(mirror == EngineMirror.OriginId ? "MirrorOrigin" : "MirrorGithub")
            : HubStrings.Get(option.TextKey);
        if (option?.NoteKey.Length > 0)
            description += HubStrings.Get(option.NoteKey);
        if (mirror is EngineMirror.AtomGitId or EngineMirror.GiteeId or EngineMirror.TencentId)
            description += "\n" + HubStrings.Get("MirrorFallbackNote");

        return (HubStrings.Get(labelKey), description, false);
    }

    private (string Label, string Tooltip, bool Warning) GetPrebuiltStatus(EngineEntry engine)
    {
        var statuses = (from target in EnginePrebuilt.BuildableTargets(engine)
                        from configuration in BuildConfigurations.All
                        select (target, configuration, status: _workspace.InspectPrebuilt(engine, target, configuration))).ToArray();
        if (statuses.Length == 0) return ("", "", false);

        var ready = statuses.Count(row => row.status.Usable);
        var warning = statuses.Any(row => row.status.Status
            is PrebuiltStatus.EngineChanged or PrebuiltStatus.TargetMismatch or PrebuiltStatus.MissingCache or PrebuiltStatus.MissingContents);
        var summary = ready == 0 && !warning
            ? HubStrings.Get("PrebuiltLabelNotBuilt")
            : string.Format(HubStrings.Get("PrebuiltReadyCount"), ready, statuses.Length);
        var details = statuses.Select(row => $"{row.target.Name} · {row.configuration}: {HubStrings.Get(PrebuiltLabelKey(row.status.Status))}"
            + " — " + (row.status.Usable ? row.status.RelativeDirectory : HubStrings.Get(row.status.TextKey)));
        return ($"{HubStrings.Get("PrebuiltStatus")} · {summary}", string.Join("\n", details), warning);
    }

    private static string PrebuiltLabelKey(PrebuiltStatus status) => status switch
    {
        PrebuiltStatus.Ready => "PrebuiltLabelReady",
        PrebuiltStatus.EngineChanged or PrebuiltStatus.TargetMismatch => "PrebuiltLabelRebuild",
        PrebuiltStatus.MissingCache or PrebuiltStatus.MissingContents => "PrebuiltLabelIncomplete",
        _ => "PrebuiltLabelNotBuilt",
    };

    private static bool SamePath(string? left, string? right)
    {
        if (left is null || right is null) return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), comparison);
    }

    private sealed class EngineVersionComparer : IComparer<EngineEntry>
    {
        public static EngineVersionComparer Instance { get; } = new();

        public int Compare(EngineEntry? left, EngineEntry? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var leftVersion = ComparableVersion(left.Version);
            var rightVersion = ComparableVersion(right.Version);
            var result = leftVersion.CompareTo(rightVersion);
            return result != 0 ? result : StringComparer.OrdinalIgnoreCase.Compare(left.Version, right.Version);
        }

        private static Version ComparableVersion(string value)
        {
            var end = value.IndexOfAny(['-', '+']);
            var numeric = end < 0 ? value : value[..end];
            return Version.TryParse(numeric, out var parsed) ? parsed : new Version(0, 0);
        }
    }
}
