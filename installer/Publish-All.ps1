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
    [ValidatePattern('\A[0-9a-fA-F]{40}\z')]
    [string]$SigningThumbprint,
    [string]$SignToolPath,
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
if (-not $env:GH_TOKEN -and -not $SigningThumbprint) { throw 'GH_TOKEN is not set.' }
if ($SigningThumbprint -and (-not $IsWindows -or -not $SignToolPath -or $TargetCommit -notmatch '\A[0-9a-f]{40}\z')) {
    throw 'Signed publishing requires Windows, SignTool, and the exact target commit SHA.'
}

$taskTag = "v$Version"
$taskIsPrerelease = $Prerelease -or $Version.StartsWith('0.', [StringComparison]::Ordinal) -or $Version.Contains('-')
$taskSlug = ($RepoUrl -replace '^https?://[^/]+/', '') -replace '\.git$', ''

# Platform -> (channel=full RID, installer suffix, artifact directory). channel uses the RID so the feed name
# releases.<rid>.json is naturally unique (osx-arm64/osx-x64 no longer collide) and equals the releases.<channel>.json the client looks up.
$taskPlatforms = @(
    @{ Channel = 'win-x64';    Suffix = '.exe';      Dir = 'win-x64' },
    @{ Channel = 'win-arm64';  Suffix = '.exe';      Dir = 'win-arm64' },
    @{ Channel = 'linux-arm64'; Suffix = '.AppImage'; Dir = 'linux-arm64' },
    @{ Channel = 'osx-arm64';  Suffix = '.pkg';      Dir = 'osx-arm64' },
    @{ Channel = 'osx-x64';    Suffix = '.pkg';      Dir = 'osx-x64' },
    @{ Channel = 'linux-x64';  Suffix = '.AppImage'; Dir = 'linux-x64' }
)

$taskUpload = @()
foreach ($taskPlat in $taskPlatforms) {
    $taskDir = Join-Path $ArtifactDir $taskPlat.Dir
    if (-not (Test-Path -LiteralPath $taskDir)) {
        if ($SigningThumbprint) { throw "Missing $($taskPlat.Dir) artifacts for signed release." }
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
    $taskFull = @($taskKept | Where-Object Type -EQ Full)
    $taskDelta = @($taskKept | Where-Object Type -EQ Delta)
    if ($taskFull.Count -ne 1 -or $taskDelta.Count -gt 1 -or $taskKept.Count -ne ($taskFull.Count + $taskDelta.Count)) {
        throw "Expected exactly one full entry and at most one delta entry for $Version ($($taskPlat.Dir))."
    }
    $taskChecksum = (Get-Content -Raw -LiteralPath ($taskSetupPath + '.sha256')).TrimEnd()
    $taskSetupHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $taskSetupPath).Hash
    if ($taskChecksum -ine "$taskSetupHash  $taskSetup") { throw "Installer checksum mismatch: $taskSetupPath" }

    $taskUpload += $taskSetupPath
    $taskUpload += ($taskSetupPath + '.sha256')

    # vpk's nupkg name = {packId}-{version}[-{channel}]-{full|delta}.nupkg; the channel segment is omitted **only when
    # os==Windows and channel is exactly the platform default "win"** (Velopack DefaultName.GetSuggestedReleaseName).
    # We use channel=rid (win-x64/osx-arm64/...), which is not "win", so all our release channels carry a -<rid>- segment.
    # A retry may already refer to the public asset name.
    # Renaming purpose: unify with the installer as axmol-hub-<ver>-<rid>-<full|delta>.nupkg (prefix: see $taskAssetPrefix).
    # After renaming, the FileName/URL referenced in the feed must be fixed in sync, otherwise the client 404s looking up the nupkg via the feed.
    foreach ($taskNupkg in @('full', 'delta')) {
        $taskEntry = $taskKept | Where-Object Type -EQ $taskNupkg
        if (-not $taskEntry) { continue }
        $taskOrigName = $taskEntry.FileName
        $taskNewName = "$taskAssetPrefix-$Version-$($taskPlat.Dir)-$taskNupkg.nupkg"
        if ($taskOrigName -cnotin @(
            "$taskPackId-$Version-$($taskPlat.Channel)-$taskNupkg.nupkg", "$taskPackId-$Version-$taskNupkg.nupkg", $taskNewName
        )) { throw "Unexpected $taskNupkg filename in the $($taskPlat.Dir) feed: $taskOrigName" }
        $taskNupkgPath = Join-Path $taskDir $taskOrigName
        if ($taskEntry.PackageId -cne $taskPackId -or
            $taskEntry.Size -ne (Get-Item -LiteralPath $taskNupkgPath).Length -or
            $taskEntry.SHA256 -ine (Get-FileHash -Algorithm SHA256 -LiteralPath $taskNupkgPath).Hash) {
            throw "Package does not match its feed entry: $taskNupkgPath"
        }
        if ($SigningThumbprint -and $taskPlat.Dir -like 'win-*' -and $taskNupkg -eq 'full') {
            & "$PSScriptRoot/Test-WindowsReleaseSignature.ps1" -SetupPath $taskSetupPath -PackagePath $taskNupkgPath -Thumbprint $SigningThumbprint -SignToolPath $SignToolPath
        }
        $taskTarget = Join-Path $taskDir $taskNewName
        if ($taskOrigName -cne $taskNewName) { Copy-Item -LiteralPath $taskNupkgPath -Destination $taskTarget -Force }
        $taskUpload += $taskTarget
        $taskEntry.FileName = $taskNewName
        if ($taskEntry.URL) { $taskEntry.URL = $taskEntry.URL -replace [regex]::Escape($taskOrigName), $taskNewName }
        if ($taskEntry.DownloadUrl) { $taskEntry.DownloadUrl = $taskEntry.DownloadUrl -replace [regex]::Escape($taskOrigName), $taskNewName }
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

function Assert-ReleaseTagTarget {
    # An existing tag takes precedence over --target, so check it independently of the draft.
    $tagRef = "refs/tags/$taskTag"
    $refsJson = & gh api "repos/$taskSlug/git/matching-refs/tags/$taskTag"
    if ($LASTEXITCODE -ne 0) { throw "Could not check the target of $taskTag." }
    $target = ($refsJson | ConvertFrom-Json) | Where-Object ref -CEQ $tagRef | Select-Object -ExpandProperty object
    if (-not $target) { return }
    while ($target.type -eq 'tag') {
        $tagJson = & gh api "repos/$taskSlug/git/tags/$($target.sha)"
        if ($LASTEXITCODE -ne 0) { throw "Could not dereference $taskTag." }
        $target = ($tagJson | ConvertFrom-Json).object
    }
    if ($target.type -ne 'commit' -or $target.sha -ne $TargetCommit) { throw "$taskTag does not point to $TargetCommit." }
}

if ($SigningThumbprint) { Assert-ReleaseTagTarget }
$taskReleaseJson = & gh release view $taskTag --repo $taskSlug --json isDraft,targetCommitish 2>$null
$taskExists = $LASTEXITCODE -eq 0
if ($taskExists) {
    if ($SigningThumbprint) {
        $taskRelease = $taskReleaseJson | ConvertFrom-Json
        if (-not $taskRelease.isDraft -or $taskRelease.targetCommitish -ne $TargetCommit) {
            throw "Refusing to overwrite ${taskTag}: it is already public or targets another commit."
        }
    }
    Write-Warning "Release $taskTag already exists; uploading into it."
    if ($NotesFile) {
        & gh release edit $taskTag --repo $taskSlug --notes-file $NotesFile
        if ($LASTEXITCODE -ne 0) { throw "gh release edit failed with $LASTEXITCODE." }
    }
} else {
    $taskCreateArgs = @('release', 'create', $taskTag, '--repo', $taskSlug, '--title', "Axmol Hub $Version")
    if ($TargetCommit) { $taskCreateArgs += @('--target', $TargetCommit) }
    if ($SigningThumbprint) { $taskCreateArgs += '--draft' }
    if ($taskIsPrerelease) { $taskCreateArgs += '--prerelease' }
    if ($NotesFile) {
        $taskCreateArgs += @('--notes-file', $NotesFile)
    } else {
        $taskCreateArgs += '--generate-notes'
    }
    & gh @taskCreateArgs
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed with $LASTEXITCODE." }
}
if (-not $SigningThumbprint) { Set-GitHubReleasePrerelease -RepoSlug $taskSlug -Tag $taskTag -Prerelease $taskIsPrerelease }
& gh release upload $taskTag --repo $taskSlug --clobber $taskUpload
if ($LASTEXITCODE -ne 0) { throw "gh release upload failed with $LASTEXITCODE." }

$taskReleaseJson = & gh release view $taskTag --repo $taskSlug --json assets,isDraft,targetCommitish
if ($LASTEXITCODE -ne 0) { throw "Could not read back release $taskTag after upload." }
$taskRelease = $taskReleaseJson | ConvertFrom-Json
$taskAssets = @($taskRelease.assets)
foreach ($taskFile in $taskUpload) {
    $taskItem = Get-Item -LiteralPath $taskFile
    $taskUploaded = @($taskAssets | Where-Object name -CEQ $taskItem.Name)
    if ($taskUploaded.Count -ne 1 -or $taskUploaded[0].size -ne $taskItem.Length -or $taskUploaded[0].state -ne 'uploaded') {
        throw "Missing or incomplete asset in release ${taskTag}: $($taskItem.Name)"
    }
}
if ($SigningThumbprint) {
    if (-not $taskRelease.isDraft -or $taskRelease.targetCommitish -ne $TargetCommit -or $taskAssets.Count -ne $taskUpload.Count) {
        throw "Release $taskTag is no longer the expected draft or contains unexpected assets."
    }
    Assert-ReleaseTagTarget
    $taskPreFlag = "--prerelease=$($taskIsPrerelease.ToString().ToLowerInvariant())"
    & gh release edit $taskTag --repo $taskSlug --draft=false $taskPreFlag
    if ($LASTEXITCODE -ne 0) { throw "Could not publish draft $taskTag." }
}

[pscustomobject]@{
    Tag = $taskTag
    Version = $Version
    AssetCount = $taskUpload.Count
    Assets = $taskUpload | ForEach-Object { [System.IO.Path]::GetFileName($_) }
} | ConvertTo-Json -Depth 4
