param(
    [string]$Runtime = 'win-x64',
    [string]$Version,
    [string]$Channel,
    [string]$PackId,
    [string]$PackTitle,
    [string]$OutputDir
)
$ErrorActionPreference = 'Stop'
$taskRoot = (Resolve-Path "$PSScriptRoot/..").Path
$taskManifest = Get-Content -Raw -LiteralPath "$PSScriptRoot/packaging-manifest.json" | ConvertFrom-Json
$taskPack = $taskManifest.packages[0]
if (-not $PackId) { $PackId = $taskManifest.packId }
if (-not $PackTitle) { $PackTitle = 'Axmol Hub' }

# 版本单一来源 = App 项目文件。旧 .iss 里手工复制的 HubVersion 副本已随 Inno 一并删除。
if (-not $Version) {
    [xml]$taskProject = Get-Content -LiteralPath "$taskRoot/src/AxmolHub.App/AxmolHub.App.csproj"
    $Version = @($taskProject.Project.PropertyGroup.Version) | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw 'Version was not supplied and could not be read from AxmolHub.App.csproj.' }

if (-not $Channel) {
    $Channel = 'linux'
    if ($Runtime -like 'win-*') { $Channel = 'win' }
    elseif ($Runtime -like 'osx-*') { $Channel = 'osx' }
}

# 打包器固定版本、装在 workspace 内：不改 PATH，也不复用系统上可能已装的 vpk。
$taskVpk = Join-Path $taskRoot 'artifacts/packaging-tools/vpk/vpk.exe'
if (-not (Test-Path -LiteralPath $taskVpk)) { $taskVpk = Join-Path $taskRoot 'artifacts/packaging-tools/vpk/vpk' }
if (-not (Test-Path -LiteralPath $taskVpk)) { throw "Prepare the pinned Velopack CLI $($taskPack.version) first: dotnet run --project tests/AxmolHub.Checks -- artifacts/packaging-tools --prepare-packaging" }
if ((& $taskVpk --help 2>&1 | Out-String) -notmatch [regex]::Escape("Velopack CLI $($taskPack.version)")) { throw "The workspace Velopack CLI is not the pinned $($taskPack.version)." }

# 宿主后端尚未实现（见 docs/hub-development-plan.md D1/D2）：App 仍是 net8.0-windows + WPF。
# Core 与 Cli 已经能为每个宿主构建，这条守卫随 Avalonia 迁移一并删除。
if ($Runtime -notlike 'win-*') { throw "AxmolHub.App targets net8.0-windows with WPF, so $Runtime cannot be published yet." }

$taskPublish = Join-Path $taskRoot "artifacts/app/$Runtime"
# 必须清空：releases.<channel>.json 是单一索引，残留的旧版本会一并列进发布内容。
# 一次性验收包（见 Test.ps1）因此必须用 -OutputDir 指向独立目录，否则会把真索引覆盖成验收包。
$taskOutput = if ($OutputDir) { $OutputDir } else { Join-Path $taskRoot "artifacts/releases/$Runtime" }
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $taskOutput
# publish 目录只覆盖不清理，与旧的 Inno 链路一致：假定 artifacts/ 是干净的。
# 已被删名的文件不会被 publish 清掉，需要彻底重来时手工删除 artifacts/app。
dotnet publish "$taskRoot/src/AxmolHub.App/AxmolHub.App.csproj" -c Release -r $Runtime --self-contained true -o $taskPublish
if ($LASTEXITCODE -ne 0) { throw 'Hub publish failed.' }

# AppImage 要求 PNG 图标，Windows 用多尺寸 ICO。
$taskIcon = Join-Path $taskRoot 'src/AxmolHub.App/Assets/hub-icon.png'
if ($Runtime -like 'win-*') { $taskIcon = Join-Path $taskRoot 'src/AxmolHub.App/Assets/hub-icon.ico' }

$taskArguments = @(
    '--skip-updates', 'pack',
    '--packId', $PackId,
    '--packVersion', $Version,
    '--packDir', $taskPublish,
    '--mainExe', 'AxmolHub.App.exe',
    '--packTitle', $PackTitle,
    '--packAuthors', 'Simdsoft Limited and other Axmol contributors',
    '--icon', $taskIcon,
    '--outputDir', $taskOutput,
    '--channel', $Channel,
    '--runtime', $Runtime,
    # Inno 的桌面快捷方式是可选项且默认不勾选；Velopack 的一键安装没有向导可承载该选项，
    # 所以固定为只建开始菜单入口，而不是接受它的 Desktop,StartMenuRoot 默认值。
    '--shortcuts', 'StartMenuRoot'
)
& $taskVpk @taskArguments
if ($LASTEXITCODE -ne 0) { throw 'Velopack packaging failed.' }

# 只给用户直接下载的产物写摘要；.nupkg 是更新载荷，由 releases.<channel>.json 引用。
$taskAssets = @(Get-ChildItem -LiteralPath $taskOutput -File | Where-Object {
    $_.Extension -in @('.exe', '.zip', '.pkg', '.dmg') -or $_.Name -like '*.AppImage'
})
if (-not $taskAssets) { throw "No user-facing release asset was produced in $taskOutput." }
foreach ($taskAsset in $taskAssets) {
    $taskHash = Get-FileHash -Algorithm SHA256 -LiteralPath $taskAsset.FullName
    Set-Content -Encoding ASCII -LiteralPath ($taskAsset.FullName + '.sha256') -Value ($taskHash.Hash.ToLowerInvariant() + '  ' + $taskAsset.Name)
    $taskHash | Format-List Path, Hash
}
