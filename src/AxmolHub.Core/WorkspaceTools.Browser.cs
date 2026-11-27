using System.ComponentModel;
using System.Text;

namespace AxmolHub.Core;

public sealed partial class WorkspaceTools
{
    [Description("Open or navigate an embedded browser tab in the workspace panel. Supports http(s):// URLs "
                 + "(including local dev servers like http://127.0.0.1:PORT or http://localhost:PORT) and local "
                 + ".html/.htm/.xhtml files inside the session workspace. Use this for live web/WASM previews, "
                 + "JS-rendered pages, or interactive debugging; use web_fetch for simple static public pages.")]
    public async Task<string> BrowserOpenAsync(
        [Description("Target http(s):// URL, localhost address, file:// URI, or workspace-relative path to an HTML file.")] string url,
        [Description("Optional existing browser tab_id to reuse. Omit to reuse the active workspace browser tab or create one.")] string? tab_id = null,
        [Description("When true, always open a fresh browser tab instead of reusing an existing one.")] bool new_tab = false,
        CancellationToken cancellationToken = default)
    {
        var decision = WorkspaceBrowserPolicy.ResolveOpenTarget(context, url);
        if (!decision.Allowed || decision.Target is not { } target)
            return SecretRedaction.Redact(WorkspaceBrowserPolicy.RefusalFor(decision, url), context.SensitiveValues);

        var bridge = context.Browser!;
        BrowserOpenResult result;
        try
        {
            result = await bridge.Open(
                context.WorkspaceRoot,
                new BrowserOpenRequest(target, string.IsNullOrWhiteSpace(tab_id) ? null : tab_id.Trim(),
                    new_tab, WorkspaceBrowserPolicy.DefaultNavigationTimeout),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SecretRedaction.Redact($"Tool failed: could not open embedded browser: {ex.Message}", context.SensitiveValues);
        }

        if (!result.Ok)
            return SecretRedaction.Redact($"Tool failed: {result.Error ?? "embedded browser could not load the page."}", context.SensitiveValues);

        var sb = new StringBuilder();
        var shownUrl = result.Url ?? target.AbsoluteUri;
        sb.Append("Opened ").Append(shownUrl);
        if (!string.IsNullOrWhiteSpace(result.Title)) sb.Append(" · title: ").Append(result.Title);
        if (!string.IsNullOrWhiteSpace(result.TabId)) sb.Append(" · tab_id: ").Append(result.TabId);
        if (result.TimedOut) sb.Append(" · (navigation timed out; page may still be loading)");
        if (!string.IsNullOrWhiteSpace(result.ConsoleOutput))
        {
            sb.Append(Lf).Append(Lf).Append(WorkspaceBrowserPolicy.UntrustedPageHeader).Append(Lf)
              .Append("Console output:").Append(Lf)
              .Append(result.ConsoleOutput);
        }
        return SecretRedaction.Redact(TextEol.ToLf(sb.ToString()), context.SensitiveValues);
    }

    [Description("Inspect open embedded browser tabs in the current workspace, or read a tab's live URL, title, "
                 + "visible text, interactive element refs (for browser_act), and captured console/error logs. "
                 + "Read-only; does not mutate the page or navigate.")]
    public async Task<string> BrowserReadAsync(
        [Description("Optional tab_id from browser_open or list_tabs. Omit to read the active browser tab.")] string? tab_id = null,
        [Description("When true, only list open embedded browser tabs in this workspace.")] bool list_tabs = false,
        [Description("Include visible page text (default true).")] bool include_text = true,
        [Description("Include indexed interactive elements and canvas sizes for browser_act (default true).")] bool include_elements = true,
        [Description("Include captured console messages and uncaught page errors (default true).")] bool include_console = true,
        [Description("Clear the captured console buffer after reading (default false).")] bool clear_console = false,
        [Description("Maximum characters of visible page text to return (default 8000, max 20000).")] int max_characters = WorkspaceBrowserPolicy.DefaultReadCharacters,
        CancellationToken cancellationToken = default)
    {
        if (context.Browser is not { } bridge)
            return "Refused: the embedded workspace browser is not attached in this host. Do not retry browser_read.";

        if (list_tabs)
        {
            IReadOnlyList<BrowserTabSummary> tabs;
            try
            {
                tabs = await bridge.ListTabs(context.WorkspaceRoot, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return SecretRedaction.Redact($"Tool failed: could not list embedded browser tabs: {ex.Message}", context.SensitiveValues);
            }

            if (tabs.Count == 0)
                return "No embedded browser tabs are open in this workspace. Call browser_open to open a URL or local HTML file.";

            var list = new StringBuilder();
            list.Append($"Open embedded browser tabs ({tabs.Count}):");
            foreach (var tab in tabs)
            {
                list.Append(Lf).Append("- ").Append(tab.TabId);
                if (tab.Active) list.Append(" [active]");
                if (tab.Loading) list.Append(" [loading]");
                if (!string.IsNullOrWhiteSpace(tab.Title)) list.Append(" · ").Append(tab.Title);
                if (!string.IsNullOrWhiteSpace(tab.Url)) list.Append(" · ").Append(tab.Url);
            }
            return SecretRedaction.Redact(TextEol.ToLf(list.ToString()), context.SensitiveValues);
        }

        var limit = Math.Clamp(max_characters, 200, WorkspaceBrowserPolicy.MaxReadCharacters);
        BrowserReadResult result;
        try
        {
            result = await bridge.Read(
                context.WorkspaceRoot,
                new BrowserReadRequest(string.IsNullOrWhiteSpace(tab_id) ? null : tab_id.Trim(),
                    include_text, include_elements, include_console, clear_console, limit),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SecretRedaction.Redact($"Tool failed: could not read embedded browser tab: {ex.Message}", context.SensitiveValues);
        }

        if (!result.Ok)
            return SecretRedaction.Redact($"Refused: {result.Error ?? "no embedded browser tab is available to read."}", context.SensitiveValues);

        var sb = new StringBuilder();
        sb.Append("tab_id: ").Append(result.TabId ?? "");
        if (!string.IsNullOrWhiteSpace(result.Url)) sb.Append(" · url: ").Append(result.Url);
        if (!string.IsNullOrWhiteSpace(result.Title)) sb.Append(" · title: ").Append(result.Title);
        if (result.Loading) sb.Append(" · [loading]");
        if (!string.IsNullOrWhiteSpace(result.Viewport)) sb.Append(" · viewport: ").Append(result.Viewport);
        sb.Append(Lf).Append(WorkspaceBrowserPolicy.UntrustedPageHeader);

        if (include_text)
        {
            sb.Append(Lf).Append(Lf).Append("## Visible text").Append(Lf);
            if (string.IsNullOrWhiteSpace(result.Text))
                sb.Append("(no visible text)");
            else
            {
                sb.Append(result.Text);
                if (result.TextTruncated)
                    sb.Append(Lf).Append($"… (text truncated to {limit} characters; call browser_read with a larger max_characters up to {WorkspaceBrowserPolicy.MaxReadCharacters} if needed)");
            }
        }

        if (include_elements)
        {
            sb.Append(Lf).Append(Lf).Append("## Interactive elements").Append(Lf);
            sb.Append(string.IsNullOrWhiteSpace(result.Elements) ? "(none detected)" : result.Elements);
        }

        if (include_console)
        {
            sb.Append(Lf).Append(Lf).Append("## Console & errors").Append(Lf);
            sb.Append(string.IsNullOrWhiteSpace(result.ConsoleOutput) ? "(no console messages captured)" : result.ConsoleOutput);
        }

        return SecretRedaction.Redact(TextEol.ToLf(sb.ToString()), context.SensitiveValues);
    }

    [Description("Interact with an embedded workspace browser tab: click, type, scroll, back, forward, reload, "
                 + "wait (for ms or a selector), or eval (synchronous JavaScript expression/body). "
                 + "Prefer element_ref from browser_read over guessing selectors.")]
    public async Task<string> BrowserActAsync(
        [Description("Action: click, type, scroll, back, forward, reload, wait, or eval.")] string action,
        [Description("Optional tab_id from browser_open or browser_read. Omit for the active workspace browser tab.")] string? tab_id = null,
        [Description("Element ref number returned by browser_read (for click, type, scroll, or wait).")] int? element_ref = null,
        [Description("CSS selector alternative to element_ref (for click, type, scroll, or wait).")] string? selector = null,
        [Description("Text to enter when action is 'type'.")] string? text = null,
        [Description("When true on action 'type', press Enter or submit the enclosing form.")] bool submit = false,
        [Description("Horizontal scroll pixels when action is 'scroll'.")] int delta_x = 0,
        [Description("Vertical scroll pixels when action is 'scroll' (positive scrolls down; defaults to 600 if no target or delta is set).")] int delta_y = 0,
        [Description("Milliseconds to wait when action is 'wait' (50 to 10000, default 1000).")] int wait_ms = 1000,
        [Description("JavaScript expression or function body to run when action is 'eval'.")] string? script = null,
        CancellationToken cancellationToken = default)
    {
        if (context.Browser is not { } bridge)
            return "Refused: the embedded workspace browser is not attached in this host. Do not retry browser_act.";

        var verb = (action ?? "").Trim().ToLowerInvariant();
        if (!WorkspaceBrowserPolicy.IsSupportedAction(verb))
            return $"Refused: unsupported browser_act action '{action}'. Use one of: click, type, scroll, back, forward, reload, wait, eval.";

        if (verb is "click" or "type" && element_ref is null && string.IsNullOrWhiteSpace(selector))
            return $"Refused: browser_act '{verb}' requires element_ref (from browser_read) or a CSS selector.";
        if (verb == "type" && text is null)
            return "Refused: browser_act 'type' requires the text parameter.";
        if (verb == "eval" && string.IsNullOrWhiteSpace(script))
            return "Refused: browser_act 'eval' requires a non-empty script parameter.";

        var clampedWait = Math.Clamp(wait_ms, 50, WorkspaceBrowserPolicy.MaxWaitMs);
        var effectiveDeltaY = verb == "scroll" && element_ref is null && string.IsNullOrWhiteSpace(selector) && delta_x == 0 && delta_y == 0
            ? 600 : delta_y;

        BrowserActResult result;
        try
        {
            result = await bridge.Act(
                context.WorkspaceRoot,
                new BrowserActRequest(
                    string.IsNullOrWhiteSpace(tab_id) ? null : tab_id.Trim(),
                    verb,
                    element_ref,
                    string.IsNullOrWhiteSpace(selector) ? null : selector.Trim(),
                    text,
                    submit,
                    delta_x,
                    effectiveDeltaY,
                    clampedWait,
                    script,
                    WorkspaceBrowserPolicy.DefaultActionTimeout),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SecretRedaction.Redact($"Tool failed: browser_act '{verb}' failed: {ex.Message}", context.SensitiveValues);
        }

        if (!result.Ok)
            return SecretRedaction.Redact($"Tool failed: {result.Error ?? $"browser_act '{verb}' did not succeed."}", context.SensitiveValues);

        var sb = new StringBuilder();
        sb.Append("Browser ").Append(verb).Append(" succeeded");
        if (!string.IsNullOrWhiteSpace(result.TabId)) sb.Append(" · tab_id: ").Append(result.TabId);
        if (!string.IsNullOrWhiteSpace(result.Url)) sb.Append(" · url: ").Append(result.Url);
        if (!string.IsNullOrWhiteSpace(result.Title)) sb.Append(" · title: ").Append(result.Title);
        if (!string.IsNullOrWhiteSpace(result.Output))
        {
            var output = result.Output;
            if (output.Length > WorkspaceBrowserPolicy.MaxEvalOutputCharacters)
                output = output[..WorkspaceBrowserPolicy.MaxEvalOutputCharacters]
                    + $"\n… ({output.Length - WorkspaceBrowserPolicy.MaxEvalOutputCharacters} more characters truncated)";
            sb.Append(Lf).Append(WorkspaceBrowserPolicy.UntrustedPageHeader).Append(Lf).Append(output);
        }
        return SecretRedaction.Redact(TextEol.ToLf(sb.ToString()), context.SensitiveValues);
    }
}
