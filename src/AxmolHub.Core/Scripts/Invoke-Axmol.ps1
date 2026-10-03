param([string]$EngineRoot)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$env:AX_ROOT = $EngineRoot

# 引擎 cmdline 在开头会用 git 反查提交号（axmol.ps1 里的 `git -C $AX_ROOT branch --show-current`）。
# 发行 ZIP 的引擎树没有 .git，git 会失败并污染 $LASTEXITCODE，最终可能被结尾的
# `exit $LASTEXITCODE` 当成"命令失败"。这里在没有 .git 时把 git 从 PATH 上遮掉，
# 让引擎走它自己的"没有 git 就跳过"分支 —— 比事后猜退出码可靠。
if (-not (Test-Path (Join-Path $EngineRoot '.git'))) {
    $separator = [System.IO.Path]::PathSeparator
    $parts = $env:PATH.Split($separator) | Where-Object {
        $_ -and -not ((Test-Path (Join-Path $_ 'git.exe')) -or (Test-Path (Join-Path $_ 'git')))
    }
    $env:PATH = ($parts -join $separator)
}

try {
    & (Join-Path $EngineRoot 'tools/cmdline/axmol.ps1') @args
    exit $LASTEXITCODE
} catch {
    [Console]::Error.WriteLine($_.Exception.ToString())
    [Console]::Error.WriteLine($_.ScriptStackTrace)
    exit 1
}
