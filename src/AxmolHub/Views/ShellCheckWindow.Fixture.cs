using Avalonia.Controls;
using AxmolHub.Core;

namespace AxmolHub;
internal sealed class ShellCheckFixture : IDisposable
{
    internal ShellCheckFixture(Window host)
    {
        Host = host;
    }

    internal Window Host { get; }

    private MainWindow? _shell;
    internal MainWindow OpenShell(string root)
    {
        PrepareDataRoot(root);
        return _shell = NewShell(root);
    }

    public void Dispose()
    {
        _shell?.Close();
    }

    internal static string PreferencesPathFor(string dataRoot) => System.IO.Path.Combine(dataRoot, "hub-settings.json");
    internal static MainWindow NewShell(string dataRoot)
        => new(dataRoot, new PreferencesStore(PreferencesPathFor(dataRoot)), new HubPreferences(), isolatedAuthForChecks: true);
    internal static void PrepareDataRoot(string scratchRoot)
    {
        var store = new StateStore(scratchRoot);
        store.Save(new HubState
        {
            Engines =
            {
                new EngineEntry("2.11.5", System.IO.Path.Combine(scratchRoot, "engines/axmol-2.11.5")),
                new EngineEntry("2.11.6", System.IO.Path.Combine(scratchRoot, "engines/axmol-2.11.6"), "official-lts"),
            },
            DefaultEnginePath = System.IO.Path.Combine(scratchRoot, "engines/axmol-2.11.5"),
        });
    }
}
