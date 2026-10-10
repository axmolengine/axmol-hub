# Merge and upload all platforms' artifacts under one tag into the same GitHub Release.
# Complementary to Publish.ps1 -Stage Upload (single runtime): this script is the dist stage's orchestration layer —
# from the artifact directory pulled off release-build, it trims each platform's feed one by one and collects the upload list,
# then finishes with a single `gh release create/upload` pass, and reads the asset list back to verify.
# Usage (invoked by dist.yml in CI):
#   ./installer/Publish-All.ps1 -Version 0.2.1 -ArtifactDir ./downloaded `
#     -TargetCommit <sha> -NotesFile ./release-notes.md
# Each platform has its own subdirectory under ArtifactDir, with file names matching build.yml's upload.
param(
    [string]$Version,
    [string]$RepoUrl = 'https://github.com/axmolengine/axmol-hub',
    [string]$ArtifactDir,
    [string]$TargetCommit,
    [string]$NotesFile,
    [switch]$Prerelease
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/GitHubRelease.ps1"
. "$PSScriptRoot/AssetNames.ps1"
$taskRoot = (Resolve-Path "$PSScriptRoot/..").Path
$taskManifest = Get-Content -Raw -LiteralPath "$PSScriptRoot/packaging-manifest.json" | ConvertFrom-Json
$taskPackId = $taskManifest.packId
# Public release filenames stay stable across changes to the Velopack installation identity.
$taskAssetPrefix = $taskManifest.assetPrefix

if (-not $Version) {
    [xml]$taskProduct = Get-Content -LiteralPath "$taskRoot/Directory.Build.props"
    $Version = @($taskProduct.Project.PropertyGroup.Version) | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw 'Version was not supplied and could not be read from Directory.Build.props.' }
if (-not $ArtifactDir) { throw 'ArtifactDir is required (where release-build artifacts were downloaded).' }
if ($NotesFile) {
    if (-not (Test-Path -LiteralPath $NotesFile -PathType Leaf)) { throw "Release notes file not found: $NotesFile" }
    $NotesFile = (Resolve-Path -LiteralPath $NotesFile).Path
}
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'The GitHub CLI (gh) is required.' }
if (-not $env:GH_TOKEN) { throw 'GH_TOKEN is not set.' }

$taskTag = "v$Version"
$taskIsPrerelease = $Prerelease -or $Version.StartsWith('0.', [StringComparison]::Ordinal) -or $Version.Contains('-')
$taskSlug = ($RepoUrl -replace '^https?://[^/]+/', '') -replace '\.git$', ''

# Platform -> (channel=full RID, installer suffix, artifact directory). channel uses the RID so the feed name
# releases.<rid>.json is naturally unique (osx-arm64/osx-x64 no longer collide) and equals the releases.<channel>.json the client looks up.
$taskPlatforms = @(
    @{ Channel = 'win-x64';    Suffix = '.exe';      Dir = 'win-x64' },
    @{ Channel = 'osx-arm64';  Suffix = '.pkg';      Dir = 'osx-arm64' },
    @{ Channel = 'osx-x64';    Suffix = '.pkg';      Dir = 'osx-x64' },
    @{ Channel = 'linux-x64';  Suffix = '.AppImage'; Dir = 'linux-x64' }
)

$taskUpload = @()
foreach ($taskPlat in $taskPlatforms) {
    $taskDir = Join-Path $ArtifactDir $taskPlat.Dir
    if (-not (Test-Path -LiteralPath $taskDir)) {
        Write-Warning "Skipping $($taskPlat.Dir): artifact directory not found."
        continue
    }

    # Installer + checksum: same-channel multi-arch builds under one tag (osx-arm64/osx-x64) don't clash, their file names carry the full runtime.
    $taskSetup = Get-HubAssetName -AssetPrefix $taskAssetPrefix -Version $Version -Runtime $taskPlat.Dir -Extension $taskPlat.Suffix -ReleaseAssetNames
    $taskSetupPath = Join-Path $taskDir $taskSetup
    $taskFeed = Join-Path $taskDir "releases.$($taskPlat.Channel).json"

    if (-not (Test-Path -LiteralPath $taskSetupPath)) {
        throw "Missing installer $taskSetupPath for $($taskPlat.Dir)."
    }
    if (-not (Test-Path -LiteralPath $taskFeed)) {
        throw "Missing release feed $taskFeed for $($taskPlat.Dir)."
    }

    # Trim the feed to this version's entries only (rationale: see the matching comment in Publish.ps1 -Stage Upload).
    $taskParsed = Get-Content -Raw -LiteralPath $taskFeed | ConvertFrom-Json
    $taskKept = @($taskParsed.Assets | Where-Object { $_.Version -eq $Version })
    if (-not $taskKept) { throw "The release feed for $($taskPlat.Dir) has no entry for version $Version." }

    $taskUpload += $taskSetupPath
    $taskUpload += ($taskSetupPath + '.sha256')

    # vpk's nupkg name = {packId}-{version}[-{channel}]-{full|delta}.nupkg; the channel segment is omitted **only when
    # os==Windows and channel is exactly the platform default "win"** (Velopack DefaultName.GetSuggestedReleaseName).
    # We use channel=rid (win-x64/osx-arm64/...), which is not "win", so **all four platforms (win included) carry a -<rid>- segment**.
    # Try the suffixed name first; if not found, fall back to the unsuffixed name.
    # Renaming purpose: unify with the installer as axmol-hub-<ver>-<rid>-<full|delta>.nupkg (prefix: see $taskAssetPrefix).
    # After renaming, the FileName/URL referenced in the feed must be fixed in sync, otherwise the client 404s looking up the nupkg via the feed.
    foreach ($taskNupkg in @('full', 'delta')) {
        $taskCandidates = @(
            (Join-Path $taskDir "$taskPackId-$Version-$($taskPlat.Channel)-$taskNupkg.nupkg"),
            (Join-Path $taskDir "$taskPackId-$Version-$taskNupkg.nupkg")
        ) | Where-Object { Test-Path -LiteralPath $_ }
        if (-not $taskCandidates) { continue }
        $taskNupkgPath = @($taskCandidates)[0]
        $taskOrigName = [System.IO.Path]::GetFileName($taskNupkgPath)
        $taskNewName  = "$taskAssetPrefix-$Version-$($taskPlat.Dir)-$taskNupkg.nupkg"
        $taskTarget = Join-Path $taskDir $taskNewName
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

    # Write the fixed feed back (feed name = releases.<channel>.json = releases.<rid>.json), uploaded together with the installer.
    Set-Content -LiteralPath $taskFeed -Encoding UTF8 -Value ([pscustomobject]@{ Assets = @($taskKept) } | ConvertTo-Json -Depth 6)
    $taskUpload += $taskFeed
}

if (-not $taskUpload) { throw 'No assets collected for upload; is ArtifactDir populated?' }

# Confirm every file exists before uploading.
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
    if ($NotesFile) {
        & gh release edit $taskTag --repo $taskSlug --notes-file $NotesFile
        if ($LASTEXITCODE -ne 0) { throw "gh release edit failed with $LASTEXITCODE." }
    }
} else {
    $taskCreateArgs = @('release', 'create', $taskTag, '--repo', $taskSlug, '--title', "Axmol Hub $Version")
    if ($TargetCommit) { $taskCreateArgs += @('--target', $TargetCommit) }
    if ($taskIsPrerelease) { $taskCreateArgs += '--prerelease' }
    if ($NotesFile) {
        $taskCreateArgs += @('--notes-file', $NotesFile)
    } else {
        $taskCreateArgs += '--generate-notes'
    }
    & gh @taskCreateArgs
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed with $LASTEXITCODE." }
}
Set-GitHubReleasePrerelease -RepoSlug $taskSlug -Tag $taskTag -Prerelease $taskIsPrerelease
& gh release upload $taskTag --repo $taskSlug --clobber $taskUpload
if ($LASTEXITCODE -ne 0) { throw "gh release upload failed with $LASTEXITCODE." }

# Read the asset list back and confirm every item really landed (the only check that proves the update source is usable).
$taskAssets = @((& gh release view $taskTag --repo $taskSlug --json assets | ConvertFrom-Json).assets | ForEach-Object { $_.name })
foreach ($taskFile in $taskUpload) {
    $taskName = [System.IO.Path]::GetFileName($taskFile)
    if ($taskAssets -notcontains $taskName) { throw "Asset is missing from release $taskTag after upload: $taskName" }
}

[pscustomobject]@{
    Tag = $taskTag
    Version = $Version
    AssetCount = $taskUpload.Count
    Assets = $taskUpload | ForEach-Object { [System.IO.Path]::GetFileName($_) }
} | ConvertTo-Json -Depth 4
