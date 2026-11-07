param([string]$EngineRoot, [string]$InvocationBase64)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$env:AX_ROOT = $EngineRoot

# 1k/1kiss.ps1 calls Get-FileHash (`$1k.hash(...)`) during the CMake configure phase to compute the cache hash.
# Get-FileHash is a cmdlet in Microsoft.PowerShell.Utility, resolved on PS 5.1 via lazy module auto-loading;
# but auto-loading fails under the "no profile + output redirection + dot-sourced long script" combination
# due to timing/state, surfacing as CommandNotFoundException: The term 'Get-FileHash' is not recognized.
# Running it manually in a terminal does not trigger it because an interactive session's auto-loading context is complete.
# Here we explicitly import that module before dot-sourcing the engine script to pin down Get-FileHash and stop
# relying on the fragile auto-loading.
Import-Module Microsoft.PowerShell.Utility -ErrorAction SilentlyContinue

# The engine cmdline uses git at the start to look up the commit id (`git -C $AX_ROOT branch --show-current`
# in axmol.ps1). The engine tree in a release ZIP has no .git, so git fails and pollutes $LASTEXITCODE,
# which the trailing `exit $LASTEXITCODE` may end up treating as "command failed". Here, when there is no
# .git, we mask git off PATH so the engine takes its own "no git, skip" branch — more reliable than
# guessing the exit code after the fact.
if (-not (Test-Path (Join-Path $EngineRoot '.git'))) {
    $separator = [System.IO.Path]::PathSeparator
    $parts = $env:PATH.Split($separator) | Where-Object {
        $_ -and -not ((Test-Path (Join-Path $_ 'git.exe')) -or (Test-Path (Join-Path $_ 'git')))
    }
    $env:PATH = ($parts -join $separator)
}

try {
    # Decode an argv array rather than evaluating command text. This preserves colons, quotes and
    # spaces that PowerShell's native -File parameter binding would otherwise reinterpret.
    [string[]]$engineArgs = ConvertFrom-Json ([System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($InvocationBase64)))
    if ($engineArgs[0] -eq 'new') {
        # Array splatting loses option-name semantics. Hashtable splatting preserves named options
        # through axmol.ps1's forwarding to the official project template script.
        $newOptions = @{}
        $newArgs = @('new')
        for ($index = 1; $index -lt $engineArgs.Count; ++$index) {
            $argument = $engineArgs[$index]
            if ($argument -in @('-p', '-d', '-l', '-m')) {
                if ($index + 1 -ge $engineArgs.Count) { throw "Missing value for axmol new option $argument." }
                $newOptions[$argument.Substring(1)] = $engineArgs[++$index]
            } else {
                $newArgs += $argument
            }
        }
        & (Join-Path $EngineRoot 'tools/cmdline/axmol.ps1') @newArgs @newOptions
    } elseif ($engineArgs[0] -eq 'build') {
        $configOnly = $engineArgs -contains '-configOnly'
        $forceConfig = $engineArgs -contains '-forceConfig'
        $buildArgs = @($engineArgs | Where-Object { $_ -notin @('-configOnly', '-forceConfig') })
        # Literal named switches keep their binding through axmol.ps1's array forwarding.
        # Hashtable values (or -switch:$true) leave stray booleans in the plugin's argv.
        $entry = Join-Path $EngineRoot 'tools/cmdline/axmol.ps1'
        if ($configOnly -and $forceConfig) {
            & $entry @buildArgs -configOnly -forceConfig
        } elseif ($configOnly) {
            & $entry @buildArgs -configOnly
        } elseif ($forceConfig) {
            & $entry @buildArgs -forceConfig
        } else {
            & $entry @buildArgs
        }
    } else {
        & (Join-Path $EngineRoot 'tools/cmdline/axmol.ps1') @engineArgs
    }
    exit $LASTEXITCODE
} catch {
    [Console]::Error.WriteLine($_.Exception.ToString())
    [Console]::Error.WriteLine($_.ScriptStackTrace)
    exit 1
}
