# Run from a clean checkout of the exact release commit on the Windows signing host.
param(
    [Parameter(Mandatory)][ValidateRange(1, [long]::MaxValue)][long]$BuildRunId,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-fA-F]{40}\z')][string]$Commit,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-fA-F]{40}\z')][string]$SigningThumbprint,
    [Parameter(Mandatory)][ValidatePattern('\Ahttps?://[a-zA-Z0-9.-]+(?::[0-9]+)?(?:/[a-zA-Z0-9._~/-]*)?\z')][string]$TimestampUrl,
    [Parameter(Mandatory)][string]$SignToolPath
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Run the signed release on the Windows host with the USB Token.' }
$root = (Resolve-Path "$PSScriptRoot/..").Path
$repo = 'axmolengine/axmol-hub'
$repoUrl = "https://github.com/$repo"
. "$PSScriptRoot/ReleaseMetadata.ps1"

$SignToolPath = (Get-Command $SignToolPath -CommandType Application).Source
$head = & git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $head -ne $Commit) { throw "Check out $Commit before releasing." }
$changes = & git -C $root status --porcelain
if ($LASTEXITCODE -ne 0 -or $changes) { throw 'The release checkout must be clean.' }
$message = @(& git -C $root show -s --format=%B $Commit) -join "`n"
if ($LASTEXITCODE -ne 0) { throw "Could not read release commit $Commit." }
$release = Get-ReleaseMetadata -CommitMessage $message
[xml]$product = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
$version = @($product.Project.PropertyGroup.Version) | Where-Object { $_ } | Select-Object -First 1
if (-not $release.IsVersionCommit -or $release.Version -cne $version) {
    throw 'The release commit title must be a ver: marker matching Directory.Build.props.'
}
if ([string]::IsNullOrWhiteSpace($env:ORCAROUTER_APP_ID)) { throw 'Set ORCAROUTER_APP_ID as for the CI packages.' }
if (-not (Test-Path -LiteralPath "Cert:\CurrentUser\My\$SigningThumbprint")) {
    throw 'The signing certificate is not in the current user Personal certificate store.'
}
$cutover = & gh variable get WINDOWS_SIGNED_RELEASES --repo $repo
if ($LASTEXITCODE -ne 0 -or $cutover -ne 'true') { throw 'Enable WINDOWS_SIGNED_RELEASES=true before using the signed publisher.' }

$runJson = & gh api "repos/$repo/actions/runs/$BuildRunId"
if ($LASTEXITCODE -ne 0) { throw "Could not read Build run $BuildRunId." }
$run = $runJson | ConvertFrom-Json
if ($run.path -notmatch '\A\.github/workflows/build\.yml(?:@.*)?\z' -or $run.conclusion -ne 'success' -or
    $run.head_repository.full_name -ne $repo -or $run.head_branch -ne 'master' -or
    $run.event -notin @('push', 'workflow_dispatch') -or $run.head_sha -ne $Commit) {
    throw "Run $BuildRunId must be a successful Build of $Commit on $repo master."
}

$work = Join-Path $root "artifacts/signed-release/$BuildRunId/$([Guid]::NewGuid().ToString('N'))"
$artifactDir = Join-Path $work 'artifacts'
& gh run download $BuildRunId --repo $repo --dir $artifactDir --name osx-arm64 --name osx-x64 --name linux-arm64 --name linux-x64
if ($LASTEXITCODE -ne 0) { throw "Could not download the non-Windows artifacts from Build run $BuildRunId." }
foreach ($rid in @('osx-arm64', 'osx-x64', 'linux-arm64', 'linux-x64')) {
    if (-not (Test-Path -LiteralPath (Join-Path $artifactDir $rid) -PathType Container)) { throw "Build run $BuildRunId has no $rid artifact." }
}

& dotnet run --project (Join-Path $root 'tests/AxmolHub.Checks') -c Release -- (Join-Path $root 'cache/packaging-tools') --prepare-packaging
if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the pinned Velopack CLI.' }
foreach ($rid in @('win-x64', 'win-arm64')) {
    & "$PSScriptRoot/Publish.ps1" -Stage Build -Runtime $rid -Version $version -PrereleaseBuild:$release.IsPrerelease `
        -OutputDir (Join-Path $artifactDir $rid) -SigningThumbprint $SigningThumbprint -TimestampUrl $TimestampUrl
}

$notes = Get-ReleaseNotesForCommit -Root $root -Commit $Commit -Tag "v$version" -RepositoryUrl $repoUrl
foreach ($warning in $notes.Warnings) { Write-Warning $warning }
$notesFile = Join-Path $work 'release-notes.md'
[IO.File]::WriteAllText($notesFile, ($notes.Lines -join "`n") + "`n")
& "$PSScriptRoot/Publish-All.ps1" -Version $version -RepoUrl $repoUrl -ArtifactDir $artifactDir -TargetCommit $Commit `
    -NotesFile $notesFile -Prerelease:$release.IsPrerelease -SigningThumbprint $SigningThumbprint -SignToolPath $SignToolPath
