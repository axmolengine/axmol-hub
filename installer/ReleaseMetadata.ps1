function Get-ReleaseMetadata {
    param(
        [AllowNull()]
        [string]$CommitMessage,
        [AllowNull()]
        [string]$FallbackVersion
    )

    $versionPattern = '\d+\.\d+\.\d+(?:-\w+)?'
    # The marker lives on the title line. Callers hand over the raw commit message, whose body can
    # sit in the same paragraph as the title, so read the first non-empty line instead of the text
    # as a whole. (git's %s is no help here: it is the first paragraph, not the first line.)
    $titleLine = ($CommitMessage ?? '') -split '\r?\n' | Where-Object { $_ -match '\S' } | Select-Object -First 1
    $subject = [Regex]::Match(
        ($titleLine ?? '').Trim(),
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
