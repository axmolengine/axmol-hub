param([Parameter(Mandatory)][string]$PublishDir, [switch]$RequireAll)
$ErrorActionPreference = 'Stop'

# Only rewrite the published manifest. Refuse the source itself, even when called manually.
$taskPath = (Resolve-Path -LiteralPath (Join-Path $PublishDir 'manifests/ai-providers.json')).ProviderPath
if ($taskPath -eq (Resolve-Path -LiteralPath "$PSScriptRoot/../manifests/ai-providers.json").ProviderPath) {
    throw 'Select a build or publish output directory, not the source tree.'
}
$taskManifest = Get-Content -Raw -LiteralPath $taskPath | ConvertFrom-Json -AsHashtable
$changed = $false
foreach ($setting in @(
    @{ provider = 'orcarouter'; field = 'appId'; variable = 'ORCAROUTER_APP_ID' }
    @{ provider = 'github-copilot'; field = 'clientId'; variable = 'GH_OAUTH_CLIENT_ID' }
)) {
    $value = [Environment]::GetEnvironmentVariable($setting.variable)
    if ([string]::IsNullOrWhiteSpace($value)) {
        if ($RequireAll) { throw "$($setting.variable) must be set to package Axmol Hub." }
        continue
    }
    $providers = @($taskManifest['providers'] | Where-Object { $_['id'] -ceq $setting.provider })
    if ($providers.Count -ne 1 -or $null -eq $providers[0]['oauth']) {
        throw "The published manifest must contain exactly one $($setting.provider) provider with OAuth settings."
    }
    $providers[0]['oauth'][$setting.field] = $value.Trim()
    $changed = $true
}
if ($changed) {
    $taskJson = $taskManifest | ConvertTo-Json -Depth 100
    [IO.File]::WriteAllText($taskPath, ($taskJson.Replace("`r`n", "`n").TrimEnd("`n") + "`n"), [Text.UTF8Encoding]::new($false))
}
