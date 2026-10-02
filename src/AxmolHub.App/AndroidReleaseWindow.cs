using System.IO;
using System.Windows;
using System.Windows.Controls;
using AxmolHub.Core;
using Microsoft.Win32;

namespace AxmolHub.App;

public sealed class AndroidReleaseWindow : Window
{
    public AndroidSigningPasswords? Passwords { get; private set; }
    private static string L(string chinese, string english) => Texts.Language == "zh-CN" ? chinese : english;
    public AndroidReleaseWindow(Window owner, ProjectEntry project, string toolsRoot, ProcessRunner runner, AndroidSigningPasswords? previous = null)
    {
        Owner = owner; Title = L("Android 发行设置", "Android release settings"); Width = 560;
        SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = owner.Background; Foreground = owner.Foreground;
        FontFamily = owner.FontFamily; FontSize = owner.FontSize;
        var current = AndroidReleaseSettings.Load(project) ?? new() { ApplicationId = AndroidPackageService.PackageName(project), KeyAlias = "upload" };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = Title, FontSize = 20, Margin = new Thickness(0, 0, 0, 12) });
        TextBox Field(string title, string value)
        {
            panel.Children.Add(new TextBlock { Text = title });
            var field = new TextBox { Text = value, Margin = new Thickness(0, 5, 0, 10) }; panel.Children.Add(field); return field;
        }
        var application = Field(L("应用包名（Application ID）", "Application ID"), current.ApplicationId);
        var code = Field(L("版本代码（每次发布递增）", "Version code (increase for each release)"), current.VersionCode.ToString());
        var version = Field(L("版本名称", "Version name"), current.VersionName);
        var keystore = Field(L("密钥库路径", "Keystore path"), current.KeystorePath);
        var alias = Field(L("密钥别名", "Key alias"), current.KeyAlias);
        panel.Children.Add(new TextBlock { Text = L("密钥库密码", "Keystore password") });
        var storePassword = new PasswordBox { Password = previous?.StorePassword ?? "", Margin = new Thickness(0, 5, 0, 10), Padding = new Thickness(8) }; panel.Children.Add(storePassword);
        panel.Children.Add(new TextBlock { Text = L("私钥密码（空白时同密钥库密码）", "Key password (blank uses keystore password)") });
        var keyPassword = new PasswordBox { Password = previous?.KeyPassword ?? "", Margin = new Thickness(0, 5, 0, 12), Padding = new Thickness(8) }; panel.Children.Add(keyPassword);
        panel.Children.Add(new TextBlock { Text = L("配置按项目保存；密码仅在当前会话使用。请自行备份密钥库与密码，更新已发布应用需要相应签名密钥。", "Settings are saved per project; passwords stay in this session. Back up your keystore and passwords: updates require the appropriate signing key."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        var buttons = new WrapPanel();
        var browse = new Button { Content = L("选择密钥库", "Choose keystore") };
        browse.Click += (_, _) => { var picker = new OpenFileDialog { Filter = "Keystore (*.jks;*.keystore;*.p12;*.pfx)|*.jks;*.keystore;*.p12;*.pfx|All files|*.*" }; if (picker.ShowDialog(this) == true) keystore.Text = picker.FileName; };
        var create = new Button { Content = L("新建密钥库", "Create keystore") };
        var save = new Button { Content = L("验证并保存", "Verify and save"), Style = (Style)owner.FindResource("Primary") };
        var cancel = new Button { Content = Texts.Get("Cancel"), IsCancel = true };
        AndroidReleaseSettings Read() => new() { ApplicationId = application.Text.Trim(), VersionCode = int.TryParse(code.Text, out var number) ? number : 0,
            VersionName = version.Text.Trim(), KeystorePath = keystore.Text.Trim(), KeyAlias = alias.Text.Trim() };
        AndroidSigningPasswords ReadPasswords() => new(storePassword.Password, keyPassword.Password.Length == 0 ? storePassword.Password : keyPassword.Password);
        var stop = new CancellationTokenSource(); Closed += (_, _) => stop.Cancel();
        async Task Verify(bool newKey)
        {
            try
            {
                if (newKey)
                {
                    var picker = new SaveFileDialog { Filter = "JKS keystore|*.jks", FileName = "upload.jks" };
                    if (picker.ShowDialog(this) != true) return;
                    keystore.Text = picker.FileName;
                }
                create.IsEnabled = save.IsEnabled = browse.IsEnabled = false;
                var settings = Read(); var passwords = ReadPasswords();
                var environment = new PlatformBuildService(runner, toolsRoot).CreateEnvironment(new(project.Version, project.Path), BuildTargets.Get("android-arm64"));
                foreach (var name in new[] { "HOME", "TEMP" }) Directory.CreateDirectory(environment[name]);
                var signing = new AndroidSigningService(runner, toolsRoot);
                if (newKey) await signing.CreateAsync(settings, passwords, environment, stop.Token);
                await signing.ValidateAsync(settings, passwords, environment, stop.Token);
                if (newKey) { MessageBox.Show(this, L("密钥库已创建，请备份。点击验证并保存完成设置。", "Keystore created. Back it up, then verify and save the settings."), Title); return; }
                settings.Save(project); Passwords = passwords; DialogResult = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (IsVisible) MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { create.IsEnabled = save.IsEnabled = browse.IsEnabled = true; }
        }
        create.Click += async (_, _) => await Verify(true); save.Click += async (_, _) => await Verify(false);
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(browse); buttons.Children.Add(create); buttons.Children.Add(save); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, Background = Background, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        MaxHeight = SystemParameters.WorkArea.Height - 40;
    }
    public void CaptureEvidence(string path)
    {
        UpdateLayout();
        var element = (FrameworkElement)Content;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(element); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
}
