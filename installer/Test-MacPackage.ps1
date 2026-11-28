param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath
)
$ErrorActionPreference = 'Stop'

$taskPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$taskWork = Join-Path ([IO.Path]::GetTempPath()) ('axmolhub-package-check-' + [Guid]::NewGuid().ToString('N'))

try {
    # Read the actual archive, not the BOM: a broken package can still have a valid file manifest.
    $taskPayload = @(& /usr/sbin/pkgutil --payload-files $taskPackage)
    if ($LASTEXITCODE -ne 0 -or $taskPayload.Count -eq 0) { throw 'The Hub installer payload is unreadable or empty.' }

    & /usr/sbin/pkgutil --expand-full $taskPackage $taskWork
    if ($LASTEXITCODE -ne 0) { throw 'pkgutil could not expand the rebuilt Hub installer package.' }
    $taskPlists = @(Get-ChildItem -LiteralPath $taskWork -Filter 'Info.plist' -File -Recurse |
        Where-Object { $_.FullName -match '\.app/Contents/Info\.plist$' })
    if ($taskPlists.Count -ne 1) { throw "Expected one Hub app Info.plist in the rebuilt package, found $($taskPlists.Count)." }
    $taskPlist = $taskPlists[0].FullName
    $taskExecutable = Join-Path $taskPlists[0].DirectoryName 'MacOS/AxmolHub'
    if (-not (Test-Path -LiteralPath $taskExecutable -PathType Leaf)) { throw 'The Hub installer is missing the AxmolHub executable.' }
    $taskEntry = & /usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' $taskPlist
    if ($LASTEXITCODE -ne 0 -or $taskEntry -cne 'AxmolHub') { throw 'The Hub app CFBundleExecutable must name AxmolHub.' }
    # Notification authorization is keyed by this identifier, and UserNotifications refuses a process without
    # one. A release whose plist lost it would install and launch fine while silently never notifying.
    $taskBundleId = & /usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' $taskPlist
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($taskBundleId)) {
        throw 'The Hub app must declare a CFBundleIdentifier; macOS notification authorization is keyed by it.'
    }
    $taskScheme = & /usr/libexec/PlistBuddy -c 'Print :CFBundleURLTypes:0:CFBundleURLSchemes:0' $taskPlist
    if ($LASTEXITCODE -ne 0 -or $taskScheme -cne 'axmolhub') { throw 'The Hub app must declare the axmolhub URL scheme.' }

    # Verify the .app bundle itself is code-signed with CFBundleIdentifier bound (not bare apphost):
    # macOS UserNotifications rejects an unbound bundle with UNErrorDomain error 1.
    $taskAppBundle = Split-Path -Parent (Split-Path -Parent $taskPlist)
    & /usr/bin/codesign --verify --deep --strict $taskAppBundle
    if ($LASTEXITCODE -ne 0) { throw "The Hub app bundle signature failed verification: $taskAppBundle." }
    $taskSignInfo = (& /usr/bin/codesign -dv $taskAppBundle 2>&1) -join "`n"
    if ($taskSignInfo -notmatch "(?m)^Identifier=$([regex]::Escape($taskBundleId))$") {
        throw "The Hub app bundle signature Identifier must match CFBundleIdentifier '$taskBundleId'."
    }
    if ($taskSignInfo -match '(?m)^Info\.plist=not bound$') {
        throw 'The Hub app bundle signature must bind Info.plist.'
    }
}
finally {
    if (Test-Path -LiteralPath $taskWork) { Remove-Item -LiteralPath $taskWork -Recurse -Force }
}
