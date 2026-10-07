param(
    [string]$Runtime = 'win-x64',
    # Two stages so the CI can gate the upload on the install acceptance test:
    # Build produces the packages locally, Upload publishes them.
    [ValidateSet('All', 'Build', 'Upload')]
    [string]$Stage = 'All',
    [string]$RepoUrl = 'https://github.com/axmolengine/axmol-hub',
    [string]$Version,
    [string]$Channel,
    [switch]$PrereleaseBuild
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/GitHubRelease.ps1"
$taskRoot = (Resolve-Path "$PSScriptRoot/..").Path
$taskManifest = Get-Content -Raw -LiteralPath "$PSScriptRoot/packaging-manifest.json" | ConvertFrom-Json
$taskPackId = $taskManifest.packId
# nupkg 上传时的统一命名前缀：由 packId 派生成小写连字符（Axmol.Hub → axmol-hub），与安装包同源。
$taskAssetPrefix = ($taskPackId.ToLowerInvariant() -replace '[^a-z0-9]+', '-').Trim('-')

# Single source of the version: the repository-root Directory.Build.props, same as
# Build.ps1 and Test.ps1 read. There is no second copy anywhere in this pipeline.
if (-not $Version) {
    [xml]$taskProduct = Get-Content -LiteralPath "$taskRoot/Directory.Build.props"
    $Version = @($taskProduct.Project.PropertyGroup.Version) | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw 'Version was not supplied and could not be read from Directory.Build.props.' }
$taskIsPrerelease = $PrereleaseBuild -or $Version.StartsWith('0.', [StringComparison]::Ordinal) -or $Version.Contains('-')

if (-not $Channel) {
    # 与 Build.ps1 保持一致：channel = 完整 RID，feed 名 releases.<rid>.json 即客户端查找名。
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

    & "$PSScriptRoot/Build.ps1" -Runtime $Runtime -Version $Version -OutputDir $taskOutput -NoClean -PrereleaseBuild:$taskIsPrerelease -ReleaseAssetNames
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
    $taskSetup = "axmol-hub-$Version-$Runtime$taskSuffix"
    $taskUpload = @(
        (Join-Path $taskOutput $taskSetup),
        (Join-Path $taskOutput ($taskSetup + '.sha256'))
    )

    # nupkg：vpk 原名 = {packId}-{version}[-{channel}]-{full|delta}.nupkg，channel 段**只在
    # os==Windows 且 channel=="win" 时省略**（Velopack DefaultName.GetSuggestedReleaseName）。这里用
    # channel=RID，故四个平台一律带 -<rid>- 段。上传时改名为与安装包同源的小写连字符
    # axmol-hub-<version>-<runtime>-<type>.nupkg，并同步改 feed 的 FileName，否则客户端按 feed 找包 404。
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

    # 写回裁剪 + 改名后的 feed（feed 名 = releases.<channel>.json = releases.<rid>.json）。
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
