using System.Security.Cryptography;
using System.Text;

namespace AxmolHub.Core;

/// <summary>Hub data may move; authentication always uses the platform user directory.</summary>
public sealed record HubStorageLayout(string DataRoot, string AuthRoot, string KeychainService)
{
    public const string SharedKeychainService = "dev.axmol.hub";

    /// <summary>Legacy source to import once, never a destination for authentication writes.</summary>
    public string? LegacySource { get; init; }

    public static string UserDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AxmolHub");

    public static string DefaultDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".axmolhub");

    public static string LegacyDataRoot => Path.Combine(UserDirectory, "data");

    /// <summary>Only the default data location depends on whether this is an existing install.</summary>
    public static bool UseHomeDataForNewInstall(bool settingsExist, bool legacyDataExists)
        => !settingsExist && !legacyDataExists;

    public static HubStorageLayout Resolve(string dataRoot, string? userDirectory = null)
    {
        var root = Path.GetFullPath(dataRoot);
        var auth = Path.GetFullPath(userDirectory ?? UserDirectory);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var dataPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (Path.TrimEndingDirectorySeparator(auth).Equals(Path.TrimEndingDirectorySeparator(root), comparison)
            || auth.StartsWith(dataPrefix, comparison))
            throw new IOException("The data directory must not contain the authentication directory.");
        return new(root, auth, SharedKeychainService)
        {
            LegacySource = root,
        };
    }

    /// <summary>The old identity, used only as a read-only migration source or an explicitly isolated fixture.</summary>
    public static HubStorageLayout Legacy(string dataRoot)
    {
        var root = Path.GetFullPath(dataRoot);
        var identity = Path.TrimEndingDirectorySeparator(root).Normalize(NormalizationForm.FormC);
        var service = "dev.axmol.hub." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return new(root, root, service);
    }
}
