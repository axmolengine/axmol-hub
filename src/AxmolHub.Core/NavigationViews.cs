namespace AxmolHub.Core;

/// <summary>Sidebar layouts only; choosing one does not change assistant capabilities or sessions.</summary>
public static class NavigationViews
{
    public const string Workbench = "workbench";
    public const string WorkbenchPlus = "workbench-plus";
    public const string GeneralAi = "general-ai";
    public const string Default = WorkbenchPlus;

    public static string Normalize(string? value) => value is Workbench or WorkbenchPlus or GeneralAi ? value : Default;

    public static bool ShowsWorkbench(string value) => Normalize(value) != GeneralAi;
    public static bool ShowsAssistant(string value) => Normalize(value) != Workbench;
}
