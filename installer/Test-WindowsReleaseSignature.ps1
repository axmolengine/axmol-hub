param(
    [Parameter(Mandatory)][string]$SetupPath,
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$Thumbprint,
    [Parameter(Mandatory)][string]$SignToolPath
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Authenticode release verification requires Windows.' }

function Assert-Signature([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $Thumbprint -or -not $signature.TimeStamperCertificate) {
        throw "Expected a valid timestamped signature from $Thumbprint on $Path (status: $($signature.Status))."
    }
    & $SignToolPath verify /pa /all /tw $Path | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "SignTool verification failed for $Path (exit $LASTEXITCODE)." }
}

Assert-Signature $SetupPath
$work = Join-Path ([IO.Path]::GetTempPath()) ('axmolhub-signature-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PackagePath).Path)
    try {
        # vpk names the installed Update.exe Squirrel.exe in the Windows update payload.
        foreach ($name in @('AxmolHub.exe', 'Axmol Hub_ExecutionStub.exe', 'Squirrel.exe')) {
            $entry = @($zip.Entries | Where-Object { $_.Name -ieq $name })
            if ($entry.Count -ne 1) { throw "Expected exactly one $name in $PackagePath." }
            $file = Join-Path $work $name
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry[0], $file)
            Assert-Signature $file
        }
    } finally { $zip.Dispose() }
} finally { Remove-Item -LiteralPath $work -Recurse -Force }
