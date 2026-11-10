using Avalonia.Controls;
using Avalonia.Threading;

namespace AxmolHub;

/// <summary>The upstream control initializes via async-void. Catch only failures from that assembly while
/// adapters are starting; never swallow a Hub exception. Keep an initializing native host attached on close
/// until its controller has finished, otherwise WebView2 aborts against a destroyed HWND.</summary>
internal static class WebViewInitialization
{
    private static readonly Dictionary<NativeWebView, Action<Exception>> Pending = [];

    internal static void Watch(NativeWebView view, Action<Exception> failed)
    {
        if (Pending.Count == 0) Dispatcher.UIThread.UnhandledException += OnFailure;
        Pending[view] = failed;
    }

    internal static void Finished(NativeWebView view)
    {
        Pending.Remove(view);
        if (Pending.Count == 0) Dispatcher.UIThread.UnhandledException -= OnFailure;
    }

    private static void OnFailure(object? sender, DispatcherUnhandledExceptionEventArgs args)
    {
        if (args.Handled || args.Exception.TargetSite?.Module.Assembly != typeof(NativeWebView).Assembly) return;
        var failed = Pending.ToArray();
        if (failed.Length == 0) return;
        args.Handled = true;
        foreach (var (view, notify) in failed)
        {
            Finished(view);
            notify(args.Exception);
        }
    }
}
