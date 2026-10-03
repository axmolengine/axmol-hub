param(
    [string]$Runtime = 'win-x64',
    [string]$Version,
    [switch]$Isolated
)
$ErrorActionPreference = 'Stop'
$taskRoot = (Resolve-Path "$PSScriptRoot/..").Path
$taskManifest = Get-Content -Raw -LiteralPath "$PSScriptRoot/packaging-manifest.json" | ConvertFrom-Json
# 版本单一来源 = 仓库根的 Directory.Build.props，与 Build.ps1 同源。
if (-not $Version) {
    [xml]$taskProduct = Get-Content -LiteralPath "$taskRoot/Directory.Build.props"
    $Version = @($taskProduct.Project.PropertyGroup.Version) | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw 'Version was not supplied and could not be read from Directory.Build.props.' }
$taskParts = $Version.Split('.')
$taskUpgraded = '{0}.{1}.{2}' -f $taskParts[0], $taskParts[1], ([int]$taskParts[2] + 1)

# 验收包一律用一次性身份与一次性输出目录：releases.<channel>.json 是单一索引，
# 用另一个 packId 重新打包会把它覆盖成验收包内容。
$taskRun = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$taskWork = Join-Path $taskRoot "artifacts/install-checks/$taskRun"
New-Item -ItemType Directory -Force -Path $taskWork | Out-Null
$taskPackId = $taskManifest.packId
$taskTitle = 'Axmol Hub'
if ($Isolated) {
    $taskPackId = $taskManifest.packId + '.Validation.' + $taskRun
    $taskTitle = 'Axmol Hub Validation ' + $taskRun
}
$taskInstall = Join-Path $taskWork 'Hub 中文'
$taskData = Join-Path $taskWork 'user data/HubData'
$taskSettings = Join-Path $taskWork 'user data/hub-settings.json'
$taskStub = Join-Path $taskInstall ($taskTitle + '.exe')

function Get-HubUninstallEntry([string]$packId) {
    Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -like ('*' + $packId + '*') }
}
function Quote([string]$value) { '"' + $value + '"' }

if (-not $Isolated) {
    $taskExisting = Join-Path $env:LOCALAPPDATA $taskManifest.packId
    $taskShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Axmol Hub.lnk'
    if ((Test-Path -LiteralPath $taskExisting) -or (Test-Path -LiteralPath $taskShortcut)) {
        throw 'An existing Hub installation or shortcut is present. Use -Isolated, or run this on a clean account.'
    }
}

$taskInstalled = $false
try {
    $taskReleases = @{}
    foreach ($taskVersion in @($Version, $taskUpgraded)) {
        $taskOutput = Join-Path $taskWork ("releases/" + $taskVersion)
        & "$PSScriptRoot/Build.ps1" -Runtime $Runtime -Version $taskVersion -PackId $taskPackId -PackTitle $taskTitle -OutputDir $taskOutput
        if ($LASTEXITCODE -ne 0) { throw "Packaging $taskVersion failed." }
        $taskReleases[$taskVersion] = @(Get-ChildItem -LiteralPath $taskOutput -File -Filter '*Setup.exe')[0].FullName
    }

    # 1. 静默安装（Velopack 的 Setup.exe 是一键安装，没有向导，--installto 覆盖安装目录）。
    $taskInstallProcess = Start-Process -FilePath $taskReleases[$Version] -ArgumentList @('-s', '-t', (Quote $taskInstall)) -WindowStyle Hidden -Wait -PassThru
    if ($taskInstallProcess.ExitCode -ne 0) { throw "Install failed: $($taskInstallProcess.ExitCode)" }
    $taskInstalled = $true

    # 2. 安装载荷完整：Velopack 把应用放在 current\ 下，外层是稳定路径的启动 stub。
    $taskCurrent = Join-Path $taskInstall 'current'
    foreach ($taskFile in @('AxmolHub.App.exe', 'coreclr.dll', 'hostfxr.dll', 'Invoke-Axmol.ps1', 'Invoke-AxmolSetup.ps1', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'licenses/Velopack.txt',
            'manifests/engine-manifest.json', 'manifests/module-manifest.json', 'manifests/android-gradle-verification.xml')) {
        if (-not (Test-Path -LiteralPath (Join-Path $taskCurrent $taskFile))) { throw "Missing installed file: $taskFile" }
    }
    if (-not (Test-Path -LiteralPath $taskStub)) { throw 'Missing install-directory stub executable.' }

    # 3. 自包含版能启动：用 stub 启动，且数据根与设置都指向验收工作区。
    $taskImage = Join-Path $taskWork 'installed-hub.png'
    $taskSmoke = Start-Process -FilePath $taskStub -ArgumentList @('--data-root', (Quote $taskData), '--preferences', (Quote $taskSettings), '--smoke', (Quote $taskImage)) -WindowStyle Hidden -Wait -PassThru
    if ($taskSmoke.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $taskImage)) { throw 'Installed self-contained Hub did not start.' }

    # 4. 升级：用户数据必须留在安装目录之外，因为 Velopack 会整体替换 current\。
    Set-Content -LiteralPath $taskSettings -Encoding UTF8 -Value '{"Language":"en-US"}'
    New-Item -ItemType Directory -Force -Path $taskData | Out-Null
    Set-Content -LiteralPath (Join-Path $taskData 'keep-user-data.txt') -Value 'preserve'
    $taskUpgradeProcess = Start-Process -FilePath $taskReleases[$taskUpgraded] -ArgumentList @('-s', '-t', (Quote $taskInstall)) -WindowStyle Hidden -Wait -PassThru
    if ($taskUpgradeProcess.ExitCode -ne 0) { throw "Upgrade failed: $($taskUpgradeProcess.ExitCode)" }
    $taskInstalledVersion = ([xml](Get-Content -Raw -LiteralPath (Join-Path $taskCurrent 'sq.version'))).package.metadata.version
    if ($taskInstalledVersion -ne $taskUpgraded) { throw "Upgrade did not take effect: installed $taskInstalledVersion, expected $taskUpgraded." }
    if ((Get-Content -Raw -LiteralPath $taskSettings | ConvertFrom-Json).Language -ne 'en-US') { throw 'Upgrade overwrote user settings.' }
    if (-not (Test-Path -LiteralPath (Join-Path $taskData 'keep-user-data.txt'))) { throw 'Upgrade removed user data.' }

    # 5. 卸载：安装载荷与注册项必须消失。
    $taskUninstall = Start-Process -FilePath (Join-Path $taskInstall 'Update.exe') -ArgumentList @('uninstall', '-s') -WindowStyle Hidden -Wait -PassThru
    if ($taskUninstall.ExitCode -ne 0) { throw "Uninstall failed: $($taskUninstall.ExitCode)" }
    $taskInstalled = $false

    # Velopack 无法删除正在运行的 Update.exe 自身，目录由一条延迟的 rmdir 收尾，因此轮询。
    $taskDeadline = (Get-Date).AddSeconds(30)
    while ((Test-Path -LiteralPath $taskCurrent) -and (Get-Date) -lt $taskDeadline) { Start-Sleep -Milliseconds 500 }
    if (Test-Path -LiteralPath $taskCurrent) { throw 'Uninstall left the application payload in place.' }
    if (Test-Path -LiteralPath $taskStub) { throw 'Uninstall left the stub executable in place.' }
    $taskShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) ($taskTitle + '.lnk')
    if (Test-Path -LiteralPath $taskShortcut) { throw 'Uninstall left the Start menu shortcut in place.' }
    if (Get-HubUninstallEntry $taskPackId) { throw 'Uninstall left the uninstall registry entry in place.' }
    $taskResidual = Test-Path -LiteralPath $taskInstall

    # 6. 卸载必须保留用户数据与设置（引擎与工具链是 GB 级的，不能随卸载丢掉）。
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
}
