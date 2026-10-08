using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// Android release (signing) settings. A port of WPF's <c>AndroidReleaseWindow.cs</c>.
///
/// Two constraints copied from the WPF version: the configuration is saved **per project** in
/// <c>.axmol-hub.android-release.json</c>; passwords stay **only in the current session** and are
/// never persisted — so they must be re-entered every time, deliberately.
/// </summary>
public sealed class AndroidReleaseWindow : Window
{
    public AndroidSigningPasswords? Passwords { get; private set; }

    private readonly TextBox _keystore;
    private readonly TextBox _storePassword;
    private readonly TextBox _keyPassword;
    private readonly Button _browse;
    private readonly Button _create;
    private readonly Button _save;
    private readonly CancellationTokenSource _stop = new();
    private readonly ProjectEntry _project;
    private readonly string _toolsRoot;
    private readonly ProcessRunner _runner;

    private static string L(string chinese, string english) => HubStrings.Language == HubTexts.ChineseLanguage ? chinese : english;

    public AndroidReleaseWindow(ProjectEntry project, string toolsRoot, ProcessRunner runner, AndroidSigningPasswords? previous = null)
    {
        _project = project;
        _toolsRoot = toolsRoot;
        _runner = runner;

        Title = L("Android 发行设置", "Android release settings");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var current = AndroidReleaseSettings.Load(project)
                      ?? new AndroidReleaseSettings { ApplicationId = AndroidPackageService.PackageName(project), KeyAlias = "upload" };

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = Title, FontSize = 20, Margin = new Thickness(0, 0, 0, 12) });

        TextBox Field(string title, string value)
        {
            panel.Children.Add(new TextBlock { Text = title });
            var field = new TextBox { Text = value, Margin = new Thickness(0, 5, 0, 10) };
            panel.Children.Add(field);
            return field;
        }

        var application = Field(L("应用包名（Application ID）", "Application ID"), current.ApplicationId);
        var code = Field(L("版本代码（每次发布递增）", "Version code (increase for each release)"), current.VersionCode.ToString());
        var version = Field(L("版本名称", "Version name"), current.VersionName);
        _keystore = Field(L("密钥库路径", "Keystore path"), current.KeystorePath);
        var alias = Field(L("密钥别名", "Key alias"), current.KeyAlias);

        panel.Children.Add(new TextBlock { Text = L("密钥库密码", "Keystore password") });
        _storePassword = new TextBox { Text = previous?.StorePassword ?? "", PasswordChar = '•', Classes = { "password" }, Margin = new Thickness(0, 5, 0, 10) };
        panel.Children.Add(_storePassword);

        panel.Children.Add(new TextBlock { Text = L("私钥密码（空白时同密钥库密码）", "Key password (blank uses keystore password)") });
        _keyPassword = new TextBox { Text = previous?.KeyPassword ?? "", PasswordChar = '•', Classes = { "password" }, Margin = new Thickness(0, 5, 0, 12) };
        panel.Children.Add(_keyPassword);

        panel.Children.Add(new TextBlock
        {
            Text = L("配置按项目保存；密码仅在当前会话使用。请自行备份密钥库与密码，更新已发布应用需要相应签名密钥。",
                     "Settings are saved per project; passwords stay in this session. Back up your keystore and passwords: updates require the appropriate signing key."),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        var buttons = new WrapPanel();
        _browse = new Button { Content = L("选择密钥库", "Choose keystore") };
        _browse.Click += async (_, _) =>
        {
            var result = await Pickers.PickFileAsync(this, Title!,
                [new FilePickerFileType(L("密钥库", "Keystore")) { Patterns = ["*.jks", "*.keystore", "*.p12", "*.pfx"] }]);
            if (result.Outcome == PickOutcome.Picked)
            {
                _keystore.Text = result.Path!;
            }
        };

        _create = new Button { Content = L("新建密钥库", "Create keystore") };
        _save = new Button { Content = L("验证并保存", "Verify and save"), Classes = { "primary" } };
        var cancel = new Button { Content = HubStrings.Get("Cancel"), IsCancel = true };

        AndroidReleaseSettings Read() => new()
        {
            ApplicationId = application.Text?.Trim() ?? "",
            VersionCode = int.TryParse(code.Text, out var number) ? number : 0,
            VersionName = version.Text?.Trim() ?? "",
            KeystorePath = _keystore.Text?.Trim() ?? "",
            KeyAlias = alias.Text?.Trim() ?? "",
        };

        AndroidSigningPasswords ReadPasswords() =>
            new(_storePassword.Text ?? "", (_keyPassword.Text ?? "").Length == 0 ? _storePassword.Text ?? "" : _keyPassword.Text ?? "");

        Closed += (_, _) => _stop.Cancel();

        async Task Verify(bool newKey)
        {
            try
            {
                if (newKey)
                {
                    var result = await Pickers.SaveFileAsync(this, Title!, "upload.jks",
                        [new FilePickerFileType("JKS keystore") { Patterns = ["*.jks"] }]);
                    if (result.Outcome != PickOutcome.Picked)
                    {
                        return;
                    }

                    _keystore.Text = result.Path!;
                }

                _create.IsEnabled = _save.IsEnabled = _browse.IsEnabled = false;
                var settings = Read();
                var passwords = ReadPasswords();
                var environment = new PlatformBuildService(_runner)
                    .CreateEnvironment(_toolsRoot, BuildTargets.Get("android-arm64"));
                foreach (var name in new[] { "HOME", "TEMP" })
                {
                    Directory.CreateDirectory(environment[name]);
                }

                var signing = new AndroidSigningService(_runner, _toolsRoot);
                if (newKey)
                {
                    await signing.CreateAsync(settings, passwords, environment, _stop.Token);
                }

                await signing.ValidateAsync(settings, passwords, environment, _stop.Token);

                if (newKey)
                {
                    await HubDialog.ShowAsync(this, Title!, L("密钥库已创建，请备份。点击验证并保存完成设置。", "Keystore created. Back it up, then verify and save the settings."));
                    return;
                }

                settings.Save(_project);
                Passwords = passwords;
                Close(HubDialogResult.Ok);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (IsVisible)
                {
                    await HubDialog.ShowAsync(this, Title!, ex.Message);
                }
            }
            finally
            {
                _create.IsEnabled = _save.IsEnabled = _browse.IsEnabled = true;
            }
        }

        _create.Click += async (_, _) => await Verify(true);
        _save.Click += async (_, _) => await Verify(false);
        cancel.Click += (_, _) => Close(HubDialogResult.Cancel);

        buttons.Children.Add(_browse);
        buttons.Children.Add(_create);
        buttons.Children.Add(_save);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
