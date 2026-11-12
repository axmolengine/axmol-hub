using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>One process-wide portal lease; a different conversation never inherits another's selected source.</summary>
internal sealed class LinuxWaylandRemoteDesktop : IDisposable
{
    private Process? _process;
    private string? _conversation, _application, _frameId;
    private CapturedFrame? _previous;
    private int _logicalWidth, _logicalHeight;
    private long _recorded, _generation;
    private ulong _mappingRevision;
    private uint _devices;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal static readonly LinuxWaylandRemoteDesktop Shared = new();
    internal static bool IsWayland => OperatingSystem.IsLinux()
        && (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            || string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase));
    internal PortalComputerBridge Bridge(string conversation) => new((r, ct) => ExecuteAsync(conversation, r, ct));

    private async Task<PortalComputerResult> ExecuteAsync(string conversation, PortalComputerRequest r, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return new(null, "Refused: another portal operation is active.");
        try
        {
            if (r.Action != "screenshot")
            {
                if (_process is null || _process.HasExited || _conversation != conversation || _application != r.Application
                    || _frameId is null || _frameId != r.FrameId || Environment.TickCount64 - _recorded > 120_000
                    || _generation != ComputerDesktop.Generation)
                    return new(null, "Refused: portal input needs the latest frame_id from this conversation and requested window. Observe again.");
                _frameId = null;
                var previousMapping = _mappingRevision;
                var previousLogicalWidth = _logicalWidth;
                var previousLogicalHeight = _logicalHeight;
                var current = await GrabAsync(ct);
                if (_mappingRevision != previousMapping || _logicalWidth != previousLogicalWidth || _logicalHeight != previousLogicalHeight
                    || _previous is null || current.Width != _previous.Width || current.Height != _previous.Height
                    || current.ContentFingerprint != _previous.ContentFingerprint || current.Stats.IsBlank())
                    return new(null, "Refused: shared-window content or dimensions changed. Observe again before input.");
                if (r.StillApproved?.Invoke() != true)
                    return new(null, "Refused: portal confirmation was revoked before input.");
                var requiredDevice = r.Action is "click" or "scroll" ? 2u : 1u;
                if ((_devices & requiredDevice) == 0)
                    return new(null, "Refused: the user did not grant this input device. Observe before requesting a new session.");
                ComputerDesktop.Invalidate();
                switch (r.Action)
                {
                    case "click":
                        if (r.X < 0 || r.Y < 0 || r.X >= current.Width || r.Y >= current.Height)
                            return new(null, "Refused: coordinates are outside the screenshot.");
                        if (_logicalWidth <= 0 || _logicalHeight <= 0)
                            return new(null, "Refused: the portal did not provide logical coordinates. Pointer control is unavailable.");
                        await SendAsync(FormattableString.Invariant($"CLICK {_mappingRevision} {r.X} {r.Y}"), ct);
                        break;
                    case "scroll":
                        if (_logicalWidth <= 0 || _logicalHeight <= 0)
                            return new(null, "Refused: the portal did not provide logical coordinates. Pointer control is unavailable.");
                        await SendAsync(FormattableString.Invariant($"SCROLL {_mappingRevision} {r.Scroll}"), ct);
                        break;
                    case "press_key":
                        if (!TryKeysym(r.Key, out var symbol)) return new(null, "Refused: unsupported key name.");
                        await SendAsync(FormattableString.Invariant($"KEY {_mappingRevision} {symbol}"), ct);
                        break;
                    case "type":
                        foreach (var rune in r.Text!.EnumerateRunes())
                        {
                            ct.ThrowIfCancellationRequested();
                            if (r.StillApproved?.Invoke() != true)
                                throw new InvalidOperationException("Portal confirmation was revoked during text input.");
                            var sym = rune.Value <= 255 ? (uint)rune.Value : 0x01000000u | (uint)rune.Value;
                            if (rune.Value == '\n') sym = 0xff0d;
                            if (rune.Value == '\t') sym = 0xff09;
                            await SendAsync(FormattableString.Invariant($"KEY {_mappingRevision} {sym}"), ct);
                        }
                        break;
                }
            }
            else if (_process is null || _process.HasExited || _conversation != conversation || _application != r.Application)
            {
                Close();
                var helper = Path.Combine(AppContext.BaseDirectory, "axmolhub-portal-helper");
                if (!File.Exists(helper)) throw new InvalidOperationException(HubTexts.Get("PortalHelperMissing", HubTexts.EnglishLanguage));
                var start = new ProcessStartInfo(helper)
                {
                    UseShellExecute = false, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                };
                try { _process = Process.Start(start) ?? throw new InvalidOperationException("The portal helper did not start."); }
                catch (System.ComponentModel.Win32Exception ex)
                { throw new InvalidOperationException("The Linux portal helper could not start. Check its executable permission and system GIO/PipeWire libraries.", ex); }
                _process.ErrorDataReceived += (_, _) => { }; // Drain diagnostics without retaining desktop-service output.
                _process.BeginErrorReadLine();
                var ready = await ReadLineAsync(TimeSpan.FromSeconds(125), ct);
                if (!ready.StartsWith("READY ", StringComparison.Ordinal)
                    || !uint.TryParse(ready.AsSpan(6), out _devices)) throw Failure(ready);
                _conversation = conversation;
                _application = r.Application;
            }
            var frame = await GrabAsync(ct);
            if (frame.Stats.IsBlank()) { Close(); return new(null, "Refused: portal returned a blank frame. The sharing session was closed."); }
            _previous = frame;
            _frameId = Guid.NewGuid().ToString("N");
            _recorded = Environment.TickCount64;
            _generation = ComputerDesktop.Generation;
            return new(frame, (r.Action == "screenshot" ? "Observed portal-selected window." : $"Input delivered ({r.Action}); application outcome is unverified.")
                + $"\nframe_id={_frameId}; one input only; expires in two minutes. Keyboard focus is not verified; stop sharing using the desktop indicator to revoke access.");
        }
        catch
        {
            Close();
            throw;
        }
        finally { _gate.Release(); }
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
        await WriteLineAsync(command, ct);
        var response = await ReadLineAsync(TimeSpan.FromSeconds(12), ct);
        if (response != "OK") throw Failure(response);
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
    private void Close()
    {
        _frameId = null; _previous = null; _conversation = _application = null;
        if (_process is { } process)
        {
            try
            {
                if (!process.HasExited)
                {
                    // Let the helper close Request/Session and release keys before the bounded hard-stop fallback.
                    Signal(process.Id, 15);
                    if (!process.WaitForExit(1000)) process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            process.Dispose(); _process = null;
        }
        _devices = 0;
    }
    [DllImport("libc", EntryPoint = "kill")] private static extern int Signal(int pid, int signal);
    public void Dispose() => Close();
}
