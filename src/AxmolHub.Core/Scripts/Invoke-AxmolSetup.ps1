param([string]$EngineRoot)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$env:AX_ROOT = $EngineRoot
$setupScript = Join-Path $EngineRoot 'setup.ps1'
$tokens = $null
$parseErrors = $null
$setupAst = [System.Management.Automation.Language.Parser]::ParseFile($setupScript, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    [Console]::Error.WriteLine("Cannot inspect engine setup.ps1 parameters: $($parseErrors[0].Message)")
    exit 1
}
if ($null -eq $setupAst.ParamBlock -or 'hub' -notin @($setupAst.ParamBlock.Parameters.Name.VariablePath.UserPath)) {
    [Console]::Error.WriteLine("This engine setup.ps1 does not support -hub. Update it before running setup from Hub; refusing to risk persisting environment changes.")
    exit 2
}

# Hub environment setup entry point. The caller passes -hub so AX_ROOT and PATH remain process-local;
# setup may still set the current user's execution policy to Bypass (UAC).
# It does not itself raise another interactive pause (that only happens when double-clicked from Explorer).
try {
    & $setupScript @args
    exit $LASTEXITCODE
} catch {
    [Console]::Error.WriteLine($_.Exception.ToString())
    [Console]::Error.WriteLine($_.ScriptStackTrace)
    exit 1
}
