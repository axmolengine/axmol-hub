using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AxmolHub.App;

/// <summary>
/// 构建进度窗口。WPF 版 <c>BuildProgressWindow.cs</c> 的移植：它整个是**代码构建**的
/// （没有 XAML），所以移植只是把 WPF 类型换成 Avalonia 类型。
///
/// 阶段判断（编译 / 打包 / 签名）靠解析子进程输出的命令行，与 WPF 版逐行一致 ——
/// 这套正则是对着 CMake / Gradle / apksigner 的实际输出调出来的，改动前先看 WPF 版注释。
/// </summary>
public sealed class BuildProgressWindow : Window
{
    private readonly TextBlock _stage = new() { FontSize = 16, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _count = new() { HorizontalAlignment = HorizontalAlignment.Right, Foreground = Brushes.LightGray };
    private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap, MaxHeight = 58, Foreground = Brushes.LightGray };
    private readonly TextBlock _elapsed = new() { Foreground = Brushes.Gray };
    private readonly ProgressBar _progress = new()
    {
        Height = 8, Minimum = 0, Maximum = 100, IsIndeterminate = true,
        Foreground = new SolidColorBrush(Color.FromRgb(57, 155, 220)),
        Background = new SolidColorBrush(Color.FromRgb(48, 48, 48)),
    };
    private readonly Button _cancel = new();
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    // 不是 readonly：Closed 回调注册得比计时器创建更早，编译器据此认为它可能还是 null。
    private Timer? _timer;
    private readonly Action _cancelBuild;
    private bool _finished;
    private bool _cancelling;

    public BuildProgressWindow(string project, string target, string configuration, Action cancelBuild)
    {
        _cancelBuild = cancelBuild;
        Title = HubStrings.Get("BuildProgress");
        Width = 600;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = project, FontSize = 22, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = target + " · " + configuration, Foreground = Brushes.LightGray, Margin = new Thickness(0, 7, 0, 24) });

        var row = new DockPanel();
        DockPanel.SetDock(_count, Dock.Right);
        row.Children.Add(_count);
        row.Children.Add(_stage);
        panel.Children.Add(row);

        _progress.Margin = new Thickness(0, 14, 0, 14);
        panel.Children.Add(_progress);
        panel.Children.Add(_detail);
        panel.Children.Add(new TextBlock
        {
            Text = HubStrings.Get("BuildProgressHint"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 14, 0, 22),
        });

        _cancel.Content = HubStrings.Get("Cancel");
        var footer = new DockPanel();
        DockPanel.SetDock(_cancel, Dock.Right);
        footer.Children.Add(_cancel);
        footer.Children.Add(_elapsed);
        panel.Children.Add(footer);

        Content = panel;
        SetStage("BuildPreparing");

        _cancel.Click += (_, _) => RequestCancel();
        Closing += (_, e) =>
        {
            if (_finished)
            {
                return;
            }

            e.Cancel = true;
            RequestCancel();
        };
        Closed += (_, _) =>
        {
            _timer?.Dispose();
            _watch.Stop();
        };

        // Avalonia 没有 WPF 的 DispatcherTimer，用普通 Timer + UI 线程投递。
        // 少这层投递就会从后台线程改 TextBlock，属于跨线程访问。
        _timer = new Timer(_ => Dispatcher.UIThread.Post(() =>
            _elapsed.Text = HubStrings.Get("BuildElapsed") + " " + _watch.Elapsed.ToString(@"hh\:mm\:ss")),
            null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _elapsed.Text = HubStrings.Get("BuildElapsed") + " 00:00:00";
    }

    private void RequestCancel()
    {
        if (_cancelling || _finished)
        {
            return;
        }

        _cancelling = true;
        _cancel.IsEnabled = false;
        SetStage("Cancelling");
        _cancelBuild();
    }

    private void SetStage(string key)
    {
        _stage.Text = HubStrings.Get(key);
        _progress.IsIndeterminate = true;
        _count.Text = "";
    }

    public void Report(string message)
    {
        if (_finished || _cancelling)
        {
            return;
        }

        var line = message.StartsWith("stderr: ") ? message[8..] : message;
        if (line.StartsWith("Command: "))
        {
            if (line.Contains("--build"))
            {
                SetStage("BuildCompiling");
            }
            else if (line.Contains("org.gradle.launcher.GradleMain"))
            {
                SetStage("BuildPackaging");
            }
            else if (line.Contains("apksigner") || line.Contains("jarsigner") || line.Contains("zipalign")
                     || line.Contains("keytool") || line.Contains("aapt2"))
            {
                SetStage("BuildVerifying");
            }
            else if (line.Contains("\"-S\""))
            {
                SetStage("BuildConfiguring");
            }

            // 完整命令留在主窗口日志中，不挤占当前步骤。
            return;
        }

        if (line.StartsWith("Exit code:"))
        {
            if (!_progress.IsIndeterminate)
            {
                SetStage("BuildVerifying");
            }

            return;
        }

        if (line.Length == 0)
        {
            return;
        }

        _detail.Text = line.Length > 220 ? line[..217] + "…" : line;

        var step = Regex.Match(line, @"(?:^|\s)\[(\d+)/(\d+)\]");
        if (step.Success && long.TryParse(step.Groups[1].Value, out var current)
            && long.TryParse(step.Groups[2].Value, out var total) && total > 0 && current <= total)
        {
            _stage.Text = HubStrings.Get("BuildCompiling");
            _progress.IsIndeterminate = false;
            _progress.Value = 100.0 * current / total;
            _count.Text = $"{current} / {total} · {_progress.Value:0}%";
        }
        else if (Regex.Match(line, @"\[\s*(\d{1,3})%\]") is { Success: true } percentage
                 && int.Parse(percentage.Groups[1].Value) <= 100)
        {
            _stage.Text = HubStrings.Get("BuildCompiling");
            _progress.IsIndeterminate = false;
            _progress.Value = int.Parse(percentage.Groups[1].Value);
            _count.Text = $"{_progress.Value:0}%";
        }
        else if (line.StartsWith("> Task "))
        {
            SetStage("BuildPackaging");
        }
    }

    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        Close();
    }
}
