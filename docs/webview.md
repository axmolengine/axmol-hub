# Embedded browser tab

## Scope and usage

Open the assistant's existing right-hand workspace panel, then choose **Browser** from its **+** menu. Enter an HTTP/HTTPS URL and press Enter. The toolbar provides back, forward, reload/stop, and an explicit **Open in default browser** action.

There is one browser tab per workspace, created on demand. It starts without a WebView or remote page; simply opening the tab makes no web request. Reopening selects the same tab. Browsers and navigation history survive conversation switches within a workspace, switching tabs, collapsing the panel, and panel expansion/restoration. Closing the tab (including the existing bulk-close commands) releases it. State is not persisted across Hub restarts.

The existing `WorkspaceView` owns the tab strip, overflow and close menus. Browser views remain attached but hidden when inactive; expansion/restoration uses `NativeWebView.BeginReparenting`. Settings and picture overlays, the narrow file-tree drawer and workspace tab menus explicitly hide the native browser surface. This avoids relying on Avalonia clipping to cover a native child window. No separate browser window or browser-specific panel layout is introduced.

## Component and platform prerequisites

`Avalonia.Controls.WebView` **12.1.2**, MIT (Copyright (c) 2026 AvaloniaUI OÜ), supports .NET 8 and depends on Avalonia >= 12.0.0. Hub remains on Avalonia 12.1.6. The package adds a managed binding assembly, not a bundled Chromium/CEF engine.

| Platform | Selected adapter | Runtime |
| --- | --- | --- |
| Windows | WebView2 only; no legacy WebView1 fallback | Shared Microsoft Edge WebView2 Evergreen Runtime |
| macOS | WKWebView | System WebKit |
| Linux | WebKitGTK only; no automatic WPE selection | GTK 3, WebKitGTK 4.1, libsoup 3, X11 or XWayland |

Runtime detection is performed before constructing the native view; missing libraries and asynchronous initialization failures produce an in-tab message. On Linux, library detection also runs when the browser tab opens, without constructing a WebView or requesting a page. If unavailable, the empty browser area immediately displays a selectable installation hint for Ubuntu 24.04 / 26.04 (`sudo apt install libgtk-3-0t64 libwebkit2gtk-4.1-0 libsoup-3.0-0`) and the X11/XWayland requirement. Detection is retried on navigation so a missing-library result is not permanently cached by Hub. Hub does not install dependencies automatically. A URL entered before a failure remains available to the explicit external-browser button (`UrlLauncher`).

Linux prerequisites (not executed by Hub):

- Debian/Ubuntu: `sudo apt install libgtk-3-0 libwebkit2gtk-4.1-0 libsoup-3.0-0` (newer distributions may use `libgtk-3-0t64`).
- Fedora: `sudo dnf install gtk3 webkit2gtk4.1 libsoup3`.
- Arch: `sudo pacman -S gtk3 webkit2gtk-4.1 libsoup3`.

The component accepts older WebKitGTK 4.0/libsoup 2.4, but this integration targets 4.1/libsoup 3. GTK 4 / WebKitGTK 6.0 is not supported. WebKitGTK uses X11, including through XWayland; native Wayland without XWayland is not an established supported configuration. AppImage does **not** bundle these libraries. These requirements are based on the pinned component's implementation and documentation, not Linux acceptance testing.

## Isolation and lifecycle

- Only HTTP/HTTPS top-level navigation is accepted. Custom protocols, `file:` and `javascript:` top-level navigation are rejected. Popup requests are handled in the same tab, not by launching another app automatically.
- No page-message handler invokes Hub tools, filesystem APIs, session state or credentials. The component may inject its own inert messaging helper; Hub does not expose a privileged bridge.
- Windows uses an isolated, per-process WebView2 data directory with a distinct InPrivate profile for each tab; it does not borrow Chrome/Edge profiles or enable OS-account SSO. macOS uses a non-persistent data store; Linux an ephemeral website-data manager. Engine housekeeping files can remain in the OS temp directory on Windows; there is no claim that the engine writes nothing to disk.
- DevTools are disabled. Hub does not turn off the browser sandbox or request permissions merely by opening the tab. Website features remain subject to the engine/platform's behavior; they are not granted access to Hub's computer-use capabilities.
- Closing while WebView2 initializes hides the view and defers detachment until the native callback finishes. An actual window close waits for pending adapters. `WebViewInitialization` handles only exceptions originating from the pinned WebView assembly while initialization is pending; unrelated Hub exceptions remain unhandled normally.

## Verification (2026-11-10)

Host: **Windows 11 26H2**, build 26300, win-x64; detected WebView2 Runtime **157.0.4341.42**.

- Debug build: 0 warnings, 0 errors.
- Release self-contained win-x64 publish: succeeded.
- One minimal live-shell check uses an ephemeral loopback HTTP server and isolated browser profiles. It covers lazy creation/deduplication, native page/handle retention through reparenting, tab/column/page hiding, settings and drawer coverage, workspace isolation, rejected local protocols, ordinary close and immediate close during initialization. No outbound website is loaded by this fixture.
- Two earlier full `--verify-shell` runs passed **1771/1771**. Latest complete run: **1770/1771**; the browser contract passed. The failure is the separate commit/push retry handoff check (`Push retry needs user attention.`). That failure is preserved, not removed or fixed as part of this feature. Reports: `tmp/selfcheck-webview-final.txt` and `tmp/selfcheck-webview-final-run.log`.
- Linux/macOS live browser behavior and AppImage runtime discovery are **not verified** on this Windows host. The user approved Windows-first implementation with cross-platform acceptance deferred. Input method, zoom, scrolling, clipboard and native menus still need human review on each platform; compiled platform bindings are not acceptance evidence.

## Ubuntu 26.04 deadlock fix (2026-11-10)

Reported installation: **AppImage 0.27.0**. Investigation and source-build checks ran on **Ubuntu 26.04.1 LTS**, x86_64, Wayland with XWayland (`DISPLAY=:0`), GTK **3.24.52**, WebKitGTK **2.52.6**, and libsoup **3.6.6**. `GDK_BACKEND` was unset; the required libraries were installed.

The isolated loopback reproduction confirmed a navigation-policy deadlock, **not a stalled GTK initialization**. Stage logs reached adapter creation, native attachment return, and navigation request return before `UiStallWatch` recorded an unresponsive UI. GDB showed both the main and GTK threads waiting; `dotnet-stack` identified the cycle:

- UI: `NavigationStarted` → `WorkspaceBrowserView.SyncNavigation` → `CanGoBack` → `AvaloniaGtk.RunOnGlibThread` → waits for GTK.
- GTK: `GtkWebViewAdapter.DecidePolicy` → `WebViewDispatcher.Invoke` → waits for UI.

The pinned GTK adapter delivers the cancellable policy event synchronously. Hub now keeps the HTTP/HTTPS validation and cancellation synchronous, but posts the toolbar/history refresh to the dispatcher so the policy handler returns without querying GTK. This does not change adapter selection, transient browser-state isolation, or sandbox settings. Browser logs record detection, environment request, native attachment, adapter creation, navigation completion and initialization exceptions; explicit stage messages do not include URLs.

Verification:

- Debug and Release source builds: **0 warnings, 0 errors**, using a temporary .NET **10.0.403** SDK (runtime **10.0.14**, major roll-forward for the net8.0 app).
- The pre-fix reproduction stalled on first loopback navigation; after the fix the same drive script completed, the page reported success, and its run had no UI-stall alarm.
- The existing `CheckWorkspaceBrowserAsync` loopback fixture was invoked directly by a temporary launcher under ignored `tmp/`, without adding a permanent suite or changing existing assertions. **PASS**: lazy creation/deduplication, hiding, page/handle retention through expansion/restoration, overlays/drawer, workspace isolation, rejected local protocol, close and immediate close. Report: `tmp/webview-linux/lifetime-check-final.log`.
- The temporary launcher's early runs had an invalid fixture setup: its workspace was inside the protected data root, so workspace selection was refused and restoration/close assertions failed. Moving the fixture workspace outside that root produced the passing result; no product code or assertion was changed to bypass that failure.
- Full `--verify-shell`: **691/699 passed**, exit **1**; it stopped before the browser scene on existing text-selection checks and a Computer Use approval failure/exception (`ShellCheckWindow.Approvals.cs`). These failures remain unresolved and are outside this fix. Reports: `tmp/webview-linux/selfcheck-shell.txt` and `tmp/webview-linux/selfcheck-shell-run.log`.
- Human review of **Axmol Hub - WebView test** confirmed other URLs opened without hanging. The temporary launcher's `--verify-shell` startup also created an unshown check window whose 60-second watchdog shut down the desktop lifetime. A monitored repeat captured the watchdog failure, normal window closing events and desktop lifetime exit code **1** (the launcher incorrectly overwrote the process exit code to **0**). This explains the unexpected test-window exit; it is not evidence of a browser crash. Manual acceptance of all interactions remains incomplete. The installed AppImage was **not** replaced or repackaged; packaged-build acceptance remains pending.

## Windows package measurements

Local unsigned artifacts only, not installed or published. `ORCAROUTER_APP_ID=webview-size-test-only` is set only for temporary packaging; CI supplies the real value. The post-change package uses `installer/Build.ps1`; the saved pre-change self-contained publish is packed with the same pinned Velopack **1.2.161** settings. Both use version 0.26.0, pack identity and icon. All artifacts/logs are under ignored `tmp/webview-size/`.

| Artifact | Before (bytes) | After (bytes) | Increase (bytes) |
| --- | ---: | ---: | ---: |
| Published files (uncompressed) | 237,196,225 | 238,557,913 | 1,361,688 |
| Setup executable | 65,451,051 | 65,804,616 | 353,565 |
| Portable ZIP | 57,789,797 | 58,143,362 | 353,565 |
| Full update payload | 57,863,211 | 58,216,776 | 353,565 |

About **1.30 MiB** uncompressed / **345 KiB** compressed additional payload. The new assembly is `Avalonia.Controls.WebView.dll` (1,329,152 bytes); no Chromium/CEF/WebKit engine was added. Published-file size is a payload measure, **not a measured installed footprint**, and excludes the shared runtime, browser cache and temporary data. Runtime memory usage was not measured. Linux/macOS package deltas remain unmeasured.

Sources:

- https://www.nuget.org/packages/Avalonia.Controls.WebView/12.1.1
- https://github.com/AvaloniaUI/Avalonia.Controls.WebView/tree/12.1.1
- https://docs.avaloniaui.net/docs/app-development/embedding-web-content
