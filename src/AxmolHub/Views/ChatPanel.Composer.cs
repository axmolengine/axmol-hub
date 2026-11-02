using System.Runtime.CompilerServices;
using AxmolHub.Core;

namespace AxmolHub;

public partial class ChatPanel
{
    // The registry owns one Conversation instance per session. Weak keys release abandoned drafts when a
    // deleted session leaves the registry; drafts never write text or attachment bytes to the session store.
    private readonly ConditionalWeakTable<Conversation, ComposerDraft> _composerDrafts = new();
    private Conversation? _composerConversation;
    private ComposerDraft _composerDraft = new();
    private long _composerEpoch;
    private bool _restoringComposer;
    private bool _creatingConversationForSend;

    private sealed class ComposerDraft
    {
        public string Text = "";
        public int Caret;
        public int SelectionStart;
        public int SelectionEnd;
        public readonly List<ContextAttachment> Attachments = [];
        public readonly List<PendingPicture> Pictures = [];
        public string? ReplyToId;
        public (string ConversationId, string Token)? PlanTarget;
        public SteerDraft? Steer;
        public long Revision;
        public bool Sending;
        public int OwnTurns;
    }

    private void SwitchComposerConversation()
    {
        var conversation = _chat.ActiveConversation;
        if (ReferenceEquals(conversation, _composerConversation)) return;
        SaveComposerText();
        var previous = _composerDraft;
        var unbound = _composerConversation is null;
        _composerConversation = conversation;
        _composerEpoch++;
        // Only creating a session for this very send adopts an unbound draft. A navigation/new-chat action
        // always opens that session's own draft, even when the previous view had no conversation yet.
        _composerDraft = conversation is null ? new ComposerDraft()
            : _composerDrafts.GetValue(conversation, _ => unbound && _creatingConversationForSend
                ? previous : new ComposerDraft());
        _composerDraft.OwnTurns = conversation?.Messages.Count(turn => turn.Role == ChatRoles.User) ?? 0;
        _restoringComposer = true;
        try
        {
            InputBox.Text = _composerDraft.Text;
            InputBox.CaretIndex = Math.Clamp(_composerDraft.Caret, 0, _composerDraft.Text.Length);
            InputBox.SelectionStart = Math.Clamp(_composerDraft.SelectionStart, 0, _composerDraft.Text.Length);
            InputBox.SelectionEnd = Math.Clamp(_composerDraft.SelectionEnd, 0, _composerDraft.Text.Length);
            RenderContextAttachments();
            RenderReplyTarget();
            _recallIndex = 0;
        }
        finally
        {
            _restoringComposer = false;
        }
    }

    private void SaveComposerText()
    {
        _composerDraft.Text = InputBox.Text ?? "";
        _composerDraft.Caret = InputBox.CaretIndex;
        _composerDraft.SelectionStart = InputBox.SelectionStart;
        _composerDraft.SelectionEnd = InputBox.SelectionEnd;
    }

    private void ComposerTextChanged()
    {
        if (_restoringComposer) return;
        SaveComposerText();
        _composerDraft.Revision++;
        CapturePlanDraftTarget();
    }

    private bool ComposerStillCurrent(long epoch, ComposerDraft draft, long revision)
        => epoch == _composerEpoch && ReferenceEquals(draft, _composerDraft) && draft.Revision == revision;

    private void ReportComposerSendChanged(string? conversationId, ComposerDraft draft, string key)
    {
        _chat.AuditWrite?.Invoke($"Composer send refused: {key}; source={conversationId ?? "unbound"}; "
            + $"current={_chat.ViewedConversationId ?? "unbound"}; expected-plan={draft.PlanTarget?.Token ?? "none"}; "
            + $"current-plan={ChatWorkspace.PendingPlanToken(_chat.ActiveConversation) ?? "none"}");
        var ownTurns = _chat.ViewedConversationId == conversationId
            ? _chat.ActiveConversation?.Messages.Count(turn => turn.Role == ChatRoles.User) ?? 0
            : draft.OwnTurns;
        RememberNotice(conversationId ?? "", HubStrings.Get(key), danger: true, ownTurns);
        RenderNotice();
    }

    private bool ValidateComposerSend(long epoch, ComposerDraft draft, long revision, string? conversationId)
    {
        if (epoch != _composerEpoch || !ReferenceEquals(draft, _composerDraft))
        {
            ReportComposerSendChanged(conversationId, draft, "ChatComposerSessionChanged");
            return false;
        }
        if (draft.Revision != revision)
        {
            ReportComposerSendChanged(conversationId, draft, "ChatComposerDraftChanged");
            return false;
        }
        if (draft.PlanTarget is { } target
            && target.Token != ChatWorkspace.PendingPlanToken(_chat.ActiveConversation))
        {
            ReportPlanDraftChanged();
            return false;
        }
        return true;
    }

    private async Task SendAsync()
    {
        SwitchComposerConversation();
        var draft = _composerDraft;
        if (draft.Sending) return;
        draft.Sending = true;
        try
        {
            await SendDraftAsync();
        }
        finally
        {
            draft.Sending = false;
        }
    }
}
