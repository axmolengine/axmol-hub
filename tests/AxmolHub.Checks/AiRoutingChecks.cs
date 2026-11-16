using AxmolHub.Core;

internal static class AiRoutingChecks
{
    internal static async Task RunAsync(string root, string[] args)
    {
        // ── Auto routing: the decision table from task intensity to reasoning tier, every cell asserted ──
        // Routing spends someone else's money, so every cell is written out here rather than sampled: the tier a
        // request gets has to be readable off this table by whoever is holding the bill, and a cell nobody enumerated
        // is the cell that quietly escalates every session in a mode nobody reviewed.
        var cells = 0;
        static (string Effort, string? Model) Pick(string mode = "agent", int failures = 0, bool written = false, double ratio = 0, bool steered = false, int images = 0, int draft = 12, string ceiling = "xhigh", string? fast = null, string? strong = null)
        {
            var decision = ModelRouting.Decide(new ModelRoutingSignal(mode, failures, written, ratio, steered, images, draft, ceiling, fast, strong));
            return (decision.ReasoningEffort, decision.ModelName);
        }

        void Expect(string label, (string Effort, string? Model) got, string effort, string? model)
        {
            cells++;
            // A router that answers the neutral tier has delegated the decision it was hired to make, so every cell is
            // also checked for being a real strength.
            if (ModelRouting.Rank(got.Effort) == 0)
                throw new Exception($"{label}: routing answered the neutral tier instead of a real one.");
            if (got.Effort != effort || !string.Equals(got.Model, model, StringComparison.Ordinal))
                throw new Exception($"{label}: expected {effort} · {(model ?? "(keep the model)")}, got " + $"{got.Effort} · {(got.Model ?? "(keep the model)")}.");
        }

        Expect("an ask-mode question", Pick("ask"), "low", null);
        Expect("a plan", Pick("plan"), "medium", null);
        Expect("agent with no tool history", Pick(), "medium", null);
        Expect("agent that has already edited a file", Pick(written: true), "high", null);
        Expect("agent at 60% of the window", Pick(ratio: 0.6), "high", null);
        Expect("agent at 59% of the window", Pick(ratio: 0.59), "medium", null);
        Expect("two tool failures in a row", Pick(failures: 2), "xhigh", null);
        Expect("one tool failure does not escalate", Pick(failures: 1, written: true), "high", null);
        Expect("the user steered mid-reply", Pick("ask", steered: true), "xhigh", null);
        Expect("failures outrank an edited file", Pick(failures: 3, written: true), "xhigh", null);
        Expect("an unrecognised mode takes the cheapest tier", Pick("不认识的模式"), "low", null);
        Expect("a written-out task is not a lookup", Pick("ask", draft: 4000), "medium", null);
        Expect("the long-draft floor still obeys the ceiling", Pick("ask", draft: 4000, ceiling: "low"), "low", null);
        Expect("the ceiling clips the top rung", Pick(failures: 5, ceiling: "high"), "high", null);
        Expect("escalation is xhigh however high the ceiling goes", Pick(failures: 5, ceiling: "ultra"), "xhigh", null);
        Expect("automatic reasoning preserves the selected model even when model slots are configured", Pick(written: true, fast: "mini", strong: "big"), "high", null);
        Expect("automatic reasoning preserves the selected model for low tiers too", Pick("ask", fast: "mini", strong: "big"), "low", null);
        Expect("a picture keeps the model that was picked", Pick(written: true, images: 1, fast: "mini", strong: "big"), "high", null);
        Expect("ceiling-clamped reasoning still preserves the selected model", Pick(failures: 4, fast: "mini", strong: "big", ceiling: "medium"), "medium", null);
        var capped = ModelRouting.Decide(new ModelRoutingSignal("agent", 4, false, 0, false, 0, 12, "high", null, null));
        if (capped.ReasoningEffort != "high" || !capped.Reason.Contains("capped at high", StringComparison.Ordinal))
            throw new Exception($"A clamped decision does not say it was clamped: {capped.Reason}");
        foreach (var signal in new[]
        {
            new ModelRoutingSignal("ask", 0, false, 0, false, 0, 12, "xhigh", null, null),
            new ModelRoutingSignal("plan", 0, false, 0, false, 0, 12, null, null, null),
            new ModelRoutingSignal("agent", 2, true, 0.8, true, 1, 4000, "ultra", "mini", "big"),
        }

        )
            if (ModelRouting.Decide(signal).Reason.Length == 0)
                throw new Exception("A routing decision arrived with no reason, which is a black box with a bill attached.");
        foreach (var(input, expected)in new (string? , string)[]
        {
            (null, "xhigh"),
            ("", "xhigh"),
            ("banana", "xhigh"),
            ("default", "xhigh"),
            ("auto", "xhigh"),
            ("low", "low"),
            ("ultra", "ultra"),
        }

        )
            if (ModelRouting.Ceiling(input) != expected)
                throw new Exception($"Ceiling({input ?? "(null)"}) answered {ModelRouting.Ceiling(input)} instead of " + $"{expected} — an unreadable ceiling must never mean \"no ceiling\".");
        if (!(ModelRouting.Rank("low") < ModelRouting.Rank("medium") && ModelRouting.Rank("medium") < ModelRouting.Rank("high") && ModelRouting.Rank("high") < ModelRouting.Rank("xhigh") && ModelRouting.Rank("xhigh") < ModelRouting.Rank("max") && ModelRouting.Rank("max") == ModelRouting.Rank("ultra") && ModelRouting.Rank("default") == 0 && ModelRouting.Rank("香蕉") == 0))
            throw new Exception("The effort order is not what the ceiling compares against.");
        if (ModelRouting.Clamp("xhigh", "max") != "xhigh" || ModelRouting.Clamp("max", "high") != "high" || ModelRouting.Clamp("low", "high") != "low")
            throw new Exception("The clamp does not bound the tier it is given.");
        foreach (var(input, expected)in new (string? , string)[]
        {
            (null, "manual"),
            ("", "manual"),
            ("auto", "auto"),
            ("AUTO", "manual"),
            ("banana", "manual")
        }

        )
            if (ChatRouting.Normalize(input) != expected)
                throw new Exception($"ChatRouting.Normalize({input ?? "(null)"}) answered {ChatRouting.Normalize(input)} " + $"instead of {expected} — a value this build cannot read must not switch routing on.");
        var routingRoot = Path.Combine(root, "routing");
        if (Directory.Exists(routingRoot))
            Directory.Delete(routingRoot, recursive: true);
        Directory.CreateDirectory(routingRoot);
        var routed = Conversation.Create("orcarouter");
        routed.Routing = ChatRouting.Auto;
        routed.Append(ChatTurn.User("这条会话按强度路由"));
        var sessions = new ConversationStore(routingRoot);
        sessions.Save(routed);
        if (sessions.Load(routed.Id)is not { Routing: "auto" })
            throw new Exception("A session's routing choice did not survive one save.");
        // The forward-compatibility rule every optional field follows: a session file written before routing existed
        // has no such property, and loading it must not turn automatic effort selection on for someone who never asked.
        File.WriteAllText(Path.Combine(routingRoot, "ai", "sessions", "legacy-no-routing.json"), """{"Id":"legacy-no-routing","Title":"legacy","ProviderId":"orcarouter","Mode":"agent","Messages":[{"Role":"user","Text":"早于路由功能"}]}""");
        if (sessions.Load("legacy-no-routing")is not { } legacyRouting || legacyRouting.Routing != ChatRouting.Manual)
            throw new Exception("A session file predating routing no longer loads, or loads as auto.");
        var prefFile = Path.Combine(routingRoot, "preferences.json");
        var prefStore = new PreferencesStore(prefFile);
        prefStore.Save(new HubPreferences { MaxAutoEffort = "low" });
        if (prefStore.Load().MaxAutoEffort != "low")
            throw new Exception("The routing ceiling did not round-trip through the settings file.");
        // A settings file this build cannot read has to come back as the shipped ceiling, not as no ceiling.
        File.WriteAllText(prefFile, """{"MaxAutoEffort":"banana","Language":"zh"}""");
        if (prefStore.Load().MaxAutoEffort != ChatReasoningEfforts.XHigh)
            throw new Exception("An unreadable ceiling fell back to something other than the shipped default.");
        Directory.Delete(routingRoot, recursive: true);
        Console.WriteLine($"PASS: all {cells} routing cells hold, with the ceiling, the slots and both round-trips.");
        return;
    }
}
