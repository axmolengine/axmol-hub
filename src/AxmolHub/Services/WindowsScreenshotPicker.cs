using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>Frozen desktop selection in physical pixels; each monitor owns an overlay at its own DPI.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsScreenshotPicker
{
    internal static async Task<ScreenshotPick> PickAsync(Window owner, ScreenshotKind kind, CancellationToken ct)
    {
        var screens = owner.Screens.All;
        if (screens.Count == 0) return new(ErrorKey: "ChatCaptureUnavailable");
        var desktop = new PixelRect(screens.Min(s => s.Bounds.X), screens.Min(s => s.Bounds.Y),
            screens.Max(s => s.Bounds.Right) - screens.Min(s => s.Bounds.X),
            screens.Max(s => s.Bounds.Bottom) - screens.Min(s => s.Bounds.Y));
        var frame = await Task.Run(() => WindowsScreenCapture.Grab(null), ct);
        if (frame is null || frame.Width != desktop.Width || frame.Height != desktop.Height)
            return new(ErrorKey: "ChatCaptureFailed");
        var windows = kind == ScreenshotKind.Window ? WindowsScreenCapture.List() : [];
        using var source = new Bitmap(new MemoryStream(frame.Png));
        var picked = new TaskCompletionSource<PixelRect?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlays = new List<SelectionWindow>();
        var images = new List<Bitmap>();
        PixelPoint? start = null;
        PixelRect? selection = null;
        CapturableWindow? selectedWindow = null;

        void Update(PixelPoint point)
        {
            if (kind == ScreenshotKind.Window)
            {
                selectedWindow = windows.FirstOrDefault(w => new PixelRect(w.Left, w.Top, w.Width, w.Height).Contains(point));
                selection = selectedWindow is { } w ? new PixelRect(w.Left, w.Top, w.Width, w.Height) : null;
            }
            else if (start is { } anchor)
                selection = new PixelRect(Math.Min(anchor.X, point.X), Math.Min(anchor.Y, point.Y),
                    Math.Abs(anchor.X - point.X), Math.Abs(anchor.Y - point.Y));
            foreach (var overlay in overlays) overlay.SetSelection(selection);
        }

        try
        {
            foreach (var screen in screens)
            {
                var image = Crop(source, new PixelRect(screen.Bounds.X - desktop.X, screen.Bounds.Y - desktop.Y,
                    screen.Bounds.Width, screen.Bounds.Height));
                images.Add(image);
                var overlay = new SelectionWindow(screen.Bounds, screen.Scaling, image, kind);
                overlays.Add(overlay);
                overlay.PointerMoved += (_, e) => Update(overlay.PointToScreen(e.GetPosition(overlay)));
                overlay.PointerPressed += (_, e) =>
                {
                    if (e.GetCurrentPoint(overlay).Properties.IsRightButtonPressed)
                    { picked.TrySetResult(null); return; }
                    if (!e.GetCurrentPoint(overlay).Properties.IsLeftButtonPressed) return;
                    var point = overlay.PointToScreen(e.GetPosition(overlay));
                    if (kind == ScreenshotKind.Window)
                    {
                        Update(point);
                        if (selection is not null) picked.TrySetResult(selection);
                    }
                    else
                    {
                        start = point;
                        e.Pointer.Capture(overlay);
                    }
                    e.Handled = true;
                };
                overlay.PointerReleased += (_, e) =>
                {
                    if (kind != ScreenshotKind.Region || start is null) return;
                    Update(overlay.PointToScreen(e.GetPosition(overlay)));
                    e.Pointer.Capture(null);
                    if (selection is { Width: > 1, Height: > 1 }) picked.TrySetResult(selection);
                    else { start = null; selection = null; }
                };
                overlay.KeyDown += (_, e) => { if (e.Key == Key.Escape) picked.TrySetResult(null); };
                overlay.Closed += (_, _) => picked.TrySetResult(null);
                overlay.Show();
            }
            using var cancellation = ct.Register(() => picked.TrySetCanceled(ct));
            var region = await picked.Task;
            foreach (var overlay in overlays) overlay.Close();
            ct.ThrowIfCancellationRequested();
            if (region is not { } rect) return new();
            if (kind == ScreenshotKind.Window)
            {
                await Task.Delay(100, ct);
                var captured = await Task.Run(() => WindowsScreenCapture.Grab(selectedWindow), ct);
                return captured is null ? new(ErrorKey: "ChatCaptureFailed") : new(captured.Png);
            }
            rect = rect.Intersect(desktop);
            if (rect.Width < 2 || rect.Height < 2) return new();
            using var crop = Crop(source, new PixelRect(rect.X - desktop.X, rect.Y - desktop.Y, rect.Width, rect.Height));
            using var output = new MemoryStream();
            crop.Save(output, PngBitmapEncoderOptions.Default);
            return new(output.ToArray());
        }
        finally
        {
            foreach (var overlay in overlays) overlay.Close();
            foreach (var image in images) image.Dispose();
        }
    }

    private static WriteableBitmap Crop(Bitmap source, PixelRect rect)
    {
        var bitmap = new WriteableBitmap(rect.Size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = bitmap.Lock();
        source.CopyPixels(rect, buffer.Address, buffer.RowBytes * rect.Height, buffer.RowBytes);
        return bitmap;
    }

    private sealed class SelectionWindow : Window
    {
        private readonly SelectionSurface _surface;
        internal SelectionWindow(PixelRect bounds, double scaling, Bitmap image, ScreenshotKind kind)
        {
            Title = HubStrings.Get(kind == ScreenshotKind.Region ? "ChatCaptureRegion" : "ChatCaptureWindow");
            WindowDecorations = WindowDecorations.None;
            CanResize = false;
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = bounds.Position;
            Width = bounds.Width / scaling;
            Height = bounds.Height / scaling;
            Cursor = new Cursor(StandardCursorType.Cross);
            _surface = new SelectionSurface(image, kind);
            Content = _surface;
            Opened += (_, _) =>
            {
                // Set physical bounds after Avalonia has attached the HWND to its monitor's DPI.
                if (TryGetPlatformHandle() is { } handle)
                    SetWindowPos(handle.Handle, new nint(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0040);
            };
        }

        internal void SetSelection(PixelRect? rect)
        {
            _surface.Selection = rect is { } r
                ? new Rect(this.PointToClient(r.Position), this.PointToClient(new PixelPoint(r.Right, r.Bottom)))
                : null;
            _surface.InvalidateVisual();
        }
    }

    private sealed class SelectionSurface(Bitmap image, ScreenshotKind kind) : Control
    {
        internal Rect? Selection;
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var bounds = new Rect(Bounds.Size);
            context.DrawImage(image, bounds);
            if (Selection is { } selected)
            {
                var r = selected.Intersect(bounds);
                if (r.Width > 0 && r.Height > 0)
                {
                    context.FillRectangle(Dim, new Rect(0, 0, Bounds.Width, r.Top));
                    context.FillRectangle(Dim, new Rect(0, r.Bottom, Bounds.Width, Math.Max(0, Bounds.Height - r.Bottom)));
                    context.FillRectangle(Dim, new Rect(0, r.Top, r.Left, r.Height));
                    context.FillRectangle(Dim, new Rect(r.Right, r.Top, Math.Max(0, Bounds.Width - r.Right), r.Height));
                    context.DrawRectangle(null, new Pen(Brushes.DeepSkyBlue, 2), selected);
                }
                else context.FillRectangle(Dim, bounds);
            }
            else context.FillRectangle(Dim, bounds);
            var hint = new FormattedText(HubStrings.Get(kind == ScreenshotKind.Region ? "ChatCaptureRegionHint" : "ChatCaptureWindowHint"),
                System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 16, Brushes.White);
            context.FillRectangle(Brushes.Black, new Rect(18, 18, hint.Width + 24, hint.Height + 16), 6);
            context.DrawText(hint, new Point(30, 26));
        }
        private static readonly IBrush Dim = new SolidColorBrush(Color.FromArgb(100, 0, 0, 0));
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
