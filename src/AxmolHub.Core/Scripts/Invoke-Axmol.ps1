param([string]$EngineRoot)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$env:AX_ROOT = $EngineRoot

# 1k/1kiss.ps1 在 CMake configure 阶段调用 Get-FileHash（`$1k.hash(...)`）算缓存哈希。
# Get-FileHash 是 Microsoft.PowerShell.Utility 里的 cmdlet，PS 5.1 下靠惰性模块自动加载解析；
# 但自动加载在「-NoProfile + 输出重定向 + 点源长脚本」的组合下会因时机/状态而失效，
# 表现为 CommandNotFoundException: The term 'Get-FileHash' is not recognized。
# 手动在终端跑不触发，是因为交互式会话的自动加载上下文是完整的。
# 这里在点源引擎脚本前显式导入该模块，把 Get-FileHash 钉住，不再依赖脆弱的自动加载。
Import-Module Microsoft.PowerShell.Utility -ErrorAction SilentlyContinue

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
