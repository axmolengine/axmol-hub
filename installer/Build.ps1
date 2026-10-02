param([string]$Compiler = "$PSScriptRoot/../artifacts/packaging-tools/inno/ISCC.exe")
$ErrorActionPreference = 'Stop'
$taskRoot = (Resolve-Path "$PSScriptRoot/..").Path
if (-not (Test-Path -LiteralPath $Compiler)) { throw 'Prepare the verified private Inno Setup 6.7.3 compiler first. See installer/README.md.' }
if ((Get-FileHash -Algorithm SHA256 -LiteralPath "$taskRoot/installer/ChineseSimplified.isl").Hash -ne '7D544B9BB1D142CFA11F2E5D3CC8ABE2E55F8E066C5124E3772675AA236E1278') { throw 'Installer translation hash mismatch.' }
dotnet publish "$taskRoot/src/AxmolHub.App/AxmolHub.App.csproj" -c Release -r win-x64 --self-contained true -o "$taskRoot/artifacts/app"
if ($LASTEXITCODE -ne 0) { throw 'Hub publish failed.' }
& $Compiler "/DPublishDir=$taskRoot/artifacts/app" "$taskRoot/installer/AxmolHub.iss"
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$taskSetup = "$taskRoot/artifacts/installer/AxmolHub-0.1.6-win-x64-setup.exe"
$taskHash = Get-FileHash -Algorithm SHA256 -LiteralPath $taskSetup
Set-Content -Encoding ASCII -LiteralPath ($taskSetup + '.sha256') -Value ($taskHash.Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($taskSetup))
$taskHash | Format-List
