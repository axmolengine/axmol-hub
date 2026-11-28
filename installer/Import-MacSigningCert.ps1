<#
.SYNOPSIS
Imports a base64-encoded macOS code-signing .p12 into a temporary keychain for CI builds, or cleans it up.

.DESCRIPTION
Reads MAC_SIGN_P12_BASE64 and MAC_SIGN_P12_PASSWORD (or explicit parameters), creates a fresh temporary keychain
under RUNNER_TEMP (or the system temp directory), unlocks it, imports the certificate for /usr/bin/codesign, and
resolves the signing identity (supporting both Apple-issued certificates and untrusted self-signed certificates).
When running under GitHub Actions (GITHUB_ENV is set), exports MAC_SIGN_KEYCHAIN and MAC_SIGN_IDENTITY.

When MAC_SIGN_P12_BASE64 is empty, exits cleanly with code 0 without creating a keychain so PRs and forks fall
back to ad-hoc bundle signing.
#>
param(
    [string]$P12Base64 = $env:MAC_SIGN_P12_BASE64,
    [string]$Password = $env:MAC_SIGN_P12_PASSWORD,
    [string]$Identity = $env:MAC_SIGN_IDENTITY,
    [string]$KeychainPath = '',
    [switch]$Cleanup
)
$ErrorActionPreference = 'Stop'
if (-not $IsMacOS) { throw 'macOS signing keychain setup is only supported on macOS.' }

$taskTempRoot = if (-not [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
if ([string]::IsNullOrWhiteSpace($KeychainPath)) {
    $KeychainPath = if (-not [string]::IsNullOrWhiteSpace($env:MAC_SIGN_KEYCHAIN)) {
        $env:MAC_SIGN_KEYCHAIN
    } else {
        Join-Path $taskTempRoot 'axmolhub-signing.keychain-db'
    }
}
$taskP12Path = Join-Path $taskTempRoot 'axmolhub-signing.p12'

if ($Cleanup) {
    if (Test-Path -LiteralPath $taskP12Path) { Remove-Item -LiteralPath $taskP12Path -Force }
    if (Test-Path -LiteralPath $KeychainPath) {
        & /usr/bin/security delete-keychain $KeychainPath *> $null
        if (Test-Path -LiteralPath $KeychainPath) { Remove-Item -LiteralPath $KeychainPath -Force }
    }
    return
}

if ([string]::IsNullOrWhiteSpace($P12Base64)) {
    Write-Host 'MAC_SIGN_P12_BASE64 is not set; skipping certificate import (macOS build will use ad-hoc signing).'
    return
}
if ([string]::IsNullOrWhiteSpace($Password)) {
    throw 'MAC_SIGN_P12_PASSWORD must be set when MAC_SIGN_P12_BASE64 is provided.'
}
# Strip trailing line endings in case the secret was uploaded with echo or pasted with a trailing newline.
$Password = $Password.TrimEnd("`r", "`n")

$taskKeychainPasswordBytes = [byte[]]::new(24)
[System.Security.Cryptography.RandomNumberGenerator]::Fill($taskKeychainPasswordBytes)
$taskKeychainPassword = [Convert]::ToBase64String($taskKeychainPasswordBytes)
if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_ACTIONS)) {
    Write-Host "::add-mask::$taskKeychainPassword"
}

if (Test-Path -LiteralPath $KeychainPath) {
    & /usr/bin/security delete-keychain $KeychainPath *> $null
    if (Test-Path -LiteralPath $KeychainPath) { Remove-Item -LiteralPath $KeychainPath -Force }
}

try {
    $taskP12Bytes = [Convert]::FromBase64String(($P12Base64 -replace '\s+', ''))
    [IO.File]::WriteAllBytes($taskP12Path, $taskP12Bytes)
    & /bin/chmod 600 $taskP12Path

    & /usr/bin/security create-keychain -p $taskKeychainPassword $KeychainPath
    if ($LASTEXITCODE -ne 0) { throw "Failed to create temporary keychain at $KeychainPath." }
    & /usr/bin/security set-keychain-settings -lut 21600 $KeychainPath
    if ($LASTEXITCODE -ne 0) { throw 'Failed to configure temporary keychain lock timeout.' }
    & /usr/bin/security unlock-keychain -p $taskKeychainPassword $KeychainPath
    if ($LASTEXITCODE -ne 0) { throw 'Failed to unlock temporary keychain.' }

    # Prepend the temporary keychain to the user search list while keeping existing keychains intact.
    $taskExisting = @(& /usr/bin/security list-keychains -d user | ForEach-Object { $_.Trim().Trim('"') } | Where-Object { $_ -and $_ -ne $KeychainPath })
    & /usr/bin/security list-keychains -d user -s $KeychainPath @taskExisting
    if ($LASTEXITCODE -ne 0) { throw 'Failed to register temporary keychain in the user search list.' }

    & /usr/bin/security import $taskP12Path -f pkcs12 -k $KeychainPath -P $Password -T /usr/bin/codesign -A
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to import MAC_SIGN_P12_BASE64 into the temporary keychain (verify that MAC_SIGN_P12_PASSWORD matches MAC_SIGN_P12_BASE64).'
    }
    & /usr/bin/security set-key-partition-list -S 'apple-tool:,apple:,codesign:' -s -k $taskKeychainPassword $KeychainPath *> $null
    if ($LASTEXITCODE -ne 0) { throw 'Failed to set key partition list on the temporary keychain.' }
}
finally {
    if (Test-Path -LiteralPath $taskP12Path) { Remove-Item -LiteralPath $taskP12Path -Force }
}

$taskResolvedIdentity = $Identity.Trim()
if ([string]::IsNullOrWhiteSpace($taskResolvedIdentity)) {
    # Omit -v so self-signed certificates (CSSMERR_TP_NOT_TRUSTED) are also discovered;
    # prefer the first matching code-signing identity inside the temporary keychain.
    $taskMatch = & /usr/bin/security find-identity -p codesigning $KeychainPath | Select-String -Pattern '^\s*\d+\)\s+[0-9A-Fa-f]{40}\s+"([^"]+)"' | Select-Object -First 1
    if (-not $taskMatch) {
        throw 'Imported .p12 does not contain a macOS code-signing identity (missing codeSigning EKU or private key).'
    }
    $taskResolvedIdentity = [regex]::Match($taskMatch.Line, '"([^"]+)"').Groups[1].Value
}

Write-Host "Imported macOS code-signing identity: $taskResolvedIdentity"
if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_ENV)) {
    Add-Content -LiteralPath $env:GITHUB_ENV -Value "MAC_SIGN_KEYCHAIN=$KeychainPath"
    Add-Content -LiteralPath $env:GITHUB_ENV -Value "MAC_SIGN_IDENTITY=$taskResolvedIdentity"
}
