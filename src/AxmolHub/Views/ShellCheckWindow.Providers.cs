using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using AxmolHub.Core;
using static AxmolHub.ShellCheckSupport;

namespace AxmolHub;
internal sealed class ShellProvidersChecks
{
    private readonly Action<bool, string> Check;
    internal ShellProvidersChecks(CheckReport report, Action<bool, string> check, ShellCheckFixture fixture)
    {
        Check = check;
    }

    internal async Task VerifyDeepSeekWebApiTransportAsync()
    {
        var handler = new DeepSeekApiProbeHandler();
        using var http = new System.Net.Http.HttpClient(handler);
        var provider = new ModelProvider
        {
            BaseUrl = "https://api.deepseek.com/v1",
            Credential = new ProviderCredential { Secret = "probe-key" },
        };
        var calls = new List<DeepSeekWebApiCall>();
        var bridge = OutboundDeepSeekWebApi.Bridge(provider, http, () => true, calls.Add)!;
        handler.Responses.Enqueue((System.Net.HttpStatusCode.OK,
            """{"results":[{"rerank_score":2.1,"title":"Guide","url":"https://example.com/guide","snippet":"short","summary":"long","content":"","site_name":"Example","date":null,"authority":3}]}"""));
        var search = await bridge.Search(new DeepSeekWebSearchRequest("engine guide"), CancellationToken.None);
        using (var body = System.Text.Json.JsonDocument.Parse(handler.Requests[0].Body))
        {
            var root = body.RootElement;
            Check(handler.Requests[0].Url == "https://api.deepseek.com/v1/tools/search"
                  && handler.Requests[0].Authorization == "Bearer probe-key"
                  && root.GetProperty("query").GetString() == "engine guide"
                  && !root.GetProperty("need_content").GetBoolean()
                  && !root.TryGetProperty("count", out _)
                  && search.Results.Count == 1,
                "DeepSeek Search 使用官方地址、Bearer 鉴权和默认条数，并关闭正文返回");
        }

        handler.Responses.Enqueue((System.Net.HttpStatusCode.OK,
            """{"title":"Guide","content":"# Guide"}"""));
        var fetch = await bridge.Fetch(new Uri("https://example.com/guide"), CancellationToken.None);
        using (var body = System.Text.Json.JsonDocument.Parse(handler.Requests[1].Body))
            Check(handler.Requests[1].Url == "https://api.deepseek.com/v1/tools/fetch"
                  && body.RootElement.GetProperty("url").GetString() == "https://example.com/guide"
                  && fetch.Content == "# Guide" && calls.Count == 2 && calls.All(call => call.Billable),
                "DeepSeek Fetch 发送页面 URL 并记录成功调用");

        handler.Responses.Enqueue((System.Net.HttpStatusCode.OK, """{"results":[]}"""));
        _ = await bridge.Search(new DeepSeekWebSearchRequest("no results"), CancellationToken.None);
        Check(calls.Count == 3 && !calls[^1].Billable,
            "空搜索结果会保留调用记录但不计入成功计费次数");

        handler.Responses.Enqueue((System.Net.HttpStatusCode.Unauthorized,
            """{"error":{"message":"invalid key"}}"""));
        var tools = new WorkspaceTools(WorkspaceToolScope.Empty with { DeepSeekWeb = bridge });
        var error = await tools.SearchWeb("bad key");
        Check(error.Contains("HTTP 401", StringComparison.Ordinal)
              && calls.Count == 4 && !calls[^1].Billable,
            "API 错误作为明确工具结果返回，并保留非计费调用记录");

        handler.CancelNext = true;
        var timeout = await tools.SearchWeb("timeout");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var canceledResult = await tools.SearchWeb("canceled", cancellationToken: canceled.Token);
        Check(timeout.Contains("timed out", StringComparison.OrdinalIgnoreCase)
              && canceledResult.Contains("canceled", StringComparison.OrdinalIgnoreCase)
              && calls.Count == 6 && calls[^1] is { Operation: "search", Billable: false }
              && calls[^2] is { Operation: "search", Billable: false },
            "超时和取消返回清晰工具结果，不计费成功且保留调用记录");

        var offBridge = OutboundDeepSeekWebApi.Bridge(provider, http, () => false, null)!;
        var scoped = WorkspaceToolScope.Empty with { DeepSeekWeb = offBridge };
        // The shape production builds: the mode flag is never set outside these fixtures, and setting it here
        // would have measured a configuration no user can reach.
        ChatToolScope WebScope(bool enabled) => new(
            new ChatWorkspace.HubReadOnlySnapshot([], [], []), scoped, DeepSeekWebApiEnabled: enabled);
        var offTools = ChatTools.CreateFor(ChatModes.Agent, WebScope(enabled: false));
        var namesOff = offTools.Select(tool => tool.Name).ToArray();
        var namesOn = ChatTools.CreateFor(ChatModes.Agent, WebScope(enabled: true))
            .Select(tool => tool.Name).ToArray();
        // With the switch off, search stays declared and answers with a refusal. The rule it replaces — "do not
        // advertise what is switched off" — lost to a measurement from the request side: the transcript replays
        // every recorded call, so the name is in front of the model whatever this request declares, and a name the
        // request does not declare is the one the invoking client refuses to run — it ends the turn right there,
        // with no prose and no trace. Ask keeps none of this: its request carries no executable tool by design.
        // Convert.ToString rather than a cast: what an AIFunction hands back is the factory's business, and this
        // project already reads tool results that way everywhere else (Program.cs's bound functions).
        var refusedSearch = Convert.ToString(await ((Microsoft.Extensions.AI.AIFunction)offTools
            .First(tool => tool.Name == "web_search"))
            .InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(
                new Dictionary<string, object?> { ["query"] = "axmol" })));
        var askOff = ChatTools.CreateFor(ChatModes.Ask, WebScope(enabled: false))
            .Select(tool => tool.Name).ToArray();
        Check(namesOff.Contains("web_search") && namesOff.Contains("web_fetch")
              && refusedSearch?.StartsWith("Refused:", StringComparison.Ordinal) == true
              && namesOn.Contains("web_search") && namesOn.Contains("web_fetch")
              && namesOn.Count(name => name is "web_search" or "web_fetch") == 2
              && askOff.Length == 0,
            "总开关关闭时 search 仍声明但以拒绝作答（Ask 档一个不给），开启时每个 web 名字只有一组真实工具（实际 关="
            + string.Join(",", namesOff) + "；开=" + string.Join(",", namesOn)
            + "；拒绝=" + (refusedSearch ?? "空") + "；Ask=" + string.Join(",", askOff) + "）");
    }

    internal void CheckDeepSeekWebUsage(MainWindow shell, ChatPanel panel)
    {
        var chat = shell.Chat;
        var previous = chat.ActiveConversation?.Id;
        var session = chat.StartConversation();
        try
        {
            session.Append(ChatTurn.User("检查网页 API 调用统计"));
            void AddCall(string operation, bool billable)
            {
                var id = NextCallId("web-usage");
                session.Append(ChatTurn.FunctionCall(id, "web_" + operation, "{}"));
                session.AppendFunctionResult(ChatTurn.FunctionResult(id, "Probe result",
                    webApiCall: new DeepSeekWebApiCall(operation, billable, DateTimeOffset.UtcNow)));
            }
            bool ShowsUsage(int searches, int billableSearches, int fetches, int billableFetches, decimal cost)
            {
                panel.Reload();
                Relayout(shell, panel, measure: true);
                var expected = string.Format(CultureInfo.CurrentCulture, HubStrings.Get("DeepSeekWebUsageFormat"),
                    searches, billableSearches, fetches, billableFetches, cost);
                return panel.GetLogicalDescendants().OfType<TextBlock>().Any(block => block.Text == expected);
            }

            AddCall("search", false);
            AddCall("fetch", false);
            chat.SeedAssistantReplyForCheck(session.Id, "这两次请求没有计费结果。");
            Check(ShowsUsage(1, 0, 1, 0, 0m),
                "网页 API 没有计费成功时仍显示 Search/Fetch 总调用次数和零估算费用");

            AddCall("search", true);
            AddCall("fetch", true);
            chat.SeedAssistantReplyForCheck(session.Id, "这两次请求返回了计费结果。");
            var stored = chat.StoredCopyForCheck(session.Id)?.Messages
                .Select(turn => turn.WebApiCall).Where(call => call is not null).ToArray();
            Check(ShowsUsage(2, 1, 2, 1, 0.010m)
                  && stored?.Length == 4 && stored.Count(call => call!.Billable) == 2,
                "会话保存全部 API 调用，显示总次数与计费成功次数，并只按成功次数估算费用");
        }
        finally
        {
            chat.DeleteConversation(session.Id);
            if (previous is not null) chat.OpenConversation(previous);
            panel.Reload();
        }
    }

    internal async Task CheckProvidersInSettingsAsync(string scratchRoot, MainWindow shell)
    {
        var settings = OpenSettingsOn(shell, "providers");

        Check(settings.ProviderCount >= 1, "设置页列出了内置 provider（实际 " + settings.ProviderCount + " 个）");
        // Every configured provider gets its own group, so the count on screen must match the model — a group
        // that failed to render would otherwise be invisible while the provider "existed".
        var orca = shell.Chat.Providers.FirstOrDefault(provider => provider.Id == "orcarouter");
        settings.RefreshProviderGroupsForCheck();
        Check(settings.ProviderGroupCount == settings.ProviderCount,
            "每个已配置的 provider 各渲染一组（实际 " + settings.ProviderGroupCount + " 组 / "
            + settings.ProviderCount + " 个 provider）");
        // The group is titled with the display name, not the type name — the failure a naive binding makes.
        Check(settings.ProviderGroupForId("orcarouter") is not null,
            "分组标题用的是 provider 名称（实际 [" + string.Join(",", settings.ProviderGroups.Select(g => g.Name)) + "]）");
        // The header's wire line: which path this provider's model is actually posted to. It is the one place a
        // wrong claim about an endpoint becomes visible before a 400 has to explain it — the 400 names a path
        // ("please use /v1/responses"), not a setting — so it is asserted as a line that is there and shaped like a
        // path, on every group, rather than as "some text got added to the header".
        var wireLabel = string.Format(CultureInfo.CurrentCulture, HubStrings.Get("ProviderWireFormat"), "");
        var wireLines = settings.ProviderGroups.Select(group => group.WireText).ToArray();
        Check(wireLines.Length > 0
              && wireLines.All(text => text.StartsWith(wireLabel, StringComparison.Ordinal)
                                       && text[wireLabel.Length..].StartsWith("/v1/", StringComparison.Ordinal)),
            "每个 provider 分组都写明请求线路，且写的是路径（实际 [" + string.Join(",", wireLines) + "]）");
        Check(settings.ProviderButtonsVisible, "设置页显示了「添加提供商」入口");
        Check(settings.AffiliateDisclosureVisible
              && settings.AffiliateDisclosureText.Contains(orca?.Name ?? "\u0000", StringComparison.Ordinal),
            "affiliate 披露在 OrcaRouter 分组内可见且点名了 provider（实际「" + settings.AffiliateDisclosureText + "」）");

        // ── Seeding: exactly one preset, not the whole catalog ──
        // A fresh workspace has no providers.json at all. Seeding every preset was the old behaviour and read
        // as "why do I have four endpoints I never added"; the contract now is one, and it is the manifest's
        // declared default. Asserted on a brand-new root so a leftover file cannot mask the branch.
        var freshRoot = Path.Combine(scratchRoot, "fresh-seed");
        Directory.CreateDirectory(freshRoot);
        using (var fresh = new ChatWorkspace(freshRoot))
        {
            Check(fresh.Providers.Count == 1, "全新安装只预置一个 provider（实际 " + fresh.Providers.Count + " 个）");
            Check(fresh.Providers[0].Id == AiProviderManifest.DefaultProviderId(),
                "预置的是清单声明的默认 provider（实际「" + fresh.Providers[0].Id + "」）");
            // A second workspace over the same root must not re-seed or duplicate: the saved file is
            // authoritative once it exists.
            fresh.SaveProviders();
        }

        using (var again = new ChatWorkspace(freshRoot))
        {
            Check(again.Providers.Count == 1, "已有 providers.json 时不再重新播种（实际 " + again.Providers.Count + " 个）");
        }

        // ── The preset catalog and the picker's search ──
        var presets = shell.Chat.AvailablePresets();
        Check(presets.Count >= 2, "清单提供多个可选预设（实际 " + presets.Count + " 个）");
        Check(presets.All(preset => preset.Description.Length > 0),
            "每个预设都带说明文案（用于搜索卡片）");
        Check(presets.All(preset => !preset.IsCustom), "预设列表里不含自定义 provider");
        Check(presets.All(preset => shell.Chat.Providers.All(provider => provider.Id != preset.Id)),
            "已配置的 provider 不再出现在可选预设里（不会重复添加）");

        // ── Multiple accounts for one service ──
        //
        // A provider id names one account, so a second account for the same service is a second provider
        // pointing at the same base URL. That is why the list filters an
        // already-configured preset out rather than offering a "add another account" affordance on it: a
        // built-in's id is its identity in the manifest, and two entries sharing it would be one entry.
        //
        // The escape hatch is the custom provider, which is already how a second account is added — it takes
        // any http(s) endpoint and its own key. Asserted here because the one-to-one migration removed the
        // account list that used to be the other way in, and this is now the *only* way in. If the base URL
        // check ever tightened enough to reject a duplicate endpoint, this is what would catch it.
        var duplicateUrl = shell.Chat.Providers.FirstOrDefault(provider => !provider.IsCustom)?.BaseUrl ?? "";
        Check(duplicateUrl.Length > 0, "至少有一个已配置 provider 提供了可复用的 base URL");
        var twinId = shell.Chat.AddProvider("DeepSeek (个人)", duplicateUrl, "twin-model", "sk-second-account", out _)?.Id;
        Check(twinId is not null, "可以为同一服务添加第二个 provider（指向同一 base URL）");
        Check(twinId is not null && shell.Chat.Providers.Count(provider => provider.BaseUrl == duplicateUrl) == 2,
            "两个 provider 可以共用一个 base URL（实际 "
                + shell.Chat.Providers.Count(provider => provider.BaseUrl == duplicateUrl) + " 个）");
        // The point of a second entry: they hold different keys, independently revocable. Same base URL with
        // one shared credential would be a duplicate row that looks like a second account and is not.
        Check(twinId is not null && CountCredentials(shell.Chat, twinId) == 1
              && shell.Chat.CredentialFor(twinId)?.Secret == "sk-second-account",
            "第二个 provider 持有自己那份密钥（实际「" + shell.Chat.CredentialFor(twinId ?? "")?.Secret + "」）");
        Check(twinId is not null && shell.Chat.DisconnectProvider(twinId)
              && CountCredentials(shell.Chat, twinId) == 0,
            "断开其中一个不影响另一个（断开后剩 "
                + CountCredentials(shell.Chat, duplicateUrl.Length > 0 && twinId is not null ? twinId : "") + " 份）");
        Check(twinId is not null && shell.Chat.RemoveProvider(twinId), "第二个 provider 可以单独移除");
        Check(shell.Chat.Providers.All(provider => provider.Id != "deepseek" || provider.BaseUrl != duplicateUrl),
            "移除后另一个 provider 仍在列表里");

        var picker = ProviderPickerWindow.ForCheck(presets);
        Check(picker.RowCountForCheck == presets.Count + 1,
            "选择器列出全部预设 + 一个「自定义接口」行（实际 " + picker.RowCountForCheck + " 行）");
        Check(picker.CustomRowVisibleForCheck, "空查询时「自定义接口」行可见");
        Check(!picker.EmptyNoticeVisibleForCheck, "空查询时不显示「无匹配」提示");
        // Scrolling this list recycles containers, and clearing one runs the row template with a null item. The
        // template used to dereference that item, so the first scroll threw out of a layout pass and took the
        // process with it. A frame of the freshly opened dialog cannot see this — nothing is ever recycled before
        // the first scroll — so the template is driven here instead.
        Check(picker.RowTemplateSurvivesNullItemForCheck(),
            "行模板扛得住空项：列表回收滚出视野的容器时就是这么调它的，曾经一滚就崩");

        // The query is a product name no other preset's description mentions. "deep" used to be here, and it
        // stopped isolating one row the day a preset legitimately described itself as fronting DeepSeek models —
        // which is the feature the description search exists for, so the assertion was the thing that was wrong.
        picker.SearchForCheck("copilot");
        Dispatcher.UIThread.RunJobs();
        Check(picker.VisiblePresetIdsForCheck.Length == 1 && picker.VisiblePresetIdsForCheck[0] == "github-copilot",
            "搜索按名称过滤（实际 [" + string.Join(",", picker.VisiblePresetIdsForCheck) + "]）");
        Check(picker.CustomRowVisibleForCheck == false, "不相关的查询会隐藏「自定义接口」行");

        // Searching the *description* must work too: someone who remembers "本地" should land on Ollama
        // without knowing the product is called Ollama.
        picker.SearchForCheck("本地");
        Dispatcher.UIThread.RunJobs();
        Check(picker.VisiblePresetIdsForCheck.Length >= 1 && picker.VisiblePresetIdsForCheck.Contains("ollama"),
            "搜索命中说明文案而不只是名称（实际 [" + string.Join(",", picker.VisiblePresetIdsForCheck) + "]）");

        picker.SearchForCheck("zzz-no-such-provider");
        Dispatcher.UIThread.RunJobs();
        Check(picker.RowCountForCheck == 0 && picker.EmptyNoticeVisibleForCheck,
            "无匹配时列表为空且显示提示（实际 " + picker.RowCountForCheck + " 行）");

        picker.SearchForCheck("custom");
        Dispatcher.UIThread.RunJobs();
        Check(picker.CustomRowVisibleForCheck, "搜索「custom」时仍能找到「自定义接口」行");

        // Back to the full list before choosing: the search box is a filter, and a stale query would make the
        // two assertions below depend on whatever the last query happened to match.
        picker.SearchForCheck("");
        Dispatcher.UIThread.RunJobs();

        // Choosing a preset must set the id, not the custom flag — the two exits of the dialog are what the
        // caller branches on, so getting them crossed would silently open the wrong form.
        Check(picker.ChoosePresetForCheck(presets[0].Id) && picker.SelectedId == presets[0].Id && !picker.WantsCustom,
            "选中预设时返回其 id 且不要求自定义表单");
        Check(picker.ChooseCustomForCheck() && picker.WantsCustom, "选中「自定义接口」行时返回自定义意图");

        // ── Adopting a preset ──
        var adoptId = presets[0].Id;
        var adopted = shell.Chat.AddPreset(adoptId);
        Check(adopted is { IsCustom: false } && adopted.Id == adoptId && adopted.BaseUrl.Length > 0,
            "采纳预设会带上清单里的接口地址与模型");
        Check(shell.Chat.AddPreset(adoptId) is null, "重复采纳同一预设被拒绝");
        Check(shell.Chat.AvailablePresets().All(preset => preset.Id != adoptId), "采纳后该预设不再出现在候选里");
        // The protection is on the *default* provider, not on the origin of the entry: adopting a preset and
        // then removing it works, and the preset returns to the catalogue. Keeping the old "built-ins cannot
        // be removed" rule here would have asserted behaviour the product deliberately does not have.
        Check(adoptId != AiProviderManifest.DefaultProviderId(),
            "这里采纳的预设不是默认 provider，所以移除规则不适用于它");
        Check(shell.Chat.RemoveProvider(adoptId), "非默认的内置预设可以移除（保护规则只针对默认 provider）");
        Check(shell.Chat.AvailablePresets().Any(preset => preset.Id == adoptId),
            "移除后该预设回到候选目录");
        Check(shell.Chat.AddPreset(adoptId) is not null, "移除的预设可以再次采纳");

        var providerCountBefore = shell.Chat.Providers.Count;

        // Validation: a custom provider without a name or without a usable URL is refused. A model is NOT part of
        // the rule — the model list is fetched from the endpoint after it exists, so a model-less provider has to
        // be saveable; what it must not do is pretend to have a model, so the list stays empty rather than
        // holding one blank row.
        Check(shell.Chat.AddProvider("本地模型", "http://localhost:11434/v1", "llama3", null, out _) is not null,
            "添加合法的自定义 provider 成功");
        Check(shell.Chat.Providers.Count == providerCountBefore + 1, "新 provider 进入列表（实际 " + shell.Chat.Providers.Count + " 个）");
        Check(shell.Chat.AddProvider("", "http://localhost:11434/v1", "llama3", null, out _) is null, "缺名称的自定义 provider 被拒绝");
        Check(shell.Chat.AddProvider("坏地址", "localhost:11434/v1", "llama3", null, out _) is null,
            "非绝对 http(s) 地址被拒绝（裸 host 会到第一次发消息才炸）");
        var modelLess = shell.Chat.AddProvider("缺模型", "http://localhost:11434/v1", "   ", null, out _);
        Check(modelLess is { } blank && blank.Models.Count == 0 && shell.Chat.RemoveProvider(blank.Id),
            "没有模型的自定义 provider 也能保存，模型列表留空而不是留一行空名字");

        // The new provider persists and reloads with its custom flag intact — a custom provider must not be
        // re-derived from the manifest (there is no manifest entry for it) on the next load.
        var custom = shell.Chat.Providers.First(provider => provider.IsCustom);
        var reloaded = new ChatWorkspace(scratchRoot);
        var reloadedCustom = reloaded.Providers.FirstOrDefault(provider => provider.Id == custom.Id);
        Check(reloadedCustom is { IsCustom: true } && reloadedCustom.BaseUrl == "http://localhost:11434/v1"
              && reloadedCustom.Model == "llama3",
            "自定义 provider 落盘并在重载后保持（含 base URL 与模型）");
        reloaded.Dispose();

        // Editing changes a custom provider's endpoint in place.
        Check(shell.Chat.UpdateProvider(custom.Id, "本地模型", "http://localhost:8080/v1", "qwen2", null, out _),
            "编辑自定义 provider 成功");
        Check(shell.Chat.UpdateProvider(custom.Id, "本地模型", "not a url", "qwen2", null, out _) == false,
            "编辑成非法地址被拒绝（校验与保存路径同一份规则）");

        // A built-in provider's base URL/name are pinned to the manifest; an update that tries to change them
        // succeeds but leaves them untouched (the model may change), so the outcome is what is asserted — a
        // boolean alone would not catch "accepted the edit and wrote the new URL anyway".
        Check(shell.Chat.UpdateProvider("orcarouter", "改名", "http://evil", "orcarouter/auto", null, out _),
            "内置 provider 的模型可以更新");
        var orcaAfter = shell.Chat.Providers.First(provider => provider.Id == "orcarouter");
        Check(orcaAfter.Name == "OrcaRouter" && orcaAfter.BaseUrl == "https://api.orcarouter.ai/v1",
            "内置 provider 的名称与接口地址被钉在清单上（实际「" + orcaAfter.Name + "」/「" + orcaAfter.BaseUrl + "」）");
        Check(shell.Chat.RemoveProvider("orcarouter") == false, "内置 provider 不能被移除");
        Check(shell.Chat.RemoveProvider(custom.Id), "自定义 provider 可以移除");
        Check(shell.Chat.Providers.All(provider => !provider.IsCustom), "移除后列表里不再有自定义 provider");

        // ── The custom endpoint draft: one full-width field per row and the same endpoint settings GitHub Desktop exposes ──
        settings.OpenAddProviderDraftForCheck();
        var boxes = settings.AddProviderDraftLayoutForCheck();
        var rows = new[] { boxes.Name, boxes.BaseUrl, boxes.Wire, boxes.Key, boxes.Headers };
        var options = settings.AddProviderDraftWireOptionsForCheck();
        Check(boxes.Present && rows.All(rect => rect.Height > 0 && rect.Width > 0)
              && rows.All(rect => Math.Abs(rect.X - rows[0].X) < 1 && Math.Abs(rect.Width - rows[0].Width) < 1)
              && rows.Zip(rows.Skip(1), (first, next) => next.Y > first.Y && !first.Intersects(next)).All(valid => valid),
            "自定义 provider 的五个设置各占一行、左边界和宽度一致（实际 [" + string.Join("; ", rows) + "]）");
        Check(options.Count == 2 && options.SelectedProtocol == ModelProtocols.Chat && options.KeyIsMasked,
            "Wire API 只列 Chat Completions 与 Responses、默认 Chat，API key 输入受遮蔽（实际 count="
            + options.Count + " selected=" + (options.SelectedProtocol ?? "none") + " masked=" + options.KeyIsMasked + ")");

        var badUrlTyped = settings.FillAddProviderDraftForCheck("AgentRouterCustom", "agentrouter.org/v1");
        var badUrlProblem = settings.StatusLineText;
        Check(badUrlTyped && !settings.AddProviderDraftLayoutForCheck().SaveEnabled
              && badUrlProblem == HubStrings.Get("ProviderBaseUrlInvalid"),
            "地址不完整时添加按钮被禁用，状态行点名地址（实际「" + badUrlProblem + "」）");

        var badHeadersTyped = settings.FillAddProviderDraftForCheck(
            "AgentRouterCustom", "https://agentrouter.org/v1", ModelProtocols.Responses, "", "{");
        var badHeadersProblem = settings.StatusLineText;
        var nonStringHeadersTyped = settings.FillAddProviderDraftForCheck(
            "AgentRouterCustom", "https://agentrouter.org/v1", ModelProtocols.Responses, "", "{\"X-Org\":true}");
        Check(badHeadersTyped && nonStringHeadersTyped && !settings.AddProviderDraftLayoutForCheck().SaveEnabled
              && badHeadersProblem == HubStrings.Get("ProviderCustomHeadersInvalid")
              && settings.StatusLineText == HubStrings.Get("ProviderCustomHeadersInvalid"),
            "无效 JSON 或非字符串请求头值都会禁用添加并点名请求头（实际「" + settings.StatusLineText + "」）");

        var countBeforeDraft = shell.Chat.Providers.Count;
        var typed = settings.FillAddProviderDraftForCheck(
            "AgentRouterCustom", "https://agentrouter.org/v1", ModelProtocols.Responses,
            "sk-custom-endpoint", "{\"X-Org\":\"acme\"}");
        var liveProblem = settings.StatusLineText;
        var selectedOptions = settings.AddProviderDraftWireOptionsForCheck();
        Check(typed && settings.AddProviderDraftLayoutForCheck().SaveEnabled && string.IsNullOrEmpty(liveProblem)
              && selectedOptions.SelectedProtocol == ModelProtocols.Responses
              && shell.Chat.Providers.Count == countBeforeDraft,
            "名称、地址、Responses、API key 与合法请求头有效后可添加（状态行「" + liveProblem + "」）");

        var modelListHandler = new ModelListProbeHandler("""{"object":"list","data":[{"id":"custom-model"}]}""");
        using var modelListHttp = new System.Net.Http.HttpClient(modelListHandler);
        shell.Chat.ModelListHttp = modelListHttp;
        var committed = await settings.ClickAddProviderDraftSaveForCheck();
        shell.Chat.ModelListHttp = null;
        var drafted = shell.Chat.Providers.FirstOrDefault(provider => provider.IsCustom);
        Check(committed && !settings.AddProviderDraftLayoutForCheck().Present
              && drafted is not null && drafted.BaseUrl == "https://agentrouter.org/v1"
              && drafted.Protocol == ModelProtocols.Responses
              && drafted.CustomHeaders.TryGetValue("x-org", out var customHeader) && customHeader == "acme"
              && drafted.Headers.Count == 1 && drafted.Headers.TryGetValue("X-Org", out var effectiveHeader)
              && effectiveHeader == "acme"
              && modelListHandler.Requests.Count == 1
              && modelListHandler.Requests[0].Headers.TryGetValues("X-Org", out var sentHeader)
              && sentHeader.Single() == "acme"
              && shell.Chat.CredentialFor(drafted.Id)?.Secret == "sk-custom-endpoint"
              && shell.Chat.CachedModels(drafted.Id).Contains("custom-model", StringComparer.OrdinalIgnoreCase)
              && shell.Chat.Providers.Count == countBeforeDraft + 1,
            "添加后线路、请求头、密钥与目录模型均生效（实际 "
            + (drafted is null ? "未添加" : "wire=" + drafted.Protocol + " headers=" + drafted.Headers.Count
                + " key=" + (shell.Chat.CredentialFor(drafted.Id)?.HasSecret == true)
                + " catalog=" + shell.Chat.CachedModels(drafted.Id).Count) + "）");

        var draftedStatus = drafted is null ? null : settings.ProviderGroupForId(drafted.Id)?.LinkedStatusText;

        using var providerReload = new ChatWorkspace(scratchRoot);
        var reloadedConfigCustom = providerReload.Providers.FirstOrDefault(provider => provider.Id == drafted?.Id);
        var expectedWireLine = string.Format(CultureInfo.CurrentCulture,
            HubStrings.Get("ProviderWireFormat"), "/v1/responses");
        Check(drafted is not null && settings.ProviderGroupForId(drafted.Id)?.WireText == expectedWireLine,
            "新建 provider 的折叠行显示所选 Responses 请求路径（实际「"
            + (drafted is null ? "无分组" : settings.ProviderGroupForId(drafted.Id)?.WireText ?? "无线路") + "」）");
        Check(reloadedConfigCustom is { Protocol: ModelProtocols.Responses }
              && reloadedConfigCustom.CustomHeaders.TryGetValue("x-org", out var savedHeader) && savedHeader == "acme",
            "重开 workspace 后自定义线路与请求头仍持久化");

        if (drafted is not null && shell.Chat.RemoveProvider(drafted.Id)) settings.RefreshProviderGroupsForCheck();

        // A blank optional key remains a legitimate keyless endpoint and does not force an authentication dialog.
        settings.OpenAddProviderDraftForCheck();
        settings.FillAddProviderDraftForCheck("Keyless endpoint", "http://localhost:11434/v1");
        var keylessSaved = await settings.ClickAddProviderDraftSaveForCheck();
        var keyless = shell.Chat.Providers.FirstOrDefault(provider => provider.IsCustom);
        Check(keylessSaved && keyless is { IsCustom: true } && shell.Chat.CredentialFor(keyless.Id) is null,
            "API key 留空时仍能添加自定义 provider，且不会创建凭据");
        var keylessStatus = keyless is null ? null : settings.ProviderGroupForId(keyless.Id)?.LinkedStatusText;
        // The optional-key requirement cannot detect a real stored key. A wrong mark makes a working
        // custom endpoint look unauthenticated; exercise both drafts through the actual rendered label.
        Check(draftedStatus == HubStrings.Get("ProviderConnected")
              && keylessStatus == HubStrings.Get("ProviderKeylessReady"),
            "自定义 provider 的状态按实际密钥显示：有密钥已鉴权，留空无需密钥（实际 ["
                + draftedStatus + " | " + keylessStatus + "]）");
        if (keyless is not null && shell.Chat.RemoveProvider(keyless.Id)) settings.RefreshProviderGroupsForCheck();

        // ── Sign-in methods and accounts ──
        // The block is driven through the page's real selection path (SelectProviderForCheck → the same
        // UpdateProviderDetail a click triggers), so what is asserted is the object graph the user would see.
        await CheckAuthAndAccountsAsync(scratchRoot, settings, shell);

        await Task.CompletedTask;
    }

    internal async Task CheckAuthAndAccountsAsync(string scratchRoot, SettingsPage settings, MainWindow shell)
    {
        // Ollama is a *preset*, not a configured provider — it only reaches the list once adopted. Adopt it
        // so the assertion exercises the keyless path rather than finding no group at all (which would pass a
        // "no sign-in shown" check for the wrong reason).
        var ollamaAdopted = shell.Chat.AddPreset("ollama") is not null;
        Check(ollamaAdopted, "可以采纳 Ollama 预设以验证无凭据路径");
        settings.RefreshProviderGroupsForCheck();
        var ollama = settings.ProviderGroupForId("ollama");
        Check(ollama is not null, "无凭据的 provider 同样有自己的分组，而不是从列表里消失");
        Check(ollama is not null && ollama.ShowsNoCredentialNote,
            "本地 provider（Ollama）只说明「无需密钥」，不给出鉴权入口");
        Check(ollama is { AuthButtonText: "" },
            "无需鉴权的 provider 不显示鉴权按钮（点它不会有任何事可做）");

        // The mirror case, and the reason 「需不需要凭据」 and 「有没有鉴权入口」 are two predicates rather than
        // one: a self-supplied endpoint is never gated (its requirement is unknowable) yet must still offer the
        // key entrance — otherwise the user whose local server does want a key has nowhere to give it, and the
        // row would insist it needs none.
        var customLocal = shell.Chat.AddProvider("无凭据但有入口", "http://localhost:11434/v1", "llama3", null, out _);
        Check(customLocal is not null, "为这条断言添加一个自定义 provider");
        settings.RefreshProviderGroupsForCheck();
        var customGroup = customLocal is null ? null : settings.ProviderGroupForId(customLocal.Id);
        Check(customGroup is not null && customGroup.AuthButtonText == HubStrings.Get("Authenticate"),
            "自定义 provider 即使不要求凭据也仍有「鉴权」入口（实际「" + customGroup?.AuthButtonText + "」）");
        Check(customGroup is not null && !customGroup.ShowsNoCredentialNote,
            "自定义 provider 不宣称「无需密钥」，它只是不被拦（实际提示"
            + (customGroup is { ShowsNoCredentialNote: true } ? "存在" : "不存在") + "）");
        Check(shell.Chat.RemoveProvider(customLocal?.Id ?? ""), "移除这条断言用的自定义 provider");
        settings.RefreshProviderGroupsForCheck();

        // ── Honest disclosure of the key backend ──
        // The three backend tiers guard against different things (DPAPI binds the logged-in user, the file
        // vault relies on a 0600 data key, the keyring defers to a daemon), so the claim "safely stored" must
        // resolve to a concrete tier. The rule is host-independent, so this asserts both what this machine's
        // page looks like right now and, through the same render function, the two tiers this machine is not.
        Check(shell.Chat.CanStoreSecrets,
            "本机可以保存密钥，鉴权闸门不再拦（Windows 走 DPAPI，Linux 走本机加密文件）");
        Check(settings.SecretBackendLineShown == (shell.Chat.SecretBackend.Kind is SecretStoreKind.EncryptedFile or SecretStoreKind.Keychain),
            "后端提示行只在用的是本机加密文件时出现（DPAPI 不把已经承诺过的东西再说一遍）");
        Check(settings.SecretBackendTextForCheck(SecretStoreKind.EncryptedFile, null) is { Length: > 0 },
            "文件档那一行说清密钥存在本机加密文件里，而不是含糊地讲「系统凭据存储」");
        Check(settings.OpenAiLayerForCheck, "OpenAI 登录层裁剪内容、有遮罩且位于设置内容之上");
        using (var openAiCardCancellation = new CancellationTokenSource())
        {
            try
            {
                await new AxmolHub.Agent.OpenAiDynamicLogin(AxmolHub.Agent.OpenAiDynamicTokenExchange.Create()).SignInAsync(
                    new OpenAiHostIdStore(scratchRoot).GetOrCreate(), null, attempt =>
                {
                    var card = new OpenAiLoginCard(attempt, () => openAiCardCancellation.Cancel());
                    Check(card.HasActionsForCheck && card.ClipToBounds && card.CallbackForCheck.PlaceholderText == HubStrings.Get("AuthOpenAiCallback"),
                        "OpenAI 登录卡片提供链接操作、回调输入与取消，且裁剪内容");
                    var cancel = card.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, HubStrings.Get("Cancel")));
                    cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                    Check(openAiCardCancellation.IsCancellationRequested, "关闭 OpenAI 登录卡片会取消授权等待");
                }, _ => { }, openAiCardCancellation.Token);
            }
            catch (OperationCanceledException) { }
        }
        Check(settings.SecretBackendTextForCheck(SecretStoreKind.Keychain, null) is { Length: > 0 },
            "macOS 提示密钥和登录令牌保存在钥匙串中");
        Check(settings.SecretBackendTextForCheck(SecretStoreKind.Dpapi, null) is null,
            "DPAPI 档不显示后端提示行（多说一次就是噪音）");
        Check(settings.SecretBackendTextForCheck(SecretStoreKind.EncryptedFile, "the data key file is missing") is { Length: > 0 },
            "已存的密钥读不出来时给一行可操作的说明，而不是静默地当成「还没鉴权」");

        // Switching authentication must not carry one service's catalog onto the other service.
        var authCacheRoot = Path.Combine(scratchRoot, "openai-auth-cache");
        using var authWorkspace = new ChatWorkspace(authCacheRoot);
        var openAiCacheProvider = authWorkspace.AddPreset("openai");
        Check(openAiCacheProvider is not null, "可添加 OpenAI 以验证鉴权方式切换");
        if (openAiCacheProvider is not null)
        {
            var authCatalogs = new ModelListStore(authCacheRoot);
            authCatalogs.Save("openai", ["api-fixture"]);
            authCatalogs.Save("openai:openaiOAuth", ["codex-fixture"]);
            authWorkspace.SetApiKey("openai", "fixture-api-key");
            var originalCredentialId = authWorkspace.CredentialFor("openai")?.Id;
            openAiCacheProvider.Models.Add(new ProviderModel { Name = "api-fixture", Enabled = true });
            var fixtureTokens = new OpenAiTokens("fixture-access", "fixture-refresh", null, DateTimeOffset.UtcNow.AddHours(1), "fixture-account");
            authWorkspace.AddOAuthCredential("openai", "fixture-account", "openid profile email offline_access", fixtureTokens.Encode(), "openaiOAuth");
            Check(openAiCacheProvider.IsOpenAiOAuth && openAiCacheProvider.Models.Select(model => model.Name).SequenceEqual(["codex-fixture"])
                && authWorkspace.CachedModels("openai").SequenceEqual(["codex-fixture"]),
                "API Key 切到 OAuth 只显示订阅模型缓存");
            using (var reopenedOpenAi = new ChatWorkspace(authCacheRoot))
                Check(reopenedOpenAi.Providers.First(provider => provider.Id == "openai").IsOpenAiOAuth
                    && reopenedOpenAi.CachedModels("openai").SequenceEqual(["codex-fixture"]),
                    "重启后恢复 OAuth 凭据类型和对应模型缓存");
            authWorkspace.SetApiKey("openai", "replacement-api-key");
            Check(!openAiCacheProvider.IsOpenAiOAuth && openAiCacheProvider.Models.Select(model => model.Name).SequenceEqual(["api-fixture"])
                && authWorkspace.CredentialFor("openai")?.Id == originalCredentialId && openAiCacheProvider.ApiKey == "replacement-api-key",
                "OAuth 切回 API Key 会清除令牌类型、替换秘密并恢复 API 模型缓存");
            authWorkspace.RemoveProvider("openai");
            Check(!authCatalogs.Load().Any(entry => entry.Id is "openai" or "openai:openaiOAuth"),
                "删除 OpenAI 提供商会删除两种鉴权模式的模型缓存");
        }

        // Loading legacy multi-account metadata must never delete the unused secrets.
        var retainedRoot = Path.Combine(scratchRoot, "retained-credentials");
        using (var setup = new ChatWorkspace(retainedRoot)) setup.AddPreset("openai");
        var retainedSecrets = AxmolHub.Agent.SecretStoreFactory.Create(retainedRoot);
        var retainedStore = new CredentialStore(retainedRoot, retainedSecrets);
        var older = new ProviderCredential { Id = "fixture-older", ProviderId = "openai", Secret = "fixture-old", CreatedAt = DateTimeOffset.UnixEpoch };
        var newer = new ProviderCredential { Id = "fixture-newer", ProviderId = "openai", Secret = "fixture-new", CreatedAt = DateTimeOffset.UnixEpoch.AddDays(1) };
        retainedStore.Save([newer, older]);
        using (var reopened = new ChatWorkspace(retainedRoot))
        {
            var selected = reopened.CredentialFor("openai");
            var savedCredentials = retainedStore.Load();
            Check(selected?.Id == older.Id && savedCredentials.Count == 2
                && savedCredentials.Any(credential => credential.Id == newer.Id && credential.Secret == "fixture-new")
                && savedCredentials.Any(credential => credential.Id == older.Id && credential.Secret == "fixture-old"),
                "启动保留旧版遗留的多条凭据与密钥，并选中最早的一条");
        }
        using (var signedOut = new ChatWorkspace(retainedRoot))
            Check(signedOut.RemoveCredential(older.Id) && signedOut.CredentialFor("openai") is null
                && retainedStore.Load().Any(credential => credential.Id == newer.Id && credential.Secret == "fixture-new"),
                "移除选中账号后不自动登录其他保存的账号，其他密钥仍在");
        retainedSecrets.Delete(older.Id);
        retainedSecrets.Delete(newer.Id);

        // macOS keeps every secret in one Keychain item and memoizes that bundle per store instance, so a second
        // instance in this process answers from its own copy. Anything asserting what is really stored — or
        // standing in for another process — has to ask a new instance: the copy one already holds is that
        // process's view of the store, not the store itself.
        ISecretStore Stored(string root) => AxmolHub.Agent.SecretStoreFactory.Create(root);

        var planLogoutRoot = Path.Combine(scratchRoot, "plan-logout");
        using (var setup = new ChatWorkspace(planLogoutRoot)) setup.AddPreset("openai");
        var planStore = new CredentialStore(planLogoutRoot, Stored(planLogoutRoot));
        var planTokens = new OpenAiTokens("fixture-access", "fixture-refresh", null, DateTimeOffset.UtcNow.AddHours(1), null)
        { Version = 1, ClientId = "fixture-client", HostId = "fixture-host", Issuer = "https://auth.openai.com", Subject = "fixture-subject", Scope = "chatgpt.tokens.use.direct" };
        var planCredential = new ProviderCredential { Id = "fixture-plan-logout", ProviderId = "openai", AuthKind = "openaiOAuth", Secret = planTokens.Encode() };
        var unrelatedCredential = new ProviderCredential { Id = "fixture-unrelated-removal", ProviderId = "deepseek", Secret = "fixture-other" };
        planStore.Save([planCredential, unrelatedCredential]);
        // Already rotated when this process opens, so one copy of the newest token is what it signs out with.
        var rotated = (planTokens with { RefreshToken = "fixture-rotated" }).Encode();
        Stored(planLogoutRoot).Write(planCredential.Id, rotated);
        using (var login = new ChatWorkspace(planLogoutRoot))
        {
            Check(login.HasPlanCredentials("openai") && !login.RemoveCredential(planCredential.Id)
                && !login.DisconnectProvider("openai") && Stored(planLogoutRoot).Read(planCredential.Id) == rotated,
                "通用凭据删除和断开不会绕过套餐账号的锁与撤销");
            Check(login.RemoveCredential(unrelatedCredential.Id) && Stored(planLogoutRoot).Read(planCredential.Id) == rotated,
                "删除其他凭据不会覆写别的进程刚轮换的套餐令牌");
        }
        using (var stale = new ChatWorkspace(planLogoutRoot))
        {
            var provider = stale.Providers.Single(candidate => candidate.Id == "openai");
            var selected = stale.CredentialFor("openai")!;
            // The snapshot a sign-in dialog captured before that rotation. Holding it, the save has to wait on
            // the lock and then refuse rather than overwrite a token this process no longer agrees with.
            var observed = planTokens.Encode();
            var refreshLock = await AxmolHub.Agent.OpenAiDynamicTokenSession.LockCredentialAsync(planLogoutRoot, selected.Id, default);
            var pendingLogin = stale.SavePlanCredentialForCheckAsync(provider, selected, planTokens, observed, planTokens);
            var waited = !pendingLogin.IsCompleted;
            await refreshLock.DisposeAsync();
            var rejected = false;
            try { await pendingLogin; }
            catch (InvalidOperationException) { rejected = true; }
            Check(waited && rejected && Stored(planLogoutRoot).Read(selected.Id) == rotated,
                "重新登录等待刷新锁并拒绝覆盖已轮换的令牌");
            Stored(planLogoutRoot).Delete(selected.Id);
            rejected = false;
            try { await stale.SavePlanCredentialForCheckAsync(provider, selected, planTokens, observed, planTokens); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && Stored(planLogoutRoot).Read(selected.Id) is null,
                "重新登录不会把已删除的凭据写回存储");
            Stored(planLogoutRoot).Write(selected.Id, rotated);
        }
        using (var logout = new ChatWorkspace(planLogoutRoot))
        {
            var stoppedBeforeRevoke = false;
            logout.RevokeOpenAiPlan = (tokens, _) =>
            {
                stoppedBeforeRevoke = tokens.RefreshToken == "fixture-rotated" && Stored(planLogoutRoot).Read(planCredential.Id) is null
                    && logout.CredentialFor("openai")?.HasSecret == false;
                return Task.FromResult(false);
            };
            var refreshLock = await AxmolHub.Agent.OpenAiDynamicTokenSession.LockCredentialAsync(planLogoutRoot, planCredential.Id, default);
            var pendingLogout = logout.DisconnectOpenAiPlanAsync();
            var waited = !pendingLogout.IsCompleted && Stored(planLogoutRoot).Read(planCredential.Id) is not null;
            await refreshLock.DisposeAsync();
            var outcome = await pendingLogout;
            Check(waited && outcome.Removed && !outcome.Revoked && stoppedBeforeRevoke
                && Stored(planLogoutRoot).Read(planCredential.Id) is null,
                "套餐退出等待刷新锁，清除本地令牌后撤销启动时读到的最新令牌");
        }
        using (var afterLogout = new ChatWorkspace(planLogoutRoot))
        {
            var retained = afterLogout.CredentialFor("openai");
            Check(retained?.HasSecret == false && retained.OpenAiRegistration?.ClientId == planTokens.ClientId
                && Stored(planLogoutRoot).Read(planCredential.Id) is null,
                "套餐退出后重启保留账号注册映射但不恢复令牌");
            var provider = afterLogout.Providers.Single(candidate => candidate.Id == "openai");
            var restored = await afterLogout.SavePlanCredentialForCheckAsync(provider, retained,
                retained!.OpenAiRegistration!.AuthorizationContext(), null, planTokens);
            Stored(planLogoutRoot).Write(restored.Id, "damaged-json");
        }
        using (var damaged = new ChatWorkspace(planLogoutRoot))
        {
            var provider = damaged.Providers.Single(candidate => candidate.Id == "openai");
            var outcome = await damaged.DisconnectOpenAiPlanAsync();
            Check(outcome.Removed && !outcome.Revoked && !provider.IsCallReady
                && damaged.CredentialFor("openai")?.OpenAiRegistration?.ClientId == planTokens.ClientId
                && Stored(planLogoutRoot).Read(planCredential.Id) is null,
                "损坏凭据可以安全清除并保留再次登录所需注册");
        }
        Stored(planLogoutRoot).Delete(planCredential.Id);
        Stored(planLogoutRoot).Delete(unrelatedCredential.Id);

        // ── Migrating a credential that predates registration ──
        // The old row cannot be converted, so it is kept as it is and the user is asked to sign in again. A
        // successful sign-in switches that same row over; a failed save must leave it, and that prompt, intact.
        var migrateRoot = Path.Combine(scratchRoot, "plan-migrate");
        using (var setup = new ChatWorkspace(migrateRoot)) setup.AddPreset("openai");
        var legacyTokens = new OpenAiTokens("fixture-legacy-access", "fixture-legacy-refresh", null, DateTimeOffset.UtcNow, "fixture-account");
        var legacyCredential = new ProviderCredential { Id = "fixture-legacy", ProviderId = "openai", AuthKind = "openaiOAuth", Secret = legacyTokens.Encode() };
        new CredentialStore(migrateRoot, Stored(migrateRoot)).Save([legacyCredential]);
        using (var stale = new ChatWorkspace(migrateRoot))
        {
            var provider = stale.Providers.Single(candidate => candidate.Id == "openai");
            var selected = stale.CredentialFor("openai")!;
            Check(provider.NeedsOpenAiRelogin && !provider.IsCallReady && selected.Secret == legacyTokens.Encode()
                && Stored(migrateRoot).Read(selected.Id) == legacyTokens.Encode(),
                "旧版 ChatGPT 凭据保留但提示重新登录，不会被静默删除");
            // Another process edits the metadata, so this save refuses — the same rollback path a failed write
            // takes. The legacy row, the selection and the sign-in prompt must be exactly as they were.
            var elsewhere = new CredentialStore(migrateRoot, Stored(migrateRoot)).Load();
            elsewhere.Single().Label = "changed elsewhere";
            new CredentialStore(migrateRoot, Stored(migrateRoot)).Save(elsewhere, writeSecrets: false);
            var refusedSave = false;
            try { await stale.SavePlanCredentialForCheckAsync(provider, selected, null, selected.Secret, planTokens); }
            catch (InvalidOperationException) { refusedSave = true; }
            Check(refusedSave && stale.CredentialFor("openai")?.Id == legacyCredential.Id
                && stale.CredentialFor("openai")?.Secret == legacyTokens.Encode()
                && provider.NeedsOpenAiRelogin && !provider.IsCallReady
                && Stored(migrateRoot).Read(selected.Id) == legacyTokens.Encode(),
                "套餐登录保存失败时保留旧凭据、原选择与重新登录提示（拒绝=" + refusedSave
                + " 需登录=" + provider.NeedsOpenAiRelogin + "）");
        }
        using (var migrate = new ChatWorkspace(migrateRoot))
        {
            var provider = migrate.Providers.Single(candidate => candidate.Id == "openai");
            var selected = migrate.CredentialFor("openai")!;
            var switched = await migrate.SavePlanCredentialForCheckAsync(provider, selected, null, selected.Secret, planTokens);
            Check(switched.Id == legacyCredential.Id && provider.IsOpenAiPlan && provider.IsCallReady
                && CountCredentials(migrate, "openai") == 1 && Stored(migrateRoot).Read(switched.Id) == planTokens.Encode(),
                "旧版凭据重新登录成功后原地切换为套餐凭据，不残留第二行");
        }
        Stored(migrateRoot).Delete(legacyCredential.Id);

        // ── The list shows no credential input, ever ──
        // This is the assertion the whole revision exists for. It is checked on every rendered group rather
        // than on one, because a single provider happening to be keyless would satisfy a check aimed at the
        // wrong provider — the input used to live per-provider, so the property to prove is universal.
        foreach (var group in settings.ProviderGroups)
        {
            Check(!group.HasCredentialField,
                "分组「" + group.Name + "」里没有任何密钥输入框（鉴权改由对话框承接）");
        }

        settings.RefreshProviderGroupsForCheck();
        var orcaGroup = settings.ProviderGroupForId("orcarouter");
        Check(orcaGroup is not null, "需要凭据的 provider 有自己的分组");
        // Both routes are declared for OrcaRouter, so the single button must offer authentication — and the
        // choice between them now happens inside the dialog, not on this row.
        Check(orcaGroup is not null && orcaGroup.AuthButtonText == HubStrings.Get("Authenticate"),
            "未鉴权时按钮文案是「鉴权」（实际「" + orcaGroup?.AuthButtonText + "」）");

        // The other half: a key-only provider gets the same single entry point, because the dialog is what
        // adapts to the manifest — the row itself is identical either way.
        var deepseekAdopted = shell.Chat.AddPreset("deepseek") is not null;
        Check(deepseekAdopted, "可以采纳 DeepSeek 预设以验证仅密钥路径");
        settings.RefreshProviderGroupsForCheck();
        Check(settings.ProviderWebToolControlsHiddenForCheck("deepseek"),
            "provider 分组不再显示独立搜索开关或后端选择");
        Check(shell.Chat.Providers.First(provider => provider.Id == "deepseek").WebToolsBackend == WebToolBackends.DeepSeekApi,
            "DeepSeek 预设仍自动使用官方 Search/Fetch API 后端");
        var deepseekGroup = settings.ProviderGroupForId("deepseek");
        Check(deepseekGroup is not null && deepseekGroup.AuthButtonText == HubStrings.Get("Authenticate"),
            "仅支持密钥的 provider 同样显示「鉴权」按钮，方式选择交给对话框");

        // The status mark is a *statement about the provider*, so it must be false before any credential
        // exists and true after one does. Asserting only the "linked" half would pass for a mark wired to
        // always-on, which is exactly the bug this pair exists to catch.
        Check(deepseekGroup is { IsLinked: false },
            "没有任何凭据的 provider 显示未鉴权（实际「" + deepseekGroup?.LinkedStatusText + "」）");

        // The model count lives in the model-section header (「模型 (N)」), not in a second summary line —
        // saying it twice is noise. The section header is what a name-only list now reads against.
        Check(deepseekGroup is not null && deepseekGroup.ShowsModelSection
              && settings.ProviderGroupForId("deepseek") is { HasModelListToggle: true },
            "模型数与列表入口在模型区标题上（「模型 (N)」+ 折叠），不再另起摘要行（实际摘要「"
                + deepseekGroup?.SummaryText + "」）");

        // The mark and its label must actually be *drawn*, not merely present in the tree. Both are looked up
        // during a rebuild that can happen while the page is still detached from the resource host, and a
        // brush resolved too early comes back null: the string is right, the tick is in the tree, and the
        // screen shows nothing at all. Only the resolved paint tells the two apart.
        Check(deepseekGroup is { StatusLabelIsPainted: true },
            "鉴权状态文字真的解析出了画刷（文字在树里不等于看得见）");
        Check(deepseekGroup is { StatusMarkIsPainted: true },
            "鉴权状态图标真的解析出了描边画刷（Path 在树里不等于画得出来）");

        // The removal rule is about the *default* provider, not about "built-in": OrcaRouter is the pinned
        // default, and everything else — preset or custom — is removable. DeepSeek is a preset that is not
        // the default, so it must offer the action.
        Check(deepseekGroup is { HasRemoveButton: true },
            "非默认的 provider（即便来自内置清单）同样可以移除");
        Check(orcaGroup is { HasRemoveButton: false },
            "默认 provider 不出现在移除目录里（清单会在下次启动时把它补回来）");

        // ── Models: a real sub-list, one marked in use ──
        //
        // A provider straight from the manifest arrives with **no** model, because the manifest no longer
        // names one. That is the change this whole block was rewritten for: a default model written into a
        // shipped JSON file goes stale the moment the provider adds or retires one, and a stale default is
        // worse than none — it is offered in the list and fails on first use with a 404 that blames the
        // user's key. The list comes from GET {baseUrl}/models instead (see CheckModelListAsync).
        //
        // So the sequence below seeds the first model the way the product now does — by adding one — and
        // asserts the mark from there. Asserting "the manifest seeds a default" would have been a check that
        // passes only while the manifest is wrong.
        // So the sequence below authenticates the provider first, then seeds the first model the way a user
        // would — by adding it — and asserts the mark from there. Asserting "the manifest seeds a default"
        // would have been a check that passes only while the manifest is wrong.
        //
        // The credential goes in *before* the model assertions, not after, because the model section is not
        // rendered at all for a provider with nothing to authenticate: the list is fetched with the user's
        // key, so before there is one there is nothing to show. That rule gets its own block below
        // (CheckModelListAsync); re-asserting it here would turn a hide-rule failure into a report about the
        // model list breaking, which sends the reader to the wrong file.
        shell.Chat.AddCredential("deepseek", "", "sk-deepseek-models", CredentialSources.ApiKey);
        settings.RefreshProviderGroupsForCheck();
        var afterAuth = settings.ProviderGroupForId("deepseek");
        Check(afterAuth is { ShowsModelSection: true, ModelNames.Length: 0 },
            "鉴权后、尚未拉取时模型区已就位但还没有模型行（实际 "
                + (afterAuth?.ModelNames.Length ?? -1) + " 行）");

        var added = shell.Chat.AddModel("deepseek", "deepseek-reasoner");
        Check(added is not null, "可以为 provider 添加第一个模型");
        Check(added is { InUse: true },
            "第一个添加的模型自动成为使用中的那个（实际 InUse=" + added?.InUse + "）");
        Check(shell.Chat.AddModel("deepseek", "DEEPSEEK-REASONER") is null,
            "重复添加同一模型被拒绝（大小写不敏感）");

        var secondModel = shell.Chat.AddModel("deepseek", "deepseek-chat")?.Name ?? "";
        Check(secondModel.Length > 0, "可以为同一 provider 添加第二个模型");

        settings.RefreshProviderGroupsForCheck();
        var withTwo = settings.ProviderGroupForId("deepseek");
        Check(withTwo is { ModelNames.Length: 2 }, "两个模型都渲染在子列表里（实际 "
            + (withTwo?.ModelNames.Length ?? -1) + " 个）");
        // Every row in the list is active and selectable — there is no enable toggle and no "in use" pill.
        // List membership is the activation set.
        Check(withTwo is { ModelEnabled.Length: 2 }
              && withTwo.ModelEnabled.All(enabled => enabled),
            "列表里的模型一律视为启用（实际 [" + string.Join(", ", withTwo?.ModelEnabled ?? []) + "]）");
        Check(withTwo is { HasUseModelButton: false },
            "模型行不再挂「使用此模型」入口（使用中由聊天选择器负责）");
        // The row states the window this model is treated as having, and nothing else. No editor: the box it
        // replaced parsed on every keystroke and the save behind it rebuilt every provider group, so the first
        // digit typed tore down the box being typed in. No source suffix either — that tag already rides the
        // chat's usage line, and repeating it here would state one thing twice. The prefix comes from the
        // resource because this suite runs in both languages, and a hard-coded "上下文" would pass in one and
        // silently never render in the other.
        var contextRow = settings.FirstModelContextRowForCheck();
        var contextSources = Enum.GetValues<CapabilitySource>()
            .Select(source => HubStrings.Get(ContextBudget.SourceLabelKey(source)))
            .Where(label => label.Length > 0)
            .ToArray();
        Check(contextRow.Text.StartsWith(HubStrings.Get("ModelContextFormat").Split('{')[0], StringComparison.Ordinal)
              && contextRow.Text.Any(char.IsDigit)
              && !contextRow.HasEditor,
            "模型行只读出这个模型被当作多大的窗口，行上不再挂任何可编辑框（实际「" + contextRow.Text + "」）");
        Check(contextRow.Text.Length > 0
              && !contextSources.Any(label => contextRow.Text.Contains(label, StringComparison.Ordinal)),
            "来源不再出现在行文本里，聊天用量行是它唯一的常驻陈述（实际「" + contextRow.Text + "」）");
        Check(contextRow.Tip.Length > 0
              && contextSources.Any(label => contextRow.Tip.Contains(label, StringComparison.Ordinal))
              && contextRow.Tip.Contains("providers.json", StringComparison.Ordinal),
            "来源与纠正路径一起收进 tooltip（实际「" + contextRow.Tip + "」）");
        // The enable toggle is gone: presence in the list is what makes a model selectable. These two
        // rows used to flip a ToggleSwitch and watch the chat picker follow; the picker now follows
        // list membership, which AddModel/RemoveModel already cover above and below.
        var firstModel = added?.Name ?? "";
        Check(firstModel.Length > 0, "读到了第一个添加的模型名");

        // Removing a model drops it from the list and from the chat picker together.
        Check(shell.Chat.RemoveModel("deepseek", "deepseek-reasoner"), "可以移除模型");
        settings.RefreshProviderGroupsForCheck();
        var afterRemoval = settings.ProviderGroupForId("deepseek");
        Check(afterRemoval is { ModelNames.Length: 1 }, "移除后子列表少一行（实际 "
            + (afterRemoval?.ModelNames.Length ?? -1) + " 个）");
        Check(afterRemoval is { ModelNames.Length: 1 }
              && afterRemoval.ModelNames[0] == secondModel
              && shell.Chat.AvailableChatModels.All(choice =>
                  choice.Provider.Id != "deepseek" || choice.ModelName != "deepseek-reasoner"),
            "移除的模型不再出现在列表里，也不再出现在聊天选择器里（实际 [" + string.Join(",", afterRemoval?.ModelNames ?? []) + "]）");

        // DeepSeek stays configured from here on: the disconnect check below needs a real provider to cut
        // loose, and adding a credential to a removed provider is a silent no-op that would make that check
        // fail for a reason that has nothing to do with disconnecting.

        // ── Credentials: one provider, one credential, however it was created ──
        //
        // The UI no longer renders a credential list, so "there is exactly one" is asserted on the data layer.
        // That is the invariant the whole one-to-one change rests on, and it is the kind of rule that rots
        // silently: a second AddCredential that appended instead of replacing would leave every screen looking
        // correct while the model underneath had quietly gone back to one-to-many.
        var label = "工作账号";
        var first = shell.Chat.AddCredential("orcarouter", label, "sk-test-account-1", CredentialSources.ApiKey);
        Check(first is not null, "可以为 provider 鉴权（密钥来源）");
        settings.RefreshProviderGroupsForCheck();
        var withOne = settings.ProviderGroupForId("orcarouter");
        Check(CountCredentials(shell.Chat, "orcarouter") == 1,
            "鉴权后该 provider 恰有一份凭据（实际 " + CountCredentials(shell.Chat, "orcarouter") + " 份）");
        Check(first is not null && first.Label == label,
            "凭据记下了用户填的备注（实际「" + first?.Label + "」）");

        // The mark flips on the first credential, whatever produced it — the whole point of collapsing key and
        // OAuth into one account model. Before/after are asserted together so a mark that is simply stuck on
        // cannot satisfy both.
        Check(withOne is { IsLinked: true },
            "有凭据后 provider 变为已鉴权（实际「" + withOne?.LinkedStatusText + "」）");
        Check(withOne is not null && withOne.LinkedStatusText == HubStrings.Get("ProviderConnected"),
            "已鉴权状态行用的就是「已鉴权」这个词，而不是「已连接」");
        // The one control, relabelled. Asserting the label rather than "a button appeared" is what makes this
        // a check of the state: a button that stayed on "authenticate" would otherwise pass unnoticed.
        Check(withOne is not null && withOne.AuthButtonText == HubStrings.Get("DisconnectProvider"),
            "已鉴权后同一个按钮变成「断开鉴权」（实际「" + withOne?.AuthButtonText + "」）");

        // An OAuth-produced credential is the same kind of thing, so it lands in the same slot — and replaces
        // the pasted key rather than joining it. This is the assertion that has teeth: under the old
        // one-to-many model this call appended, and the list grew to two rows.
        var oauthCredential = shell.Chat.AddOAuthCredential("orcarouter", "acct-77", "api", "sk-yoex-test");
        Check(oauthCredential is not null, "浏览器登录产出的凭据以同样的形式入库");
        settings.RefreshProviderGroupsForCheck();
        Check(CountCredentials(shell.Chat, "orcarouter") == 1,
            "两类来源共用同一个凭据槽位，而不是各占一行（实际 "
                + CountCredentials(shell.Chat, "orcarouter") + " 份）");
        Check(oauthCredential is not null && oauthCredential.Secret == "sk-yoex-test",
            "登录产出的凭据取代了先前粘贴的密钥");
        Check(settings.ProviderGroupForId("orcarouter") is { IsLinked: true },
            "换来源之后仍然是已鉴权，不需要用户再点一次");

        // 「需要凭据」 is a promise about *a credential existing*, not about a key being pasted — otherwise a
        // provider that offers browser sign-in would still refuse to work after a successful sign-in. Asserted on
        // a model added here rather than one the catalog happens to ship, so it cannot pass by accident, and
        // asserted while the OAuth credential is the only one in the slot.
        var oauthProbeModel = "oauth-only-probe";
        shell.Chat.AddModel("orcarouter", oauthProbeModel);
        var oauthProvider = shell.Chat.Providers.First(candidate => candidate.Id == "orcarouter");
        Check(oauthProvider.RequiresCredential && oauthProvider.Credential?.Source == CredentialSources.OAuth
              && shell.Chat.AvailableChatModels.Any(choice =>
                  choice.Provider.Id == "orcarouter" && choice.ModelName == oauthProbeModel),
            "只要鉴权过就能用它的模型，不必是粘贴的密钥（实际来源「"
            + (oauthProvider.Credential?.Source ?? "无凭据") + "」、可用模型 "
            + shell.Chat.AvailableChatModels.Count(choice => choice.Provider.Id == "orcarouter") + " 个）");
        shell.Chat.RemoveModel("orcarouter", oauthProbeModel);

        // Re-authenticating rotates the secret on the credential already there.
        var again = shell.Chat.AddOAuthCredential("orcarouter", "acct-77", "api", "sk-yoex-rotated");
        Check(again?.Id == oauthCredential?.Id, "同一 provider 再次登录是轮换密钥而不是新增一份");
        Check(again?.Secret == "sk-yoex-rotated", "轮换确实写入了新密钥（实际「" + again?.Secret + "」）");
        settings.RefreshProviderGroupsForCheck();
        Check(CountCredentials(shell.Chat, "orcarouter") == 1,
            "轮换后凭据数不变（实际 " + CountCredentials(shell.Chat, "orcarouter") + " 份）");

        // The one-to-one rule is not only about the OAuth path: pasting a second key over an authenticated
        // provider has to replace too, or the two entry points would disagree about what "one" means.
        var replaced = shell.Chat.AddCredential("orcarouter", "", "sk-pasted-over-oauth", CredentialSources.ApiKey);
        Check(replaced?.Id == oauthCredential?.Id, "粘贴新密钥替换的是同一份凭据，而不是叠一份新的");
        Check(CountCredentials(shell.Chat, "orcarouter") == 1,
            "两条鉴权路径交替使用后仍然只有一份凭据（实际 "
                + CountCredentials(shell.Chat, "orcarouter") + " 份）");

        // ── The sign-in flow itself, over an injected transport ──
        // The handler answers endpoint discovery, which is the request that proves the flow actually started. No
        // browser can be launched from a harness and no callback will ever arrive, so the run ends when the wait
        // expires; the handler's blanket refusal is what the exchange would meet if a code did land.
        settings.UseOAuthHandlerForCheck(new OAuthProbeHandler());
        await settings.RunOAuthFlowForCheckAsync();
        Check(settings.StatusLineText.Length > 0 && settings.StatusLineText != HubStrings.Get("AuthOAuthPending"),
            "登录失败时状态行给出了具体原因，而不是停在「正在等待授权…」（实际「"
                + settings.StatusLineText + "」）");
        // The harness cannot launch a browser, so the one honest ending is the wait running out. Asserting that
        // ending by name also pins the shape of the fallback: no browser gets a link in the status line, never an
        // input box to type a callback into.
        Check(settings.StatusLineText == HubStrings.Get("AuthOAuthCancelled"),
            "浏览器打不开时登录按时限收尾，回话只走状态行而不是弹输入框（实际「"
                + settings.StatusLineText + "」）");

        // Clean up so the later checks see the provider list they expect.
        var owned = shell.Chat.CredentialFor("orcarouter");
        if (owned is not null) shell.Chat.RemoveCredential(owned.Id);
        settings.RefreshProviderGroupsForCheck();
        Check(CountCredentials(shell.Chat, "orcarouter") == 0
              && settings.ProviderGroupForId("orcarouter") is { IsLinked: false },
            "移除凭据后该 provider 回到未鉴权（实际 " + CountCredentials(shell.Chat, "orcarouter") + " 份）");

        // ── GitHub Copilot: device-code login, from the manifest declaration to the code shown in the window ──
        //
        // The loopback flow above cannot be run to a landing from a harness: it ends when the wait expires, and
        // the assertion only proves a refusal was reported. A device code is the opposite shape — every request
        // is one this app makes itself — so the whole thing can be driven here: the code the provider issues, the
        // poll that waits for an approval that a scripted answer grants, and the window the person would have
        // read. No browser, no GitHub account, and no request that could spend anyone's Copilot quota.
        // The assertions below describe a *first* adoption, so the run starts from one. This data root is the
        // harness's sandbox, but the one state worth carrying across is a sign-in someone did for real: it is
        // recorded here and put back at the end, because a suite that quietly logs the developer out of Copilot
        // on every run would be the reason nobody dares run it against their own data.
        var priorCopilot = shell.Chat.CredentialFor("github-copilot");
        if (shell.Chat.Providers.Any(candidate => candidate.Id == "github-copilot"))
            shell.Chat.RemoveProvider("github-copilot");
        var copilot = shell.Chat.AddPreset("github-copilot");
        Check(copilot is not null, "清单里的 GitHub Copilot 预设可以被采纳");
        settings.RefreshProviderGroupsForCheck();
        Check(copilot is { SupportsOAuth: true }
              && copilot.OAuth?.EffectiveFlow == ProviderOAuthFlows.DeviceCode,
            "它声明的是设备码流程，页面也承认能浏览器登录（实际流程「"
            + (copilot?.OAuth?.EffectiveFlow ?? "无 oauth") + "」）");
        Check(settings.ProviderGroupForId("github-copilot") is { IsLinked: false } freshCopilot
              && freshCopilot.AuthButtonText == HubStrings.Get("Authenticate"),
            "刚采纳的 copilot 分组以未鉴权出现，动作写着「鉴权」");

        // Walk the "user denied it on the device" branch first: the window must still be built, and not a
        // single credential may be left behind.
        var refused = new DeviceCodeProbeHandler(
            """{"error":"access_denied","error_description":"the end user skipped the consent screen"}""");
        settings.UseOAuthHandlerForCheck(refused);
        await settings.RunDeviceCodeFlowForCheck("github-copilot");
        await WaitUntilPostedAsync(() => settings.DeviceWindowForCheck is not null);
        var refusedWindow = settings.DeviceWindowForCheck;
        Check(refusedWindow is not null, "设备码登录把授权窗口建了出来（实际 " + (refusedWindow is null ? "没有" : "有") + "）");
        Check(refusedWindow?.CodeForCheck == DeviceCodeProbeHandler.UserCode,
            "窗口上的代码就是服务端返回的那一串（实际「" + refusedWindow?.CodeForCheck + "」）");
        // Keeping the server's raw form on screen matches the page in the browser; what gets copied out is the
        // separator-stripped form — one stray '-' on paste is the kind of failure nobody would notice.
        Check(refusedWindow?.CopyTextForCheck == "ABCD1234"
              && refusedWindow?.CodeForCheck == "ABCD-1234",
            "复制的代码不含中间的分隔符，而屏上仍是原样（实际复制「" + refusedWindow?.CopyTextForCheck + "」）");
        Check(refusedWindow?.LinkForCheck == DeviceCodeProbeHandler.VerificationUri,
            "验证地址取自响应而不是清单里的字符串（实际「" + refusedWindow?.LinkForCheck + "」）");
        // The polling secret is a credential too, and it is not the thing a person types. Reading every text
        // block rather than the three named properties is what makes "it is not on screen" a fact.
        Check(refusedWindow is not null
              && !refusedWindow.TextsForCheck.Contains("dc-never-display-this", StringComparison.Ordinal),
            "屏上只有用户要输的代码，没有轮询用的 device_code");
        Check((refusedWindow?.LogicalChildCountForCheck ?? 0) >= 5
              && refusedWindow is not null && !refusedWindow.CopyRevealedForCheck,
            "窗口里真的摆了控件，复制按钮要悬停才亮（实际 "
            + (refusedWindow?.LogicalChildCountForCheck ?? 0) + " 个节点）");
        Check(refusedWindow?.StatusForCheck == HubStrings.Get("AuthDeviceWaiting"),
            "窗口先说一句在等什么（实际「" + refusedWindow?.StatusForCheck + "」）");
        Check(refused.Requests.Any(entry => entry.Contains("/login/device/code", StringComparison.Ordinal))
              && refused.Requests.All(entry => !entry.Contains("openid-configuration", StringComparison.Ordinal)),
            "登录走清单声明的设备码端点，一个 discovery 请求都没发（实际 "
            + string.Join(" → ", refused.Requests.ConvertAll(entry => entry.Split(' ')[1])) + "）");
        Check(refused.Requests.Count > 0
              && refused.Requests[0].Contains("\"client_id\":\"" + (copilot?.OAuth?.ClientId ?? "") + "\"")
              && refused.Requests[0].Contains("read:user"),
            "取码请求带的是清单里的公开 client_id 和 read:user（实际 " + refused.Requests.FirstOrDefault() + "）");
        Check(refused.Requests.All(entry => !entry.Contains("client_secret", StringComparison.Ordinal)),
            "客户端密钥一次都没有出现：这是公开客户端，密钥存在 GitHub 那边但不属于这个二进制");
        Check(CountCredentials(shell.Chat, "github-copilot") == 0, "被拒绝的登录不留任何凭据");
        Check(settings.StatusLineText.StartsWith(HubStrings.Get("AuthOAuthFailed"), StringComparison.Ordinal),
            "被拒绝时状态行说清是没登录成功，而不是停在等待中（实际「" + settings.StatusLineText + "」）");

        // Closing the window means cancelling. This branch must fire while the polling is still alive, so the
        // flow is started, the dispatcher pumped a few times, and only then is the window closed and reaped.
        var waiting = new DeviceCodeProbeHandler("""{"error":"authorization_pending"}""");
        settings.UseOAuthHandlerForCheck(waiting);
        var pending = settings.RunDeviceCodeFlowForCheck("github-copilot");
        await WaitUntilPostedAsync(() => settings.DeviceWindowForCheck is not null);

        Check(settings.CloseDeviceWindowForCheck(), "等待授权期间可以把这个窗口关掉");
        var ended = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(5))) == pending;
        Check(ended, "关掉窗口之后等待中的登录按时收尾，而不是留下一条没人听的轮询");
        Check(settings.StatusLineText == HubStrings.Get("AuthOAuthCancelled"),
            "关窗口这条路径的结论走状态行（实际「" + settings.StatusLineText + "」）");
        Check(CountCredentials(shell.Chat, "github-copilot") == 0, "取消的登录同样不留凭据");
        Check(waiting.Requests.Count(entry => entry.Contains("/login/oauth/access_token", StringComparison.Ordinal)) >= 1
              && waiting.Requests.Any(entry => entry.Contains("device_code", StringComparison.Ordinal)),
            "轮询真的开始了，而且带着取码时拿到的 device_code（实际 " + waiting.Requests.Count + " 个请求）");

        // The approved run: same flow, but this time the server hands back a token, so the credential and the
        // model list land together.
        var approved = new DeviceCodeProbeHandler(
            """{"access_token":"ghu-copilot-probe","token_type":"bearer","scope":"read:user"}""");
        // gpt-6-luna is not decoration: adoption keeps a reported row only when it is already enabled or named in
        // the preset's defaultEnabledModels (ChatWorkspace's intersection), and GitHub's chat default is
        // manifest-retuned — this fixture went stale once when that name moved off gpt-5-mini. Retune the preset
        // and retune this body with it.
        var catalog = new ModelListProbeHandler(
            """{"data":[{"id":"gpt-6-luna","model_picker_enabled":true,"is_chat_default":true,"policy":{"state":"enabled"},"supported_endpoints":["/chat/completions","/responses"],"capabilities":{"type":"chat","limits":{"max_context_window_tokens":264000},"supports":{"vision":true,"reasoning_effort":["low","medium","high"]}}},{"id":"exec-agent-a","model_picker_enabled":false,"policy":{"state":"enabled"},"supported_endpoints":["/chat/completions"],"capabilities":{"type":"chat","limits":{"max_context_window_tokens":128000}}},{"id":"text-embedding-3-small","model_picker_enabled":false,"policy":{"state":"disabled"},"capabilities":{"type":"embeddings"}},{"id":"gpt-4o","model_picker_enabled":false,"policy":{"state":"enabled"},"capabilities":{"type":"chat","limits":{"max_context_window_tokens":128000}}}]}""");
        settings.UseOAuthHandlerForCheck(approved);
        settings.UseModelListHandlerForCheck(catalog);
        await settings.RunDeviceCodeFlowForCheck("github-copilot");
        var landed = shell.Chat.CredentialFor("github-copilot");
        Check(landed is { Source: CredentialSources.OAuth } && landed.Secret == "ghu-copilot-probe",
            "设备码批准后落库的是一份 OAuth 凭据（实际来源「" + (landed?.Source ?? "无凭据") + "」）");
        settings.RefreshProviderGroupsForCheck();
        var linkedCopilot = settings.ProviderGroupForId("github-copilot");
        Check(linkedCopilot is { IsLinked: true } && linkedCopilot.AuthButtonText == HubStrings.Get("DisconnectProvider"),
            "登录成功之后分组显示已鉴权，同一个按钮变成「断开鉴权」（实际「"
            + linkedCopilot?.AuthButtonText + "」）");
        Check(catalog.Requests.Count == 1
              && catalog.Requests[0].Headers.Authorization?.Parameter == "ghu-copilot-probe",
            "登录收尾时刷新的模型清单用的就是刚拿到的令牌（实际 " + catalog.Requests.Count + " 次请求）");
        Check(catalog.Requests.Count > 0
              && catalog.Requests[0].Headers.TryGetValues("x-github-api-version", out var apiVersions)
              && apiVersions.Contains("2026-06-01"),
            "清单声明的 x-github-api-version 真的跟着请求发出去了");
        var offeredModels = shell.Chat.Providers
            .First(candidate => candidate.Id == "github-copilot").Models
            .ConvertAll(model => model.Name);
        Check(offeredModels.Contains("gpt-6-luna") && !offeredModels.Contains("exec-agent-a")
              && !offeredModels.Contains("gpt-4o") && !offeredModels.Contains("text-embedding-3-small"),
            "只采纳目录里真的能挑的那一行，其余三类各自被 capability type、picker 与 policy 挡掉（实际 ["
            + string.Join(",", offeredModels) + "]）");
        Check(AxmolHub.Agent.ChatClientFactory.ProtocolFor(
                  shell.Chat.Providers.First(candidate => candidate.Id == "github-copilot"), "gpt-6-luna")
              == ModelProtocols.Chat,
            "gpt-6-luna 同时给了两个端点，取 chat 而不是 responses");

        // Leave the provider list the way the checks after this one expect it.
        if (landed is not null) shell.Chat.RemoveCredential(landed.Id);
        Check(shell.Chat.RemoveProvider("github-copilot"), "验证用的 copilot provider 可以移除");
        settings.RefreshProviderGroupsForCheck();
        Check(settings.ProviderGroupForId("github-copilot") is null, "移除后分组从页面消失");
        if (priorCopilot?.Secret is { Length: > 0 } realToken)
        {
            // Restore the login that really existed before this run. The scripted token is only an assertion
            // prop; the human's login is the single valuable credential on this machine — a self-check must
            // not lose it.
            shell.Chat.AddPreset("github-copilot");
            shell.Chat.AddOAuthCredential(
                "github-copilot", priorCopilot.AccountId, priorCopilot.Scope, realToken);
            settings.RefreshProviderGroupsForCheck();
            Check(settings.ProviderGroupForId("github-copilot") is { IsLinked: true },
                "跑完设备码断言之后，先前真实存在的登录又回来了");
        }

        // ── Disconnect versus remove: the two intentions stay separate ──
        // Disconnect is "revoke on this machine": it clears the credential and the mark, and leaves the
        // provider itself alone. Asserting the *group* survives is the half that catches an implementation
        // which quietly calls remove instead — the two would look identical from the credential list.
        shell.Chat.AddCredential("deepseek", "临时账号", "sk-disconnect-probe", CredentialSources.ApiKey);
        settings.RefreshProviderGroupsForCheck();
        Check(settings.ProviderGroupForId("deepseek") is { IsLinked: true }, "断开前的 provider 已鉴权");
        Check(shell.Chat.DisconnectProvider("deepseek"), "可以断开一个已鉴权的 provider");
        settings.RefreshProviderGroupsForCheck();
        Check(CountCredentials(shell.Chat, "deepseek") == 0, "断开后本机不再保存任何凭据");
        Check(settings.ProviderGroupForId("deepseek") is { IsLinked: false },
            "断开后回到未鉴权（实际「" + settings.ProviderGroupForId("deepseek")?.LinkedStatusText + "」）");
        Check(settings.ProviderGroupForId("deepseek") is not null,
            "断开只清除凭据，provider 本身与它的分组都保留");
        // The same control flips back rather than disappearing: it is the provider's only entry point, and a
        // row whose right-hand side went empty would be harder to read than one that says "authenticate".
        Check(settings.ProviderGroupForId("deepseek") is { AuthButtonText: var back }
              && back == HubStrings.Get("Authenticate"),
            "断开之后按钮变回「鉴权」（实际「" + settings.ProviderGroupForId("deepseek")?.AuthButtonText + "」）");
        Check(shell.Chat.DisconnectProvider("deepseek") == false,
            "对未鉴权的 provider 再次断开是空操作，返回 false 而不是假装成功");

        // ── A "credential record with no key" must not leave the page claiming 已鉴权 while the session cannot send ──
        // The composer offers a provider when it holds a credential; the client factory refuses to build a client
        // unless that credential holds a secret. Two rules with a state between them: a record whose secret is not
        // there — a blank submit in the dialog, or a stored entry that no longer resolves — leaves the row saying
        // 「已鉴权」 while every send in the session answers 「请先在设置中鉴权」. The invariant is one sentence:
        // nothing the picker offers may be something the factory would reject.
        var zombie = shell.Chat.AddCredential("deepseek", "空密钥", "", CredentialSources.ApiKey);
        settings.RefreshProviderGroupsForCheck();
        var zombieProvider = shell.Chat.Providers.First(provider => provider.Id == "deepseek");
        var zombieOffered = shell.Chat.AvailableChatModels.Any(choice => choice.Provider.Id == "deepseek");
        string? zombieRefusal = null;
        try
        {
            AxmolHub.Agent.ChatClientFactory.Create(zombieProvider, zombieProvider.Model);
        }
        catch (Exception exception)
        {
            zombieRefusal = exception.Message;
        }

        Check(zombie is null, "需要凭据的 provider 拒绝一份没有密钥的「鉴权」，而不是记下一条看起来已配好的记录");
        Check(!zombieOffered || zombieRefusal is null,
            "选择器给出的 provider，工厂必须建得出客户端（实际：" + (zombieRefusal ?? "可以建出") + "）");
        Check(zombieOffered == false && settings.ProviderGroupForId("deepseek") is { IsLinked: false },
            "记录在、密钥不在时，provider 行不再宣称已鉴权（实际「"
            + settings.ProviderGroupForId("deepseek")?.LinkedStatusText + "」）");
        shell.Chat.RemoveCredential(zombie?.Id ?? "");

        // Cleanup: the removal rule is "the default provider is protected, everything else is not" — not
        // "built-ins are protected". OrcaRouter is the pinned default, so it is the one that must refuse.
        Check(shell.Chat.RemoveProvider("orcarouter") == false, "默认 provider 不能被移除，它的分组也随之保留");
        settings.RefreshProviderGroupsForCheck();
        Check(settings.ProviderGroupForId("orcarouter") is not null, "被拒绝移除的默认 provider 分组仍在页面上");

        var customId = shell.Chat.AddProvider("Custom Test", "https://example.test/v1", "test-model", null, out _)?.Id;
        Check(customId is not null, "可以添加自定义 provider 以验证移除路径");
        settings.RefreshProviderGroupsForCheck();
        Check(settings.ProviderGroupForId(customId ?? "\u0000") is { HasRemoveButton: true },
            "自定义 provider 的分组带移除按钮");
        Check(shell.Chat.RemoveProvider(customId ?? ""), "自定义 provider 可以移除");
        settings.RefreshProviderGroupsForCheck();
        Check(settings.ProviderGroupForId(customId ?? "\u0000") is null, "移除 provider 后它的分组也从页面消失");

        // A removed *preset* is not gone for good: it drops out of the configured list and reappears in the
        // catalogue, so the same id can be adopted again. Without this, "remove" on the default-neighbouring
        // presets would read as a one-way door.
        Check(shell.Chat.RemoveProvider("deepseek"), "非默认的内置预设可以移除");
        settings.RefreshProviderGroupsForCheck();
        Check(settings.ProviderGroupForId("deepseek") is null, "移除后预设的分组从配置列表消失");
        Check(shell.Chat.AvailablePresets().Any(preset => preset.Id == "deepseek"),
            "移除的预设回到「添加提供商」目录里，可以再加回来");
        Check(shell.Chat.AddPreset("deepseek") is not null, "移除的预设可以重新采纳");
        settings.RefreshProviderGroupsForCheck();
        Check(settings.ProviderGroupForId("deepseek") is not null, "重新采纳后分组重新出现");

        CheckAuthDialogSteps(shell.Chat.Providers);
        await CheckModelListAsync(scratchRoot, settings, shell);
        await Task.CompletedTask;
    }

    internal async Task CheckModelListAsync(string scratchRoot, SettingsPage settings, MainWindow shell)
    {
        // ── The parser, on the shapes that matter ──
        Check(ModelList.Parse(System.Text.Encoding.UTF8.GetBytes(
                """{"object":"list","data":[{"id":"gpt-5"},{"id":"gpt-5-mini"},{"id":"gpt-5"}]}"""))
                is ["gpt-5", "gpt-5-mini"],
            "解析 data[].id，并按大小写不敏感去重保序（实际 ["
                + string.Join(",", ModelList.Parse(System.Text.Encoding.UTF8.GetBytes(
                    """{"object":"list","data":[{"id":"gpt-5"},{"id":"gpt-5-mini"},{"id":"gpt-5"}]}"""))) + "]）");
        Check(ModelList.Parse(System.Text.Encoding.UTF8.GetBytes("""{"data":[{"id":" GPT-5 "}]}""")) is ["GPT-5"],
            "模型名两端的空白被裁掉");
        // A gateway is free to attach whatever it likes to each entry. A whole-object binding would make an
        // unexpected extra field a hard failure, so only "id" is read and anything else is ignored.
        Check(ModelList.Parse(System.Text.Encoding.UTF8.GetBytes(
                """{"object":"list","data":[{"id":"m","owned_by":"me","permission":[],"extra":{"x":1}}]}""")) is ["m"],
            "只读 data[].id，多余字段不影响解析");
        // A built-in preset storing null in the archive no longer wipes the manifest-declared window. This
        // chain was the root cause of "every model runs at 8192": the file stored null, load never backfilled
        // the manifest value, and every `?? 8192` fell through to that 2023-era number.
        //
        // The number below is the 2026-10-13 retune's, and it happens to equal the fallback — which is why the
        // second cell also demands IsDeclared. A window that reads 256,000 because the preset said so and one
        // that reads it because nobody said anything are the same integer and opposite facts.
        var presetProvider = shell.Chat.Providers.FirstOrDefault(
            candidate => candidate.Id == "orcarouter");
        Check(presetProvider?.MaxContextTokens == 256000,
            "内置预设声明的窗口不再被存档里的 null 抹掉（实际 "
            + (presetProvider?.MaxContextTokens?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "空") + "）");
        var presetWindow = ContextBudget.For(presetProvider, presetProvider?.Model);
        Check(presetWindow.Tokens == 256000 && presetWindow.IsDeclared
              && presetWindow.ConversationRoom < presetWindow.Tokens - ContextBudget.MinimumOutputReserveTokens,
            "默认网关按它自己声明的窗口计，并先留出回答的那一份（窗口 "
            + presetWindow.Tokens.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)
            + "，可谈 " + presetWindow.ConversationRoom.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) + "）");

        var overrideModel = presetProvider?.Models.FirstOrDefault();
        if (overrideModel is not null)
        {
            var beforeOverride = ContextBudget.For(presetProvider, overrideModel.Name).Tokens;
            Check(shell.Chat.SetModelContextTokens("orcarouter", overrideModel.Name, 4096)
                  && ContextBudget.For(presetProvider, overrideModel.Name).Tokens == 4096
                  && ContextBudget.For(presetProvider, overrideModel.Name).Source == CapabilitySource.UserOverride,
                "手填的窗口覆盖掉网关与预设的数字（覆盖前 "
                + beforeOverride.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) + "）");
            // And back out of it: an override nobody can take away is a trap, and leaving one set would change
            // the fixture for every assertion that runs after this one.
            Check(shell.Chat.SetModelContextTokens("orcarouter", overrideModel.Name, null)
                  && ContextBudget.For(presetProvider, overrideModel.Name).Tokens == beforeOverride,
                "覆盖可以撤销，回到覆盖前那个数字");
        }

        var effortMetadata = ModelList.ParseMetadata(System.Text.Encoding.UTF8.GetBytes(
            """{"object":"list","data":[{"id":"deepseek-flash","effort":{"supported_levels":["low","high","max"],"default_level":"high"}}]}"""));
        Check(effortMetadata.Models is ["deepseek-flash"]
              && effortMetadata.ReasoningModels["deepseek-flash"].Efforts is ["low", "high", "max"]
              && effortMetadata.ReasoningModels["deepseek-flash"].DefaultEffort == "high",
            "解析 DeepSeek /models 返回的推理档位与默认档位");
        // An HTML error page from a proxy is a 200 with no models in it. That is "this provider will not tell
        // us", not "we could not ask" — and the two lead to different user advice.
        Check(ModelList.Parse(System.Text.Encoding.UTF8.GetBytes("<html>gateway</html>")).Count == 0,
            "非协议形状的响应体解析为空列表而不是报错");
        Check(ModelList.Parse(System.Text.Encoding.UTF8.GetBytes("""{"data":[{"object":"model"}]}""")).Count == 0,
            "缺少 id 的条目被跳过，不产生空行");
        Check(ModelList.Parse(System.Text.Encoding.UTF8.GetBytes("""{"object":"list","data":[]}""")).Count == 0,
            "空 data 数组是合法答案（这就是「该提供商没有模型」）");

        // ── The model section is always rendered ──
        //
        // List membership is the activation set. An unauthenticated provider must still show the section —
        // otherwise a freshly added Xiaomi MiMo has nowhere to put a model name, which is the bug this
        // revision fixes. Catalog fetch stays gated; the section itself does not.
        var unauthenticated = shell.Chat.Providers.FirstOrDefault(provider =>
            provider.RequiresCredential && shell.Chat.CredentialFor(provider.Id) is null);
        Check(unauthenticated is not null, "存在未鉴权且需要凭据的 provider，用它检查模型区始终可见");
        // The empty-state assertion needs a provider with no local models; a leftover row would turn it into
        // a different check. Clear first — removal is local-list only.
        if (unauthenticated is not null)
        {
            foreach (var leftover in unauthenticated.Models.ToArray())
            {
                shell.Chat.RemoveModel(unauthenticated.Id, leftover.Name);
            }
        }

        settings.RefreshProviderGroupsForCheck();
        var unauthGroup = unauthenticated is null ? null : settings.ProviderGroupForId(unauthenticated.Id);
        Check(unauthenticated is not null && unauthGroup is { ShowsModelSection: true },
            "未鉴权的 provider 仍然渲染模型区（实际 "
                + (unauthGroup is { ShowsModelSection: true } ? "仍然渲染" : "未渲染") + "）");
        Check(unauthenticated is not null && unauthGroup is { ModelNames.Length: 0, ShowsModelEmptyState: true },
            "未鉴权且零模型时给空态，而不是整块藏起来（实际 "
                + (unauthGroup?.ModelNames.Length ?? -1) + " 行）");
        Check(unauthenticated is not null && unauthGroup is { HasAddModelButton: true },
            "未鉴权时手动添加模型仍然可用（本地列表不依赖密钥）");
        // No extra CTAs in the empty state or the header: auth success fetches the catalog by itself, and
        // method change is disconnect → authenticate. A second entry would be noise next to 鉴权.
        Check(unauthGroup is { HasChangeAuthMethodButton: false },
            "分组没有「更换方式」入口（换方式 = 断开后再鉴权）");
        Check(unauthGroup is { EmptyStateButtonCount: 0 },
            "未鉴权空态不放鉴权/添加 CTA（头部鉴权与工具栏已各有一份，实际 "
                + (unauthGroup?.EmptyStateButtonCount ?? -1) + " 个按钮）");

        // A keyless provider is the exemption that used to justify the hide rule: Ollama never has a
        // credential, so a "hide until linked" rule made its models unreachable forever.
        var keyless = shell.Chat.Providers.FirstOrDefault(provider =>
            !provider.IsCustom && !provider.RequiresCredential);
        Check(keyless is not null, "存在无需凭据的 provider（Ollama），用它检查豁免规则");
        settings.RefreshProviderGroupsForCheck();
        var keylessGroup = keyless is null ? null : settings.ProviderGroupForId(keyless.Id);
        Check(keyless is not null && keylessGroup is { ShowsModelSection: true },
            "无需鉴权的 provider 仍然渲染模型列表（豁免生效）");

        // ── A successful fetch adopts the list and renders it ──
        var target = keyless ?? unauthenticated;
        Check(target is not null, "有一个 provider 可以用来跑拉取路径");
        if (target is null) return;

        // The manifest gives a small, exact default-enabled set. The fetched response remains authoritative
        // about existence, and a later refresh must not undo a user's explicit toggle.
        var recommendedModel = AiProviderManifest.Find("deepseek")?.DefaultEnabledModels.FirstOrDefault()
                               ?? "missing-manifest-default";
        var defaultsResponse = System.Text.Json.JsonSerializer.Serialize(new
        {
            data = new[]
            {
                new
                {
                    id = recommendedModel,
                    effort = new
                    {
                        supported_levels = new[] { "low", "high", "max" },
                        default_level = (string?)"high",
                    },
                },
                new
                {
                    id = "unlisted-manifest-probe",
                    effort = new
                    {
                        supported_levels = Array.Empty<string>(),
                        default_level = (string?)null,
                    },
                },
            },
        });
        var defaultsRoot = Path.Combine(scratchRoot, "manifest-defaults-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(defaultsRoot);
        using (var defaultsWorkspace = new ChatWorkspace(defaultsRoot))
        using (var defaultsHttp = new System.Net.Http.HttpClient(new ModelListProbeHandler(
                   defaultsResponse)))
        {
            var defaultProvider = defaultsWorkspace.AddPreset("deepseek");
            var credentialAdded = defaultsWorkspace.AddCredential(
                "deepseek", "", "sk-manifest-model-probe", CredentialSources.ApiKey);
            defaultsWorkspace.ModelListHttp = defaultsHttp;
            var defaultsResult = await defaultsWorkspace.RefreshModelsAsync("deepseek");
            var configured = defaultsWorkspace.Providers.First(provider => provider.Id == "deepseek");
            var enabledDefault = configured.Models.FirstOrDefault(model => model.Name == recommendedModel);
            var reasoning = ModelCatalog.ReasoningFor(configured, recommendedModel);
            Check(defaultProvider is not null && defaultsResult.Reachable
                  && defaultProvider.DefaultEnabledModels.Contains(recommendedModel)
                  && enabledDefault is { Enabled: true }
                  && reasoning is { DefaultEffort: "high" }
                  && reasoning.Efforts.SequenceEqual(["low", "high", "max"])
                  && !configured.Models.Any(model => model.Name == "unlisted-manifest-probe"),
                "manifest 中的 defaultEnabledModels 只启用与目录匹配的模型（defaults ["
                    + string.Join(",", defaultProvider?.DefaultEnabledModels ?? [])
                    + "] credential=" + (credentialAdded is not null)
                    + " fetch=" + defaultsResult.Reachable + " problem=" + defaultsResult.Problem
                    + " configured=[" + string.Join(",", configured.Models.Select(model => model.Name + ":" + model.Enabled))
                    + "]）");
            var cachedEffort = new ModelListStore(defaultsRoot).Load().Single()
                .ReasoningModels.GetValueOrDefault(recommendedModel);
            Check(cachedEffort is { DefaultEffort: "high" }
                  && cachedEffort.Efforts.SequenceEqual(["low", "high", "max"]),
                "刷新模型列表时将推理档位元数据一并写入本地缓存");
            Check(credentialAdded is not null
                  && defaultsWorkspace.SetModelEnabled("deepseek", recommendedModel, false),
                "用户可以关闭 manifest 默认启用的模型");
            await defaultsWorkspace.RefreshModelsAsync("deepseek");
            configured = defaultsWorkspace.Providers.First(provider => provider.Id == "deepseek");
            enabledDefault = configured.Models.FirstOrDefault(model => model.Name == recommendedModel);
            Check(enabledDefault is { Enabled: false }
                  && !configured.Models.Any(model => model.Name == "unlisted-manifest-probe"),
                "后续目录刷新保留用户已关闭的状态且新模型仍默认关闭");
        }
        // ── The key probe carries the provider's declared static headers ──
        //
        // The probe is the one request whose 401 is read as "the key was rejected" — the verdict that blocks the auth
        // dialog — and a gateway that filters clients answers a headerless probe that way for a perfectly good key.
        // So it sends the same headers inference does. Asserted here rather than in --check-ai-providers because the
        // probe lives in this assembly and the checks project references only Core and Agent; the catalog half of
        // the same rule is asserted there.
        var probeRoot = Path.Combine(scratchRoot, "probe-headers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probeRoot);
        var probeHandler = new ModelListProbeHandler();
        using (var probeHttp = new System.Net.Http.HttpClient(probeHandler))
        using (var probeWorkspace = new ChatWorkspace(probeRoot))
        {
            var probePreset = probeWorkspace.AddPreset("agentrouter");
            probeWorkspace.OAuthHttp = probeHttp;
            var probeOutcome = await probeWorkspace.CheckApiKeyAsync("agentrouter", "sk-probe");
            var probeUserAgent = probeHandler.Requests
                .Select(sent => sent.Headers.TryGetValues("user-agent", out var values) ? string.Join(", ", values) : null)
                .FirstOrDefault();
            Check(probePreset is not null
                  && probeOutcome == KeyCheckOutcome.Accepted
                  && probeUserAgent == "codex_cli_rs/0.101.0",
                "密钥探测带上 provider 声明的静态头（outcome=" + probeOutcome + " user-agent=「"
                    + (probeUserAgent ?? "无") + "」）");
        }
        // ── A catalog refresh must not move a declared endpoint off its wire ──
        //
        // This is the regression that made the wire an endpoint-wide claim. The catalog publishes endpoint *families*
        // ("openai", "anthropic") beside the occasional real wire name, and reading those as wires put a working
        // /v1/responses gateway back onto chat — the 400 that earns says "please use /v1/responses", which reads as a
        // model problem rather than as Hub's routing. Asserted here rather than in --check-ai-providers because the
        // refresh path and the provider it produces live in this assembly.
        var wireMergeRoot = Path.Combine(scratchRoot, "endpoint-wire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(wireMergeRoot);
        var wireMergeHandler = new ModelListProbeHandler(
            """{"object":"list","data":[{"id":"gpt-6.1-sol","context_length":400000,"supported_endpoint_types":["openai"]},{"id":"gpt-5.6-sol","supported_endpoint_types":["chat"]}]}""");
        using (var wireMergeHttp = new System.Net.Http.HttpClient(wireMergeHandler))
        using (var wireMergeWorkspace = new ChatWorkspace(wireMergeRoot))
        {
            wireMergeWorkspace.AddPreset("agentrouter");
            wireMergeWorkspace.AddCredential("agentrouter", "", "sk-wire-merge", CredentialSources.ApiKey);
            wireMergeWorkspace.ModelListHttp = wireMergeHttp;
            await wireMergeWorkspace.RefreshModelsAsync("agentrouter");
            var mergedProvider = wireMergeWorkspace.Providers.First(provider => provider.Id == "agentrouter");
            var namedWire = AxmolHub.Agent.ChatClientFactory.ProtocolFor(mergedProvider, "gpt-6.1-sol");
            var addedWire = AxmolHub.Agent.ChatClientFactory.ProtocolFor(mergedProvider, "gpt-5.6-sol");
            var mergedWindow = ContextBudget.CapabilitiesOf(mergedProvider, "gpt-6.1-sol")?.ContextTokens;
            Check(namedWire == ModelProtocols.Responses && addedWire == ModelProtocols.Responses && mergedWindow == 400000,
                "目录发布端点家族名与窗口后，提供商声明的请求线路不变，目录新加的模型也走同一条（gpt-6.1-sol="
                    + namedWire + " gpt-5.6-sol=" + addedWire
                    + " window=" + (mergedWindow?.ToString(CultureInfo.InvariantCulture) ?? "无") + "）");
        }
        // ── What a fresh provider looks like before anything is fetched ──
        //
        // This is the state a user meets on a freshly adopted preset, and it is the one that has no model at
        // all. Catalog browsing and manual addition are actions on the model section.
        settings.RefreshProviderGroupsForCheck();
        var beforeFetch = settings.ProviderGroupForId(target.Id);
        Check(beforeFetch is { ShowsModelSection: true, ShowsModelEmptyState: true },
            "尚未启用模型时显示模型区空状态（实际「" + (beforeFetch is { ShowsModelEmptyState: true } ? "有" : "无") + "」）");
        Check(beforeFetch is
              {
                  HasModelCatalogButton: true,
                  HasAddModelButton: true,
                  ModelCatalogPrecedesAdd: true,
                  HasRemoveAllModelsButton: true,
                  RemoveAllFollowsAdd: true,
                  HasProviderOutline: true,
                  HasProviderSurface: true,
                  HasProviderHeaderDivider: true,
                  HasRefreshButton: false
              },
            "provider 卡片有边界和标题分隔线，目录、添加、移除全部按顺序位于模型工具栏");
        Check(settings.ModelCatalogForCheck(target.Id) is { HasRefreshButtonForCheck: true },
            "模型目录弹窗提供刷新按钮");

        // ── The request itself, on a keyless provider ──
        //
        // Asserted on what was actually sent, because nothing about the *list* can catch a header that should
        // not have been added: an endpoint that ignores Authorization answers identically either way, and a
        // header carrying a null token reads as "Bearer " with an empty value — which some gateways reject.
        var keylessProbe = new ModelListProbeHandler("""{"object":"list","data":[]}""");
        settings.UseModelListHandlerForCheck(keylessProbe);
        await settings.RefreshModelsForCheckAsync(target.Id);

        Check(keylessProbe.Requests.Count > 0,
            "确实发出了模型列表请求（实际 " + keylessProbe.Requests.Count + " 次）");
        Check(keylessProbe.Requests.All(request => request.Headers.Authorization is null),
            "无凭据的 provider 不发送 Authorization 头（实际 ["
                + string.Join(",", keylessProbe.Requests.Select(request => request.Headers.Authorization?.ToString() ?? "null")) + "]）");
        // The URL is the provider's own base URL + /models. Trailing-slash tolerance is checked through the
        // request itself rather than by reading the URL-building expression: a base written as ".../v1/" is
        // legitimate user input, and "//models" 404s on a provider that otherwise works.
        // Ollama also asks its own native metadata endpoints; those must stay on the same origin.
        var catalogUri = new Uri(target.BaseUrl.TrimEnd('/') + "/models");
        Check(keylessProbe.Requests.Count(request => request.RequestUri == catalogUri) == 1
              && keylessProbe.Requests.All(request => request.RequestUri?.GetLeftPart(UriPartial.Authority)
                  == catalogUri.GetLeftPart(UriPartial.Authority)),
            "请求打到该 provider 自己的 {baseUrl}/models（实际 "
                + keylessProbe.Requests.FirstOrDefault()?.RequestUri + "）");

        // ── A successful fetch caches the full catalog without activating every model ──
        settings.UseModelListHandlerForCheck(new ModelListProbeHandler(
            """{"object":"list","data":[{"id":"llama3.2"},{"id":"qwen3"}]}"""));
        await settings.RefreshModelsForCheckAsync(target.Id);
        Check(settings.StatusLineText.Length > 0 && !settings.StatusLineText.Contains("正在拉取"),
            "拉取结束后状态行给出了结果（实际「" + settings.StatusLineText + "」）");

        settings.RefreshProviderGroupsForCheck();
        var fetched = settings.ProviderGroupForId(target.Id);
        Check(fetched is { ShowsModelEmptyState: true, ModelNames.Length: 0 },
            "拉取目录不会默认激活未列入 manifest 的模型");
        Check(shell.Chat.CachedModels(target.Id).Contains("llama3.2")
              && shell.Chat.CachedModels(target.Id).Contains("qwen3"),
            "完整模型列表写入目录缓存（实际 [" + string.Join(",", shell.Chat.CachedModels(target.Id)) + "]）");
        Check(fetched is { HasModelListToggle: true, ModelListExpanded: true, HasUseModelButton: false },
            "模型区可折叠，短列表默认展开且不再显示重复的「使用此模型」按钮");

        var catalog = settings.ModelCatalogForCheck(target.Id);
        Check(catalog is not null, "可以打开已缓存的模型目录");
        if (catalog is not null)
        {
            Check(catalog.UsesVirtualizingPanelForCheck,
                "模型目录使用虚拟化列表面板，滚动时只创建可见项");
            var countStates = new List<string> { catalog.CountTextForCheck };
            catalog.SearchForCheck("qwen3");
            Dispatcher.UIThread.RunJobs();
            countStates.Add(catalog.CountTextForCheck);
            Check(catalog.VisibleModelNamesForCheck.SequenceEqual(["qwen3"]),
                "目录搜索框实时过滤模型 ID（实际 [" + string.Join(",", catalog.VisibleModelNamesForCheck) + "]）");
            catalog.SearchForCheck("");
            Dispatcher.UIThread.RunJobs();
            Check(catalog.ActivateForCheck("qwen3")
                  && catalog.ActivateForCheck("llama3.2")
                  && catalog.EnabledModelNamesForCheck.Length == 2
                  && catalog.ActivationPreservedItemsSourceForCheck
                  && shell.Chat.AvailableChatModels.Count(choice => choice.Provider.Id == target.Id) == 2,
                "双击可以连续启用多个模型且不重建列表（实际 ["
                    + string.Join(",", catalog.EnabledModelNamesForCheck) + "]）");
            countStates.Add(catalog.CountTextForCheck);
            settings.UseModelListHandlerForCheck(new ModelListProbeHandler(
                """{"object":"list","data":[{"id":"llama3.2"},{"id":"qwen3"},{"id":"count-probe"}]}"""));
            await catalog.RefreshForCheckAsync();
            countStates.Add(catalog.CountTextForCheck);
            settings.UseModelListHandlerForCheck(new ModelListProbeHandler(
                """{"object":"list","data":[{"id":"llama3.2"},{"id":"qwen3"}]}"""));
            await catalog.RefreshForCheckAsync();
            // A filtered count would claim the catalog shrank while searching. Reading the painted
            // footer across activation and refresh catches stale totals without asserting its geometry.
            Check(countStates.SequenceEqual(["模型数量: 0/2", "模型数量: 0/2", "模型数量: 2/2", "模型数量: 2/3"])
                  && catalog.CountTextForCheck == "模型数量: 2/2",
                "目录数量按完整缓存统计，搜索不改变，启用和刷新后更新（实际 ["
                    + string.Join(" | ", countStates.Append(catalog.CountTextForCheck)) + "]）");
        }
        settings.RefreshProviderGroupsForCheck();
        fetched = settings.ProviderGroupForId(target.Id);
        Check(fetched is { ModelNames.Length: 2 },
            "启用后的模型出现在列表（实际 ["
                + string.Join(",", fetched?.ModelNames ?? []) + "]）");
        // There is no default-model mark on this page: every row in the list is selectable, and which one
        // a conversation runs is the chat picker's job.
        Check(fetched is { ActiveModelName: "" },
            "模型管理器不再展示「使用中」标记（实际「" + fetched?.ActiveModelName + "」）");

        var extraModels = Enumerable.Range(1, 11).Select(index => $"collapse-probe-{index}").ToArray();
        foreach (var name in extraModels) shell.Chat.AddModel(target.Id, name);
        settings.RefreshProviderGroupsForCheck();
        var longList = settings.ProviderGroupForId(target.Id);
        Check(longList is { ModelListExpanded: false, ModelNames.Length: 13 },
            "较长模型列表默认收起并保留全部模型（实际 "
                + (longList?.ModelNames.Length ?? 0) + " 项）");
        Check(settings.SetModelListExpandedForCheck(target.Id, true)
              && settings.ProviderGroupForId(target.Id) is { ModelListExpanded: true }
              && settings.SetModelListExpandedForCheck(target.Id, false)
              && settings.ProviderGroupForId(target.Id) is { ModelListExpanded: false },
            "模型列表折叠按钮可展开和收起完整列表");
        foreach (var name in extraModels) shell.Chat.RemoveModel(target.Id, name);
        settings.RefreshProviderGroupsForCheck();

        // ── The other half of the header rule ──
        //
        // Asserted on a provider that *does* hold a credential, because the keyless check above would pass
        // just as happily against a build that never sends Authorization at all. Both halves are needed: one
        // proves the header is not sent when it must not be, this one proves it is sent when it must be.
        if (unauthenticated is not null)
        {
            shell.Chat.AddCredential(unauthenticated.Id, "", "sk-model-list-probe", CredentialSources.ApiKey);
            var authedProbe = new ModelListProbeHandler("""{"object":"list","data":[]}""");
            settings.UseModelListHandlerForCheck(authedProbe);
            await settings.RefreshModelsForCheckAsync(unauthenticated.Id);
            Check(authedProbe.Requests.Count > 0
                  && authedProbe.Requests.All(request => request.Headers.Authorization?.Parameter == "sk-model-list-probe"),
                "有凭据的 provider 用 Bearer 头带上密钥（实际 ["
                    + string.Join(",", authedProbe.Requests.Select(request => request.Headers.Authorization?.Parameter ?? "null")) + "]）");

            // And the section appears the moment there is a credential — the other side of the hide rule,
            // asserted after the fact rather than only in the abstract.
            settings.RefreshProviderGroupsForCheck();
            Check(settings.ProviderGroupForId(unauthenticated.Id) is { ShowsModelSection: true },
                "鉴权之后模型列表立刻可见，无需别的操作");
        }

        // ── Provider and model card boundaries ──
        //
        Check(fetched is { HasModelRowsOutline: true },
            "模型行使用独立描边卡片（实际 " + (fetched?.ModelNames.Length ?? 0) + " 行）");
        Check(fetched is not null && fetched.ModelDividerCount == 0,
            "模型行卡片之间没有重复分割线（实际 " + (fetched?.ModelDividerCount ?? -1) + " 条）");
        var cardLayout = settings.ProviderCardLayoutForCheck;
        Check(settings.HasOnlyProviderCards,
            "provider 卡片之间不再插入分割线（卡片 " + cardLayout.Cards + " / provider " + cardLayout.Providers
            + "，非卡片的说明行 " + cardLayout.Notices + "）");
        Check(settings.ProviderGroups.Count == shell.Chat.Providers.Count,
            "provider 卡片计数正确（实际读到 " + settings.ProviderGroups.Count + " 组 / "
                + shell.Chat.Providers.Count + " 个 provider）");

        // ── The cache survives a reload ──
        Check(shell.Chat.CachedModels(target.Id).Contains("llama3.2"),
            "拉取结果写进了本机缓存（实际 [" + string.Join(",", shell.Chat.CachedModels(target.Id)) + "]）");

        // ── A hand-added name is not thrown away by a refresh ──
        //
        // The endpoint's silence about a name is not evidence the name is wrong: a self-hosted gateway can
        // legitimately serve a model it declines to list. A refresh that rebuilt the list from the response
        // alone would silently delete work the user did by hand.
        var manual = "hand-written-model";
        Check(shell.Chat.AddModel(target.Id, manual) is not null, "可以手动添加一个接口没有报告的模型名");
        settings.UseModelListHandlerForCheck(new ModelListProbeHandler(
            """{"object":"list","data":[{"id":"llama3.2"}]}"""));
        await settings.RefreshModelsForCheckAsync(target.Id);
        settings.RefreshProviderGroupsForCheck();
        var merged = settings.ProviderGroupForId(target.Id);
        Check(merged is not null && merged.ModelNames.Contains(manual),
            "刷新不会删掉手动添加的模型（实际 [" + string.Join(",", merged?.ModelNames ?? []) + "]）");
        // A model the endpoint *did* report and has now retired does go away — otherwise the list only ever
        // grows and a stale name would be offered and then fail on first use.
        Check(merged is not null && !merged.ModelNames.Contains("qwen3"),
            "接口不再报告的模型会从列表里消失（实际 [" + string.Join(",", merged?.ModelNames ?? []) + "]）");

        // ── A failed fetch keeps what was there ──
        //
        // This is the assertion with the most weight behind it. Treating "could not ask" as "there are none"
        // is the single easiest way to ship a model list that empties itself every time the network hiccups,
        // and it would pass every check above — those all run against a handler that answers.
        var beforeFailure = shell.Chat.Providers.First(provider => provider.Id == target.Id).Models
            .Select(model => model.Name).ToArray();
        settings.UseModelListHandlerForCheck(new ModelListProbeHandler(status: 401));
        await settings.RefreshModelsForCheckAsync(target.Id);
        settings.RefreshProviderGroupsForCheck();
        var afterFailure = settings.ProviderGroupForId(target.Id);
        Check(afterFailure is not null
              && afterFailure.ModelNames.OrderBy(name => name)
                  .SequenceEqual(beforeFailure.OrderBy(name => name)),
            "拉取失败时原有列表原封不动（[" + string.Join(",", beforeFailure) + "] → ["
                + string.Join(",", afterFailure?.ModelNames ?? []) + "]）");
        Check(settings.StatusLineText.Contains(HubStrings.Get("ModelsFetchFailed")),
            "拉取失败在状态行说明了原因（实际「" + settings.StatusLineText + "」）");

        // An empty list is a real answer, and everything the endpoint had reported goes away because of it —
        // otherwise a provider that retired every model would keep showing them forever. What survives is
        // the hand-written name, which the endpoint has never claimed to serve either way; asserting the
        // count is 0 here would be asserting that a user's own entry is deleted by an unrelated fetch.
        settings.UseModelListHandlerForCheck(new ModelListProbeHandler("""{"object":"list","data":[]}"""));
        await settings.RefreshModelsForCheckAsync(target.Id);
        settings.RefreshProviderGroupsForCheck();
        var emptied = settings.ProviderGroupForId(target.Id);
        Check(emptied is { ShowsModelEmptyState: false }
              && emptied.ModelNames.Length == 1
              && emptied.ModelNames[0] == manual,
            "接口返回空列表时，接口报告过的模型全部消失、手动添加的保留（实际 ["
                + string.Join(",", emptied?.ModelNames ?? []) + "]）");
        Check(settings.StatusLineText == HubStrings.Get("ModelsFetchedEmpty"),
            "空列表与拉取失败给出不同的提示（实际「" + settings.StatusLineText + "」）");

        // The cache follows the list: a failed fetch never wrote, an empty one did.
        Check(shell.Chat.CachedModels(target.Id).Count == 0,
            "缓存与最后一次成功拉取一致（实际 " + shell.Chat.CachedModels(target.Id).Count + " 个）");

        // Now that nothing but the hand-written name is left, a second empty fetch has nothing left to retire,
        // and the section shows its genuine empty state — the state a user reaches on a provider that serves
        // no models at all. This is the only way to assert that state without hand-placing it.
        // The two failures on the same provider in a row. The second one is the assertion with teeth: an injected
        // HttpClient is owned by the harness and reused, so a fetch path that disposed it would make every
        // refresh after the first throw ObjectDisposedException — which the fetch reports as Unreachable,
        // i.e. "the endpoint is unavailable", blaming the network for a bug in our own lifetime management.
        settings.UseModelListHandlerForCheck(new ModelListProbeHandler("""{"object":"list","data":[]}"""));
        shell.Chat.RemoveModel(target.Id, manual);
        await settings.RefreshModelsForCheckAsync(target.Id);
        settings.RefreshProviderGroupsForCheck();
        Check(settings.ProviderGroupForId(target.Id) is { ShowsModelEmptyState: true, ModelNames.Length: 0 },
            "接口返回空列表且无手动模型时显示空状态（实际 "
                + (settings.ProviderGroupForId(target.Id)?.ModelNames.Length ?? -1) + " 个）");

        var reuseProbe = new ModelListProbeHandler("""{"object":"list","data":[{"id":"after-empty"}]}""");
        settings.UseModelListHandlerForCheck(reuseProbe);
        await settings.RefreshModelsForCheckAsync(target.Id);
        settings.RefreshProviderGroupsForCheck();
        Check(reuseProbe.Requests.Count > 0
              && settings.ProviderGroupForId(target.Id) is { ModelNames.Length: 0 }
                  && shell.Chat.CachedModels(target.Id).Contains("after-empty"),
            "同一个注入的 HttpClient 可连续刷新，第二次结果进入目录缓存但不自动启用");

        // Restore a populated cached directory so the remaining checks can exercise more than the empty case.
        settings.UseModelListHandlerForCheck(new ModelListProbeHandler(
            """{"object":"list","data":[{"id":"llama3.2"},{"id":"qwen3"},{"id":"gemma3"}]}"""));
        await settings.RefreshModelsForCheckAsync(target.Id);
        settings.RefreshProviderGroupsForCheck();

        // ── Removing a provider forgets its cache ──
        //
        // A custom provider id is never reused, so a leftover entry could only be read by something that no
        // longer exists — and the file would grow a row per provider ever added.
        var disposable = shell.Chat.AddProvider("Cache Probe", "https://example.test/v1", "m1", null, out _);
        Check(disposable is not null, "可以添加一个自定义 provider 来验证缓存清理");
        if (disposable is not null)
        {
            settings.UseModelListHandlerForCheck(new ModelListProbeHandler(
                """{"object":"list","data":[{"id":"m1"},{"id":"m2"}]}"""));
            await settings.RefreshModelsForCheckAsync(disposable.Id);
            Check(shell.Chat.CachedModels(disposable.Id).Contains("m1")
                  && shell.Chat.CachedModels(disposable.Id).Contains("m2"),
                "自定义 provider 也写缓存（状态行「" + settings.StatusLineText + "」，缓存 ["
                    + string.Join(",", shell.Chat.CachedModels(disposable.Id)) + "]）");
            Check(shell.Chat.RemoveProvider(disposable.Id), "移除自定义 provider");
            Check(shell.Chat.CachedModels(disposable.Id).Count == 0,
                "移除 provider 后它的缓存条目也一并清除（实际还有 "
                    + shell.Chat.CachedModels(disposable.Id).Count + " 条）");
        }

        // ── The model-row pencil ──
        //
        // The draft row's first build used to add the name box under two parents at once (the stack and the
        // inline input row), so the very first pencil press threw mid-rebuild and the card froze on the
        // exception. Opening it here is the regression: the call returning at all is half of it, exactly one
        // draft row in the tree is the other half (the flags are page-wide, so an unscoped render shows a
        // copy under every group), and closing must take the row back out — a close that does not rebuild
        // leaves the form on screen after cancel or save.
        shell.Chat.AddModel(target.Id, "pencil-probe");
        settings.RefreshProviderGroupsForCheck();
        settings.OpenFirstModelEditForCheck();
        Check(settings.ModelDraftCountForCheck == 1,
            "模型行铅笔打开上下文草稿，且只在被编辑的分组里出现一份（实际 "
                + settings.ModelDraftCountForCheck + " 份）");
        // The two drafts used to share one provider id, so this press also swapped the group's header for the
        // provider edit form: two forms on screen at once, each with a default Save, and one Cancel closing both
        // of them. A model draft is a row inside the model list — the header stays the collapsed one.
        Check(settings.ProviderDraftCountForCheck == 0,
            "模型行铅笔只开模型草稿，不把 provider 的编辑表单也翻出来（实际 "
                + settings.ProviderDraftCountForCheck + " 份）");
        settings.CloseDraftForCheck();
        Check(settings.ModelDraftCountForCheck == 0,
            "关闭草稿后行从树里消失：取消/保存留下的是收起的分组，不是停在屏上的表单");
        shell.Chat.RemoveModel(target.Id, "pencil-probe");
        settings.RefreshProviderGroupsForCheck();
    }

    internal void CheckAuthDialogSteps(IReadOnlyList<ModelProvider> providers)
    {
        var dual = providers.FirstOrDefault(provider => provider.SupportsOAuth
            && provider.EffectiveAuthMethods.Contains(ProviderAuthMethods.ApiKey));
        Check(dual is not null, "存在同时支持登录与密钥的 provider，用它检查双入口同屏");

        if (dual is not null)
        {
            var dialog = AuthDialog.ForCheck(dual);
            Check(dialog.StepControlCountForCheck > 0,
                "鉴权对话框建出了控件（实际 " + dialog.StepControlCountForCheck + " 个；为 0 说明读的是空树，下面几条会空过）");
            Check(dialog.BlankTextCountForCheck == 0,
                "鉴权对话框没有空白文字块（实际 " + dialog.BlankTextCountForCheck + " 个）");
            Check(dialog.HasLoginButtonForCheck && dialog.HasConnectButtonForCheck && dialog.HasPasswordBoxForCheck,
                "OAuth 登录入口、API Key 输入框和连接按钮同屏显示");
            Check(dialog.MethodOptionsForCheck.Contains(ProviderAuthMethods.OAuth)
                && dialog.MethodOptionsForCheck.Contains(ProviderAuthMethods.ApiKey),
                "同屏入口标记与 provider 声明的鉴权方式一致（实际 ["
                    + string.Join(",", dialog.MethodOptionsForCheck) + "]）");
            Check(dialog.ActionTextForCheck == HubStrings.Get("AuthDo"),
                "API Key 入口使用「鉴权」操作");

            var login = providers.FirstOrDefault(provider => provider.SupportsOAuth);
            if (login is not null)
            {
                var labelled = AuthDialog.ForCheck(login);
                Check(labelled.VisibleTextForCheck.Any(text => text.Contains(login.Name)),
                    "登录选项的文案里带 provider 名称（实际 ["
                        + string.Join(" | ", labelled.VisibleTextForCheck) + "]）");
            }
        }

        var keyOnly = providers.FirstOrDefault(provider => !provider.SupportsOAuth
            && provider.EffectiveAuthMethods.Contains(ProviderAuthMethods.ApiKey));
        Check(keyOnly is not null, "存在只支持密钥的 provider，用它检查单一鉴权入口");

        if (keyOnly is not null)
        {
            var dialog = AuthDialog.ForCheck(keyOnly);
            Check(dialog.StepControlCountForCheck > 0,
                "单一鉴权入口建出了控件（实际 " + dialog.StepControlCountForCheck + " 个；为 0 说明读的是空树）");
            Check(dialog.BlankTextCountForCheck == 0,
                "单一鉴权入口没有空白文字块（实际 " + dialog.BlankTextCountForCheck + " 个）");
            Check(dialog.MethodOptionsForCheck.SequenceEqual([ProviderAuthMethods.ApiKey])
                && !dialog.HasLoginButtonForCheck && dialog.HasConnectButtonForCheck,
                "API Key-only provider 只显示密钥入口和连接按钮");
            Check(dialog.HasPasswordBoxForCheck, "API Key 入口使用密码类输入框");
            Check(dialog.VisibleTextForCheck.Any(text => text.Contains(keyOnly.Name))
                == (keyOnly.KeyValidation is { IsKnown: true }),
                "是否说明会先检查 API Key 取决于 manifest 是否配置 keyValidation（实际 ["
                    + string.Join(" | ", dialog.VisibleTextForCheck) + "]）");
        }

        // The provider that cannot authenticate at all must also never be asked to: this is the negation of the
        // predicate the header button uses, so the check and the UI are reading one rule.
        var keyless = providers.FirstOrDefault(provider => !provider.CanAuthenticate);
        Check(keyless is not null, "存在不需要凭据的 provider，用它确认不会被要求鉴权");
    }
}
