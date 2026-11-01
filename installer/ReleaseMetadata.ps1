function Get-ReleaseMetadata {
    param(
        [AllowNull()]
        [string]$CommitMessage,
        [AllowNull()]
        [string]$FallbackVersion
    )

    $versionPattern = '\d+\.\d+\.\d+(?:-\w+)?'
    # The "ver:" marker lives on the title line. Callers hand over the raw commit message, whose body
    # can sit in the same paragraph as the title, so read the first non-empty line instead of the text
    # as a whole. (git's %s is the first paragraph, not the first line.)
    $titleLine = ($CommitMessage ?? '') -split '\r?\n' | Where-Object { $_ -match '\S' } | Select-Object -First 1
    $subject = [Regex]::Match(
        ($titleLine ?? '').Trim(),
        "^ver:\s+(?<version>$versionPattern)(?<preview>\s+\(Preview\))?$")
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

function Get-ReleaseNotes {
    param(
        [string[]]$LogLines,
        [string]$RepositoryUrl,
        [string]$PreviousTag,
        [string]$ReleaseTag,
        [Parameter(Mandatory)]
        [scriptblock]$ReadTouchedFiles
    )

    $notes = [System.Collections.Generic.List[string]]::new()
    $warnings = [System.Collections.Generic.List[string]]::new()
    $notes.Add('## Release notes')
    $notes.Add('')
    $commitCount = 0
    foreach ($line in $LogLines) {
        # %B keeps the title on its own line even when the body follows without a blank line.
        # Only a record's first line contains the SHA fields; everything else is commit body.
        if ($line -notmatch '^(?<sha>[0-9a-f]{40})\t(?<short>[0-9a-f]+)\t(?<title>.*)$') { continue }
        $sha = $Matches.sha
        $short = $Matches.short
        $title = $Matches.title.Trim()
        # A version marker is a release marker, not a change: skip it only when it is a pure version
        # bump. Work bundled with it stays visible, and the warning names the files that rode along.
        # Skipped markers must never return, so an all-version range gets a neutral note instead.
        if ($title.StartsWith('ver:', [StringComparison]::OrdinalIgnoreCase)) {
            $touched = @(& $ReadTouchedFiles $sha | Where-Object { $_ })
            if ($touched.Count -eq 1 -and $touched[0] -eq 'Directory.Build.props') { continue }
            $extra = @($touched | Where-Object { $_ -ne 'Directory.Build.props' })
            $detail = if ($extra) { "also changes $($extra -join ', ')" } else { 'changes no files' }
            $warnings.Add("::warning file=Directory.Build.props::Commit $short is a version marker but $detail; keeping it in the release notes.")
        }
        $notes.Add("- [``$short``]($RepositoryUrl/commit/$sha) $title")
        $commitCount++
    }

    if ($commitCount -eq 0) {
        $notes.Add('No user-facing changes in this release.')
    }
    if ($PreviousTag) {
        $notes.Add('')
        $notes.Add("[Full changelog: $PreviousTag...$ReleaseTag]($RepositoryUrl/compare/$PreviousTag...$ReleaseTag)")
    }

    [pscustomobject]@{
        Lines = $notes.ToArray()
        CommitCount = $commitCount
        Warnings = $warnings.ToArray()
    }
}
