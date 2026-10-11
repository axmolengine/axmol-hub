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
    # Delta packages presuppose the previous version's .nupkg already present in the output
    # directory at pack time. Publish.ps1 first fetches it with `vpk download github`, then
    # calls this script with -NoClean; the default still cleans, because releases.<channel>.json is a single index and leftover old versions would be pulled into the release content.
    [switch]$NoClean
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/AssetNames.ps1"
$taskRoot = (Resolve-Path "$PSScriptRoot/..").Path
$taskManifest = Get-Content -Raw -LiteralPath "$PSScriptRoot/packaging-manifest.json" | ConvertFrom-Json
$taskPack = $taskManifest.packages[0]
if (-not $PackId) { $PackId = $taskManifest.packId }
if (-not $PackTitle) {
    # One packTitle value decides three things, and all three must consistently show 'Axmol Hub':
    #   1) the installer exe's version resources ProductName / FileDescription (vpk writes them from the nuspec);
    #   2) the desktop / Start menu shortcut .lnk file names;
    #   3) the stable launcher stub file name at the install root ({packTitle}.exe, see WindowsPackCommandRunner).
    # The download name and the update payload name come from packId / assetPrefix (AssetNames.ps1), unrelated to packTitle,
    # so renaming here does not affect release asset naming.
    $PackTitle = 'Axmol Hub'
}

# Single source of the version: Directory.Build.props at the repo root (auto-imported by MSBuild, one value shared by five projects).
# The hand-copied HubVersion in the old .iss was removed along with Inno; this no longer reads the App project file either.
if (-not $Version) {
    [xml]$taskProduct = Get-Content -LiteralPath "$taskRoot/Directory.Build.props"
    $Version = @($taskProduct.Project.PropertyGroup.Version) | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw 'Version was not supplied and could not be read from Directory.Build.props.' }

if (-not $Channel) {
    # channel uses the full RID (win-x64/osx-arm64/osx-x64/linux-x64): each architecture is an independent update stream,
    # the feed name releases.<rid>.json is naturally unique (fixes the two osx architectures colliding on one name), and the client looks up
    # releases.<channel>.json exactly by the installed package's channel, so the packaging side must name it identically.
    $Channel = $Runtime
}

# Pinned packager version, installed inside the workspace: does not touch PATH, and does not reuse any vpk that may already be on the system.
$taskVpk = Join-Path $taskRoot 'cache/packaging-tools/vpk/vpk.exe'
if (-not (Test-Path -LiteralPath $taskVpk)) { $taskVpk = Join-Path $taskRoot 'cache/packaging-tools/vpk/vpk' }
if (-not (Test-Path -LiteralPath $taskVpk)) { throw "Prepare the pinned Velopack CLI $($taskPack.version) first: dotnet run --project tests/AxmolHub.Checks -- cache/packaging-tools --prepare-packaging" }
if ((& $taskVpk --help 2>&1 | Out-String) -notmatch [regex]::Escape("Velopack CLI $($taskPack.version)")) { throw "The workspace Velopack CLI is not the pinned $($taskPack.version)." }

# All three platforms run `vpk pack` on their native runner: Windows yields Setup.exe, macOS yields .pkg,
# Linux yields .AppImage. Velopack's osx/linux packaging runners depend on platform tools (pkgbuild/codesign,
# mksquashfs) and cannot cross-pack from Windows, so no platform guard is set here anymore.

$taskPublish = if ($PublishDir) { $PublishDir } else { Join-Path $taskRoot "artifacts/app/$Runtime" }
$taskOutput = if ($OutputDir) { $OutputDir } else { Join-Path $taskRoot "artifacts/releases/$Runtime" }
if ($NoClean) {
    # The previous version's .nupkg already in the directory must stay: vpk uses it to generate the delta and merges the previous version into the feed.
    # Only safe when Publish.ps1 has run `vpk download github` beforehand.
    New-Item -ItemType Directory -Force -Path $taskOutput | Out-Null
} else {
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $taskOutput
}
# The publish directory is only overwritten, never cleaned by publish: files with stale names or abnormal size
# (old exe names, huge native PDBs) survive publish, get swept into the package by vpk, and cause bloated or stale-named packages. Wipe the
# directory before every pack — this cures the "overwrite only, never clean" relapse; no manual deletion of artifacts/app is needed after a rename or a native dependency upgrade.
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $taskPublish
$taskIsPrereleaseBuild = $PrereleaseBuild -or $Version.StartsWith('0.', [StringComparison]::Ordinal) -or $Version.Contains('-')
$taskPrereleaseValue = $taskIsPrereleaseBuild.ToString().ToLowerInvariant()
dotnet publish "$taskRoot/src/AxmolHub/AxmolHub.csproj" -c Release -r $Runtime --self-contained true -o $taskPublish "-p:HubIsPrereleaseBuild=$taskPrereleaseValue"
if ($LASTEXITCODE -ne 0) { throw 'Hub publish failed.' }

# When a publish has to rewrite packages.lock.json — a package version changed, or the file is not there — a
# restore carrying a RID writes only the base node plus that RID. Measured: 2 nodes out against the 5 this
# tracked file carries. Repair the full shape here, before vpk pack and the guards below can fail, so a run
# that did bump a package cannot leave a pruned lock to be committed by accident. On an ordinary run the lock
# is up to date, this restore changes nothing, and it costs about a fifth of a second. A warning and not a
# throw: an offline machine must still finish the package it has already published.
# Three alternatives deliberately not taken: turning generation off for this publish (a lock file is consumed
# whether or not it is written, so that silently freezes the one pin that matters); snapshot and restore it
# around the publish (that reverts legitimate updates too, leaving the artifact and its lock disagreeing); a
# RID-less restore first and then a publish without restore (the self-contained runtime pack is an implicit
# package reference that only a restore carrying a RID injects, so it fails).
dotnet restore "$taskRoot/src/AxmolHub/AxmolHub.csproj"
if ($LASTEXITCODE -ne 0) {
    Write-Warning "RID-less restore failed: src/AxmolHub/packages.lock.json may be pruned to net8.0 and net8.0/$Runtime. Run 'dotnet restore src/AxmolHub/AxmolHub.csproj' before committing."
}

# Icon format per platform: Windows uses a multi-size ICO, macOS requires ICNS (.app bundle icon), Linux uses a single-size PNG.
# Linux takes 512 rather than the 1254 original: vpk writes this one file as .DirIcon, {packId}.png at the AppDir root, and
# usr/share/icons/hicolor/scalable/apps/{packId}.png (LinuxPackCommandRunner.PreprocessPackDir),
# all three are icons meant to be scaled down; the original would just waste package size. Both PNG sizes are generated by installer/Build-Icon.ps1.
$taskIcon = Join-Path $taskRoot 'src/AxmolHub/Assets/hub-icon-512.png'
if ($Runtime -like 'win-*') { $taskIcon = Join-Path $taskRoot 'src/AxmolHub/Assets/hub-icon.ico' }
if ($Runtime -like 'osx-*') { $taskIcon = Join-Path $taskRoot 'src/AxmolHub/Assets/hub-icon.icns' }

# Main executable name per platform: the Windows build carries .exe, macOS/Linux use Avalonia's suffix-less executable of the same name.
# The argument is --mainExe on all three platforms (the cross-platform flag of official vpk 1.2.x; the --exeName
# seen in the master source is an unreleased new name that does not exist in 1.2.161). The macOS entry point actually comes from the .app's Info.plist,
# Linux from the generated .desktop; --mainExe is accepted on all three platforms.
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
# Only --shortcuts is Windows/Inno-specific (only Velopack.Packaging.Windows.dll has it);
# passing it to the Linux/macOS vpk errors with "Unrecognized command or argument" and also messes up the parsing that follows.
if ($Runtime -like 'win-*') {
    # Shortcut locations: Velopack's legal values are only Desktop and StartMenuRoot (comma-separated, multi-select).
    # One-click install has no wizard to carry a "create desktop shortcut?" option, so this is fixed to one on the desktop + one in the Start menu.
    $taskArguments += @('--shortcuts', 'Desktop,StartMenuRoot')
}
if ($Runtime -like 'linux-*') {
    # vpk's generated desktop entry defaults Categories to Utility, so the app grid files Hub under system tools.
    # This .desktop only lives inside the AppDir (Velopack's runtime library does no Linux menu integration);
    # the one actually installed into ~/.local/share/applications is written by the app itself, see Services/LinuxDesktopIdentity.
    $taskArguments += @('--categories', 'Development;Utility')
}
& $taskVpk @taskArguments
if ($LASTEXITCODE -ne 0) { throw 'Velopack packaging failed.' }

# The artifact users download directly is renamed; the rule lives in one place, installer/AssetNames.ps1 — Publish.ps1 and
# Publish-All.ps1 look this file up by the same name, so writing the rule out twice means "rename in one place, release job red in the other".
# vpk's native names are {packId}-{channel}-Setup.exe / {packId}-{channel}.pkg / {packId}-{channel}-Portable etc. —
# they carry neither the version nor the architecture; on the GitHub Release page versions can only be told apart by the release title,
# while same-named files recur in every release. Rename only this one file: auto-update reads releases.<channel>.json
# and the .nupkg entries it references, independent of what the downloaded file is called (measured: releases.linux-x64.json holds only nupkgs, the AppImage
# is not in the update source at all). Side effect: assets.<channel>.json still records vpk's native names —
# that is `vpk upload`'s upload manifest, and this project lists its own files with gh release upload, so it is never consumed.
# Installer picked per platform: Windows is *-Setup.exe, macOS is *.pkg, Linux is *.AppImage.
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

# Write checksums only for the artifacts users download directly; .nupkg is the update payload, referenced by releases.<channel>.json.
$taskAssets = @(Get-ChildItem -LiteralPath $taskOutput -File | Where-Object {
    $_.Extension -in @('.exe', '.zip', '.pkg', '.dmg') -or $_.Name -like '*.AppImage'
})
if (-not $taskAssets) { throw "No user-facing release asset was produced in $taskOutput." }
foreach ($taskAsset in $taskAssets) {
    $taskHash = Get-FileHash -Algorithm SHA256 -LiteralPath $taskAsset.FullName
    Set-Content -Encoding ASCII -LiteralPath ($taskAsset.FullName + '.sha256') -Value ($taskHash.Hash.ToLowerInvariant() + '  ' + $taskAsset.Name)
    $taskHash | Format-List Path, Hash
}
