using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// The shell's two right-hand surfaces: the inspector column (the plan in full, the diffs of what the assistant
/// changed) and the window-level picture viewer. Split out of <c>MainWindow.axaml.cs</c> because both are
/// self-contained interactions with their own state, and the shell file's job is navigation and workspace
/// plumbing — neither of these is that.
/// </summary>
public partial class MainWindow
{
    // ───────────────────────── Inspector column ─────────────────────────

    /// <summary>Inspector width bounds. The floor is what keeps a diff's two columns legible; the ceiling is
    /// what keeps the chat column from being squeezed past its own floor on a narrow window.</summary>
    private const double InspectorMin = 300;
    private const double InspectorMax = 520;
    private const double InspectorDefault = 360;

    /// <summary>Drag state for the inspector grip, mirroring the sidebar's: pointer x and width at grab.</summary>
    private double _inspectorGrabStartX;
    private double _inspectorGrabStartWidth;
    private bool _inspectorGrabbing;

    /// <summary>Whether the inspector is open. Separate from the column's <c>IsVisible</c>, which is also gated
    /// on the current page — the inspector belongs to the assistant, and follows it off-screen.</summary>
    private bool _inspectorOpen;

    /// <summary>Wires the inspector grip and applies the persisted width/open state. Called once from the
    /// constructor after <c>InitializeComponent</c>, beside <see cref="InitializeSidebar"/>.</summary>
    private void InitializeInspector()
    {
        InspectorResizeGrip.PointerPressed += OnInspectorGripPressed;
        InspectorResizeGrip.PointerMoved += OnInspectorGripMoved;
        InspectorResizeGrip.PointerReleased += OnInspectorGripReleased;

        Inspector.Width = InspectorClamp(_preferences.InspectorWidth > 0
            ? _preferences.InspectorWidth
            : InspectorDefault);
        _inspectorOpen = _preferences.InspectorOpen;
        SetInspectorColumnVisible();
    }

    private static double InspectorClamp(double width) => Math.Clamp(width, InspectorMin, InspectorMax);

    /// <summary>Opens the inspector on a tab ("plan" or "changes") and returns true so the caller knows the
    /// surface it asked for is now up. The content is hosted in <see cref="InspectorHost"/>; a conversation
    /// with nothing to show still opens, because "there is no plan yet" is itself an answer.</summary>
    internal bool OpenInspector(string tab)
    {
        _inspectorOpen = true;
        InspectorHost.Content = _chatPanel?.BuildInspectorContent(tab);
        SetInspectorColumnVisible();
        if (_preferences.InspectorOpen != true)
        {
            _preferences.InspectorOpen = true;
            _preferencesStore.Save(_preferences);
        }
        return true;
    }

    internal void CloseInspector()
    {
        _inspectorOpen = false;
        SetInspectorColumnVisible();
        if (_preferences.InspectorOpen)
        {
            _preferences.InspectorOpen = false;
            _preferencesStore.Save(_preferences);
        }
    }

    /// <summary>Applies the two gates at once: the inspector shows only on the assistant page and only while
    /// open. The grip rides with the column, so there is never a floating handle beside a hidden pane.</summary>
    private void SetInspectorColumnVisible()
    {
        var show = _inspectorOpen && _currentKey == "Assistant";
        InspectorColumn.IsVisible = show;
        InspectorResizeGrip.IsVisible = show;
    }

    /// <summary>Called from <see cref="NavigateTo"/>: the inspector follows the assistant page on and off.</summary>
    private void SyncInspectorForPage() => SetInspectorColumnVisible();

    private void OnInspectorGripPressed(object? sender, PointerPressedEventArgs e)
    {
        _inspectorGrabbing = true;
        _inspectorGrabStartX = e.GetPosition(this).X;
        _inspectorGrabStartWidth = Inspector.Width;
        e.Pointer.Capture(InspectorResizeGrip);
        e.Handled = true;
    }

    private void OnInspectorGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_inspectorGrabbing) return;
        // The grip is on the right, so dragging left grows the pane: the delta is inverted against the sidebar's.
        var width = _inspectorGrabStartWidth - (e.GetPosition(this).X - _inspectorGrabStartX);
        Inspector.Width = InspectorClamp(width);
    }

    private void OnInspectorGripReleased(object? sender, PointerEventArgs e)
    {
        if (!_inspectorGrabbing) return;
        _inspectorGrabbing = false;
        e.Pointer.Capture(null);
        _preferences.InspectorWidth = InspectorClamp(Inspector.Width);
        _preferencesStore.Save(_preferences);
    }

    internal bool InspectorVisibleForCheck => InspectorColumn.IsVisible;
    internal double InspectorWidthForCheck => Inspector.Width;

    // ───────────────────────── Picture viewer ─────────────────────────

    /// <summary>The set the viewer pages through and where it stands in it. Empty when shut; the loader on each
    /// entry stays unread until the viewer lands on that entry (see <see cref="PictureRef"/>).</summary>
    private IReadOnlyList<PictureRef> _viewerSet = Array.Empty<PictureRef>();
    private int _viewerIndex;
    private Bitmap? _viewerBitmap;

    /// <summary>Builds the caption-row buttons once. They never change, so rebuilding them per open would only
    /// churn the object graph a check counts.</summary>
    private void InitializePictureViewer()
    {
        PicturePrevButton.Click += (_, _) => StepPictureViewer(-1);
        PictureNextButton.Click += (_, _) => StepPictureViewer(+1);
        PictureViewerScrim.PointerPressed += (_, e) => { ClosePictureViewer(); e.Handled = true; };

        PictureViewerBar.Children.Add(ViewerActionButton(
            "PictureViewerCopy", "Hub.Icon.Copy", CopyViewerPicture));
        PictureViewerBar.Children.Add(ViewerActionButton(
            "PictureViewerClose", "Hub.Icon.Close", ClosePictureViewer));

        // Window-level keys, on the tunnel so a focused composer or transcript cannot eat them first: while the
        // viewer is up it owns ←/→/Esc, and only while it is up.
        AddHandler(KeyDownEvent, OnViewerKeyDown, RoutingStrategies.Tunnel);
    }

    private static Button ViewerActionButton(string textKey, string geometryKey, Action onClick)
    {
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            Fill = Brushes.Transparent,
            StrokeThickness = 1.7,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        };
        icon.Bind(Avalonia.Controls.Shapes.Path.DataProperty, new DynamicResourceExtension(geometryKey));
        icon.Bind(Avalonia.Controls.Shapes.Path.StrokeProperty, new DynamicResourceExtension("Hub.TextSecondary"));

        var button = new Button { Content = icon, Tag = textKey };
        button.Classes.Add("viewer-action");
        ToolTip.SetTip(button, HubStrings.Get(textKey));
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>Opens the viewer on one picture of a set. A set with nothing in it opens nothing: an empty frame
    /// would read as "the picture is gone", which is a different claim from the one the caller is making.</summary>
    internal void OpenPictureViewer(IReadOnlyList<PictureRef> set, int index)
    {
        if (set is null || set.Count == 0) return;
        _viewerSet = set;
        PictureViewerHost.IsVisible = true;
        ShowPictureAt(index);
    }

    /// <summary>Lands the viewer on one entry: decodes it, captions it, and arms the paging buttons. Wraps, so
    /// the last picture's "next" is the first — a viewer you can page out of is a viewer you have to close.</summary>
    private void ShowPictureAt(int index)
    {
        var count = _viewerSet.Count;
        if (count == 0) { ClosePictureViewer(); return; }
        _viewerIndex = ((index % count) + count) % count;

        var reference = _viewerSet[_viewerIndex];
        var bytes = SafeLoad(reference);
        _viewerBitmap?.Dispose();
        _viewerBitmap = Decode(bytes);
        PictureViewerImage.Source = _viewerBitmap;

        var position = string.Format(
            HubStrings.Get("PictureViewerPositionFormat"), _viewerIndex + 1, count);
        PictureViewerCaption.Text = _viewerBitmap is null
            ? $"{reference.Caption} · {HubStrings.Get("PictureViewerMissing")}"
            : $"{reference.Caption} · {position}";

        // Paging is meaningless with one picture; hiding the arrows says so without a disabled-looking control.
        var many = count > 1;
        PicturePrevButton.IsVisible = many;
        PictureNextButton.IsVisible = many;
    }

    private void StepPictureViewer(int delta) => ShowPictureAt(_viewerIndex + delta);

    internal void ClosePictureViewer()
    {
        PictureViewerHost.IsVisible = false;
        PictureViewerImage.Source = null;
        _viewerBitmap?.Dispose();
        _viewerBitmap = null;
        _viewerSet = Array.Empty<PictureRef>();
        _viewerIndex = 0;
    }

    private void OnViewerKeyDown(object? sender, KeyEventArgs e)
    {
        if (!PictureViewerHost.IsVisible) return;
        switch (e.Key)
        {
            case Key.Escape:
                ClosePictureViewer();
                e.Handled = true;
                break;
            case Key.Left:
                StepPictureViewer(-1);
                e.Handled = true;
                break;
            case Key.Right:
                StepPictureViewer(+1);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Hands the on-screen picture to the clipboard as a bitmap, not a filename or a run of text.</summary>
    private void CopyViewerPicture()
    {
        var payload = BuildViewerCopyPayload();
        if (payload is null) return;
        var clipboard = this.Clipboard;
        if (clipboard is null) return;
        _ = clipboard.SetDataAsync(payload);
    }

    /// <summary>Runs the entry's loader without letting one bad file take the viewer down: a picture that will
    /// not decode is shown as "gone" in the caption, and the rest of the set still pages.</summary>
    private static byte[]? SafeLoad(PictureRef reference)
    {
        try { return reference.Load(); }
        catch (Exception) { return null; }
    }

    private static Bitmap? Decode(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return null;
        try { return new Bitmap(new System.IO.MemoryStream(bytes)); }
        catch (Exception) { return null; }
    }

    /// <summary>The transfer the copy button would hand the clipboard, built but not written — so a check can
    /// read the payload without disturbing whatever the person last copied.</summary>
    internal DataTransfer? BuildViewerCopyPayload()
    {
        var bytes = _viewerIndex < _viewerSet.Count ? SafeLoad(_viewerSet[_viewerIndex]) : null;
        var bitmap = Decode(bytes);
        if (bitmap is null) return null;
        var item = new DataTransferItem();
        item.Set(DataFormat.Bitmap, bitmap);
        var data = new DataTransfer();
        data.Add(item);
        return data;
    }

    // ── Check accessors ──

    internal bool PictureViewerOpenForCheck => PictureViewerHost.IsVisible;
    internal string PictureViewerCaptionForCheck => PictureViewerCaption.Text ?? "";
    internal int PictureViewerButtonCountForCheck => PictureViewerBar.Children.OfType<Button>().Count();
    internal int PictureViewerIndexForCheck => _viewerIndex;
    internal int PictureViewerCountForCheck => _viewerSet.Count;
    internal DataTransfer? PictureViewerCopyPayloadForCheck => BuildViewerCopyPayload();

    /// <summary>The on-screen picture's own size, or -1 when the frame shows nothing: a viewer that "opened" on
    /// a blank image would pass a visibility check and still be useless.</summary>
    internal (int Width, int Height) PictureViewerSizeForCheck
        => PictureViewerImage.Source is Bitmap bitmap
            ? (bitmap.PixelSize.Width, bitmap.PixelSize.Height)
            : (-1, -1);

    /// <summary>Clicks a caption-row button (0 = copy, 1 = close) through its own Click, so the wiring is what
    /// a check exercises.</summary>
    internal void ClickPictureViewerButtonForCheck(int index)
    {
        var buttons = PictureViewerBar.Children.OfType<Button>().ToList();
        if (index >= 0 && index < buttons.Count)
            buttons[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Sends a key down the window's own tunnel, the same route <see cref="OnViewerKeyDown"/> listens
    /// on, so a check drives the real handler rather than the method it happens to call.</summary>
    internal void PressViewerKeyForCheck(Key key)
        => OnViewerKeyDown(this, new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = KeyModifiers.None,
            Source = this,
        });
}
