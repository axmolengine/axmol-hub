using AxmolHub.Core;

internal static class EngineInstallLinkChecks
{
    internal static async Task RunAsync(string root, string[] args)
    {
        if (!EngineInstallLink.IsActivation("axmolhub://activate")
            || EngineInstallLink.IsActivation("axmolhub://activate?x=1")
            || EngineInstallLink.IsActivation("axmolhub://install?version=2.11.5&source=github"))
            throw new Exception("The Hub activation link parser accepted an invalid activation route or rejected the exact route.");

        var link = EngineInstallLink.Parse("axmolhub://install?version=2.11.5&source=atomgit");
        if (link.Version != "2.11.5" || link.Source != DownloadSources.AtomGitId)
            throw new Exception("A valid engine install link did not preserve its exact version and source.");
        var encoded = EngineInstallLink.Parse("AXMOLHUB://INSTALL/?version=2.11.5%2Bcustom&source=custom");
        if (encoded.Version != "2.11.5+custom" || encoded.Source != DownloadSources.CustomId)
            throw new Exception("A valid encoded engine install link did not decode its values.");
        var package = new PackageEntry
        {
            Id = "axmol-engine",
            Version = "2.11.5",
            Channel = "official-lts",
            Url = "https://github.com/axmolengine/axmol/releases/download/v2.11.5/engine.zip",
            Sha256 = new string ('a', 64),
        };
        var mirrored = DownloadSources.Apply(package, link.Source, null);
        if (!mirrored.Url.StartsWith("https://atomgit.com/", StringComparison.Ordinal) || mirrored.Sha256 != package.Sha256 || package.Url.StartsWith("https://atomgit.com/", StringComparison.Ordinal))
            throw new Exception("The link's one-time source must rewrite only its package copy and preserve the manifest digest.");
        if (DownloadSources.Validate(DownloadSources.CustomId, null)is null || DownloadSources.Validate(DownloadSources.CustomId, "http://example.test/{version}")is null || DownloadSources.Validate(DownloadSources.CustomId, "https://example.test/releases/{version}")is not null)
            throw new Exception("A custom source must already be configured as an HTTPS template.");
        foreach (var invalid in new[]
        {
            "https://axmol.dev/install?version=2.11.5&source=github",
            "axmolhub://other?version=2.11.5&source=github",
            "axmolhub://install/path?version=2.11.5&source=github",
            "axmolhub://install?version=2.11.5",
            "axmolhub://install?version=2.11.5&source=github&source=atomgit",
            "axmolhub://install?version=2.11.5&source=https%3A%2F%2Fevil.example",
            "axmolhub://install?version=2.11.5&source=github&url=https%3A%2F%2Fevil.example",
            "axmolhub://install?version=2.11.5%ZZ&source=github",
            "axmolhub://install?version=2.11.5&source=github#fragment",
            "axmolhub://user@install?version=2.11.5&source=github",
            "axmolhub://install?version=%202.11.5&source=github",
            new string ('a', 2049),
        }

        )
        {
            try
            {
                EngineInstallLink.Parse(invalid);
                throw new Exception($"An invalid engine install link was accepted: {invalid}");
            }
            catch (FormatException)
            {
            }
        }

        Console.WriteLine("PASS: install links require an exact version and supported source, and reject malformed or injected parameters.");
        return;
    }
}
