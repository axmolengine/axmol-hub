param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath
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
    & /usr/libexec/PlistBuddy -c 'Print :CFBundleURLTypes' $taskPlist *> $null
    if ($LASTEXITCODE -eq 0) { throw 'The Hub app already declares URL types; refusing to overwrite them.' }

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

    & /usr/sbin/pkgutil --flatten $taskExpanded $taskRebuilt
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $taskRebuilt)) { throw 'pkgutil could not rebuild the Hub installer package.' }
    Move-Item -LiteralPath $taskRebuilt -Destination $taskPackage -Force
}
finally {
    if (Test-Path -LiteralPath $taskWork) { Remove-Item -LiteralPath $taskWork -Recurse -Force }
}
