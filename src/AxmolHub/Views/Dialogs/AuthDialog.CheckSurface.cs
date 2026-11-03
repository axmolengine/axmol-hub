using Avalonia.Controls;
using Avalonia.LogicalTree;
using AxmolHub.Core;

namespace AxmolHub;
public sealed partial class AuthDialog : Window
{
    /// <summary>Builds the dialog without showing it, for the shell's existing self-check surface.</summary>
    internal static AuthDialog ForCheck(ModelProvider provider)
        => new(provider, null);

    /// <summary>Reads the built logical tree, which remains available before the window is shown.</summary>
    internal int StepControlCountForCheck => LogicalControls.Count();
    internal string[] VisibleTextForCheck => LogicalControls.Select(control => Read(control)).Where(text => !string.IsNullOrWhiteSpace(text)).Cast<string>().ToArray();
    internal int BlankTextCountForCheck => LogicalControls.OfType<TextBlock>().Count(block => string.IsNullOrWhiteSpace(block.Text) && block.IsVisible);
    internal string[] BlankTextClassesForCheck => LogicalControls.OfType<TextBlock>().Where(block => string.IsNullOrWhiteSpace(block.Text) && block.IsVisible).Select(block => string.Join("+", block.Classes)).ToArray();
    internal string[] MethodOptionsForCheck => LogicalControls.OfType<Button>().Select(button => button.Tag as string).Where(tag => tag is not null).Cast<string>().ToArray();
    internal bool HasPasswordBoxForCheck => LogicalControls.OfType<TextBox>().Any(box => box.Classes.Contains("password"));
    internal bool HasLoginButtonForCheck => _login is not null;
    internal bool HasConnectButtonForCheck => _connect is not null;
    internal string? ActionTextForCheck => _connect?.Content as string;
    internal bool HasStatusForCheck => _status is not null;

    private IEnumerable<Control> LogicalControls => this.GetLogicalDescendants().OfType<Control>();

    private static string? Read(Control control) => control switch
    {
        TextBlock block => block.Text,
        Button { Content: string text } => text,
        TextBox box => box.Text ?? box.PlaceholderText,
        _ => null,
    };
}
