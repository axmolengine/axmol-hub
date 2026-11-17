using Avalonia.Controls;
using AxmolHub.Core;

namespace AxmolHub;

public partial class ChatPanel
{
    private Task AddScreenshotAsync(ScreenshotKind kind) => AddScreenshotAsync(kind, ScreenshotPicker.PickAsync);

    private async Task AddScreenshotAsync(ScreenshotKind kind,
        Func<Window, ScreenshotKind, CancellationToken, Task<ScreenshotPick>> pick)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner || ScreenshotPicker.IsPicking) return;
        if (_pendingPictures.Count >= ChatImageFormat.MaxImagesPerMessage)
        {
            AppendNotice(ImageNotice(ChatImageVerdict.TooMany), danger: true);
            return;
        }
        var epoch = _composerEpoch;
        var draft = _composerDraft;
        using var stop = new CancellationTokenSource();
        void Closed(object? sender, EventArgs args) => stop.Cancel();
        owner.Closed += Closed;
        try
        {
            var result = await pick(owner, kind, stop.Token);
            // Selection happens outside Hub. A delayed result must never enter another conversation's draft,
            // including a round trip away from and back to the original session.
            if (stop.IsCancellationRequested || epoch != _composerEpoch || !ReferenceEquals(draft, _composerDraft)) return;
            if (result.ErrorKey is { } error)
                AppendNotice(NoticeFor(error, _chat.ViewedConversationId), danger: true);
            else if (result.Png is { } bytes)
            {
                if (result.ManualTarget) AppendNotice(HubStrings.Get("ChatCapturePortalHint"), danger: false);
                AddPendingPicture(bytes, HubStrings.Get(result.ManualTarget ? "ChatCapturedImageName"
                    : kind == ScreenshotKind.Region ? "ChatCapturedRegionName" : "ChatCapturedWindowName"));
            }
            InputBox.Focus();
        }
        finally { owner.Closed -= Closed; }
    }
}
