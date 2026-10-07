. "$PSScriptRoot/../installer/ReleaseMetadata.ps1"

$cases = @(
    @{ Subject = 'Version 1.2.3'; Fallback = ''; Version = '1.2.3'; Marker = $true; Prerelease = $false },
    @{ Subject = 'Version 0.2.3'; Fallback = ''; Version = '0.2.3'; Marker = $true; Prerelease = $true },
    @{ Subject = 'Version 1.2.3-beta'; Fallback = ''; Version = '1.2.3-beta'; Marker = $true; Prerelease = $true },
    @{ Subject = 'Version 1.2.3 (Preview)'; Fallback = ''; Version = '1.2.3'; Marker = $true; Prerelease = $true },
    @{ Subject = 'Version 1.2.3-beta (Preview)'; Fallback = ''; Version = '1.2.3-beta'; Marker = $true; Prerelease = $true },
    @{ Subject = 'feature: update'; Fallback = '0.3.0'; Version = '0.3.0'; Marker = $false; Prerelease = $true },
    @{ Subject = 'feature: update'; Fallback = '1.2.3'; Version = '1.2.3'; Marker = $false; Prerelease = $false },
    @{ Subject = 'Version 1.2.3-preview'; Fallback = ''; Version = '1.2.3-preview'; Marker = $true; Prerelease = $true },
    @{ Subject = 'Version 1.2.3 (Preview!)'; Fallback = ''; Version = ''; Marker = $false; Prerelease = $false }
)

foreach ($case in $cases) {
    $actual = Get-ReleaseMetadata -CommitMessage $case.Subject -FallbackVersion $case.Fallback
    if (($actual.Version -ne $case.Version) -or
        ($actual.IsVersionCommit -ne $case.Marker) -or
        ($actual.IsPrerelease -ne $case.Prerelease)) {
        throw "Unexpected release metadata for '$($case.Subject)': $($actual | ConvertTo-Json -Compress)"
    }
}
