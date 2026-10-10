param(
    [string]$Runtime = 'win-x64',
    [string]$Version,
    [string]$Channel,
    [string]$PackId,
    [string]$PackTitle,
    [string]$OutputDir,
    [string]$PublishDir,
    [switch]$PrereleaseBuild,
    [switch]$ReleaseAssetNames,
    # delta 包的前提是打包时输出目录里已经有上一版的 .nupkg。Publish.ps1 先用
    # `vpk download github` 把上一版取回来，再以 -NoClean 调本脚本；默认仍然清空，
    # 因为 releases.<channel>.json 是单一索引，残留的旧版本会一并列进发布内容。
    [switch]$NoClean
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/AssetNames.ps1"
$taskRoot = (Resolve-Path "$PSScriptRoot/..").Path
$taskManifest = Get-Content -Raw -LiteralPath "$PSScriptRoot/packaging-manifest.json" | ConvertFrom-Json
$taskPack = $taskManifest.packages[0]
if (-not $PackId) { $PackId = $taskManifest.packId }
if (-not $PackTitle) {
    # packTitle 一个值同时决定三件事，三者必须一致地显示「Axmol Hub」：
    #   1) 安装器 exe 的版本资源 ProductName / FileDescription（vpk 从 nuspec 写入）；
    #   2) 桌面 / 开始菜单快捷方式的 .lnk 文件名；
    #   3) 安装根目录稳定启动器 stub 的文件名（{packTitle}.exe，见 WindowsPackCommandRunner）。
    # 下载名与更新载荷名来自 packId / assetPrefix（AssetNames.ps1），与 packTitle 无关，
    # 因此这里改名不会影响发布资产命名。
    $PackTitle = 'Axmol Hub'
}

# 版本单一来源 = 仓库根的 Directory.Build.props（MSBuild 自动导入，五个项目共用一个值）。
# 旧 .iss 里手工复制的 HubVersion 副本已随 Inno 一并删除，这里也不再读 App 项目文件。
if (-not $Version) {
    [xml]$taskProduct = Get-Content -LiteralPath "$taskRoot/Directory.Build.props"
    $Version = @($taskProduct.Project.PropertyGroup.Version) | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw 'Version was not supplied and could not be read from Directory.Build.props.' }

if (-not $Channel) {
    # channel 用完整 RID（win-x64/osx-arm64/osx-x64/linux-x64）：每个架构是独立更新流，
    # feed 名 releases.<rid>.json 天然唯一（解决 osx 双架构同名冲突），且客户端按已装包的
    # channel 精确查找 releases.<channel>.json，打包端命名必须与之严格一致。
    $Channel = $Runtime
}

# 打包器固定版本、装在 workspace 内：不改 PATH，也不复用系统上可能已装的 vpk。
$taskVpk = Join-Path $taskRoot 'cache/packaging-tools/vpk/vpk.exe'
if (-not (Test-Path -LiteralPath $taskVpk)) { $taskVpk = Join-Path $taskRoot 'cache/packaging-tools/vpk/vpk' }
if (-not (Test-Path -LiteralPath $taskVpk)) { throw "Prepare the pinned Velopack CLI $($taskPack.version) first: dotnet run --project tests/AxmolHub.Checks -- cache/packaging-tools --prepare-packaging" }
if ((& $taskVpk --help 2>&1 | Out-String) -notmatch [regex]::Escape("Velopack CLI $($taskPack.version)")) { throw "The workspace Velopack CLI is not the pinned $($taskPack.version)." }

# 三平台都在各自原生 runner 上跑 `vpk pack`：Windows 出 Setup.exe，macOS 出 .pkg，
# Linux 出 .AppImage。Velopack 的 osx/linux 打包 runner 依赖平台工具（pkgbuild/codesign、
# mksquashfs），不能在 Windows 上交叉出包，因此这里不再设平台守卫。

$taskPublish = if ($PublishDir) { $PublishDir } else { Join-Path $taskRoot "artifacts/app/$Runtime" }
$taskOutput = if ($OutputDir) { $OutputDir } else { Join-Path $taskRoot "artifacts/releases/$Runtime" }
if ($NoClean) {
    # 目录里已有的上一版 .nupkg 必须留下：vpk 靠它生成 delta，并把上一版并进 feed。
    # 只有在 Publish.ps1 先跑过 `vpk download github` 时才安全。
    New-Item -ItemType Directory -Force -Path $taskOutput | Out-Null
} else {
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $taskOutput
}
# publish 目录只覆盖不清理：被删名或体积异常的文件（旧 exe 名、巨型原生 PDB）不会被
# publish 清掉，会被 vpk 收进包，造成包体异常甚至旧名残留。每次打包前整体清空，
# 根治"目录只覆盖不清理"的复发——改名或原生依赖升级后无需再手工删 artifacts/app。
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $taskPublish
$taskIsPrereleaseBuild = $PrereleaseBuild -or $Version.StartsWith('0.', [StringComparison]::Ordinal) -or $Version.Contains('-')
$taskPrereleaseValue = $taskIsPrereleaseBuild.ToString().ToLowerInvariant()
dotnet publish "$taskRoot/src/AxmolHub/AxmolHub.csproj" -c Release -r $Runtime --self-contained true -o $taskPublish "-p:HubIsPrereleaseBuild=$taskPrereleaseValue"
if ($LASTEXITCODE -ne 0) { throw 'Hub publish failed.' }

# 图标格式按平台：Windows 用多尺寸 ICO，macOS 要求 ICNS（.app bundle 图标），Linux 用单尺寸 PNG。
# Linux 取 512 而不是 1254 原图：vpk 把这份文件同时写成 .DirIcon、AppDir 根的 {packId}.png 和
# usr/share/icons/hicolor/scalable/apps/{packId}.png（LinuxPackCommandRunner.PreprocessPackDir），
# 三处都是给人缩放的图标，原图只会白占包体。两档 PNG 由 installer/Build-Icon.ps1 生成。
$taskIcon = Join-Path $taskRoot 'src/AxmolHub/Assets/hub-icon-512.png'
if ($Runtime -like 'win-*') { $taskIcon = Join-Path $taskRoot 'src/AxmolHub/Assets/hub-icon.ico' }
if ($Runtime -like 'osx-*') { $taskIcon = Join-Path $taskRoot 'src/AxmolHub/Assets/hub-icon.icns' }

# 主程序名按平台取：Windows 产物带 .exe，macOS/Linux 是 Avalonia 的无后缀同名可执行文件。
# 参数名三平台统一用 --mainExe（官方 vpk 1.2.x 的跨平台参数；master 源码里出现的 --exeName
# 是尚未发布的新名，1.2.161 里不存在）。macOS 的 entry point 其实来自 .app 的 Info.plist、
# Linux 来自生成的 .desktop，--mainExe 在三平台都被接受。
$taskMainExe = 'AxmolHub.exe'
if ($Runtime -like 'osx-*' -or $Runtime -like 'linux-*') { $taskMainExe = 'AxmolHub' }

$taskArguments = @(
    '--skip-updates', 'pack',
    '--packId', $PackId,
    '--packVersion', $Version,
    '--packDir', $taskPublish,
    '--packTitle', $PackTitle,
    '--packAuthors', 'Simdsoft Limited',
    '--icon', $taskIcon,
    '--outputDir', $taskOutput,
    '--channel', $Channel,
    '--runtime', $Runtime,
    '--mainExe', $taskMainExe
)
# 只有 --shortcuts 是 Windows/Inno 专属（Velopack.Packaging.Windows.dll 才有），
# 传给 Linux/macOS 版 vpk 会报 "Unrecognized command or argument" 并连带搞乱后续解析。
if ($Runtime -like 'win-*') {
    # 快捷方式位置：Velopack 的合法值只有 Desktop 与 StartMenuRoot 两个（逗号分隔，可多选）。
    # 一键安装没有向导可承载「是否建桌面快捷方式」这个选项，这里固定为桌面 + 开始菜单各建一个。
    $taskArguments += @('--shortcuts', 'Desktop,StartMenuRoot')
}
if ($Runtime -like 'linux-*') {
    # vpk 生成的桌面入口 Categories 默认是 Utility，应用网格会把 Hub 归到系统工具里。
    # 这份 .desktop 只活在 AppDir 内部（Velopack 的运行时库不做 Linux 菜单集成），
    # 真正装进 ~/.local/share/applications 的那份由 App 自己写，见 Services/LinuxDesktopIdentity。
    $taskArguments += @('--categories', 'Development;Utility')
}
& $taskVpk @taskArguments
if ($LASTEXITCODE -ne 0) { throw 'Velopack packaging failed.' }

# 用户直接下载的产物换成自定义名，规则只写在 installer/AssetNames.ps1 一处 —— Publish.ps1 与
# Publish-All.ps1 按同一个名字找回这个文件，名字规则各拼一遍就是"改了一处、发布 job 在另一处红"。
# vpk 的原生名是 {packId}-{channel}-Setup.exe / {packId}-{channel}.pkg / {packId}-{channel}-Portable 等 ——
# 里面既没有版本也没有架构，在 GitHub Release 页上只能靠 release 标题分辨版本，
# 而同名文件在每次发布里都会重复出现。只改这一个文件：自动更新读的是 releases.<channel>.json
# 与它引用的 .nupkg，与下载的文件叫什么无关（实测 releases.linux-x64.json 里只有 nupkg，AppImage
# 根本不在更新源里）。副作用是 assets.<channel>.json 里仍记着 vpk 原生名 ——
# 那是 `vpk upload` 的上传清单，本项目用 gh release upload 自己列文件，因此不消费它。
# 安装器按平台挑：Windows 是 *-Setup.exe，macOS 是 *.pkg，Linux 是 *.AppImage。
$taskSetup = switch -Wildcard ($Runtime) {
    'win-*'   { @(Get-ChildItem -LiteralPath $taskOutput -File -Filter '*-Setup.exe') }
    'osx-*'   { @(Get-ChildItem -LiteralPath $taskOutput -File -Filter '*.pkg') }
    default   { @(Get-ChildItem -LiteralPath $taskOutput -File -Filter '*.AppImage') }
}
if ($taskSetup.Count -ne 1) { throw "Expected exactly one installer in $taskOutput for $Runtime, found $($taskSetup.Count)." }
if ($Runtime -like 'osx-*') {
    & "$PSScriptRoot/Register-Protocol-Mac.ps1" -PackagePath $taskSetup[0].FullName
}
$taskSetupName = Get-HubAssetName -AssetPrefix $taskManifest.assetPrefix -Version $Version -Runtime $Runtime `
    -Extension $taskSetup[0].Extension -ReleaseAssetNames:$ReleaseAssetNames
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
