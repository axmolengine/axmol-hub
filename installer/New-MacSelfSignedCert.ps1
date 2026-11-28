<#
.SYNOPSIS
Generates a long-lived self-signed macOS Code Signing certificate (.p12) for stable app identity in CI and local builds.

.DESCRIPTION
Creates a 20-year RSA-4096 self-signed Code Signing certificate and exports it as a password-protected .p12 file.
Signing macOS app bundles with a fixed certificate gives every release the same Designated Requirement
(`identifier "dev.axmol.hub" and certificate leaf = H"..."`), so macOS TCC (Screen Recording / Accessibility),
Keychain, and UserNotifications grants persist across upgrades — even without a paid Developer ID certificate
and without users needing to trust the certificate in Keychain.

By default the .p12 is written under /tmp (never inside the repository). Store its base64 content and password
in GitHub Repository Secrets:
  - MAC_SIGN_P12_BASE64
  - MAC_SIGN_P12_PASSWORD
  - MAC_SIGN_IDENTITY (optional; defaults to the certificate's CN when omitted)

.PARAMETER CommonName
Certificate Common Name (CN). Defaults to 'Simdsoft Limited'.

.PARAMETER Days
Certificate validity in days. Defaults to 7300 (20 years).

.PARAMETER OutputPath
Path for the exported .p12 file. Defaults to /tmp/axmol-hub-codesign.p12.

.PARAMETER Password
Password protecting the .p12 archive. Generated randomly when omitted.

.PARAMETER ImportToLoginKeychain
Also import the generated .p12 into the current user's login keychain so local builds share the same identity as CI.

.EXAMPLE
pwsh installer/New-MacSelfSignedCert.ps1 -ImportToLoginKeychain
#>
param(
    [string]$CommonName = 'Simdsoft Limited',
    [int]$Days = 7300,
    [ValidateSet(3072, 4096)]
    [int]$KeyBits = 4096,
    [string]$OutputPath = '/tmp/axmol-hub-codesign.p12',
    [string]$Password = '',
    [switch]$ImportToLoginKeychain
)
$ErrorActionPreference = 'Stop'
if (-not $IsMacOS) { throw 'This script generates a macOS code-signing .p12 and must be run on macOS.' }

if ([string]::IsNullOrWhiteSpace($Password)) {
    $taskBytes = [byte[]]::new(24)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($taskBytes)
    $Password = [Convert]::ToBase64String($taskBytes)
}

$taskOutDir = Split-Path -Parent $OutputPath
if ($taskOutDir) { New-Item -ItemType Directory -Force -Path $taskOutDir | Out-Null }

$taskWork = Join-Path ([IO.Path]::GetTempPath()) ('axmolhub-cert-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $taskWork | Out-Null

try {
    $taskConfig = Join-Path $taskWork 'codesign.cnf'
    $taskKey = Join-Path $taskWork 'codesign.key'
    $taskCert = Join-Path $taskWork 'codesign.crt'

    $taskConfigText = @"
[req]
distinguished_name = dn
x509_extensions = v3_ext
prompt = no

[dn]
CN = $CommonName

[v3_ext]
basicConstraints = critical,CA:FALSE
keyUsage = critical,digitalSignature
extendedKeyUsage = critical,codeSigning
"@
    [IO.File]::WriteAllText($taskConfig, $taskConfigText.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))

    & /usr/bin/openssl req -x509 -newkey "rsa:$KeyBits" -sha256 -nodes -keyout $taskKey -out $taskCert -days $Days -config $taskConfig
    if ($LASTEXITCODE -ne 0) { throw 'openssl failed to generate the self-signed code-signing certificate.' }

    # Detect OpenSSL 3 vs macOS LibreSSL: OpenSSL 3 defaults to AES-256-CBC PKCS#12 which macOS security(1)
    # rejects unless -legacy is passed, while LibreSSL does not recognize -legacy.
    $taskOpensslVersion = (& /usr/bin/openssl version 2>&1) -join ''
    $taskPkcs12Args = @('pkcs12', '-export', '-out', $OutputPath, '-inkey', $taskKey, '-in', $taskCert, '-passout', "pass:$Password")
    if ($taskOpensslVersion -match '^OpenSSL 3\.') {
        $taskPkcs12Args = @('pkcs12', '-export', '-legacy', '-out', $OutputPath, '-inkey', $taskKey, '-in', $taskCert, '-passout', "pass:$Password")
    }
    & /usr/bin/openssl @taskPkcs12Args
    if ($LASTEXITCODE -ne 0) { throw 'openssl failed to export the .p12 archive.' }

    $taskFingerprint = ((& /usr/bin/openssl x509 -in $taskCert -noout -fingerprint -sha1) -join '').Trim()

    if ($ImportToLoginKeychain) {
        $taskLoginKeychain = Join-Path $HOME 'Library/Keychains/login.keychain-db'
        & /usr/bin/security import $OutputPath -k $taskLoginKeychain -P $Password -T /usr/bin/codesign
        if ($LASTEXITCODE -ne 0) { throw "Failed to import $OutputPath into $taskLoginKeychain." }
        Write-Host "Imported into login keychain: $taskLoginKeychain"
    }

    $taskBase64Path = $OutputPath + '.b64'
    $taskBase64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($OutputPath))
    [IO.File]::WriteAllText($taskBase64Path, $taskBase64 + "`n", [Text.UTF8Encoding]::new($false))
    & /bin/chmod 600 $OutputPath $taskBase64Path

    Write-Host ''
    Write-Host 'Generated self-signed macOS code-signing certificate:'
    Write-Host "  Common Name : $CommonName"
    Write-Host "  Fingerprint : $taskFingerprint"
    Write-Host "  Valid Days  : $Days"
    Write-Host "  PKCS#12 File: $OutputPath"
    Write-Host "  Base64 File : $taskBase64Path"
    Write-Host ''
    Write-Host 'Configure these GitHub Repository Secrets (keep both files outside the repo and delete after saving):'
    Write-Host "  MAC_SIGN_P12_BASE64   = contents of $taskBase64Path"
    Write-Host "  MAC_SIGN_P12_PASSWORD = $Password"
    Write-Host "  MAC_SIGN_IDENTITY     = $CommonName (optional)"
    Write-Host ''
    Write-Host 'Or set them directly with GitHub CLI:'
    Write-Host "  gh secret set MAC_SIGN_P12_BASE64 < `"$taskBase64Path`""
    Write-Host "  printf '%s' '$Password' | gh secret set MAC_SIGN_P12_PASSWORD"
    Write-Host "  printf '%s' '$CommonName' | gh secret set MAC_SIGN_IDENTITY"
}
finally {
    if (Test-Path -LiteralPath $taskWork) { Remove-Item -LiteralPath $taskWork -Recurse -Force }
}
