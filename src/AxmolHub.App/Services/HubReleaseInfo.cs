namespace AxmolHub.App;

internal static class HubReleaseInfo
{
#if HUB_PRERELEASE_BUILD
    public static bool IsPrereleaseBuild => true;
#else
    public static bool IsPrereleaseBuild => false;
#endif
}
