param(
    [string]$Runtime = 'win-x64',
    [string]$Version,
    [string]$Channel,
    [string]$PackId,
    [string]$PackTitle,
    [string]$OutputDir,
    # delta 包的前提是打包时输出目录里已经有上一版的 .nupkg。Publish.ps1 先用
    # `vpk download github` 把上一版取回来，再以 -NoClean 调本脚本；默认仍然清空，
    # 因为 releases.<channel>.json 是单一索引，残留的旧版本会一并列进发布内容。
    [switch]$NoClean
)
$ErrorActionPreference = 'Stop'
$taskRoot = (Resolve-Path "$PSScriptRoot/..").Path
$taskManifest = Get-Content -Raw -LiteralPath "$PSScriptRoot/packaging-manifest.json" | ConvertFrom-Json
$taskPack = $taskManifest.packages[0]
if (-not $PackId) { $PackId = $taskManifest.packId }
if (-not $PackTitle) { $PackTitle = 'Axmol Hub' }

# 版本单一来源 = 仓库根的 Directory.Build.props（MSBuild 自动导入，五个项目共用一个值）。
# 旧 .iss 里手工复制的 HubVersion 副本已随 Inno 一并删除，这里也不再读 App 项目文件。
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

# 打包器固定版本、装在 workspace 内：不改 PATH，也不复用系统上可能已装的 vpk。
$taskVpk = Join-Path $taskRoot 'artifacts/packaging-tools/vpk/vpk.exe'
if (-not (Test-Path -LiteralPath $taskVpk)) { $taskVpk = Join-Path $taskRoot 'artifacts/packaging-tools/vpk/vpk' }
if (-not (Test-Path -LiteralPath $taskVpk)) { throw "Prepare the pinned Velopack CLI $($taskPack.version) first: dotnet run --project tests/AxmolHub.Checks -- artifacts/packaging-tools --prepare-packaging" }
if ((& $taskVpk --help 2>&1 | Out-String) -notmatch [regex]::Escape("Velopack CLI $($taskPack.version)")) { throw "The workspace Velopack CLI is not the pinned $($taskPack.version)." }

# P6 起 App 是 Avalonia 版（net8.0，三平台都能构建），但**发行通道仍只验过 Windows**：
# Velopack 的 osx/linux 载荷要走各自平台的签名与打包流程，尚未跑通。守卫因此保留，
# 理由从"项目是 Windows 独占"换成了"这条通道还没验过"—— 见 docs/hub-development-plan.md D1/D2/D5。
if ($Runtime -notlike 'win-*') { throw "The $Runtime release channel is not wired up yet (see docs/hub-development-plan.md D1/D2)." }

$taskPublish = Join-Path $taskRoot "artifacts/app/$Runtime"
$taskOutput = if ($OutputDir) { $OutputDir } else { Join-Path $taskRoot "artifacts/releases/$Runtime" }
if ($NoClean) {
    # 目录里已有的上一版 .nupkg 必须留下：vpk 靠它生成 delta，并把上一版并进 feed。
    # 只有在 Publish.ps1 先跑过 `vpk download github` 时才安全。
    New-Item -ItemType Directory -Force -Path $taskOutput | Out-Null
} else {
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $taskOutput
}
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

# 用户直接下载的安装包换成自定义名：axmol-hub-<version>-<runtime>.exe。
# vpk 的原生名是 {packId}-{channel}-Setup.exe —— 里面既没有版本也没有架构，
# 在 GitHub Release 页上只能靠 release 标题分辨版本，而同名文件在每次发布里都会重复出现。
# 只改这一个文件：自动更新读的是 releases.<channel>.json 与它引用的 .nupkg，
# 与安装包叫什么无关。副作用是 assets.<channel>.json 里仍记着 vpk 原生名 ——
# 那是 `vpk upload` 的上传清单，本项目用 gh release upload 自己列文件，因此不消费它。
$taskSetup = @(Get-ChildItem -LiteralPath $taskOutput -File -Filter '*-Setup.exe')
if ($taskSetup.Count -ne 1) { throw "Expected exactly one *-Setup.exe in $taskOutput, found $($taskSetup.Count)." }
$taskSetupName = 'axmol-hub-{0}-{1}{2}' -f $Version, $Runtime, $taskSetup[0].Extension
Move-Item -LiteralPath $taskSetup[0].FullName -Destination (Join-Path $taskOutput $taskSetupName) -Force

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
