using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace AxmolHub.App;

/// <summary>The result of a file / folder picker.</summary>
public enum PickOutcome
{
    /// <summary>The user cancelled, or the picker returned nothing.</summary>
    Cancelled,

    /// <summary>A local path was picked.</summary>
    Picked,

    /// <summary>
    /// An item was picked, but it has no local filesystem path (cloud drive, virtual location,
    /// content provider). Hub's semantics are "local directory", so such a location can't be used as
    /// a path and must be rejected explicitly rather than treated as a cancel.
    /// </summary>
    NotLocal,
}

public readonly record struct PickResult(PickOutcome Outcome, string? Path)
{
    public static PickResult Cancelled { get; } = new(PickOutcome.Cancelled, null);

    public static PickResult NotLocal { get; } = new(PickOutcome.NotLocal, null);

    public static PickResult Picked(string path) => new(PickOutcome.Picked, path);
}

/// <summary>
/// WPF's <c>OpenFileDialog</c> / <c>OpenFolderDialog</c> are unified into
/// <see cref="IStorageProvider"/> in Avalonia, and **only async signatures exist**. The return
/// semantics changed too: WPF gives <c>string?</c> directly, Avalonia gives
/// <c>IStorageFolder</c>/<c>IStorageFile</c>; the local path must be recovered via
/// <c>TryGetLocalPath()</c>, which is null when it can't be recovered (cloud location).
/// See docs/avalonia-migration-plan.md §3.3.
/// </summary>
public static class Pickers
{
    /// <summary>
    /// The decision logic itself. Extracted into a pure function that doesn't touch Avalonia, for
    /// the sole reason that it **can be asserted**: actually popping a picker can't be automated,
    /// and distinguishing "cancelled / picked / picked a non-local location" is exactly the kind of
    /// thing that's easy to get wrong and hard to spot in a few manual clicks.
    /// </summary>
    public static PickResult Resolve(bool itemPresent, string? localPath)
    {
        if (!itemPresent)
        {
            return PickResult.Cancelled;
        }

        return string.IsNullOrEmpty(localPath) ? PickResult.NotLocal : PickResult.Picked(localPath);
    }

    /// <summary>Translates the picker's returned item into a result.</summary>
    public static PickResult Translate(IStorageItem? item)
        => Resolve(item is not null, item?.TryGetLocalPath());

    public static async Task<PickResult> PickFolderAsync(TopLevel owner, string title)
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });

        return Translate(folders.Count > 0 ? folders[0] : null);
    }

    public static async Task<PickResult> PickFileAsync(
        TopLevel owner,
        string title,
        IReadOnlyList<FilePickerFileType>? types = null)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = types,
        });

        return Translate(files.Count > 0 ? files[0] : null);
    }

    public static async Task<PickResult> SaveFileAsync(
        TopLevel owner,
        string title,
        string suggestedName,
        IReadOnlyList<FilePickerFileType>? types = null)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = types,
        });

        return Translate(file);
    }
}
