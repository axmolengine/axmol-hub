using AxmolHub.Core;
using AxmolHub.Agent;
using Microsoft.Extensions.AI;

internal static class AiWebSearchChecks
{
    internal static async Task RunAsync(string root, string[] args)
    {
        // ── Server-side search of its own: the verdict table, the bytes on both wires, kept in the transcript but not
        //    on the wire ──
        // Not one real network request: both bridges are answered on 127.0.0.1 by a recording handler. The assertions
        // here read the **bytes that were serialised out**, not what Hub intended to send — a hosted tool's declaration
        // is exactly the thing nobody notices when it sends the wrong field, and the field names Core's table predicts
        // must share one source with the names the bridge actually writes, otherwise "what we declared" and "what the
        // gateway received" would become two assertions that can each lie.
        var declared = new List<string>
        {
            ProviderServerTools.WebSearch
        };
        var chatOn = WebSearchHosted.Decide(declared, ModelProtocols.Chat, true);
        var responsesOn = WebSearchHosted.Decide(declared, ModelProtocols.Responses, true);
        var geminiOn = WebSearchHosted.Decide(declared, ModelProtocols.Gemini, true);
        var switched = WebSearchHosted.Decide(declared, ModelProtocols.Chat, false);
        var ask = WebSearchHosted.Decide(declared, ModelProtocols.Chat, true);
        var plan = WebSearchHosted.Decide(declared, ModelProtocols.Chat, true);
        var silent = WebSearchHosted.Decide([], ModelProtocols.Chat, true);
        var openAiOAuth = new ModelProvider
        {
            Id = "openai",
            Credential = new ProviderCredential
            {
                AuthKind = "openaiOAuth",
                Secret = "fixture"
            },
        };
        var openAiApiKey = new ModelProvider
        {
            Id = "openai",
            Credential = new ProviderCredential
            {
                AuthKind = "apiKey",
                Secret = "fixture"
            },
        };
        var oauthSearch = WebSearchHosted.DecideForProvider(openAiOAuth, ModelProtocols.Responses, true);
        var oauthOff = WebSearchHosted.DecideForProvider(openAiOAuth, ModelProtocols.Responses, false);
        var oauthWrongWire = WebSearchHosted.DecideForProvider(openAiOAuth, ModelProtocols.Chat, true);
        var apiKeySearch = WebSearchHosted.DecideForProvider(openAiApiKey, ModelProtocols.Responses, true);
        if (chatOn.Verdict != WebSearchHostedVerdict.On || chatOn.WireField != WebSearchHosted.ChatWireField || responsesOn.Verdict != WebSearchHostedVerdict.On || responsesOn.WireField != WebSearchHosted.ResponsesWireField || geminiOn.Verdict != WebSearchHostedVerdict.On || geminiOn.WireField != WebSearchHosted.GoogleSearchWireField || switched.Verdict != WebSearchHostedVerdict.SwitchedOff || ask.Verdict != WebSearchHostedVerdict.On || plan.Verdict != WebSearchHostedVerdict.On || silent.Verdict != WebSearchHostedVerdict.NotDeclared || oauthSearch.Verdict != WebSearchHostedVerdict.On || oauthSearch.WireField != WebSearchHosted.ResponsesWireField || oauthOff.Verdict != WebSearchHostedVerdict.SwitchedOff || oauthWrongWire.Verdict != WebSearchHostedVerdict.NotDeclared || apiKeySearch.Verdict != WebSearchHostedVerdict.NotDeclared)
            throw new Exception("The hosted-search decision table answered one of its three questions wrong.");
        Console.WriteLine("PASS: hosted search requires opt-in; OpenAI OAuth gets Responses search while API keys and other routes stay explicit.");
        // Each refusal has to say what would change it, because the ordinary failure of this feature is silence: the
        // person sees no search and nothing on screen ever claimed one was possible.
        if (!WebSearchHosted.Explain(silent).Contains("serverTools", StringComparison.Ordinal) || !WebSearchHosted.Explain(switched).Contains("设置", StringComparison.Ordinal))
            throw new Exception("A hosted-search refusal did not name the thing that would have to change.");
        Console.WriteLine("PASS: each refusal points at the provider capability or the separate search switch.");
        // Ordinal, because the kind string is what the service looks up: a case-folded match would let a manifest
        // claim Web_Search, send it, and get nothing back — a bug with no error message anywhere.
        if (ProviderServerTools.DeclaresWebSearch(["Web_Search"]) || ProviderServerTools.DeclaresWebSearch(null) || !ProviderServerTools.DeclaresWebSearch([ProviderServerTools.WebSearch]))
            throw new Exception("The server-tool kind matched by something other than an ordinal string.");
        Console.WriteLine("PASS: the server-tool kind matches by ordinal, since it goes on the wire verbatim.");
        // Only presets whose native search grounding has been wired (currently google-gemini) may declare
        // serverTools in the shipped catalog; every other preset stays explicit opt-in.
        var sawGeminiHostedSearch = false;
        foreach (var preset in AiProviderManifest.Load())
        {
            if (preset.Id == "google-gemini")
            {
                if (!preset.ServerTools.SequenceEqual([ProviderServerTools.WebSearch]))
                    throw new Exception($"The google-gemini preset must declare only web_search, saw ({string.Join(", ", preset.ServerTools)}).");
                sawGeminiHostedSearch = true;
            }
            else if (preset.ServerTools.Count > 0)
            {
                throw new Exception($"The preset '{preset.Id}' declares serverTools ({string.Join(", ", preset.ServerTools)}) " + "while no gateway has been measured honouring them.");
            }
        }
        if (!sawGeminiHostedSearch)
            throw new Exception("The google-gemini preset was missing from the shipped catalog.");

        Console.WriteLine("PASS: only google-gemini declares hosted web_search by default; all other shipped presets declare none.");
        static ModelProvider WireLine(string protocol, bool declares) => new()
        {
            IsCustom = true,
            Name = "Wire fixture",
            BaseUrl = protocol switch
            {
                ModelProtocols.Responses => "http://127.0.0.1:9/responses",
                ModelProtocols.Gemini => "http://127.0.0.1:9/v1beta",
                _ => "http://127.0.0.1:9/v1",
            },
            Protocol = protocol == ModelProtocols.Gemini ? ModelProtocols.Gemini : null,
            Model = "m",
            ServerTools = declares ? [ProviderServerTools.WebSearch] : [],
            ModelCapabilities = new Dictionary<string, ModelCapabilities>(StringComparer.Ordinal)
            {
                ["m"] = new ModelCapabilities
                {
                    EndpointTypes = [protocol],
                },
            },
        };
        async Task<string> BodyFor(ModelProvider provider, ChatOptions options, IEnumerable<ChatMessage> messages)
        {
            var recorder = new WireRecorder();
            // The production factory, with the transport seam a real caller never passes: what is asserted below is
            // the JSON the shipped bridge wrote, not a description of it.
            var client = ChatClientFactory.Create(provider, "m", null, null, recorder);
            try
            {
                await client.GetResponseAsync(messages.ToList(), options);
            }
            catch (Exception ex)
            {
                throw new Exception($"The fixture call to the {provider.BaseUrl} bridge threw " + $"{ex.GetType().Name}: {ex.Message}");
            }

            return recorder.Body;
        }

        ChatOptions WithDeclaration(bool functionToo) => new()
        {
            Tools = functionToo ? [..new AITool[]
            {
                AIFunctionFactory.Create(() => Task.FromResult("page"), new AIFunctionFactoryOptions { Name = "web_fetch", Description = "read one page" }),
                new HostedWebSearchTool(),
            }

            ] : [new HostedWebSearchTool()],
        };
        var question = new List<ChatMessage>
        {
            new(ChatRole.User, "3.1 出了什么")
        };
        // The needles below come from the decision table's own prediction, not from a second copy of the literal.
        // That is what makes "we declared it" and "the bridge sent it" one statement: renaming
        // `WebSearchHosted.ResponsesWireField` to a field no bridge writes used to leave all eleven cells green,
        // because the table compared the claim against itself and the byte cell had its own string to look for.
        static string PredictedNeedle(string field)
        {
            var split = field.IndexOf('=', StringComparison.Ordinal);
            if (split < 0)
                return $"\"{field}\":{{";
            var key = field[(field.IndexOf(".", StringComparison.Ordinal) + 1)..split];
            return $"{{\"{key}\":\"{field[(split + 1)..]}\"}}";
        }

        var chatBody = await BodyFor(WireLine(ModelProtocols.Chat, true), WithDeclaration(true), question);
        if (!chatBody.Contains(PredictedNeedle(chatOn.WireField), StringComparison.Ordinal) || !chatBody.Contains("\"name\":\"web_fetch\"", StringComparison.Ordinal))
            throw new Exception($"The chat bridge did not put the declaration where the table says it goes " + $"(predicted {chatOn.WireField}, looking for {PredictedNeedle(chatOn.WireField)}). Body: {chatBody}");
        Console.WriteLine("PASS: on the chat line the declaration lands in web_search_options, beside the function tools.");
        var responsesBody = await BodyFor(WireLine(ModelProtocols.Responses, true), WithDeclaration(true), question);
        if (responsesBody.Contains("\"prompt_cache_key\"", StringComparison.Ordinal))
            throw new Exception("A standalone Responses call generated a cache key without a session.");
        if (!responsesBody.Contains(PredictedNeedle(responsesOn.WireField), StringComparison.Ordinal) || !responsesBody.Contains("\"name\":\"web_fetch\"", StringComparison.Ordinal))
            throw new Exception($"The responses bridge did not add the tool entry the table predicts " + $"(predicted {responsesOn.WireField}, looking for {PredictedNeedle(responsesOn.WireField)}). " + $"Body: {responsesBody}");
        Console.WriteLine("PASS: on the responses line it lands as a tools[] entry beside the function definitions.");
        var geminiBody = await BodyFor(WireLine(ModelProtocols.Gemini, true), WithDeclaration(true), question);
        if (!geminiBody.Contains("\"googleSearch\":{", StringComparison.Ordinal)
            || !geminiBody.Contains("\"functionDeclarations\"", StringComparison.Ordinal)
            || !geminiBody.Contains("\"name\":\"web_fetch\"", StringComparison.Ordinal)
            || !geminiBody.Contains("\"includeServerSideToolInvocations\":true", StringComparison.Ordinal))
            throw new Exception($"The Gemini native bridge did not combine googleSearch, functionDeclarations, and includeServerSideToolInvocations. Body: {geminiBody}");
        Console.WriteLine("PASS: on the Gemini native line it lands in tools[].googleSearch beside functionDeclarations with includeServerSideToolInvocations.");
        var quietBody = await BodyFor(WireLine(ModelProtocols.Chat, false), new ChatOptions { Tools = [AIFunctionFactory.Create(() => Task.FromResult("page"), new AIFunctionFactoryOptions { Name = "web_fetch", Description = "read one page" })], }, question);
        if (quietBody.Contains("web_search", StringComparison.OrdinalIgnoreCase))
            throw new Exception($"An undeclared provider still sent a search field: {quietBody}");
        Console.WriteLine("PASS: a provider that declares nothing sends neither field on either wire.");
        // The half that decides the transcript design. Feeding the item back is what "preserve it verbatim for replay"
        // would mean, and it was measured on both bridges: the chat line serializes the assistant message as an empty
        // content string — losing the item *and* the text of that turn — and the responses line drops the message from
        // input altogether. Neither throws, which is the reason Hub records the fact instead of replaying it.
        var replay = new List<ChatMessage>
        {
            new(ChatRole.User, "3.1 出了什么"),
            new(ChatRole.Assistant, [new WebSearchToolCallContent("ws-1") { Queries = ["axmol 3.1"] }]),
            new(ChatRole.Assistant, [new WebSearchToolResultContent("ws-1") { Outputs = [new UriContent("https://axmol.dev/blog")], }]),
            new(ChatRole.Assistant, "答案是 3.1.2"),
        };
        var replayChat = await BodyFor(WireLine(ModelProtocols.Chat, true), WithDeclaration(false), replay);
        var replayResponses = await BodyFor(WireLine(ModelProtocols.Responses, true), WithDeclaration(false), replay);
        if (replayChat.Contains("axmol.dev/blog", StringComparison.Ordinal) || replayResponses.Contains("axmol.dev/blog", StringComparison.Ordinal))
            throw new Exception("A replayed search item reached the body after all; the record-only design needs rethinking.");
        if (!replayChat.Contains("{\"role\":\"assistant\",\"content\":\"\"}", StringComparison.Ordinal))
            throw new Exception("The measured chat-line corruption (an emptied assistant message) is the premise of the " + "record-only design; if the bridge changed, the premise and this cell both have to be re-read.");
        Console.WriteLine("PASS: a search item replayed in history reaches neither wire, and the emptied assistant " + "message that made it record-only is still what the chat bridge does.");
        // Hub's own boundary: the stored field is read by the view, never by the request builder. Asserted next to the
        // bridge measurement so the two halves of "record, do not replay" cannot drift apart.
        var storedTurn = new ChatTurn(ChatRoles.Assistant, "答案是 3.1.2", DateTimeOffset.Now)
        {
            WebSearch = new ServerSearchLog([new ServerSearchNotice("ws-1", ["axmol 3.1"], ["https://axmol.dev/blog"], false)]).ToStored()!,
        };
        var boundary = ChatPipeline.ToChatMessage(storedTurn);
        if (boundary.Contents.Count != 1 || boundary.Text.Contains("axmol.dev/blog", StringComparison.Ordinal))
            throw new Exception("The request boundary leaked the search record into the message.");
        Console.WriteLine("PASS: the request boundary carries the reply and none of the search record beside it.");
        var log = new ServerSearchLog([new ServerSearchNotice("ws-1", ["第一个查询"], ["https://a.test/one", "https://b.test"], false), new ServerSearchNotice("ws-2", ["第一个查询", "第二个查询"], ["https://a.test/one"], false), ]);
        var round = ServerSearchLog.Read(log.ToStored()!);
        if (round.Entries.Count != 2 || round.Queries().Count != 2 || round.Sources().Count != 2 || round.Sources()[0] != "https://a.test/one" || round.Queries()[1] != "第二个查询")
            throw new Exception("The search record did not round-trip, or lost the order the endpoint reported.");
        if (new ServerSearchLog([]).ToStored()is not null || ServerSearchLog.Read(null).IsEmpty != true || ServerSearchLog.Read("{ not json").IsEmpty != true)
            throw new Exception("An empty or unreadable search record was stored, or read back as something to show.");
        Console.WriteLine("PASS: two searches in one reply keep both queries and de-duplicate sources in order; " + "nothing worth showing stores nothing and unreadable JSON reads as no searches.");
        // An older conversation file has no such key at all, and has to keep loading — the field is nullable precisely
        // so a session written before server-side tools existed is not a migration.
        var legacyRoot = Path.Combine(root, "web-search-legacy");
        var legacySessions = Path.Combine(legacyRoot, "ai", "sessions");
        Directory.CreateDirectory(legacySessions);
        File.WriteAllText(Path.Combine(legacySessions, "old.json"), """{"Id":"old","Title":"旧会话","Messages":[{"Role":"user","Text":"看过的一问","At":"2026-01-01T00:00:00+08:00"},""" + """{"Role":"assistant","Text":"一答","At":"2026-01-01T00:00:05+08:00"}]}""");
        var legacy = new ConversationStore(legacyRoot).Load("old");
        if (legacy is null || legacy.Messages.Count != 2 || legacy.Messages[1].WebSearch is not null)
            throw new Exception("A conversation written before the search field existed did not load, or invented a record.");
        new ConversationStore(legacyRoot).Save(legacy);
        if (new ConversationStore(legacyRoot).Load("old")?.Messages[1].WebSearch is not null)
            throw new Exception("Saving an old conversation gave it an empty search record.");
        Directory.Delete(legacyRoot, recursive: true);
        Console.WriteLine("PASS: a session file written before this field existed still loads, and saving it back " + "adds no search record.");
        // The recorder that exists to answer "what actually went out" is only worth keeping if a later launch cannot
        // overwrite it. Pair numbers restart at one in every process, so a shared directory quietly replaced the
        // evidence of a failure with another launch's request — measured on 2026-10-21, when a failure was recorded and
        // gone by the time it was read (83 index lines against 36 pairs on disk). One folder per launch, plus a time on
        // every index line so a log line can be matched to the pair it belongs to.
        var wireRoot = Path.Combine(root, "wire-launch");
        WireLog.Directory = wireRoot;
        try
        {
            WireLog.TryWrite("0001.request.json", "{}");
            WireLog.TryAppendIndex("0001 completions tools=0");
            var launches = Directory.GetDirectories(wireRoot);
            if (launches.Length != 1 || !File.Exists(Path.Combine(launches[0], "0001.request.json")))
                throw new Exception("The wire recording did not get a folder of its own per launch: " + $"{(launches.Length == 0 ? "nothing was written" : string.Join(", ", launches))}.");
            var indexLine = File.ReadAllLines(Path.Combine(launches[0], "index.log")).Single();
            var space = indexLine.IndexOf(' ', StringComparison.Ordinal);
            if (space <= 0 || !DateTimeOffset.TryParse(indexLine[..space], out _))
                throw new Exception($"A wire index line carries no timestamp to match a failure against: 「{indexLine}」");
        }
        finally
        {
            WireLog.Directory = null;
            if (Directory.Exists(wireRoot))
                Directory.Delete(wireRoot, recursive: true);
        }

        Console.WriteLine("PASS: the wire recording lands in one folder per launch, and every index line is timestamped.");
        return;
    }
}
