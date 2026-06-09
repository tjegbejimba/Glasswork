<#
.SYNOPSIS
  Inspect a Glasswork screen: launch in an isolated sandbox, walk the live UI
  Automation tree, and emit a selector catalog + paired screenshot + a scaffolded
  verification scenario.

.DESCRIPTION
  This is the "computer use" authoring aid. It runs a seed scenario (just enough
  to navigate to the screen you care about), then dumps:
    - inspection.json        the accessibility tree (AutomationIds, names, control
                             types, supported patterns, screenshot-relative bounds,
                             and candidate groupings by action) paired with...
    - inspection.png         ...a screenshot captured at the same moment, so the
                             catalog lines up with the pixels.
    - scenario.suggested.json a minimal, valid starter scenario you can refine.

  Workflow: write a tiny seed scenario (startUri + optional nav action + a
  throwaway capture) -> run this -> read the catalog + view the PNG -> flesh out
  a committed deterministic scenario under scripts\visual-verification\.

  Like the other verification scripts, this launches the Debug build with a
  verification-only AppInstance key and an isolated temp Vault + UI state, so it
  never touches the user's installed Glasswork, real Vault, or glasswork://
  handler. Windows-only; cloud/Linux agents cannot run it.

.PARAMETER Scenario
  Path to the seed scenario JSON used to launch + navigate before inspecting.

.PARAMETER OutDir
  Directory for inspection.json / inspection.png / scenario.suggested.json.
  Defaults to a timestamped temp folder (printed on completion).

.PARAMETER NoBuild
  Skip dotnet build (use when you've just built).

.EXAMPLE
  pwsh -File scripts\inspect-app.ps1 -Scenario scripts\visual-verification\backlog-smoke.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Scenario,

    [string]$OutDir,

    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'tools\Glasswork.VisualVerification\Glasswork.VisualVerification.csproj'

$runnerArgs = @(
    'run',
    '--project', $project,
    '--',
    '--scenario', (Resolve-Path -LiteralPath $Scenario).Path,
    '--repo-root', $repo,
    '--inspect'
)

if ($OutDir) {
    $runnerArgs += @('--out-dir', $OutDir)
}
if ($NoBuild) {
    $runnerArgs += '--no-build'
}

& dotnet @runnerArgs
if ($LASTEXITCODE -ne 0) {
    throw "Inspection failed with exit code $LASTEXITCODE."
}
