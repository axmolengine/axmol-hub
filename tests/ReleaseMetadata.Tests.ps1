. "$PSScriptRoot/../installer/ReleaseMetadata.ps1"

$cases = @(
    @{ Message = 'Version 1.2.3'; Fallback = ''; Version = '1.2.3'; Marker = $true; Prerelease = $false },
    @{ Message = 'Version 0.2.3'; Fallback = ''; Version = '0.2.3'; Marker = $true; Prerelease = $true },
    @{ Message = 'Version 1.2.3-beta'; Fallback = ''; Version = '1.2.3-beta'; Marker = $true; Prerelease = $true },
    @{ Message = 'Version 1.2.3 (Preview)'; Fallback = ''; Version = '1.2.3'; Marker = $true; Prerelease = $true },
    @{ Message = 'Version 1.2.3-beta (Preview)'; Fallback = ''; Version = '1.2.3-beta'; Marker = $true; Prerelease = $true },
    @{ Message = 'feature: update'; Fallback = '0.3.0'; Version = '0.3.0'; Marker = $false; Prerelease = $true },
    @{ Message = 'feature: update'; Fallback = '1.2.3'; Version = '1.2.3'; Marker = $false; Prerelease = $false },
    @{ Message = 'Version 1.2.3-preview'; Fallback = ''; Version = '1.2.3-preview'; Marker = $true; Prerelease = $true },
    @{ Message = 'Version 1.2.3 (Preview!)'; Fallback = ''; Version = ''; Marker = $false; Prerelease = $false },
    # The marker is the title line, so a body may follow it with or without the blank line git's
    # "subject" needs. Version text deeper in the message is prose, not a marker.
    @{ Message = "Version 1.2.3`nBumps the version and nothing else."; Fallback = ''; Version = '1.2.3'; Marker = $true; Prerelease = $false },
    @{ Message = "Version 0.4.5 (Preview)`nFirst body line`nSecond body line"; Fallback = ''; Version = '0.4.5'; Marker = $true; Prerelease = $true },
    @{ Message = "`n`nVersion 1.2.3`n`nBody"; Fallback = ''; Version = '1.2.3'; Marker = $true; Prerelease = $false },
    @{ Message = "fix: keep the activity fold state as data`nVersion 9.9.9 appears only in the body"; Fallback = ''; Version = ''; Marker = $false; Prerelease = $false }
)

foreach ($case in $cases) {
    $actual = Get-ReleaseMetadata -CommitMessage $case.Message -FallbackVersion $case.Fallback
    if (($actual.Version -ne $case.Version) -or
        ($actual.IsVersionCommit -ne $case.Marker) -or
        ($actual.IsPrerelease -ne $case.Prerelease)) {
        $shown = ($case.Message -replace '\r?\n', '\n')
        throw "Unexpected release metadata for '$shown': $($actual | ConvertTo-Json -Compress)"
    }
}
