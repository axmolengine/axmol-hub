param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,
    # Optional code-signing identity for local macOS builds (self-signed or Apple Development).
    # Signs the expanded .app bundle before re-flattening so TCC (Screen Recording / Accessibility)
    # and Keychain grants survive rebuilds instead of changing the designated requirement to a cdhash.
    [string]$SignIdentity = ''
)
$ErrorActionPreference = 'Stop'

$taskPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$taskWork = Join-Path ([IO.Path]::GetTempPath()) ('axmolhub-protocol-' + [Guid]::NewGuid().ToString('N'))
$taskExpanded = Join-Path $taskWork 'expanded'
$taskRebuilt = Join-Path $taskWork 'rebuilt.pkg'
New-Item -ItemType Directory -Force -Path $taskWork | Out-Null

try {
    & /usr/sbin/pkgutil --expand-full $taskPackage $taskExpanded
    if ($LASTEXITCODE -ne 0) { throw 'pkgutil could not expand the Hub installer package.' }

    $taskPlists = @(Get-ChildItem -LiteralPath $taskExpanded -Filter 'Info.plist' -File -Recurse |
        Where-Object { $_.FullName -match '\.app/Contents/Info\.plist$' })
    if ($taskPlists.Count -ne 1) { throw "Expected one Hub app Info.plist in the package, found $($taskPlists.Count)." }

    $taskPlist = $taskPlists[0].FullName
    $taskPlistModified = $false
    & /usr/libexec/PlistBuddy -c 'Print :CFBundleURLTypes' $taskPlist *> $null
    if ($LASTEXITCODE -eq 0) {
        $taskScheme = & /usr/libexec/PlistBuddy -c 'Print :CFBundleURLTypes:0:CFBundleURLSchemes:0' $taskPlist
        if ($LASTEXITCODE -ne 0 -or $taskScheme -cne 'axmolhub') {
            throw 'The Hub app has unexpected URL types; refusing to overwrite them.'
        }
    }
    else {
        foreach ($taskCommand in @(
                'Add :CFBundleURLTypes array',
                'Add :CFBundleURLTypes:0 dict',
                'Add :CFBundleURLTypes:0:CFBundleURLName string AxmolHub',
                'Add :CFBundleURLTypes:0:CFBundleURLSchemes array',
                'Add :CFBundleURLTypes:0:CFBundleURLSchemes:0 string axmolhub'
            )) {
            & /usr/libexec/PlistBuddy -c $taskCommand $taskPlist
            if ($LASTEXITCODE -ne 0) { throw "PlistBuddy failed: $taskCommand" }
        }
        $taskPlistModified = $true
    }

    $taskEffectiveSignIdentity = $SignIdentity
    if ([string]::IsNullOrWhiteSpace($taskEffectiveSignIdentity) -and $taskPlistModified) {
        # Re-seal the bundle if Info.plist had to be modified so the existing signature is never left broken.
        $taskEffectiveSignIdentity = '-'
    }
    if (-not [string]::IsNullOrWhiteSpace($taskEffectiveSignIdentity)) {
        $taskAppBundle = Split-Path -Parent (Split-Path -Parent $taskPlist)
        # Hardened runtime and timestamping are intentionally omitted for local certificates / ad-hoc re-sealing:
        # self-signed certificates cannot use Apple's timestamp server, and local builds are not notarized.
        & /usr/bin/codesign --force --deep --sign $taskEffectiveSignIdentity --timestamp=none $taskAppBundle
        if ($LASTEXITCODE -ne 0) { throw "codesign failed to sign $taskAppBundle with identity '$taskEffectiveSignIdentity'." }
        & /usr/bin/codesign --verify --deep --strict $taskAppBundle
        if ($LASTEXITCODE -ne 0) { throw "codesign verification failed for $taskAppBundle." }
        Write-Host "Signed macOS app bundle with identity: $taskEffectiveSignIdentity"
        & /usr/bin/codesign -d -r- $taskAppBundle 2>&1 | Select-String -Pattern '^designated' | ForEach-Object { Write-Host $_.Line }
    }

    # Fully expanded payloads must be archived again, not stored as directories in the XAR.
    & /usr/sbin/pkgutil --flatten-full $taskExpanded $taskRebuilt
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $taskRebuilt)) { throw 'pkgutil could not rebuild the Hub installer package.' }
    & "$PSScriptRoot/Test-MacPackage.ps1" -PackagePath $taskRebuilt
    Move-Item -LiteralPath $taskRebuilt -Destination $taskPackage -Force
}
finally {
    if (Test-Path -LiteralPath $taskWork) { Remove-Item -LiteralPath $taskWork -Recurse -Force }
}
