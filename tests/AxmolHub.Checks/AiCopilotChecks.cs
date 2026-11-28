using System.Text;
using AxmolHub.Core;
using AxmolHub.Agent;

internal static class AiCopilotChecks
{
    internal static async Task RunAsync(string root, string[] args)
    {
        void Assert(bool condition, string name)
        {
            if (!condition)
                throw new Exception("FAILED: " + name);
            Console.WriteLine("PASS: " + name);
        }

        var copilot = AiProviderManifest.CreateBuiltIn("github-copilot") ?? throw new Exception("github-copilot is missing from ai-providers.json.");
        // Packaging injects the real ID. This check only talks to the in-memory transport.
        const string FixtureClientId = "offline-copilot-check";
        if (copilot.OAuth is not null) copilot.OAuth.ClientId = FixtureClientId;
        Assert(copilot.SupportsOAuth, "a device-code preset offers browser sign-in with no discovery document to point at");
        Assert(copilot.OAuth?.EffectiveFlow == ProviderOAuthFlows.DeviceCode, "the Copilot preset uses the device-code flow");
        Assert(copilot.RequestSemantics == ProviderRequestSemantics.Copilot && copilot.ExtraHeaders.Count > 0 && copilot.CapabilitySource == "github-copilot-models", "headers, request semantics and the catalog source survive the manifest-to-provider trip");
        Assert(!copilot.Affiliate && copilot.ReferralUrl is null, "Copilot carries no affiliate declaration — the referral arrangement is OrcaRouter's, not GitHub's");
        // The endpoint guard: these are the addresses a credential is handed over at, so a hand-edited manifest
        // must not be able to aim them somewhere else. Loopback http stays legal (a self-hosted deployment), and a
        // half-declared flow is refused here rather than at the moment the user is waiting on a code.
        Assert(!new AiProviderOAuth { Flow = ProviderOAuthFlows.DeviceCode, ClientId = "x", DeviceAuthorizationUrl = "http://evil.test/device", TokenUrl = "https://github.com/login/oauth/access_token", }.IsUsableFlow, "a plain http device-code endpoint on a remote host is refused");
        Assert(new AiProviderOAuth { Flow = ProviderOAuthFlows.DeviceCode, ClientId = "x", DeviceAuthorizationUrl = "http://127.0.0.1:8965/login/device/code", TokenUrl = "http://127.0.0.1:8965/token", }.IsUsableFlow, "loopback http stays legal for a self-hosted deployment");
        Assert(!new AiProviderOAuth { Flow = ProviderOAuthFlows.DeviceCode, DeviceAuthorizationUrl = "https://github.com/login/device/code", TokenUrl = "https://github.com/login/oauth/access_token", }.IsUsableFlow, "a device flow with no client id is not offered as a button");
        Assert(!new AiProviderOAuth { Flow = ProviderOAuthFlows.DeviceCode, ClientId = "x", DeviceAuthorizationUrl = "https://github.com/login/device/code", }.IsUsableFlow, "a device flow that cannot poll for the token is refused");
        Assert(new AiProviderOAuth { DiscoveryUrl = "https://example.test/.well-known/openid-configuration" }.IsUsableFlow, "the discovery shape the shipped presets use still counts as usable");
        Assert(!new AiProviderOAuth().IsUsableFlow, "an empty oauth block promises nothing and is therefore not offered");
        // Poll arithmetic (RFC 8628 §3.4/§3.5).
        var epoch = DateTimeOffset.UnixEpoch;
        var expiry = epoch.AddMinutes(15);
        DeviceCodeOAuthFlow.DevicePollKind KindOf(DeviceCodeOAuthFlow.DeviceTokenResponse response) => DeviceCodeOAuthFlow.Classify(response, TimeSpan.FromSeconds(5), epoch, expiry).Kind;
        Assert(KindOf(new DeviceCodeOAuthFlow.DeviceTokenResponse(Error: "authorization_pending")) == DeviceCodeOAuthFlow.DevicePollKind.Waiting, "authorization_pending keeps polling");
        Assert(KindOf(new DeviceCodeOAuthFlow.DeviceTokenResponse(AccessToken: "gho_token")) == DeviceCodeOAuthFlow.DevicePollKind.Approved, "an access token ends the wait");
        Assert(KindOf(new DeviceCodeOAuthFlow.DeviceTokenResponse(Error: "access_denied")) == DeviceCodeOAuthFlow.DevicePollKind.Declined, "access_denied is reported as the user declining, not as a transport failure");
        Assert(KindOf(new DeviceCodeOAuthFlow.DeviceTokenResponse(Error: "expired_token")) == DeviceCodeOAuthFlow.DevicePollKind.Expired, "expired_token tells them to start again");
        Assert(KindOf(new DeviceCodeOAuthFlow.DeviceTokenResponse(Error: "something_new")) == DeviceCodeOAuthFlow.DevicePollKind.Refused, "an error we do not know ends the sign-in instead of spinning for five minutes");
        Assert(KindOf(new DeviceCodeOAuthFlow.DeviceTokenResponse(AccessToken: "gho_token", Error: "access_denied")) == DeviceCodeOAuthFlow.DevicePollKind.Declined, "a body carrying both a token and an error is believed as the error");
        Assert(DeviceCodeOAuthFlow.Classify(new DeviceCodeOAuthFlow.DeviceTokenResponse(), TimeSpan.FromSeconds(5), epoch.AddMinutes(20), expiry).Kind == DeviceCodeOAuthFlow.DevicePollKind.Expired, "a silent wait past the code's own expiry stops asking");
        var floor = new AiProviderOAuth
        {
            MinIntervalSeconds = 5
        };
        Assert(DeviceCodeOAuthFlow.NextInterval(TimeSpan.FromSeconds(5), floor, new DeviceCodeOAuthFlow.DeviceTokenResponse(Error: "slow_down")) == TimeSpan.FromSeconds(13), "slow_down adds the RFC's five seconds to the current interval, plus the skew margin");
        Assert(DeviceCodeOAuthFlow.NextInterval(TimeSpan.FromSeconds(5), floor, new DeviceCodeOAuthFlow.DeviceTokenResponse(Interval: 30)) == TimeSpan.FromSeconds(33), "a server-named interval wins over our own arithmetic");
        Assert(DeviceCodeOAuthFlow.NextInterval(TimeSpan.FromSeconds(1), floor, null) == TimeSpan.FromSeconds(8), "the manifest floor keeps a mocked or hostile interval from turning a poll into a flood");
        // The same rules over an injected transport, because the arithmetic being right is not the same as the loop
        // being right. Two of these exist for bugs that were real here: a classifier that answered "some error" before
        // checking for authorization_pending ended the sign-in the first time a person took a second longer to type,
        // and every assertion above kept passing while it did.
        static async Task<Exception?> Caught(Func<Task> action)
        {
            try
            {
                await action();
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        var wire = new AiProviderOAuth
        {
            Flow = ProviderOAuthFlows.DeviceCode,
            ClientId = FixtureClientId,
            DeviceAuthorizationUrl = "https://github.com/login/device/code",
            TokenUrl = "https://github.com/login/oauth/access_token",
            Scope = "read:user",
            MinIntervalSeconds = 1,
        };
        const string Issued = """
        {"device_code":"dc-1","user_code":"WXYZ-5678",
         "verification_uri":"https://github.com/login/device","expires_in":900,"interval":1}
        """;
        DeviceCodeChallenge? reported = null;
        var approvedTransport = new DeviceCodeTransport(Issued, """{"access_token":"gho_token","scope":"read:user"}""");
        var approved = await new DeviceCodeOAuthFlow(new HttpClient(approvedTransport)).SignInAsync(wire, new InlineProgress<DeviceCodeChallenge>(challenge => reported = challenge));
        Assert(approved.Key == "gho_token" && approved.Scope == "read:user", "an approved device code comes back as the token the provider issued");
        Assert(reported is { UserCode: "WXYZ-5678" } && reported.VerificationUri == "https://github.com/login/device", "the surface is handed the user code and the address from the response, not from the manifest");
        Assert(approvedTransport.Requests.Count == 2 && approvedTransport.Requests[0].StartsWith("POST /login/device/code ") && approvedTransport.Requests[0].Contains($"\"client_id\":\"{FixtureClientId}\"") && approvedTransport.Requests[0].Contains("read:user"), "the code request goes to the declared device endpoint with the public id and the declared scope");
        Assert(approvedTransport.Requests[1].Contains("urn:ietf:params:oauth:grant-type:device_code") && approvedTransport.Requests[1].Contains("dc-1"), "the poll is the device grant carrying the code that was issued");
        Assert(approvedTransport.Requests.All(entry => !entry.Contains("client_secret", StringComparison.Ordinal)), "no request on either arm ever carries a client secret");
        // Nobody approved this one, and the wait is what has to say so. The clamp is what makes the assertion take
        // milliseconds rather than the four-second poll interval it would otherwise sleep through: sleeping past a
        // deadline the loop already computed is a wait nobody asked for.
        var silentStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var silentTransport = new DeviceCodeTransport(Issued, """{"error":"authorization_pending"}""");
        var silent = await Caught(() => new DeviceCodeOAuthFlow(new HttpClient(silentTransport), wait: TimeSpan.FromMilliseconds(150)).SignInAsync(wire));
        var silentElapsed = System.Diagnostics.Stopwatch.GetTimestamp() - silentStart;
        Assert(silent is InvalidOperationException && silentTransport.Polls >= 1 && silentTransport.Polls <= 4 && System.Diagnostics.Stopwatch.GetElapsedTime(silentStart) < TimeSpan.FromSeconds(2), "an unapproved code ends on the clock, not one poll interval late (waited " + silentElapsed * 1000 / System.Diagnostics.Stopwatch.Frequency + " ms over " + silentTransport.Polls + " polls, " + (silent?.GetType().Name ?? "no exception") + ")");
        // A cancel arriving *between* polls — which is also the proof that pending is a wait rather than an error:
        // had the classifier called it a refusal, this would have surfaced that refusal instead of a cancellation.
        using (var closeWindow = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
        {
            var waitingTransport = new DeviceCodeTransport(Issued, """{"error":"authorization_pending"}""");
            var closed = await Caught(() => new DeviceCodeOAuthFlow(new HttpClient(waitingTransport), wait: TimeSpan.FromSeconds(30)).SignInAsync(wire, progress: null, closeWindow.Token));
            Assert(closed is OperationCanceledException && waitingTransport.Polls >= 1, "closing the code window stops the wait between polls, and a cancelled sign-in hands back no token");
        }

        var declinedTransport = new DeviceCodeTransport(Issued, """{"error":"access_denied"}""");
        var declined = await Caught(() => new DeviceCodeOAuthFlow(new HttpClient(declinedTransport)).SignInAsync(wire));
        Assert(declined is InvalidOperationException && declined.Message.Contains("declined", StringComparison.Ordinal), "the person declining on their device ends the sign-in with a reason they can read");
        var proxyTransport = new DeviceCodeTransport("<html>502 Bad Gateway</html>");
        var misshapen = await Caught(() => new DeviceCodeOAuthFlow(new HttpClient(proxyTransport)).SignInAsync(wire));
        Assert(misshapen is InvalidOperationException && proxyTransport.Polls == 0, "a proxy's error page where the code should be is a refusal, not a parse crash and not a poll");
        // The catalog, read from the captured bytes.
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "copilot-models.json");
        if (!File.Exists(fixture))
            throw new Exception($"FAILED: the Copilot catalog fixture is missing from the output ({fixture}).");
        var batch = GithubCopilotModelsSource.Parse(File.ReadAllBytes(fixture));
        Assert(batch.Reachable && batch.Problem is null, "the captured catalog parses without a problem");
        Assert(batch.Models.SequenceEqual(new[] { "gpt-5.3-codex", "gpt-5.4", "kimi-k3", "gpt-5-mini" }, StringComparer.Ordinal), "internal agents, embeddings and un-picked legacy rows are dropped while the catalog's own default survives");
        var kimi = batch.Capabilities["kimi-k3"];
        Assert(kimi.ContextTokens == 1048576 && kimi.MaxOutputTokens == 131072, "the model's own window replaces the 8192 fallback");
        Assert(kimi.Source == CapabilitySource.EndpointReported, "a number the endpoint published is labelled as one, not as a preset's claim");
        Assert(kimi.AcceptsInput("image"), "supports.vision becomes an image input modality");
        Assert(kimi.Protocol == ModelProtocols.Chat, "a chat-only model stays on the chat wire");
        Assert(batch.Reasoning["kimi-k3"].Efforts.SequenceEqual(new[] { "low", "high", "max" }), "reasoning_effort maps onto Hub's tiers, including max");
        Assert(batch.Capabilities["gpt-5.3-codex"].Protocol == ModelProtocols.Responses, "a /responses-only model is not offered on the wire that rejects it");
        Assert(batch.Capabilities["gpt-5.4"].Protocol == ModelProtocols.Chat, "a model answering on both wires is called on the one this app has proven");
        Assert(!batch.Capabilities["gpt-5.4"].EffortLevels.Contains("none"), "an effort level Hub does not implement is dropped rather than normalized into a menu entry");
        Assert(batch.Capabilities["gpt-5.4"].EndpointTypes.Count(name => name == ModelProtocols.Responses) == 1, "the websocket alias collapses onto its protocol instead of doubling the list");
        // The third name, driven through the real parser rather than constructed in memory. Copilot's Claude rows list
        // /v1/messages beside /chat/completions, so both halves matter: the path must be *named* (not dropped as an
        // unknown value, which is what made the old literal invisible to everything downstream) and naming it must not
        // move a model that also answers on chat off the wire this build speaks with a bearer token.
        var anthropicRow = GithubCopilotModelsSource.Parse(Encoding.UTF8.GetBytes("""
        {"data":[
         {"id":"claude-only","object":"model","name":"Claude only","vendor":"Anthropic",
          "capabilities":{"object":"model_capabilities","type":"chat","limits":{"max_context_window_tokens":200000}},
          "supported_endpoints":["/v1/messages"]},
         {"id":"claude-both","object":"model","name":"Claude both","vendor":"Anthropic",
          "capabilities":{"object":"model_capabilities","type":"chat","limits":{"max_context_window_tokens":200000}},
          "supported_endpoints":["/chat/completions","/v1/messages","/v1/messages/count_tokens"]}
        ]}
        """));
        Assert(anthropicRow.Reachable && anthropicRow.Models.Contains("claude-only"), "a row whose only endpoint is /v1/messages is still an offered model");
        Assert(anthropicRow.Capabilities["claude-only"].Protocol == ModelProtocols.Messages, "a model that appears nowhere else is named as the messages wire instead of defaulting to chat");
        Assert(anthropicRow.Capabilities["claude-both"].Protocol == ModelProtocols.Chat, "a Copilot model that also answers on chat stays on the bearer scheme Hub actually has for it");
        var html = GithubCopilotModelsSource.Parse(Encoding.UTF8.GetBytes("<html>502 Bad Gateway</html>"));
        Assert(html.Models.Count == 0 && html.Reachable, "a proxy's error page reads as no models, not as a failed sign-in");
        Assert(new ModelCapabilities().Protocol == ModelProtocols.Chat, "a provider that publishes nothing about protocols stays on chat");
        // Another real shape of the catalog, captured from GitHub on 2026-10-09: the list an OAuth app it does not
        // recognise (our device code) receives has no gpt-5, all eight rows are model_picker_enabled:false, and the
        // older rows have no vision sub-object under limits at all. What follows is the same shape trimmed to three
        // rows. Miss one ValueKind check and calling TryGetProperty on an Undefined JsonElement throws
        // InvalidOperationException, so the settings page reports a genuine HTTP 200 catalog as "Operation not valid"
        // — the request shape is innocent, the parser is guilty.
        var reduced = GithubCopilotModelsSource.Parse(Encoding.UTF8.GetBytes("""
        {"data":[
         {"id":"gpt-4o-mini","object":"model","name":"GPT-4o mini","vendor":"OpenAI",
          "capabilities":{"object":"model_capabilities","type":"chat","tokenizer":"o200k_base",
            "limits":{"max_context_window_tokens":128000,"max_output_tokens":16384,"max_prompt_tokens":128000},
            "supports":{"parallel_tool_calls":true,"streaming":true,"tool_calls":true}},
          "is_chat_default":true,"is_chat_fallback":false,"model_picker_enabled":false,"preview":false},
         {"id":"gpt-4o","object":"model","name":"GPT-4o","vendor":"OpenAI",
          "capabilities":{"object":"model_capabilities","type":"chat","tokenizer":"o200k_base",
            "limits":{"max_context_window_tokens":128000,"vision":{"supported_media_types":["image/jpeg","image/png"]}},
            "supports":{"streaming":true,"vision":true,"tool_calls":true}},
          "is_chat_default":false,"is_chat_fallback":true,"model_picker_enabled":false,"preview":false},
         {"id":"text-embedding-3-small","object":"model","name":"Text Embedding 3 Small",
          "capabilities":{"object":"model_capabilities","type":"embeddings","limits":{"max_input_tokens":8191}},
          "model_picker_enabled":false,"preview":false}
        ]}
        """));
        Assert(reduced.Reachable && reduced.Problem is null, "a legacy catalog row whose limits carry no vision child is an answer, not a broken endpoint");
        Assert(reduced.Models.SequenceEqual(new[] { "gpt-4o-mini", "gpt-4o" }, StringComparer.Ordinal), "an all-unpicked catalog still offers the two rows GitHub names as the chat default and fallback");
        Assert(reduced.Capabilities["gpt-4o-mini"].ContextTokens == 128000 && reduced.Capabilities["gpt-4o-mini"].MaxOutputTokens == 16384, "the window still reads out of a limits object that has no vision block");
        Assert(!reduced.Capabilities["gpt-4o-mini"].AcceptsInput("image") && reduced.Capabilities["gpt-4o"].AcceptsInput("image"), "a model never reported as taking pictures is not handed one by the parser");
        Assert(!reduced.Models.Contains("text-embedding-3-small"), "the embeddings row is still dropped even when nothing in the catalog is pickable");
        // A local server, asked the two questions the OpenAI shape cannot answer. The numbers below are the ones that
        // decide whether a long conversation gets compacted or quietly loses its beginning, and they are the real ones:
        // the qwen3-coder on this machine reports 262144 trained tokens, pins no num_ctx at all, and is therefore served
        // with the server's own default — the trained figure is the one a reader reaches for and the wrong one.
        var shown = OllamaModelsSource.ParseShow(Encoding.UTF8.GetBytes("""
        {"license":"Apache-2.0","modelfile":"# Modelfile generated by \"ollama show\"",
         "parameters":"stop    \"<|im_end|>\"\nnum_ctx    8192",
         "template":"{{ .System }}",
         "details":{"family":"qwen3","parameter_size":"30.5B","quantization_level":"Q4_K_M"},
         "model_info":{"qwen3.context_length":262144,"qwen3.embedding_length":2048},
         "capabilities":["completion","tools"]}
        """));
        Assert(shown.TrainedContext == 262144 && shown.ParameterContext == 8192, "the trained window and the pinned num_ctx are read as two different facts");
        Assert(shown.Resolve(null) == new OllamaWindow(8192, CapabilitySource.EndpointReported), "the Modelfile's num_ctx is the window, not the 262144 the weights were trained for");
        Assert(shown.Resolve(4096) == new OllamaWindow(4096, CapabilitySource.EndpointReported), "a window the server reports for the model it has loaded outranks the Modelfile");
        Assert(shown.DeclaresCapabilities && !shown.Vision, "a capabilities list that does not name vision is an answer, not silence");
        var pinned = OllamaModelsSource.Merge(new ModelCapabilities { ContextTokens = 256000, Source = CapabilitySource.ManifestDeclared }, shown.Resolve(null), shown);
        Assert(pinned.ContextTokens == 8192 && pinned.Source == CapabilitySource.EndpointReported, "the server's own number replaces the preset's 256000 and says where it came from");
        Assert(!pinned.AcceptsInput("image"), "a model whose capability list leaves vision out is not handed a screenshot");
        // A model whose Modelfile pins nothing — the shape every model on this machine actually has. Its trained maximum
        // is 131072 and the server will hand it 4096, so believing the trained number is exactly the bug: Hub would
        // build a request the server cuts down in silence and then read the truncation as a model that lost the thread.
        var unpinned = OllamaModelsSource.ParseShow(Encoding.UTF8.GetBytes("""
        {"parameters":"stop    \"<|end|>\"","model_info":{"llama.context_length":131072}}
        """));
        Assert(unpinned.ParameterContext is null && unpinned.Resolve(null) == new OllamaWindow(OllamaModelsSource.DefaultContextTokens, CapabilitySource.Fallback), "with no num_ctx pinned the server's own default sizes the request, not the trained maximum");
        Assert(OllamaModelsSource.DefaultContextTokens == 4096, "the default is a real local window — not a hosted one, and not a number a model cannot answer in");
        Assert(!unpinned.DeclaresCapabilities && OllamaModelsSource.Merge(null, unpinned.Resolve(null), unpinned).AcceptsInput("image"), "a server that describes no capabilities must not take screenshots away from a model");
        var smallModel = OllamaModelsSource.ParseShow(Encoding.UTF8.GetBytes("""{"model_info":{"m.context_length":2048}}"""));
        Assert(OllamaModelsSource.Merge(null, smallModel.Resolve(32768), smallModel).ContextTokens == 32768, "a running window larger than the trained maximum is still the number the server is using");
        Assert(OllamaModelsSource.ParseShow(Encoding.UTF8.GetBytes("""{"model_info":{"tiny.context_length":2048},"parameters":"num_ctx 32768"}""")).Resolve(null).Tokens == 2048, "a num_ctx pinned above what the weights were trained for is cut down to the model that has to hold it");
        var noise = OllamaModelsSource.ParseShow(Encoding.UTF8.GetBytes("<html>502 Bad Gateway</html>"));
        Assert(noise == OllamaModelFacts.Empty, "a body that is not JSON answers nothing instead of throwing on the way to a model list");
        // /api/ps: the loaded models and the window each is actually served with. `name` on the answers that carry it,
        // `model` on the ones that only carry that, and the tag Ollama adds and drops at will.
        var running = OllamaModelsSource.ParseRunning(Encoding.UTF8.GetBytes("""
        {"models":[{"name":"qwen3:latest","model":"qwen3:latest","context_length":4096,"size_vram":1},
                   {"model":"gemma4","context_length":8192},
                   {"name":"nosize","size":1}]}
        """));
        Assert(running["qwen3"] == 4096 && running["gemma4"] == 8192 && running.Count == 2, "a running model's context_length is read under either spelling and a row without one is skipped");
        Assert(OllamaModelsSource.NormalizeName("qwen3:latest") == "qwen3" && OllamaModelsSource.NormalizeName("qwen3") == "qwen3", "the :latest tag Ollama adds and omits does not make one model look like two");
        Assert(OllamaModelsSource.ParseRunning(Encoding.UTF8.GetBytes("""{"models":[]}""")).Count == 0, "nothing loaded is an empty answer, not an error");
        // The base URL the OpenAI client needs carries a path Ollama does not serve its own API under.
        Assert(OllamaModelsSource.ApiRoot("http://localhost:11434/v1") == "http://localhost:11434", "/api sits beside /v1 rather than under it, so the suffix comes off");
        Assert(OllamaModelsSource.ApiRoot("https://gpu.lan/ollama/v1/") == "https://gpu.lan/ollama", "a reverse proxy's own path survives the trim");
        Assert(OllamaModelsSource.ApiRoot("http://localhost:11434") == "http://localhost:11434", "a base URL that already names the root is left alone");
        // The preset has to name a source that is registered, or none of the above is ever asked. Asserted in
        // --check-ai-providers, where the manifest's own entries are read.
        // A model that describes its own limits instead of acting, which is what a small model does when it reads
        // "every call waits for approval" as "I am not allowed". The sentence is from the report that started this.
        Assert(ToolCallNudge.LooksLikeDeclinedAction("I understand you want me to actually perform the file operations, but I cannot actually execute " + "shell commands or modify files in your local repositories because I don't have the appropriate " + "authorization to make file system modifications"), "the refusal that prompted this is recognised, not just its exact wording");
        Assert(ToolCallNudge.LooksLikeDeclinedAction("这些操作需要你手动执行，我无法直接修改你的仓库"), "a model refusing in the session's own language is recognised too");
        Assert(!ToolCallNudge.LooksLikeDeclinedAction("Axmol is a cross-platform 2D game engine, so the physics module wraps Box2D."), "an ordinary answer is not corrected");
        Assert(!ToolCallNudge.LooksLikeDeclinedAction(null) && !ToolCallNudge.LooksLikeDeclinedAction("   "), "an empty reply has its own handling and is never nudged");
        Assert(ToolCallNudge.Sentence.Contains("approval", StringComparison.OrdinalIgnoreCase) && ToolCallNudge.Sentence.Contains("call the tool now", StringComparison.OrdinalIgnoreCase), "the correction names the approval card as the mechanism and tells the model to act");
        // The two dynamic headers, decided on a serialized body.
        Assert(!ProviderRequestPolicy.IsToolRound("""{"messages":[{"role":"user","content":"hi"}]}"""), "a human prompt reports x-initiator: user");
        Assert(ProviderRequestPolicy.IsToolRound("""{"messages":[{"role":"user","content":"hi"},{"role":"assistant","tool_calls":[{"id":"c1"}]},{"role":"tool","tool_call_id":"c1","content":"1"}]}"""), "a tool round reports x-initiator: agent, which is what keeps it off the user's premium-request meter");
        Assert(ProviderRequestPolicy.IsToolRound("""{"input":[{"role":"assistant","content":[]}]}"""), "the responses wire is judged by the same rule");
        Assert(!ProviderRequestPolicy.IsToolRound("not json"), "an unreadable body answers user — the expensive direction is the honest one");
        Assert(ProviderRequestPolicy.HasPicture("""{"messages":[{"role":"user","content":[{"type":"text","text":"x"},{"type":"image_url","image_url":{"url":"data:image/png;base64,"}}]}]}"""), "a picture on the chat wire is marked for routing");
        Assert(ProviderRequestPolicy.HasPicture("""{"input":[{"role":"user","content":[{"type":"input_image","image_url":"data:,"}]}]}"""), "a picture on the responses wire is marked too");
        Assert(!ProviderRequestPolicy.HasPicture("""{"messages":[{"role":"user","content":"hi"}]}"""), "a text-only request carries no vision marker");
        // The third body, and the shell that is not a pipeline policy. Anthropic has no `tool` role — a result is a
        // `tool_result` block inside the next *user* message — so a rule that only read roles would report every tool
        // round as a human turn. On a metered provider that is the direction that spends the user's allowance, and it
        // is the exact mistake this class's own doc refuses to make twice.
        Assert(ProviderRequestPolicy.IsToolRound("""{"system":"be brief","messages":[{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}]}"""), "an Anthropic tool round reads as a tool round even though the entry's role is user");
        Assert(!ProviderRequestPolicy.IsToolRound("""{"system":"be brief","messages":[{"role":"user","content":[{"type":"text","text":"what changed?"}]}]}"""), "a user message holding plain text blocks is still a human turn");
        Assert(ProviderRequestPolicy.IsToolRound("""{"messages":[{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"run_command","input":{}}]}]}"""), "an assistant turn asking for a call is a tool round on this body too");
        Assert(ProviderRequestPolicy.HasPicture("""{"messages":[{"role":"user","content":[{"type":"image","source":{"type":"base64","data":""}}]}]}"""), "the Anthropic block type is one the picture rule already knows");
        // The same answers, reached through the plain-request shell a client without a pipeline uses. Parity here is
        // the reason such a client does not get to invent its own copy of the meter rule.
        var probeRequest = new HttpRequestMessage(HttpMethod.Post, "https://mirror.test/v1/messages");
        ProviderRequestPolicy.ApplyTo(probeRequest, new Dictionary<string, string> { ["x-client"] = "axmol-hub" }, ProviderRequestSemantics.Copilot, "interaction-7", """{"messages":[{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}]}""");
        Assert(probeRequest.Headers.TryGetValues("x-client", out var declared) && declared.SequenceEqual(new[] { "axmol-hub" }), "the static headers land on the plain-request shell too");
        Assert(probeRequest.Headers.TryGetValues(ProviderRequestPolicy.InitiatorHeader, out var initiator) && initiator.SequenceEqual(new[] { ProviderRequestPolicy.InitiatorAgent }), "the plain shell derives the same x-initiator the pipeline shell does");
        Assert(probeRequest.Headers.TryGetValues(ProviderRequestPolicy.InteractionIdHeader, out var interaction) && interaction.SequenceEqual(new[] { "interaction-7" }), "the conversation id rides the same request");
        Assert(!probeRequest.Headers.Contains("Authorization"), "no shell writes the credential: that header belongs to whoever authenticates the call");
        // The default user-agent is static; a tool result must not add any Copilot-specific headers here.
        Assert(ProviderRequestPolicy.Headers(new Dictionary<string, string>(), null, "ignored", """{"messages":[{"role":"user","content":[{"type":"tool_result"}]}]}""")
            .SequenceEqual([("user-agent", AxmolHubUserAgent.Value)]), "without family semantics only the default user-agent is sent");
        var manifestText = File.ReadAllText(AiProviderManifest.ManifestPath);
        Assert(!manifestText.Contains("clientSecret", StringComparison.OrdinalIgnoreCase) && !manifestText.Contains("client_secret", StringComparison.OrdinalIgnoreCase), "the shipped manifest names a public client id and never a client secret");
        return;
    }
}
