using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using AxmolHub.Agent;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// The device-code sign-in surface: a short code to type somewhere else, and one line that says what is
/// happening while this app waits.
///
/// <para><b>Its own window, for the same reason <see cref="AuthDialog"/> has one.</b> The provider list on the
/// settings page is rebuilt from the provider and credential collections on every change, so anything living
/// inside a group is thrown away mid-flight — and a device sign-in is the longest flight in the app, since the
/// person may be unlocking a phone while it runs.</para>
///
/// <para><b>Modeless rather than modal, and closing it is the cancel.</b> A modal would freeze the whole
/// application while the user is on another device, which is the one thing this flow guarantees will happen.
/// Making the close button cancel means there is no "Cancel" control to add and no second way to say the same
/// thing — the window is the affordance.</para>
///
/// <para><b>The code is the point of the screen, so it is the biggest thing on it.</b> Monospaced because the
/// characters have to be distinguishable when read aloud or copied by eye (GitHub issues them as
/// <c>XXXX-XXXX</c>), and paired with a copy button that stays quiet until hovered, per the shell charter: the
/// person either reads four characters or copies them, and only one of those needs a control.</para>
/// </summary>
public sealed class DeviceAuthWindow : Window
{
    private readonly TextBlock _code;
    private readonly TextBlock _link;
    private readonly TextBlock _status;
    private readonly Button _copy;

    /// <summary>
    /// Builds the window for one challenge. The caller shows it — construction and display are separate so a
    /// <c>--verify-shell</c> pass can read the built state of a window it never puts on screen, the same way it
    /// reads <see cref="AuthDialog"/>.
    /// </summary>
    public DeviceAuthWindow(DeviceCodeChallenge challenge)
    {
        Title = HubStrings.Get("AuthDeviceTitle");
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _code = new TextBlock
        {
            Text = challenge.UserCode,
            FontFamily = ThemedFont("Hub.Font.Mono"),
            FontSize = 30,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };

        _link = new TextBlock
        {
            Text = challenge.VerificationUri,
            Classes = { "muted" },
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        _status = new TextBlock
        {
            Text = HubStrings.Get("AuthDeviceWaiting"),
            Classes = { "muted" },
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
        };

        _copy = new Button
        {
            Content = HubStrings.Get("AuthDeviceCopy"),
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };
        _copy.Click += async (_, _) => await CopyCodeAsync();
        // Reveal on hover, not on first paint: the code itself is what this window exists to show.
        _code.PointerEntered += (_, _) => _copy.Opacity = 1;
        _code.PointerExited += (_, _) => _copy.Opacity = 0;
        _copy.PointerEntered += (_, _) => _copy.Opacity = 1;

        var root = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = HubStrings.Get("AuthDeviceHint"),
                    FontSize = 15,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                },
                _code,
                _copy,
                _link,
                _status,
            },
        };
        Content = new Border { Child = root, Padding = new Thickness(24) };

        // The browser is already opening on this machine when the flow reports the challenge (the flow calls the
        // launcher itself); this row is the fallback for the case where it could not, and for approving on a
        // different device entirely.
        _link.PointerPressed += (_, _) => UrlLauncher.TryOpen(challenge.VerificationUri);

        // There is deliberately no CancellationTokenSource in here. A window cannot hand its own token to the
        // flow that started before it — the flow is already awaiting by the time this class is constructed — so
        // the cancel would be a token nobody listens to. The caller owns the source and subscribes to
        // <see cref="Window.Closed"/>; this class owns the pixels.
    }

    /// <summary>Rewrites the one status line. Kept to a single line on purpose: a list of poll attempts is a log,
    /// and a log is chrome this window has no room for.</summary>
    public void Report(string status) => _status.Text = status;

    /// <summary>
    /// What goes on the clipboard: the code without its separator.
    ///
    /// <para>The hyphen GitHub puts in a user code is a display aid — the person reads <c>ABCD-1234</c> off this
    /// screen and types it into a field that wants <c>ABCD1234</c>. Copying the on-screen spelling therefore hands
    /// over one character more than the credential, and a paste is exactly the case where nobody notices. The
    /// window keeps showing the code as it arrived, so it still matches the page the person is looking at beside
    /// it; only the copied text is normalized.</para>
    /// </summary>
    internal static string ClipboardText(string shown) => shown.Replace("-", "");

    private async Task CopyCodeAsync()
    {
        var text = ClipboardText(_code.Text ?? "");
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null || text.Length == 0) return;
        try
        {
            var item = new DataTransferItem();
            item.Set(DataFormat.Text, text);
            var data = new DataTransfer();
            data.Add(item);
            await clipboard.SetDataAsync(data);
            Report(HubStrings.Get("AuthDeviceCopied"));
        }
        catch (Exception)
        {
            // A clipboard that refuses (locked by another process, a headless session) is not worth an error
            // dialog: the code is on screen and selectable, which is the same fallback every copy affordance in
            // this app degrades to.
        }
    }

    private static FontFamily ThemedFont(string key)
        => Application.Current?.TryGetResource(key, null, out var value) == true && value is FontFamily family
            ? family
            : FontFamily.Default;

    // ── Self-check surface ──
    //
    // Read the LOGICAL tree and read the TEXT — the two rules AuthDialog's own notes record, learned by
    // breaking them: a window that was never shown has no visual tree, and a control that lays out without
    // painting anything still counts.

    internal string CodeForCheck => _code.Text ?? "";
    internal string LinkForCheck => _link.Text ?? "";
    internal string StatusForCheck => _status.Text ?? "";

    /// <summary>The text the copy button would put on the clipboard, read without touching a clipboard: a check
    /// that drove the real one would be asserting on whatever the session's clipboard already held.</summary>
    internal string CopyTextForCheck => ClipboardText(CodeForCheck);
    internal int LogicalChildCountForCheck => this.GetLogicalDescendants().Count();
    internal bool CopyRevealedForCheck => _copy.Opacity > 0;

    /// <summary>
    /// Every piece of text on the window, joined. The check that matters here is a negative one — the polling
    /// secret must not be on screen — and a negative assertion written against three named properties only proves
    /// those three do not hold it. Reading all the text is what makes "nothing on this window says it" a fact.
    /// </summary>
    internal string TextsForCheck => string.Join("|",
        this.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text ?? ""));

    /// <summary>
    /// Raises the close the way the window manager does, so a check can cancel a sign-in it never put on screen.
    ///
    /// <para><see cref="Window.Close"/> is not usable here: it goes through the platform, and a window that was
    /// never shown has no platform side to ask. <c>OnClosed</c> is the method the platform itself calls, so
    /// routing the check through it fires the same <see cref="Window.Closed"/> event the person closing the
    /// window fires — which is the wiring worth asserting. A seam that cancelled the token directly would prove
    /// the flow listens to a token, and the flow already does that in the Checks project.</para>
    /// </summary>
    internal void CloseForCheck() => OnClosed(EventArgs.Empty);
}
