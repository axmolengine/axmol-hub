using AxmolHub.Core;
using static AxmolHub.ShellCheckSupport;

namespace AxmolHub;
internal sealed class ShellContextBoundaryChecks
{
    private readonly Action<bool, string> Check;
    internal ShellContextBoundaryChecks(CheckReport report, Action<bool, string> check, ShellCheckFixture fixture)
    {
        Check = check;
    }

    internal async Task CheckContextBoundaryAsync(MainWindow shell, ChatPanel panel)
    {
        var chat = shell.Chat;
        var before = chat.ActiveConversation?.Id;
        var previous = chat.ClientOverride;
        var workspace = ScratchDirectory.Resolve("context-boundary", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var provider = chat.AddProvider("Boundary fixture", "https://offline.invalid/v1", "boundary-model", null, out _)!;
        chat.SetModelContextTokens(provider.Id, "boundary-model", 32768);
        var session = StartAgentSession(chat, provider.Id);
        session.WorkspaceRoot = workspace;
        var burst = new ContextBurstClient();
        var summaries = 0;
        try
        {
            for (var i = 0; i < 8; i++) File.WriteAllText(Path.Combine(workspace, $"part-{i}.txt"), new string('x', 80_000));
            var usage = chat.TranscriptUsageForCheck(session.Id);
            var body = Math.Max(1000, ((int)(usage.Room * 0.70) - usage.Estimated) / 6 * 3);
            for (var i = 0; i < 6; i++) chat.SeedAssistantReplyForCheck(session.Id, new string('x', body));
            chat.ClientOverride = (_, id) =>
            {
                if (id == session.Id && chat.IsCompressingContext(id))
                {
                    summaries++;
                    return new ScriptedChatClient(["Context summary: continue reading the requested fixture files."]);
                }
                return burst;
            };
            chat.OpenConversation(session.Id);
            await panel.SendForCheckAsync("Read all eight fixture parts, then finish.");
            await WaitForIdleAsync(chat);
            var results = session.Messages.Where(turn => turn.Role == ChatRoles.Tool).ToArray();
            Check(summaries > 0 && burst.Executed == 8 && results.Length == 8
                  && results.Select(turn => turn.ToolCallId).Distinct().Count() == 8
                  && session.Messages[^1].Text == "boundary done",
                $"工具执行内部压缩后自动续跑，八个结果各出现一次并正常结束（摘要 {summaries}，调用 {burst.Executed}，结果 {results.Length}）");
        }
        finally
        {
            chat.ClientOverride = previous;
            chat.DeleteConversation(session.Id);
            chat.RemoveProvider(provider.Id);
            if (before is not null) chat.OpenConversation(before);
            panel.Reload();
            // Native decoders, repository reads and background memory logging may still own fixture files.
            // Retry only fixture cleanup, with a finite deadline; a persistent handle remains a check failure.
            await DeleteFixtureDirectoryAsync(workspace);
        }
    }
}
