$script:ReleaseIsPrerelease = $false

function gh {
    $global:LASTEXITCODE = 0
    if ($args -contains '--method') {
        $field = $args | Where-Object { $_ -like 'prerelease=*' } | Select-Object -First 1
        $script:ReleaseIsPrerelease = $field -eq 'prerelease=true'
    }

    @{ id = 42; prerelease = $script:ReleaseIsPrerelease } | ConvertTo-Json -Compress
}

. "$PSScriptRoot/../installer/GitHubRelease.ps1"
Set-GitHubReleasePrerelease -RepoSlug 'owner/repo' -Tag 'v1.2.3' -Prerelease $true
if (-not $script:ReleaseIsPrerelease) { throw 'The release helper failed to enable Pre-release.' }

Set-GitHubReleasePrerelease -RepoSlug 'owner/repo' -Tag 'v1.2.3' -Prerelease $false
if ($script:ReleaseIsPrerelease) { throw 'The release helper failed to restore a stable release.' }
