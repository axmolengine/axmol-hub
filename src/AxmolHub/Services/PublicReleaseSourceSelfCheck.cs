using System.Net;
using System.Net.Http;
using System.Text.Json;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;

namespace AxmolHub;

/// <summary>Offline transport checks shared by the shell verification and headless probes.</summary>
internal static class PublicReleaseSourceSelfCheck
{
    public static async Task Run(Action<bool, string> check, string scratchRoot)
    {
        const string repository = PublicReleaseSource.RepositoryUrl;
        const string id = "dev.axmol.hubapp";
        var log = new NullVelopackLogger();
        Directory.CreateDirectory(scratchRoot);

        static string Feed(string rid, string version, params string[] types) => JsonSerializer.Serialize(new
        {
            Assets = (types.Length == 0 ? ["Full"] : types).Select(type => new
            {
                PackageId = id, Version = version, Type = type,
                FileName = $"axmol-hub-{version}-{rid}-{type.ToLowerInvariant()}.nupkg",
                SHA1 = new string('A', 40), SHA256 = new string('B', 64), Size = 3
            })
        });
        static string Atom(params string[] tags) =>
            "<feed xmlns=\"http://www.w3.org/2005/Atom\">" + string.Concat(tags.Select(tag =>
                $"<entry><title>Title is not release classification</title><link rel=\"alternate\" href=\"{repository}/releases/tag/{tag}\" /></entry>")) + "</feed>";
        static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK)
            => new(status) { Content = new StringContent(body) };

        var ridFailures = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void RecordRid(string claim, bool passed, string rid)
        {
            if (passed) return;
            if (!ridFailures.TryGetValue(claim, out var failures))
                ridFailures.Add(claim, failures = []);
            failures.Add(rid);
        }

        string[] rids = ["osx-arm64", "osx-x64", "win-x64", "win-arm64", "linux-x64"];
        foreach (var rid in rids)
        {
            var requests = new List<string>();
            using var client = new HttpClient(new Handler(request =>
            {
                var url = request.RequestUri!.AbsoluteUri;
                requests.Add(url);
                if (url.EndsWith("releases.atom")) return Reply(Atom("api-index-v1", "v0.12.3", "v0.12.2", "v0.12.1"));
                if (url.EndsWith($"v0.12.3/releases.{rid}.json")) return Reply(Feed(rid, "0.12.3", "Full", "Delta"));
                if (url.EndsWith($"v0.12.2/releases.{rid}.json")) return Reply(Feed(rid, "0.12.2"));
                if (url.EndsWith($"v0.12.1/releases.{rid}.json")) return Reply("", HttpStatusCode.NotFound);
                if (url.EndsWith(".nupkg")) return Reply("pkg");
                throw new InvalidOperationException("Unexpected request: " + url);
            }));
            var source = new PublicReleaseSource(true, client);
            var feed = await source.GetReleaseFeed(log, id, rid);
            RecordRid("RID feeds remain isolated and missing history is skipped",
                feed.Assets.Length == 3 && feed.Assets.All(asset => asset.FileName.Contains(rid)), rid);

            // A fresh source must still download assets from the exact release that supplied them.
            var downloadSource = new PublicReleaseSource(true, client);
            foreach (var asset in feed.Assets)
            {
                var progress = new List<int>();
                var file = Path.Combine(scratchRoot, asset.FileName);
                await downloadSource.DownloadReleaseEntry(log, asset, file, progress.Add, CancellationToken.None);
                RecordRid("full and delta packages download from their originating release",
                    await File.ReadAllTextAsync(file) == "pkg" && progress.Last() == 100
                    && requests.Last() == $"{repository}/releases/download/v{asset.Version}/{asset.FileName}", rid);
            }
            RecordRid("discovery and downloads use only public release URLs",
                requests.All(url => url.StartsWith(repository + "/", StringComparison.Ordinal)), rid);

            var locator = new TestVelopackLocator(id, "0.12.2", scratchRoot, scratchRoot, scratchRoot,
                Path.Combine(scratchRoot, "unused-updater"), rid, localPackage: new VelopackAsset
                { PackageId = id, Version = SemanticVersion.Parse("0.12.2"), Type = VelopackAssetType.Full,
                    FileName = "local-full.nupkg", Size = 3 });
            var manager = new UpdateManager(source, new UpdateOptions(), locator);
            var update = await manager.CheckForUpdatesAsync();
            RecordRid("Velopack selects the next release's delta when a local base is present",
                update?.TargetFullRelease.Version.ToString() == "0.12.3"
                && update.BaseRelease?.FileName == "local-full.nupkg"
                && update.DeltasToTarget.SingleOrDefault()?.FileName == $"axmol-hub-0.12.3-{rid}-delta.nupkg", rid);
            var currentLocator = new TestVelopackLocator(id, "0.12.3", scratchRoot, scratchRoot, scratchRoot,
                Path.Combine(scratchRoot, "unused-updater"), rid, localPackage: feed.Assets[0]);
            RecordRid("an equal installed version is reported up to date",
                await new UpdateManager(source, new UpdateOptions(), currentLocator).CheckForUpdatesAsync() is null, rid);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var wasCancelled = false;
            try
            {
                await downloadSource.DownloadReleaseEntry(log, feed.Assets[0],
                    Path.Combine(scratchRoot, "cancelled.nupkg"), _ => { }, cancellation.Token);
            }
            catch (OperationCanceledException) { wasCancelled = true; }
            RecordRid("package download cancellation is respected", wasCancelled, rid);
        }

        foreach (var claim in new[]
                 {
                     "RID feeds remain isolated and missing history is skipped",
                     "full and delta packages download from their originating release",
                     "discovery and downloads use only public release URLs",
                     "Velopack selects the next release's delta when a local base is present",
                     "an equal installed version is reported up to date",
                     "package download cancellation is respected"
                 })
        {
            var failures = ridFailures.GetValueOrDefault(claim) ?? [];
            check(failures.Count == 0,
                claim + " across " + string.Join(", ", rids)
                + (failures.Count == 0 ? string.Empty : " (failed: " + string.Join(", ", failures) + ")"));
        }

        var stableRequests = new List<string>();
        using (var stableClient = new HttpClient(new Handler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            stableRequests.Add(url);
            if (url.EndsWith("/latest"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                { RequestMessage = new HttpRequestMessage(HttpMethod.Get, repository + "/releases/tag/v1.2.0") };
            if (url.EndsWith("v1.2.0/releases.osx-arm64.json")) return Reply(Feed("osx-arm64", "1.2.0"));
            if (url.EndsWith(".nupkg")) return Reply("pkg");
            throw new InvalidOperationException("Unexpected stable request: " + url);
        })))
        {
            var source = new PublicReleaseSource(false, stableClient);
            var stable = await source.GetReleaseFeed(log, id, "osx-arm64");
            await source.DownloadReleaseEntry(log, stable.Assets.Single(), Path.Combine(scratchRoot, "stable.nupkg"),
                _ => { }, CancellationToken.None);
            check(stableRequests.Count == 3 && stableRequests[0].EndsWith("/latest")
                && stableRequests.Skip(1).All(url => url.Contains("/download/v1.2.0/")),
                "Stable resolves GitHub Latest once and pins manifest and package to that tag");
        }

        var negativeScenarios = new[] { "missing", "broken", "empty", "network", "forbidden", "discovery", "foreign" };
        var unexpectedlyAccepted = new List<string>();
        var missingDiagnosticAddress = new List<string>();
        foreach (var scenario in negativeScenarios)
        {
            using var client = new HttpClient(new Handler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("releases.atom"))
                    return Reply(scenario == "discovery" ? "not xml" : Atom("v0.12.3"));
                return scenario switch
                {
                    "missing" => Reply("", HttpStatusCode.NotFound),
                    "broken" => Reply("not json"),
                    "empty" => Reply("{\"Assets\":[]}"),
                    "network" => throw new HttpRequestException("Offline fixture"),
                    "forbidden" => Reply("Forbidden", HttpStatusCode.Forbidden),
                    "foreign" => Reply(Feed("osx-arm64", "0.12.3").Replace(id, "other.app")),
                    _ => throw new InvalidOperationException("Unexpected fixture request")
                };
            }));
            try
            {
                await new PublicReleaseSource(true, client).GetReleaseFeed(log, id, "osx-arm64");
                unexpectedlyAccepted.Add(scenario);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                if (!ex.ToString().Contains(repository)) missingDiagnosticAddress.Add(scenario);
            }
        }
        check(unexpectedlyAccepted.Count == 0,
            "Unavailable or corrupt release data fails closed across " + string.Join(", ", negativeScenarios)
            + (unexpectedlyAccepted.Count == 0 ? string.Empty : " (accepted: " + string.Join(", ", unexpectedlyAccepted) + ")"));
        check(missingDiagnosticAddress.Count == 0,
            "Failed updates include the diagnostic address across " + string.Join(", ", negativeScenarios)
            + (missingDiagnosticAddress.Count == 0
                ? string.Empty : " (missing: " + string.Join(", ", missingDiagnosticAddress) + ")"));

        using var unusedClient = new HttpClient(new Handler(_ => throw new InvalidOperationException("No request expected")));
        var refused = false;
        // The transport refuses assets that did not originate in a manifest.
        try
        {
            await new PublicReleaseSource(true, unusedClient).DownloadReleaseEntry(log,
                new VelopackAsset(), Path.Combine(scratchRoot, "untrusted.nupkg"), _ => { }, CancellationToken.None);
        }
        catch (ArgumentException) { refused = true; }
        check(refused, "Downloads refuse packages without a release origin");
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = reply(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
