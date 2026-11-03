
internal static class Program
{
    private static async Task Main(string[] args)
    {
        // args[0] is the data root ONLY when it is an actual path. A leading "--" means the caller passed a
        // flag in first position, in which case we fall back to the default so flags never get turned into
        // directory names (that was how "--check-cli-json"/"--check-build-profiles" ended up as empty folders).
        var root = Path.GetFullPath(args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "artifacts/checks");
        Directory.CreateDirectory(root);
        if (args.Contains("--openai-refresh-worker"))
        {
            await OpenAiChecks.RefreshWorkerAsync(root, args[^1]);
            return;
        }
        if (args.Contains("--openai-metadata-worker"))
        {
            await OpenAiChecks.MetadataWorkerAsync(root, args);
            return;
        }
        if (args.Contains("--check-openai-oauth"))
        {
            await OpenAiChecks.RunAsync(root, args.Contains("--native-keychain"), args.Contains("--loopback"));
            return;
        }

        if (args.Contains("--check-ai-plan-tasks"))
        {
            await AiPlanTasksChecks.RunAsync(root, args);
            return;
        }

        if (args.Contains("--check-ai-commit-summary"))
        {
            GitWorkflowEvidenceChecks.Run();
            await AiCommitSummaryChecks.RunAsync(root, args);
            return;
        }

        if (args.Contains("--check-ai-providers"))
        {
            await AiProvidersChecks.RunAsync(root, args);
            return;
        }

        // Copilot: the manifest shapes of device-code sign-in, the polling arithmetic, and parsing of the
        // /models capability catalog. The samples are 7 entries trimmed from a real capture on 2026-10-09
        // (see local/copilot-oauth-probe.md); neither the field names nor the values are invented.
        if (args.Contains("--check-ai-copilot"))
        {
            await AiCopilotChecks.RunAsync(root, args);
            return;
        }

        if (args.Contains("--check-secret-store"))
        {
            await SecretStoreChecks.RunAsync(root, args);
            return;
        }

        if (args.Contains("--check-engine-install-link"))
        {
            await EngineInstallLinkChecks.RunAsync(root, args);
            return;
        }

        if (args.Contains("--check-ai-sessions"))
        {
            await AiSessionsChecks.RunAsync(root, args);
            return;
        }

        if (args.Contains("--check-ai-memory"))
        {
            await AiMemoryChecks.RunAsync(root, args);
            return;
        }

        if (args.Contains("--check-ai-context"))
        {
            await AiContextChecks.RunAsync(root, args);
            return;
        }

        if (args.Contains("--check-ai-workspace"))
        {
            await AiWorkspaceChecks.RunAsync(root, args);
            return;
        }

        if (args.Contains("--check-code-workspace"))
        {
            await CodeWorkspaceChecks.RunAsync(root, args);
            return;
        }

        if (args.Contains("--check-ai-tools"))
        {
            await AiToolsChecks.RunAsync(root, args);
            return;
        }

        if (args.Contains("--check-ai-web-search"))
        {
            await AiWebSearchChecks.RunAsync(root, args);
            return;
        }

        if (args.Contains("--check-ai-tool-policy"))
        {
            await AiToolPolicyChecks.RunAsync(root, args);
            return;
        }

        if (args.Contains("--check-ai-cross-session"))
        {
            await AiCrossSessionChecks.RunAsync(root, args);
            return;
        }

        if (args.Contains("--check-ai-images"))
        {
            await AiImagesChecks.RunAsync(root, args);
            return;
        }

        if (args.Contains("--check-ai-routing"))
        {
            await AiRoutingChecks.RunAsync(root, args);
            return;
        }

        if (args.Contains("--check-release-receipt"))
        {
            await ReleaseReceiptChecks.RunAsync(root, args);
            return;
        }

        if (args.Contains("--check-release-debug-key"))
        {
            await ReleaseDebugKeyChecks.RunAsync(root, args);
            return;
        }

        if (args.Contains("--prepare-release-check"))
        {
            await PrepareReleaseCommand.RunAsync(root, args);
            return;
        }

        if (args.Contains("--check-android-verification"))
        {
            await AndroidVerificationChecks.RunAsync(root, args);
            return;
        }

        // `--prepare-android-verification` retired along with the build delegation: it regenerated
        // `manifests/android-gradle-verification.xml` using Hub's own gradle orchestration (depending on Hub-managed
        // JDK/gradle and project staging). The build is now done by the engine, so that manifest can only be re-produced
        // by the engine's Android flow.
        if (args.Contains("--prepare-packaging"))
        {
            await PreparePackagingCommand.RunAsync(root, args);
            return;
        }

        if (args.Contains("--build-game") || args.Contains("--run-game"))
        {
            await GameCommands.RunAsync(root, args);
            return;
        }

        // `--prepare-windows` / `--install-msvc` (download and install the managed MSVC / Windows SDK) retired with
        // toolchain self-hosting: preparing the environment now means running the engine's own setup.ps1, see
        // --install-tools below.
        if (args.Contains("--install-tools"))
        {
            await InstallToolsCommand.RunAsync(root, args);
            return;
        }

        if (args.Contains("--install-engine"))
        {
            await InstallEngineCommand.RunAsync(root, args);
            return;
        }

        // ---------------------------------------------------------------------------
        // Download contract: atomic landing, cache reuse, bad-hash rejection, resume, streaming SHA-256.
        // Self-contained return, host-independent, no network (FixtureHandler injected), so it runs in CI.
        // ---------------------------------------------------------------------------
        if (args.Contains("--check-download"))
        {
            await DownloadChecks.RunAsync(root, args);
            return;
        }

        // ---------------------------------------------------------------------------
        // Host PowerShell 7: the probe's decision rules plus the decisions of the three install paths. Self-contained
        // return, no network (FixtureHandler injected), no child processes, no changes to the host — so CI can run it.
        // A "real install once" would change the whole machine, the category OpsCheck skips; the assertions here only
        // guarantee the **verdict** is right: the hand that installs onto the host must first be provable offline as
        // never guessing.
        // ---------------------------------------------------------------------------
        if (args.Contains("--check-host-shell"))
        {
            await HostShellChecks.RunAsync(root, args);
            return;
        }

        // ---------------------------------------------------------------------------
        // Compute and print, read-only, how installing would work on this machine: no downloads, no elevation, nothing
        // written at all. It needs the network, so like --install-tools it only runs on a dev machine, never in CI.
        // ---------------------------------------------------------------------------
        if (args.Contains("--pwsh-release-report"))
        {
            await PwshReleaseReportChecks.RunAsync(root, args);
            return;
        }

        // ---------------------------------------------------------------------------
        // Tool version source of truth + toolchain probe of the engine tree. Self-contained return, host-independent, no
        // network, no installation — so it can run in CI against a real engine tree. The source of truth is the engine's own
        // 1k/build.profiles, and the landing spot is tools/external from the official setup.ps1.
        // ---------------------------------------------------------------------------
        if (args.Contains("--check-engine-upgrade"))
        {
            await EngineUpgradeChecks.RunAsync(root);
            return;
        }

        if (args.Contains("--check-build-profiles"))
        {
            await BuildProfilesChecks.RunAsync(root, args);
            return;
        }

        // ---------------------------------------------------------------------------
        // Prebuilt engine libraries (Windows targets). Self-contained return, host-independent — everything runs on
        // fixtures, so no real engine and no Windows is required, which is why this can go into CI.
        //
        // What is guarded here are the properties **the engine never errors about**: when the directory AX_PREBUILT_DIR
        // points at does not qualify, the engine silently falls back to a source build (AXGameEngineSetup.cmake:22), so
        // "is it usable" has to be judged correctly by Hub.
        // ---------------------------------------------------------------------------
        if (args.Contains("--check-prebuilt"))
        {
            await PrebuiltChecks.RunAsync(root, args);
            return;
        }

        // ---------------------------------------------------------------------------
        // CLI --json contract (docs: docs/cli-json-contract.md). This is an **end-to-end** check: it really starts the CLI
        // process and trusts only stdout. Pure shape assertions can never prove "stdout holds exactly one JSON document" —
        // that is how the real help bug slipped through (help text first, the envelope behind it). So the core assertion is
        // "the whole stdout must be consumed by JsonDocument.Parse", not one character more. Parsing happens in-process,
        // with no jq/python dependency, so behaviour is identical on all three platforms.
        //
        // Placed before the main flow, with its own return: the contract check needs no real engine and no toolchain, so it
        // must run on its own on a clean CI machine (the main flow, by contrast, needs a real engine tree, which CI cannot
        // run today).
        // ---------------------------------------------------------------------------
        if (args.Contains("--check-cli-json"))
        {
            await CliJsonChecks.RunAsync(root, args);
            return;
        }

        await DefaultChecks.RunAsync(root, args);
    }
}
