param([string]$InstallerPath)
$ErrorActionPreference = 'Stop'
try {
    $signature = Get-AuthenticodeSignature -LiteralPath $InstallerPath
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -ne 'Microsoft Corporation') {
        throw "Installer must have a valid Microsoft Corporation Authenticode signature. Status: $($signature.Status)"
    }
    [Console]::WriteLine('Valid Microsoft Corporation Authenticode signature.')
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.ToString())
    exit 1
}
