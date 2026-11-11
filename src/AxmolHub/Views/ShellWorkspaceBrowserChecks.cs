using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using AxmolHub.Core;
using static AxmolHub.ShellCheckSupport;

namespace AxmolHub;

internal sealed partial class ShellDecisionsChecks
{
    /// <summary>The uncovered contract is native page lifetime across shell reparenting, not toolbar geometry.
    /// A loopback-only fixture proves it without visiting the network or touching a user's browser profile.</summary>
    private static async Task<bool> CheckWorkspaceBrowserAsync(MainWindow shell, string root)
    {
        var view = shell.WorkspaceViewForCheck!;
        view.HideFiles();
        view.OpenTool("browser");
        var browser = view.BrowserForCheck!;
        var empty = browser.WebViewForCheck is null;
        view.OpenTool("browser");
        var second = view.BrowserForCheck!;
        var multiple = !ReferenceEquals(browser, second) && browser.TabId != second.TabId && second.WebViewForCheck is null;
        view.SelectBrowserForCheck(browser.TabId);
        // Bind an ephemeral loopback port. No outbound access and no HTML loaded from the workspace.
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var url = new Uri($"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/");
        using var stop = new CancellationTokenSource();
        var serving = ServeAsync();
        try
        {
            browser.NavigateForCheck(url);
            await WaitUntilAsync(() => browser.WebViewForCheck?.TryGetPlatformHandle() is not null
                && browser.WebViewForCheck.Source == url && !browser.LoadingForCheck, maxTicks: 4000);
            var web = browser.WebViewForCheck;
            if (web?.TryGetPlatformHandle() is not { } handle) return false;
            await web.InvokeScript("window.hubLifetime = 'retained';");
            view.SelectBrowserForCheck(second.TabId);
            second.NavigateForCheck(new Uri(url, "/second"));
            await WaitUntilAsync(() => !second.LoadingForCheck && second.PageTitle == "Second page" && second.Favicon is not null, maxTicks: 4000);
            multiple &= !browser.NativeVisibleForCheck && second.NativeVisibleForCheck && browser.PageTitle == "First page"
                && browser.Favicon is not null && second.PageTitle == "Second page" && second.Favicon is not null
                && !ReferenceEquals(web, second.WebViewForCheck);
            view.SelectBrowserForCheck(browser.TabId);
            view.OpenTool("plan");
            var hidden = !browser.NativeVisibleForCheck;
            view.SelectBrowserForCheck(browser.TabId);
            shell.UpdateLayout();
            shell.ClickInspectorExpandToggleForCheck();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            shell.RestoreInspector();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var retained = ReferenceEquals(web, browser.WebViewForCheck)
                && web.TryGetPlatformHandle()?.Handle == handle.Handle
                && (await web.InvokeScript("window.hubLifetime"))?.Contains("retained", StringComparison.Ordinal) == true;
            shell.OpenSettings();
            var covered = !browser.NativeVisibleForCheck;
            shell.CloseSettings();
            view.ShowFiles();
            var drawer = !browser.NativeVisibleForCheck;
            view.HideFiles();
            shell.ClickInspectorToggleForCheck();
            hidden &= !browser.NativeVisibleForCheck;
            shell.ClickInspectorToggleForCheck();
            shell.NavigateTo("Projects");
            hidden &= !browser.NativeVisibleForCheck;
            shell.NavigateTo("Assistant");
            var alternate = Path.Combine(root, "browser-other-workspace");
            Directory.CreateDirectory(alternate);
            view.SetContext(alternate, () => shell.Chat.EditorScopeFor("", alternate));
            var isolated = view.BrowserForCheck is null && !browser.NativeVisibleForCheck && !second.NativeVisibleForCheck;
            view.SetContext(root, () => shell.Chat.EditorScopeFor("", root));
            isolated &= ReferenceEquals(browser, view.BrowserForCheck);
            browser.NavigateForCheck(new Uri("file:///not-authorized.html"));
            var restricted = web.Source.Scheme == "http";
            view.CloseToolForCheck(browser.TabId);
            Dispatcher.UIThread.RunJobs();
            var released = browser.DisposedForCheck && browser.WebViewForCheck is null && ReferenceEquals(second, view.BrowserForCheck)
                && !second.DisposedForCheck && second.NativeVisibleForCheck;
            view.CloseToolForCheck(second.TabId);
            // Closing during native controller initialization must not destroy its parent HWND early.
            view.OpenTool("browser");
            var quick = view.BrowserForCheck!;
            quick.NavigateForCheck(url);
            view.CloseToolForCheck(quick.TabId);
            await WaitUntilAsync(() => quick.WebViewForCheck is null, maxTicks: 4000);
            released &= quick.DisposedForCheck && quick.WebViewForCheck is null;
            shell.SelectWorkspaceDocument(Path.Combine(root, "src/main.cpp"));
            Console.WriteLine($"workspace browser: empty={empty}, multiple={multiple}, hidden={hidden}, retained={retained}, covered={covered}, drawer={drawer}, isolated={isolated}, restricted={restricted}, released={released}");
            return empty && multiple && hidden && retained && covered && drawer && isolated && restricted && released;
        }
        finally
        {
            stop.Cancel();
            server.Stop();
            try { await serving; } catch (OperationCanceledException) { } catch (SocketException) { }
            while (view.BrowserForCheck is { } remaining) view.CloseToolForCheck(remaining.TabId);
        }

        async Task ServeAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await server.AcceptTcpClientAsync(stop.Token);
                await using var stream = client.GetStream();
                try
                {
                    var buffer = new byte[4096];
                    var read = await stream.ReadAsync(buffer, stop.Token);
                    var request = Encoding.ASCII.GetString(buffer, 0, read);
                    var svg = request.StartsWith("GET /icon.svg ", StringComparison.Ordinal);
                    var png = request.StartsWith("GET /favicon.ico ", StringComparison.Ordinal);
                    var other = request.StartsWith("GET /second ", StringComparison.Ordinal);
                    var title = other ? "Second page" : "First page";
                    var body = png ? File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets/hub-icon-256.png"))
                        : Encoding.UTF8.GetBytes(svg ? "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'><rect width='16' height='16' fill='#36a'/></svg>"
                            : $"<!doctype html><html><head><title>{title}</title>{(other ? "<link rel='icon' href='/icon.svg'>" : "")}</head><body><input aria-label='fixture' value='native browser fixture'></body></html>");
                    var type = png ? "image/png" : svg ? "image/svg+xml" : "text/html; charset=utf-8";
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, stop.Token);
                    await stream.WriteAsync(body, stop.Token);
                }
                catch (IOException) { /* A stopped page may close its connection before the response is written. */ }
            }
        }
    }
}
