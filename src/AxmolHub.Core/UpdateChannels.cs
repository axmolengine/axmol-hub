namespace AxmolHub.Core;

public static class UpdateChannels
{
    public const string Stable = "stable";
    public const string Preview = "preview";
    public const string DefaultChannel = Stable;

    public static string[] All => [Stable, Preview];

    public static bool IsSupported(string? channel) => channel is Stable or Preview;

    public static string Normalize(string? channel) => channel switch
    {
        Preview => Preview,
        _ => Stable,
    };

    public static bool IncludesPrereleases(string? channel, bool isPrereleaseBuild)
        => isPrereleaseBuild || Normalize(channel) == Preview;
}
