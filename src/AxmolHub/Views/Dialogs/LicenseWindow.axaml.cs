using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using AvaloniaEdit.Editing;
using System.Text;

namespace AxmolHub;

/// <summary>Displays the English third-party package licenses and upstream notices shipped with Axmol Hub.</summary>
public partial class LicenseWindow : Window
{
    public LicenseWindow()
    {
        InitializeComponent();
        ChromeHost.Content = new HubDialogChrome(this, "CloseWindow", Close).Header;
        LicenseText.Options.EnableHyperlinks = false;
        LicenseText.Options.EnableEmailHyperlinks = false;
        LicenseText.Options.AcceptsTab = false;
        LicenseText.TextArea.Bind(TextArea.SelectionBrushProperty, new DynamicResourceExtension("Hub.Selection"));
        LicenseText.TextArea.Bind(TextArea.SelectionForegroundProperty, new DynamicResourceExtension("Hub.TextPrimary"));
        LicenseText.Text = LoadLicenseText();
        LicenseCloseButton.Click += (_, _) => Close();
    }

    internal static Task ShowAsync(Window owner)
    {
        var dialog = new LicenseWindow();
        if (owner is { IsVisible: true })
        {
            return dialog.ShowDialog(owner);
        }

        dialog.Show();
        return Task.CompletedTask;
    }

    private static string LoadLicenseText()
    {
        var licensesDirectory = Path.Combine(AppContext.BaseDirectory, "licenses");
        var content = new StringBuilder();
        AppendFile(content, licensesDirectory, "PackageLicenses.txt", null);
        AppendFile(content, licensesDirectory, "Anthropic.txt", "Anthropic — Apache-2.0 license text");
        AppendFile(content, licensesDirectory, "ANGLE.txt", "ANGLE — BSD-3-Clause license text");
        AppendFile(content, licensesDirectory, "Velopack.txt", "Velopack — MIT license text");
        AppendFile(content, licensesDirectory, "SkiaSharp-THIRD-PARTY-NOTICES.txt",
            "SkiaSharp and HarfBuzzSharp native assets — bundled third-party notices and license texts");
        AppendFile(content, licensesDirectory, "Microsoft-THIRD-PARTY-NOTICES.txt",
            "Microsoft.Extensions and System.* packages — bundled third-party notices and license texts");
        return content.ToString();
    }

    private static void AppendFile(StringBuilder content, string directory, string fileName, string? heading)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Required third-party license file is missing: {fileName}", path);
        }

        if (heading is not null)
        {
            content.AppendLine().AppendLine().Append("• ").AppendLine(heading).AppendLine();
        }

        content.Append(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
    }
}
