param([string]$EngineRoot)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$env:AX_ROOT = $EngineRoot

# Official environment setup entry point. **It changes global state**: writes a User-level AX_ROOT,
# inserts <engine>/tools/cmdline into the User PATH, and when necessary sets the execution policy to
# Bypass (popping a UAC prompt) — this matches the engine's official flow exactly, and is deliberate.
# It does not itself raise another interactive pause (that only happens when double-clicked from Explorer).
try {
    & (Join-Path $EngineRoot 'setup.ps1') @args
    exit $LASTEXITCODE
} catch {
    [Console]::Error.WriteLine($_.Exception.ToString())
    [Console]::Error.WriteLine($_.ScriptStackTrace)
    exit 1
}
