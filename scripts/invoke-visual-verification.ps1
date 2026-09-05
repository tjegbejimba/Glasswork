<#
.SYNOPSIS
  Run a Glasswork visual verification scenario in an isolated temporary Vault.

.DESCRIPTION
  Builds the dev WinUI app, creates a scenario-specific Vault/UI-state sandbox,
  launches Glasswork with verification-only environment variables, optionally
  drives UI Automation actions, captures named screenshots, and writes result.json.

  With -MergeEvidence, the runner requires a clean committed checkout and a
  repository-owned scenario, rejects -NoBuild, builds an immutable snapshot of
  that commit into an isolated temporary output tree, and binds result.json to
  the unchanged source, scenario, launched files, and screenshots. Normal
  verification keeps its existing iterative development behavior.

.EXAMPLE
  pwsh -File scripts\invoke-visual-verification.ps1 -Scenario scripts\visual-verification\backlog-smoke.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Scenario,

    [string]$OutDir,

    [switch]$NoBuild,

    [switch]$KeepWorkingDirectory,

    [switch]$MergeEvidence
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'tools\Glasswork.VisualVerification\Glasswork.VisualVerification.csproj'

$runnerArgs = @(
    'run',
    '--project', $project,
    '--',
    '--scenario', (Resolve-Path -LiteralPath $Scenario).Path,
    '--repo-root', $repo
)

if ($OutDir) {
    $runnerArgs += @('--out-dir', $OutDir)
}
if ($NoBuild) {
    $runnerArgs += '--no-build'
}
if ($KeepWorkingDirectory) {
    $runnerArgs += '--keep-working-directory'
}
if ($MergeEvidence) {
    $runnerArgs += '--merge-evidence'
}

& dotnet @runnerArgs
if ($LASTEXITCODE -ne 0) {
    throw "Visual verification failed with exit code $LASTEXITCODE."
}
