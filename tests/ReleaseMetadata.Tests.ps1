. "$PSScriptRoot/../installer/ReleaseMetadata.ps1"

$cases = @(
    @{ Message = 'ver: 1.2.3'; Fallback = ''; Version = '1.2.3'; Marker = $true; Prerelease = $false },
    @{ Message = 'ver: 0.19.0'; Fallback = ''; Version = '0.19.0'; Marker = $true; Prerelease = $true },
    @{ Message = 'ver: 1.2.3-beta'; Fallback = ''; Version = '1.2.3-beta'; Marker = $true; Prerelease = $true },
    @{ Message = 'ver: 1.2.3 (Preview)'; Fallback = ''; Version = '1.2.3'; Marker = $true; Prerelease = $true },
    @{ Message = 'ver: 1.2.3-beta (Preview)'; Fallback = ''; Version = '1.2.3-beta'; Marker = $true; Prerelease = $true },
    @{ Message = 'feature: update'; Fallback = '0.3.0'; Version = '0.3.0'; Marker = $false; Prerelease = $true },
    @{ Message = 'feature: update'; Fallback = '1.2.3'; Version = '1.2.3'; Marker = $false; Prerelease = $false },
    @{ Message = 'ver: 1.2.3-preview'; Fallback = ''; Version = '1.2.3-preview'; Marker = $true; Prerelease = $true },
    @{ Message = 'ver: 1.2.3 (Preview!)'; Fallback = ''; Version = ''; Marker = $false; Prerelease = $false },
    @{ Message = 'Version 1.2.3'; Fallback = ''; Version = ''; Marker = $false; Prerelease = $false },
    @{ Message = 'misc: Version 0.19.0'; Fallback = ''; Version = ''; Marker = $false; Prerelease = $false },
    @{ Message = 'release: 1.0.0'; Fallback = ''; Version = ''; Marker = $false; Prerelease = $false },
    @{ Message = 'fix: ver: 1.2.3'; Fallback = ''; Version = ''; Marker = $false; Prerelease = $false },
    @{ Message = 'misc: ver: 1.2.3'; Fallback = ''; Version = ''; Marker = $false; Prerelease = $false },
    @{ Message = 'ver: update 1.2.3'; Fallback = ''; Version = ''; Marker = $false; Prerelease = $false },
    # The marker is the title line, so a body may follow it with or without the blank line git's
    # "subject" needs. Version text deeper in the message is prose, not a marker.
    @{ Message = "ver: 1.2.3`nBumps the version and nothing else."; Fallback = ''; Version = '1.2.3'; Marker = $true; Prerelease = $false },
    @{ Message = "ver: 0.4.5 (Preview)`nFirst body line`nSecond body line"; Fallback = ''; Version = '0.4.5'; Marker = $true; Prerelease = $true },
    @{ Message = "`n`nver: 1.2.3`n`nBody"; Fallback = ''; Version = '1.2.3'; Marker = $true; Prerelease = $false },
    @{ Message = "fix: update`nver: 9.9.9"; Fallback = ''; Version = ''; Marker = $false; Prerelease = $false }
)

# CI uses this marker for packaging and publishing: accidental matches would publish an ordinary
# commit, while missed matches would silently skip a release.
foreach ($case in $cases) {
    $actual = Get-ReleaseMetadata -CommitMessage $case.Message -FallbackVersion $case.Fallback
    if (($actual.Version -ne $case.Version) -or
        ($actual.IsVersionCommit -ne $case.Marker) -or
        ($actual.IsPrerelease -ne $case.Prerelease)) {
        $shown = ($case.Message -replace '\r?\n', '\n')
        throw "Unexpected release metadata for '$shown': $($actual | ConvertTo-Json -Compress)"
    }
}

# Release notes hide pure version bumps without hiding real work bundled with a version title, and
# an all-version range must not fall back to restoring the skipped markers.
$noteCases = @(
    @{
        Name = 'mixed'
        Commits = @(
            @{ Title = 'feat: ship the feature'; Files = @('src/Feature.cs') },
            @{ Title = 'ver: 1.2.3'; Files = @('Directory.Build.props') },
            @{ Title = 'VER: 1.2.3 (Preview)'; Files = @('Directory.Build.props') },
            @{ Title = 'ver: 1.2.4'; Files = @('Directory.Build.props', 'src/Feature.cs') },
            @{ Title = 'ver: update the manifest'; Files = @('installer/README.md') },
            @{ Title = 'fix: document ver: syntax'; Files = @('docs/ci.md') }
        )
        Expected = @('feat: ship the feature', 'ver: 1.2.4', 'ver: update the manifest', 'fix: document ver: syntax')
        Warnings = 2
        PreviousTag = 'v1.2.2'
    },
    @{
        Name = 'version-only'
        Commits = @(
            @{ Title = 'ver: 1.2.3'; Files = @('Directory.Build.props') },
            @{ Title = 'ver: 1.2.3-beta'; Files = @('Directory.Build.props') }
        )
        Expected = @()
        Warnings = 0
        PreviousTag = 'v1.2.2'
    },
    @{ Name = 'empty'; Commits = @(); Expected = @(); Warnings = 0; PreviousTag = '' },
    @{ Name = 'empty-version-commit'; Commits = @(@{ Title = 'ver: 1.2.3'; Files = @() }); Expected = @('ver: 1.2.3'); Warnings = 1; PreviousTag = '' }
)

foreach ($case in $noteCases) {
    $changedFiles = @{}
    $logLines = @(
        $index = 1
        foreach ($commit in $case.Commits) {
            $sha = $index.ToString('x40')
            $short = $index.ToString('x7')
            $changedFiles[$sha] = $commit.Files
            "$sha`t$short`t$($commit.Title)"
            'BODY MUST STAY OUT: ver: 9.9.9 is only prose.'
            ''
            $index++
        }
    )
    $notes = Get-ReleaseNotes -LogLines $logLines -RepositoryUrl 'https://github.com/owner/repo' -PreviousTag $case.PreviousTag -ReleaseTag 'v1.2.3' -ReadTouchedFiles { param($sha) $changedFiles[$sha] }
    $rendered = $notes.Lines -join "`n"
    $titles = @(
        foreach ($line in $notes.Lines) {
            if ($line -match '^- \[`[0-9a-f]+`\]\([^)]*\) (?<title>.*)$') { $Matches.title }
        }
    )
    if (($titles -join "`n") -cne ($case.Expected -join "`n") -or
        $notes.CommitCount -ne $case.Expected.Count -or $notes.Warnings.Count -ne $case.Warnings -or
        $rendered.Contains('BODY MUST STAY OUT')) {
        throw "Unexpected release notes for '$($case.Name)' ($($notes.CommitCount) commit(s), $($notes.Warnings.Count) warning(s)): $rendered"
    }
    if ($case.Expected.Count -eq 0 -and -not ($notes.Lines | Where-Object { $_ -and $_ -notmatch '^(## |\[Full changelog:)' })) {
        throw "Release notes have no body after filtering '$($case.Name)'."
    }
}

# The warning is the only place a reviewer learns which files rode along with a version marker.
$bundled = Get-ReleaseNotes -LogLines @("$('1'.PadLeft(40, '0'))`t1234567`tver: 1.2.4") -RepositoryUrl 'https://github.com/owner/repo' -PreviousTag '' -ReleaseTag 'v1.2.4' -ReadTouchedFiles { param($sha) @('Directory.Build.props', 'src/Feature.cs') }
if ($bundled.Warnings -notmatch 'also changes src/Feature\.cs') {
    throw "Unexpected bundled-bump warning: $($bundled.Warnings -join ' | ')"
}

Write-Host 'PASS: release marker parsing, and release notes that omit pure ver: bumps and bodies, preserve and warn on bundled work, and never restore filtered markers.'
