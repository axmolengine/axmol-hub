param(
    [string]$Runtime = 'win-x64',
    # Two stages so the CI can gate the upload on the install acceptance test:
    # Build produces the packages locally, Upload publishes them.
    [ValidateSet('All', 'Build', 'Upload')]
    [string]$Stage = 'All',
    [string]$RepoUrl = 'https://github.com/axmolengine/axmol-hub',
    [string]$Version,
    [string]$Channel,
    [string]$OutputDir,
    [string]$SigningThumbprint,
    [string]$TimestampUrl,
    [switch]$PrereleaseBuild
)
$ErrorActionPreference = 'Stop'
$taskSigning = @{}
if ($SigningThumbprint -or $TimestampUrl) {
    if ($Stage -ne 'Build' -or -not $IsWindows -or $Runtime -notlike 'win-*' -or -not $SigningThumbprint -or -not $TimestampUrl) {
        throw 'Pass both signing options with -Stage Build on Windows. Use Release-WindowsSigned.ps1 to publish.'
    }
    $taskSigning = @{ SigningThumbprint = $SigningThumbprint; TimestampUrl = $TimestampUrl }
}
. "$PSScriptRoot/GitHubRelease.ps1"
. "$PSScriptRoot/AssetNames.ps1"
$taskRoot = (Resolve-Path "$PSScriptRoot/..").Path
$taskManifest = Get-Content -Raw -LiteralPath "$PSScriptRoot/packaging-manifest.json" | ConvertFrom-Json
$taskPackId = $taskManifest.packId
# Public release asset names are intentionally independent of the Velopack install identity.
$taskAssetPrefix = $taskManifest.assetPrefix

# Single source of the version: the repository-root Directory.Build.props, same as
# Build.ps1 and Test.ps1 read. There is no second copy anywhere in this pipeline.
if (-not $Version) {
    [xml]$taskProduct = Get-Content -LiteralPath "$taskRoot/Directory.Build.props"
    $Version = @($taskProduct.Project.PropertyGroup.Version) | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw 'Version was not supplied and could not be read from Directory.Build.props.' }
$taskIsPrerelease = $PrereleaseBuild -or $Version.StartsWith('0.', [StringComparison]::Ordinal) -or $Version.Contains('-')

if (-not $Channel) {
    # Keep consistent with Build.ps1: channel = full RID, the feed name releases.<rid>.json is exactly what the client looks up.
    $Channel = $Runtime
}

# The tag check only matters when actually uploading: it's the one place that can catch
# "version bumped, tag forgotten" (the feed, nupkg name and installer name all carry the
# version, while the release page is keyed by the tag). The Build stage runs on any master
# push (GITHUB_REF_NAME=master) and must not be gated by it.
$taskTag = "v$Version"
if ($Stage -eq 'Upload' -and $env:GITHUB_REF_NAME -and $env:GITHUB_REF_NAME -ne $taskTag) {
    throw "Tag '$env:GITHUB_REF_NAME' does not match version $Version (expected '$taskTag')."
}

$taskSlug = ($RepoUrl -replace '^https?://[^/]+/', '') -replace '\.git$', ''
$taskOutput = if ($OutputDir) { $OutputDir } else { Join-Path $taskRoot "artifacts/releases/$Runtime" }
$taskVpk = Join-Path $taskRoot 'cache/packaging-tools/vpk/vpk.exe'
if (-not (Test-Path -LiteralPath $taskVpk)) { $taskVpk = Join-Path $taskRoot 'cache/packaging-tools/vpk/vpk' }

if ($Stage -in @('All', 'Build')) {
    if (-not (Test-Path -LiteralPath $taskVpk)) { throw "Prepare the pinned Velopack CLI $($taskManifest.packages[0].version) first." }
    # Do not consume stale packages or delete caller-owned files on a retry.
    New-Item -ItemType Directory -Force -Path $taskOutput | Out-Null
    if (Get-ChildItem -LiteralPath $taskOutput -Force | Select-Object -First 1) {
        throw "Release output directory must be empty: $taskOutput. Use -OutputDir to choose a fresh directory."
    }

    $taskDownload = @('download', 'github', '--outputDir', $taskOutput, '--channel', $Channel, '--repoUrl', $RepoUrl)
    if ($taskIsPrerelease) { $taskDownload += '--pre' }
    if ($env:GH_TOKEN) { $taskDownload += @('--token', $env:GH_TOKEN) }
    & $taskVpk @taskDownload
    if ($LASTEXITCODE -ne 0) { throw "Previous release lookup/download for $Channel failed (vpk exit $LASTEXITCODE)." }
    # vpk 1.2.161 checks the feed digest before renaming .incomplete to .nupkg, but a checksum
    # mismatch can return exit 0. Do not mistake that for a channel without a previous release.
    if (Get-ChildItem -LiteralPath $taskOutput -File -Filter '*.incomplete') {
        throw "Previous release download or checksum verification failed for $Channel."
    }
    $taskBases = @(Get-ChildItem -LiteralPath $taskOutput -File -Filter '*.nupkg')
    if ($taskBases.Count -gt 1) { throw "Expected at most one previous full package for $Channel." }
    if ($taskBases.Count -eq 0) {
        Write-Host "No previous full release found for $Channel. Building a full-only release."
    } else {
        $taskBase = $taskBases[0]
        $taskZip = [IO.Compression.ZipFile]::OpenRead($taskBase.FullName)
        try {
            $taskSpec = @($taskZip.Entries | Where-Object { $_.FullName -like '*.nuspec' -and $_.FullName -eq $_.Name })
            if ($taskSpec.Count -ne 1) { throw "Missing or ambiguous nuspec in $($taskBase.Name)." }
            $taskReader = [IO.StreamReader]::new($taskSpec[0].Open())
            try { [xml]$taskBaseSpec = $taskReader.ReadToEnd() } finally { $taskReader.Dispose() }
        } finally { $taskZip.Dispose() }
        $taskBaseMetadata = $taskBaseSpec.package.metadata
        if ($taskBaseMetadata.id -ne $taskPackId -or $taskBaseMetadata.rid -ne $Runtime -or $taskBaseMetadata.channel -ne $Channel -or $taskBase.Name -notlike '*-full.nupkg') {
            throw "Previous package $($taskBase.Name) is not a full $taskPackId package for $Runtime ($Channel)."
        }
        if ([semver]$taskBaseMetadata.version -ge [semver]$Version) {
            throw "Previous package version $($taskBaseMetadata.version) is not older than $Version for $Channel."
        }
        Write-Host "Using verified base package $($taskBase.Name) for $Version ($Channel)."
    }

    & "$PSScriptRoot/Build.ps1" -Runtime $Runtime -Version $Version -Channel $Channel -OutputDir $taskOutput -NoClean -PrereleaseBuild:$taskIsPrerelease -ReleaseAssetNames @taskSigning
    if ($LASTEXITCODE -ne 0) { throw 'Build.ps1 failed.' }
    $taskFeed = Join-Path $taskOutput "releases.$Channel.json"
    $taskBuilt = Get-Content -Raw -LiteralPath $taskFeed | ConvertFrom-Json
    $taskExpected = if ($taskBases.Count -eq 0) { @('Full') } else { @('Full', 'Delta') }
    foreach ($taskType in $taskExpected) {
        $taskEntry = @($taskBuilt.Assets | Where-Object { $_.Version -eq $Version -and $_.Type -ieq $taskType })
        if ($taskEntry.Count -ne 1 -or -not (Test-Path -LiteralPath (Join-Path $taskOutput $taskEntry[0].FileName) -PathType Leaf)) {
            throw "The $Channel build did not produce an indexed $taskType package for $Version."
        }
    }
}

if ($Stage -in @('All', 'Upload')) {
    if (-not (Test-Path -LiteralPath $taskOutput)) { throw "Nothing to upload: $taskOutput does not exist. Run -Stage Build first." }
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'The GitHub CLI (gh) is required to publish a release.' }
    if (-not $env:GH_TOKEN) { throw 'GH_TOKEN is not set. In CI pass GITHUB_TOKEN; locally use your own token.' }

    # Keep only this version's entries in the feed. GithubSource merges the feeds of the
    # last 10 releases and then looks up every entry's nupkg *inside the release that
    # feed came from*; an entry pointing at a package that only exists in an older release
    # throws during the update check. Old versions stay reachable through their own release.
    $taskFeed = Join-Path $taskOutput "releases.$Channel.json"
    if (-not (Test-Path -LiteralPath $taskFeed)) { throw "Missing release feed: $taskFeed" }
    $taskParsed = Get-Content -Raw -LiteralPath $taskFeed | ConvertFrom-Json
    $taskKept = @($taskParsed.Assets | Where-Object { $_.Version -eq $Version })
    if (-not $taskKept) { throw "The release feed has no entry for version $Version." }

    # Explicit upload list instead of `vpk upload github`: the installer is renamed, and
    # vpk's own uploader enumerates files from assets.<channel>.json, which still carries
    # the original Setup.exe name. The portable zip is deliberately left out for now.
    # The suffix follows what vpk emits per platform: .exe on Windows, .pkg on macOS and
    # .AppImage on Linux. The build stage uses -ReleaseAssetNames for stable release filenames.
    $taskSuffix = switch -Wildcard ($Runtime) {
        'win-*' { '.exe' }
        'osx-*' { '.pkg' }
        default { '.AppImage' }
    }
    # The name rule is shared with Build.ps1 (AssetNames.ps1). The Upload stage faces release names: the Build stage packs
    # with -ReleaseAssetNames, so the Linux asset carries no version segment.
    $taskSetup = Get-HubAssetName -AssetPrefix $taskAssetPrefix -Version $Version -Runtime $Runtime -Extension $taskSuffix -ReleaseAssetNames
    $taskUpload = @(
        (Join-Path $taskOutput $taskSetup),
        (Join-Path $taskOutput ($taskSetup + '.sha256'))
    )

    # nupkg: vpk's native name = {packId}-{version}[-{channel}]-{full|delta}.nupkg; the channel segment is omitted **only when
    # os==Windows and channel=="win"** (Velopack DefaultName.GetSuggestedReleaseName). Here we use channel=RID, so all four
    # platforms always carry a -<rid>- segment. On upload it is renamed to the lowercase hyphenated form sharing the
    # installer's source, axmol-hub-<version>-<runtime>-<type>.nupkg, and the feed's FileName is updated in lockstep, otherwise the client 404s looking up the package via the feed.
    foreach ($taskNupkg in @('full', 'delta')) {
        $taskCandidates = @(
            (Join-Path $taskOutput "$taskPackId-$Version-$Channel-$taskNupkg.nupkg"),
            (Join-Path $taskOutput "$taskPackId-$Version-$taskNupkg.nupkg")
        ) | Where-Object { Test-Path -LiteralPath $_ }
        if (-not $taskCandidates) { continue }
        $taskNupkgPath = @($taskCandidates)[0]
        $taskOrigName = [System.IO.Path]::GetFileName($taskNupkgPath)
        $taskNewName  = "$taskAssetPrefix-$Version-$Runtime-$taskNupkg.nupkg"
        $taskTarget = Join-Path $taskOutput $taskNewName
        Copy-Item -LiteralPath $taskNupkgPath -Destination $taskTarget -Force
        $taskUpload += $taskTarget
        foreach ($a in $taskKept) {
            if ($a.FileName -eq $taskOrigName) {
                $a.FileName = $taskNewName
                if ($a.URL)         { $a.URL = $a.URL -replace [regex]::Escape($taskOrigName), $taskNewName }
                if ($a.DownloadUrl) { $a.DownloadUrl = $a.DownloadUrl -replace [regex]::Escape($taskOrigName), $taskNewName }
            }
        }
    }

    # Write back the trimmed + renamed feed (feed name = releases.<channel>.json = releases.<rid>.json).
    Set-Content -LiteralPath $taskFeed -Encoding UTF8 -Value ([pscustomobject]@{ Assets = @($taskKept) } | ConvertTo-Json -Depth 6)
    $taskUpload += $taskFeed

    foreach ($taskFile in $taskUpload) {
        if (-not (Test-Path -LiteralPath $taskFile)) { throw "Missing release asset: $taskFile" }
    }

    $taskExists = $false
    try {
        & gh release view $taskTag --repo $taskSlug | Out-Null
        $taskExists = ($LASTEXITCODE -eq 0)
    } catch {
        $taskExists = $false
    }
    if ($taskExists) {
        Write-Warning "Release $taskTag already exists; uploading into it."
    } else {
        $taskCreateArgs = @('release', 'create', $taskTag, '--repo', $taskSlug, '--title', "Axmol Hub $Version", '--generate-notes')
        if ($taskIsPrerelease) { $taskCreateArgs += '--prerelease' }
        & gh @taskCreateArgs
        if ($LASTEXITCODE -ne 0) { throw "gh release create failed with $LASTEXITCODE." }
    }
    Set-GitHubReleasePrerelease -RepoSlug $taskSlug -Tag $taskTag -Prerelease $taskIsPrerelease
    & gh release upload $taskTag --repo $taskSlug --clobber $taskUpload
    if ($LASTEXITCODE -ne 0) { throw "gh release upload failed with $LASTEXITCODE." }

    # The update source is only usable if the feed actually landed as an asset; this is the
    # one check that proves it, so it runs after every publish.
    $taskAssets = @((& gh release view $taskTag --repo $taskSlug --json assets | ConvertFrom-Json).assets | ForEach-Object { $_.name })
    foreach ($taskFile in $taskUpload) {
        $taskName = [System.IO.Path]::GetFileName($taskFile)
        if ($taskAssets -notcontains $taskName) { throw "Asset is missing from release $taskTag after upload: $taskName" }
    }

    [pscustomobject]@{
        Tag = $taskTag
        Version = $Version
        Channel = $Channel
        Runtime = $Runtime
        Delta = [bool](Test-Path -LiteralPath $taskDelta)
        Assets = $taskUpload | ForEach-Object { [System.IO.Path]::GetFileName($_) }
    } | ConvertTo-Json -Depth 4
}
