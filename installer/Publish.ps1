param(
    [string]$Runtime = 'win-x64',
    # Two stages so the CI can gate the upload on the install acceptance test:
    # Build produces the packages locally, Upload publishes them.
    [ValidateSet('All', 'Build', 'Upload')]
    [string]$Stage = 'All',
    [string]$RepoUrl = 'https://github.com/axmolengine/axmol-hub',
    [string]$Version,
    [string]$Channel
)
$ErrorActionPreference = 'Stop'
$taskRoot = (Resolve-Path "$PSScriptRoot/..").Path
$taskManifest = Get-Content -Raw -LiteralPath "$PSScriptRoot/packaging-manifest.json" | ConvertFrom-Json
$taskPackId = $taskManifest.packId

# Single source of the version: the repository-root Directory.Build.props, same as
# Build.ps1 and Test.ps1 read. There is no second copy anywhere in this pipeline.
if (-not $Version) {
    [xml]$taskProduct = Get-Content -LiteralPath "$taskRoot/Directory.Build.props"
    $Version = @($taskProduct.Project.PropertyGroup.Version) | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw 'Version was not supplied and could not be read from Directory.Build.props.' }

if (-not $Channel) {
    $Channel = 'linux'
    if ($Runtime -like 'win-*') { $Channel = 'win' }
    elseif ($Runtime -like 'osx-*') { $Channel = 'osx' }
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
$taskOutput = Join-Path $taskRoot "artifacts/releases/$Runtime"
$taskVpk = Join-Path $taskRoot 'artifacts/packaging-tools/vpk/vpk.exe'
if (-not (Test-Path -LiteralPath $taskVpk)) { $taskVpk = Join-Path $taskRoot 'artifacts/packaging-tools/vpk/vpk' }

if ($Stage -in @('All', 'Build')) {
    if (-not (Test-Path -LiteralPath $taskVpk)) { throw "Prepare the pinned Velopack CLI $($taskManifest.packages[0].version) first." }
    New-Item -ItemType Directory -Force -Path $taskOutput | Out-Null

    # Delta packages require the previous release to sit in the output directory before
    # packing. Missing or empty releases are not an error: the first published version
    # has nothing to diff against and ships as a full package.
    $taskDownload = @('download', 'github', '--outputDir', $taskOutput, '--channel', $Channel, '--repoUrl', $RepoUrl)
    if ($env:GH_TOKEN) { $taskDownload += @('--token', $env:GH_TOKEN) }
    try {
        & $taskVpk @taskDownload
        if ($LASTEXITCODE -ne 0) { throw "vpk download exited with $LASTEXITCODE." }
    } catch {
        Write-Warning "No previous release could be downloaded ($($_.Exception.Message)). This release will be full-only."
    }

    & "$PSScriptRoot/Build.ps1" -Runtime $Runtime -Version $Version -OutputDir $taskOutput -NoClean
    if ($LASTEXITCODE -ne 0) { throw 'Build.ps1 failed.' }
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
    Set-Content -LiteralPath $taskFeed -Encoding UTF8 -Value ([pscustomobject]@{ Assets = @($taskKept) } | ConvertTo-Json -Depth 6)

    # Explicit upload list instead of `vpk upload github`: the installer is renamed, and
    # vpk's own uploader enumerates files from assets.<channel>.json, which still carries
    # the original Setup.exe name. The portable zip is deliberately left out for now.
    # The suffix follows what vpk emits per platform: .exe on Windows, .pkg on macOS and
    # .AppImage on Linux; Build.ps1 renames by extension, so both stay in sync.
    $taskSuffix = switch -Wildcard ($Runtime) {
        'win-*' { '.exe' }
        'osx-*' { '.pkg' }
        default { '.AppImage' }
    }
    $taskSetup = "axmol-hub-$Version-$Runtime$taskSuffix"
    $taskUpload = @(
        (Join-Path $taskOutput $taskSetup),
        (Join-Path $taskOutput ($taskSetup + '.sha256')),
        (Join-Path $taskOutput "$taskPackId-$Version-full.nupkg"),
        $taskFeed
    )
    $taskDelta = Join-Path $taskOutput "$taskPackId-$Version-delta.nupkg"
    if (Test-Path -LiteralPath $taskDelta) { $taskUpload += $taskDelta }
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
        & gh release create $taskTag --repo $taskSlug --title "Axmol Hub $Version" --generate-notes
        if ($LASTEXITCODE -ne 0) { throw "gh release create failed with $LASTEXITCODE." }
    }
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
