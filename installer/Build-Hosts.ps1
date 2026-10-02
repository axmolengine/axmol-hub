param([string[]]$Runtimes = @('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64'))
$ErrorActionPreference = 'Stop'
$taskRoot = (Resolve-Path "$PSScriptRoot/..").Path
foreach ($taskRuntime in $Runtimes) {
    if ($taskRuntime -notin @('win-x64','linux-x64','osx-x64','osx-arm64')) { throw "Unsupported Hub host: $taskRuntime" }
    dotnet publish "$taskRoot/src/AxmolHub.Cli/AxmolHub.Cli.csproj" -c Release -r $taskRuntime --self-contained true -o "$taskRoot/artifacts/cli/$taskRuntime"
    if ($LASTEXITCODE -ne 0) { throw "CLI publish failed: $taskRuntime" }
}
