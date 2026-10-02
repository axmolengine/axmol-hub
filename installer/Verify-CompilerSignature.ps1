param([Parameter(Mandatory=$true)][string]$InstallerPath)
$ErrorActionPreference = 'Stop'
$taskSignature = Get-AuthenticodeSignature -LiteralPath $InstallerPath
if ($taskSignature.Status -ne 'Valid' -or $taskSignature.SignerCertificate.Subject -notmatch 'CN=Pyrsys B.V.') {
    throw 'Inno Setup Authenticode publisher validation failed.'
}
Write-Output "Verified compiler publisher: $($taskSignature.SignerCertificate.Subject)"
