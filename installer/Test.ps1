param([string]$Setup = "$PSScriptRoot/../artifacts/installer/AxmolHub-0.1.6-win-x64-setup.exe", [switch]$Isolated)
$ErrorActionPreference = 'Stop'
$taskWorkspace = (Resolve-Path "$PSScriptRoot/..").Path
$taskKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{6917FD80-56A9-4E98-B327-A56D8CCE7641}_is1'
$taskShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Axmol Hub.lnk'
if ($Isolated) {
    $taskValidationId = [Guid]::NewGuid().ToString()
    $taskValidationName = 'Axmol Hub Validation ' + $taskValidationId
    $taskSetupName = 'AxmolHub-validation-' + $taskValidationId + '-setup'
    $taskCompiler = Join-Path $taskWorkspace 'artifacts/packaging-tools/inno/ISCC.exe'
    & $taskCompiler ("/DPublishDir=$taskWorkspace/artifacts/app") ("/DHubAppId={{" + $taskValidationId + "}") ("/DHubAppName=" + $taskValidationName) ("/DHubSetupName=" + $taskSetupName) ("/DHubInstallFolder=AxmolHubValidation-" + $taskValidationId) "$PSScriptRoot/AxmolHub.iss" *> "$taskWorkspace/artifacts/isolated-installer-build.log"
    if ($LASTEXITCODE -ne 0) { throw 'Isolated installer compilation failed.' }
    $Setup = Join-Path $taskWorkspace ('artifacts/installer/' + $taskSetupName + '.exe')
    $taskKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{' + $taskValidationId + '}_is1'
    $taskShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) ($taskValidationName + '.lnk')
}
if ((Test-Path -LiteralPath $taskKey) -or (Test-Path -LiteralPath $taskShortcut)) { throw 'An existing Hub installation or shortcut is present. Use an isolated Windows account for this test.' }
$taskCheck = Join-Path $taskWorkspace ('artifacts/installer-checks/' + [Guid]::NewGuid().ToString('N'))
$taskDestination = Join-Path $taskCheck 'Hub 中文'
New-Item -ItemType Directory -Path $taskCheck | Out-Null
$taskLog = Join-Path $taskCheck 'install.log'
$taskSetup = (Resolve-Path $Setup).Path
$taskInstalled = $false
try {
    $taskProcess = Start-Process -FilePath $taskSetup -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LANG=zhCN /LOG=`"$taskLog`" /DIR=`"$taskDestination`"" -WindowStyle Hidden -Wait -PassThru
    if ($taskProcess.ExitCode -ne 0) { throw "Install failed: $($taskProcess.ExitCode)" }
    $taskInstalled = $true
    $taskApp = Join-Path $taskDestination 'AxmolHub.App.exe'
    foreach ($taskFile in @('AxmolHub.App.exe','coreclr.dll','hostfxr.dll','Invoke-Axmol.ps1','manifests/engine-manifest.json','manifests/toolchain-manifest.json','manifests/module-manifest.json','manifests/android-native-toolchain-windows.json','manifests/android-packaging-toolchain-windows.json','manifests/android-gradle-verification.xml','manifests/web-toolchain-windows.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $taskDestination $taskFile))) { throw "Missing installed file: $taskFile" }
    }
    $taskImage = Join-Path $taskCheck 'installed-hub.png'
    $taskSmoke = Start-Process -FilePath $taskApp -ArgumentList "--smoke `"$taskImage`"" -WindowStyle Hidden -Wait -PassThru
    if ($taskSmoke.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $taskImage)) { throw 'Installed self-contained Hub did not start.' }
    $taskSettings = Join-Path $taskDestination 'hub-settings.json'
    Set-Content -LiteralPath $taskSettings -Encoding UTF8 -Value '{"Language":"en-US"}'
    $taskData = Join-Path $taskDestination 'data'
    New-Item -ItemType Directory -Force -Path $taskData | Out-Null
    Set-Content -LiteralPath (Join-Path $taskData 'keep-user-data.txt') -Value 'preserve'
    $taskUpgrade = Start-Process -FilePath $taskSetup -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LANG=zhCN /DIR=`"$taskDestination`"" -WindowStyle Hidden -Wait -PassThru
    if ($taskUpgrade.ExitCode -ne 0 -or (Get-Content -Raw -LiteralPath $taskSettings | ConvertFrom-Json).Language -ne 'en-US') { throw 'Upgrade failed or overwrote user settings.' }
    $taskUninstall = Start-Process -FilePath (Join-Path $taskDestination 'unins000.exe') -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -WindowStyle Hidden -Wait -PassThru
    if ($taskUninstall.ExitCode -ne 0) { throw 'Uninstall failed.' }
    $taskInstalled = $false
    if ((Test-Path -LiteralPath $taskApp) -or (Test-Path -LiteralPath $taskKey) -or (Test-Path -LiteralPath $taskShortcut)) { throw 'Uninstall left an app file or registration.' }
    if (-not (Test-Path -LiteralPath (Join-Path $taskData 'keep-user-data.txt')) -or -not (Test-Path -LiteralPath $taskSettings)) { throw 'Uninstall removed user data or settings.' }
    [pscustomobject]@{ Install = 'Passed'; SelfContainedStartup = 'Passed'; ChinesePath = 'Passed'; UpgradePreservesSettings = 'Passed'; UninstallPreservesData = 'Passed'; IsolatedIdentity = [bool]$Isolated; Evidence = $taskCheck } | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $taskCheck 'result.json')
    Get-Content -LiteralPath (Join-Path $taskCheck 'result.json')
} finally {
    if ($taskInstalled -and (Test-Path -LiteralPath (Join-Path $taskDestination 'unins000.exe'))) {
        $taskCleanup = Start-Process -FilePath (Join-Path $taskDestination 'unins000.exe') -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -WindowStyle Hidden -Wait -PassThru
        Write-Output "Test installation cleanup exit: $($taskCleanup.ExitCode)"
    }
}
