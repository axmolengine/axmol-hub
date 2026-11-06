using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;
using static AxmolHub.ThemeProbe;
using System.Net;
using System.Text;
using System.Text.Json;
using AxmolHub.Agent;
using Microsoft.Extensions.AI;
using Avalonia.Controls.Primitives;

namespace AxmolHub;
internal static class ShellCheckSupport
{
    internal static Conversation StartAgentSession(ChatWorkspace chat, string? providerId = null)
    {
        var session = chat.StartConversation(providerId);
        if (session.Mode != ChatModes.Agent) chat.SelectMode(ChatModes.Agent);
        return session;
    }

    internal static int DifferingPixels(string left, string right)
    {
        var a = SmokeCapture.ReadBgra(left, out var aw, out var ah, out var aStride);
        var b = SmokeCapture.ReadBgra(right, out var bw, out var bh, out var bStride);
        if (aw != bw || ah != bh || aStride != bStride || a.Length != b.Length) return -1;
        var differing = 0;
        for (var index = 0; index < a.Length; index++)
        {
            if (a[index] != b[index]) differing++;
        }

        return differing;
    }

    internal static TaskCompletionSource<bool> PeerGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static bool IsUiFontStack(string family)
        => family.Contains("Segoe UI", StringComparison.Ordinal)
           && family.Contains("Microsoft YaHei UI", StringComparison.Ordinal);
    internal static int FixturePixels(string png)
    {
        using var bitmap = new Bitmap(png);
        var size = bitmap.PixelSize;
        using var staging = new WriteableBitmap(size, bitmap.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var framebuffer = staging.Lock();
        bitmap.CopyPixels(framebuffer);

        var pixels = new byte[framebuffer.RowBytes * size.Height];
        System.Runtime.InteropServices.Marshal.Copy(framebuffer.Address, pixels, 0, pixels.Length);

        var found = 0;
        for (var offset = 0; offset + 3 < pixels.Length; offset += 4)
        {
            if (pixels[offset + 3] != 0xFF) continue;
            if (Math.Abs(pixels[offset] - 0x30) > 6 || Math.Abs(pixels[offset + 1] - 0x60) > 6) continue;
            found++;
        }

        return found;
    }

    internal static int StoredPictureCount(ChatPanel panel, string conversationId)
    {
        var directory = panel.SessionImageDirectoryForCheck(conversationId);
        return System.IO.Directory.Exists(directory) ? System.IO.Directory.GetFiles(directory).Length : -1;
    }

    internal static byte[] PictureFixture(int width = 8, int height = 8)
    {
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 0x30;
            pixels[offset + 1] = 0x60;
            pixels[offset + 2] = (offset / 4) % 2 == 0 ? (byte)0xC0 : (byte)0x18;
            pixels[offset + 3] = 0xFF;
        }

        using var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var frame = bitmap.Lock())
        {
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, frame.Address, pixels.Length);
        }

        using var stream = new System.IO.MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    internal static Dictionary<string, object?> PeerSend(string target, string text, bool wake) => new()
    {
        ["target"] = target,
        ["text"] = text,
        ["wake"] = wake,
    };
    internal const string Bars = "\uFF5C\uFF5CDSML\uFF5C\uFF5C";
    internal static string LeakedCall { get; } = string.Join("\n",
        $"<{Bars} calls>",
        $"<{Bars} invoke name=\"fs_grep\">",
        $"<{Bars} parameter name=\"path\" string=\"true\">.agents/memory</{Bars} parameter>",
        $"</{Bars} invoke>",
        $"</{Bars} calls>");

    internal static string LeakedCallAfter(string prose) => prose + "\n\n" + LeakedCall;
    internal static async Task<string?> WaitForPendingCallAsync(Conversation session)
    {
        for (var wait = 0; wait < 400; wait++)
        {
            Dispatcher.UIThread.RunJobs();
            var callId = session.Messages.LastOrDefault(turn => turn.Role == ChatRoles.Assistant
                && turn.ToolCallId is { Length: > 0 }
                && turn.ApprovalState == ChatApprovalStates.Pending)?.ToolCallId;
            if (callId is not null) return callId;
            await Task.Delay(2);
        }

        return null;
    }

    internal static async Task<bool> WaitForStaleDeadlineAsync(ChatWorkspace chat, string conversationId)
    {
        for (var wait = 0; wait < 400; wait++)
        {
            Dispatcher.UIThread.RunJobs();
            if (chat.RunFor(conversationId)?.Token.IsCancellationRequested == true) return true;
            await Task.Delay(2);
        }

        return false;
    }

    internal static async Task<bool> WaitForConditionAsync(Func<bool> condition)
    {
        for (var wait = 0; wait < 500; wait++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            await Task.Delay(2);
        }

        return false;
    }

    internal static int SummaryBoundary(Conversation conversation)
        => Math.Clamp(conversation.ContextSummaryThroughMessageCount, 0, conversation.Messages.Count);
    internal static bool EveryToolCallAnswered(Conversation conversation)
    {
        var answered = conversation.Messages
            .Where(turn => turn.Role == ChatRoles.Tool && turn.ToolCallId is { Length: > 0 })
            .Select(turn => turn.ToolCallId)
            .ToHashSet(StringComparer.Ordinal);
        return conversation.Messages
            .Where(turn => turn.Role == ChatRoles.Assistant && turn.ToolCallId is { Length: > 0 })
            .All(turn => answered.Contains(turn.ToolCallId));
    }

    internal static int _callSequence;
    internal static string NextCallId(string prefix)
        => prefix + "-" + Interlocked.Increment(ref _callSequence);
    internal sealed class ApprovalChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public const string Answer = "批准之后接着说的话";
        /// <summary>The options of the request that was just made, so a check can read what would have gone on
        /// the wire — the same field <see cref = "ScriptedChatClient"/> keeps, for the same reason.</summary>
        public Microsoft.Extensions.AI.ChatOptions? LastOptions { get; private set; }
        public int CallsRemaining { get; set; } = 1;
        public int Requests { get; private set; }
        /// <summary>Optional provider failure after the first tool-call response, for retry-streak checks.</summary>
        public Exception? FollowUpException { get; set; }
        /// <summary>What the response says before it asks, emitted as text ahead of the call — the shape a model
        /// writes an answer in: the prose and the call arrive as one assistant message and Hub files both on one
        /// turn. Said once, on the response that carries the call.</summary>
        public string? Said { get; set; }
        /// <summary>The call the fixture asks for. A real write by default: the gate is what is under test, and a
        /// read-only call would sail through it without proving anything.</summary>
        public string ToolName { get; set; } = "file_write";
        private static (string Name, Dictionary<string, object?> Arguments) CurrentFileCall(
            string name, Dictionary<string, object?> arguments)
        {
            if (name != "file_write" || !arguments.TryGetValue("old_string", out var old)) return (name, arguments);
            if (old is string { Length: 0 })
                return ("fs_write", new Dictionary<string, object?>
                { ["path"] = arguments.GetValueOrDefault("path"), ["content"] = arguments.GetValueOrDefault("new_string") });
            return ("fs_replace", new Dictionary<string, object?> { ["edits"] = new[] { arguments } });
        }
        public Dictionary<string, object?> Arguments { get; set; } = new();
        /// <summary>The arguments of the call about to be filed, asked instead of <see cref = "Arguments"/> when
        /// set. A fixture that needs several results out of one tool has to vary its signature: the spin guard
        /// refuses the third byte-identical call in a window on purpose, so a check that is about something else
        /// cannot ask five times for the same path.</summary>
        public Func<int, Dictionary<string, object?>>? ArgumentsByCall { get; set; }
        /// <summary>Calls asked for in <b>one</b> response, used instead of <see cref = "ToolName"/> when set. A real
        /// gateway hands back several calls from a single reply however the request asked it not to — that is the
        /// shape that stranded an approved result at the wrong end of the transcript, and no number of
        /// one-call-per-response fixtures can reach it.</summary>
        public List<(string Name, Dictionary<string, object?> Arguments)> Batch { get; set; } = [];
        /// <summary>What the response thinks before it asks, emitted as reasoning content ahead of the calls. A
        /// thinking model produces <b>one</b> block per response and can ask for several tools inside it — the
        /// shape that used to leave calls 2..N filed with no thinking to replay, and the request rejected.</summary>
        public List<string> Thinking { get; set; } = [];
        /// <summary>How many times the fixture actually answered. A parked turn attempts a follow-up request and
        /// dies in it, so counting requests would say nothing about whether a reply was produced.</summary>
        public int Answers { get; private set; }
        /// <summary>A task held before the <b>second</b> request answers, so a check can act on the transcript
        /// while the run is still calling tools. What a person does to an open fold between one call and the next
        /// is exactly the state a rebuild used to erase, and no fixture that answered instantly could reach it.
        /// Cancelled like a real stream, because a check that parks has to be able to be stopped parking it.</summary>
        public Task? ParkBeforeFollowUp { get; set; }
        /// <summary>Signalled once the stream has reached that park, so the check waits for the position rather
        /// than guessing at a delay.</summary>
        public System.Threading.Tasks.TaskCompletionSource<bool>? FollowUpReached { get; set; }
        /// <summary>A second park, before the <b>answer</b> rather than before the next call. The window some
        /// checks have to stand in — the last call's row is on screen and the run is still going — closes the
        /// moment this fixture answers, so a check racing it passes or fails on scheduler luck. Both are optional
        /// and unset for every other caller, which is why adding them changes nothing else.</summary>
        public Task? ParkBeforeAnswer { get; set; }
        /// <summary>Signalled once the stream has reached that second park.</summary>
        public System.Threading.Tasks.TaskCompletionSource<bool>? AnswerReached { get; set; }

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException("The approval check only uses the streaming path.");
        public async System.Collections.Generic.IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests++;
            LastOptions = options;
            if (Requests >= 2 && FollowUpException is { } failure) throw failure;
            if (Requests >= 2)
            {
                FollowUpReached?.TrySetResult(true);
                if (ParkBeforeFollowUp is { } park)
                    await park.WaitAsync(cancellationToken);
            }

            if (Requests >= 3)
            {
                AnswerReached?.TrySetResult(true);
                if (ParkBeforeAnswer is { } park)
                    await park.WaitAsync(cancellationToken);
            }

            if (Thinking.Count > 0)
            {
                // One block, once: it belongs to the response this call is about to answer.
                foreach (var said in Thinking)
                    yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, [new Microsoft.Extensions.AI.TextReasoningContent(said)]);
                Thinking.Clear();
            }

            if (Said is { Length: > 0 } prose)
            {
                // Ahead of the call rather than after it: the prose is what the model wrote before asking, and
                // the workspace files it on the call's own turn from the live buffer.
                Said = null;
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, prose);
            }

            if (Batch.Count > 0)
            {
                var batch = new List<Microsoft.Extensions.AI.AIContent>();
                for (var index = 0; index < Batch.Count; index++)
                {
                    var item = CurrentFileCall(Batch[index].Name, Batch[index].Arguments);
                    batch.Add(new Microsoft.Extensions.AI.FunctionCallContent(NextCallId($"call-{Requests}-{index}"), item.Name, item.Arguments));
                }
                Batch.Clear();
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, batch);
            }
            else if (CallsRemaining > 0)
            {
                CallsRemaining--;
                var item = CurrentFileCall(ToolName, ArgumentsByCall is { } byCall ? byCall(Requests) : Arguments);
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, [new Microsoft.Extensions.AI.FunctionCallContent(NextCallId("call-" + Requests), item.Name, item.Arguments)]);
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, Answer);
            }

            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose()
        {
        }
    }

    internal static async Task WaitForRunAsync(ChatWorkspace chat, string conversationId)
    {
        for (var wait = 0; wait < 200 && chat.IsRunning(conversationId); wait++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
    }

    internal static Task WaitForSignalAsync(Task signal) => Task.WhenAny(signal, Task.Delay(TimeSpan.FromSeconds(3)));
    internal static async Task WaitForIdleAsync(ChatWorkspace chat)
    {
        for (var wait = 0; wait < 200 && chat.RunningCount > 0; wait++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
    }

    internal static async Task WaitUntilPostedAsync(Func<bool> landed)
    {
        for (var tick = 0; tick < 100 && !landed(); tick++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Dispatcher.UIThread.RunJobs();
    }

    internal static async Task WaitUntilAsync(Func<bool> settled, int maxTicks = 400)
    {
        for (var wait = 0; wait < maxTicks && !settled(); wait++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
    }

    internal static int ElapsedSeconds(string label)
    {
        var text = label.Trim();
        if (text.Length == 0 || !text.EndsWith('s')) return -1;
        var parts = text[..^1].Split('m', StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out var minutes) && int.TryParse(parts[1], out var tail))
            return minutes * 60 + tail;
        return parts.Length == 1 && int.TryParse(parts[0], out var seconds) ? seconds : -1;
    }

    internal sealed class StalledChatClient(TimeSpan silence) : Microsoft.Extensions.AI.IChatClient
    {
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException("The assistant check only uses the streaming path.");
        public async System.Collections.Generic.IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(silence, cancellationToken);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose()
        {
        }
    }

    internal sealed class StepGate : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(initialCount: 0);
        internal Task Wait(CancellationToken cancellationToken = default) => _gate.WaitAsync(cancellationToken);
        internal void Release() => _gate.Release();
        public void Dispose() => _gate.Dispose();
    }

    internal sealed class ReasoningTrickleChatClient(int deltas, TimeSpan gap, TimeSpan silence, string? text = null, string? unit = null, Task? afterTextGate = null, Func<CancellationToken, Task>? pace = null) : Microsoft.Extensions.AI.IChatClient
    {
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException("The assistant check only uses the streaming path.");
        public async System.Collections.Generic.IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // A check that wants the reasoning to overflow its bounded box needs more than a label per delta, and
            // the length is the thing under test rather than the wording — so the caller chooses it.
            var piece = unit is { Length: > 0 } ? unit : "思考片段";
            for (var index = 0; index < deltas; index++)
            {
                // A pace the caller drives replaces the delay outright: the check decides when the next chunk of
                // thinking arrives, so it can assert on a state it chose rather than on a moment it caught.
                if (pace is { })
                    await pace(cancellationToken);
                else
                    await Task.Delay(gap, cancellationToken);
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, [new Microsoft.Extensions.AI.TextReasoningContent($"{piece}{index + 1}，")]);
            }

            await Task.Delay(silence, cancellationToken);
            if (text is { Length: > 0 })
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, text);
            // Held open past the first prose on purpose. A check that wants the state of the bubble <i>while</i>
            // the answer is arriving cannot get it from a stream that ends in the same tick: by the time it looks,
            // the run is over and the live row it meant to read has been thrown away with the bubble.
            if (afterTextGate is not null)
                await afterTextGate.WaitAsync(cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose()
        {
        }
    }

    internal sealed class SpawnAskingChatClient(string phase) : Microsoft.Extensions.AI.IChatClient
    {
        private int _calls;
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException("The assistant check only uses the streaming path.");
        public async System.Collections.Generic.IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _calls++;
            if (_calls <= 2)
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, [new Microsoft.Extensions.AI.FunctionCallContent(NextCallId($"spawn-{_calls}"), "spawn_session", new Dictionary<string, object?> { ["task"] = $"把两个断言文件读完，只回 5 行结论，然后发回派生你的那条会话（{phase}）", ["mode"] = "agent", ["inherit_workspace"] = false, })]);
            else
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, "我已经自己看过一遍了。");
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose()
        {
        }
    }

    internal sealed class ToolCallingChatClient : Microsoft.Extensions.AI.IChatClient
    {
        private int _calls;
        /// <summary>Held before the first response is produced. The loop's second request is answered moments
        /// after the first returns, which is no window a check can drive a steer into; parking the first response
        /// instead leaves the steer queued before the boundary arrives, which is exactly the case worth proving.</summary>
        public Task? HoldFirstResponse { get; init; }
        /// <summary>The messages of the request that was just made. The loop asks again after each tool result, so
        /// reading this after a two-iteration turn says what the model actually saw on its second look — which is
        /// the only way a check can tell a steer that landed mid-loop from one that waited for the turn to end.</summary>
        public System.Collections.Generic.IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>? LastMessages { get; private set; }

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException("The assistant check only uses the streaming path.");
        public async System.Collections.Generic.IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastMessages = messages.ToList();
            _calls++;
            if (_calls == 1 && HoldFirstResponse is { } hold)
                await hold.ConfigureAwait(false);
            if (_calls == 1)
            {
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, "先说的话");
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, [new Microsoft.Extensions.AI.FunctionCallContent(NextCallId("call-ui"), "project_list", new Dictionary<string, object?>())]);
            }
            else
            {
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, "后说的话");
            }

            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose()
        {
        }
    }

    internal sealed class UndeclaredCallChatClient : Microsoft.Extensions.AI.IChatClient
    {
        private int _round;
        public int Round => _round;
        /// <summary>The trailing user message of the last request, where the correction is delivered.</summary>
        public string? LastUser { get; private set; }

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException("The undeclared-call check only uses the streaming path.");
        public async System.Collections.Generic.IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            LastUser = list.LastOrDefault() is { Role: var role } last
                && role == Microsoft.Extensions.AI.ChatRole.User ? last.Text : null;
            _round++;
            if (_round == 1)
            {
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, [new Microsoft.Extensions.AI.FunctionCallContent(NextCallId("call-undeclared"), "write", new Dictionary<string, object?> { ["path"] = "docs/plan.md" })]);
            }
            else
            {
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, "用 file_write 写完了");
            }

            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose()
        {
        }
    }

    internal sealed class NarratedCallChatClient(Task secondRound, string narration) : Microsoft.Extensions.AI.IChatClient
    {
        private int _calls;
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException("The handoff check only uses the streaming path.");
        public async System.Collections.Generic.IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _calls++;
            if (_calls == 1)
            {
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, narration);
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, [new Microsoft.Extensions.AI.FunctionCallContent(NextCallId("call-handoff"), "project_list", new Dictionary<string, object?>())]);
            }
            else
            {
                await secondRound.ConfigureAwait(false);
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, "第二轮才开口");
            }

            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose()
        {
        }
    }

    internal sealed class TwoRoundThinkingChatClient(TaskCompletionSource<bool> firstRoundReady, Task releaseFirstRound) : Microsoft.Extensions.AI.IChatClient
    {
        private int _calls;
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException("The duration check only uses the streaming path.");
        public async System.Collections.Generic.IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _calls++;
            if (_calls == 1)
            {
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, [new Microsoft.Extensions.AI.TextReasoningContent("第一轮想了一下。")]);
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, "先查一下工程列表");
                firstRoundReady.TrySetResult(true);
                await releaseFirstRound.WaitAsync(cancellationToken).ConfigureAwait(false);
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, [new Microsoft.Extensions.AI.FunctionCallContent(NextCallId("call-duration"), "project_list", new Dictionary<string, object?>())]);
            }
            else
            {
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, [new Microsoft.Extensions.AI.TextReasoningContent("第二轮又想了一下。")]);
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, "两轮都想完了");
            }

            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose()
        {
        }
    }

    internal static string FmtPoint(PixelPoint point) => point.X + "," + point.Y;
    internal static PixelPoint? ScreenCentreOf(Visual control, MainWindow shell)
        => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), shell)
           is { } centre
            ? shell.PointToScreen(centre)
            : null;
    internal static bool FillsOrMaximizes(MainWindow shell)
        => shell.WindowState == WindowState.Maximized || shell.FillsWorkAreaForCheck;
    internal static Point BarPressPoint(Border dragArea, MainWindow shell)
        => dragArea.TranslatePoint(new Point(dragArea.Bounds.Width / 2, dragArea.Bounds.Height / 2), shell) ?? default;
    internal static void PressTitleBar(Border dragArea, MainWindow shell, int clickCount)
        => dragArea.RaiseEvent(new PointerPressedEventArgs(
            dragArea, CheckPointer, shell, BarPressPoint(dragArea, shell), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, clickCount));
    internal static readonly Avalonia.Input.Pointer CheckPointer = new(0, PointerType.Mouse, isPrimary: true);
    internal static bool SameRect(Rect first, Rect second, double tolerance = 0.5)
        => Math.Abs(first.X - second.X) <= tolerance
           && Math.Abs(first.Y - second.Y) <= tolerance
           && Math.Abs(first.Width - second.Width) <= tolerance
           && Math.Abs(first.Height - second.Height) <= tolerance;
    internal static byte[] RegionPixels(string png, Rect region, Size client)
    {
        using var bitmap = new Bitmap(png);
        var size = bitmap.PixelSize;
        if (client.Width <= 0 || client.Height <= 0 || size.Width == 0 || size.Height == 0) return [];

        var scaleX = size.Width / client.Width;
        var scaleY = size.Height / client.Height;
        var left = Math.Clamp((int)Math.Floor(region.X * scaleX), 0, size.Width - 1);
        var right = Math.Clamp((int)Math.Ceiling(region.Right * scaleX), left + 1, size.Width);
        var top = Math.Clamp((int)Math.Floor(region.Y * scaleY), 0, size.Height - 1);
        var bottom = Math.Clamp((int)Math.Ceiling(region.Bottom * scaleY), top + 1, size.Height);

        using var staging = new WriteableBitmap(size, bitmap.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var framebuffer = staging.Lock();
        bitmap.CopyPixels(framebuffer);

        var rowBytes = framebuffer.RowBytes;
        var all = new byte[rowBytes * size.Height];
        Marshal.Copy(framebuffer.Address, all, 0, all.Length);

        var region4 = new byte[(right - left) * 4 * (bottom - top)];
        var at = 0;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var offset = (y * rowBytes) + (x * 4);
                region4[at++] = all[offset];
                region4[at++] = all[offset + 1];
                region4[at++] = all[offset + 2];
                region4[at++] = all[offset + 3];
            }
        }
        return region4;
    }

    internal static string Fmt(double value) => value.ToString("F2", CultureInfo.InvariantCulture);
    internal static bool GlyphInkIsCentered(string png, Avalonia.Media.Color glyph,
        out double dx, out double dy, out double inkWidth, out double inkHeight)
    {
        var pixels = SmokeCapture.ReadBgra(png, out var width, out var height, out var stride);
        const int tolerance = 24;
        var minX = width;
        var minY = height;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * stride) + (x * 4);
                var alpha = pixels[o + 3];
                if (alpha < 96) continue;
                if (Math.Abs(pixels[o] * 255.0 / alpha - glyph.B) > tolerance
                    || Math.Abs(pixels[o + 1] * 255.0 / alpha - glyph.G) > tolerance
                    || Math.Abs(pixels[o + 2] * 255.0 / alpha - glyph.R) > tolerance) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        dx = dy = inkWidth = inkHeight = 0;
        if (maxX < minX || maxY < minY) return false;
        dx = (minX + maxX) / 2.0 - (width - 1) / 2.0;
        dy = (minY + maxY) / 2.0 - (height - 1) / 2.0;
        inkWidth = maxX - minX + 1;
        inkHeight = maxY - minY + 1;
        return true;
    }

    internal static bool StopGlyphCornersAreRounded(string png, Avalonia.Media.Color plate,
        Avalonia.Media.Color glyph, out double cornerInk, out double edgeInk)
    {
        cornerInk = 0;
        edgeInk = 1;

        var pixels = SmokeCapture.ReadBgra(png, out var width, out var height, out var stride);
        if (width < 8 || height < 8) return false;

        var dr = glyph.R - plate.R;
        var dg = glyph.G - plate.G;
        var db = glyph.B - plate.B;
        var lengthSquared = ((double)dr * dr) + ((double)dg * dg) + ((double)db * db);
        if (lengthSquared <= 0) return false;

        // The pixel's ink share, projected onto the plate→glyph line: 0 is bare plate, 1 is the glyph's colour.
        double Ink(int x, int y)
        {
            var o = (y * stride) + (x * 4);
            if (pixels[o + 3] < 250) return 0;
            var f = (((pixels[o + 2] - plate.R) * dr) + ((pixels[o + 1] - plate.G) * dg)
                     + ((pixels[o] - plate.B) * db)) / lengthSquared;
            return Math.Clamp(f, 0, 1);
        }

        var ink = new double[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++) ink[(y * width) + x] = Ink(x, y);
        }

        // Walking inward along one pixel line, the first pixel at half coverage or more carries the edge on its own
        // outer side, so the edge's position follows from that pixel's own coverage.
        static bool EdgeOf(double[] coverage, int from, int step, out double edge)
        {
            for (var i = from; i >= 0 && i < coverage.Length; i += step)
            {
                if (coverage[i] < 0.5) continue;
                edge = step > 0 ? i + (1 - coverage[i]) : i + coverage[i];
                return true;
            }

            edge = 0;
            return false;
        }

        var middleRow = new double[width];
        for (var x = 0; x < width; x++) middleRow[x] = ink[((height / 2) * width) + x];
        var middleColumn = new double[height];
        for (var y = 0; y < height; y++) middleColumn[y] = ink[(y * width) + (width / 2)];

        if (!EdgeOf(middleRow, 0, 1, out var left) || !EdgeOf(middleRow, width - 1, -1, out var right)
            || !EdgeOf(middleColumn, 0, 1, out var top) || !EdgeOf(middleColumn, height - 1, -1, out var bottom))
            return false;
        if (right - left < 4 || bottom - top < 4) return false;

        // The outermost pixel columns and rows the box touches, and a window of two pixels per probe so that one
        // half-covered pixel cannot decide the answer by itself.
        var lowX = (int)Math.Floor(left);
        var highX = (int)Math.Ceiling(right) - 1;
        var lowY = (int)Math.Floor(top);
        var highY = (int)Math.Ceiling(bottom) - 1;
        if (highX - lowX < 3 || highY - lowY < 3 || highX >= width || highY >= height) return false;
        var nearLowX = lowX + 1;
        var nearHighX = highX - 1;
        var nearLowY = lowY + 1;
        var nearHighY = highY - 1;
        // The middle of an edge, clamped so the window stays inside the outermost two columns and rows.
        var middleX = Math.Clamp((int)Math.Floor((left + right) / 2), lowX + 1, highX - 1);
        var middleY = Math.Clamp((int)Math.Floor((top + bottom) / 2), lowY + 1, highY - 1);

        // How much of this pixel the glyph's box covers: outside the box the plate is still there, and a light
        // pixel there says nothing about a corner.
        double BoxFraction(int x, int y)
        {
            var fx = Math.Min(x + 1.0, right) - Math.Max((double)x, left);
            var fy = Math.Min(y + 1.0, bottom) - Math.Max((double)y, top);
            return Math.Max(0, fx) * Math.Max(0, fy);
        }

        // A probe's score: the emptiest (corners — one square corner is enough to fail) or the average (edges)
        // share of ink over the pixels that hold enough of the box to mean anything.
        double Score(int x0, int x1, int y0, int y1, bool emptiest)
        {
            var total = 0.0;
            var count = 0;
            var lowest = 1.0;
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    var box = BoxFraction(x, y);
                    if (box < 0.25) continue;
                    var share = Ink(x, y) / box;
                    if (share < lowest) lowest = share;
                    total += share;
                    count++;
                }
            }

            if (count == 0) return -1;
            return emptiest ? lowest : total / count;
        }

        var corners = new[]
        {
            Score(lowX, nearLowX, lowY, nearLowY, true),
            Score(nearHighX, highX, lowY, nearLowY, true),
            Score(lowX, nearLowX, nearHighY, highY, true),
            Score(nearHighX, highX, nearHighY, highY, true),
        };
        var edges = new[]
        {
            Score(middleX, middleX + 1, lowY, nearLowY, false),
            Score(middleX, middleX + 1, nearHighY, highY, false),
            Score(lowX, nearLowX, middleY, middleY + 1, false),
            Score(nearHighX, highX, middleY, middleY + 1, false),
        };
        if (corners.Any(value => value < 0) || edges.Any(value => value < 0)) return false;

        cornerInk = corners.Max();
        edgeInk = edges.Min();
        // 0.60 sits between what the two shapes measure (cut corners 0.00–0.25, square corners 1.00) with room on
        // both sides, and 0.85 lets the antialiased rim of a full edge through without letting a shrunken square
        // pass the second half.
        return cornerInk <= 0.6 && edgeInk >= 0.85;
    }

    internal static int CountCredentials(ChatWorkspace chat, string providerId)
        => chat.Credentials.Count(credential => credential.ProviderId == providerId);
    internal sealed class DeepSeekApiProbeHandler : System.Net.Http.HttpMessageHandler
    {
        public Queue<(System.Net.HttpStatusCode Status, string Body)> Responses { get; } = new();
        public List<(string Url, string Authorization, string Body)> Requests { get; } = [];
        public bool CancelNext { get; set; }

        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri?.ToString() ?? "", request.Headers.Authorization?.ToString() ?? "", body));
            if (CancelNext)
            {
                CancelNext = false;
                throw new OperationCanceledException("Probe timeout");
            }

            var answer = Responses.Dequeue();
            return new System.Net.Http.HttpResponseMessage(answer.Status)
            {
                Content = new System.Net.Http.StringContent(answer.Body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    internal sealed class OAuthProbeHandler : System.Net.Http.HttpMessageHandler
    {
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.Contains("openid-configuration", StringComparison.Ordinal) == true)
            {
                var json = """{"authorization_endpoint":"https://example.test/auth","token_endpoint":"https://example.test/token"}""";
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json"),
                };
            }

            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Forbidden);
        }
    }

    internal sealed class DeviceCodeProbeHandler(string tokenBody) : System.Net.Http.HttpMessageHandler
    {
        public const string UserCode = "ABCD-1234";
        public const string VerificationUri = "https://example.test/verify-device";
        /// <summary>Every request this handler saw, in order: "METHOD path body".</summary>
        public List<string> Requests { get; } = [];

        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method.Method + " " + path + " " + body).TrimEnd());
            var payload = path.EndsWith("/login/device/code", StringComparison.Ordinal) ? $$"""
                  {"device_code":"dc-never-display-this","user_code":"{{UserCode}}",
                   "verification_uri":"{{VerificationUri}}","expires_in":900,"interval":5}
                  """ : tokenBody;
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    internal sealed class FetchRecordingHandler : System.Net.Http.HttpMessageHandler
    {
        /// <summary>"METHOD url" per request, in the order the host asked for them. The content is consumed here
        /// because <see cref = "OutboundFetch"/> drains it on the other side; a probe that left a body unread would
        /// be measuring a request path the app does not run.</summary>
        public List<string> Requests { get; } = [];

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method.Method} {request.RequestUri}");
            var response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent("<html><head><title>Build guide</title><script>tracking()</script></head>" + "<body><h1>cmake --build</h1><p>Two thousand eight hundred and ninety-one assertions " + "and no network.</p></body></html>", System.Text.Encoding.UTF8, "text/html"),
                RequestMessage = request,
            };
            // Drained before it is handed over: the app reads the body as a stream, and a probe that left it
            // unread would be measuring a request path the product never takes. ReadAsByteArray is already
            // complete, so nothing keeps the response alive behind our back.
            _ = response.Content.ReadAsByteArrayAsync(cancellationToken);
            return Task.FromResult(response);
        }
    }

    internal sealed class ModelListProbeHandler(string? body = null, int status = 200) : System.Net.Http.HttpMessageHandler
    {
        /// <summary>Every request this handler saw, in order, so a check can assert on what was sent.</summary>
        public List<System.Net.Http.HttpRequestMessage> Requests { get; } = [];

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = new System.Net.Http.HttpResponseMessage((System.Net.HttpStatusCode)status)
            {
                // JSON by default so the content type matches what a real OpenAI-compatible endpoint sends;
                // a body served as text/plain is a difference no rule here depends on, but it would be one
                // more thing differing for no reason.
                Content = new System.Net.Http.StringContent(body ?? """{"object":"list","data":[]}""", System.Text.Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    internal sealed class PeerChatClient(System.Collections.Generic.IReadOnlyList<(string Tool, System.Collections.Generic.Dictionary<string, object?> Arguments)> calls, string answer, TaskCompletionSource<bool>? answerReached = null, Task? beforeAnswer = null) : Microsoft.Extensions.AI.IChatClient
    {
        private int _responses;
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, System.Threading.CancellationToken cancellationToken = default) => throw new NotSupportedException("The cross-session check only uses the streaming path.");
        public async System.Collections.Generic.IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken cancellationToken = default)
        {
            var index = _responses++;
            if (index < calls.Count)
            {
                var call = calls[index];
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, [new Microsoft.Extensions.AI.FunctionCallContent(NextCallId("peer-" + index), call.Tool, call.Arguments)]);
            }
            else
            {
                answerReached?.TrySetResult(true);
                if (beforeAnswer is not null)
                    await beforeAnswer.WaitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, answer);
            }

            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose()
        {
        }
    }

    internal sealed class ScriptedChatClient(IReadOnlyList<string> chunks, Task? gate = null, Task? afterFirstChunkGate = null, System.Threading.Tasks.TaskCompletionSource<bool>? firstChunkReached = null, Exception? exception = null, (long Input, long Output, long Reasoning, long Cached)[]? usage = null, // The two halves of a hosted search, in the shape the bridge hands them over: a start that may carry
    // nothing yet, and a finish that carries the queries and the addresses. Yielded around the gate on
    // purpose — the point of this fixture is the state of the bubble <i>while</i> the search is the only thing
    // that has happened, which a client that answers instantly can never show.
    IList<Microsoft.Extensions.AI.AIContent>? searchStart = null, IList<Microsoft.Extensions.AI.AIContent>? searchEnd = null, System.Threading.Tasks.TaskCompletionSource<bool>? searchStartReached = null, // Why the response stopped, on the frame that closes it. A gateway that ends without one is common enough
    // that null has to stay a possible answer, and it is the case the caller must not report as a reason.
    string? finish = null, IList<Microsoft.Extensions.AI.AIContent>? reasoning = null, Task? afterAllChunksGate = null, System.Threading.Tasks.TaskCompletionSource<bool>? allChunksReached = null) : Microsoft.Extensions.AI.IChatClient
    {
        public int Requests { get; private set; }
        public int FailuresBeforeSuccess { get; init; } = int.MaxValue;
        public bool FailAfterChunks { get; init; }
        /// <summary>The options of the request that was just made, so a check can read what would have gone on
        /// the wire. The scripted client answers instead of a gateway, and the field a gateway would have been
        /// asked for has to be asserted on the same object the real request is built from.</summary>
        public Microsoft.Extensions.AI.ChatOptions? LastOptions { get; private set; }
        /// <summary>The messages of the request that was just made, system prompt first — which is how a check
        /// reads the instructions Hub gave the model without reaching into the private builder that made them.</summary>
        public System.Collections.Generic.IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>? LastMessages { get; private set; }

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, System.Threading.CancellationToken cancellationToken = default) => throw new NotSupportedException("The assistant check only uses the streaming path.");
        public async System.Collections.Generic.IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken cancellationToken = default)
        {
            Requests++;
            LastOptions = options;
            LastMessages = messages.ToList();
            if (searchStart is { Count: > 0 } start)
            {
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, start);
                searchStartReached?.TrySetResult(true);
            }

            if (reasoning is { Count: > 0 } thought && Requests <= FailuresBeforeSuccess)
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, thought);
            // A gate lets a check hold the stream open at its first token, so the mid-stream state of the
            // composer button can be asserted instead of only its end state. The wait is cancellation-aware:
            // a real stream dies when its token is cancelled, and "stop pressed while parked mid-reply" is
            // one of the paths a check has to be able to take.
            if (gate is not null)
                await gate.WaitAsync(cancellationToken);
            if (exception is not null && Requests <= FailuresBeforeSuccess && !FailAfterChunks)
                throw exception;
            if (searchEnd is { Count: > 0 } end)
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, end);
            var firstChunk = true;
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, chunk);
                if (firstChunk)
                {
                    firstChunk = false;
                    firstChunkReached?.TrySetResult(true);
                    if (afterFirstChunkGate is not null)
                        await afterFirstChunkGate.WaitAsync(cancellationToken);
                }

                await Task.Yield();
            }
            if (exception is not null && Requests <= FailuresBeforeSuccess && FailAfterChunks)
                throw exception;

            allChunksReached?.TrySetResult(true);
            if (afterAllChunksGate is not null)
                await afterAllChunksGate.WaitAsync(cancellationToken);
            // The reason on a frame of its own, carrying no content — the shape an OpenAI-compatible endpoint
            // actually sends it in, and precisely why a caller that only reads contents never learns why the
            // answer stopped.
            if (finish is { Length: > 0 } ended)
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, "")
                {
                    FinishReason = new Microsoft.Extensions.AI.ChatFinishReason(ended),
                };
            // The end-of-stream token report, in the same shape the gateway puts it: a sibling content on its own
            // update, after the text. Without it the read-back path could only ever be tested against a network.
            if (usage is null)
                yield break;
            foreach (var report in usage)
            {
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, [new Microsoft.Extensions.AI.UsageContent(new Microsoft.Extensions.AI.UsageDetails { InputTokenCount = report.Input, OutputTokenCount = report.Output, ReasoningTokenCount = report.Reasoning, CachedInputTokenCount = report.Cached, TotalTokenCount = report.Input + report.Output, }), ]);
                await Task.Yield();
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose()
        {
        }
    }

    internal static HubDialog[] HubDialogWindows()
        => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows
               .OfType<HubDialog>().ToArray() ?? [];
    internal static Button[] ActionButtons(Control dialog)
        => NamedDescendant<StackPanel>(dialog, "DialogButtons")?.GetVisualDescendants().OfType<Button>().ToArray() ?? [];
    internal static string? FindSourceDirectory() => ScratchDirectory.RepositoryRoot() is { } root
        ? System.IO.Path.Combine(root, "src", "AxmolHub")
        : null;
    internal static readonly string[] EngineHeaderKeys = ["Version", "Channel", "EngineDirectory", "Actions"];
    internal static string EngineHeaderExpectation(string language)
        => string.Join("/", EngineHeaderKeys.Select(key => HubTexts.Get(key, language)));
    internal static string EngineHeaderTexts(DataGrid? grid)
        => grid is null
            ? ""
            : string.Join("/", grid.Columns.Select(column => column.Header switch
            {
                TextBlock { Text: { } text } => text,
                null => "<null>",
                var header => header.ToString() ?? "<null>",
            }));
    internal static Button? EngineRowButton(DataGrid grid, EngineEntry engine, string textKey)
        => grid.GetVisualDescendants().OfType<DataGridRow>()
            .Where(row => row.DataContext is EngineListItem item && item.Engine.Path == engine.Path)
            .SelectMany(row => row.GetVisualDescendants().OfType<Button>())
            .FirstOrDefault(button => button.Tag is EngineEntry tagged && tagged.Path == engine.Path
                                      && ToolTip.GetTip(button) as string == HubStrings.Get(textKey));
    internal static SettingsPage OpenSettingsOn(MainWindow shell, string? category)
    {
        var settings = shell.OpenSettingsForCheck(category) ?? throw new InvalidOperationException(
            "设置浮层没有构建出页面：" + (category ?? "<last>"));
        settings.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return settings;
    }

    internal static void PressScrim(Border scrim, MainWindow shell)
        => scrim.RaiseEvent(new PointerPressedEventArgs(
            scrim, CheckPointer, shell, new Point(4, 4), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
    internal static Rect InWindow(Visual window, Visual target)
        => target.TranslatePoint(new Point(0, 0), window) is { } origin
            ? new Rect(origin, target.Bounds.Size)
            : default;
    internal static MenuItem[] BottomMenuSubmenuRows(MainWindow shell, string tag)
        => shell.BottomMenuForCheck?.Items.OfType<MenuItem>()
            .SelectMany(row => row.Items.OfType<MenuItem>())
            .FirstOrDefault(row => row.Tag as string == tag)?.Items.OfType<MenuItem>().ToArray() ?? [];
    internal static string ThemeLabels(SettingsPage settings)
        => NamedDescendant<ComboBox>(settings, "ThemePicker") is { } picker
            ? string.Join("/", picker.Items.OfType<ComboBoxItem>().Select(item => item.Content?.ToString() ?? ""))
            : "";
    internal static string NavLabel(RadioButton button) => button.Content is Panel panel
        ? string.Join("", panel.Children.OfType<TextBlock>().Select(text => text.Text))
        : button.Content?.ToString() ?? "";
    internal static bool Throws<T>(Action action)
        where T : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (T)
        {
            return true;
        }
    }

    internal sealed class LoopProbeFunction(AIFunction inner, Func<string> result) : DelegatingAIFunction(inner)
    {
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken token) => ValueTask.FromResult<object?>(result());
    }

    internal sealed class LoopProbeClient(IChatClient inner, RequestProbe wire, ChatTurn[] calls, List<ChatTurn> transcript) : IChatClient
    {
        private int _step;
        private string? _previous;
        public string CurrentResult { get; private set; } = "";
        public List<object> Rounds { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                await foreach (var _ in inner.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
                {
                }
            }
            catch (Exception)when (wire.Body is not null)
            {
            }

            var body = wire.Body!;
            var common = -1;
            var first = -1;
            if (_previous is not null)
            {
                var oldBytes = Encoding.UTF8.GetBytes(_previous);
                var newBytes = Encoding.UTF8.GetBytes(body);
                common = 0;
                while (common < Math.Min(oldBytes.Length, newBytes.Length) && oldBytes[common] == newBytes[common])
                    common++;
                using var oldJson = JsonDocument.Parse(_previous);
                using var newJson = JsonDocument.Parse(body);
                var name = newJson.RootElement.TryGetProperty("messages", out _) ? "messages" : "input";
                var oldItems = oldJson.RootElement.GetProperty(name);
                var newItems = newJson.RootElement.GetProperty(name);
                for (var i = 0; i < Math.Min(oldItems.GetArrayLength(), newItems.GetArrayLength()); i++)
                    if (oldItems[i].GetRawText() != newItems[i].GetRawText())
                    {
                        first = i;
                        break;
                    }
            }

            Rounds.Add(new { round = _step, bytes = Encoding.UTF8.GetByteCount(body), commonPrefixBytes = common, firstChangedMessage = first });
            _previous = body;
            if (_step < calls.Length)
            {
                var call = calls[_step++];
                CurrentResult = transcript.First(turn => turn.Role == ChatRoles.Tool && turn.ToolCallId == call.ToolCallId).Text;
                yield return new ChatResponseUpdate(ChatRole.Assistant, ChatPipeline.ToChatMessage(call).Contents)
                {
                    FinishReason = ChatFinishReason.ToolCalls,
                    ResponseId = "probe-" + _step
                };
            }
            else
                yield return new ChatResponseUpdate(ChatRole.Assistant, "offline done")
                {
                    FinishReason = ChatFinishReason.Stop,
                    ResponseId = "probe-done"
                };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose()
        {
        }
    }

    internal sealed class RequestProbe : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            Body = await request.Content!.ReadAsStringAsync(token).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":{\"message\":\"offline capture\",\"type\":\"invalid_request_error\"}}")
            };
        }
    }

    internal static Point ThumbGrabPoint(Thumb thumb, MainWindow shell)
        => thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), shell) ?? default;
    internal static double ThumbTopInTrack(Thumb thumb, Track track)
        => (thumb.TranslatePoint(new Point(0, 0), track) ?? default).Y;
    internal static void Press(Thumb thumb, Point origin, MainWindow shell)
        => thumb.RaiseEvent(new PointerPressedEventArgs(
            thumb, CheckPointer, shell, origin, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
    internal static void Move(Thumb thumb, Point origin, double at, MainWindow shell)
        => thumb.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent,
            thumb, CheckPointer, shell, origin + new Vector(0, at), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
            KeyModifiers.None));
    internal static void Release(Thumb thumb, Point origin, double at, MainWindow shell)
        => thumb.RaiseEvent(new PointerReleasedEventArgs(
            thumb, CheckPointer, shell, origin + new Vector(0, at), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));
    internal static long[] KeysOf(TranscriptSnapshot snapshot)
    {
        var keys = new List<long>();
        foreach (var entry in snapshot.Entries)
        {
            switch (entry)
            {
                case TranscriptMessage message: keys.Add(message.TurnKey); break;
                case TranscriptNotice notice: keys.Add(notice.TurnKey); break;
                case TranscriptActivity activity: keys.AddRange(activity.Piece.TurnKeys ?? []); break;
            }
        }

        return [.. keys];
    }

    internal const string AboutMenuLabel = "About Axmol Hub…";
    internal const string AboutTagline = "Axmol Hub is the desktop workspace for Axmol game development: engines, projects, toolchains and an AI " + "assistant in one place.";
    internal const string AboutRepository = "https://github.com/axmolengine/axmol-hub";
    internal sealed class ContextBurstClient : IChatClient
    {
        public int Executed { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Executed < 8)
            {
                var part = Executed++;
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("boundary-" + part, "fs_read", new Dictionary<string, object?> { ["path"] = $"part-{part}.txt" })])
                {
                    ResponseId = "boundary-response-" + part,
                    FinishReason = ChatFinishReason.ToolCalls
                };
            }
            else
                yield return new ChatResponseUpdate(ChatRole.Assistant, "boundary done")
                {
                    ResponseId = "boundary-response-final",
                    FinishReason = ChatFinishReason.Stop
                };
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose()
        {
        }
    }

    internal static void Relayout(MainWindow shell, ChatPanel panel, bool measure = false)
    {
        panel.Reload();
        if (measure) shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    internal static void SettleTranscript(MainWindow shell, ChatPanel panel, int rounds)
    {
        for (var i = 0; i < rounds; i++)
        {
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    internal static void Wheel(Control target, MainWindow shell, double delta)
        => target.RaiseEvent(new PointerWheelEventArgs(
            target, CheckPointer, shell,
            target.TranslatePoint(new Point(target.Bounds.Width / 2, Math.Min(24, target.Bounds.Height / 2)), shell) ?? default,
            0, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other),
            KeyModifiers.None, new Vector(0, delta)));
    internal sealed record ProjectionCase(string Label, Action<Conversation> Build);
}
