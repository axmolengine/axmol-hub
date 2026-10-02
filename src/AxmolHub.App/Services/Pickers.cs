using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace AxmolHub.App;

/// <summary>文件 / 文件夹选择的结果。</summary>
public enum PickOutcome
{
    /// <summary>用户取消，或选择器没有返回任何项。</summary>
    Cancelled,

    /// <summary>选中了一个本地路径。</summary>
    Picked,

    /// <summary>
    /// 选中了一项，但它没有本地文件系统路径（云端盘、虚拟位置、内容提供程序）。
    /// Hub 的语义是"本地目录"，这类位置不能当路径用，必须显式拒绝而不是当成取消。
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
/// WPF 的 <c>OpenFileDialog</c> / <c>OpenFolderDialog</c> 在 Avalonia 里统一成
/// <see cref="IStorageProvider"/>，而且**只有异步签名**。返回值语义也变了：WPF 直接给
/// <c>string?</c>，Avalonia 给 <c>IStorageFolder</c>/<c>IStorageFile</c>，本地路径要用
/// <c>TryGetLocalPath()</c> 取回来，取不到（云端位置）时为 null。
/// 见 docs/avalonia-migration-plan.md §3.3。
/// </summary>
public static class Pickers
{
    /// <summary>
    /// 决定逻辑本身。抽成不碰 Avalonia 的纯函数，唯一理由是它**能被断言**：
    /// 真去弹一个选择器没法进自动化，而"取消 / 选中 / 选中了非本地位置"这三种结果的区分
    /// 恰恰是最容易写错、也最难在人工点几次里发现的地方。
    /// </summary>
    public static PickResult Resolve(bool itemPresent, string? localPath)
    {
        if (!itemPresent)
        {
            return PickResult.Cancelled;
        }

        return string.IsNullOrEmpty(localPath) ? PickResult.NotLocal : PickResult.Picked(localPath);
    }

    /// <summary>把选择器返回的项翻译成结果。</summary>
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
