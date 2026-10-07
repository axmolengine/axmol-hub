function Set-GitHubReleasePrerelease {
    param(
        [Parameter(Mandatory)]
        [string]$RepoSlug,
        [Parameter(Mandatory)]
        [string]$Tag,
        [Parameter(Mandatory)]
        [bool]$Prerelease
    )

    $releaseJson = & gh api "repos/$RepoSlug/releases/tags/$Tag"
    if ($LASTEXITCODE -ne 0) { throw "Could not read GitHub release $Tag." }
    $release = $releaseJson | ConvertFrom-Json
    if (-not $release.id) { throw "GitHub release $Tag has no release id." }

    $value = $Prerelease.ToString().ToLowerInvariant()
    $updatedJson = & gh api --method PATCH "repos/$RepoSlug/releases/$($release.id)" -F "prerelease=$value"
    if ($LASTEXITCODE -ne 0) { throw "Could not set the Pre-release flag on GitHub release $Tag." }
    $updated = $updatedJson | ConvertFrom-Json
    if ([bool]$updated.prerelease -ne $Prerelease) {
        throw "GitHub release $Tag has an unexpected Pre-release flag."
    }
}
