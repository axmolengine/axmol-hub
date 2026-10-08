using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace AxmolHub;

/// <summary>Hub-token table colors, layered over the Markdown theme. Instantiated per viewer: a
/// <see cref="Styles"/> collection keeps a single owner.</summary>
public sealed class HubMarkdownStyles : Styles
{
    public HubMarkdownStyles()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
