using System.Text;
using AxmolHub.Agent;
using AxmolHub.Core;
using Microsoft.Extensions.AI;
using static AxmolHub.ChatWorkspace;

namespace AxmolHub;

internal sealed partial class ChatExecutionCoordinator
{
    private readonly CancellationTokenSource _memoryStop = new();
    // Keep summary scripts separate from task scripts; a background summary must not advance a task fixture.
    internal Func<ModelProvider, string, IChatClient>? MemoryClientOverride { get; set; }

    private sealed record TaskMemory(string Root, DateTimeOffset At, MemoryRunSummary Summary,
        ChatModelOption? Choice, Func<ModelProvider, string, IChatClient>? ClientFactory,
        IReadOnlyList<string> Secrets, DateTimeOffset? GoalTurnAt);

    private static void SetMemoryStart(ConversationRun run, Conversation? conversation)
    {
        run.MemoryStartTurnId = conversation?.Messages.LastOrDefault(turn => turn.Role == ChatRoles.User)?.Id;
        run.MemoryWorkspaceRoot = conversation?.WorkspaceRoot;
    }

    /// <summary>Called on the dispatcher before completion. Never includes the next send, reasoning or attachments.</summary>
    private TaskMemory? PrepareTaskMemory(ConversationRun run, RunResult result, string? detail)
    {
        try
        {
            if (_disposed || run.MemoryWorkspaceRoot is not { Length: > 0 } root
                || Sessions.Registry.Peek(run.ConversationId) is not { } conversation
                || conversation.WorkspaceRoot != root) return null;
            var start = conversation.Messages.FindIndex(turn => turn.Id == run.MemoryStartTurnId);
            if (start < 0) return null;
            var turns = conversation.Messages.Skip(start).ToArray();
            // A tool-less successful conversation adds no daily noise. Failed tasks still have a useful handoff.
            if (result == RunResult.Completed && !turns.Any(turn => turn.ToolCallId is not null)) return null;
            var secrets = Providers.SensitiveValues().ToArray();
            var summary = MemoryLog.Capture(run.ConversationId, conversation.Title, turns,
                result.ToString(), detail, secrets);
            var choice = ClientOverride is not null && MemoryClientOverride is null
                ? null : Providers.ModelFor(run.ConversationId);
            return new(root, DateTimeOffset.Now, summary, choice, MemoryClientOverride, secrets, conversation.GoalTurnAt);
        }
        catch (Exception ex)
        {
            Audit(run.ConversationId, $"Task memory snapshot failed: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>Best effort, one bounded tool-free request. Errors never escape into task completion.</summary>
    private async Task WriteTaskMemoryAsync(TaskMemory memory)
    {
        string? generated = null;
        var id = memory.Summary.ConversationId;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_memoryStop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            if (memory.Choice is { } choice && memory.Summary.Result != nameof(RunResult.Cancelled))
            {
                var reasoning = new ReasoningTable();
                var overridden = memory.ClientFactory?.Invoke(choice.Provider, id);
                var client = overridden ?? ChatClientFactory.Create(choice.Provider, choice.ModelName, reasoning, maxRetries: 0);
                using var ownedClient = overridden is null ? client : null;
                var pipeline = new ChatPipeline(client, reasoning);
                var output = new StringBuilder();
                ContextReport? usage = null;
                var reservedGoal = false;
                try
                {
                    var inputBudget = Math.Clamp(ContextBudget.For(choice.Provider, choice.ModelName).ConversationRoom / 2, 256, 6000);
                    var input = ToolResultCap.ApplyTokenBudget(MemoryLog.InputFor(memory.Summary), inputBudget);
                    await foreach (var chunk in pipeline.SendAsync(choice.Provider,
                        [ChatTurn.User(input)], MemoryLog.SummaryPrompt,
                        modelName: choice.ModelName, maxOutputTokens: 4096, toolMode: ChatToolMode.None,
                        retryOptions: new RequestRetryOptions
                        {
                            MaxRetries = 0, RetryEmpty = false,
                            ValidateSelection = () => ValidateRequestSelectionAsync(id, choice.Provider.Id, choice.ModelName),
                        },
                        beforeRequest: estimate => ApplyOnUiAsync(() =>
                        {
                            if (_disposed || Sessions.Registry.Peek(id) is not { } session || session.WorkspaceRoot != memory.Root)
                                throw new OperationCanceledException();
                            if (session.Goal is not { Length: > 0 }) return;
                            if (session.GoalTurnAt != memory.GoalTurnAt) throw new GoalGuardException("goal_changed");
                            if (!GoalPolicyFor(session).CanRequest(session, estimate)) throw new GoalGuardException("token_budget");
                            reservedGoal = true;
                            Sessions.Registry.TryUpdate(id, opened => opened.GoalEstimatedTokens += estimate);
                        }),
                        afterRequest: (estimate, reported, _) => ApplyOnUiAsync(() =>
                        {
                            if (!_disposed && reservedGoal && reported >= 0) Sessions.Registry.TryUpdate(id, opened =>
                            {
                                if (opened.Goal is not { Length: > 0 } || opened.GoalTurnAt != memory.GoalTurnAt) return;
                                opened.GoalEstimatedTokens = Math.Max(0, opened.GoalEstimatedTokens - estimate);
                                opened.GoalReportedTokens += reported;
                            });
                        }),
                        onUsage: report => usage = report, cancellationToken: deadline.Token).ConfigureAwait(false))
                    {
                        output.Append(chunk);
                        if (output.Length > MemoryLog.MaxSummaryCharacters)
                            throw new InvalidDataException("Task memory exceeded its output limit.");
                    }
                    generated = MemoryLog.ValidateSummary(output.ToString(), memory.Secrets);
                    if (generated is null) Audit(id, "Task memory response rejected; using factual fallback.");
                }
                finally
                {
                    // Account for the extra request without replacing the next answer's live context meter or cost.
                    if (usage is not null)
                    {
                        Audit(id, $"Task memory usage: input={usage.SummedInputTokens} output={usage.OutputTokens} requests={usage.Requests}");
                        var burn = new ContextBurn(usage.SummedInputTokens, usage.OutputTokens, usage.ReasoningTokens,
                            usage.SummedCachedInputTokens, usage.Requests);
                        await ApplyOnUiAsync(() =>
                        {
                            if (!_disposed) Sessions.Registry.TryUpdate(id, opened => AddSessionBurn(opened, burn));
                        }).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // No exception messages: a provider can echo private input or credentials in an error response.
            Audit(id, $"Task memory generation failed ({ex.GetType().Name}); using factual fallback.");
        }

        try
        {
            // A deleted session, switched workspace or disposed shell must not receive a late background write.
            var stillValid = await ReadOnUiAsync(() => !_disposed
                && Sessions.Registry.Peek(id)?.WorkspaceRoot == memory.Root).ConfigureAwait(false);
            if (!stillValid) return;
            var lines = MemoryLog.LinesForRun(memory.Summary, memory.At, generated)
                .Select(line => MemoryLog.Clean(line, memory.Secrets)).ToArray();
            var appended = MemoryLog.Append(MemoryLog.FileFor(memory.Root, memory.At), lines);
            if (!appended.Written) Audit(id, "Task memory not written: " + appended.Message);
        }
        catch (Exception ex)
        {
            Audit(id, $"Task memory write failed: {ex.GetType().Name}");
        }
    }
}
