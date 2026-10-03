# 把一个 tag 下所有平台的产物合并上传到同一个 GitHub Release。
# 与 Publish.ps1 -Stage Upload（单 runtime）互补：本脚本是 dist 阶段的编排层，
# 从 release-build 拉下来的产物目录里，按平台逐一裁剪 feed 并收集上传清单，
# 最后 `gh release create/upload` 一次完成，再回读资产清单校验。
# 用法（CI 里由 release-dist.yml 调用）：
#   ./installer/Publish-All.ps1 -Version 0.2.1 -ArtifactDir ./downloaded
# 其中 ArtifactDir 下每个平台一个子目录，文件名与 release-build.yml 的 upload 一致。
param(
    [string]$Version,
    [string]$RepoUrl = 'https://github.com/axmolengine/axmol-hub',
    [string]$ArtifactDir
)
$ErrorActionPreference = 'Stop'
$taskRoot = (Resolve-Path "$PSScriptRoot/..").Path
$taskManifest = Get-Content -Raw -LiteralPath "$PSScriptRoot/packaging-manifest.json" | ConvertFrom-Json
$taskPackId = $taskManifest.packId

if (-not $Version) {
    [xml]$taskProduct = Get-Content -LiteralPath "$taskRoot/Directory.Build.props"
    $Version = @($taskProduct.Project.PropertyGroup.Version) | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw 'Version was not supplied and could not be read from Directory.Build.props.' }
if (-not $ArtifactDir) { throw 'ArtifactDir is required (where release-build artifacts were downloaded).' }
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'The GitHub CLI (gh) is required.' }
if (-not $env:GH_TOKEN) { throw 'GH_TOKEN is not set.' }

$taskTag = "v$Version"
$taskSlug = ($RepoUrl -replace '^https?://[^/]+/', '') -replace '\.git$', ''

# 平台 → (channel, 安装包后缀)。与 Build.ps1 的改名、release-build.yml 的 upload glob 保持一致。
$taskPlatforms = @(
    @{ Channel = 'win';   Suffix = '.exe';      Dir = 'win-x64' },
    @{ Channel = 'osx';   Suffix = '.pkg';      Dir = 'osx-arm64' },
    @{ Channel = 'osx';   Suffix = '.pkg';      Dir = 'osx-x64' },
    @{ Channel = 'linux'; Suffix = '.AppImage'; Dir = 'linux-x64' }
)

$taskUpload = @()
foreach ($taskPlat in $taskPlatforms) {
    $taskDir = Join-Path $ArtifactDir $taskPlat.Dir
    if (-not (Test-Path -LiteralPath $taskDir)) {
        Write-Warning "Skipping $($taskPlat.Dir): artifact directory not found."
        continue
    }

    # 安装包 + 摘要：一个 tag 下同 channel 的多个架构（osx-arm64/osx-x64）文件名带完整 runtime，不冲突。
    $taskSetup = "axmol-hub-$Version-$($taskPlat.Dir)$($taskPlat.Suffix)"
    $taskSetupPath = Join-Path $taskDir $taskSetup
    $taskFeed = Join-Path $taskDir "releases.$($taskPlat.Channel).json"

    if (-not (Test-Path -LiteralPath $taskSetupPath)) {
        throw "Missing installer $taskSetupPath for $($taskPlat.Dir)."
    }
    if (-not (Test-Path -LiteralPath $taskFeed)) {
        throw "Missing release feed $taskFeed for $($taskPlat.Dir)."
    }

    # 裁剪 feed 只留本版条目（理由见 Publish.ps1 -Stage Upload 的同一段注释）。
    $taskParsed = Get-Content -Raw -LiteralPath $taskFeed | ConvertFrom-Json
    $taskKept = @($taskParsed.Assets | Where-Object { $_.Version -eq $Version })
    if (-not $taskKept) { throw "The release feed for $($taskPlat.Dir) has no entry for version $Version." }
    Set-Content -LiteralPath $taskFeed -Encoding UTF8 -Value ([pscustomobject]@{ Assets = @($taskKept) } | ConvertTo-Json -Depth 6)

    $taskUpload += $taskSetupPath
    $taskUpload += ($taskSetupPath + '.sha256')

    # nupkg 按 packId+version 命名，各平台 full/delta 同名但内容不同，必须带平台目录区分——
    # 但上传到 release 页时不能重名，否则互相覆盖。这里把它们改名为带 runtime 前缀的副本。
    foreach ($taskNupkg in @('full', 'delta')) {
        $taskNupkgPath = Join-Path $taskDir "$taskPackId-$Version-$taskNupkg.nupkg"
        if (Test-Path -LiteralPath $taskNupkgPath) {
            $taskTarget = Join-Path $taskDir "$taskPackId-$Version-$($taskPlat.Dir)-$taskNupkg.nupkg"
            Copy-Item -LiteralPath $taskNupkgPath -Destination $taskTarget -Force
            $taskUpload += $taskTarget
        }
    }

    # feed 本身：每个 channel 一个，按 channel 命名避免 osx-arm64/osx-x64 两个 feed 重名。
    $taskFeedTarget = Join-Path $taskDir "releases.$($taskPlat.Channel)-$($taskPlat.Dir).json"
    Copy-Item -LiteralPath $taskFeed -Destination $taskFeedTarget -Force
    $taskUpload += $taskFeedTarget
}

if (-not $taskUpload) { throw 'No assets collected for upload; is ArtifactDir populated?' }

# 上传前逐件确认文件存在。
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

# 回读资产清单，确认每一件都真的在（这是唯一能证明更新源可用的检查）。
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
