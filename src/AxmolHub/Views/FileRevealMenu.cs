using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.MarkupExtensions;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>A live target resolver keeps a menu from revealing a stale or no longer authorized path.</summary>
internal static class FileRevealMenu
{
    internal static void Add(ContextMenu menu, Window? owner, Func<string?> pathFor)
    {
        var item = Create(() => owner, pathFor);
        menu.Opened += (_, _) => Refresh(item, pathFor());
        menu.Items.Add(item);
    }

    internal static MenuItem Create(Func<Window?> ownerFor, Func<string?> pathFor, bool directory = false, Action? beforeReveal = null)
    {
        var item = new MenuItem { Tag = "reveal" };
        item.Bind(HeaderedSelectingItemsControl.HeaderProperty, new DynamicResourceExtension(FileManagerReveal.LabelKey));
        Refresh(item, pathFor(), directory);
        item.Click += async (_, _) =>
        {
            beforeReveal?.Invoke();
            if (pathFor() is not { } path
                || !(directory ? Directory.Exists(path) : File.Exists(path))
                || !await FileManagerReveal.TryRevealAsync(path))
                await HubDialog.ShowAsync(ownerFor(), HubStrings.Get(FileManagerReveal.LabelKey),
                    HubStrings.Get(directory ? "RevealDirectoryFailed" : "RevealFileFailed"));
        };
        return item;
    }

    internal static void Refresh(MenuItem item, string? path, bool directory = false)
    {
        item.IsEnabled = path is not null && (directory ? Directory.Exists(path) : File.Exists(path));
        ToolTip.SetTip(item, path);
    }
}
