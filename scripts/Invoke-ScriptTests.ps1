#Requires -Version 7.0

[CmdletBinding()]
param(
    [string]$TestPath = "tests\scripts",
    [string]$ResultPath = "TestResults\pester\script-tests.xml"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
$pesterVersion = [version]"5.7.1"

function Resolve-RepositoryPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

if ([System.Management.Automation.WildcardPattern]::ContainsWildcardCharacters($TestPath)) {
    throw "Pester test path must not contain wildcard characters: '$TestPath'."
}
if ([System.Management.Automation.WildcardPattern]::ContainsWildcardCharacters($ResultPath)) {
    throw "Pester result path must not contain wildcard characters: '$ResultPath'."
}

$resolvedTestPath = Resolve-RepositoryPath -Path $TestPath
$resolvedResultPath = Resolve-RepositoryPath -Path $ResultPath

if (Test-Path -LiteralPath $resolvedResultPath -PathType Container) {
    throw "Pester result path '$resolvedResultPath' is an existing directory."
}

$pathsMatch = $resolvedTestPath.Equals(
    $resolvedResultPath,
    [System.StringComparison]::OrdinalIgnoreCase)
if ($pathsMatch) {
    throw "Pester result path '$resolvedResultPath' overlaps test input '$resolvedTestPath'."
}
if ([System.IO.Path]::GetExtension($resolvedResultPath) -ine ".xml") {
    throw "Pester result path '$resolvedResultPath' must use the .xml extension."
}

$resultDirectory = Split-Path $resolvedResultPath -Parent
New-Item -ItemType Directory -Force -Path $resultDirectory | Out-Null
if (Test-Path -LiteralPath $resolvedResultPath) {
    Remove-Item -LiteralPath $resolvedResultPath -Force
}

if (-not (Test-Path -LiteralPath $resolvedTestPath)) {
    throw "Pester test path '$resolvedTestPath' does not exist."
}

$exactPester = Get-Module -ListAvailable -Name Pester |
    Where-Object { $_.Version -eq $pesterVersion } |
    Select-Object -First 1
if ($null -eq $exactPester) {
    Install-Module Pester `
        -RequiredVersion $pesterVersion `
        -Repository PSGallery `
        -Scope CurrentUser `
        -Force `
        -ErrorAction Stop
}

Import-Module Pester -RequiredVersion $pesterVersion -Force -ErrorAction Stop
$loadedPester = Get-Module -Name Pester
if ($null -eq $loadedPester -or $loadedPester.Version -ne $pesterVersion) {
    $loadedVersion = if ($null -eq $loadedPester) { "<none>" } else { $loadedPester.Version }
    throw "Expected Pester $pesterVersion, but loaded $loadedVersion."
}

Write-Host "Pester version: $($loadedPester.Version)"

$configuration = New-PesterConfiguration
$configuration.Run.Path = @($resolvedTestPath)
$configuration.Run.PassThru = $true
$configuration.Run.Exit = $false
$configuration.Output.Verbosity = "Detailed"
$configuration.Output.CIFormat = "Auto"
$configuration.TestResult.Enabled = $true
$configuration.TestResult.OutputPath = $resolvedResultPath
$configuration.TestResult.OutputFormat = "NUnitXml"

$result = Invoke-Pester -Configuration $configuration
$pesterExitCode = $LASTEXITCODE

$failedContainerCount = @(
    $result.Containers | Where-Object { $_.Result -eq "Failed" }
).Count

$summaryFormat = (
    "Pester summary: result={0}, total={1}, passed={2}, failed={3}, " +
    "skipped={4}, inconclusive={5}, notRun={6}, failedContainers={7}, pesterExit={8}"
)
Write-Host ($summaryFormat -f
    $result.Result,
    $result.TotalCount,
    $result.PassedCount,
    $result.FailedCount,
    $result.SkippedCount,
    $result.InconclusiveCount,
    $result.NotRunCount,
    $failedContainerCount,
    $pesterExitCode)

$failures = [System.Collections.Generic.List[string]]::new()
if ($result.Result -ne "Passed") {
    $failures.Add("run result was '$($result.Result)'")
}
if ($result.TotalCount -le 0) {
    $failures.Add("no tests were discovered")
}
if ($failedContainerCount -gt 0) {
    $failures.Add("$failedContainerCount test container(s) failed")
}
if ($result.FailedCount -gt 0) {
    $failures.Add("$($result.FailedCount) test(s) failed")
}
if ($result.SkippedCount -gt 0) {
    $failures.Add("$($result.SkippedCount) test(s) were skipped")
}
if ($result.InconclusiveCount -gt 0) {
    $failures.Add("$($result.InconclusiveCount) test(s) were inconclusive")
}
if ($result.NotRunCount -gt 0) {
    $failures.Add("$($result.NotRunCount) test(s) were not run")
}
if (-not (Test-Path -LiteralPath $resolvedResultPath -PathType Leaf)) {
    $failures.Add("Pester did not write '$resolvedResultPath'")
}

if ($failures.Count -gt 0) {
    Write-Error ("Script tests failed policy: " + ($failures -join "; ")) -ErrorAction Continue
    exit 1
}

exit 0
