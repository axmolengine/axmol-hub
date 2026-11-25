using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>One portal lease per coordinator; desktop operations share the process-wide Core gate.</summary>
internal sealed class LinuxWaylandRemoteDesktop : IDisposable
{
    private Process? _process;
    private string? _conversation, _application, _frameId;
    private CapturedFrame? _previous;
    private int _logicalWidth, _logicalHeight;
    private long _recorded, _generation;
    private ulong _mappingRevision;
    private uint _devices;
    private bool _clipboard;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<CancellationToken, Task<bool>> _hubIsActive;
    private readonly object _ownerGate = new();
    private string? _operationOwner;
    private CancellationTokenSource? _operationStop;
    private bool _notifyEnd = true;
    internal Action<string>? SessionEnded { get; set; }

    internal void CloseFor(string conversation, bool notify = true)
    {
        lock (_ownerGate)
        {
            if (_operationOwner != conversation && _conversation != conversation) return;
            _notifyEnd = notify;
            _frameId = null;
            _operationStop?.Cancel();
        }
        if (!_gate.Wait(0)) return;
        // Revoke on the UI thread immediately; process cleanup must not block its controls.
        _ = Task.Run(() =>
        {
            try { Close(notify); }
            finally { _gate.Release(); }
        });
    }

    internal LinuxWaylandRemoteDesktop(Func<CancellationToken, Task<bool>>? hubIsActive = null)
        => _hubIsActive = hubIsActive ?? HubIsActiveAsync;

    private static async Task<bool> HubIsActiveAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Only our own activation state is knowable without a compositor-specific API. Being inactive
        // does NOT prove that the selected stream owns keyboard focus.
        return await Dispatcher.UIThread.InvokeAsync(() =>
            Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.Windows.Any(window => window.IsActive));
    }

    internal static bool IsWayland => OperatingSystem.IsLinux()
        && (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            || string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase));
    internal PortalComputerBridge Bridge(string conversation) => new((r, ct) => ExecuteAsync(conversation, r, ct));

    private async Task<PortalComputerResult> ExecuteAsync(string conversation, PortalComputerRequest r, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return new(null, "Refused: another portal operation is active.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_ownerGate) { _operationOwner = conversation; _operationStop = stop; _notifyEnd = true; }
        ct = stop.Token;
        try
        {
            CapturedFrame? beforeInput = null;
            if (r.StillApproved?.Invoke() != true) return new(null, "Refused: portal permission changed before execution.");
            if (r.Action == "press_key" && !TryKeysym(r.Key, out _)) return new(null, "Refused [unsupported]: unknown Linux key.");
            if (r.Action == "type" && !ValidUnicode(r.Text!)) return new(null, "Refused [unsupported]: invalid Unicode text.");
            if (r.Action != "screenshot")
            {
                if (_process is null || _process.HasExited || _conversation != conversation || _application != r.Application
                    || _frameId is null || _frameId != r.FrameId || Environment.TickCount64 - _recorded > 120_000
                    || _generation != ComputerDesktop.Generation)
                    return new(null, "Refused: portal input needs the latest frame_id from this conversation and requested window. Observe again.");
                if (r.Action == "type" && !_clipboard && r.Text!.Any(c => c > 127))
                    return new(null, "Refused [unsupported]: literal Unicode input needs temporary continuous interaction with Portal Clipboard permission. No characters were sent; request fresh sharing with that option enabled.");
                _frameId = null;
                // Only our own inactivity is observable. Never infer the selected app's focus from it.
                if (await _hubIsActive(ct) && !await WaitForHandoffAsync(r, ct))
                    return new(null, "Refused: Hub remained active during the 15-second handoff. Switch to the intended app and observe again; no input was sent.");
                if (r.StillApproved?.Invoke() != true)
                    return new(null, "Refused: portal confirmation was revoked during the focus handoff.");
                if (await _hubIsActive(ct))
                    return new(null, "Refused: Focus returned to Hub after the handoff. Focus the intended app and request a fresh screenshot; no input was sent.");
                if (Environment.TickCount64 - _recorded > 120_000 || _generation != ComputerDesktop.Generation)
                    return new(null, "Refused: the observation expired or was invalidated during the handoff. Observe again.");
                var previousMapping = _mappingRevision;
                var previousLogicalWidth = _logicalWidth;
                var previousLogicalHeight = _logicalHeight;
                var current = await GrabAsync(ct);
                if (_mappingRevision != previousMapping || _logicalWidth != previousLogicalWidth || _logicalHeight != previousLogicalHeight
                    || _previous is null || current.Width != _previous.Width || current.Height != _previous.Height
                    || current.Stats.IsBlank())
                    return new(null, "Refused: shared-window dimensions or coordinate mapping changed, or capture was blank. Observe again before input.");
                if (r.StillApproved?.Invoke() != true)
                    return new(null, "Refused: portal confirmation was revoked before input.");
                var pointerAction = r.Action is "move" or "click" or "double_click" or "drag" or "scroll";
                var requiredDevice = pointerAction ? 2u : 1u;
                if ((_devices & requiredDevice) == 0)
                    return new(null, "Refused: the user did not grant this input device. Observe before requesting a new session.");
                if (await _hubIsActive(ct))
                    return new(null, "Refused: focus returned to Hub during verification. No input was sent; observe again.");
                ct.ThrowIfCancellationRequested();
                if (r.StillApproved?.Invoke() != true)
                    return new(null, "Refused: portal confirmation was revoked before delivery.");
                beforeInput = current;
                ComputerDesktop.Invalidate();
                if (pointerAction && (_logicalWidth <= 0 || _logicalHeight <= 0))
                    return new(null, "Refused: the portal did not provide logical coordinates. Pointer control is unavailable.");
                var x = r.Action == "scroll" && r.X == -1 && r.Y == -1 ? current.Width / 2 : r.X;
                var y = r.Action == "scroll" && r.X == -1 && r.Y == -1 ? current.Height / 2 : r.Y;
                bool Inside(int px, int py) => px >= 0 && py >= 0 && px < current.Width && py < current.Height;
                if (pointerAction && (!Inside(x, y) || r.Action == "drag" && !Inside(r.EndX, r.EndY)))
                    return new(null, "Refused: pointer endpoints are outside the screenshot.");
                await DeliverAsync(r with { X = x, Y = y }, ct);
            }
            else if (_process is null || _process.HasExited || _conversation != conversation || _application != r.Application)
            {
                Close(notify: false);
                var helper = Path.Combine(AppContext.BaseDirectory, "axmolhub-portal-helper");
                if (!File.Exists(helper)) throw new InvalidOperationException(HubTexts.Get("PortalHelperMissing", HubTexts.EnglishLanguage));
                await ProbeAsync(helper, ct);
                var start = new ProcessStartInfo(helper)
                {
                    UseShellExecute = false, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                };
                if (r.ContinuousInteraction) start.ArgumentList.Add("--clipboard");
                try { _process = Process.Start(start) ?? throw new InvalidOperationException("The portal helper did not start."); }
                catch (System.ComponentModel.Win32Exception ex)
                { throw new InvalidOperationException("The Linux portal helper could not start. Check its executable permission and system GIO/PipeWire libraries.", ex); }
                _process.ErrorDataReceived += (_, _) => { }; // Drain diagnostics without retaining desktop-service output.
                _process.BeginErrorReadLine();
                var ready = await ReadLineAsync(TimeSpan.FromSeconds(125), ct);
                var capabilities = ready.Split(' ');
                if (capabilities.Length != 3 || capabilities[0] != "READY"
                    || !uint.TryParse(capabilities[1], out _devices)
                    || capabilities[2] is not ("0" or "1")) throw Failure(ready);
                _clipboard = capabilities[2] == "1";
                _conversation = conversation;
                _application = r.Application;
                var selectedProcess = _process;
                selectedProcess.Exited += (_, _) =>
                {
                    lock (_ownerGate)
                    {
                        if (!ReferenceEquals(_process, selectedProcess)) return;
                        _frameId = null;
                        _operationStop?.Cancel();
                        if (_notifyEnd) SessionEnded?.Invoke(conversation);
                    }
                };
                selectedProcess.EnableRaisingEvents = true;
            }
            var frame = beforeInput is null ? await GrabAsync(ct) : await ObserveAfterInputAsync(beforeInput, ct);
            if (frame.Stats.IsBlank()) { Close(); return new(null, "Refused: portal returned a blank frame. The sharing session was closed."); }
            _previous = frame;
            _frameId = Guid.NewGuid().ToString("N");
            _recorded = Environment.TickCount64;
            _generation = ComputerDesktop.Generation;
            return new(frame, (r.Action == "screenshot" ? "Observed portal-selected window." : $"Input delivered ({r.Action}); application outcome is unverified.")
                + (r.Action == "type" && _clipboard ? " Portal UTF-8 clipboard transfer acknowledged; clipboard replaced. Inspect the document to verify insertion." : "")
                + (r.Action == "screenshot" ? _clipboard ? " Text transport: Portal Clipboard paste (replaces clipboard)." : " Text transport: ASCII keys only; Unicode needs Portal Clipboard permission." : "")
                + (beforeInput is not null && SameContent(beforeInput, frame) ? " No visible change observed; inspect the new frame and do not replay the action." : "")
                + $"\nframe_id={_frameId}; one input only; expires in two minutes. Keyboard focus is not verified; stop sharing using the desktop indicator to revoke access.");
        }
        catch
        {
            Close(_notifyEnd);
            throw;
        }
        finally
        {
            bool cleanup;
            lock (_ownerGate)
            {
                cleanup = stop.IsCancellationRequested;
                if (cleanup) _frameId = null;
                else { _operationStop = null; _operationOwner = null; _gate.Release(); }
            }
            if (cleanup)
            {
                Close(_notifyEnd);
                lock (_ownerGate) { _operationStop = null; _operationOwner = null; }
                _gate.Release();
            }
        }
    }

    private static bool ValidUnicode(string text)
    {
        var remaining = text.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (System.Text.Rune.DecodeFromUtf16(remaining, out var rune, out var count) != System.Buffers.OperationStatus.Done
                || rune.Value == 0) return false;
            remaining = remaining[count..];
        }
        return true;
    }

    private async Task DeliverAsync(PortalComputerRequest r, CancellationToken ct)
    {
        async Task Send(string command)
        {
            ct.ThrowIfCancellationRequested();
            if (r.StillApproved?.Invoke() != true || await _hubIsActive(ct))
                throw new InvalidOperationException("Permission or Hub focus changed; further input stopped.");
            r.InputDeliveryStarted?.Invoke();
            await SendAsync(command, ct);
        }
        string Move(int x, int y) => FormattableString.Invariant($"MOVE {_mappingRevision} {x} {y}");
        if (r.Action is "move" or "click" or "double_click" or "drag" or "scroll")
        {
            await Send(Move(r.X, r.Y));
            if (r.Action == "move") return;
            if (r.Action == "scroll")
            {
                await Send(FormattableString.Invariant($"AXIS {_mappingRevision} {r.Scroll} {r.HorizontalScroll}"));
                return;
            }
            if (r.Action == "drag")
            {
                try
                {
                    await Send(FormattableString.Invariant($"BUTTON {_mappingRevision} 1"));
                    var steps = Math.Clamp(r.DurationMs / 20, 5, 150);
                    for (var i = 1; i <= steps; i++)
                    {
                        await Task.Delay(r.DurationMs / steps, ct);
                        await Send(Move(r.X + (r.EndX - r.X) * i / steps, r.Y + (r.EndY - r.Y) * i / steps));
                    }
                }
                finally { await ReleaseAsync(); }
                return;
            }
            var count = r.Action == "double_click" ? 2 : 1;
            for (var i = 0; i < count; i++)
            {
                try { await Send(FormattableString.Invariant($"BUTTON {_mappingRevision} 1")); }
                finally { await ReleaseAsync(); }
                if (i + 1 < count) await Task.Delay(50, ct);
            }
            return;
        }
        if (r.Action == "press_key")
        {
            if (!TryKeysym(r.Key, out var symbol)) throw new InvalidOperationException("Unsupported Linux key name.");
            try
            {
                foreach (var modifier in r.Modifiers)
                {
                    var sym = modifier switch { "control" => 0xffe3u, "alt" => 0xffe9u, "shift" => 0xffe1u, _ => throw new InvalidOperationException("Unsupported modifier.") };
                    await Send(FormattableString.Invariant($"DOWN {_mappingRevision} {sym}"));
                }
                await Send(FormattableString.Invariant($"KEY {_mappingRevision} {symbol}"));
            }
            finally { await ReleaseAsync(); }
            return;
        }
        if (_clipboard)
        {
            var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(TextEol.ToLf(r.Text!)));
            await Send(FormattableString.Invariant($"TEXT {_mappingRevision} {encoded}"));
            try
            {
                await Send(FormattableString.Invariant($"DOWN {_mappingRevision} {0xffe3u}"));
                await Send(FormattableString.Invariant($"KEY {_mappingRevision} {0x76u}"));
            }
            finally { await ReleaseAsync(); }
            await Send("TEXT_DONE");
            return;
        }
        foreach (var rune in TextEol.ToLf(r.Text!).EnumerateRunes())
        {
            var sym = rune.Value <= 255 ? (uint)rune.Value : 0x01000000u | (uint)rune.Value;
            if (rune.Value == '\n') sym = 0xff0d;
            if (rune.Value == '\t') sym = 0xff09;
            await Send(FormattableString.Invariant($"KEY {_mappingRevision} {sym}"));
        }
    }

    private async Task ReleaseAsync()
    {
        if (_process is null) return;
        // Releases must outlive cancellation and stale geometry, but are still bounded by helper shutdown.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await SendAsync("RELEASE", deadline.Token); }
        catch { Close(); throw; }
    }

    private async Task<bool> WaitForHandoffAsync(PortalComputerRequest request, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + 15_000;
        long inactiveSince = 0;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (request.StillApproved?.Invoke() != true)
                throw new InvalidOperationException("Portal confirmation was revoked during the focus handoff. No input was sent.");
            if (await _hubIsActive(ct)) inactiveSince = 0;
            else if (inactiveSince == 0) inactiveSince = Environment.TickCount64;
            else if (Environment.TickCount64 - inactiveSince >= 300) return true;
            await Task.Delay(100, ct);
        }
        return false;
    }

    private static async Task ProbeAsync(string helper, CancellationToken ct)
    {
        var start = new ProcessStartInfo(helper)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--probe");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The Linux portal capability probe did not start.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var errors = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            var result = (await output).Trim();
            await errors;
            if (process.ExitCode != 0 || !result.StartsWith("CAPABILITIES window ", StringComparison.Ordinal))
                throw Failure(result);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static bool SameContent(CapturedFrame a, CapturedFrame b)
        => a.Width == b.Width && a.Height == b.Height && a.ContentFingerprint == b.ContentFingerprint;

    private async Task<CapturedFrame> ObserveAfterInputAsync(CapturedFrame before, CancellationToken ct)
    {
        // Notify* acknowledges event delivery, not the target application's redraw. Give its event loop
        // time, then take at most three fresh captures. A change still does not prove task success.
        for (var attempt = 0; ; ++attempt)
        {
            await Task.Delay(200, ct);
            var frame = await GrabAsync(ct);
            if (frame.Stats.IsBlank() || !SameContent(before, frame) || attempt == 2) return frame;
        }
    }

    private async Task<CapturedFrame> GrabAsync(CancellationToken ct)
    {
        await WriteLineAsync("GRAB", ct);
        var header = (await ReadLineAsync(TimeSpan.FromSeconds(10), ct)).Split(' ');
        if (header.Length != 7 || header[0] != "FRAME2"
            || !int.TryParse(header[1], out var w) || !int.TryParse(header[2], out var h)
            || !int.TryParse(header[3], out var lw) || !int.TryParse(header[4], out var lh)
            || !int.TryParse(header[5], out var count) || !ulong.TryParse(header[6], out var revision) || revision == 0
            || w <= 0 || h <= 0
            || (long)w * h * 4 != count || count > 64 * 1024 * 1024)
            throw Failure(string.Join(' ', header));
        var pixels = new byte[count];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await _process!.StandardOutput.BaseStream.ReadExactlyAsync(pixels, timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException(HubTexts.Get("PortalFrameTimeout", HubTexts.EnglishLanguage)); }
        _logicalWidth = lw; _logicalHeight = lh; _mappingRevision = revision;
        using var bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var buffer = bitmap.Lock())
            for (var y = 0; y < h; ++y) Marshal.Copy(pixels, y * w * 4, buffer.Address + y * buffer.RowBytes, w * 4);
        using var png = new MemoryStream();
        bitmap.Save(png, PngBitmapEncoderOptions.Default);
        return new(png.ToArray(), w, h, FrameAnalysis.Analyze(pixels, w, h, w * 4))
        { ContentFingerprint = FrameAnalysis.Fingerprint(pixels) };
    }

    private async Task SendAsync(string command, CancellationToken ct)
    {
        try
        {
            await WriteLineAsync(command, ct);
            var response = await ReadLineAsync(TimeSpan.FromSeconds(12), ct);
            if (response != "OK") throw Failure(response);
        }
        catch
        {
            // A cancelled read leaves an ambiguous response in the pipe. Close and let the helper
            // release its tracked inputs; never let a cleanup command consume that earlier response.
            Close(_notifyEnd);
            throw;
        }
    }
    private async Task WriteLineAsync(string command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await _process!.StandardInput.WriteAsync((command + "\n").AsMemory(), ct);
        await _process.StandardInput.FlushAsync(ct);
    }
    private async Task<string> ReadLineAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var bytes = new List<byte>(128);
        var one = new byte[1];
        while (bytes.Count < 1024)
        {
            int read;
            try { read = await _process!.StandardOutput.BaseStream.ReadAsync(one, deadline.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new TimeoutException(HubTexts.Get("PortalFrameTimeout", HubTexts.EnglishLanguage)); }
            if (read == 0)
                throw new InvalidOperationException(HubTexts.Get("PortalDisconnected", HubTexts.EnglishLanguage));
            if (one[0] == '\n') return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add(one[0]);
        }
        throw new InvalidOperationException("Invalid portal helper response.");
    }
    private static InvalidOperationException Failure(string code) => new(code switch
    {
        "ERROR window-sources-unavailable" or "ERROR non-window-source-refused" => HubTexts.Get("PortalWindowMissing", HubTexts.EnglishLanguage),
        "ERROR session-bus-unavailable" => "The desktop session D-Bus is unavailable. Start Hub in the graphical user session.",
        "ERROR portal-unavailable" => "The desktop portal service is unavailable. Install and start the portal backend for this desktop.",
        "ERROR remotedesktop-unavailable" => "This desktop portal does not advertise RemoteDesktop. Window control is unavailable; no global-input fallback is permitted.",
        "ERROR input-devices-unavailable" => "This desktop portal offers neither keyboard nor pointer input.",
        "ERROR text-transfer-unverified" => "Portal clipboard text transfer was not acknowledged. The clipboard may have changed and paste may have been delivered; observe the document, do not replay.",
        "ERROR frame-mapping-changed" => "The shared window crop or orientation changed. Observe again before input.",
        "ERROR frame-timeout" => HubTexts.Get("PortalFrameTimeout", HubTexts.EnglishLanguage),
        "ERROR session-closed" or "ERROR stream-disconnected" => HubTexts.Get("PortalDisconnected", HubTexts.EnglishLanguage),
        _ => "Portal request failed or was cancelled (" + code + "). Check desktop portal permissions before retrying.",
    });
    private static bool TryKeysym(string? key, out uint symbol)
    {
        var name = (key ?? "").Trim().ToLowerInvariant();
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0])) { symbol = name[0]; return true; }
        symbol = name switch
        {
            "enter" or "return" => 0xff0d, "tab" => 0xff09, "escape" or "esc" => 0xff1b,
            "left" => 0xff51, "up" => 0xff52, "right" => 0xff53, "down" => 0xff54,
            "backspace" => 0xff08, "delete" => 0xffff, "space" => 0x20,
            "home" => 0xff50, "end" => 0xff57, "pageup" => 0xff55, "pagedown" => 0xff56, _ => 0,
        };
        return symbol != 0;
    }
    private void Close(bool notify = true)
    {
        var conversation = _conversation;
        _frameId = null; _previous = null; _conversation = _application = null;
        var process = Interlocked.Exchange(ref _process, null);
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    // Release pending inputs and close the session before a bounded hard-stop fallback.
                    Signal(process.Id, 15);
                    if (!process.WaitForExit(6000)) process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            process.Dispose();
        }
        _devices = 0; _clipboard = false;
        if (notify && conversation is not null) SessionEnded?.Invoke(conversation);
    }
    [DllImport("libc", EntryPoint = "kill")] private static extern int Signal(int pid, int signal);
    public void Dispose()
    {
        string? owner;
        lock (_ownerGate) owner = _operationOwner ?? _conversation;
        if (owner is not null) CloseFor(owner);
    }
}
