using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using AxmolHub.Core;

namespace AxmolHub;

internal sealed partial class WorkspaceBrowserView
{
    private const string ConsoleHookScript = """
        (() => {
          if (window.__axmolHubConsoleHooked) return 'ok';
          window.__axmolHubConsoleHooked = true;
          const buf = window.__axmolHubConsoleLog = [];
          const maxEntries = 200;
          const push = (level, parts) => {
            try {
              const text = parts.map(v => {
                if (v instanceof Error) return v.stack || v.message || String(v);
                if (typeof v === 'string') return v;
                try { return JSON.stringify(v); } catch (_) { return String(v); }
              }).join(' ');
              const clipped = text.length > 500 ? text.slice(0, 500) + '…' : text;
              buf.push({ level, text: clipped });
              if (buf.length > maxEntries) buf.splice(0, buf.length - maxEntries);
            } catch (_) {}
          };
          for (const level of ['log', 'info', 'warn', 'error']) {
            const orig = console[level];
            if (typeof orig === 'function') {
              console[level] = function(...args) {
                push(level.toUpperCase(), args);
                return orig.apply(this, args);
              };
            }
          }
          window.addEventListener('error', e => {
            const loc = e.filename ? ` (${e.filename}:${e.lineno || 0})` : '';
            push('ERROR', [e.message || 'Script error', loc]);
          });
          window.addEventListener('unhandledrejection', e => {
            push('ERROR', ['Unhandled promise rejection:', e.reason]);
          });
          return 'ok';
        })()
        """;

    private void CompleteNavigationWaiter(bool success, Uri? request)
    {
        var waiter = _navigationWaiter;
        _navigationWaiter = null;
        waiter?.TrySetResult((success, request));
    }

    private async Task OnNavigationSucceededAsync()
    {
        await InstallConsoleHookAsync();
        await RefreshMetadataAsync();
    }

    private async Task InstallConsoleHookAsync()
    {
        if (_disposed || _message.IsVisible || _web is not { } web) return;
        try
        {
            await InvokeScriptStringAsync(web, ConsoleHookScript);
        }
        catch (Exception)
        {
            // Console hook is best-effort; never disrupt normal browsing if a page blocks execution.
        }
    }

    internal static async Task<string?> InvokeScriptStringAsync(NativeWebView web, string script)
    {
        var result = await web.InvokeScript(script);
        // WebView2 JSON-encodes script return values; WebKit returns strings directly.
        if (OperatingSystem.IsWindows() && result is not null)
            return JsonSerializer.Deserialize<string>(result);
        return result;
    }

    internal async Task<BrowserOpenResult> NavigateAndWaitAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_disposed)
            return new BrowserOpenResult(false, TabId, null, null, "The browser tab was closed.");
        if (!(IsWebAddress(uri) || IsLocalFileAddress(uri)))
            return new BrowserOpenResult(false, TabId, null, null, "Unsupported URL for the embedded browser.");
        if (uri.IsFile)
        {
            var folder = Path.GetDirectoryName(uri.LocalPath);
            if (string.IsNullOrEmpty(folder) || !File.Exists(uri.LocalPath))
                return new BrowserOpenResult(false, TabId, null, null, "The local HTML file does not exist.");
            _localDirectories.Add(folder);
        }

        _requested = uri;
        _currentUri = uri;
        _address.Text = uri.AbsoluteUri;
        _loading = true;
        _lastErrorKey = null;
        ResetMetadata();
        var waiter = new TaskCompletionSource<(bool Success, Uri? Request)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _navigationWaiter = waiter;

        if (!EnsureWebView())
        {
            _loading = false;
            CompleteNavigationWaiter(false, null);
            SyncNavigation();
            return new BrowserOpenResult(false, TabId, uri.AbsoluteUri, null,
                HubStrings.Get(_lastErrorKey ?? MissingRuntimeKey()));
        }

        _message.IsVisible = false;
        _web!.IsVisible = _active;
        SyncNavigation();
        Log("assistant navigation requested");
        _ = NavigatePageAsync(uri);

        var (completed, outcome) = await WaitForNavigationAsync(waiter, timeout, cancellationToken);
        if (!completed)
        {
            return new BrowserOpenResult(true, TabId, _currentUri?.AbsoluteUri ?? uri.AbsoluteUri, _pageTitle,
                null, TimedOut: true);
        }
        if (!outcome.Success)
        {
            var message = _disposed
                ? "The browser tab was closed during navigation."
                : HubStrings.Get(_lastErrorKey ?? "WorkspaceBrowserLoadFailed");
            return new BrowserOpenResult(false, TabId, _currentUri?.AbsoluteUri ?? uri.AbsoluteUri, _pageTitle, message);
        }

        await InstallConsoleHookAsync();
        await RefreshMetadataAsync();
        var consoleOutput = await ReadConsoleOnlyAsync();
        return new BrowserOpenResult(true, TabId, _currentUri?.AbsoluteUri ?? outcome.Request?.AbsoluteUri ?? uri.AbsoluteUri,
            _pageTitle, null, TimedOut: false, ConsoleOutput: consoleOutput);
    }

    internal async Task<BrowserReadResult> ReadPageAsync(BrowserReadRequest request, CancellationToken cancellationToken)
    {
        if (_disposed)
            return new BrowserReadResult(false, TabId, null, null, false, null, null, false, null, null, "The browser tab was closed.");
        if (_web is null)
            return new BrowserReadResult(false, TabId, _currentUri?.AbsoluteUri, _pageTitle, false, null, null, false, null, null,
                "This browser tab has no loaded page yet. Call browser_open first.");

        await WaitReadyAsync(WorkspaceBrowserPolicy.DefaultActionTimeout, cancellationToken);
        if (_disposed || _web is not { } web)
            return new BrowserReadResult(false, TabId, null, null, false, null, null, false, null, null, "The browser tab was closed.");
        if (_message.IsVisible)
            return new BrowserReadResult(false, TabId, _currentUri?.AbsoluteUri, _pageTitle, false, null, null, false, null, null,
                HubStrings.Get(_lastErrorKey ?? "WorkspaceBrowserLoadFailed"));

        try
        {
            await InstallConsoleHookAsync();
            var raw = await InvokeScriptStringAsync(web, BuildReadPageScript(request));
            if (string.IsNullOrWhiteSpace(raw))
                return new BrowserReadResult(false, TabId, _currentUri?.AbsoluteUri, _pageTitle, Loading, null, null, false, null, null,
                    "The page returned no readable snapshot.");

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var url = StringProp(root, "url") ?? _currentUri?.AbsoluteUri;
            var title = StringProp(root, "title");
            if (!string.IsNullOrWhiteSpace(title) && !string.Equals(_pageTitle, title, StringComparison.Ordinal))
            {
                _pageTitle = title;
                StateChanged?.Invoke(this);
            }
            var viewport = StringProp(root, "viewport");
            var text = StringProp(root, "text");
            var truncated = BoolProp(root, "textTruncated");
            var elements = StringProp(root, "elements");
            var consoleOutput = StringProp(root, "console");
            return new BrowserReadResult(true, TabId, url, title ?? _pageTitle, Loading, viewport, text, truncated,
                elements, consoleOutput, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new BrowserReadResult(false, TabId, _currentUri?.AbsoluteUri, _pageTitle, Loading, null, null, false, null, null,
                $"Could not inspect page DOM: {ex.Message}");
        }
    }

    internal async Task<BrowserScreenshotResult> CaptureScreenshotAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_disposed)
            return new BrowserScreenshotResult(false, TabId, null, null, null, "The browser tab was closed.");
        if (_web is null)
            return new BrowserScreenshotResult(false, TabId, _currentUri?.AbsoluteUri, _pageTitle, null,
                "This browser tab has no loaded page yet. Call browser_open first.");

        await WaitReadyAsync(timeout, cancellationToken);
        if (_disposed || _web is not { } web)
            return new BrowserScreenshotResult(false, TabId, null, null, null, "The browser tab was closed.");
        if (_message.IsVisible)
            return new BrowserScreenshotResult(false, TabId, _currentUri?.AbsoluteUri, _pageTitle, null,
                HubStrings.Get(_lastErrorKey ?? "WorkspaceBrowserLoadFailed"));

        var windowBounds = TryGetWindowBounds(web);
        var (frame, error) = await WebViewSnapshot.CaptureAsync(web.TryGetPlatformHandle(), windowBounds, timeout, cancellationToken, Log);
        if (frame is null)
            return new BrowserScreenshotResult(false, TabId, _currentUri?.AbsoluteUri, _pageTitle, null,
                error ?? "Could not capture the embedded browser screenshot.");

        return new BrowserScreenshotResult(true, TabId, _currentUri?.AbsoluteUri, _pageTitle, frame, null);
    }

    private static WebViewWindowBounds? TryGetWindowBounds(NativeWebView web)
    {
        if (TopLevel.GetTopLevel(web) is not Window window) return null;
        if (web.Bounds.Width <= 0 || web.Bounds.Height <= 0) return null;
        if (window.ClientSize.Width <= 0 || window.ClientSize.Height <= 0) return null;
        if (web.TranslatePoint(new Point(0, 0), window) is not { } origin) return null;
        return new WebViewWindowBounds(origin.X, origin.Y, web.Bounds.Width, web.Bounds.Height,
            window.ClientSize.Width, window.ClientSize.Height);
    }

    internal async Task<BrowserActResult> ExecuteActionAsync(BrowserActRequest request, CancellationToken cancellationToken)
    {
        if (_disposed)
            return new BrowserActResult(false, TabId, null, null, null, "The browser tab was closed.");
        if (_web is null)
            return new BrowserActResult(false, TabId, _currentUri?.AbsoluteUri, _pageTitle, null,
                "This browser tab has no loaded page yet. Call browser_open first.");

        await WaitReadyAsync(request.Timeout, cancellationToken);
        if (_disposed || _web is not { } web)
            return new BrowserActResult(false, TabId, null, null, null, "The browser tab was closed.");
        if (_message.IsVisible && request.Action != "reload")
            return new BrowserActResult(false, TabId, _currentUri?.AbsoluteUri, _pageTitle, null,
                HubStrings.Get(_lastErrorKey ?? "WorkspaceBrowserLoadFailed"));

        switch (request.Action)
        {
            case "back":
                if (!web.CanGoBack)
                    return new BrowserActResult(false, TabId, _currentUri?.AbsoluteUri, _pageTitle, null,
                        "Cannot go back: there is no previous history entry.");
                return await NavigateHistoryAsync(() => web.GoBack(), "Navigated back.", request.Timeout, cancellationToken);

            case "forward":
                if (!web.CanGoForward)
                    return new BrowserActResult(false, TabId, _currentUri?.AbsoluteUri, _pageTitle, null,
                        "Cannot go forward: there is no forward history entry.");
                return await NavigateHistoryAsync(() => web.GoForward(), "Navigated forward.", request.Timeout, cancellationToken);

            case "reload":
                return await NavigateHistoryAsync(() =>
                {
                    _message.IsVisible = false;
                    web.IsVisible = _active;
                    _loading = true;
                    _lastErrorKey = null;
                    ResetMetadata(keepWaiter: true);
                    SyncNavigation();
                    return web.Refresh();
                }, "Reloaded page.", request.Timeout, cancellationToken);

            case "wait":
                return await ExecuteWaitAsync(web, request, cancellationToken);

            case "click":
            case "type":
            case "scroll":
            case "eval":
                return await ExecuteScriptActionAsync(web, request, cancellationToken);

            default:
                return new BrowserActResult(false, TabId, _currentUri?.AbsoluteUri, _pageTitle, null,
                    $"Unsupported browser action '{request.Action}'.");
        }
    }

    private async Task<BrowserActResult> NavigateHistoryAsync(Func<bool> trigger, string output, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var waiter = new TaskCompletionSource<(bool Success, Uri? Request)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _navigationWaiter = waiter;
        if (!trigger())
        {
            CompleteNavigationWaiter(false, null);
            return new BrowserActResult(false, TabId, _currentUri?.AbsoluteUri, _pageTitle, null,
                "The embedded browser refused the navigation command.");
        }
        var (completed, outcome) = await WaitForNavigationAsync(waiter, timeout, cancellationToken);
        if (!completed)
            return new BrowserActResult(true, TabId, _currentUri?.AbsoluteUri, _pageTitle,
                output + " (still loading after timeout)", null);
        if (!outcome.Success)
            return new BrowserActResult(false, TabId, _currentUri?.AbsoluteUri, _pageTitle, null,
                HubStrings.Get(_lastErrorKey ?? "WorkspaceBrowserLoadFailed"));
        await InstallConsoleHookAsync();
        await RefreshMetadataAsync();
        return new BrowserActResult(true, TabId, _currentUri?.AbsoluteUri, _pageTitle, output, null);
    }

    private async Task<BrowserActResult> ExecuteWaitAsync(NativeWebView web, BrowserActRequest request, CancellationToken cancellationToken)
    {
        if (request.ElementRef is null && string.IsNullOrWhiteSpace(request.Selector))
        {
            await Task.Delay(request.WaitMs, cancellationToken);
            return new BrowserActResult(true, TabId, _currentUri?.AbsoluteUri, _pageTitle, $"Waited {request.WaitMs} ms.", null);
        }

        var checkScript = BuildCheckTargetScript(request.ElementRef, request.Selector);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed || !ReferenceEquals(web, _web))
                return new BrowserActResult(false, TabId, null, null, null, "The browser tab was closed while waiting.");
            var raw = await InvokeScriptStringAsync(web, checkScript);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                using var doc = JsonDocument.Parse(raw);
                if (BoolProp(doc.RootElement, "found"))
                {
                    return new BrowserActResult(true, TabId, _currentUri?.AbsoluteUri, _pageTitle,
                        StringProp(doc.RootElement, "summary") ?? "Target element appeared.", null);
                }
            }
            if (watch.ElapsedMilliseconds >= request.WaitMs)
                return new BrowserActResult(false, TabId, _currentUri?.AbsoluteUri, _pageTitle, null,
                    $"Timed out after {request.WaitMs} ms waiting for element.");
            await Task.Delay(100, cancellationToken);
        }
    }

    private async Task<BrowserActResult> ExecuteScriptActionAsync(NativeWebView web, BrowserActRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await InstallConsoleHookAsync();
            var script = request.Action switch
            {
                "click" => BuildClickScript(request.ElementRef, request.Selector),
                "type" => BuildTypeScript(request.ElementRef, request.Selector, request.Text ?? "", request.Submit),
                "scroll" => BuildScrollScript(request.ElementRef, request.Selector, request.DeltaX, request.DeltaY),
                "eval" => BuildEvalScript(request.Script ?? ""),
                _ => throw new InvalidOperationException($"Unsupported action {request.Action}"),
            };
            var raw = await InvokeScriptStringAsync(web, script);
            if (string.IsNullOrWhiteSpace(raw))
                return new BrowserActResult(false, TabId, _currentUri?.AbsoluteUri, _pageTitle, null,
                    $"Browser '{request.Action}' script returned no response (check syntax or page state).");

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var ok = BoolProp(root, "ok");
            var url = StringProp(root, "url") ?? _currentUri?.AbsoluteUri;
            var title = StringProp(root, "title") ?? _pageTitle;
            var output = StringProp(root, "output");
            var error = StringProp(root, "error");
            return new BrowserActResult(ok, TabId, url, title, output, error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new BrowserActResult(false, TabId, _currentUri?.AbsoluteUri, _pageTitle, null,
                $"Browser '{request.Action}' failed: {ex.Message}");
        }
    }

    private async Task<string?> ReadConsoleOnlyAsync()
    {
        if (_disposed || _message.IsVisible || _web is not { } web) return null;
        try
        {
            var raw = await InvokeScriptStringAsync(web, """
                JSON.stringify((window.__axmolHubConsoleLog || []).map(e => `[${e.level}] ${e.text}`).join('\n'))
                """);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var text = JsonSerializer.Deserialize<string>(raw);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task WaitReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_initializing) return;
        using var delayCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(timeout, delayCancel.Token);
        var finished = await Task.WhenAny(Ready, delay);
        if (ReferenceEquals(finished, Ready))
        {
            delayCancel.Cancel();
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task<(bool Completed, (bool Success, Uri? Request) Outcome)> WaitForNavigationAsync(
        TaskCompletionSource<(bool Success, Uri? Request)> waiter,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var delayCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(timeout, delayCancel.Token);
        var finished = await Task.WhenAny(waiter.Task, delay);
        if (ReferenceEquals(finished, waiter.Task))
        {
            delayCancel.Cancel();
            return (true, await waiter.Task);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return (false, default);
    }

    private static string BuildReadPageScript(BrowserReadRequest request)
    {
        var includeText = request.IncludeText ? "true" : "false";
        var includeElements = request.IncludeElements ? "true" : "false";
        var includeConsole = request.IncludeConsole ? "true" : "false";
        var clearConsole = request.ClearConsole ? "true" : "false";
        var maxChars = request.MaxCharacters;
        return $$"""
            (() => {
              const out = {
                url: location.href,
                title: document.title || '',
                viewport: `${window.innerWidth}x${window.innerHeight} (scrollY=${Math.round(window.scrollY)}/${document.documentElement ? document.documentElement.scrollHeight : 0})`,
                text: '',
                textTruncated: false,
                elements: '',
                console: ''
              };
              if ({{includeText}}) {
                const raw = ((document.body && document.body.innerText) || '').replace(/\r\n/g, '\n').replace(/\n{3,}/g, '\n\n').trim();
                out.textTruncated = raw.length > {{maxChars}};
                out.text = out.textTruncated ? raw.slice(0, {{maxChars}}) : raw;
              }
              if ({{includeElements}}) {
                document.querySelectorAll('[data-axmol-ref]').forEach(el => el.removeAttribute('data-axmol-ref'));
                const selector = 'a[href],button,input,textarea,select,[role="button"],[role="link"],[role="textbox"],[contenteditable="true"],canvas';
                const nodes = Array.from(document.querySelectorAll(selector));
                const lines = [];
                let ref = 0;
                for (const el of nodes) {
                  if (ref >= 80) break;
                  const style = window.getComputedStyle(el);
                  if (style.display === 'none' || style.visibility === 'hidden') continue;
                  const rect = el.getBoundingClientRect();
                  if (rect.width <= 0 && rect.height <= 0) continue;
                  ref++;
                  el.setAttribute('data-axmol-ref', String(ref));
                  const tag = el.tagName.toLowerCase();
                  const attrs = [];
                  if (el.id) attrs.push(`id="${el.id}"`);
                  const type = el.getAttribute('type');
                  if (type) attrs.push(`type="${type}"`);
                  const role = el.getAttribute('role');
                  if (role) attrs.push(`role="${role}"`);
                  if (el.disabled) attrs.push('disabled');
                  if (tag === 'a' && el.getAttribute('href')) attrs.push(`href="${el.getAttribute('href').slice(0, 120)}"`);
                  if (tag === 'canvas') attrs.push(`size=${el.width}x${el.height}`);
                  const label = (el.getAttribute('aria-label') || el.getAttribute('placeholder') || el.innerText || el.getAttribute('title') || '').replace(/\s+/g, ' ').trim().slice(0, 80);
                  let val = '';
                  if (tag === 'input' || tag === 'textarea' || tag === 'select') {
                    if ((type || '').toLowerCase() === 'password') val = ' value="***"';
                    else if ((type || '').toLowerCase() === 'checkbox' || (type || '').toLowerCase() === 'radio') val = el.checked ? ' checked' : '';
                    else if (el.value) val = ` value="${String(el.value).slice(0, 80)}"`;
                  }
                  const attrText = attrs.length ? ' ' + attrs.join(' ') : '';
                  const labelText = label ? ` "${label}"` : '';
                  lines.push(`[${ref}] <${tag}${attrText}>${labelText}${val}`);
                }
                out.elements = lines.join('\n');
              }
              if ({{includeConsole}}) {
                const buf = window.__axmolHubConsoleLog || [];
                out.console = buf.map(e => `[${e.level}] ${e.text}`).join('\n');
                if ({{clearConsole}} && Array.isArray(window.__axmolHubConsoleLog)) window.__axmolHubConsoleLog.length = 0;
              }
              return JSON.stringify(out);
            })()
            """;
    }

    private static string TargetLookupJs(int? elementRef, string? selector)
    {
        if (elementRef is { } r)
            return $"document.querySelector('[data-axmol-ref=\"{r}\"]')";
        return $"document.querySelector({JsonSerializer.Serialize(selector ?? "")})";
    }

    private static string BuildCheckTargetScript(int? elementRef, string? selector)
    {
        var lookup = TargetLookupJs(elementRef, selector);
        return $$"""
            (() => {
              try {
                const el = {{lookup}};
                if (!el) return JSON.stringify({ found: false });
                const text = (el.innerText || el.getAttribute('aria-label') || '').replace(/\s+/g, ' ').trim().slice(0, 80);
                return JSON.stringify({ found: true, summary: `Found <${el.tagName.toLowerCase()}>${text ? ' "' + text + '"' : ''}` });
              } catch (e) {
                return JSON.stringify({ found: false });
              }
            })()
            """;
    }

    private static string BuildClickScript(int? elementRef, string? selector)
    {
        var lookup = TargetLookupJs(elementRef, selector);
        return $$"""
            (() => {
              try {
                const el = {{lookup}};
                if (!el) return JSON.stringify({ ok: false, error: 'Target element not found. Call browser_read to refresh element refs.' });
                el.scrollIntoView({ block: 'center', inline: 'nearest' });
                if (typeof el.focus === 'function') el.focus();
                el.click();
                const label = (el.innerText || el.getAttribute('aria-label') || '').replace(/\s+/g, ' ').trim().slice(0, 80);
                return JSON.stringify({ ok: true, url: location.href, title: document.title || '', output: `Clicked <${el.tagName.toLowerCase()}>${label ? ' "' + label + '"' : ''}` });
              } catch (e) {
                return JSON.stringify({ ok: false, error: e && e.message ? e.message : String(e) });
              }
            })()
            """;
    }

    private static string BuildTypeScript(int? elementRef, string? selector, string text, bool submit)
    {
        var lookup = TargetLookupJs(elementRef, selector);
        var encodedText = JsonSerializer.Serialize(text);
        var doSubmit = submit ? "true" : "false";
        return $$"""
            (() => {
              try {
                const el = {{lookup}};
                if (!el) return JSON.stringify({ ok: false, error: 'Target element not found. Call browser_read to refresh element refs.' });
                el.scrollIntoView({ block: 'center', inline: 'nearest' });
                if (typeof el.focus === 'function') el.focus();
                const value = {{encodedText}};
                const tag = el.tagName.toLowerCase();
                if (tag === 'input' || tag === 'textarea') {
                  const proto = tag === 'textarea' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
                  const desc = Object.getOwnPropertyDescriptor(proto, 'value');
                  if (desc && desc.set) desc.set.call(el, value);
                  else el.value = value;
                  el.dispatchEvent(new Event('input', { bubbles: true }));
                  el.dispatchEvent(new Event('change', { bubbles: true }));
                } else if (el.isContentEditable) {
                  el.textContent = value;
                  el.dispatchEvent(new Event('input', { bubbles: true }));
                } else {
                  return JSON.stringify({ ok: false, error: `Element <${tag}> is not an editable input, textarea, or contenteditable element.` });
                }
                if ({{doSubmit}}) {
                  if (el.form && typeof el.form.requestSubmit === 'function') el.form.requestSubmit();
                  else if (el.form && typeof el.form.submit === 'function') el.form.submit();
                  else {
                    el.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', bubbles: true }));
                    el.dispatchEvent(new KeyboardEvent('keyup', { key: 'Enter', code: 'Enter', bubbles: true }));
                  }
                }
                return JSON.stringify({ ok: true, url: location.href, title: document.title || '', output: `Typed ${value.length} character(s) into <${tag}>${{{doSubmit}} ? ' and submitted' : ''}.` });
              } catch (e) {
                return JSON.stringify({ ok: false, error: e && e.message ? e.message : String(e) });
              }
            })()
            """;
    }

    private static string BuildScrollScript(int? elementRef, string? selector, int deltaX, int deltaY)
    {
        var hasTarget = elementRef is not null || !string.IsNullOrWhiteSpace(selector);
        var targetBlock = hasTarget
            ? $$"""
                const el = {{TargetLookupJs(elementRef, selector)}};
                if (!el) return JSON.stringify({ ok: false, error: 'Scroll target element not found.' });
                el.scrollIntoView({ block: 'center', inline: 'nearest' });
                """
            : $"window.scrollBy({deltaX}, {deltaY});";
        return $$"""
            (() => {
              try {
                {{targetBlock}}
                return JSON.stringify({ ok: true, url: location.href, title: document.title || '', output: `Scrolled to (${Math.round(window.scrollX)}, ${Math.round(window.scrollY)})` });
              } catch (e) {
                return JSON.stringify({ ok: false, error: e && e.message ? e.message : String(e) });
              }
            })()
            """;
    }

    private static string BuildEvalScript(string script)
    {
        var trimmed = script.Trim();
        var isBody = trimmed.Contains("return ", StringComparison.Ordinal)
                     || trimmed.Contains("return;", StringComparison.Ordinal)
                     || trimmed.Contains(';')
                     || trimmed.StartsWith("const ", StringComparison.Ordinal)
                     || trimmed.StartsWith("let ", StringComparison.Ordinal)
                     || trimmed.StartsWith("var ", StringComparison.Ordinal)
                     || trimmed.StartsWith("for ", StringComparison.Ordinal)
                     || trimmed.StartsWith("if ", StringComparison.Ordinal)
                     || trimmed.StartsWith("while ", StringComparison.Ordinal)
                     || trimmed.StartsWith("try ", StringComparison.Ordinal);
        var wrapped = isBody ? $"(() => {{ {trimmed} }})()" : $"(() => (\n{trimmed}\n))()";
        return $$"""
            (() => {
              try {
                const value = {{wrapped}};
                let text;
                if (value === undefined) text = 'undefined';
                else if (typeof value === 'string') text = value;
                else {
                  try { text = JSON.stringify(value, null, 2); }
                  catch (_) { text = String(value); }
                }
                return JSON.stringify({ ok: true, url: location.href, title: document.title || '', output: text });
              } catch (e) {
                return JSON.stringify({ ok: false, url: location.href, title: document.title || '', error: e && (e.stack || e.message) ? (e.stack || e.message) : String(e) });
              }
            })()
            """;
    }

    private static string? StringProp(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool BoolProp(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
