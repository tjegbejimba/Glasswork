<#
.SYNOPSIS
  Run a Glasswork visual verification scenario in an isolated temporary Vault.

.DESCRIPTION
  Builds the dev WinUI app, creates a scenario-specific Vault/UI-state sandbox,
  launches Glasswork with verification-only environment variables, optionally
  drives UI Automation actions, captures named screenshots, and writes result.json.

  With -MergeEvidence, pass an explicit -OutDir. The runner requires a clean committed checkout and a
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

$mergeOutDir = $null
$mergeResultPath = $null
$mergeFailurePath = $null
$mergeOutputInitialized = $false

function Remove-GeneratedEvidenceFile([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
        if ($item -isnot [IO.FileInfo] -or
            (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "Refusing to remove non-file merge-evidence path: $Path"
        }
        if (($item.Attributes -band [IO.FileAttributes]::ReadOnly) -ne 0) {
            [IO.File]::SetAttributes(
                $item.FullName,
                $item.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly))
        }
        [IO.File]::Delete($item.FullName)
    }
    if (Test-Path -LiteralPath $Path) {
        throw "Could not remove generated merge-evidence file: $Path"
    }
}

function Write-WrapperFailure([string]$Message) {
    if (-not $MergeEvidence -or -not $mergeOutputInitialized) {
        return
    }

    Remove-GeneratedEvidenceFile $mergeResultPath
    if (-not [IO.File]::Exists($mergeFailurePath)) {
        [ordered]@{
            Success = $false
            Stage = 'wrapper'
            Message = $Message
        } |
            ConvertTo-Json |
            Set-Content -LiteralPath $mergeFailurePath -Encoding utf8NoBOM
    }
}

try {
    if ($MergeEvidence) {
        if ([string]::IsNullOrWhiteSpace($OutDir)) {
            throw '-OutDir is required with -MergeEvidence.'
        }

        $mergeOutDir = [IO.Path]::GetFullPath($OutDir)
        if ([IO.File]::Exists($mergeOutDir)) {
            throw "Merge-evidence output path is a file: $mergeOutDir"
        }
        [IO.Directory]::CreateDirectory($mergeOutDir) | Out-Null
        $mergeResultPath = Join-Path $mergeOutDir 'result.json'
        $mergeFailurePath = Join-Path $mergeOutDir 'failure.json'
        Remove-GeneratedEvidenceFile $mergeResultPath
        Remove-GeneratedEvidenceFile $mergeFailurePath
        $mergeOutputInitialized = $true
    }

    $runnerArgs = @(
        'run',
        '--project', $project,
        '--',
        '--scenario', (Resolve-Path -LiteralPath $Scenario -ErrorAction Stop).Path,
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
    $runnerExitCode = $LASTEXITCODE
    if ($runnerExitCode -ne 0) {
        try {
            Write-WrapperFailure "Visual verification runner exited with code $runnerExitCode."
        }
        catch {
            [Console]::Error.WriteLine(
                "Could not write merge-evidence failure details: $($_.Exception.Message)")
        }
        exit $runnerExitCode
    }
}
catch {
    $originalError = $_
    try {
        Write-WrapperFailure $originalError.Exception.Message
    }
    catch {
        [Console]::Error.WriteLine(
            "Could not write merge-evidence failure details: $($_.Exception.Message)")
    }
    throw $originalError
}
