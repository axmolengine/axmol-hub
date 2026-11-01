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
        var item = new MenuItem { Tag = "reveal" };
        item.Bind(HeaderedSelectingItemsControl.HeaderProperty, new DynamicResourceExtension(FileManagerReveal.LabelKey));
        menu.Opened += (_, _) =>
        {
            var path = pathFor();
            item.IsEnabled = path is not null && File.Exists(path);
            ToolTip.SetTip(item, path);
        };
        item.Click += async (_, _) =>
        {
            if (pathFor() is not { } path || !await FileManagerReveal.TryRevealAsync(path))
                await HubDialog.ShowAsync(owner, HubStrings.Get(FileManagerReveal.LabelKey), HubStrings.Get("RevealFileFailed"));
        };
        menu.Items.Add(item);
    }
}
