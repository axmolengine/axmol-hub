using AxmolHub.Core;
using AxmolHub.Agent;
using Microsoft.Extensions.AI;

internal static class AiImagesChecks
{
    private sealed class ControlVerification : IComputerInputVerification
    {
        internal bool Changed;
        public string? Verify() => Changed ? "The input control changed." : null;
        public void Dispose() { }
    }

    internal static async Task RunAsync(string root, string[] args)
    {
        // A session file is rewritten on every message and replayed into every later request, so the bytes of an
        // attachment live in a directory beside it and the turn keeps only the file name. These are the properties
        // that shape buys: the name survives a reload, a session written before attachments existed still loads,
        // deleting a conversation takes its pictures with it, and nothing about the storage lets a session file
        // name a path outside its own directory.
        var imageRoot = Path.Combine(root, "images");
        var store = new ConversationStore(imageRoot);
        var album = Conversation.Create("orcarouter");
        album.Append(ChatTurn.User("先说一句没有附件的话"));
        static byte[] WithHeader(int length, params byte[] header)
        {
            var bytes = new byte[length];
            header.CopyTo(bytes, 0);
            return bytes;
        }

        var png = WithHeader(64, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
        var jpeg = WithHeader(44, 0xFF, 0xD8, 0xFF);
        var gif = WithHeader(40, (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a');
        var webp = WithHeader(40, (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P');
        // RIFF is a container, not a picture: a WAV file shares the first four bytes with a WebP and is refused only
        // if the reader looks at offset 8, which is the whole reason Identify reads it.
        var wave = WithHeader(40, (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'A', (byte)'V', (byte)'E');
        var prose = WithHeader(32, (byte)'t', (byte)'r', (byte)'o', (byte)'u', (byte)'b', (byte)'l', (byte)'e');
        var first = store.SaveImage(album.Id, png);
        var second = store.SaveImage(album.Id, jpeg);
        if (first is not { File: "1.png", MediaType: "image/png" } || first.Bytes != png.Length || second is not { File: "2.jpg", MediaType: "image/jpeg" })
            throw new Exception($"The stored name, type or size is wrong: {first} / {second}");
        if (store.ReadImage(album.Id, "2.jpg")is not { } storedBytes || !jpeg.SequenceEqual(storedBytes))
            throw new Exception("A stored image did not read back byte for byte.");
        album.Append(ChatTurn.User("这两张图是什么报错？", images: [first, second]));
        store.Save(album);
        var reloadedAlbum = store.Load(album.Id) ?? throw new Exception("A session with attachments no longer loads.");
        if (reloadedAlbum.Messages[^1].Images.SequenceEqual([first, second]) == false || reloadedAlbum.Messages[0].Images.Count != 0)
            throw new Exception($"Attachments did not round-trip: {reloadedAlbum.Messages[^1].Images.Count} on the last turn.");
        // The file name is read from a session file, which is user-editable data: a name carrying a separator is
        // refused instead of resolved, or a hand-written transcript would be an arbitrary file read.
        if (store.ReadImage(album.Id, "../images.json")is not null || store.ReadImage(album.Id, "gone.png")is not null || store.ReadImage(album.Id, "")is not null)
            throw new Exception("An attachment name outside the session's directory was resolved anyway.");
        // ── Recognise the header, not the extension: the four sendable formats, and two non-images that most resemble
        //    them ──
        if (ChatImageFormat.Identify(gif) != ChatImageFormat.Gif || ChatImageFormat.Identify(webp) != ChatImageFormat.WebP || ChatImageFormat.Identify(wave)is not null || ChatImageFormat.Identify(prose)is not null || ChatImageFormat.Identify([1, 2])is not null || ChatImageFormat.Identify(null)is not null)
            throw new Exception($"A header was not recognized, or something that is not a picture was: " + $"{ChatImageFormat.Identify(gif)} / {ChatImageFormat.Identify(webp)} / " + $"{ChatImageFormat.Identify(wave)}");
        // The stored suffix has to agree with the header, because the next reader recognizes the file by its bytes and
        // a ".png" that holds a GIF is the kind of lie a gateway rejects with an error nobody can trace.
        if (store.SaveImage(album.Id, gif)is not { File: "3.gif", MediaType: "image/gif" } || store.SaveImage(album.Id, webp)is not { File: "4.webp", MediaType: "image/webp" })
            throw new Exception("A recognized format was not stored under its own extension.");
        try
        {
            store.SaveImage(album.Id, prose);
            throw new Exception("Bytes with no known image header were stored anyway.");
        }
        catch (ArgumentException)
        { /* the store names a file by its header, so it cannot keep one it cannot read back */
        }

        // A session written before this field existed has no Images property at all, and must still load — the
        // forward-compatibility rule every optional turn field follows.
        File.WriteAllText(Path.Combine(imageRoot, "ai", "sessions", "legacy-no-images.json"), """{"Id":"legacy-no-images","Title":"legacy","ProviderId":"orcarouter","Messages":[{"Role":"user","Text":"早于附件功能"}]}""");
        if (store.Load("legacy-no-images")is not { } legacy || legacy.Messages[0].Images.Count != 0)
            throw new Exception("A session file predating attachments no longer loads.");
        // The image directory is named after the session, in the same folder the index lives in. Listing reads files,
        // so a directory cannot become a row — but the rebuild path is the one that walks the folder, so it is what
        // gets asserted once the derived index is gone.
        if (store.List().Count(summary => summary.Id == album.Id) != 1)
            throw new Exception("The attachment directory made the session appear twice in the list.");
        File.Delete(Path.Combine(imageRoot, "ai", "sessions", "index.json"));
        var rebuilt = store.List();
        if (rebuilt.Count(summary => summary.Id == album.Id) != 1 || !rebuilt.Any(summary => summary.Id == "legacy-no-images") || rebuilt.Count != 2)
            throw new Exception("Rebuilding the index from the files miscounted the sessions: " + string.Join(", ", rebuilt.Select(summary => summary.Id)));
        store.Delete(album.Id);
        if (File.Exists(Path.Combine(imageRoot, "ai", "sessions", album.Id + ".json")) || Directory.Exists(Path.Combine(imageRoot, "ai", "sessions", album.Id)))
            throw new Exception("A deleted session left its images behind in a directory nothing can reach again.");
        Console.WriteLine("PASS: attachments live beside the session file, keep their names across a reload, and go " + "with the session.");
        // The admission rule is one pure function over two inputs, so every cell is asserted rather than sampled:
        // empty bytes, an unknown header, a picture over the ceiling, and a message that already carries the most it
        // may. The last two rows pin the ordering — something both oversized and one-too-many is reported as
        // oversized, because "attach four smaller ones" is not the fix anyone would try from the other answer.
        var oversized = new byte[ChatImageFormat.MaxImageBytes + 1];
        png.CopyTo(oversized, 0);
        foreach (var(bytes, already, expected)in new[]
        {
            (new byte[0], 0, ChatImageVerdict.Empty),
            (prose, 0, ChatImageVerdict.Unrecognized),
            (png, ChatImageFormat.MaxImagesPerMessage - 1, ChatImageVerdict.Accepted),
            (png, ChatImageFormat.MaxImagesPerMessage, ChatImageVerdict.TooMany),
            (oversized, 0, ChatImageVerdict.TooLarge),
            (oversized, ChatImageFormat.MaxImagesPerMessage, ChatImageVerdict.TooLarge),
        }

        )
            if (ChatImageFormat.Admit(bytes, already) != expected)
                throw new Exception($"Admit({bytes.Length} bytes, {already} already attached) answered " + $"{ChatImageFormat.Admit(bytes, already)} instead of {expected}.");
        Console.WriteLine("PASS: an image is admitted by its header, its size, and how many the message already carries.");
        // ── Wiring shape: the image is DataContent on the user message that carries the question ──
        // chat/completions carries an image on a user message and not on a tool result, and Microsoft.Extensions.AI
        // 10.10 has no ImageContent type at all: an image is a DataContent whose media type says image/*. The scripted
        // client is the only zero-network way to see the contents list the bridge is handed, so the shape is asserted
        // there rather than discovered by a gateway returning 400.
        var wire = new FakeChatClient(["ok"]);
        var wireProvider = AiProviderManifest.CreateBuiltIn("orcarouter")!;
        wireProvider.Model = "orcarouter/auto";
        var withPictures = Conversation.Create("orcarouter");
        withPictures.Append(ChatTurn.User("这两张图是什么报错？", images: [first, second]));
        await foreach (var _ in new ChatPipeline(wire).SendAsync(wireProvider, withPictures.Messages, images: image => image.File switch
        {
            "1.png" => BinaryData.FromBytes(png),
            "2.jpg" => BinaryData.FromBytes(jpeg),
            _ => null,
        }))
        {
        }

        var sent = wire.LastMessages?.FirstOrDefault(message => message.Role == ChatRole.User) ?? throw new Exception("The scripted client never saw the user message.");
        var pictures = sent.Contents.OfType<DataContent>().ToList();
        if (sent.Contents[0] is not TextContent || pictures.Count != 2 || pictures[0].MediaType != "image/png" || pictures[1].MediaType != "image/jpeg" || !png.AsSpan().SequenceEqual(pictures[0].Data.Span) || !jpeg.AsSpan().SequenceEqual(pictures[1].Data.Span) || !sent.Contents.OfType<TextContent>().Single().Text.Contains("这两张图是什么报错？", StringComparison.Ordinal))
            throw new Exception("The request is not [text, image, image] with the bytes that were stored: " + string.Join(" · ", sent.Contents.Select(content => content.GetType().Name)));
        if (!pictures[0].Uri.ToString()!.StartsWith("data:image/png;base64,", StringComparison.Ordinal))
            throw new Exception($"An attachment did not reach the wire as a data URL ({pictures[0].Uri}).");
        // A file the store cannot hand back is the case that must not pass silently: an empty contents list would let
        // the model describe a screenshot it was never given, which reads to the user as a working image channel.
        var lostClient = new FakeChatClient(["ok"]);
        var lostPictures = Conversation.Create("orcarouter");
        lostPictures.Append(ChatTurn.User("这张图呢？", images: [new ChatImage("gone.png", "image/png", 12)]));
        await foreach (var _ in new ChatPipeline(lostClient).SendAsync(wireProvider, lostPictures.Messages, images: _ => null))
        {
        }

        var lostMessage = lostClient.LastMessages?.Single(message => message.Role == ChatRole.User) ?? throw new Exception("The scripted client never saw the message with the lost attachment.");
        if (lostMessage.Contents.OfType<DataContent>().Any() || !lostMessage.Contents.OfType<TextContent>().Single().Text.Contains("were not sent", StringComparison.Ordinal))
            throw new Exception("A missing attachment was sent as nothing, and the model was not told it was missing.");
        // A turn with no attachments must still produce exactly one plain text message: everything in the sessions and
        // context groups is built on that, and it is the one thing this change had no licence to alter.
        var plain = ChatPipeline.ToChatMessage(ChatTurn.User("没有附件"));
        if (plain.Contents.Count != 1 || plain.Contents[0] is not TextContent)
            throw new Exception("A text-only turn stopped arriving as one text part.");
        Console.WriteLine("PASS: an attachment rides its own question as a data URL, and a lost one says so in the text.");
        // ── Returning an image at the boundary: a tool-role result cannot carry an image, so follow it with a user
        //    message ──
        // A frame the assistant captured itself is recorded on the result turn, and a `tool` message cannot carry
        // image content, so the picture leaves as its own user-role message right after the result that names it.
        // The injection has to be visible in the message list: ToChatMessage's tool branch never reads Images at all,
        // so dropping this line sends a capture the model was told about and never shown — the silent kind.
        var boundary = ChatPipeline.ToChatMessages([ChatTurn.User("看看现在的界面"), ChatTurn.FunctionCall("call-1", "capture_screen", "{}"), ChatTurn.FunctionResult("call-1", "captured · 1.png", images: [first, second]), ], image => image.File == "1.png" ? BinaryData.FromBytes(png) : BinaryData.FromBytes(jpeg));
        if (boundary.Count != 4 || boundary[2].Role != ChatRole.Tool || boundary[2].Contents.OfType<DataContent>().Any() || boundary[3].Role != ChatRole.User || boundary[3].Contents.OfType<DataContent>().Count() != 2 || !boundary[3].Text.Contains("1.png", StringComparison.Ordinal))
            throw new Exception("The captured frame is not a user message beside the result that names it: " + string.Join(" · ", boundary.Select(message => message.Role)));
        // And the case that must stay untouched: a result with no picture adds no message, or every tool call in every
        // existing session would grow a second bubble on replay.
        if (ChatPipeline.ToChatMessages([ChatTurn.User("问题"), ChatTurn.FunctionResult("c0", "ok")]).Count != 2)
            throw new Exception("A tool result with no attachment gained a message of its own.");
        Console.WriteLine("PASS: a captured frame reaches the model as a user message beside the result that names it.");
        // A live tool loop must see the bytes before any replay or approval restart.
        var live = new ToolLoopChatClient(callName: "capture_screen");
        await foreach (var _ in new ChatPipeline(live).SendAsync(wireProvider,
            [ChatTurn.User("Inspect the app")],
            tools: [AIFunctionFactory.Create(() => "captured", "capture_screen")],
            images: _ => BinaryData.FromBytes(png), toolImages: _ => [first])) { }
        if (live.SecondRequest.SelectMany(message => message.Contents).OfType<DataContent>().SingleOrDefault()
                is not { } liveImage || !liveImage.Data.Span.SequenceEqual(png))
            throw new Exception("The live follow-up request did not contain the captured image bytes.");
        Console.WriteLine("PASS: live capture bytes reach the next model request without replay.");
        // A mid-run question must survive another tool round, not only the request that first received it.
        // Keep the fixture shared with the ordinary tool-loop checks; no network or session storage is involved.
        var steered = new ToolLoopChatClient(callName: "project_list", toolRounds: 2);
        var interjection = ChatPipeline.ToChatMessage(ChatTurn.User("Inspect this attached error", images: [first]),
            _ => BinaryData.FromBytes(png));
        var deliveries = 0;
        await foreach (var _ in new ChatPipeline(steered).SendAsync(wireProvider,
            [ChatTurn.User("Inspect the project")],
            tools: [AIFunctionFactory.Create(() => "project contents", "project_list")],
            pendingInterjection: () => Task.FromResult<ChatMessage?>(++deliveries == 1 ? interjection : null))) { }
        if (steered.CallCount != 3 || deliveries != 2)
            throw new Exception("The steer fixture did not cross two completed tool boundaries.");
        foreach (var request in new[] { steered.SecondRequest, steered.ThirdRequest })
        {
            var index = request.IndexOf(interjection);
            if (request.Count(message => ReferenceEquals(message, interjection)) != 1 || index < 1
                || !request[index - 1].Contents.OfType<FunctionResultContent>().Any(result => result.CallId == "call-1")
                || interjection.Text != "Inspect this attached error"
                || interjection.Contents.OfType<DataContent>().SingleOrDefault() is not { } image
                || !image.Data.Span.SequenceEqual(png)
                || request == steered.ThirdRequest && (index + 1 >= request.Count
                    || !request[index + 1].Contents.OfType<FunctionCallContent>().Any(call => call.CallId == "call-2")))
                throw new Exception("A mid-run question or its image disappeared, duplicated, or moved after another tool round.");
        }
        Console.WriteLine("PASS: mid-run text and image survive subsequent tool requests once, at their original boundary.");
        // Nullable optional modifiers must have the same meaning in the gate and the tool body.
        var noModifiers = ComputerInput.Parse("""{"action":"move","x":1,"y":1,"modifiers":null}""");
        if (noModifiers is not { Modifiers.Length: 0 } || ComputerInput.Validate(noModifiers) is not null
            || ComputerInput.Parse("""{"action":"move","modifiers":false}""") is not null)
            throw new Exception("Input parsing disagreed on null modifiers or accepted a malformed modifier value.");
        // Live application permission is an isolation boundary, not a persisted tool approval.
        var appTarget = new ComputerUseTarget(new ComputerApplication(Path.GetFullPath("fixture-editor")), 11, 100, 1, "First");
        var applicationControl = new ComputerControlSession(appTarget, "Edit a disposable document");
        var newWindow = appTarget with { ProcessId = 12, ProcessStartedAtUtcTicks = 200, WindowHandle = 2, WindowTitle = "Second" };
        var otherApp = newWindow with { Application = new ComputerApplication(Path.GetFullPath("fixture-other")) };
        var version = applicationControl.Version;
        if (!applicationControl.Covers(newWindow) || applicationControl.Covers(otherApp))
            throw new Exception("Application permission failed new-window reuse or crossed an executable boundary.");
        applicationControl.Pause(true);
        if (applicationControl.Covers(newWindow) || !applicationControl.Covers(newWindow, allowPaused: true)
            || applicationControl.Version == version)
            throw new Exception("Pause did not invalidate live input permission.");
        applicationControl.Pause(false);
        applicationControl.Revoke();
        if (applicationControl.Covers(newWindow) || applicationControl.Wait(CancellationToken.None)
            || new ComputerControlSession(appTarget, "New task").Version == applicationControl.Version)
            throw new Exception("Revoked permission survived or its version was recycled.");
        // A call waking after pause/resume must fail before touching either capture or input backends.
        long waitingVersion = 1;
        var waitingTools = new WorkspaceTools(WorkspaceToolScope.Empty with
        {
            ComputerControlVersion = () => waitingVersion,
            WaitComputerControl = _ => { waitingVersion++; return true; },
        });
        if (!waitingTools.ComputerUse("Fixture", "move", frame_id: "old")
                .Contains("permission changed while this call was waiting", StringComparison.Ordinal)
            || !waitingTools.CaptureScreen("Fixture")
                .Contains("permission changed while this call was waiting", StringComparison.Ordinal))
            throw new Exception("A resumed call did not explain invalidation before accessing the desktop.");
        // Frame consumption is an isolation boundary, not a cosmetic coordinate detail.
        var observation = new ComputerUseObservation();
        var observedWindow = new CapturableWindow(1, "Fixture", 1, 100, 50)
        { Owner = new ComputerProcessIdentity(new ComputerApplication(Path.GetFullPath("fixture-app")), 1) };
        var observedFrame = new CapturedFrame(png, 200, 100, new FrameStats(200, 100, 2, 100));
        var frameId = observation.Record(observedWindow, observedFrame);
        if (!observation.MatchesContent(observedFrame)
            || observation.MatchesContent(observedFrame with { ContentFingerprint = "changed" }))
            throw new Exception("Changed image content was admitted as the original observation.");
        var drag = new ComputerActionRequest("drag", 150, 80) { EndX = 20, EndY = 10 };
        var mappedDrag = observation.Map(frameId, observedWindow, drag);
        if (mappedDrag is not { X: 75, Y: 40, EndX: 10, EndY: 5 }
            || observation.Map(frameId, observedWindow, drag with { EndX = 200 }) is not null)
            throw new Exception("Drag mapping accepted an out-of-window end or scaled its points incorrectly.");
        var cx = 150; var cy = 80;
        if (observation.Consume(frameId, observedWindow, ref cx, ref cy, true) is not null || cx != 75 || cy != 40
            || observation.Consume(frameId, observedWindow, ref cx, ref cy, true) is null)
            throw new Exception("Frame scaling or single-use isolation failed.");
        frameId = observation.Record(observedWindow, observedFrame);
        if (observation.Consume(frameId, observedWindow with { Left = 10 }, ref cx, ref cy, false) is null)
            throw new Exception("A moved window accepted stale input.");
        if (!GoalProgress.StateChanged(ToolNames.ComputerUse, "Input delivered (click); outcome unverified")
            || GoalProgress.StateChanged(ToolNames.ComputerUse, "frame_id=example; image=200×100"))
            throw new Exception("Input and observation must have different progress semantics.");
        frameId = observation.Record(observedWindow, observedFrame);
        using (var desktop = ComputerDesktop.TryEnter())
        {
            using var competing = ComputerDesktop.TryEnter();
            if (desktop is null || competing is not null)
                throw new Exception("Concurrent desktop operations were admitted.");
            ComputerDesktop.Invalidate();
            if (observation.Consume(frameId, observedWindow, ref cx, ref cy, false) is null)
                throw new Exception("Another desktop action did not invalidate an old observation.");
        }
        // A classified target may stay unchanged while unrelated pixels blink. A changed target still refuses.
        var controlVerification = new ControlVerification();
        var inputObservation = new ComputerUseObservation();
        var inputTarget = ComputerUseTarget.FromWindow(observedWindow)!;
        var changingPixels = observedFrame;
        var targetDeliveries = 0;
        var ordinaryTools = new WorkspaceTools(WorkspaceToolScope.Empty with
        {
            Screen = new ScreenCaptureBridge(() => [observedWindow], _ => changingPixels)
                { Backend = new CaptureBackend("fixture", "offline target fixture") },
            ApprovedComputerTarget = () => inputTarget,
            ComputerObservation = () => inputObservation,
            ComputerInputDecision = () => new(ComputerRisk.Allow, "Verified fixture text control") { Verification = controlVerification },
            ComputerUse = new ComputerUseBridge((_, request) =>
            {
                if (request.VerifyObservation?.Invoke() != true) return "Refused: target changed.";
                targetDeliveries++;
                return null;
            }),
        });
        var targetFrame = inputObservation.Record(observedWindow, observedFrame);
        changingPixels = observedFrame with { ContentFingerprint = "caret blink" };
        ordinaryTools.ComputerUse("Fixture", "move", x: 1, y: 1, frame_id: targetFrame);
        controlVerification.Changed = true;
        targetFrame = inputObservation.Record(observedWindow, changingPixels);
        ordinaryTools.ComputerUse("Fixture", "move", x: 1, y: 1, frame_id: targetFrame);
        if (targetDeliveries != 1) throw new Exception("Ordinary input required identical pixels or admitted a changed control.");
        using var cancelledInput = new CancellationTokenSource();
        cancelledInput.Cancel();
        var neverOperated = false;
        var cancelledTools = new WorkspaceTools(WorkspaceToolScope.Empty with
        {
            ComputerUse = new ComputerUseBridge((_, _) => { neverOperated = true; return null; }),
        });
        try
        {
            cancelledTools.ComputerUse("Fixture", "type", text: "not sent", cancellationToken: cancelledInput.Token);
            throw new Exception("Cancelled input was admitted.");
        }
        catch (OperationCanceledException) when (!neverOperated) { }
        // Exercise live revocation and cancellation after one simulated event, never the real desktop.
        foreach (var cancelDuringInput in new[] { false, true })
        {
            using var runningInput = new CancellationTokenSource();
            var liveControl = new ComputerControlSession(inputTarget, "Offline interruption");
            var liveObservation = new ComputerUseObservation();
            var eventsSent = 0;
            var capturesAfterInput = 0;
            var interruptibleTools = new WorkspaceTools(WorkspaceToolScope.Empty with
            {
                Screen = new ScreenCaptureBridge(() => [observedWindow], _ => { capturesAfterInput++; return observedFrame; })
                    { Backend = new CaptureBackend("fixture", "offline interruption fixture") },
                ComputerControlVersion = () => liveControl.Version,
                ApprovedComputerTarget = () => liveControl.Covers(inputTarget) ? inputTarget : null,
                ComputerObservation = () => liveObservation,
                ComputerInputDecision = () => new(ComputerRisk.Allow, "Offline interruption"),
                ComputerUse = new ComputerUseBridge((_, request) =>
                {
                    for (var index = 0; index < 3; index++)
                    {
                        request.Cancellation.ThrowIfCancellationRequested();
                        if (request.StillAuthorized?.Invoke() != true) return "Refused: live permission changed.";
                        eventsSent++;
                        if (cancelDuringInput) runningInput.Cancel(); else liveControl.Revoke();
                    }
                    throw new Exception("Interrupted input reached its end.");
                }),
            });
            var liveFrame = liveObservation.Record(observedWindow, observedFrame);
            try
            {
                var result = interruptibleTools.ComputerUse("Fixture", "type", text: "abc", frame_id: liveFrame,
                    cancellationToken: runningInput.Token);
                if (cancelDuringInput || !result.StartsWith("Refused:", StringComparison.Ordinal))
                    throw new Exception("Interrupted input was reported as delivered.");
            }
            catch (OperationCanceledException) when (cancelDuringInput && runningInput.IsCancellationRequested) { }
            if (eventsSent != 1 || capturesAfterInput != 0)
                throw new Exception("Interrupted input sent another event or performed a post-input capture.");
        }
        if (!ComputerUseGrants.IsInput(ToolNames.ComputerUse, "{\"action\":\"click\"}")
            || ComputerUseGrants.IsInput(ToolNames.ComputerUse, "{\"action\":\"screenshot\"}"))
            throw new Exception("Input confirmation classification failed.");
        Console.WriteLine("PASS: screenshot coordinates scale once and stale observations refuse input.");
        // Portal consent is not an executable grant. Test the shared call boundary once, without a desktop.
        var portalCalls = 0;
        var portalApproved = false;
        var portalTools = new WorkspaceTools(WorkspaceToolScope.Empty with
        {
            PortalComputerApproved = () => portalApproved,
            PortalComputer = new PortalComputerBridge((request, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                portalCalls++;
                return Task.FromResult(new PortalComputerResult(null, "fixture portal result"));
            }),
        });
        var noConsent = await portalTools.CaptureScreenAsync("Fixture");
        if (portalCalls != 0 || !noConsent.StartsWith("Refused:", StringComparison.Ordinal))
            throw new Exception("A portal call bypassed its individual confirmation.");
        portalApproved = true;
        await portalTools.CaptureScreenAsync("Fixture", fullscreen: true);
        await portalTools.CaptureScreenAsync("Fixture", process_id: 123);
        if (portalCalls != 0) throw new Exception("Portal accepted whole-display or unverifiable PID targeting.");
        if (await portalTools.CaptureScreenAsync("Fixture") != "fixture portal result" || portalCalls != 1)
            throw new Exception("Confirmed portal capture did not reach the async host exactly once.");
        try
        {
            await portalTools.ComputerUseAsync("Fixture", "type", text: "never sent", cancellationToken: cancelledInput.Token);
            throw new Exception("Cancelled portal input was admitted.");
        }
        catch (OperationCanceledException) when (portalCalls == 1) { }
        Console.WriteLine("PASS: portal calls require individual consent, refuse display/PID targeting and honor cancellation.");
        // ── Intake: a file a person hands in is first read by the rules, then stored by the rules ──
        // The composer's three entrances all end in the same two calls, so the rules they share are asserted here
        // rather than through a UI that cannot be automated: a file is measured before it is read, a picture is
        // stored only once it can be sent, and a refusal writes nothing at all.
        // Its own data root rather than the one above: that store's index is counted by the block before this one, and
        // a session left behind here by a crash would read as "the attachment directory became a row".
        var composerRoot = Path.Combine(root, "images-composer");
        var composerStore = new ConversationStore(composerRoot);
        var composer = Conversation.Create("orcarouter");
        composerStore.Save(composer);
        // Read back through the store's own accessor: the directory is named after a *sanitized* id, and a check that
        // builds the path by hand would be counting a folder nothing ever used.
        var candidateDirectory = composerStore.SessionImageDirectory(composer.Id);
        var realPng = Path.Combine(root, "images", "capture.png");
        File.WriteAllBytes(realPng, png);
        var fakePng = Path.Combine(root, "images", "notes.png");
        File.WriteAllBytes(fakePng, prose);
        var emptyFile = Path.Combine(root, "images", "empty.png");
        File.WriteAllBytes(emptyFile, []);
        // Text that is over the ceiling is the case that tells size-first from read-first: reading it would answer
        // "not an image", while measuring it answers "too large" — and the second one is the fixable complaint.
        var hugeText = Path.Combine(root, "images", "huge.txt");
        using (var huge = File.Create(hugeText))
            huge.SetLength(ChatImageFormat.MaxImageBytes + 1);
        if (ConversationStore.ReadCandidateFile(realPng, out var pickedVerdict)is not { } pickedBytes || pickedVerdict != ChatImageVerdict.Accepted || !pickedBytes.SequenceEqual(png))
            throw new Exception($"A picked picture did not come through as its own bytes ({pickedVerdict}).");
        if (ConversationStore.ReadCandidateFile(fakePng, out var fakeVerdict)is not null || fakeVerdict != ChatImageVerdict.Unrecognized)
            throw new Exception($"A text file renamed .png was taken for a picture ({fakeVerdict}).");
        if (ConversationStore.ReadCandidateFile(emptyFile, out var emptyVerdict)is not null || emptyVerdict != ChatImageVerdict.Empty)
            throw new Exception($"An empty file was not refused as empty ({emptyVerdict}).");
        if (ConversationStore.ReadCandidateFile(hugeText, out var hugeVerdict)is not null || hugeVerdict != ChatImageVerdict.TooLarge)
            throw new Exception($"An oversized file was admitted by its header rather than refused by its size ({hugeVerdict}).");
        if (Directory.Exists(candidateDirectory))
            throw new Exception("Holding a picture in the composer wrote it to the session directory already.");
        var admitted = composerStore.AttachImage(composer.Id, png, 0);
        if (!admitted.Accepted || admitted.Image is not { File: "1.png", MediaType: "image/png" } || composerStore.ImagePath(composer.Id, "1.png")is not { } imagePath || !File.Exists(imagePath))
            throw new Exception($"An admitted picture did not land where the turn can name it ({admitted}).");
        var rejected = composerStore.AttachImage(composer.Id, prose, 1);
        if (rejected.Verdict != ChatImageVerdict.Unrecognized || rejected.Image is not null || composerStore.ImagePath(composer.Id, "2.png")is not null || Directory.EnumerateFiles(candidateDirectory).Count() != 1)
            throw new Exception($"A refused picture was stored anyway: {string.Join(", ", Directory.EnumerateFiles(candidateDirectory))}");
        if (composerStore.AttachImage(composer.Id, jpeg, ChatImageFormat.MaxImagesPerMessage)is { Verdict: ChatImageVerdict.TooMany, Image: null })
        {
            if (Directory.EnumerateFiles(candidateDirectory).Count() != 1)
                throw new Exception("The over-count picture reached the disk before the message was refused.");
        }
        else
            throw new Exception("A message at the image limit accepted another picture.");
        // The name comes from a session file, which is user-editable: the path resolver refuses anything that is not
        // one plain name, so a hand-written transcript cannot become an arbitrary file read of the data root.
        if (composerStore.ImagePath(composer.Id, "../state.json")is not null || composerStore.ImagePath(composer.Id, "")is not null || composerStore.ImagePath(composer.Id, "gone.png")is not null)
            throw new Exception("An attachment name outside the session's directory was resolved anyway.");
        composerStore.Delete(composer.Id);
        if (Directory.Exists(candidateDirectory))
            throw new Exception("The composer's session was deleted and its picture stayed on disk.");
        Console.WriteLine("PASS: the composer's entrances measure before they read, store only what can be sent, and " + "write nothing for a refusal.");
        // A picture with no question under it is what people actually send, and the turn's text is empty — so the
        // boundary must not put an empty text part in front of it. Asserted as the absence of the part rather than
        // as "an empty string is harmless", because a gateway that 400s on it fails the whole message.
        var onlyPicture = ChatPipeline.ToChatMessage(ChatTurn.User("", images: [first]), image => BinaryData.FromBytes(png));
        if (onlyPicture.Contents.OfType<TextContent>().Any() || onlyPicture.Contents.OfType<DataContent>().Count() != 1)
            throw new Exception($"A picture-only message still carries text: {string.Join(" · ", onlyPicture.Contents.Select(content => content.GetType().Name))}");
        // …and when its bytes are gone, the sentence that says so is the only content left, which is the case that
        // used to be an empty message.
        var lostOnlyPicture = ChatPipeline.ToChatMessage(ChatTurn.User("", images: [first]), _ => null);
        if (lostOnlyPicture.Contents.OfType<TextContent>().SingleOrDefault()?.Text.Contains("were not sent", StringComparison.Ordinal) != true)
            throw new Exception("A picture-only message whose bytes are gone says nothing about it.");
        Console.WriteLine("PASS: a picture sent without a question carries the pictures and no empty text part.");
        // ── Budget: images are billed, and erasure drops the image but keeps the text ──
        // A turn whose text is one character is the cheap turn only if nobody priced the picture riding on it, and
        // both estimators count characters — so an unpriced attachment reads as free to the window and as free to the
        // loop guard, which is how a request the trimmer was supposed to keep legal goes over the limit anyway. The
        // price is asserted as a difference against the same turn without the attachment, never against the constant:
        // comparing to the constant would still pass if the constant were 0.
        if (ContextTrimmer.EstimateTokens(ChatTurn.User("看图", images: [first])) <= ContextTrimmer.EstimateTokens(ChatTurn.User("看图")))
            throw new Exception("An attachment cost nothing in the turn estimate.");
        // Four turns of one character each, two of them carrying a picture. The budget is a literal rather than a
        // formula over the price: two picture turns at the 1 024 floor plus two one-character answers is 2 064, so
        // 2 050 clears the newest three and no more. If the attachment stops being charged, all four fit and this is
        // the assertion that says so.
        var crowded = new List<ChatTurn>
        {
            ChatTurn.User("一", images: [first]),
            ChatTurn.Assistant("答"),
            ChatTurn.User("二", images: [second]),
            ChatTurn.Assistant("答"),
        };
        var crowdedWindow = ContextTrimmer.Trim(crowded, 2050);
        if (crowdedWindow.Count != 3 || crowdedWindow.Any(turn => turn.Text == "一"))
            throw new Exception($"Pictures were not charged against the window: {crowdedWindow.Count} of 4 turns survived.");
        // Inside the loop the same bytes are billed again by a different estimator, and the older picture is dropped
        // rather than the message: dropping the turn would orphan an assistant call from its result. The results here
        // are deliberately short (under the elision floor), so the picture is the only thing over budget — which is
        // what makes this the negative control for the price above.
        var pictureLoop = new List<ChatMessage>
        {
            new(ChatRole.System, "system rules"),
            new(ChatRole.User, [new TextContent("这张图是什么报错？"), new DataContent(png, "image/png")]),
        };
        for (var index = 0; index <= ToolLoopContextGuard.KeepRecentResults; index++)
            pictureLoop.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"c{index}", "短结果")]));
        var elidedPictures = ToolLoopContextGuard.Elide(pictureLoop, 300);
        var pictureTurn = elidedPictures.SingleOrDefault(message => message.Role == ChatRole.User) ?? throw new Exception("Elision deleted the message that carried the picture instead of the picture.");
        if (elidedPictures.Count != pictureLoop.Count || pictureTurn.Contents.OfType<DataContent>().Any() || !pictureTurn.Text.Contains("这张图是什么报错？", StringComparison.Ordinal) || !pictureTurn.Text.Contains("elided", StringComparison.Ordinal))
            throw new Exception("The older picture survived the budget, or its message went with it: " + string.Join(" · ", pictureTurn.Contents.Select(content => content.GetType().Name)));
        if (elidedPictures.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Any(result => (result.Result?.ToString() ?? "").Contains("elided", StringComparison.Ordinal)))
            throw new Exception("The picture was priced by shrinking a tool result that was already short.");
        Console.WriteLine("PASS: a picture is charged for the window, and an old one is elided without losing its turn.");
        // A refusal the model reads as "try again" spends the whole turn budget on the same bytes, so the retry ban is
        // part of the contract here exactly as it is for a path the sandbox rejected.
        foreach (var verdict in new[]
        {
            ChatImageVerdict.Empty,
            ChatImageVerdict.Unrecognized,
            ChatImageVerdict.TooLarge,
            ChatImageVerdict.TooMany,
        }

        )
        {
            var sentence = ChatImageFormat.ResultFor(verdict, ChatImageFormat.MaxImageBytes + 1);
            if (sentence.Length == 0 || !sentence.Contains("retry", StringComparison.OrdinalIgnoreCase))
                throw new Exception($"The image refusal for {verdict} does not tell the model to stop retrying.");
        }

        Directory.Delete(imageRoot, recursive: true);
        return;
    }
}
