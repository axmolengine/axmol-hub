using Avalonia.Controls;

namespace AxmolHub.App;

public enum HubDialogButtons
{
    Ok,
    OkCancel,
    YesNo,
}

/// <summary>
/// 对应 WPF 的 MessageBoxResult。**Cancel 必须排在第一位**：Avalonia 的
/// <c>ShowDialog&lt;TResult&gt;</c> 在窗口"没有带结果就关掉"（点标题栏的 X）时返回 <c>default(T)</c>，
/// 若把 Ok 放在 0，用户关窗就会被读成"点了确定"—— 卸载确认框上这是个真错误。
/// </summary>
public enum HubDialogResult
{
    Cancel,
    Ok,
    Yes,
    No,
}

/// <summary>
/// WPF <c>MessageBox.Show</c> 在 Avalonia 的替代物。见 Views/HubDialog.axaml 的说明。
/// </summary>
public partial class HubDialog : Window
{
    private TaskCompletionSource<HubDialogResult>? _standalone;
    private bool _finished;

    public HubDialogResult Result { get; private set; } = HubDialogResult.Cancel;

    public HubDialog() => InitializeComponent();

    /// <summary>替换 MessageBox.Show。owner 为 null 时也能显示（启动失败路径）。</summary>
    public static Task<HubDialogResult> ShowAsync(
        Window? owner,
        string title,
        string message,
        HubDialogButtons buttons = HubDialogButtons.Ok,
        bool danger = false)
    {
        var dialog = new HubDialog();
        dialog.DialogTitle.Text = title;
        dialog.DialogMessage.Text = message;
        dialog.Build(buttons, danger);

        if (owner is not null)
        {
            return dialog.ShowDialog<HubDialogResult>(owner);
        }

        // ShowDialog 需要一个 owner，而"启动失败"这条路径上主窗口还不存在。
        // 退化成非模态 Show()，结果自己收：Closed 之后一并是 Cancel。
        var completion = new TaskCompletionSource<HubDialogResult>();
        dialog._standalone = completion;
        dialog.Closed += (_, _) => completion.TrySetResult(dialog.Result);
        dialog.Show();
        return completion.Task;
    }

    /// <summary>
    /// 按钮文案走 <see cref="HubStrings"/>。**以前这四个字是写死的中文** ——
    /// 那时界面以中文优先，看不出问题；英文界面上点开任何弹窗，按钮却还是"取消/确定"。
    /// 更要紧的一处是 `CjkFontNotice` 那条"缺中文字体"提示：它唯一的按钮如果写死中文，
    /// 在那个系统上本身就是个方框，提示就闭环不了。
    /// 顺序与语义（哪个是 primary / cancel）**不变**：那部分有自检断言守着。
    /// </summary>
    private void Build(HubDialogButtons buttons, bool danger)
    {
        switch (buttons)
        {
            case HubDialogButtons.OkCancel:
                Add(HubDialogResult.Cancel, HubStrings.Get("Cancel"), primary: false, cancel: true, danger: false);
                Add(HubDialogResult.Ok, HubStrings.Get("Ok"), primary: true, cancel: false, danger: danger);
                break;

            case HubDialogButtons.YesNo:
                Add(HubDialogResult.No, HubStrings.Get("No"), primary: false, cancel: true, danger: false);
                Add(HubDialogResult.Yes, HubStrings.Get("Yes"), primary: true, cancel: false, danger: danger);
                break;

            default:
                Add(HubDialogResult.Ok, HubStrings.Get("Ok"), primary: true, cancel: false, danger: danger);
                break;
        }
    }

    private void Add(HubDialogResult result, string caption, bool primary, bool cancel, bool danger)
    {
        // IsDefault / IsCancel 承担回车与 Esc，等价于 WPF MessageBox 的默认按钮与 Esc 行为。
        var button = new Button { Content = caption, IsDefault = primary, IsCancel = cancel };
        if (primary)
        {
            button.Classes.Add("primary");
        }

        if (danger)
        {
            button.Classes.Add("danger");
        }

        button.Click += (_, _) => Finish(result);
        DialogButtons.Children.Add(button);
    }

    private void Finish(HubDialogResult result)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        Result = result;

        if (_standalone is not null)
        {
            _standalone.TrySetResult(result);
            Close();
            return;
        }

        Close(result);
    }
}
