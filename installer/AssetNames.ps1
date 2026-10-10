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

    # Linux 的 AppImage 不是安装器，是用户留在盘上反复运行的那个文件：名字必须跨版本不变，
    # 否则每次发布都要重发链接、书签和脚本。本地调试产物也用同一个名字 —— artifacts/ 是
    # gitignore 的临时区，覆盖没有代价。版本可以从标题栏（Axmol Hub v0.8.3）、桌面入口的
    # X-AppImage-Version 与随包的 .sha256 三处任一处确认。
    if ($Runtime -like 'linux-*') { return "$prefix-$Runtime$Extension" }

    # The Windows setup artifact has a stable local name independent of the package title.
    if ($Runtime -like 'win-*' -and -not $ReleaseAssetNames) { return 'AxmolHub.exe' }

    # Windows / macOS 是装完即弃的安装器：版本段帮人确认下到的到底是哪一版，保留。
    return "$prefix-$Version-$Runtime$Extension"
}
