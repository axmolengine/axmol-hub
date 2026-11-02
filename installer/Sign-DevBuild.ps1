<#
.SYNOPSIS
Signs the development build with a stable identity, so macOS keeps the keychain grant it was given.

.DESCRIPTION
Keychain access is granted to an application *identity*. An unsigned binary is identified by its cdhash, which
changes with every build, so "Always Allow" never covers the next build and each launch asks again for the
secrets Hub keeps in the login keychain. Signing the apphost with a certificate replaces that identity with one
that survives rebuilds — identifier plus certificate — and the grant sticks.

Hub already keeps all of a data root's secrets in one keychain item (`MacKeychainSecretStore`). This is the
other half of "ask once": one item, and an identity that stays the same.

.PARAMETER Path
Executable to sign. Defaults to the current Debug build of Hub.

.PARAMETER Identity
Code-signing identity name. Defaults to the first usable identity of this user.

.EXAMPLE
pwsh installer/Sign-DevBuild.ps1
#>
param(
    [string]$Path = '',
    [string]$Identity = ''
)
$ErrorActionPreference = 'Stop'
if (-not $IsMacOS) { throw 'macOS is the only host that binds keychain access to a code signature.' }

$target = if ($Path) { (Resolve-Path -LiteralPath $Path).Path } else {
    Join-Path $PSScriptRoot '../src/AxmolHub/bin/Debug/net8.0/AxmolHub'
}
if (-not (Test-Path -LiteralPath $target)) {
    throw "Nothing to sign at $target — run 'dotnet build src/AxmolHub/AxmolHub.csproj' first."
}

if (-not $Identity) {
    $found = & security find-identity -v -p codesigning | Select-String -Pattern '"([^"]+)"' | Select-Object -First 1
    if (-not $found) {
        throw 'No code-signing identity in this login keychain. Keychain Access → Certificate Assistant → "Create a Certificate…" with type "Code Signing" makes one; run this script again afterwards.'
    }
    $Identity = [regex]::Match($found.Line, '"([^"]+)"').Groups[1].Value
}

# --identifier pins the designated requirement to "this identifier + this certificate": it does not move when the
# toolchain, the output path or the build's contents change, which is exactly what the cdhash used to do.
# Hardened runtime is left off on purpose — it needs JIT entitlements before a .NET host can start at all, and a
# dev build is not the artifact that gets notarized.
& /usr/bin/codesign --force --sign $Identity --identifier AxmolHub --timestamp=none $target
if ($LASTEXITCODE -ne 0) { throw "codesign could not sign with $Identity." }
& /usr/bin/codesign --verify --strict $target
if ($LASTEXITCODE -ne 0) { throw 'The signature does not verify.' }

Write-Host "signed $target"
Write-Host "identity: $Identity"
Write-Host 'designated requirement (what macOS matches future launches against):'
& /usr/bin/codesign -d -r- $target 2>&1 | Select-String -Pattern '^designated' | ForEach-Object { $_.Line }
