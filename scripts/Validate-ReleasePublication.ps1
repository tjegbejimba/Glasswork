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

function Resolve-AppPublicationState {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$ReleaseExists,

        [Parameter(Mandatory = $true)]
        [bool]$ReleaseIsDraft,

        [Parameter(Mandatory = $true)]
        [bool]$TagExists,

        [string]$ReleaseTargetRevision,

        [Parameter(Mandatory = $true)]
        [string]$RequestedSourceRevision
    )

    if ($RequestedSourceRevision -notmatch '^[0-9a-f]{40}$') {
        throw "App publication source revision is invalid: '$RequestedSourceRevision'."
    }

    if (-not $ReleaseExists -and -not $TagExists) {
        return "New"
    }

    if ($ReleaseExists -and $ReleaseIsDraft) {
        if ($ReleaseTargetRevision -ne $RequestedSourceRevision) {
            throw "Existing draft App Release targets a different source revision."
        }

        return "ResumeDraft"
    }

    if (-not $ReleaseExists -and $TagExists) {
        throw "App integrity tag exists without its draft release."
    }

    throw "App GitHub Release is already published for this version."
}

function ConvertFrom-AppTagMessage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message,

        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $versionMatch = [regex]::Match($Message, '(?m)^version: (?<value>\d+\.\d+\.\d+)\s*$')
    $revisionMatch = [regex]::Match($Message, '(?m)^commit: (?<value>[0-9a-f]{40})\s*$')
    $shaMatch = [regex]::Match($Message, '(?m)^sha256: (?<value>[0-9a-f]{64})\s*$')
    if (-not $versionMatch.Success -or $versionMatch.Groups["value"].Value -ne $Version) {
        throw "App tag metadata does not match requested version '$Version'."
    }
    if (-not $revisionMatch.Success) {
        throw "App tag metadata is missing a valid source revision."
    }
    if (-not $shaMatch.Success) {
        throw "App tag metadata is missing a valid SHA-256 checksum."
    }

    [pscustomobject]@{
        Version        = $Version
        SourceRevision = $revisionMatch.Groups["value"].Value
        Sha256         = $shaMatch.Groups["value"].Value
    }
}

function Test-AppReleaseAssetIntegrity {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagePath,

        [Parameter(Mandatory = $true)]
        [string]$ChecksumPath,

        [string]$ExpectedSha256
    )

    if (-not (Test-Path $PackagePath -PathType Leaf)) {
        throw "App release package not found: $PackagePath"
    }
    if (-not (Test-Path $ChecksumPath -PathType Leaf)) {
        throw "App release checksum not found: $ChecksumPath"
    }

    $sha256 = (Get-FileHash -Algorithm SHA256 -Path $PackagePath).Hash.ToLowerInvariant()
    $checksum = (Get-Content $ChecksumPath -Raw).Trim().ToLowerInvariant()
    $packageName = Split-Path $PackagePath -Leaf
    if ($checksum -ne "$sha256  $packageName") {
        throw "App release checksum does not match the downloaded package."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256) -and
        $sha256 -ne $ExpectedSha256.ToLowerInvariant()) {
        throw "App release checksum does not match the independent integrity anchor."
    }

    [pscustomobject]@{
        PackagePath  = $PackagePath
        ChecksumPath = $ChecksumPath
        Sha256       = $sha256
    }
}
