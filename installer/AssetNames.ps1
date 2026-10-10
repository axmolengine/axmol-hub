# The name of a user-facing build artifact, in one place. Three scripts consume it:
# Build.ps1 renames what vpk produced, Publish.ps1 (single-platform Upload stage) and
# Publish-All.ps1 (dist stage) each rebuild the same name to find the file — and both throw
# when it is missing, so a rule written out twice here is a release that fails to publish.
function Get-HubAssetName {
    param(
        [string]$AssetPrefix,
        [string]$Version,
        [string]$Runtime,
        [string]$Extension,
        [switch]$ReleaseAssetNames
    )

    # Keep public download names stable when the private Velopack installation identity changes.
    $prefix = $AssetPrefix

    # Linux's AppImage is not an installer, it is the file users keep on disk and run repeatedly: the name must stay constant across
    # versions, otherwise every release would mean re-issuing links, bookmarks and scripts. Local debug output uses the same name — artifacts/ is
    # a gitignored scratch area, overwriting costs nothing. The version can be confirmed in any of three places: the title bar (Axmol Hub v0.8.3), the desktop
    # entry's X-AppImage-Version, and the bundled .sha256.
    if ($Runtime -like 'linux-*') { return "$prefix-$Runtime$Extension" }

    # The Windows setup artifact has a stable local name independent of the package title.
    if ($Runtime -like 'win-*' -and -not $ReleaseAssetNames) { return 'AxmolHub.exe' }

    # Windows / macOS installers are discard-after-install packages: the version segment helps people confirm which version they downloaded, keep it.
    return "$prefix-$Version-$Runtime$Extension"
}
