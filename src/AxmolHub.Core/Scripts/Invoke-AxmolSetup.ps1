param([string]$EngineRoot)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$env:AX_ROOT = $EngineRoot

# 官方环境准备入口。**它会改全局状态**：写 User 级 AX_ROOT、把 <engine>/tools/cmdline 插进
# User PATH、必要时把执行策略设为 Bypass（弹 UAC）—— 这与引擎官方流程完全一致，是刻意的。
# 它自己不会再拉起交互式 pause（只有从资源管理器双击才会）。
try {
    & (Join-Path $EngineRoot 'setup.ps1') @args
    exit $LASTEXITCODE
} catch {
    [Console]::Error.WriteLine($_.Exception.ToString())
    [Console]::Error.WriteLine($_.ScriptStackTrace)
    exit 1
}
