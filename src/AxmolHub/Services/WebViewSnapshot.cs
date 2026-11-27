using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>DIP bounds of the embedded WebView relative to its hosting top-level window.</summary>
internal readonly record struct WebViewWindowBounds(
    double X,
    double Y,
    double Width,
    double Height,
    double WindowWidth,
    double WindowHeight);

/// <summary>
/// Captures a PNG snapshot directly from the native WebView handle on macOS (WKWebView),
/// Windows (WebView2), and Linux (WebKitGTK), then decodes it into a <see cref="CapturedFrame"/>
/// for blank-frame checks and multimodal attachment.
/// </summary>
internal static class WebViewSnapshot
{
    internal static async Task<(CapturedFrame? Frame, string? Error)> CaptureAsync(
        IPlatformHandle? handle,
        WebViewWindowBounds? windowBounds,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<string>? log = null)
    {
        if (handle is null || handle.Handle == 0)
            return (null, "The embedded browser native view is not initialized yet.");

        // On macOS, WKWebView.takeSnapshotWithConfiguration omits CoreAnimation CABackdropLayer effects
        // (such as CSS backdrop-filter: blur). When the app already holds Screen Recording permission and
        // the WebView is visible in an on-screen window, prefer a WindowServer snapshot of our own window
        // cropped strictly to the WebView bounds; otherwise fall back silently to WKWebView's native snapshot
        // without ever prompting for Screen Recording permission.
        if (OperatingSystem.IsMacOS() && handle is IAppleWKWebViewPlatformHandle apple)
        {
            if (windowBounds is not { Width: > 0, Height: > 0, WindowWidth: > 0, WindowHeight: > 0 } rect)
                log?.Invoke("screenshot: using WKWebView snapshot (missing window bounds)");
            else if (!MacOsComputerPermissions.IsGranted(MacOsComputerPermission.ScreenRecording))
                log?.Invoke("screenshot: using WKWebView snapshot (Screen Recording permission not granted)");
            else
            {
                var composited = await MacSnapshot.TryCaptureCompositedAsync(apple.WKWebView, rect, timeout, cancellationToken, log);
                if (composited is not null && !composited.Stats.IsBlank())
                {
                    log?.Invoke($"screenshot: captured composited window crop ({composited.Width}x{composited.Height})");
                    return (composited, null);
                }
                log?.Invoke("screenshot: composited window crop unavailable or blank, falling back to WKWebView snapshot");
            }
        }

        (byte[]? png, string? error) = handle switch
        {
            IAppleWKWebViewPlatformHandle macHandle when OperatingSystem.IsMacOS() =>
                await MacSnapshot.CaptureAsync(macHandle.WKWebView, timeout, cancellationToken),
            IWindowsWebView2PlatformHandle win when OperatingSystem.IsWindows() =>
                await WindowsSnapshot.CaptureAsync(win.CoreWebView2, timeout, cancellationToken),
            IGtkWebViewPlatformHandle gtk when OperatingSystem.IsLinux() =>
                await LinuxSnapshot.CaptureAsync(gtk.WebKitWebView, timeout, cancellationToken),
            _ => (null, "Native browser screenshot capture is not supported for this WebView adapter."),
        };

        if (png is null || png.Length == 0)
            return (null, error ?? "The embedded browser returned no screenshot image.");

        var frame = DecodePng(png);
        if (frame is null)
            return (null, "The embedded browser screenshot could not be decoded as a PNG image.");

        return (frame, null);
    }

    private static CapturedFrame? DecodePng(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png, writable: false);
            using var bitmap = new Bitmap(stream);
            if (bitmap.PixelSize.Width <= 0 || bitmap.PixelSize.Height <= 0) return null;
            using var pixels = new WriteableBitmap(bitmap.PixelSize, bitmap.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var locked = pixels.Lock();
            bitmap.CopyPixels(locked);
            var bytes = new byte[locked.RowBytes * locked.Size.Height];
            Marshal.Copy(locked.Address, bytes, 0, bytes.Length);
            return new CapturedFrame(png, locked.Size.Width, locked.Size.Height,
                FrameAnalysis.Analyze(bytes, locked.Size.Width, locked.Size.Height, locked.RowBytes))
            {
                ContentFingerprint = FrameAnalysis.Fingerprint(bytes),
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<(byte[]? Png, string? Error)> WaitWithTimeoutAsync(
        Task<(byte[]? Png, string? Error)> task,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var delayCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(timeout, delayCancel.Token);
        var finished = await Task.WhenAny(task, delay);
        if (ReferenceEquals(finished, task))
        {
            delayCancel.Cancel();
            return await task;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return (null, $"Timed out after {(int)timeout.TotalSeconds}s waiting for the browser screenshot.");
    }

    // ── macOS: WKWebView.takeSnapshotWithConfiguration:completionHandler: ──────────────
    private static class MacSnapshot
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private const int BlockIsGlobal = 1 << 28;
        private const nuint NsBitmapImageFileTypePng = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct BlockDescriptor
        {
            public ulong Reserved;
            public ulong Size;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BlockLiteral
        {
            public nint Isa;
            public int Flags;
            public int Reserved;
            public nint Invoke;
            public nint Descriptor;
            public nint State;
        }

        private sealed class State(TaskCompletionSource<(byte[]? Png, string? Error)> tcs)
        {
            public TaskCompletionSource<(byte[]? Png, string? Error)> Tcs { get; } = tcs;
            public GCHandle BlockPin;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SnapshotCallback(nint block, nint nsImage, nint nsError);

        private static readonly SnapshotCallback Callback = OnSnapshot;
        private static readonly nint CallbackPtr = Marshal.GetFunctionPointerForDelegate(Callback);
        private static readonly int StateOffset = Marshal.OffsetOf<BlockLiteral>(nameof(BlockLiteral.State)).ToInt32();
        private static readonly nint GlobalBlockIsa = ResolveGlobalBlockIsa();
        private static readonly GCHandle DescriptorPin = GCHandle.Alloc(
            new BlockDescriptor { Reserved = 0, Size = (ulong)Marshal.SizeOf<BlockLiteral>() },
            GCHandleType.Pinned);

        private static nint ResolveGlobalBlockIsa()
        {
            if (!OperatingSystem.IsMacOS()) return 0;
            try
            {
                var lib = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
                return NativeLibrary.GetExport(lib, "_NSConcreteGlobalBlock");
            }
            catch (Exception)
            {
                return 0;
            }
        }

        internal static Task<(byte[]? Png, string? Error)> CaptureAsync(nint wkWebView, TimeSpan timeout, CancellationToken ct)
        {
            if (wkWebView == 0 || GlobalBlockIsa == 0)
                return Task.FromResult<(byte[]?, string?)>((null, "WKWebView snapshot runtime is unavailable."));

            var tcs = new TaskCompletionSource<(byte[]?, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var state = new State(tcs);
            var stateHandle = GCHandle.Alloc(state);
            var block = new BlockLiteral
            {
                Isa = GlobalBlockIsa,
                Flags = BlockIsGlobal,
                Reserved = 0,
                Invoke = CallbackPtr,
                Descriptor = DescriptorPin.AddrOfPinnedObject(),
                State = GCHandle.ToIntPtr(stateHandle),
            };
            // Keep both handles alive until WebKit invokes the completion callback (even on timeout) to avoid UAF.
            state.BlockPin = GCHandle.Alloc(block, GCHandleType.Pinned);
            try
            {
                SendSnapshot(wkWebView, Sel("takeSnapshotWithConfiguration:completionHandler:"), 0, state.BlockPin.AddrOfPinnedObject());
            }
            catch (Exception ex)
            {
                if (state.BlockPin.IsAllocated) state.BlockPin.Free();
                if (stateHandle.IsAllocated) stateHandle.Free();
                return Task.FromResult<(byte[]?, string?)>((null, $"WKWebView snapshot failed: {ex.Message}"));
            }
            return WaitWithTimeoutAsync(tcs.Task, timeout, ct);
        }

        internal static async Task<CapturedFrame?> TryCaptureCompositedAsync(
            nint wkWebView,
            WebViewWindowBounds bounds,
            TimeSpan timeout,
            CancellationToken ct,
            Action<string>? log)
        {
            if (wkWebView == 0) return null;
            long windowNumber;
            try
            {
                if (SendBool(wkWebView, Sel("isHiddenOrHasHiddenAncestor")) != 0)
                {
                    log?.Invoke("screenshot: composited skipped (WKWebView hidden)");
                    return null;
                }
                var nsWindow = Send(wkWebView, Sel("window"));
                if (nsWindow == 0
                    || SendBool(nsWindow, Sel("isVisible")) == 0
                    || SendBool(nsWindow, Sel("isMiniaturized")) != 0)
                {
                    log?.Invoke("screenshot: composited skipped (window not visible)");
                    return null;
                }
                windowNumber = Send(nsWindow, Sel("windowNumber"));
                if (windowNumber <= 0) return null;
            }
            catch (Exception ex)
            {
                log?.Invoke("screenshot: composited window lookup failed: " + ex.Message);
                return null;
            }

            var path = Path.Combine(Path.GetTempPath(), $"axmolhub-wv-{Guid.NewGuid():N}.png");
            try
            {
                var start = new ProcessStartInfo("/usr/sbin/screencapture")
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                start.ArgumentList.Add("-x");
                start.ArgumentList.Add("-o");
                start.ArgumentList.Add("-l");
                start.ArgumentList.Add(windowNumber.ToString(CultureInfo.InvariantCulture));
                start.ArgumentList.Add(path);

                using var process = Process.Start(start);
                if (process is null) return null;
                using var waitCancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waitCancel.CancelAfter(timeout);
                try
                {
                    await process.WaitForExitAsync(waitCancel.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                    if (ct.IsCancellationRequested) throw;
                    log?.Invoke("screenshot: composited timed out");
                    return null;
                }
                if (process.ExitCode != 0 || !File.Exists(path))
                {
                    log?.Invoke($"screenshot: screencapture exited with code {process.ExitCode}");
                    return null;
                }

                var fullPng = await File.ReadAllBytesAsync(path, ct);
                return CropWindowPng(fullPng, bounds, log);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log?.Invoke("screenshot: composited capture failed: " + ex.Message);
                return null;
            }
            finally
            {
                try { File.Delete(path); } catch (IOException) { }
            }
        }

        private static CapturedFrame? CropWindowPng(byte[] fullPng, WebViewWindowBounds bounds, Action<string>? log)
        {
            using var stream = new MemoryStream(fullPng, writable: false);
            using var full = new Bitmap(stream);
            if (full.PixelSize.Width <= 0 || full.PixelSize.Height <= 0) return null;
            var scaleX = full.PixelSize.Width / bounds.WindowWidth;
            var scaleY = full.PixelSize.Height / bounds.WindowHeight;
            if (scaleX < 0.5 || scaleX > 4.5 || Math.Abs(scaleX - scaleY) > 0.15)
            {
                log?.Invoke($"screenshot: composited scale mismatch ({full.PixelSize.Width}x{full.PixelSize.Height} vs {bounds.WindowWidth:0.#}x{bounds.WindowHeight:0.#})");
                return null;
            }

            var x = Math.Clamp((int)Math.Round(bounds.X * scaleX), 0, full.PixelSize.Width - 1);
            var y = Math.Clamp((int)Math.Round(bounds.Y * scaleY), 0, full.PixelSize.Height - 1);
            var w = Math.Clamp((int)Math.Round(bounds.Width * scaleX), 1, full.PixelSize.Width - x);
            var h = Math.Clamp((int)Math.Round(bounds.Height * scaleY), 1, full.PixelSize.Height - y);

            // Convert the decoded window PNG into canonical Bgra8888 via CopyPixels(ILockedFramebuffer), which
            // properly converts any source PNG format (including macOS RGBA8888/16-bit Display P3) without swapping R/B.
            using var staged = new WriteableBitmap(full.PixelSize, full.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var cropped = new WriteableBitmap(new PixelSize(w, h), full.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
            var copyRowBytes = w * 4;
            var cropBytes = new byte[copyRowBytes * h];
            using (var fullLock = staged.Lock())
            using (var cropLock = cropped.Lock())
            {
                full.CopyPixels(fullLock);
                var row = new byte[copyRowBytes];
                for (var yRow = 0; yRow < h; yRow++)
                {
                    Marshal.Copy(fullLock.Address + (y + yRow) * fullLock.RowBytes + x * 4, row, 0, copyRowBytes);
                    Marshal.Copy(row, 0, cropLock.Address + yRow * cropLock.RowBytes, copyRowBytes);
                    Buffer.BlockCopy(row, 0, cropBytes, yRow * copyRowBytes, copyRowBytes);
                }
            }
            using var encoded = new MemoryStream();
            cropped.Save(encoded, PngBitmapEncoderOptions.Default);
            return new CapturedFrame(encoded.ToArray(), w, h, FrameAnalysis.Analyze(cropBytes, w, h, copyRowBytes))
            {
                ContentFingerprint = FrameAnalysis.Fingerprint(cropBytes),
            };
        }

        private static void OnSnapshot(nint block, nint nsImage, nint nsError)
        {
            GCHandle stateHandle = default;
            State? state = null;
            byte[]? png = null;
            string? error = null;
            try
            {
                var statePtr = Marshal.ReadIntPtr(block, StateOffset);
                stateHandle = GCHandle.FromIntPtr(statePtr);
                state = stateHandle.Target as State;
                if (nsError != 0)
                {
                    var desc = Send(nsError, Sel("localizedDescription"));
                    var utf8 = desc != 0 ? Send(desc, Sel("UTF8String")) : 0;
                    error = Marshal.PtrToStringUTF8(utf8) ?? "WKWebView snapshot failed.";
                }
                else if (nsImage == 0)
                {
                    error = "WKWebView snapshot returned no image.";
                }
                else
                {
                    png = ConvertNsImageToPng(nsImage);
                    if (png is null) error = "Could not encode WKWebView snapshot as PNG.";
                }
            }
            catch (Exception ex)
            {
                // Never allow a managed exception to cross back into Objective-C.
                error = $"WKWebView snapshot callback failed: {ex.Message}";
            }
            finally
            {
                if (state is { BlockPin.IsAllocated: true }) state.BlockPin.Free();
                if (stateHandle.IsAllocated) stateHandle.Free();
                state?.Tcs.TrySetResult((png, error));
            }
        }

        private static byte[]? ConvertNsImageToPng(nint nsImage)
        {
            var cgImage = SendCgImage(nsImage, Sel("CGImageForProposedRect:context:hints:"), 0, 0, 0);
            if (cgImage == 0) return null;
            var rep = SendPtr(Send(Class("NSBitmapImageRep"), Sel("alloc")), Sel("initWithCGImage:"), cgImage);
            if (rep == 0) return null;
            try
            {
                var data = SendRep(rep, Sel("representationUsingType:properties:"), NsBitmapImageFileTypePng, 0);
                if (data == 0) return null;
                var length = checked((int)Send(data, Sel("length")));
                var bytesPtr = Send(data, Sel("bytes"));
                if (length <= 0 || bytesPtr == 0) return null;
                var png = new byte[length];
                Marshal.Copy(bytesPtr, png, 0, length);
                return png;
            }
            finally
            {
                Send(rep, Sel("release"));
            }
        }

        [DllImport(ObjC, EntryPoint = "objc_getClass")]
        private static extern nint Class(string name);
        [DllImport(ObjC, EntryPoint = "sel_registerName")]
        private static extern nint Sel(string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern nint Send(nint receiver, nint selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern byte SendBool(nint receiver, nint selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern nint SendPtr(nint receiver, nint selector, nint arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern void SendSnapshot(nint receiver, nint selector, nint config, nint block);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern nint SendCgImage(nint receiver, nint selector, nint rect, nint context, nint hints);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern nint SendRep(nint receiver, nint selector, nuint fileType, nint properties);
    }

    // ── Windows: ICoreWebView2::CapturePreview (vtable slot 30) ────────────────────────
    private static class WindowsSnapshot
    {
        private const int CoreWebView2CapturePreviewVtableIndex = 30;
        private const int CapturePreviewFormatPng = 0;
        private const int SOk = 0;
        private const int ENoInterface = unchecked((int)0x80004002);

        private static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");
        private static readonly Guid IidCapturePreviewHandler = new("697e05e9-3d8f-45fa-96f4-8ffe1ededaf5");

        private sealed class HandlerState(TaskCompletionSource<int> tcs)
        {
            public TaskCompletionSource<int> Tcs { get; } = tcs;
            public int RefCount = 1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HandlerObject
        {
            public nint VTable;
            public nint StateHandle;
        }

        private static readonly int HandlerStateOffset = Marshal.OffsetOf<HandlerObject>(nameof(HandlerObject.StateHandle)).ToInt32();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int QueryInterfaceFn(nint thisPtr, ref Guid riid, out nint ppv);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint AddRefReleaseFn(nint thisPtr);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int InvokeFn(nint thisPtr, int errorCode);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CapturePreviewFn(nint coreWebView2, int imageFormat, nint imageStream, nint handler);

        private static readonly QueryInterfaceFn QiDelegate = QueryInterface;
        private static readonly AddRefReleaseFn AddRefDelegate = AddRef;
        private static readonly AddRefReleaseFn ReleaseDelegate = Release;
        private static readonly InvokeFn InvokeDelegate = Invoke;
        private static readonly nint HandlerVTable = BuildVTable();

        private static nint BuildVTable()
        {
            var table = Marshal.AllocHGlobal(nint.Size * 4);
            Marshal.WriteIntPtr(table, 0, Marshal.GetFunctionPointerForDelegate(QiDelegate));
            Marshal.WriteIntPtr(table, nint.Size, Marshal.GetFunctionPointerForDelegate(AddRefDelegate));
            Marshal.WriteIntPtr(table, nint.Size * 2, Marshal.GetFunctionPointerForDelegate(ReleaseDelegate));
            Marshal.WriteIntPtr(table, nint.Size * 3, Marshal.GetFunctionPointerForDelegate(InvokeDelegate));
            return table;
        }

        internal static async Task<(byte[]? Png, string? Error)> CaptureAsync(nint coreWebView2, TimeSpan timeout, CancellationToken ct)
        {
            if (coreWebView2 == 0)
                return (null, "WebView2 CoreWebView2 handle is unavailable.");

            var hrStream = CreateStreamOnHGlobal(0, fDeleteOnRelease: true, out var streamPtr);
            if (hrStream != SOk || streamPtr == 0)
                return (null, $"Could not create in-memory stream (HRESULT 0x{hrStream:X8}).");

            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handlerPtr = CreateHandler(tcs);
            try
            {
                var vtable = Marshal.ReadIntPtr(coreWebView2);
                var capturePtr = Marshal.ReadIntPtr(vtable, CoreWebView2CapturePreviewVtableIndex * nint.Size);
                var capture = Marshal.GetDelegateForFunctionPointer<CapturePreviewFn>(capturePtr);
                var hr = capture(coreWebView2, CapturePreviewFormatPng, streamPtr, handlerPtr);
                // Release our initial ref; WebView2 holds its own reference while CapturePreview is in flight.
                Release(handlerPtr);
                handlerPtr = 0;
                if (hr != SOk)
                    return (null, $"WebView2 CapturePreview failed to start (HRESULT 0x{hr:X8}).");

                var waitTask = WaitHrAsync(tcs.Task, streamPtr);
                return await WaitWithTimeoutAsync(waitTask, timeout, ct);
            }
            catch (Exception ex)
            {
                if (handlerPtr != 0) Release(handlerPtr);
                return (null, $"WebView2 CapturePreview failed: {ex.Message}");
            }
            finally
            {
                Marshal.Release(streamPtr);
            }
        }

        private static async Task<(byte[]? Png, string? Error)> WaitHrAsync(Task<int> hrTask, nint streamPtr)
        {
            var hr = await hrTask;
            if (hr != SOk)
                return (null, $"WebView2 CapturePreview failed (HRESULT 0x{hr:X8}).");
            var png = ReadStreamBytes(streamPtr);
            return png is { Length: > 0 }
                ? (png, null)
                : (null, "WebView2 CapturePreview returned an empty image stream.");
        }

        private static byte[]? ReadStreamBytes(nint streamPtr)
        {
            if (GetHGlobalFromStream(streamPtr, out var hGlobal) != SOk || hGlobal == 0) return null;
            var size = GlobalSize(hGlobal);
            if (size == 0 || size > int.MaxValue) return null;
            var locked = GlobalLock(hGlobal);
            if (locked == 0) return null;
            try
            {
                var bytes = new byte[(int)size];
                Marshal.Copy(locked, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }
        }

        private static nint CreateHandler(TaskCompletionSource<int> tcs)
        {
            var stateHandle = GCHandle.Alloc(new HandlerState(tcs));
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<HandlerObject>());
            Marshal.WriteIntPtr(ptr, 0, HandlerVTable);
            Marshal.WriteIntPtr(ptr, HandlerStateOffset, GCHandle.ToIntPtr(stateHandle));
            return ptr;
        }

        private static int QueryInterface(nint thisPtr, ref Guid riid, out nint ppv)
        {
            if (riid == IidIUnknown || riid == IidCapturePreviewHandler)
            {
                AddRef(thisPtr);
                ppv = thisPtr;
                return SOk;
            }
            ppv = 0;
            return ENoInterface;
        }

        private static uint AddRef(nint thisPtr)
        {
            var statePtr = Marshal.ReadIntPtr(thisPtr, HandlerStateOffset);
            if (statePtr == 0 || GCHandle.FromIntPtr(statePtr).Target is not HandlerState state) return 0;
            return (uint)Interlocked.Increment(ref state.RefCount);
        }

        private static uint Release(nint thisPtr)
        {
            var statePtr = Marshal.ReadIntPtr(thisPtr, HandlerStateOffset);
            if (statePtr == 0) return 0;
            var handle = GCHandle.FromIntPtr(statePtr);
            if (handle.Target is not HandlerState state) return 0;
            var remaining = Interlocked.Decrement(ref state.RefCount);
            if (remaining == 0)
            {
                if (handle.IsAllocated) handle.Free();
                Marshal.FreeHGlobal(thisPtr);
            }
            return (uint)Math.Max(0, remaining);
        }

        private static int Invoke(nint thisPtr, int errorCode)
        {
            try
            {
                var statePtr = Marshal.ReadIntPtr(thisPtr, HandlerStateOffset);
                if (statePtr != 0 && GCHandle.FromIntPtr(statePtr).Target is HandlerState state)
                    state.Tcs.TrySetResult(errorCode);
            }
            catch (Exception)
            {
                // Never propagate managed exceptions across a COM boundary.
            }
            return SOk;
        }

        [DllImport("ole32.dll")]
        private static extern int CreateStreamOnHGlobal(nint hGlobal, [MarshalAs(UnmanagedType.Bool)] bool fDeleteOnRelease, out nint ppstm);
        [DllImport("ole32.dll")]
        private static extern int GetHGlobalFromStream(nint pstm, out nint phglobal);
        [DllImport("kernel32.dll")]
        private static extern nuint GlobalSize(nint hMem);
        [DllImport("kernel32.dll")]
        private static extern nint GlobalLock(nint hMem);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalUnlock(nint hMem);
    }

    // ── Linux: WebKitGTK webkit_web_view_get_snapshot + cairo PNG stream ───────────────
    private static class LinuxSnapshot
    {
        private const string GLib = "libglib-2.0.so.0";
        private const string WebKit = "libwebkit2gtk-4.1.so.0";
        private const string Cairo = "libcairo.so.2";
        private const int WebKitSnapshotRegionVisible = 0;
        private const int WebKitSnapshotOptionsNone = 0;
        private const int CairoStatusSuccess = 0;
        private const int CairoStatusWriteError = 11;

        private sealed class State(nint webView, TaskCompletionSource<(byte[]? Png, string? Error)> tcs)
        {
            public nint WebView { get; } = webView;
            public TaskCompletionSource<(byte[]? Png, string? Error)> Tcs { get; } = tcs;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GSourceFunc(nint userData);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GAsyncReadyCallback(nint sourceObject, nint asyncResult, nint userData);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CairoWriteFunc(nint closure, nint data, uint length);

        private static readonly GSourceFunc IdleCallback = OnIdle;
        private static readonly GAsyncReadyCallback ReadyCallback = OnReady;
        private static readonly CairoWriteFunc WriteCallback = OnCairoWrite;

        internal static Task<(byte[]? Png, string? Error)> CaptureAsync(nint webKitWebView, TimeSpan timeout, CancellationToken ct)
        {
            if (webKitWebView == 0)
                return Task.FromResult<(byte[]?, string?)>((null, "WebKitGTK view handle is unavailable."));

            var tcs = new TaskCompletionSource<(byte[]?, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handle = GCHandle.Alloc(new State(webKitWebView, tcs));
            try
            {
                g_idle_add(IdleCallback, GCHandle.ToIntPtr(handle));
            }
            catch (Exception ex)
            {
                if (handle.IsAllocated) handle.Free();
                return Task.FromResult<(byte[]?, string?)>((null, $"WebKitGTK snapshot could not be scheduled: {ex.Message}"));
            }
            return WaitWithTimeoutAsync(tcs.Task, timeout, ct);
        }

        private static int OnIdle(nint userData)
        {
            try
            {
                var handle = GCHandle.FromIntPtr(userData);
                if (handle.Target is State state)
                {
                    webkit_web_view_get_snapshot(state.WebView, WebKitSnapshotRegionVisible,
                        WebKitSnapshotOptionsNone, 0, ReadyCallback, userData);
                    return 0; // G_SOURCE_REMOVE; handle is freed in OnReady.
                }
            }
            catch (Exception ex)
            {
                var handle = GCHandle.FromIntPtr(userData);
                if (handle.Target is State state) state.Tcs.TrySetResult((null, $"WebKitGTK snapshot failed: {ex.Message}"));
                if (handle.IsAllocated) handle.Free();
            }
            return 0;
        }

        private static void OnReady(nint sourceObject, nint asyncResult, nint userData)
        {
            GCHandle handle = default;
            State? state = null;
            byte[]? png = null;
            string? error = null;
            try
            {
                handle = GCHandle.FromIntPtr(userData);
                state = handle.Target as State;
                if (state is null) return;
                var surface = webkit_web_view_get_snapshot_finish(state.WebView, asyncResult, out var gError);
                if (gError != 0)
                {
                    g_error_free(gError);
                    error = "WebKitGTK snapshot returned an error.";
                }
                else if (surface == 0)
                {
                    error = "WebKitGTK snapshot returned an empty surface.";
                }
                else
                {
                    try
                    {
                        using var stream = new MemoryStream();
                        var streamHandle = GCHandle.Alloc(stream);
                        try
                        {
                            var status = cairo_surface_write_to_png_stream(surface, WriteCallback, GCHandle.ToIntPtr(streamHandle));
                            if (status == CairoStatusSuccess && stream.Length > 0) png = stream.ToArray();
                            else error = $"Could not encode WebKitGTK snapshot as PNG (cairo status {status}).";
                        }
                        finally
                        {
                            streamHandle.Free();
                        }
                    }
                    finally
                    {
                        cairo_surface_destroy(surface);
                    }
                }
            }
            catch (Exception ex)
            {
                error = $"WebKitGTK snapshot callback failed: {ex.Message}";
            }
            finally
            {
                if (handle.IsAllocated) handle.Free();
                state?.Tcs.TrySetResult((png, error));
            }
        }

        private static int OnCairoWrite(nint closure, nint data, uint length)
        {
            try
            {
                if (length == 0) return CairoStatusSuccess;
                var stream = (MemoryStream)GCHandle.FromIntPtr(closure).Target!;
                var chunk = new byte[length];
                Marshal.Copy(data, chunk, 0, (int)length);
                stream.Write(chunk, 0, chunk.Length);
                return CairoStatusSuccess;
            }
            catch (Exception)
            {
                return CairoStatusWriteError;
            }
        }

        [DllImport(GLib)]
        private static extern uint g_idle_add(GSourceFunc function, nint data);
        [DllImport(GLib)]
        private static extern void g_error_free(nint error);
        [DllImport(WebKit)]
        private static extern void webkit_web_view_get_snapshot(
            nint webView, int region, int options, nint cancellable, GAsyncReadyCallback callback, nint userData);
        [DllImport(WebKit)]
        private static extern nint webkit_web_view_get_snapshot_finish(nint webView, nint result, out nint error);
        [DllImport(Cairo)]
        private static extern int cairo_surface_write_to_png_stream(nint surface, CairoWriteFunc writeFunc, nint closure);
        [DllImport(Cairo)]
        private static extern void cairo_surface_destroy(nint surface);
    }
}
