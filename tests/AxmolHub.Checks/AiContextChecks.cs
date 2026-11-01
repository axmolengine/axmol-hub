using System.Text;
using System.Text.Json;
using AxmolHub.Core;
using AxmolHub.Agent;
using Microsoft.Extensions.AI;

internal static class AiContextChecks
{
    internal static async Task RunAsync(string root, string[] args)
    {
        // The diff is what the user approves, so headers and caps are asserted as text: a wrong hunk header is not
        // cosmetic, it is a preview that does not describe what lands on disk.
        if (FileDiff.Unified("same\n", "same\n", "a.txt").Length != 0)
            throw new Exception("Identical text produced a diff.");
        var created = FileDiff.Unified("", "one\ntwo\n", "src/new.cpp");
        if (!created.Contains("--- /dev/null") || !created.Contains("+++ b/src/new.cpp") || !created.Contains("@@ -0,0 +1,2 @@") || !created.Contains("+one") || !created.Contains("+two"))
            throw new Exception($"A created file did not diff as a pure addition:{Environment.NewLine}{created}");
        var deleted = FileDiff.Unified("one\ntwo\n", "", "src/gone.cpp");
        if (!deleted.Contains("+++ /dev/null") || !deleted.Contains("@@ -1,2 +0,0 @@") || !deleted.Contains("-one"))
            throw new Exception($"A deleted file did not diff as a pure removal:{Environment.NewLine}{deleted}");
        var nine = string.Join('\n', Enumerable.Range(1, 9).Select(index => $"line{index}")) + "\n";
        var middle = FileDiff.Unified(nine, nine.Replace("line5\n", "LINE5\n"), "src/a.cpp");
        if (!middle.Contains("@@ -2,7 +2,7 @@") || !middle.Contains("-line5") || !middle.Contains("+LINE5") || middle.Split('\n').Count(line => line.StartsWith(' ')) != 6)
            throw new Exception($"A one-line edit did not produce one hunk with three context lines each side:{Environment.NewLine}{middle}");
        var crlf = FileDiff.Unified("a\r\nb\r\nc\r\n", "a\r\nB\r\nc\r\n", "src/crlf.cpp");
        if (crlf.Contains('\r') || !crlf.Contains("-b") || !crlf.Contains("+B"))
            throw new Exception($"A CRLF file leaked carriage returns into the diff:{Environment.NewLine}{crlf.Replace("\r", "\\r")}");
        var twenty = string.Join('\n', Enumerable.Range(1, 20).Select(index => $"l{index}")) + "\n";
        var twoEdits = twenty.Replace("l3\n", "L3\n").Replace("l18\n", "L18\n");
        if (FileDiff.Unified(twenty, twoEdits, "src/b.cpp").Split("@@").Length - 1 != 4)
            throw new Exception("Two distant edits did not produce two hunks.");
        var huge = FileDiff.Unified(string.Join('\n', Enumerable.Range(1, 5000).Select(index => $"old{index}")) + "\n", string.Join('\n', Enumerable.Range(1, 5000).Select(index => $"new{index}")) + "\n", "src/huge.cpp");
        if (!huge.Contains("@@ -1,5000 +1,5000 @@") || !huge.Contains("more changed lines") || huge.Split('\n').Length > 63)
            throw new Exception($"A whole-file rewrite was not capped ({huge.Split('\n').Length} lines).");
        // The prefix/suffix trim is what keeps a small edit inside the edit-script bound. Without it this degrades
        // to "the file was rewritten", which is the negative control for the assertion below.
        var big = string.Join('\n', Enumerable.Range(1, 2000).Select(index => $"row{index}")) + "\n";
        if (!FileDiff.Unified(big, big.Replace("row1000\n", "ROW1000\n"), "src/big.cpp").Contains("@@ -997,7 +997,7 @@"))
            throw new Exception("A one-line edit in a 2000-line file lost its hunk.");
        var citedUser = ChatTurn.User("请解释") with { Id = "user-target" };
        var citedAssistant = ChatTurn.Assistant("这是答案") with { Id = "assistant-target" };
        var reply = ChatTurn.User("继续说明") with { ReplyToId = "assistant-target" };
        var wireReply = ChatPipeline.ToChatMessages([citedUser, citedAssistant, reply]).Last();
        if (!wireReply.Contents.OfType<TextContent>().Any(content => content.Text.Contains("Replying to assistant message")
                && content.Text.Contains("这是答案") && content.Text.Contains("继续说明")))
            throw new Exception("A reply reference did not resolve to the cited assistant text for the model request.");
        var missingReply = ChatPipeline.ToChatMessages([ChatTurn.User("独立回复") with { ReplyToId = "missing" }]).Last();
        if (missingReply.Contents.OfType<TextContent>().Any(content => content.Text.Contains("Replying to")))
            throw new Exception("A missing reply target added fabricated context.");
        Console.WriteLine("PASS: the unified diff names its hunks correctly and stays inside the preview cap.");
        // An independent recount is the oracle: CutPoint and SplitsToolCall must agree with a second, dumb
        // implementation at every boundary, or one of them is reasoning about a shape the other does not handle.
        static bool OrphanCallBefore(IReadOnlyList<ChatTurn> messages, int cut)
        {
            for (var i = 0; i < cut; i++)
            {
                if (messages[i].Role != ChatRoles.Assistant || messages[i].ToolCallId is not { Length: > 0 } callId)
                    continue;
                var answered = false;
                for (var j = 0; j < cut; j++)
                    if (messages[j].Role == ChatRoles.Tool && messages[j].ToolCallId == callId)
                    {
                        answered = true;
                        break;
                    }

                if (!answered)
                    return true;
            }

            return false;
        }

        var transcript = new List<ChatTurn>
        {
            ChatTurn.User("修一下构建")
        };
        for (var step = 0; step < 6; step++)
        {
            transcript.Add(ChatTurn.FunctionCall($"c{step}", "read_file", """{"path":"a.cpp"}"""));
            transcript.Add(ChatTurn.FunctionResult($"c{step}", "ok"));
            transcript.Add(ChatTurn.Assistant($"第 {step} 步做完了"));
        }

        for (var keep = 0; keep <= 6; keep++)
        {
            var cut = ContextCompression.CutPoint(transcript, keep);
            if (cut > transcript.Count - keep)
                throw new Exception($"The cut kept fewer than the requested {keep} turns.");
            if (OrphanCallBefore(transcript, cut))
                throw new Exception($"keepRecent={keep} archived an unanswered tool call.");
        }

        for (var boundary = 1; boundary < transcript.Count; boundary++)
            if (ContextCompression.SplitsToolCall(transcript, boundary) != OrphanCallBefore(transcript, boundary))
                throw new Exception($"SplitsToolCall disagreed with the oracle at cut {boundary}.");
        // The case that matters: "everything but the newest four" lands between a call and its result, and the cut
        // has to walk back. This is exactly what the workspace's one-step backoff cannot do.
        var pairStraddling = new List<ChatTurn>
        {
            ChatTurn.User("看一下"),
            ChatTurn.FunctionCall("c0", "read_file", "{}"),
            ChatTurn.FunctionResult("c0", "ok"),
            ChatTurn.User("再看"),
            ChatTurn.FunctionCall("c1", "read_file", "{}"),
            ChatTurn.FunctionResult("c1", "ok"),
        };
        if (!OrphanCallBefore(pairStraddling, pairStraddling.Count - 4))
            throw new Exception("The fixture no longer straddles a call/result pair, so it proves nothing.");
        if (ContextCompression.CutPoint(pairStraddling, 4) != 1)
            throw new Exception($"The cut did not walk back off the tool call (landed at {ContextCompression.CutPoint(pairStraddling, 4)}).");
        if (ContextCompression.CutPoint([], 4) != 0 || ContextCompression.CutPoint([ChatTurn.User("hi")], 4) != 0)
            throw new Exception("A short transcript did not collapse to a zero cut.");
        Console.WriteLine("PASS: the compression cut never archives half of a tool call.");
        // ── The bounds on one tool result ──
        if (ToolResultCap.TokensFor(8192) != 1024 || ToolResultCap.TokensFor(128000) != ToolResultCap.MaximumTokens || ToolResultCap.TokensFor(1000) != ToolResultCap.MinimumTokens)
            throw new Exception("The result cap is not proportional to the window.");
        var longResult = string.Concat(Enumerable.Repeat("0123456789", 12000));
        var cappedResult = ToolResultCap.Apply(longResult, 8192);
        if (cappedResult.Length > ToolResultCap.TokensFor(8192) * ContextTrimmer.CharactersPerToken + 64 || !cappedResult.Contains("characters truncated") || !cappedResult.StartsWith(longResult[..512], StringComparison.Ordinal) || !cappedResult.EndsWith(longResult[^512..], StringComparison.Ordinal))
            throw new Exception($"The capped result lost its head, its tail, or its marker ({cappedResult.Length} characters).");
        if (ToolResultCap.Apply("short", 8192) != "short")
            throw new Exception("A result inside the cap was rewritten.");
        // ── Chinese is priced by its real byte weight, and not one English number moves ──
        // The ASCII half of this is the compatibility anchor for every other number in this file: the trimmer's
        // budget figures, the cap arithmetic above, the shell fixtures that count characters. If it moves, the
        // estimate is not merely different — every assertion standing on it is silently re-calibrated.
        const string asciiSample = "The quick brown fox jumps";
        if (ContextTrimmer.EstimateTokens(asciiSample) != asciiSample.Length / ContextTrimmer.CharactersPerToken + ContextTrimmer.MessageOverheadTokens)
            throw new Exception($"ASCII pricing moved to {ContextTrimmer.EstimateTokens(asciiSample)}; budgets are calibrated on it.");
        var hanSample = ChatTurn.User(new string ('中', 300));
        var latinSample = ChatTurn.User(new string ('n', 300));
        if (ContextTrimmer.EstimateTokens(hanSample) <= ContextTrimmer.EstimateTokens(latinSample) * 3)
            throw new Exception($"300 Chinese characters still priced like 300 Latin ones ({ContextTrimmer.EstimateTokens(hanSample)} vs {ContextTrimmer.EstimateTokens(latinSample)}).");
        if (TokenWeighing.Units("\U0001F600\U0001F600") != 4)
            throw new Exception($"A surrogate pair was not counted once ({TokenWeighing.Units("\U0001F600\U0001F600")} units for two emoji).");
        if (TokenWeighing.Units("\U00020000") != TokenWeighing.Units("中"))
            throw new Exception("A CJK extension-B ideograph was priced as an emoji rather than as an ideograph.");
        Console.WriteLine("PASS: the estimator weighs Chinese at its real size and leaves Latin exactly where it was.");
        // One message, four priced fields: the role framing belongs to the message, not to each string in it.
        var framedTurn = ChatTurn.FunctionCall("c9", "file_write", """{"old_string":"aaaa","new_string":"bbbb"}""", text: "写入", reasoning: "想一想");
        var framedUnits = TokenWeighing.Units(framedTurn.Text) + TokenWeighing.Units(framedTurn.ToolArguments) + TokenWeighing.Units(framedTurn.Reasoning);
        if (ContextTrimmer.EstimateTokens(framedTurn) != ContextTrimmer.ToTokens(framedUnits) + ContextTrimmer.MessageOverheadTokens)
            throw new Exception($"A multi-field turn paid the message framing more than once ({ContextTrimmer.EstimateTokens(framedTurn)}).");
        Console.WriteLine("PASS: one message pays its role framing once, however many fields it carries.");
        // The cap counts the same currency, so a Chinese result can no longer cost three times what it was capped at.
        var wideResult = new string ('漢', 4000);
        var wideCapped = ToolResultCap.Apply(wideResult, 8192);
        if (TokenWeighing.Units(wideCapped) > ToolResultCap.TokensFor(8192) * ContextTrimmer.CharactersPerToken + 64 || !wideCapped.StartsWith(wideResult[..64], StringComparison.Ordinal) || !wideCapped.EndsWith(wideResult[^64..], StringComparison.Ordinal) || !wideCapped.Contains("characters truncated") || wideCapped.Length >= 1200)
            throw new Exception($"A Chinese result was capped in characters, not tokens ({wideCapped.Length} characters, {TokenWeighing.Units(wideCapped)} units).");
        Console.WriteLine("PASS: one tool result is capped in tokens for Chinese too, not in characters.");
        // ── A tool's declaration itself also spends window ──
        // Every declaration (name, description, JSON schema) rides on every later request while the mode offers
        // the tool, and it was priced by nothing: the category the other clients call "System Tools" / "Tools"
        // simply did not exist in Hub's arithmetic.
        static int ProbeSchemaTokens(string description) => ChatPipeline.ToolSchemaTokens([AIFunctionFactory.Create(() => "ok", new AIFunctionFactoryOptions { Name = "read_file", Description = description })]);
        var bareSchema = ProbeSchemaTokens("Read a file.");
        var wideSchema = ProbeSchemaTokens(new string ('描', 400));
        if (bareSchema <= 0)
            throw new Exception("A tool declaration costs nothing, so the window never sees the schema it sends every request.");
        if (wideSchema <= bareSchema * 3)
            throw new Exception($"A Chinese tool description is still priced like Latin ({bareSchema} vs {wideSchema}).");
        if (ChatPipeline.ToolSchemaTokens(null) != 0 || ChatPipeline.ToolSchemaTokens([]) != 0)
            throw new Exception("Ask mode, which offers no tools, was charged for a schema anyway.");
        if (ChatPipeline.MinimumConversationBudgetTokens <= 0)
            throw new Exception("A mode whose schemas outgrow its window would be left with no room at all.");
        Console.WriteLine("PASS: a mode's tool declarations are priced against the window they share with the conversation.");
        // ── The window is read from the metadata the model reports itself, not guessed from a hand-copied table ──
        var catalogJson = """
    {"data":[
      {"id":"vendor/with-window","context_length":1000000,
       "architecture":{"input_modalities":["text","image"],"output_modalities":["text"]},
       "supported_endpoint_types":["openai","anthropic"],
       "top_provider":{"context_length":1000000,"max_completion_tokens":128000}},
      {"id":"vendor/bare-id","created":1,"owned_by":"Vendor"},
      {"id":"vendor/zero-window","top_provider":{"context_length":0,"max_completion_tokens":-5}},
      {"id":"vendor/quoted-window","context_length":"128000"},
      {"id":"vendor/absurd-window","context_length":99999999999},
      {"id":"vendor/huge-but-real","context_length":2000000}
    ]}
    """;
        var catalog = OpenAiCompatibleModelsSource.Parse(System.Text.Encoding.UTF8.GetBytes(catalogJson));
        if (catalog.Models.Count != 6)
            throw new Exception($"The catalog did not list every model ({catalog.Models.Count}).");
        var withWindow = catalog.Capabilities["vendor/with-window"];
        if (withWindow.ContextTokens != 1000000 || withWindow.MaxOutputTokens != 128000 || withWindow.InputModalities is not ["text", "image"] || withWindow.EndpointTypes is not ["openai", "anthropic"] || withWindow.Source != CapabilitySource.EndpointReported)
            throw new Exception("A reported window, output cap or modality list did not survive the parser.");
        if (catalog.Capabilities.ContainsKey("vendor/bare-id"))
            throw new Exception("A model the endpoint described with an id alone was recorded as if it had been described.");
        if (catalog.Capabilities.ContainsKey("vendor/zero-window") || catalog.Capabilities.ContainsKey("vendor/quoted-window") || catalog.Capabilities.ContainsKey("vendor/absurd-window"))
            throw new Exception("A zero, negative, quoted or absurd context_length was accepted as a window.");
        if (catalog.Capabilities["vendor/huge-but-real"].ContextTokens != 2000000)
            throw new Exception("A two-million-token window was rejected as implausible.");
        Console.WriteLine("PASS: a model's window, output cap and input modalities come from its own metadata, and junk is not a window.");
        // The resolver, not the parser: five sites used to write `provider.MaxContextTokens ?? 8192`, which made the
        // whole app run every hosted model against a 2023 number. Order is the contract, so it is asserted at every
        // rung rather than only at the one that happens to be reached today.
        var resolverProvider = new ModelProvider
        {
            Id = "probe",
            Name = "Probe",
            BaseUrl = "https://probe.test/v1",
            Models = [new ProviderModel
            {
                Name = "override-me",
                MaxContextTokens = 4096
            }, new ProviderModel
            {
                Name = "vendor/with-window"
            }, ],
        };
        resolverProvider.ModelCapabilities["vendor/with-window"] = withWindow;
        var reported = ContextBudget.For(resolverProvider, "vendor/with-window");
        if (reported.Tokens != 1000000 || reported.Source != CapabilitySource.EndpointReported)
            throw new Exception($"The endpoint's own number was not used ({reported.Tokens}, {reported.Source}).");
        if (reported.OutputReserve != 128000 || reported.ConversationRoom != 1000000 - 128000)
            throw new Exception($"The answer's share was not held back before the room was measured (reserve {reported.OutputReserve}).");
        var overridden = ContextBudget.For(resolverProvider, "override-me");
        if (overridden.Tokens != 4096 || overridden.Source != CapabilitySource.UserOverride)
            throw new Exception("A hand-set window did not outrank everything the endpoint said.");
        if (overridden.OutputReserve < ContextBudget.MinimumOutputReserveTokens || overridden.OutputReserve * 4 > overridden.Tokens)
            throw new Exception($"A 4096-token model was left with no room to answer (reserve {overridden.OutputReserve}).");
        var unknown = ContextBudget.For(resolverProvider, "never-described");
        if (unknown.Tokens != ContextBudget.FallbackTokens || unknown.Source != CapabilitySource.Fallback || unknown.IsDeclared)
            throw new Exception($"An undescribed model did not fall back honestly ({unknown.Tokens}, {unknown.Source}).");
        var providerDeclared = new ModelProvider
        {
            Id = "preset",
            BaseUrl = "https://p.test/v1",
            MaxContextTokens = 64000
        };
        if (ContextBudget.For(providerDeclared, "any").Tokens != 64000)
            throw new Exception("A preset's declared window was ignored for a model the endpoint never described.");
        if (ContextBudget.For(null, null).Tokens != ContextBudget.FallbackTokens || ContextBudget.AttachmentCeilingTokens(8192) != 2048 || ContextBudget.AttachmentCeilingTokens(1_000_000) != 32_768)
            throw new Exception("The fallback or the one-quarter attachment ceiling is not the number the meter says it is.");
        Console.WriteLine("PASS: the window resolves per model — override, endpoint, preset, fallback — and never as one global 8192.");
        // Modality questions answer "unknown" as "allowed", because a gateway that publishes nothing must not have
        // the user's screenshots taken away from them on the strength of an absence.
        if (!withWindow.AcceptsInput("image") || !withWindow.AcceptsInput("text"))
            throw new Exception("A model that said it takes images was told it does not.");
        if (new ModelCapabilities
        {
            ContextTokens = 8192
        }.AcceptsInput("image") != true || new ModelCapabilities
        {
            ContextTokens = 8192,
            InputModalities = ["text"]
        }.AcceptsInput("image"))
            throw new Exception("An unlisted modality was treated as supported (or a listed refusal as silence).");
        Console.WriteLine("PASS: a model that never said what it accepts still accepts a picture.");
        // ── The number in a refusal is the model's window, but only a refusal counts ──
        // A gateway that has never described its models still states the window in the one message where it has to
        // be honest. The reading has to survive the phrasings in the wild, where the size of the prompt that just
        // failed sits in the same sentence as the limit: mistaking one for the other shrinks the conversation to
        // nothing on every retry, which is worse than never learning at all.
        var openAiRefusal = "This model's maximum context length is 8192 tokens. However, your messages resulted" + " in 9000 tokens. Please reduce the length of the messages.";
        if (!ContextOverflow.IsContextOverflow(openAiRefusal) || ContextOverflow.TryReadRealLimit(openAiRefusal) != 8192)
            throw new Exception("An OpenAI-shaped overflow did not name 8192 as its window.");
        var anthropicRefusal = "400 prompt is too long: 100562 tokens > 200000 maximum";
        if (!ContextOverflow.IsContextOverflow(anthropicRefusal) || ContextOverflow.TryReadRealLimit(anthropicRefusal) != 200000)
            throw new Exception($"An Anthropic-shaped overflow read {ContextOverflow.TryReadRealLimit(anthropicRefusal)}" + " instead of the 200000 it allowed.");
        // A refusal that says no number is still a refusal, and a number that cannot be a window is not one either.
        if (!ContextOverflow.IsContextOverflow("too many tokens") || ContextOverflow.TryReadRealLimit("too many tokens")is not null || ContextOverflow.TryReadRealLimit("maximum context length is 64 tokens")is not null)
            throw new Exception("A numberless or implausible overflow was read as a window, or a real one missed.");
        // The two numbers in the sentence the other way round: this is the shape that would let a naive "first
        // number in the message" read the size of the prompt that just failed as the model's window.
        var reversed = "Your prompt has 9000 tokens, which exceeds the maximum context length of 8192 tokens.";
        if (!ContextOverflow.IsContextOverflow(reversed) || ContextOverflow.TryReadRealLimit(reversed) != 8192)
            throw new Exception($"A prompt size was read as the window ({ContextOverflow.TryReadRealLimit(reversed)}).");
        // The negative control: an authentication failure, or a 400 about something else that mentions the same
        // letters, must stay a plain failure. Compacting on it would throw away a conversation to fix a wrong key.
        if (ContextOverflow.IsContextOverflow("Incorrect API key provided") || ContextOverflow.TryReadRealLimit("Incorrect API key provided")is not null || ContextOverflow.IsContextOverflow("Invalid schema for function 'file_read': context_length is required"))
            throw new Exception("A failure that is not an overflow was treated as one.");
        // And the learned number has to reach the meter, or learning is a fact nobody acts on: a refusal outranks
        // the catalog entry it contradicts.
        var learnedProvider = new ModelProvider
        {
            Id = "learn",
            Name = "Learn",
            BaseUrl = "https://learn.test/v1",
            MaxContextTokens = 128_000,
        };
        learnedProvider.ModelCapabilities["m"] = withWindow.WithContext(65_536, CapabilitySource.LearnedFromRefusal);
        var afterLearning = ContextBudget.For(learnedProvider, "m");
        if (afterLearning.Tokens != 65_536 || afterLearning.Source != CapabilitySource.LearnedFromRefusal || afterLearning.ConversationRoom <= 0)
            throw new Exception($"A window learned from a refusal was not the window the meter used" + $" ({afterLearning.Tokens}, {afterLearning.Source}).");
        Console.WriteLine("PASS: an overflow refusal names the window it violated, and that number is what the next request is measured against.");
        // ── Only the number the model reports is a measurement; an estimate is just a guess ──
        // One send in agent mode is up to seventeen HTTP requests, because the tool loop re-sends the growing
        // transcript every time. Only the largest of those inputs ever held the whole window, while every output is
        // separate money: summing the inputs would report a seventeen-fold window and drive the meter backwards, and
        // taking the largest output would under-bill the reply. The rule is therefore asymmetric on purpose, and the
        // pairing with Hub's own estimate of that same request is what makes the estimate get better.
        var reading = new ContextReport();
        if (reading.IsMeasured || reading.DriftPermille != ContextReport.UncalibratedPermille)
            throw new Exception("An empty report read as a measurement, or calibrated itself out of nothing.");
        reading.Merge(inputTokens: 900, outputTokens: 10, reasoningTokens: 4, cachedInputTokens: 1, estimatedInputTokens: 890);
        reading.Merge(inputTokens: 1_200, outputTokens: 20, reasoningTokens: 8, cachedInputTokens: 2, estimatedInputTokens: 1_000);
        reading.Merge(inputTokens: 600, outputTokens: 5, reasoningTokens: 0, cachedInputTokens: 0, estimatedInputTokens: 4_000);
        if (!reading.IsMeasured || reading.InputTokens != 1_200 || reading.CachedInputTokens != 2 || reading.OutputTokens != 35 || reading.ReasoningTokens != 12 || reading.Requests != 3)
            throw new Exception($"A report did not merge the way a window is read (input {reading.InputTokens}, " + $"output {reading.OutputTokens}, reasoning {reading.ReasoningTokens}, " + $"cached {reading.CachedInputTokens}).");
        // The other half of the same three requests: what they <i>cost</i>. Every one of them read a transcript, so
        // the spend is the sum — including the smallest request, which the window rule above throws away. A merge
        // placed after the window's early return would quietly report 1 200 here, and a bill that only counts the
        // biggest round is the mistake this pair of fields exists to prevent.
        if (reading.SummedInputTokens != 2_700 || reading.SummedCachedInputTokens != 3)
            throw new Exception($"The same three requests did not sum to what they cost " + $"({reading.SummedInputTokens} in, {reading.SummedCachedInputTokens} cached).");
        if (reading.EstimatedInputTokens != 1_000)
            throw new Exception("The estimate kept with the largest input is not the one the reading must be compared to.");
        // A single reading cannot move the scale more than 4×, and an estimate that turns out to have been generous
        // is not rewarded with more room than the model published — only the direction that overflows is corrected.
        if (reading.DriftPermille != 1_200)
            throw new Exception($"The drift over its own estimate read {reading.DriftPermille} instead of 1200 per mille.");
        var underReading = new ContextReport();
        underReading.Merge(40, 1, 0, 0, 10_000);
        if (underReading.DriftPermille != ContextReport.UncalibratedPermille)
            throw new Exception("An estimate that over-read the transcript was rewarded with a bigger window.");
        var runaway = new ContextReport();
        runaway.Merge(10_000_000, 1, 0, 0, 100);
        if (runaway.DriftPermille != ContextReport.MaximumDriftPermille)
            throw new Exception($"A single absurd reading moved the scale past its cap ({runaway.DriftPermille}).");
        var calibrated = new ContextWindow(128_000, 8_000, CapabilitySource.EndpointReported);
        if (calibrated.WithDrift(ContextReport.UncalibratedPermille) != calibrated || calibrated.WithDrift(0) != calibrated || calibrated.WithDrift(999) != calibrated)
            throw new Exception("A reading at or below the neutral ratio changed the window it was supposed to explain.");
        var halved = calibrated.WithDrift(2_000);
        if (halved.Tokens != 64_000 || halved.ConversationRoom != 56_000 || halved.Source != calibrated.Source)
            throw new Exception($"The measurement did not become the denominator ({halved.Tokens} tokens, " + $"{halved.Source}).");
        if (calibrated.WithDrift(ContextReport.MaximumDriftPermille).ConversationRoom < ContextWindow.MinimumConversationTokens + calibrated.OutputReserve)
            throw new Exception("The calibration can shrink a window under the floor it documents.");
        var fourTimes = calibrated.WithDrift(ContextReport.MaximumDriftPermille);
        if (fourTimes.Tokens != 32_000)
            throw new Exception($"A 4× calibration left {fourTimes.Tokens} instead of a quarter of the window.");
        Console.WriteLine("PASS: the model's own token report is the measurement, merged as a window and used as the denominator.");
        // ── Compaction has two tiers, and the cheap one runs first ──
        // The rule used to be "half the raw window, then ask the model to summarize". Half of a hosted window is a
        // lot of transcript, and the one tier that costs nothing was never tried. The thresholds are therefore stated
        // against the room — the window minus the answer's share — and the tiers are ordered by what they cost.
        var room = 10_000;
        if (ContextCompaction.ShouldCompact(5_000, room) || ContextCompaction.ShouldCompact(8_499, room) || !ContextCompaction.ShouldCompact(8_500, room))
            throw new Exception("Compaction no longer starts at 85% of the room, or still starts at half the window.");
        if (!ContextCompaction.ReachedTarget(6_000, room) || ContextCompaction.ReachedTarget(6_001, room))
            throw new Exception("The target a tier has to clear is not the 60% the audit says it is.");
        if (!ContextCompaction.IsHardStopped(9_700, room) || ContextCompaction.IsHardStopped(9_699, room))
            throw new Exception("The point past which no tier can help is not the 97% the ring will name.");
        if (ContextCompaction.Ratio(1_000, 0) != 1d || ContextCompaction.Ratio(0, 0) != 1d)
            throw new Exception("A conversation with no room at all read as empty instead of full.");
        // The window is what fits, not what a round is worth re-sending. A model that declares half a million
        // tokens is still billed per round, so the decision room is capped — measured once, a 468 750-token room
        // let one session reach 416 000 tokens a round and spend an hour's allowance in an hour.
        if (ContextCompaction.DecisionRoom(500_000) != ContextCompaction.CostCeilingTokens
            || ContextCompaction.DecisionRoom(40_000) != 40_000
            || ContextCompaction.DecisionRoom(0) != 0)
            throw new Exception("The room a compaction decision uses is no longer capped by what a round may cost, or a small window was capped too.");
        if (!ContextCompaction.ShouldCompact(90_000, ContextCompaction.DecisionRoom(500_000))
            || ContextCompaction.ShouldCompact(80_000, ContextCompaction.DecisionRoom(500_000)))
            throw new Exception("A 90 000-token transcript still counts as affordable because the model's window is huge.");
        // Over target with nothing to take back is not a failed compaction. Counting it as one is what banned a
        // session for good: three sends of a 100 000-token first message filled the allowance, and the block
        // outlived the transcript it was decided on.
        var opening = new Conversation { Id = "candidates", Title = "candidates" };
        opening.Messages.Add(ChatTurn.User(new string('u', 60_000)));
        opening.Messages.Add(ChatTurn.Assistant("收到了"));
        if (ContextCompaction.CandidateTokens(opening.Messages, 0) > 0)
            throw new Exception("A transcript with nothing old enough to clear and nothing to summarize reported compaction candidates.");
        for (var pair = 0; pair < 4; pair++)
        {
            opening.Messages.Add(ChatTurn.FunctionCall($"c{pair}", "read_file", "{}"));
            opening.Messages.Add(ChatTurn.FunctionResult($"c{pair}", new string('r', 4_000)));
        }

        if (ContextCompaction.CandidateTokens(opening.Messages, 0) <= 0)
            throw new Exception("A transcript with a summarizable prefix reported no compaction candidates, so auto compaction would stay blocked.");
        if (ContextCompaction.CandidateTokens(opening.Messages, summaryThroughMessageCount: 10) != 0)
            throw new Exception("A fully summarized transcript with no old results reported compaction candidates, so a block would be lifted for nothing.");
        // One message is not a prefix: the summarizer and the request builder both refuse fewer than two, so a
        // candidate measure that counted one reports work no tier can do — the attempt then looks like a failure,
        // and three of those block a session that is perfectly well.
        var single = new Conversation { Id = "candidates-one", Title = "one" };
        single.Messages.Add(ChatTurn.User(new string('u', 60_000)));
        single.Messages.Add(ChatTurn.Assistant("收到"));
        single.Messages.Add(ChatTurn.User("继续"));
        single.Messages.Add(ChatTurn.Assistant("好"));
        single.Messages.Add(ChatTurn.User("再继续"));
        if (ContextCompaction.CanSummarize(single.Messages, 0))
            throw new Exception("A prefix of one message was reported as summarizable, so the candidate measure and the summarizer disagree.");
        if (ContextCompaction.CandidateTokens(single.Messages, 0) != 0)
            throw new Exception("A history whose cut leaves one message behind reported a summary candidate, so an attempt that could do nothing counts as a failure.");
        // A block is lifted only for materially more to work with: the candidates that failed are still there
        // afterwards, so "candidates exist" would reset the allowance forever and rebuy the failing request.
        if (!ContextCompaction.ShouldRecover(2_000, 1_000) || ContextCompaction.ShouldRecover(1_999, 1_000))
            throw new Exception("Recovery no longer waits for twice as much to take back, so a failing summary is retried every round.");
        if (!ContextCompaction.ShouldRecover(500, 0))
            throw new Exception("A block recorded before candidates were tracked never gets the one attempt that keeps it from being pinned by age.");
        if (ContextBudget.SummaryCeilingTokens(1_000_000) != 4_096 || ContextBudget.SummaryCeilingTokens(2_048) != 512 || ContextBudget.SummaryCeilingTokens(16_000) != 2_000)
            throw new Exception("The summary ceiling is not an eighth-of-the-window with both a floor and a cap.");
        if (ContextBudget.SummaryOutputTokens(64_000) != 4_000 || ContextBudget.SummaryOutputTokens(512) != 1_024 || ContextBudget.SummaryOutputTokens(10_000_000) != 8_192)
            throw new Exception("The summarizing request's own answer budget left its documented bounds.");
        Console.WriteLine("PASS: compaction triggers at 85% of the room, stops at 60%, and prices its two tiers in the right order.");
        // Tier one: the transcript's own tool results, cleared in place. The assertions are the invariants the tier
        // is allowed to have — nothing removed, nothing re-timed, the newest work left alone, and a second pass that
        // does nothing because the first one already did it.
        var call = ChatTurn.FunctionCall("c1", "read_file", """{"path":"big.txt"}""");
        var answer = ChatTurn.FunctionResult("c1", new string ('y', 4_000));
        var tail = new[]
        {
            ChatTurn.User("接着看"),
            ChatTurn.Assistant("好的")
        };
        var clearable = new List<ChatTurn>
        {
            call,
            answer
        };
        clearable.AddRange(tail);
        var through = ContextElider.Boundary(clearable, keepRecent: 2);
        if (through != 2)
            throw new Exception($"The clear boundary ran past the pair it was given ({through}).");
        var clearedList = ContextElider.Clear(clearable, through, int.MaxValue).Messages;
        if (clearedList.Count != clearable.Count || clearedList[0].Role != ChatRoles.Assistant || clearedList[0].ToolCallId != "c1" || clearedList[1].Role != ChatRoles.Tool || clearedList[1].ToolCallId != "c1" || clearedList[1].Text.Length > 400)
            throw new Exception("Clearing a tool result removed it, unpaired it, or left its body where it was.");
        if (clearedList[1].At != answer.At || clearedList[1].ToolName != answer.ToolName)
            throw new Exception("A cleared turn lost the timestamp or the name it was written with.");
        if (!clearedList[1].Text.Contains("4000", StringComparison.Ordinal) || !clearedList[1].Text.Contains("call the tool again", StringComparison.OrdinalIgnoreCase))
            throw new Exception($"The placeholder does not say what went or how to get it back ({clearedList[1].Text}).");
        if (ContextElider.Clear(clearedList, through, int.MaxValue).Messages.SequenceEqual(clearedList) == false || ContextElider.ReclaimableTokens(clearedList, through) != 0)
            throw new Exception("A second pass over an already cleared transcript changed it again, or still billed it.");
        // Tier one takes only what the target needs. Clearing the whole span buys the same saving and rewrites
        // more of the transcript than it has to — and the middle it rewrites is the prefix every later round
        // re-sends, so on a caching provider the surplus is paid for again. Measured once: 251 280 tokens were
        // cleared to reach 35% of a room whose target is 60%.
        var wide = new List<ChatTurn>();
        for (var pair = 0; pair < 6; pair++)
        {
            wide.Add(ChatTurn.FunctionCall($"w{pair}", "read_file", "{}"));
            wide.Add(ChatTurn.FunctionResult($"w{pair}", new string('x', 4_000)));
        }

        wide.AddRange(tail);
        var wideThrough = ContextElider.Boundary(wide, keepRecent: 2);
        var reclaimable = ContextElider.ReclaimableTokens(wide, wideThrough);
        var partial = ContextElider.Clear(wide, wideThrough, 1_500);
        if (partial.Tokens <= 0 || partial.Tokens >= reclaimable)
            throw new Exception($"A clear given a 1 500-token budget took {partial.Tokens} of {reclaimable} reclaimable tokens instead of stopping short of it.");
        if (partial.Through >= wideThrough)
            throw new Exception("A clear that stopped at its budget claimed the whole span, so what it left behind could never be reclaimed.");
        if (partial.Messages.Skip(partial.Through).Any(turn => ContextElider.IsCleared(turn)))
            throw new Exception("A clear past its budget cleared a turn beyond the point it reported reaching.");
        if (ContextElider.Clear(wide, wideThrough, int.MaxValue).Tokens != reclaimable)
            throw new Exception("An unbudgeted clear no longer takes everything, so a forced compaction stopped shrinking.");
        // A summarized prefix is not on the wire any more, so the clear budget must not be spent behind it: the
        // transcript would not shrink, the saving would never reach a request, and the paid tier would run anyway.
        // Measured once, an entire clear went to 430 results the summary had already replaced.
        var behindSummary = ContextElider.Clear(wide, wideThrough, 1_500, from: 4);
        if (ContextElider.IsCleared(behindSummary.Messages[1]) || ContextElider.IsCleared(behindSummary.Messages[3]))
            throw new Exception("A clear reached back behind the summary boundary and rewrote results the provider no longer receives.");
        if (behindSummary.Tokens <= 0 || behindSummary.Through > wideThrough)
            throw new Exception("A clear bounded by a summary made no progress, or claimed a span it never reached.");
        if (ContextElider.ReclaimableTokens(wide, wideThrough, from: wideThrough) != 0)
            throw new Exception("What a clear reports as reclaimable ignored the summary boundary.");
        // The spend of a run that never produced an answer still has to be readable somewhere: the session total
        // counts it, so a transcript where no turn carries it is a screen that says nothing about the run someone
        // is asking about. Five such runs held 19 257 466 tokens of one measured session — 16% of its burn.
        var spent = new Conversation { Id = "burn-stamp", Title = "burn" };
        spent.Messages.Add(ChatTurn.User("跑一下回归"));
        spent.Messages.Add(ChatTurn.FunctionCall("b1", "read_file", """{"path":"big.txt"}"""));
        spent.Messages.Add(ChatTurn.FunctionResult("b1", new string('r', 400)));
        var burned = new ContextBurn(120_000, 900, 100, 60_000, 4);
        var where = spent.StampBurn(burned, stamped: -1);
        if (where != spent.Messages.Count - 1 || spent.Messages[^1].Burned != burned)
            throw new Exception("A run that ended without an answer filed its spend nowhere, so the total counts tokens no turn can show.");
        if (spent.StampBurn(new ContextBurn(1, 1, 0, 0, 1), stamped: -1) != -1 || spent.Messages[^1].Burned != burned)
            throw new Exception("A later unfinished run overwrote the reading another answer had already earned.");
        spent.Messages.Add(ChatTurn.Assistant("答完了"));
        if (spent.StampBurn(new ContextBurn(2, 2, 0, 0, 1), spent.Messages.Count - 1) != spent.Messages.Count - 1
            || spent.Messages[^1].Burned?.Requests != 1)
            throw new Exception("A finished run stopped filing its reading on the answer that owned it.");
        Console.WriteLine("PASS: a run that ended without an answer files its spend on its own newest turn, and no run ever overwrites another's reading.");
        if (!ContextElider.IsClearable(answer) || ContextElider.IsClearable(clearedList[1]) || ContextElider.IsClearable(ChatTurn.User("用户的提问")))
            throw new Exception("A turn was billed as clearable that should never be, or a real one was missed.");
        var pictureResult = ChatTurn.FunctionResult("c2", new string ('z', 900), false, [new ChatImage("frame.png", "image/png", 24_576)]);
        var pictureThrough = ContextElider.Boundary([pictureResult], keepRecent: 0);
        var clearedPicture = ContextElider.Clear([pictureResult], pictureThrough, int.MaxValue).Messages[0];
        if (clearedPicture.Images.Count != 0 || !clearedPicture.Text.Contains("picture", StringComparison.OrdinalIgnoreCase))
            throw new Exception("Clearing a captured frame dropped the file reference without saying so, or kept the bytes.");
        // A boundary that would open on an unanswered call walks back, the same rule the summarizer's cut obeys —
        // and here it matters more, because this one rewrites the file the next request is assembled from.
        var splitProne = new List<ChatTurn>
        {
            call,
            answer,
            ChatTurn.FunctionCall("c3", "build", "{}")
        };
        splitProne.AddRange(tail);
        var safeThrough = ContextElider.Boundary(splitProne, keepRecent: 2);
        if (ContextCompression.SplitsToolCall(splitProne, safeThrough))
            throw new Exception($"The clear boundary at {safeThrough} split a call from its result.");
        if (ContextElider.ReclaimableTokens(splitProne, safeThrough) <= 0)
            throw new Exception("A transcript full of tool output reported nothing to reclaim.");
        Console.WriteLine("PASS: tier one clears the older tool results in place, keeps every call paired, and never asks the model.");
        // ── The rows of the ring added together are exactly the total it reports ──
        // The meter used to be one number nobody could take apart, so a piece that never made it into the total was
        // invisible — which is precisely how the tool declarations went unpriced while riding on every request. These
        // rows are the fix, and the identity is the assertion that keeps it: the categories must add up to the same
        // integer the trimmer bills the request, field by field, including the awkward ones. A turn whose text is two
        // Latin letters and whose attachment is one ideograph divides to 0 + 1 separately but to 2 as one message; the
        // split therefore has to come from the message's own total, not from each field on its own.
        var meterMode = "You are the assistant.";
        var meterCharter = new string ('章', 30);
        var meterSummary = new string ('总', 40);
        var meterMemory = new string ('m', 20);
        var meterTurns = new List<ChatTurn>
        {
            ChatTurn.User("看下构建日志")with
            {
                AttachedContext = new string ('文', 700)
            },
            ChatTurn.User("ab")with
            {
                AttachedContext = "文"
            },
            ChatTurn.FunctionCall("c1", "read_file", """{"path":"build.log"}"""),
            ChatTurn.FunctionResult("c1", new string ('x', 3_000)),
            ChatTurn.Assistant("改好了")with
            {
                Reasoning = new string ('想', 40),
                Images = [new ChatImage("frame.png", "image/png", 24_576)],
            },
        };
        var meter = new ContextLedger();
        meter.AddSystemPrompt(meterMode, meterCharter, meterSummary, meterMemory);
        meter.AddToolSchema(1_234);
        foreach (var turn in meterTurns)
            meter.AddTurn(turn);
        var meterRows = meter.Categories();
        var billedAlone = ContextTrimmer.EstimateTokens(meterMode + meterCharter + meterSummary + meterMemory) + 1_234 + meterTurns.Sum(ContextTrimmer.EstimateTokens);
        if (meterRows.Sum(row => row.Tokens) != billedAlone || meter.TotalTokens != billedAlone)
            throw new Exception($"The breakdown adds to {meterRows.Sum(row => row.Tokens)} while the request is billed at " + $"{billedAlone}; a heading is missing from the total.");
        if (meterRows.Any(row => row.Tokens <= 0))
            throw new Exception($"A heading with nothing in it is being shown as {meterRows.First(row => row.Tokens <= 0).Tokens}.");
        if (meterRows.Count(row => row.Kind == ContextCostKind.ProjectCharter) != 1 || meterRows.Count(row => row.Kind == ContextCostKind.Summary) != 1 || meterRows.Count(row => row.Kind == ContextCostKind.Attachments) != 1 || meterRows.Count(row => row.Kind == ContextCostKind.Images) != 1)
            throw new Exception($"The headings are not each booked once ({string.Join(",", meterRows.Select(row => row.Kind))}).");
        if (meterRows.First(row => row.Kind == ContextCostKind.Images).Tokens != ContextTrimmer.ImageTokenCost)
            throw new Exception("A picture's row does not cost the figure the estimator charges for it.");
        // The negative half, and the reason the identity above is not decoration: dividing every field on its own is
        // the obvious way to split a turn, and it throws whole tokens away to the remainder. One message is billed as
        // one division, so the rows have to be cut out of that division rather than each priced by itself.
        static long FieldUnits(ChatTurn turn) => TokenWeighing.Units(turn.Text) + TokenWeighing.Units(turn.AttachedContext) + TokenWeighing.Units(turn.ToolArguments) + TokenWeighing.Units(turn.Reasoning);
        var dividedOnce = meterTurns.Sum(turn => ContextTrimmer.ToTokens(FieldUnits(turn)));
        var dividedPerField = meterTurns.Sum(turn => ContextTrimmer.ToTokens(TokenWeighing.Units(turn.Text)) + ContextTrimmer.ToTokens(TokenWeighing.Units(turn.AttachedContext)) + ContextTrimmer.ToTokens(TokenWeighing.Units(turn.ToolArguments)) + ContextTrimmer.ToTokens(TokenWeighing.Units(turn.Reasoning)));
        if (dividedPerField >= dividedOnce)
            throw new Exception($"Per-field division lost nothing ({dividedPerField} against {dividedOnce}), so the identity proves nothing.");
        // The framing cost of each message rides in the text row too, which is why it is added here and not counted as
        // a field: a turn is one message whatever fields it carries.
        var fieldRowBudget = dividedOnce + meterTurns.Count * ContextTrimmer.MessageOverheadTokens;
        if (meterRows.Where(row => row.Kind is ContextCostKind.Messages or ContextCostKind.Attachments).Sum(row => row.Tokens) != fieldRowBudget)
            throw new Exception("The rows split each field on its own instead of dividing one message once.");
        var emptySplit = new ContextLedger();
        emptySplit.AddSystemPrompt(meterMode, null, null, "");
        if (emptySplit.Categories().Count != 1 || !emptySplit.Categories().Single().Kind.Equals(ContextCostKind.SystemPrompt))
            throw new Exception("An empty summary or memory index still took a row in the popover.");
        // A filed plan is priced from the figure frozen on its turn, and that figure has to be the one the request
        // pays when the body is put back for the wire. Not an approximation: an estimate that reads low sends an
        // oversized request, and one that reads high drops turns the model still needs.
        var pricedBody = "# 计划\n\n" + string.Concat(Enumerable.Range(1, 60).Select(i => $"- 第 {i} 步：改实现并跑一遍自检。\n"));
        var filedPlan = ChatTurn.Assistant("")with
        {
            PlanApprovalState = PlanApprovalStates.Pending,
            Plan = new PlanReference("0123456789abcdef0123456789abcdef.md", "计划", pricedBody.Length, ContextTrimmer.BodyTokens(pricedBody)),
        };
        // The copy the request is assembled from holds the body as text and names no document. Both halves are
        // pinned: a boundary that left the reference on the copy would have the estimator bill the same plan twice.
        var wirePlan = filedPlan with
        {
            Text = pricedBody,
            Plan = null
        };
        if (ContextTrimmer.EstimateTokens(filedPlan) != ContextTrimmer.EstimateTokens(wirePlan))
            throw new Exception($"A filed plan prices at {ContextTrimmer.EstimateTokens(filedPlan)} while the same body inside " + $"the message costs {ContextTrimmer.EstimateTokens(wirePlan)}.");
        if (ContextTrimmer.EstimateTokens(filedPlan with { Text = pricedBody }) == ContextTrimmer.EstimateTokens(wirePlan))
            throw new Exception("A copy holding both the body and its frozen figure cost the same as one holding the body, " + "so the identity above proves nothing about the boundary.");
        // The length cannot stand in for the weight. This body is Chinese: priced at characters-per-token it would be
        // counted at a quarter of what it costs, and the low direction is the one this file calls a failed turn.
        if (ContextTrimmer.BodyTokens(pricedBody) <= pricedBody.Length / ContextTrimmer.CharactersPerToken)
            throw new Exception("A Chinese plan was priced by its length instead of its weight.");
        var planMeter = new ContextLedger();
        planMeter.AddTurn(filedPlan);
        if (planMeter.TotalTokens != ContextTrimmer.EstimateTokens(filedPlan) || planMeter.Categories().Count(row => row.Kind == ContextCostKind.Messages) != 1)
            throw new Exception("A plan's frozen cost is not booked under 正文, so the breakdown stopped adding up to the request.");
        // The inline case is the same turn with its body in the text and no document named: charging the frozen figure
        // on top of it would price one plan twice.
        if (ContextTrimmer.EstimateTokens(ChatTurn.Assistant(pricedBody)with { Plan = filedPlan.Plan!with { File = null } }) != ContextTrimmer.EstimateTokens(ChatTurn.Assistant(pricedBody)))
            throw new Exception("A plan kept in the transcript paid for its body twice.");
        // Where the denominator came from has to be sayable, and the two surfaces that say it must not own two
        // different spellings of the same five facts.
        var sourceKeys = new[]
        {
            CapabilitySource.UserOverride,
            CapabilitySource.EndpointReported,
            CapabilitySource.LearnedFromRefusal,
            CapabilitySource.ManifestDeclared,
            CapabilitySource.Fallback,
            CapabilitySource.None,
        }.Select(ContextBudget.SourceLabelKey).ToArray();
        if (sourceKeys.Any(string.IsNullOrWhiteSpace) || sourceKeys.Distinct().Count() != 5)
            throw new Exception($"Two window sources share one label, or one has none ({string.Join(",", sourceKeys)}).");
        Console.WriteLine("PASS: the context breakdown adds up to the billed total, omits nothing, and names where the window came from.");
        // ── The window cannot open on an orphaned tool result ──
        var trimmedOrphan = ContextTrimmer.Trim([ChatTurn.FunctionResult("c0", "上一轮的答案"), ChatTurn.User("新问题"), ChatTurn.Assistant("回答")], 4096);
        if (trimmedOrphan.Count == 0 || trimmedOrphan[0].Role == ChatRoles.Tool)
            throw new Exception("Trimming kept an orphaned tool result at the front of the window.");
        var heavyCall = ChatTurn.FunctionCall("c1", "file_write", """{"old_string":"aaaa","new_string":"bbbb"}""");
        if (ContextTrimmer.EstimateTokens(heavyCall) <= ContextTrimmer.EstimateTokens(heavyCall with { ToolArguments = null }))
            throw new Exception("A tool call's arguments were not charged against the budget.");
        Console.WriteLine("PASS: one tool result cannot push the conversation out of the window.");
        // ── Several calls in one response, one of them waiting on approval ──
        // The gateway numbers the multiple calls of the same response as call_00_ / call_01_, and Hub stores one turn
        // per call. Once any one of them is parked on approval, its result must no longer sit directly after itself —
        // the provider rejects the whole array, and every later resend from this session hits the same 400. The
        // fixture below is exactly the shape of that on-disk transcript, and the verdict is counted independently,
        // not taken from the function under test.
        static bool EveryCallAnsweredBesideItself(IReadOnlyList<ChatMessage> messages)
        {
            for (var i = 0; i < messages.Count; i++)
            {
                var calls = messages[i].Contents.OfType<FunctionCallContent>().Select(call => call.CallId).ToList();
                if (calls.Count == 0)
                    continue;
                var answered = new List<string?>();
                for (var j = i + 1; j < messages.Count && messages[j].Role == ChatRole.Tool; j++)
                    answered.AddRange(messages[j].Contents.OfType<FunctionResultContent>().Select(result => result.CallId));
                if (!calls.All(answered.Contains))
                    return false;
            }

            return true;
        }

        var waiting = new Conversation
        {
            Id = "waiting",
            ProviderId = "deepseek",
            ModelName = "test"
        };
        waiting.Append(ChatTurn.FunctionCall("call_00_run", "run_command", """{"command":"cmake --build ."}"""));
        waiting.Append(ChatTurn.FunctionCall("call_01_ls", "list_directory", """{"depth":1}"""));
        waiting.Append(ChatTurn.FunctionResult("call_01_ls", "1 dir(s), 0 file(s)"));
        if (!waiting.HasUnansweredToolCall())
            throw new Exception("A call still waiting on its own card read as answered, so the resumed request went out doomed.");
        // Where a result belongs once approved: right after the call it answers, not at the end of the transcript.
        var ordered = new Conversation
        {
            Id = "ordered",
            ProviderId = "deepseek",
            ModelName = "test"
        };
        ordered.Append(ChatTurn.FunctionCall("call_00_run", "run_command", "{}"));
        ordered.Append(ChatTurn.FunctionCall("call_01_ls", "list_directory", "{}"));
        ordered.Append(ChatTurn.FunctionResult("call_01_ls", "1 dir(s), 0 file(s)"));
        ordered.AppendFunctionResult(ChatTurn.FunctionResult("call_00_run", "shell: exit 0"));
        if (ordered.Messages[1].Role != ChatRoles.Tool || ordered.Messages[1].ToolCallId != "call_00_run")
            throw new Exception("An approved result was not recorded beside the call it answers.");
        if (ordered.FirstMispairedToolCallId()is not null || !EveryCallAnsweredBesideItself(ChatPipeline.ToChatMessages(ordered.Messages)))
            throw new Exception("A transcript built at the right insertion point still went out mispaired.");
        if (ordered.Messages.Count != 4 || ordered.Messages[0].ToolCallId != "call_00_run" || ordered.Messages[2].ToolCallId != "call_01_ls")
            throw new Exception("Recording a result beside its call disturbed the turns around it.");
        // Already-paired entries must not be touched: otherwise every request rewrites the file while fixing nothing.
        if (ordered.RepairToolCallOrdering() != 0)
            throw new Exception("Repair shifted a transcript that was already paired correctly.");
        // An already-broken transcript: the result appended at the very end is what got rejected on the wire.
        var parked = new Conversation
        {
            Id = "parked",
            ProviderId = "deepseek",
            ModelName = "test"
        };
        parked.Append(ChatTurn.User("新建一个基于 cmake 的计算器"));
        parked.Append(ChatTurn.FunctionCall("call_00_run", "run_command", "{}"));
        parked.Append(ChatTurn.FunctionCall("call_01_ls", "list_directory", "{}"));
        parked.Append(ChatTurn.FunctionResult("call_01_ls", "1 dir(s), 0 file(s)"));
        parked.Append(ChatTurn.FunctionResult("call_00_run", "shell: exit 0"));
        if (EveryCallAnsweredBesideItself(ChatPipeline.ToChatMessages(parked.Messages)))
            throw new Exception("The fixture no longer reproduces the rejected order, so it proves nothing.");
        // An existence check cannot see ordering problems — precisely why it failed to block this 400, so pin it here.
        if (parked.CloseUnansweredToolCalls(_ => "superseded") != 0)
            throw new Exception("The existence-based repair claimed to fix a transcript it cannot see.");
        if (parked.FirstMispairedToolCallId() != "call_00_run")
            throw new Exception($"The adjacency check blamed the wrong call ({parked.FirstMispairedToolCallId()}).");
        // Moving one result within a transcript reports 1 — reporting it as "positions disturbed" would make the audit
        // line read as though turns were lost.
        if (parked.RepairToolCallOrdering() != 1)
            throw new Exception($"Repairing one misplaced result reported {parked.RepairToolCallOrdering()} moved.");
        if (parked.FirstMispairedToolCallId()is not null || !EveryCallAnsweredBesideItself(ChatPipeline.ToChatMessages(parked.Messages)))
            throw new Exception("The send-boundary repair left the transcript rejected.");
        if (parked.Messages.Count != 5 || parked.RepairToolCallOrdering() != 0)
            throw new Exception("Repair invented a turn, or is not idempotent.");
        // Reorder only, never add or drop: a call without a result still has no result — that is a decision a person
        // still owes, not an error to be rewritten.
        var owed = new Conversation
        {
            Id = "owed",
            ProviderId = "deepseek",
            ModelName = "test"
        };
        owed.Append(ChatTurn.User("问题"));
        owed.Append(ChatTurn.FunctionCall("call_00_run", "run_command", "{}"));
        owed.Append(ChatTurn.Assistant("模型后面说的话"));
        owed.RepairToolCallOrdering();
        if (owed.Messages.Count != 3 || !owed.HasUnansweredToolCall())
            throw new Exception("Repair closed or dropped a call that is still owed a decision.");
        // A result with no matching call stays where it is: deleting it would rewrite history, which is outside the
        // reorderer's authority.
        var orphan = new Conversation
        {
            Id = "orphan",
            ProviderId = "deepseek",
            ModelName = "test"
        };
        orphan.Append(ChatTurn.FunctionResult("call_gone", "答案"));
        orphan.Append(ChatTurn.User("新问题"));
        if (orphan.RepairToolCallOrdering() != 0 || orphan.Messages.Count != 2)
            throw new Exception("Repair touched a result whose call is gone.");
        Console.WriteLine("PASS: a tool result is read beside the call it answers, and a misplaced one is repaired before sending.");
        // ── Reasoning models + tool calls: reasoning_content must be carried back verbatim ──
        // DeepSeek's rule: in ordinary multi-turn chat reasoning_content may be left out (it ignores it), but once
        // tools have been given, every later request must carry back the reasoning each assistant turn originally
        // produced, or the whole request gets a 400. The connector has no such field on the chat/completions write
        // path (measured: TextReasoningContent is dropped silently, no throw), so Hub carries it itself — and what is
        // verified here is that carry-back path.
        //
        // A request whose endpoint was asked to think is the strict half of that rule: every assistant message owes
        // the field, including the ones whose thinking Hub never recorded — the turns of a conversation that changed
        // models midway. One of those left bare is what the provider answers 400 to ("The reasoning_content in the
        // thinking mode must be passed back to the API"), on every later request, because the turn stays in the
        // history. There the field says that no thinking was recorded instead of borrowing another turn's; the two
        // runs below are the two halves of the rule, and ModelCatalog.IsThinkingMode is what tells them apart.
        var thoughtBody = """
        {"messages":[{"role":"user","content":"问题"},{"role":"assistant","content":"我先看下。",
        "tool_calls":[{"id":"call_00_a","type":"function","function":{"name":"run_command","arguments":"{}"}}]},
        {"role":"tool","tool_call_id":"call_00_a","content":"ok"},{"role":"assistant","content":"看过了"},
        {"role":"assistant","tool_calls":[{"id":"call_01_b","type":"function","function":{"name":"read_file","arguments":"{}"}}]},
        {"role":"tool","tool_call_id":"call_01_b","content":"file"},{"role":"user","content":"继续"}],"model":"m"}
        """;
        List<ChatTurn> thoughtTurns = [ChatTurn.User("问题"), ChatTurn.FunctionCall("call_00_a", "run_command", "{}", "我先看下。", "需要先确认工具链在不在。"), ChatTurn.FunctionResult("call_00_a", "ok"), ChatTurn.Assistant("看过了", "这一轮不用再查了。"), // A thinking model answers with no chain of thought plenty of often. This is the turn a client is tempted
        // to pad; on an endpoint that does not require the field it comes back exactly as it went in, and on one
        // that does it carries the placeholder — asserted both ways below.
        ChatTurn.FunctionCall("call_01_b", "read_file", "{}"), ChatTurn.FunctionResult("call_01_b", "file"), ChatTurn.User("继续"), ];
        var thoughts = new ReasoningTable();
        thoughts.Observe(ChatPipeline.ToChatMessages(thoughtTurns));
        if (thoughts.IsEmpty)
            throw new Exception("The harvest saw no reasoning, so nothing would go back out.");
        if (!ReasoningReplayPolicy.TryInject(thoughtBody, thoughts, everyAssistantMessage: false, out var replayed))
            throw new Exception("Nothing was replayed — the gateway would reject the request again.");
        var replayedMessages = JsonDocument.Parse(replayed).RootElement.GetProperty("messages").EnumerateArray().ToList();
        if (replayedMessages.Count != 7)
            throw new Exception($"Replaying rewrote the conversation, not just one field ({replayedMessages.Count} messages).");
        if (replayedMessages[1].TryGetProperty("reasoning_content", out var onCall)is false || onCall.GetString() != "需要先确认工具链在不在。")
            throw new Exception("The call's own turn did not carry its thinking back.");
        if (replayedMessages[3].GetProperty("reasoning_content").GetString() != "这一轮不用再查了。")
            throw new Exception("A plain answer lost its thinking on the way back out.");
        if (replayedMessages[4].TryGetProperty("reasoning_content", out _))
            throw new Exception("A request that does not demand the field invented a chain of thought for a turn that had none.");
        if (replayedMessages[0].TryGetProperty("reasoning_content", out _) || replayedMessages[2].TryGetProperty("reasoning_content", out _) || replayedMessages[5].TryGetProperty("reasoning_content", out _) || replayedMessages[6].TryGetProperty("reasoning_content", out _))
            throw new Exception("A user or tool message was given a chain of thought it never had.");
        // If the Chinese reasoning came back \uXXXX-escaped, every turn pays several times the bytes for the same
        // sentence — when all it was asked to do was carry it back verbatim.
        if (replayed.Contains("\\u"))
            throw new Exception("The replayed thinking was escaped, inflating every later request that carries it.");
        if (!replayed.Contains("tool_calls") || !replayed.Contains("call_00_a"))
            throw new Exception("The rewrite dropped the tool call it was only supposed to annotate.");
        // The same body on a request whose endpoint was asked to think, where the field is mandatory on every
        // assistant message. The bare turn below is the reported bug in miniature: a conversation that changed models
        // midway holds turns whose thinking was never recorded, and one of them left empty is a 400 on every later
        // request until each of them carries something — so the ones with thinking go back verbatim, and the one
        // without says so itself. The harvest is consumed by the request it was read for (that is what makes two
        // identical answers get their own thinking), so the strict half is given a fresh read of the same transcript.
        thoughts.Observe(ChatPipeline.ToChatMessages(thoughtTurns));
        if (!ReasoningReplayPolicy.TryInject(thoughtBody, thoughts, everyAssistantMessage: true, out var filled))
            throw new Exception("A thinking-mode request was left without the field it requires.");
        var filledMessages = JsonDocument.Parse(filled).RootElement.GetProperty("messages").EnumerateArray().ToList();
        if (filledMessages[1].TryGetProperty("reasoning_content", out var onMandatoryCall)is false || onMandatoryCall.GetString() != "需要先确认工具链在不在。" || filledMessages[3].TryGetProperty("reasoning_content", out var onMandatoryAnswer)is false || onMandatoryAnswer.GetString() != "这一轮不用再查了。")
            throw new Exception("A turn with recorded thinking was not carried back verbatim on a mandatory request.");
        if (filledMessages[4].TryGetProperty("reasoning_content", out var onBareCall)is false || onBareCall.GetString() != ReasoningReplayPolicy.MissingReasoning)
            throw new Exception("A turn whose thinking was never recorded did not say so on a mandatory request.");
        if (filledMessages[0].TryGetProperty("reasoning_content", out _) || filledMessages[2].TryGetProperty("reasoning_content", out _) || filledMessages[5].TryGetProperty("reasoning_content", out _) || filledMessages[6].TryGetProperty("reasoning_content", out _))
            throw new Exception("A user or tool message was handed the placeholder on a mandatory request.");
        if (!filled.Contains("tool_calls") || !filled.Contains("call_00_a"))
            throw new Exception("The mandatory rewrite dropped the tool call it was only supposed to annotate.");
        if (filled.Contains("\\u"))
            throw new Exception("The placeholder was escaped, inflating every later request that carries it.");
        Console.WriteLine("PASS: a request whose endpoint thinks carries the field on every assistant message, and says so where no thinking was recorded.");
        // Harvesting nothing at all is the same bug in its smallest form: every earlier reply was produced without a
        // recorded thought, and the request still owes the field on each of them.
        var nothing = new ReasoningTable();
        nothing.Observe(ChatPipeline.ToChatMessages([ChatTurn.User("问题"), ChatTurn.Assistant("答")]));
        if (!nothing.IsEmpty || !ReasoningReplayPolicy.TryInject(thoughtBody, nothing, everyAssistantMessage: true, out var bare))
            throw new Exception("A thinking-mode request with nothing harvested went out without the field.");
        var bareAssistant = JsonDocument.Parse(bare).RootElement.GetProperty("messages").EnumerateArray().Where(message => message.GetProperty("role").GetString() == "assistant").ToList();
        if (bareAssistant.Count != 3)
            throw new Exception($"The fixture no longer holds three assistant messages ({bareAssistant.Count}).");
        if (bareAssistant.Any(message => !message.TryGetProperty("reasoning_content", out var carriedBack) || carriedBack.GetString() != ReasoningReplayPolicy.MissingReasoning))
            throw new Exception("An assistant message on a mandatory request was left without the placeholder.");
        // Which requests the strict half applies to is a declaration rather than a model name: the manifest's request
        // options say whether the endpoint is being asked to think. An undeclared or unrecognised shape fails closed —
        // a field invented for an endpoint that never asked for one is a field that endpoint may reject.
        static ModelProvider ThinkingDeclared(object? thinking)
        {
            var provider = new ModelProvider
            {
                Id = "thinking",
                BaseUrl = "https://thinking.test/v1"
            };
            var options = new Dictionary<string, JsonElement>();
            if (thinking is not null)
                options["thinking"] = JsonSerializer.SerializeToElement(thinking);
            provider.ReasoningModels["m"] = new AiModelReasoning
            {
                RequestOptions = options
            };
            return provider;
        }

        if (!ModelCatalog.IsThinkingMode(ThinkingDeclared(new { type = "enabled" }), "m") || !ModelCatalog.IsThinkingMode(ThinkingDeclared("enabled"), "m") || !ModelCatalog.IsThinkingMode(ThinkingDeclared(true), "m"))
            throw new Exception("A model that declared thinking was not read as one.");
        if (ModelCatalog.IsThinkingMode(ThinkingDeclared(new { type = "disabled" }), "m") || ModelCatalog.IsThinkingMode(ThinkingDeclared(new { budget_tokens = 4096 }), "m") || ModelCatalog.IsThinkingMode(ThinkingDeclared(new { type = 1 }), "m") || ModelCatalog.IsThinkingMode(ThinkingDeclared(null), "m") || ModelCatalog.IsThinkingMode(ThinkingDeclared(new { type = "enabled" }), "unknown") || ModelCatalog.IsThinkingMode(null, "m"))
            throw new Exception("A model that did not declare thinking was read as one, so its request would carry the field.");
        Console.WriteLine("PASS: the mandatory-field rule is read off the model's own declaration, and a model that declared nothing never gets the field.");
        // A request whose endpoint was not asked to think keeps its bytes exactly: not every compatible gateway allows
        // unknown fields, and inventing reasoning for a turn that did none is a fabrication.
        var silent = new ReasoningTable();
        silent.Observe(ChatPipeline.ToChatMessages([ChatTurn.User("问题"), ChatTurn.Assistant("答")]));
        if (!silent.IsEmpty || ReasoningReplayPolicy.TryInject(thoughtBody, silent, everyAssistantMessage: false, out var untouched) || untouched != thoughtBody)
            throw new Exception("A request that does not demand the field was touched at all.");
        // Two identical answers in the same session each get their own reasoning back — consumed in order, not looked
        // up by text against one standing value.
        var twice = new ReasoningTable();
        twice.Observe(ChatPipeline.ToChatMessages([ChatTurn.Assistant("好的", "第一次的想法"), ChatTurn.Assistant("好的", "第二次的想法")]));
        if (twice.TakeFor(null, "好的") != "第一次的想法" || twice.TakeFor(null, "好的") != "第二次的想法")
            throw new Exception("Two identical answers were handed the same thinking.");
        // Carrier: the reasoning rides on the assistant message it belongs to, without turning one message into two.
        var carried = ChatPipeline.ToChatMessages([ChatTurn.FunctionCall("c1", "run_command", "{}", "先看。", "想了"), ChatTurn.FunctionResult("c1", "ok"), ChatTurn.Assistant("答", "又想了"), ]);
        if (carried.Count != 3)
            throw new Exception("Carrying reasoning split an assistant message in two.");
        if (carried[0].Contents.OfType<FunctionCallContent>().Count() != 1 || carried[0].Contents.OfType<TextReasoningContent>().Single().Text != "想了")
            throw new Exception("A call turn lost either its call or its thinking.");
        if (carried[2].Contents.OfType<TextReasoningContent>().Single().Text != "又想了")
            throw new Exception("A plain answer's thinking never reached the message list.");
        // Persistence and budget: an old session file loads unchanged without this property, and the reasoning bytes
        // count against the window — they are resent on every later request.
        if (JsonSerializer.Deserialize<ChatTurn>(JsonSerializer.Serialize(ChatTurn.Assistant("答", "想了很久")))?.Reasoning != "想了很久")
            throw new Exception("The thinking did not survive a save.");
        if (JsonSerializer.Deserialize<ChatTurn>("""{"Role":"assistant","Text":"旧","At":"2026-01-01T00:00:00+08:00"}""")?.Reasoning is not null)
            throw new Exception("A session file written before reasoning was stored stopped loading.");
        var plain = ChatTurn.Assistant("短");
        if (ContextTrimmer.EstimateTokens(plain with { Reasoning = new string ('思', 3000) }) <= ContextTrimmer.EstimateTokens(plain))
            throw new Exception("A turn whose only weight is its thinking counted as free.");
        Console.WriteLine("PASS: a thinking model's reasoning goes back out on the message that produced it, and only there.");
        // ── A notice is a fact about the conversation, not a message ──
        // The model-switch row is a turn so it can sit where it happened and survive a restart, and it must not ride
        // the request: nobody said it, it costs nothing, and a line nobody typed is a line the model answers to.
        var noticeTurns = new List<ChatTurn>
        {
            ChatTurn.User("问题"),
            ChatTurn.Notice("已切换模型：OrcaRouter · deepseek-flash"),
            ChatTurn.Assistant("答"),
        };
        var noticeWire = ChatPipeline.ToChatMessages(noticeTurns);
        if (noticeWire.Count != 2)
            throw new Exception($"A notice turn became a message the model would answer ({noticeWire.Count} for 2 said turns).");
        if (noticeWire[0].Text != "问题" || noticeWire[1].Text != "答")
            throw new Exception("Dropping the notice changed what the conversation actually said.");
        var withNotice = ContextTrimmer.Trim(noticeTurns, 10_000, "prompt");
        var withoutNotice = ContextTrimmer.Trim([..noticeTurns.Where(turn => turn.Role != ChatRoles.Notice)], 10_000, "prompt");
        if (withNotice.Count != withoutNotice.Count || withNotice.Any(turn => turn.Role == ChatRoles.Notice))
            throw new Exception("A notice was kept inside the window the model pays for.");
        if (ContextTrimmer.EstimateTokens(ChatTurn.Notice("已切换模型：OrcaRouter · deepseek-flash")) != 0)
            throw new Exception("A line the request never carries was charged against the window.");
        Console.WriteLine("PASS: a model-switch notice sits in the transcript and stays off the wire, out of the window and off the bill.");
        // ── Several calls in one response: that one reasoning payload must ride with every one of them ──
        // The model can ask for several tools in a single response while thinking only once; that response is split
        // into several assistant messages in the transcript, each one a product of that single thought. On replay the
        // gateway wants each of them to carry it — earlier only the first message to stream out got it, the rest were
        // empty, and the request was refused again.
        var batchBody = """
        {"messages":[{"role":"user","content":"问题"},
        {"role":"assistant","tool_calls":[{"id":"call_00_x","type":"function","function":{"name":"run_command","arguments":"{}"}}]},
        {"role":"tool","tool_call_id":"call_00_x","content":"ok"},
        {"role":"assistant","tool_calls":[{"id":"call_01_y","type":"function","function":{"name":"read_file","arguments":"{}"}}]},
        {"role":"tool","tool_call_id":"call_01_y","content":"file"},{"role":"user","content":"继续"}],"model":"m"}
        """;
        var batchThought = "一次思考，两个调用。";
        var batchTable = new ReasoningTable();
        batchTable.Observe(ChatPipeline.ToChatMessages([ChatTurn.User("问题"), ChatTurn.FunctionCall("call_00_x", "run_command", "{}", null, batchThought), ChatTurn.FunctionResult("call_00_x", "ok"), ChatTurn.FunctionCall("call_01_y", "read_file", "{}", null, batchThought), ChatTurn.FunctionResult("call_01_y", "file"), ChatTurn.User("继续"), ]));
        if (!ReasoningReplayPolicy.TryInject(batchBody, batchTable, everyAssistantMessage: true, out var batchReplayed))
            throw new Exception("A second call of one response had nothing to replay, so the body went out unchanged.");
        var batchMessages = JsonDocument.Parse(batchReplayed).RootElement.GetProperty("messages").EnumerateArray().ToList();
        if (batchMessages.Count != 6)
            throw new Exception($"Replaying a batch rewrote the conversation, not just one field ({batchMessages.Count} messages).");
        if (batchMessages[1].GetProperty("reasoning_content").GetString() != batchThought || batchMessages[3].GetProperty("reasoning_content").GetString() != batchThought)
            throw new Exception("Only the first call of one response carried its thinking back to the gateway.");
        Console.WriteLine("PASS: every call of one thinking response replays with that response's thinking, not just the first.");
        // ── Inside the loop, results shrink but messages never disappear ──
        var loopMessages = new List<ChatMessage>
        {
            new(ChatRole.System, "system rules"),
            new(ChatRole.User, "第一个问题"),
        };
        for (var index = 0; index < 6; index++)
        {
            loopMessages.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call-{index}", "read_file", new Dictionary<string, object?> { ["path"] = $"f{index}.cpp" })]));
            loopMessages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call-{index}", new string ('x', 4000))]));
        }

        // A complete batch hands control back before a request can churn the old prefix. An owner that cannot
        // compact may resume with the existing finite elision fallback; an initial request never yields forever.
        var boundaryClient = new FakeChatClient(["ok"]);
        var boundaryGuard = new ToolLoopContextGuard(boundaryClient, 1200)
        {
            YieldForCompaction = true
        };
        await foreach (var _ in boundaryGuard.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "short")]))
        {
        }

        var firstSent = boundaryClient.LastMessages;
        var yielded = false;
        try
        {
            await foreach (var _ in boundaryGuard.GetStreamingResponseAsync(loopMessages))
            {
            }
        }
        catch (ToolLoopContextBoundaryException)
        {
            yielded = true;
        }

        if (!yielded || !ReferenceEquals(firstSent, boundaryClient.LastMessages))
            throw new Exception("A full tool boundary sent a prefix-changing request instead of yielding first.");
        // The handoff is a cost decision, not a fit decision. With a 120 000-token window the same loop never
        // yields — which is how one measured run kept sending 100 000–400 000 tokens a round and spent a five-hour
        // allowance in eighteen minutes. The yield budget is the seam the host passes its ceiling through, while
        // trimming keeps the window, so a small ceiling over a huge window has to yield.
        var costClient = new FakeChatClient(["ok"]);
        var costGuard = new ToolLoopContextGuard(costClient, 200_000, yieldBudgetTokens: 1_200)
        {
            YieldForCompaction = true
        };
        await foreach (var _ in costGuard.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "short")]))
        {
        }

        var costYielded = false;
        try
        {
            await foreach (var _ in costGuard.GetStreamingResponseAsync(loopMessages))
            {
            }
        }
        catch (ToolLoopContextBoundaryException)
        {
            costYielded = true;
        }

        if (!costYielded)
            throw new Exception("A loop with a huge window and a small cost ceiling kept the turn instead of handing control back, so only the window ever decides when to compact.");
        var youngClient = new FakeChatClient(["ok"]);
        var youngGuard = new ToolLoopContextGuard(youngClient, 1200)
        {
            YieldForCompaction = true,
            CanYieldForCompaction = () => Task.FromResult(false)
        };
        await foreach (var _ in youngGuard.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "short")]))
        {
        }

        await foreach (var _ in youngGuard.GetStreamingResponseAsync(loopMessages))
        {
        }

        if (youngClient.LastMessages?.Count != loopMessages.Count)
            throw new Exception("A boundary with no reclaimable history failed instead of allowing progress.");
        var fallbackClient = new FakeChatClient(["ok"]);
        var fallbackGuard = new ToolLoopContextGuard(fallbackClient, 1200)
        {
            YieldForCompaction = true
        };
        await foreach (var _ in fallbackGuard.GetStreamingResponseAsync(loopMessages))
        {
        }

        if (fallbackClient.LastMessages is null)
            throw new Exception("An oversized first request could never resume.");
        using (var cancelledBoundary = new CancellationTokenSource())
        {
            cancelledBoundary.Cancel();
            var stopped = false;
            try
            {
                await foreach (var _ in boundaryGuard.GetStreamingResponseAsync(loopMessages, cancellationToken: cancelledBoundary.Token))
                {
                }
            }
            catch (OperationCanceledException)
            {
                stopped = true;
            }

            if (!stopped)
                throw new Exception("Compaction masked a cancelled run.");
        }

        Console.WriteLine("PASS: complete tool batches yield before prefix churn, oversized initial requests have a finite fallback, and cancellation wins.");
        var elided = ToolLoopContextGuard.Elide(loopMessages, 1200);
        if (elided.Count != loopMessages.Count)
            throw new Exception("Elision removed messages instead of shrinking them.");
        if (!elided.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Select(call => call.CallId).SequenceEqual(elided.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Select(result => result.CallId)))
            throw new Exception("Elision broke the call/result pairing.");
        if (elided[0].Text != "system rules" || elided[1].Text != "第一个问题")
            throw new Exception("Elision touched the system prompt or the question being answered.");
        var resultTexts = elided.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Select(result => result.Result?.ToString() ?? "").ToList();
        if (resultTexts.Take(2).Any(text => !text.Contains("elided")) || resultTexts.Skip(2).Any(text => text.Contains("elided") || text.Length != 4000))
            throw new Exception($"Elision did not spare exactly the newest {ToolLoopContextGuard.KeepRecentResults} results.");
        // A chain of thought is priced inside the loop too. TextReasoningContent is a *sibling* of TextContent in
        // Microsoft.Extensions.AI — its own docs say neither type derives from the other — so no arm of the guard's
        // estimator would notice it by accident. The same fixture therefore runs twice, with and without the
        // thinking block: without it the list fits the budget and nothing is elided, with it two results have to
        // go. That difference is the assertion, and it is what pins the field down as priced rather than free.
        static List<ChatMessage> ThinkingLoopFixture(bool withThought)
        {
            var fixture = new List<ChatMessage>
            {
                new(ChatRole.System, "system rules"),
                new(ChatRole.User, "问题"),
            };
            for (var step = 0; step < 6; step++)
            {
                var contents = new List<AIContent>();
                if (step == 0 && withThought)
                    contents.Add(new TextReasoningContent(new string ('x', 6000)));
                contents.Add(new FunctionCallContent($"th-{step}", "read_file", new Dictionary<string, object?> { ["path"] = $"{step}.cpp" }));
                fixture.Add(new ChatMessage(ChatRole.Assistant, contents));
                fixture.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"th-{step}", new string ('y', 600))]));
            }

            return fixture;
        }

        static int ElidedResults(List<ChatMessage> messages, int budget) => ToolLoopContextGuard.Elide(messages, budget).SelectMany(message => message.Contents).OfType<FunctionResultContent>().Select(result => result.Result?.ToString() ?? "").Count(text => text.Contains("elided"));
        if (ElidedResults(ThinkingLoopFixture(true), 1500) != 2 || ElidedResults(ThinkingLoopFixture(false), 1500) != 0)
            throw new Exception("The loop's estimate did not price the chain of thought (expected 2 elided with it, 0 without).");
        Console.WriteLine("PASS: a thinking model's reasoning is priced inside the tool loop, not only on the turn.");
        // ── The loop ends on its own, and stays inside the window while it runs ──
        // It has to run past both numbers that used to end it by force: the library's own default of 40 and the
        // 16 this app once set. Reaching either sends one last request with the tool declarations taken off, and
        // a model still mid-investigation answers a request with no schema by writing its call into prose — the
        // DSML leak, measured on the wire at request 17 of a 16-round run. The only legitimate ending is the
        // model answering without asking for a tool, which this client does at round 45.
        var insistent = new InsistentToolClient
        {
            RoundsBeforeAnswering = 45
        };
        // A threshold test owns its threshold: the fixture declares the window it is testing against rather than
        // inheriting whatever the fallback for an undescribed model happens to be this release.
        var probeProvider = new ModelProvider
        {
            Id = "probe",
            Name = "probe",
            BaseUrl = "https://example.invalid/v1",
            Model = "probe",
            MaxContextTokens = 8192
        };
        var probeWindow = ContextBudget.For(probeProvider, probeProvider.Model);
        var probeTool = AIFunctionFactory.Create((Func<string>)(() => new string ('y', 20000)), new AIFunctionFactoryOptions { Name = "get_projects", Description = "probe" });
        var answered = new StringBuilder();
        string? capFailure = null;
        try
        {
            await foreach (var chunk in new ChatPipeline(insistent).SendAsync(probeProvider, [ChatTurn.User("一直调用工具")], tools: [probeTool]))
                answered.Append(chunk);
        }
        catch (Exception exception)
        {
            capFailure = exception.GetType().Name + ": " + exception.Message;
        }

        if (capFailure is not null)
            throw new Exception($"The tool loop surfaced as an exception instead of an answer: {capFailure}");
        if (insistent.CallCount != 46)
            throw new Exception($"The loop made {insistent.CallCount} requests instead of ending on the model's own answer at round 46.");
        if (answered.Length == 0)
            throw new Exception("The loop ended without leaving the user any text.");
        if (insistent.LastOptions?.AllowMultipleToolCalls is not null)
            throw new Exception("A forty-six-round loop went out asking the endpoint for one call at a time.");
        if (insistent.LastOptions?.Tools is not { Count: > 0 })
            throw new Exception("The final request went out with the tool declarations taken off — the shape that turns " + "a model still wanting to call into markup written into its answer.");
        // Every round returned 20 000 characters against the 8192-token window this fixture declares, so by the last
        // request the older results must have been elided — with the messages themselves still there, pairing intact.
        var lastResults = insistent.LastRequest.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Select(result => result.Result?.ToString() ?? "").ToList();
        if (lastResults.Count < 45)
            throw new Exception($"The final request carried only {lastResults.Count} tool results.");
        if (!lastResults.Any(text => text.Contains("elided")) || !lastResults.Any(text => !text.Contains("elided")))
            throw new Exception("The loop either elided nothing or elided the result it is reasoning about.");
        if (lastResults.Any(text => text.Length > ToolResultCap.TokensFor(probeWindow.Tokens) * ContextTrimmer.CharactersPerToken + 64))
            throw new Exception("A tool result reached the model without being capped.");
        if (!insistent.LastRequest.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Select(call => call.CallId).SequenceEqual(insistent.LastRequest.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Select(result => result.CallId)))
            throw new Exception("The loop's own request lost the call/result pairing.");
        Console.WriteLine("PASS: the tool loop ends on the model's own answer, with the declarations on every request, " + "capping every result and eliding the old ones.");
        // A goal's ceiling must intercept the follow-up request inside the invoker, not only the outer send.
        // The existing long-loop fixture never installs a goal guard, so it cannot catch this overspend.
        var budgetClient = new InsistentToolClient { RoundsBeforeAnswering = 3 };
        var budgetGoal = Conversation.Create("probe");
        budgetGoal.GoalTokenBudget = 0;
        var admissions = 0;
        var settled = 0;
        try
        {
            await foreach (var chunk in new ChatPipeline(budgetClient).SendAsync(probeProvider,
                [ChatTurn.User("budgeted tool loop")], tools: [probeTool],
                beforeRequest: estimate =>
                {
                    admissions++;
                    if (!GoalPolicy.For(budgetGoal).CanRequest(budgetGoal, estimate))
                        throw new GoalGuardException("token_budget");
                    budgetGoal.GoalEstimatedTokens += estimate;
                    budgetGoal.GoalTokenBudget = budgetGoal.GoalEstimatedTokens;
                    return Task.CompletedTask;
                },
                afterRequest: (_, reported, completed) =>
                {
                    if (completed && reported < 0) settled++;
                    return Task.CompletedTask;
                })) _ = chunk;
            throw new Exception("An over-budget follow-up reached the provider.");
        }
        catch (GoalGuardException ex) when (ex.Reason == "token_budget") { }
        if (budgetClient.CallCount != 1 || admissions != 2 || settled != 1)
            throw new Exception($"Budget guard admitted {admissions}, sent {budgetClient.CallCount}, settled {settled} requests.");
        Console.WriteLine("PASS: the goal budget refuses the tool-loop follow-up before the provider sees it.");

        // End-of-stream usage replaces a reservation once, while an endpoint without usage retains the estimate.
        // The normal context meter measures spend too, but cannot prove this independent goal ledger is settled.
        var usageCases = new (long Input, long Output, long Reasoning, long Cached)[]?[]
            { null, [(40, 2, 1, 0), (40, 4, 2, 0)] };
        foreach (var reports in usageCases)
        {
            long reserved = 0, actual = 0;
            var measured = new FakeChatClient(["answer"], usage: reports);
            await foreach (var chunk in new ChatPipeline(measured).SendAsync(probeProvider,
                [ChatTurn.User("measured goal request")],
                beforeRequest: estimate => { reserved += estimate; return Task.CompletedTask; },
                afterRequest: (estimate, reported, _) =>
                {
                    if (reported >= 0) { reserved -= estimate; actual += reported; }
                    return Task.CompletedTask;
                })) _ = chunk;
            if (reports is null ? reserved <= 0 || actual != 0 : reserved != 0 || actual != 44)
                throw new Exception($"Goal request settlement retained {reserved} estimated and {actual} reported tokens.");
        }
        // No-content failures used to spend a full context reservation on every cooldown, so switching
        // providers could immediately exhaust the shared goal budget without sending a successful request.
        foreach (var failure in new Exception[] { new HttpRequestException("429 token rate limit"),
                     new ProviderCooldownException(TimeSpan.FromMinutes(1)), new OperationCanceledException() })
        foreach (var hasPartialResponse in new[] { false, true })
        {
            long reserved = 0;
            var settlements = 0;
            var completedRequests = 0;
            try
            {
                var client = new FakeChatClient(hasPartialResponse ? ["partial response"] : [""], failure: failure);
                await foreach (var chunk in new ChatPipeline(client).SendAsync(probeProvider,
                    [ChatTurn.User("failed goal request")],
                    beforeRequest: estimate => { reserved += estimate; return Task.CompletedTask; },
                    afterRequest: (estimate, reported, completed) =>
                    {
                        settlements++;
                        if (completed) completedRequests++;
                        if (reported >= 0) reserved -= estimate;
                        return Task.CompletedTask;
                    })) _ = chunk;
                throw new Exception("A failed request returned successfully.");
            }
            catch (Exception ex) when (ReferenceEquals(ex, failure)) { }
            if (settlements != 1 || completedRequests != 0 || (hasPartialResponse ? reserved <= 0 : reserved != 0))
                throw new Exception($"Failed goal settlement: partial={hasPartialResponse}, reserved={reserved}, settled={settlements}.");
        }
        Console.WriteLine("PASS: reported usage replaces reservations; empty failures release them, while completed/partial responses without usage retain estimates.");

        // ── A loop that repeats itself instead of working is stopped ──
        // With no round cap, this and the error counter are what stand between a long task and an unbounded paid
        // loop. The verdict is read from the transcript the loop itself accumulated, so the probe here is the pure
        // function over message lists: the pipeline wiring around it is one line in the invoker.
        static ChatMessage SpinCall(string callId, string tool, string json) => new(ChatRole.Assistant, [new FunctionCallContent(callId, tool, JsonSerializer.Deserialize<Dictionary<string, object?>>(json))]);
        static ChatMessage SpinResult(string callId, string text) => new(ChatRole.Tool, [new FunctionResultContent(callId, text)]);
        static List<ChatMessage> SpinHistory(params (string CallId, string Tool, string Json, string Result)[] runs)
        {
            var list = new List<ChatMessage>
            {
                new(ChatRole.User, "调查")
            };
            foreach (var(callId, tool, json, result)in runs)
            {
                list.Add(SpinCall(callId, tool, json));
                list.Add(SpinResult(callId, result));
            }

            return list;
        }

        // Key order is not intent: the same call with its arguments spelled in another order is the same call.
        if (ToolSpinGuard.NormalizeArguments("""{"path":"a.cpp","deep":true}""") != ToolSpinGuard.NormalizeArguments("""{"deep":true,"path":"a.cpp"}"""))
            throw new Exception("Argument normalization made key order look like a different call.");
        var probeRun = SpinHistory(("c1", "read_file", """{"path":"a.cpp"}""", new string ('x', 2000)));
        if (ToolSpinGuard.RefusalFor("read_file", """{"path":"a.cpp"}""", probeRun)is not null)
            throw new Exception("A verify re-read after one run was refused; call plus verify is correct work.");
        probeRun.Add(SpinCall("c2", "read_file", """{"path":"a.cpp"}"""));
        probeRun.Add(SpinResult("c2", new string ('x', 2000)));
        if (ToolSpinGuard.RefusalFor("read_file", """{"path":"a.cpp"}""", probeRun)is null)
            throw new Exception("A third identical run was allowed; that is the shape that used to run forever.");
        if (ToolSpinGuard.RefusalFor("read_file", """{"path":"a.cpp", "lines":"1-10"}""", probeRun)is not null)
            throw new Exception("A call with different arguments was refused as a repeat of another call.");
        // Re-reading a file you just edited is verification: a successful state-changing call in between resets.
        // Two identical reads sit before the write; one after it — so this next call is the second since the write
        // and allowed, while without the reset it would be the fourth and refused.
        var edited = SpinHistory(("c1", "read_file", """{"path":"a.cpp"}""", new string ('x', 2000)), ("c2", "read_file", """{"path":"a.cpp"}""", new string ('x', 2000)), ("c3", "file_write", """{"path":"a.cpp","content":"int main(){}"}""", "Edited a.cpp: 1 line"), ("c4", "read_file", """{"path":"a.cpp"}""", new string ('x', 2000)));
        edited.Add(SpinCall("c5", "read_file", """{"path":"a.cpp"}"""));
        if (ToolSpinGuard.RefusalFor("read_file", """{"path":"a.cpp"}""", edited)is not null)
            throw new Exception("A re-read after a write was refused as a spin; the write reset the window.");
        // A write that reports no change or a refusal did not move the world. It must not erase the two earlier
        // reads from the guard's view, or repeated failed anchors can keep a loop alive indefinitely.
        foreach (var refusedWrite in new[]
        {
            "No change: old_string was not found.",
            "Refused: path is outside the workspace."
        }

        )
        {
            var unchanged = SpinHistory(("c1", "read_file", """{"path":"a.cpp"}""", "old"), ("c2", "read_file", """{"path":"a.cpp"}""", "old"), ("c3", "file_write", """{"path":"a.cpp","old_string":"wrong"}""", refusedWrite));
            if (ToolSpinGuard.RefusalFor("read_file", """{"path":"a.cpp"}""", unchanged)is null)
                throw new Exception("A no-op or refused file_write reset the spin guard; the file did not change.");
        }

        // A refused approval and a failed tool did not move the world either — they must not reset it.
        var denied = SpinHistory(("c1", "run_command", """{"command":"rm x"}""", ToolApprovalResults.Denied), ("c2", "run_command", """{"command":"rm x"}""", ToolApprovalResults.Denied));
        denied.Add(SpinCall("c3", "run_command", """{"command":"rm x"}"""));
        if (ToolSpinGuard.RefusalFor("run_command", """{"command":"rm x"}""", denied)is null)
            throw new Exception("A third denied attempt was allowed; a refusal is not a state change.");
        // The sentence a continuation writes over a call that never ran belongs in the same list. Left out, a
        // never-executed write reads as "the world moved", so the window resets and the loop this guard exists to stop
        // becomes legal again — the exemption has to be earned by something that actually ran, not by Hub's own note
        // that nothing did.
        var unanswered = SpinHistory(("c1", "run_command", """{"command":"rm x"}""", ToolApprovalResults.NotAnswered), ("c2", "run_command", """{"command":"rm x"}""", ToolApprovalResults.NotAnswered));
        unanswered.Add(SpinCall("c3", "run_command", """{"command":"rm x"}"""));
        if (ToolSpinGuard.RefusalFor("run_command", """{"command":"rm x"}""", unanswered)is null)
            throw new Exception("A call closed as never-answered reset the window; nothing ran, so nothing moved.");
        // An elided result is Hub asking for the re-call — the placeholder says so in its own words.
        var elidedHistory = SpinHistory(("c1", "read_file", """{"path":"a.cpp"}""", "old content"), ("c2", "read_file", """{"path":"a.cpp"}""", $"[elided: 2000 characters of tool output — call the tool again if you need this]"));
        elidedHistory.Add(SpinCall("c3", "read_file", """{"path":"a.cpp"}"""));
        if (ToolSpinGuard.RefusalFor("read_file", """{"path":"a.cpp"}""", elidedHistory)is not null)
            throw new Exception("A re-read after an elision was refused as a spin; Hub asked for that call.");
        // The invoking client's own list keeps the original bytes, so the guard reports elided ids for it — and an
        // unreported elision must not invent the exemption, or every repeat becomes legal by claiming elided.
        var elidedReport = SpinHistory(("c1", "read_file", """{"path":"a.cpp"}""", new string ('x', 2000)), ("c2", "read_file", """{"path":"a.cpp"}""", new string ('x', 2000)));
        elidedReport.Add(SpinCall("c3", "read_file", """{"path":"a.cpp"}"""));
        if (ToolSpinGuard.RefusalFor("read_file", """{"path":"a.cpp"}""", elidedReport, new HashSet<string> { "c2" })is not null)
            throw new Exception("A re-read whose earlier result the guard reported as elided was refused anyway.");
        if (ToolSpinGuard.RefusalFor("read_file", """{"path":"a.cpp"}""", elidedReport, new HashSet<string>())is null)
            throw new Exception("An unreported elision invented the exemption; two live runs are two live runs.");
        // Alternating A→B→A→B is caught too: the window counts occurrences of one signature, not consecutive
        // runs, so the fifth call (the third A) is refused. Pinned because a reviewer read this as uncatchable —
        // exact-match over a window is what makes the cycle hit the same wall every rotation does.
        var alternating = SpinHistory(("a1", "read_file", """{"path":"a.cpp"}""", "old"), ("b1", "search_text", """{"pattern":"x"}""", "hit"), ("a2", "read_file", """{"path":"a.cpp"}""", "old"), ("b2", "search_text", """{"pattern":"x"}""", "hit"));
        alternating.Add(SpinCall("a3", "read_file", """{"path":"a.cpp"}"""));
        if (ToolSpinGuard.RefusalFor("read_file", """{"path":"a.cpp"}""", alternating)is null)
            throw new Exception("An alternating A-B cycle slipped through; the window counts occurrences, not runs.");
        // Beyond the window, repetitions were part of working through the problem, not the problem.
        var distant = new List<ChatMessage>
        {
            new(ChatRole.User, "调查")
        };
        for (var round = 0; round < ToolSpinGuard.WindowSize; round++)
        {
            distant.Add(SpinCall($"w{round}", "read_file", """{"path":"other.cpp"}"""));
            distant.Add(SpinResult($"w{round}", new string ('x', 100)));
        }

        distant.Add(SpinCall("s1", "read_file", """{"path":"a.cpp"}"""));
        distant.Add(SpinResult("s1", "old"));
        if (ToolSpinGuard.RefusalFor("read_file", """{"path":"a.cpp"}""", distant)is not null)
            throw new Exception("A lone repeat older than the window was refused as a spin.");
        Console.WriteLine("PASS: three identical runs are refused, and writes, denials, elisions and the window " + "keep correct work off the refusal.");
        // ── The refusal that ends the turn, end to end through the real invoking client ──
        // A model that asks for the same call every round gets two executions, one plain refusal, and then the
        // loop is terminated rather than bought a third identical round. Requests are bounded at four; the tool
        // ran twice; the last result says the turn is stopping, which is what the shell reads to name the reason.
        var spinner = new InsistentToolClient
        {
            SameCallEveryRound = true
        };
        var executions = 0;
        var spinTool = AIFunctionFactory.Create((Func<string>)(() =>
        {
            executions++;
            return "ok";
        }), new AIFunctionFactoryOptions { Name = "get_projects", Description = "probe" });
        var spinAnswered = new StringBuilder();
        // The termination sentence is the last result of the run — no request carries it, because no request
        // follows. The callback is the only place it can be read from.
        var spinResults = new List<string>();
        await foreach (var chunk in new ChatPipeline(spinner).SendAsync(probeProvider, [ChatTurn.User("一直要同一个调用")], tools: [spinTool], onToolCompleted: (_, result, _) =>
        {
            spinResults.Add(result);
            return Task.CompletedTask;
        }))
            spinAnswered.Append(chunk);
        if (spinner.CallCount != 4)
            throw new Exception($"A spinning model was bought {spinner.CallCount} requests; the bound is two " + "executions, one refusal, one termination.");
        if (executions != 2)
            throw new Exception($"The spinning call ran {executions} times; it must run exactly twice before the " + "refusal.");
        if (!spinResults.Any(text => text.Contains(ToolSpinGuard.RefusalMarker, StringComparison.Ordinal)) || !spinResults.Any(text => text.StartsWith(ToolSpinGuard.TerminationMarker, StringComparison.Ordinal)))
            throw new Exception("The spin run produced neither the refusal nor the termination sentence.");
        Console.WriteLine("PASS: a spinning model gets two executions, one refusal, then the turn — four requests, " + "no more.");
        return;
    }
}
