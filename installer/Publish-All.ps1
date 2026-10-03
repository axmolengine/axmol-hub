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
# 用户可见的产物命名统一走这一份前缀：从 packId（Axmol.Hub）派生成小写连字符（axmol-hub），
# 与安装包 axmol-hub-<version>-<rid>.{ext} 同源。nupkg 也用它，feed 里的 FileName 随之改写，
# 保证「feed 引用名 == release 上实际资产名」，客户端下载才不会 404。
$taskAssetPrefix = ($taskPackId.ToLowerInvariant() -replace '[^a-z0-9]+', '-').Trim('-')

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

# 平台 → (channel=完整 RID, 安装包后缀, 产物目录)。channel 用 RID 是为了让 feed 名
# releases.<rid>.json 天然唯一（osx-arm64/osx-x64 不再撞名），且等于客户端查找的 releases.<channel>.json。
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

    $taskUpload += $taskSetupPath
    $taskUpload += ($taskSetupPath + '.sha256')

    # vpk 的 nupkg 名 = {packId}-{version}[-{channel}]-{full|delta}.nupkg；channel 段**仅当
    # os==Windows 且 channel 恰等于平台默认值 "win" 时才省略**（Velopack DefaultName.GetSuggestedReleaseName）。
    # 我们用 channel=rid（win-x64/osx-arm64/…），它不是 "win"，故**四个平台（含 win）都带 -<rid>- 段**。
    # 按带后缀名找，找不到再退回无后缀名兜底。
    # 改名目的：与安装包统一成小写连字符 axmol-hub-<ver>-<rid>-<full|delta>.nupkg（前缀由 packId 派生，
    # 见 $taskAssetPrefix）。改完必须同步修 feed 里引用的 FileName/URL，否则客户端按 feed 找 nupkg 会 404。
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

    # 写回修好的 feed（feed 名 = releases.<channel>.json = releases.<rid>.json），随安装包一起上传。
    Set-Content -LiteralPath $taskFeed -Encoding UTF8 -Value ([pscustomobject]@{ Assets = @($taskKept) } | ConvertTo-Json -Depth 6)
    $taskUpload += $taskFeed
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
