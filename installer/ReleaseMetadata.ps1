function Get-ReleaseMetadata {
    param(
        [AllowNull()]
        [string]$CommitMessage,
        [AllowNull()]
        [string]$FallbackVersion
    )

    $versionPattern = '\d+\.\d+\.\d+(?:-\w+)?'
    $subject = [Regex]::Match(
        $CommitMessage ?? '',
        "^Version\s+(?<version>$versionPattern)(?<preview>\s+\(Preview\))?$")
    $fallback = [Regex]::Match($FallbackVersion ?? '', "^(?<version>$versionPattern)$")
    $version = if ($subject.Success) {
        $subject.Groups['version'].Value
    } elseif ($fallback.Success) {
        $fallback.Groups['version'].Value
    } else {
        ''
    }

    $isPrerelease = $version.StartsWith('0.', [StringComparison]::Ordinal) -or $version.Contains('-')
    if ($subject.Success -and $subject.Groups['preview'].Success) {
        $isPrerelease = $true
    }

    [pscustomobject]@{
        Version = $version
        IsVersionCommit = $subject.Success
        IsPrerelease = $isPrerelease
    }
}
