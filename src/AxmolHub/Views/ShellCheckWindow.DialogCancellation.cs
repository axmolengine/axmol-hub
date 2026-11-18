using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AxmolHub.Agent;
using AxmolHub.Core;
using static AxmolHub.ThemeProbe;

namespace AxmolHub;

/// <summary>Consumer wiring: a shared caption must cancel the caller's operation, never submit it.</summary>
internal sealed class ShellDialogCancellationChecks(Action<bool, string> check)
{
    internal async Task RunAsync(MainWindow shell)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            check(false, "夹具：对话框取消检查需要桌面生命周期");
            return;
        }

        var root = ScratchDirectory.Resolve("dialog-cancellation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // A registered project always carries .axproj (HubState refuses one without it), and the Android release
        // dialog reads the application id from there — a bare directory is not a project this fixture may build.
        File.WriteAllText(Path.Combine(root, ".axproj"), "package_name=dev.axmol.fixture\n");
        var project = new ProjectEntry { Name = "fixture", Path = root, Version = "2.11.5", Platform = "windows-x64" };
        var engine = new EngineEntry("2.11.5", Path.Combine(root, "engine"));
        var target = BuildTargets.Get(project.Platform);
        var provider = new ModelProvider { Id = "fixture", Name = "Fixture", IsCustom = true, BaseUrl = "https://offline.invalid/v1" };
        var catalog = new EngineReleases(root, Path.GetFullPath("manifests"));
        var cases = new (string Name, Func<Task<bool>> Open)[]
        {
            ("Prompt", async () => await PromptWindow.ShowAsync(shell, "Fixture") is null),
            ("BuildTarget", BuildTargetClosed),
            ("EngineVersion", async () => await EngineVersionDialog.PickAsync(shell, catalog, null) is null),
            ("EngineMirror", async () => await EngineMirrorDialog.PickAsync(shell, engine,
                [new MirrorOption("github", "", "fixture")]) is null),
            ("EngineBuild", async () => await EngineBuildWindow.PickAsync(shell, engine, [target]) is null),
            ("Prebuilt", () => Cancel(new PrebuiltWindow(project, engine, target,
                new PrebuiltAvailability(PrebuiltStatus.NotBuilt, "fixture"), false, null))),
            ("AndroidRelease", () => Cancel(new AndroidReleaseWindow(project, root,
                new ProcessRunner(_ => throw new InvalidOperationException("Cancellation must not start a process."))))),
            ("Auth", async () => await AuthDialog.ShowAsync(shell, provider) is null),
            ("DeviceAuth", DeviceClosed),
        };
        var observations = new List<string>();
        var allCancelled = true;
        foreach (var sample in cases)
        {
            var before = desktop.Windows.ToArray();
            Window? window = null;
            try
            {
                var result = sample.Open();
                window = desktop.Windows.Except(before).SingleOrDefault();
                window?.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                var plate = window is null ? null : NamedDescendant<Button>(window, "DialogCloseButton");
                var chrome = window is null ? null : NamedDescendant<Border>(window, "DialogChrome");
                var drag = window is null ? null : NamedDescendant<Border>(window, "DialogDragArea");
                var caption = window is null ? null : NamedDescendant<TextBlock>(window, "DialogTitle")?.Text;
                var wired = plate is not null && chrome is not null && drag is not null && caption == window!.Title;
                plate?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var completed = await Task.WhenAny(result, Task.Delay(500)) == result;
                var confirmed = completed && await result;
                var cancelled = wired && completed && confirmed && !window!.IsVisible;
                allCancelled &= cancelled;
                observations.Add(cancelled ? sample.Name + ":True"
                    : sample.Name + ":False（plate=" + (plate is not null) + ", chrome=" + (chrome is not null)
                      + ", drag=" + (drag is not null) + ", caption=" + (caption is null ? "<null>" : "「" + caption + "」")
                      + ", windowTitle=" + (window?.Title is { } shown ? "「" + shown + "」" : "<null>")
                      + ", settled=" + completed + ", confirmed=" + confirmed + ", visible=" + (window?.IsVisible) + "）");
            }
            catch (Exception ex)
            {
                allCancelled = false;
                observations.Add(sample.Name + ":" + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                foreach (var remaining in desktop.Windows.Except(before).ToArray()) remaining.Close(HubDialogResult.Cancel);
            }
        }
        // One failure: a caller's close plate is disconnected or accidentally confirms an operation.
        check(allCancelled,
            "迁移后的对话框关闭盘接共享标题栏并取消调用方操作（实际 " + string.Join(", ", observations) + "）");

        async Task<bool> BuildTargetClosed()
        {
            var picked = BuildTargetDialog.PickAsync(shell, project, usePrebuilt: true,
                prebuiltState: (_, _, _) => new(new(PrebuiltStatus.Ready, "fixture", "build"), null));
            var dialog = desktop.Windows.OfType<BuildTargetDialog>().Single();
            dialog.UpdateLayout();
            var picker = NamedDescendant<ComboBox>(dialog, "BuildTargetPicker");
            var prebuilt = NamedDescendant<CheckBox>(dialog, "UsePrebuiltLibraries");
            var clean = NamedDescendant<CheckBox>(dialog, "ClearBuildDirectories");
            var choicesCorrect = picker is not null && prebuilt is not null && clean?.IsChecked == false;
            if (choicesCorrect && OperatingSystem.IsWindows())
            {
                var savedTarget = picker!.SelectedItem;
                var visibleOnWindows = prebuilt!.IsEffectivelyVisible && prebuilt.IsChecked == true;
                picker.SelectedItem = BuildTargets.Get("android-arm64");
                var hiddenOnAndroid = !prebuilt.IsEffectivelyVisible;
                picker.SelectedItem = savedTarget;
                choicesCorrect = visibleOnWindows && hiddenOnAndroid && prebuilt.IsEffectivelyVisible && prebuilt.IsChecked == true;
            }
            if (clean is not null) clean.IsChecked = true;
            var result = await picked;
            return choicesCorrect && result == (null, null) && !File.Exists(PrebuiltSettings.PathFor(project));
        }

        async Task<bool> Cancel(Window window) => await window.ShowDialog<HubDialogResult>(shell) == HubDialogResult.Cancel;
        async Task<bool> DeviceClosed()
        {
            using var stop = new CancellationTokenSource();
            var window = new DeviceAuthWindow(new DeviceCodeChallenge("ABCD-EFGH", "https://offline.invalid/device",
                TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow.AddMinutes(1)));
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => { stop.Cancel(); closed.TrySetResult(); };
            window.Show(shell);
            await closed.Task;
            return stop.IsCancellationRequested;
        }
    }
}
