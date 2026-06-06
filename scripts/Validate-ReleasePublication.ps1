<#
.SYNOPSIS
    Validates local inputs for Glasswork Release publication.
#>

function Test-ReleasePublicationInputs {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,

        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $ErrorActionPreference = "Stop"

    if ($Version -notmatch '^\d+\.\d+\.\d+$') {
        throw "Version must be in X.Y.Z form: $Version"
    }

    $appProject = Join-Path $RepoRoot "src\Glasswork.App\Glasswork.csproj"
    if (-not (Test-Path $appProject)) {
        throw "Glasswork app project not found: $appProject"
    }

    [xml]$projectXml = Get-Content $appProject -Raw
    $propertyGroup = $projectXml.Project.PropertyGroup | Select-Object -First 1
    if ($propertyGroup.Version -ne $Version) {
        throw "Glasswork.csproj Version '$($propertyGroup.Version)' does not match requested version '$Version'."
    }
    if ($propertyGroup.InformationalVersion -ne $Version) {
        throw "Glasswork.csproj InformationalVersion '$($propertyGroup.InformationalVersion)' does not match requested version '$Version'."
    }

    $fourPartVersion = "$Version.0"
    if ($propertyGroup.AssemblyVersion -ne $fourPartVersion) {
        throw "Glasswork.csproj AssemblyVersion '$($propertyGroup.AssemblyVersion)' does not match '$fourPartVersion'."
    }
    if ($propertyGroup.FileVersion -ne $fourPartVersion) {
        throw "Glasswork.csproj FileVersion '$($propertyGroup.FileVersion)' does not match '$fourPartVersion'."
    }

    $notesPath = Join-Path $RepoRoot "docs\releases\v$Version.md"
    if (-not (Test-Path $notesPath)) {
        throw "Release notes not found: $notesPath"
    }

    $notes = Get-Content $notesPath -Raw
    if ([string]::IsNullOrWhiteSpace($notes)) {
        throw "Release notes must not be empty: $notesPath"
    }

    $requiredTitle = "# Glasswork v$Version"
    if ($notes -notmatch "(?m)^$([regex]::Escape($requiredTitle))\s*$") {
        throw "Release notes must include title '$requiredTitle'."
    }
    if ($notes -notmatch '(?m)^## Changes\s*$') {
        throw "Release notes must include a '## Changes' section."
    }
    if ($notes -notmatch '(?m)^## Validation\s*$') {
        throw "Release notes must include a '## Validation' section."
    }
}
