using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AxmolHub.App;

public sealed class BuildProgressWindow : Window
{
    private readonly TextBlock stage = new() { FontSize = 16, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock count = new() { HorizontalAlignment = HorizontalAlignment.Right, Foreground = Brushes.LightGray };
    private readonly TextBlock detail = new() { TextWrapping = TextWrapping.Wrap, MaxHeight = 58, Foreground = Brushes.LightGray };
    private readonly TextBlock elapsed = new() { Foreground = Brushes.Gray };
    private readonly ProgressBar progress = new() { Height = 8, Minimum = 0, Maximum = 100, IsIndeterminate = true, Foreground = new SolidColorBrush(Color.FromRgb(57, 155, 220)), Background = new SolidColorBrush(Color.FromRgb(48, 48, 48)) };
    private readonly Button cancel = new() { Content = Texts.Get("Cancel") };
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Action cancelBuild;
    private bool finished;
    private bool cancelling;

    public BuildProgressWindow(Window owner, string project, string target, string configuration, Action cancelBuild)
    {
        this.cancelBuild = cancelBuild;
        Owner = owner; Title = Texts.Get("BuildProgress"); Width = 600; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = owner.Background; Foreground = owner.Foreground; FontFamily = owner.FontFamily; FontSize = owner.FontSize;
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = project, FontSize = 22, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = target + " · " + configuration, Foreground = Brushes.LightGray, Margin = new Thickness(0, 7, 0, 24) });
        var row = new DockPanel(); DockPanel.SetDock(count, Dock.Right); row.Children.Add(count); row.Children.Add(stage); panel.Children.Add(row);
        progress.Margin = new Thickness(0, 14, 0, 14); panel.Children.Add(progress); panel.Children.Add(detail);
        panel.Children.Add(new TextBlock { Text = Texts.Get("BuildProgressHint"), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 14, 0, 22) });
        var footer = new DockPanel(); DockPanel.SetDock(cancel, Dock.Right); footer.Children.Add(cancel); footer.Children.Add(elapsed); panel.Children.Add(footer);
        Content = panel; SetStage("BuildPreparing");
        cancel.Click += (_, _) => RequestCancel();
        Closing += (_, e) => { if (!finished) { e.Cancel = true; RequestCancel(); } };
        Closed += (_, _) => { timer.Stop(); watch.Stop(); };
        timer.Tick += (_, _) => elapsed.Text = Texts.Get("BuildElapsed") + " " + watch.Elapsed.ToString(@"hh\:mm\:ss");
        elapsed.Text = Texts.Get("BuildElapsed") + " 00:00:00"; timer.Start();
    }

    private void RequestCancel()
    {
        if (cancelling || finished) return;
        cancelling = true; cancel.IsEnabled = false; SetStage("Cancelling"); cancelBuild();
    }
    private void SetStage(string key)
    {
        stage.Text = Texts.Get(key); progress.IsIndeterminate = true; count.Text = "";
    }
    public void Report(string message)
    {
        if (finished || cancelling) return;
        var line = message.StartsWith("stderr: ") ? message[8..] : message;
        if (line.StartsWith("Command: "))
        {
            if (line.Contains("--build")) SetStage("BuildCompiling");
            else if (line.Contains("org.gradle.launcher.GradleMain")) SetStage("BuildPackaging");
            else if (line.Contains("apksigner") || line.Contains("jarsigner") || line.Contains("zipalign") || line.Contains("keytool") || line.Contains("aapt2")) SetStage("BuildVerifying");
            else if (line.Contains("\"-S\"")) SetStage("BuildConfiguring");
            return; // 完整命令留在主窗口日志中，不挤占当前步骤。
        }
        if (line.StartsWith("Exit code:"))
        {
            if (!progress.IsIndeterminate) SetStage("BuildVerifying");
            return;
        }
        if (line.Length == 0) return;
        detail.Text = line.Length > 220 ? line[..217] + "…" : line;
        var step = Regex.Match(line, @"(?:^|\s)\[(\d+)/(\d+)\]");
        if (step.Success && long.TryParse(step.Groups[1].Value, out var current) && long.TryParse(step.Groups[2].Value, out var total) && total > 0 && current <= total)
        {
            stage.Text = Texts.Get("BuildCompiling"); progress.IsIndeterminate = false; progress.Value = 100.0 * current / total;
            count.Text = $"{current} / {total} · {progress.Value:0}%";
        }
        else if (Regex.Match(line, @"\[\s*(\d{1,3})%\]") is { Success: true } percentage && int.Parse(percentage.Groups[1].Value) <= 100)
        {
            stage.Text = Texts.Get("BuildCompiling"); progress.IsIndeterminate = false; progress.Value = int.Parse(percentage.Groups[1].Value); count.Text = $"{progress.Value:0}%";
        }
        else if (line.StartsWith("> Task ")) SetStage("BuildPackaging");
    }
    public void Finish()
    {
        if (finished) return;
        finished = true; Close();
    }
    public void CaptureEvidence(string path)
    {
        UpdateLayout(); var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = System.IO.File.Create(path); encoder.Save(output);
    }
}
