using Avalonia;
using Avalonia.Controls;
using AxmolHub.Core;

namespace AxmolHub;

internal sealed class WindowsTrayIntegration : IDisposable
{
    private readonly TrayIcon _trayIcon;

    private WindowsTrayIntegration(TrayIcon trayIcon) => _trayIcon = trayIcon;

    internal static WindowsTrayIntegration? Create(Application application, MainWindow window)
    {
        var icon = HubAppIcon.CreateWindowIcon();
        if (icon is null)
        {
            System.Diagnostics.Trace.TraceWarning("The Axmol Hub tray icon could not be loaded.");
            return null;
        }

        var trayIcon = new TrayIcon
        {
            Icon = icon,
            Menu = CreateMenu(window),
            ToolTipText = HubStrings.Get("TrayToolTip")
        };
        trayIcon.Clicked += (_, _) => window.RestoreFromTray();
        TrayIcon.SetIcons(application, new TrayIcons { trayIcon });
        return new WindowsTrayIntegration(trayIcon);
    }

    internal static NativeMenu CreateMenu(MainWindow window)
    {
        var showWindow = new NativeMenuItem(HubStrings.Get("TrayShowWindow"));
        showWindow.Click += (_, _) => window.RestoreFromTray();

        var exit = new NativeMenuItem(HubStrings.Get("TrayExit"));
        exit.Click += (_, _) => window.RequestExit();

        var menu = new NativeMenu();
        menu.Items.Add(showWindow);
        menu.Items.Add(exit);
        menu.Opening += (_, _) =>
        {
            showWindow.Header = HubStrings.Get("TrayShowWindow");
            exit.Header = HubStrings.Get("TrayExit");
        };
        return menu;
    }

    public void Dispose()
    {
        _trayIcon.Dispose();
    }
}
