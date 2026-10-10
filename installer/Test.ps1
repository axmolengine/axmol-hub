param(
    [string]$Runtime = 'win-x64',
    [string]$Version,
    [switch]$Isolated
)
$ErrorActionPreference = 'Stop'
$taskRoot = (Resolve-Path "$PSScriptRoot/..").Path
$taskManifest = Get-Content -Raw -LiteralPath "$PSScriptRoot/packaging-manifest.json" | ConvertFrom-Json
# Single source of the version: Directory.Build.props at the repo root, same as Build.ps1.
if (-not $Version) {
    [xml]$taskProduct = Get-Content -LiteralPath "$taskRoot/Directory.Build.props"
    $Version = @($taskProduct.Project.PropertyGroup.Version) | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw 'Version was not supplied and could not be read from Directory.Build.props.' }
$taskParts = $Version.Split('.')
$taskUpgraded = '{0}.{1}.{2}' -f $taskParts[0], $taskParts[1], ([int]$taskParts[2] + 1)

# Validation packages always use a throwaway identity and a throwaway output directory:
# releases.<channel>.json is a single index, and repacking under another packId would overwrite it with the validation content.
$taskRun = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$taskWork = Join-Path $taskRoot "artifacts/install-checks/$taskRun"
New-Item -ItemType Directory -Force -Path $taskWork | Out-Null
$taskPackId = $taskManifest.packId
# The title shares its source with the --packTitle default in installer/Build.ps1 ('Axmol Hub'):
# it simultaneously drives the installer version resources (ProductName/FileDescription), the shortcut names, and the install-root launcher stub name.
$taskTitle = 'Axmol Hub'
if ($Isolated) {
    $taskPackId = $taskManifest.packId + '.Validation.' + $taskRun
    $taskTitle = 'AxmolHub Validation ' + $taskRun
}
$taskInstall = Join-Path $taskWork 'Hub 中文'
$taskData = Join-Path $taskWork 'user data/HubData'
$taskSettings = Join-Path $taskWork 'user data/hub-settings.json'
$taskStub = Join-Path $taskInstall ($taskTitle + '.exe')
# Main executable name inside the Windows package; same value as the win branch of --mainExe in Build.ps1; rename both places together.
$taskMainExe = 'AxmolHub.exe'
# vpk names the shortcuts after --packTitle (one on the desktop + one in the Start menu, same name).
$taskShortcutName = $taskTitle + '.lnk'
$taskStartMenuShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) $taskShortcutName
$taskDesktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) $taskShortcutName

function Get-HubUninstallEntry([string]$packId) {
    Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -like ('*' + $packId + '*') }
}
function Quote([string]$value) { '"' + $value + '"' }

if (-not $Isolated) {
    $taskExisting = Join-Path $env:LOCALAPPDATA $taskManifest.packId
    if ((Test-Path -LiteralPath $taskExisting) -or
            (Test-Path -LiteralPath $taskStartMenuShortcut) -or
            (Test-Path -LiteralPath $taskDesktopShortcut)) {
        throw 'An existing Hub installation or shortcut is present. Use -Isolated, or run this on a clean account.'
    }
}

$taskInstalled = $false
$taskProtocolRegistryPath = 'HKCU:\Software\Classes\axmolhub'
$taskProtocolBackup = Join-Path $taskWork 'axmolhub-protocol.reg'
$taskProtocolBackupTaken = $false
$taskProtocolStateCaptured = $false
try {
    if (Test-Path -LiteralPath $taskProtocolRegistryPath) {
        & reg.exe export 'HKCU\Software\Classes\axmolhub' $taskProtocolBackup /y | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not preserve the existing axmolhub protocol registration.' }
        $taskProtocolBackupTaken = $true
    }
    $taskProtocolStateCaptured = $true

    $taskReleases = @{}
    foreach ($taskVersion in @($Version, $taskUpgraded)) {
        $taskOutput = Join-Path $taskWork ("releases/" + $taskVersion)
        & "$PSScriptRoot/Build.ps1" -Runtime $Runtime -Version $taskVersion -PackId $taskPackId -PackTitle $taskTitle -OutputDir $taskOutput -PublishDir (Join-Path $taskWork ("publish/" + $taskVersion))
        if ($LASTEXITCODE -ne 0) { throw "Packaging $taskVersion failed." }
        # Local builds use the friendly installer name; there should still be only one
        # installer executable in this version's isolated output directory.
        $taskSetup = @(Get-ChildItem -LiteralPath $taskOutput -File -Filter '*.exe')
        if ($taskSetup.Count -ne 1) { throw "Expected exactly one installer in $taskOutput, found $($taskSetup.Count)." }
        # The installer (Setup) version resources must show the brand name: vpk writes ProductName / FileDescription from --packTitle.
        # The local debug name is fixed as AxmolHub.exe, but the properties page must read 'Axmol Hub' with the space kept.
        $taskSetupInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($taskSetup[0].FullName)
        if ($taskSetupInfo.ProductName -ne $taskTitle) {
            throw "Installer ProductName mismatch: $($taskSetupInfo.ProductName)"
        }
        if ($taskSetupInfo.FileDescription -ne $taskTitle) {
            throw "Installer FileDescription mismatch: $($taskSetupInfo.FileDescription)"
        }
        $taskReleases[$taskVersion] = $taskSetup[0].FullName
    }

    # 1. Silent install (Velopack's Setup.exe is one-click, no wizard; --installto overrides the install directory).
    $taskInstallProcess = Start-Process -FilePath $taskReleases[$Version] -ArgumentList @('-s', '-t', (Quote $taskInstall)) -WindowStyle Hidden -Wait -PassThru
    if ($taskInstallProcess.ExitCode -ne 0) { throw "Install failed: $($taskInstallProcess.ExitCode)" }
    $taskInstalled = $true

    # 2. Installed payload is complete: Velopack puts the app under current\, the outer layer is the stable-path launch stub.
    $taskCurrent = Join-Path $taskInstall 'current'
    foreach ($taskFile in @($taskMainExe, 'coreclr.dll', 'hostfxr.dll', 'Invoke-Axmol.ps1', 'Invoke-AxmolSetup.ps1', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'licenses/Velopack.txt',
            'manifests/engine-manifest.json', 'manifests/recipe-manifest.json', 'manifests/android-gradle-verification.xml')) {
        if (-not (Test-Path -LiteralPath (Join-Path $taskCurrent $taskFile))) { throw "Missing installed file: $taskFile" }
    }
    $taskInstallHook = Start-Process -FilePath (Join-Path $taskCurrent $taskMainExe) -ArgumentList @('--veloapp-install', $Version) -WindowStyle Hidden -Wait -PassThru
    if ($taskInstallHook.ExitCode -ne 0) { throw "Install hook failed: $($taskInstallHook.ExitCode)" }
    $taskMuiCachePath = 'HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache'
    $taskMuiCacheInstalledEntry = Join-Path $taskCurrent "$taskMainExe.FriendlyAppName"
    # The stable launcher under the install root is now 'Axmol Hub.exe'; 'AxmolHub.exe' is the name from 0.8.7 and earlier. Both must be cleaned.
    $taskMuiCacheStableEntry = Join-Path $taskInstall 'Axmol Hub.exe.FriendlyAppName'
    $taskMuiCacheLegacyEntry = Join-Path $taskInstall 'AxmolHub.exe.FriendlyAppName'
    $taskMuiCacheSameDirectoryOtherAppEntry = Join-Path $taskCurrent 'OtherApp.exe.FriendlyAppName'
    $taskMuiCacheOtherEntry = Join-Path $taskWork ("UnrelatedMuiCacheProbe-" + $taskRun + '.exe.FriendlyAppName')
    New-Item -Path $taskMuiCachePath -Force | Out-Null
    $taskMuiCacheMergedPath = 'Registry::HKEY_CLASSES_ROOT\Local Settings\Software\Microsoft\Windows\Shell\MuiCache'
    $taskMuiCache = $null
    try {
        New-ItemProperty -LiteralPath $taskMuiCachePath -Name $taskMuiCacheInstalledEntry -Value 'Old Axmol Hub Name' -PropertyType String -Force | Out-Null
        New-ItemProperty -LiteralPath $taskMuiCachePath -Name $taskMuiCacheStableEntry -Value 'Old Axmol Hub Launcher Name' -PropertyType String -Force | Out-Null
        New-ItemProperty -LiteralPath $taskMuiCachePath -Name $taskMuiCacheLegacyEntry -Value 'Old Axmol Hub Legacy Name' -PropertyType String -Force | Out-Null
        New-ItemProperty -LiteralPath $taskMuiCachePath -Name $taskMuiCacheSameDirectoryOtherAppEntry -Value 'Other App Name' -PropertyType String -Force | Out-Null
        New-ItemProperty -LiteralPath $taskMuiCachePath -Name $taskMuiCacheOtherEntry -Value 'Unrelated App Name' -PropertyType String -Force | Out-Null
        $taskMuiCacheMerged = [Microsoft.Win32.Registry]::ClassesRoot.OpenSubKey('Local Settings\Software\Microsoft\Windows\Shell\MuiCache', $false)
        if (-not $taskMuiCacheMerged) { throw 'MuiCache is not visible through HKEY_CLASSES_ROOT.' }
        try {
            if ($taskMuiCacheMerged.GetValueNames() -notcontains $taskMuiCacheInstalledEntry) {
                throw 'The test Axmol Hub cache entry is not visible through HKEY_CLASSES_ROOT.'
            }
        }
        finally {
            $taskMuiCacheMerged.Dispose()
        }

        $taskCacheHook = Start-Process -FilePath (Join-Path $taskCurrent $taskMainExe) -ArgumentList @('--veloapp-install', $Version) -WindowStyle Hidden -Wait -PassThru
        if ($taskCacheHook.ExitCode -ne 0) { throw "MuiCache cleanup hook failed: $($taskCacheHook.ExitCode)" }
        $taskMuiCache = Get-Item -LiteralPath $taskMuiCachePath
        if ($taskMuiCache.GetValueNames() -contains $taskMuiCacheInstalledEntry) { throw 'Install hook left an Axmol Hub MuiCache entry behind.' }
        if ($taskMuiCache.GetValueNames() -contains $taskMuiCacheStableEntry) { throw 'Install hook left the stable launcher MuiCache entry behind.' }
        if ($taskMuiCache.GetValueNames() -contains $taskMuiCacheLegacyEntry) { throw 'Install hook left the legacy Axmol Hub MuiCache entry behind.' }
        if ($taskMuiCache.GetValueNames() -notcontains $taskMuiCacheSameDirectoryOtherAppEntry) { throw 'Install hook removed an unrelated app from its own install directory.' }
        if ($taskMuiCache.GetValueNames() -notcontains $taskMuiCacheOtherEntry) { throw 'Install hook removed an unrelated MuiCache entry.' }
        $taskMuiCacheMerged = [Microsoft.Win32.Registry]::ClassesRoot.OpenSubKey('Local Settings\Software\Microsoft\Windows\Shell\MuiCache', $false)
        try {
            if ($taskMuiCacheMerged -and $taskMuiCacheMerged.GetValueNames() -contains $taskMuiCacheInstalledEntry) {
                throw 'Install hook left the Axmol Hub entry visible through HKEY_CLASSES_ROOT.'
            }
        }
        finally {
            if ($taskMuiCacheMerged) { $taskMuiCacheMerged.Dispose() }
        }
    }
    finally {
        if ($taskMuiCache) { $taskMuiCache.Dispose() }
        Remove-ItemProperty -LiteralPath $taskMuiCachePath -Name $taskMuiCacheInstalledEntry -ErrorAction SilentlyContinue
        Remove-ItemProperty -LiteralPath $taskMuiCachePath -Name $taskMuiCacheStableEntry -ErrorAction SilentlyContinue
        Remove-ItemProperty -LiteralPath $taskMuiCachePath -Name $taskMuiCacheLegacyEntry -ErrorAction SilentlyContinue
        Remove-ItemProperty -LiteralPath $taskMuiCachePath -Name $taskMuiCacheSameDirectoryOtherAppEntry -ErrorAction SilentlyContinue
        Remove-ItemProperty -LiteralPath $taskMuiCachePath -Name $taskMuiCacheOtherEntry -ErrorAction SilentlyContinue
    }
    if (-not (Test-Path -LiteralPath $taskStub)) { throw 'Missing install-directory stub executable.' }
    # After install, both the desktop and the Start menu must have the branded shortcut (vpk names it after --packTitle, space kept in the name).
    if (-not (Test-Path -LiteralPath $taskStartMenuShortcut)) { throw "Install did not create the Start menu shortcut: $taskShortcutName" }
    if (-not (Test-Path -LiteralPath $taskDesktopShortcut)) { throw "Install did not create the desktop shortcut: $taskShortcutName" }
    $taskProtocolCommand = (Get-Item -LiteralPath (Join-Path $taskProtocolRegistryPath 'shell\open\command')).GetValue('')
    if ($taskProtocolCommand -notlike ('"' + $taskStub + '" "%1"')) { throw "The installed URI handler does not target the stable launcher: $taskProtocolCommand" }
    $taskMainVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $taskCurrent $taskMainExe))
    if ($taskMainVersion.FileDescription -ne 'Axmol Hub') {
        throw "Main executable FileDescription mismatch: $($taskMainVersion.FileDescription)"
    }
    if ($taskMainVersion.ProductName -ne 'Axmol Hub') {
        throw "Main executable ProductName mismatch: $($taskMainVersion.ProductName)"
    }

    # 3. The self-contained build starts: launch via the stub, with data root and settings both pointed at the validation workspace.
    $taskImage = Join-Path $taskWork 'installed-hub.png'
    $taskSmoke = Start-Process -FilePath $taskStub -ArgumentList @('--data-root', (Quote $taskData), '--preferences', (Quote $taskSettings), '--smoke', (Quote $taskImage)) -WindowStyle Hidden -Wait -PassThru
    if ($taskSmoke.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $taskImage)) { throw 'Installed self-contained Hub did not start.' }

    # 4. Upgrade: user data must stay outside the install directory, because Velopack replaces current\ wholesale.
    Set-Content -LiteralPath $taskSettings -Encoding UTF8 -Value '{"Language":"en-US"}'
    New-Item -ItemType Directory -Force -Path $taskData | Out-Null
    Set-Content -LiteralPath (Join-Path $taskData 'keep-user-data.txt') -Value 'preserve'
    $taskUpgradeProcess = Start-Process -FilePath $taskReleases[$taskUpgraded] -ArgumentList @('-s', '-t', (Quote $taskInstall)) -WindowStyle Hidden -Wait -PassThru
    if ($taskUpgradeProcess.ExitCode -ne 0) { throw "Upgrade failed: $($taskUpgradeProcess.ExitCode)" }
    $taskInstalledVersion = ([xml](Get-Content -Raw -LiteralPath (Join-Path $taskCurrent 'sq.version'))).package.metadata.version
    if ($taskInstalledVersion -ne $taskUpgraded) { throw "Upgrade did not take effect: installed $taskInstalledVersion, expected $taskUpgraded." }
    if ((Get-Content -Raw -LiteralPath $taskSettings | ConvertFrom-Json).Language -ne 'en-US') { throw 'Upgrade overwrote user settings.' }
    if (-not (Test-Path -LiteralPath (Join-Path $taskData 'keep-user-data.txt'))) { throw 'Upgrade removed user data.' }

    # 5. Uninstall: the installed payload and the registrations must be gone.
    $taskUninstall = Start-Process -FilePath (Join-Path $taskInstall 'Update.exe') -ArgumentList @('uninstall', '-s') -WindowStyle Hidden -Wait -PassThru
    if ($taskUninstall.ExitCode -ne 0) { throw "Uninstall failed: $($taskUninstall.ExitCode)" }
    $taskInstalled = $false

    # Velopack cannot delete the running Update.exe itself; the directory is finished off by a delayed rmdir, so poll.
    $taskDeadline = (Get-Date).AddSeconds(30)
    while ((Test-Path -LiteralPath $taskCurrent) -and (Get-Date) -lt $taskDeadline) { Start-Sleep -Milliseconds 500 }
    if (Test-Path -LiteralPath $taskCurrent) { throw 'Uninstall left the application payload in place.' }
    if (Test-Path -LiteralPath $taskStub) { throw 'Uninstall left the stub executable in place.' }
    if (Test-Path -LiteralPath $taskStartMenuShortcut) { throw 'Uninstall left the Start menu shortcut in place.' }
    if (Test-Path -LiteralPath $taskDesktopShortcut) { throw 'Uninstall left the desktop shortcut in place.' }
    if (Get-HubUninstallEntry $taskPackId) { throw 'Uninstall left the uninstall registry entry in place.' }
    if (Test-Path -LiteralPath $taskProtocolRegistryPath) { throw 'Uninstall left the axmolhub URI registration in place.' }
    $taskResidual = Test-Path -LiteralPath $taskInstall

    # 6. Uninstall must preserve user data and settings (engines and toolchains are GB-scale and must not go away with the uninstall).
    if (-not (Test-Path -LiteralPath (Join-Path $taskData 'keep-user-data.txt'))) { throw 'Uninstall removed user data.' }
    if (-not (Test-Path -LiteralPath $taskSettings)) { throw 'Uninstall removed user settings.' }

    [pscustomobject]@{
        Install = 'Passed'; PayloadComplete = 'Passed'; SelfContainedStartup = 'Passed'; NonAsciiPath = 'Passed'
        UpgradeApplies = 'Passed'; UpgradePreservesData = 'Passed'
        UninstallRemovesPayload = 'Passed'; UninstallRemovesRegistration = 'Passed'; UninstallPreservesData = 'Passed'
        IsolatedIdentity = [bool]$Isolated; ResidualInstallDirectory = $taskResidual; Evidence = $taskWork
    } | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $taskWork 'result.json')
    Get-Content -LiteralPath (Join-Path $taskWork 'result.json')
} finally {
    if ($taskInstalled -and (Test-Path -LiteralPath (Join-Path $taskInstall 'Update.exe'))) {
        $taskCleanup = Start-Process -FilePath (Join-Path $taskInstall 'Update.exe') -ArgumentList @('uninstall', '-s') -WindowStyle Hidden -Wait -PassThru
        Write-Output "Test installation cleanup exit: $($taskCleanup.ExitCode)"
    }
    if ($taskProtocolStateCaptured) {
        if (Test-Path -LiteralPath $taskProtocolRegistryPath) {
            Remove-Item -LiteralPath $taskProtocolRegistryPath -Recurse -Force
        }
        if ($taskProtocolBackupTaken) {
            & reg.exe import $taskProtocolBackup | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Could not restore the previous axmolhub protocol registration after the installer test.' }
        }
    }
}
