# The name of a user-facing build artifact, in one place. Three scripts consume it:
# Build.ps1 renames what vpk produced, Publish.ps1 (single-platform Upload stage) and
# Publish-All.ps1 (dist stage) each rebuild the same name to find the file — and both throw
# when it is missing, so a rule written out twice here is a release that fails to publish.
function Get-HubAssetName {
    param(
        [string]$PackId,
        [string]$PackTitle = 'Axmol Hub',
        [string]$Version,
        [string]$Runtime,
        [string]$Extension,
        [switch]$ReleaseAssetNames
    )

    # 资产前缀由 packId 派生（Axmol.Hub → axmol-hub），与 nupkg 改名用的是同一条规则；
    # 那两处（Publish.ps1 / Publish-All.ps1 的 $taskAssetPrefix）各自还要改写 feed 里的引用名，
    # 属于更新载荷，不在本函数的职责里。
    $prefix = ($PackId.ToLowerInvariant() -replace '[^a-z0-9]+', '-').Trim('-')

    # Linux 的 AppImage 不是安装器，是用户留在盘上反复运行的那个文件：名字必须跨版本不变，
    # 否则每次发布都要重发链接、书签和脚本。本地调试产物也用同一个名字 —— artifacts/ 是
    # gitignore 的临时区，覆盖没有代价。版本可以从标题栏（Axmol Hub v0.8.3）、桌面入口的
    # X-AppImage-Version 与随包的 .sha256 三处任一处确认。
    if ($Runtime -like 'linux-*') { return "$prefix-$Runtime$Extension" }

    # Windows 的本地构建沿用 brand 名（与安装器 packTitle 同源），发布才换成带 RID 的资产名。
    if ($Runtime -like 'win-*' -and -not $ReleaseAssetNames) { return "$PackTitle.exe" }

    # Windows / macOS 是装完即弃的安装器：版本段帮人确认下到的到底是哪一版，保留。
    return "$prefix-$Version-$Runtime$Extension"
}
