#Requires -Version 7.0

<#
.SYNOPSIS
    Validates committed inputs and artifacts for glasswork-mcp publication.
#>

function Test-McpReleasePublicationInputs {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,

        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $ErrorActionPreference = "Stop"

    if ($Version -notmatch '^0\.\d+\.\d+$') {
        throw "MCP version must be a stable 0.x semantic version in X.Y.Z form: $Version"
    }

    $projectPath = Join-Path $RepoRoot "src\Glasswork.Mcp\Glasswork.Mcp.csproj"
    if (-not (Test-Path $projectPath)) {
        throw "Glasswork MCP project not found: $projectPath"
    }

    [xml]$projectXml = Get-Content $projectPath -Raw
    $propertyGroup = $projectXml.Project.PropertyGroup | Select-Object -First 1
    if ($propertyGroup.PackageId -ne "glasswork-mcp") {
        throw "Glasswork MCP PackageId must be 'glasswork-mcp'."
    }
    if ($propertyGroup.Version -ne $Version) {
        throw "Glasswork.Mcp.csproj Version '$($propertyGroup.Version)' does not match requested version '$Version'."
    }

    $changelogPath = Join-Path $RepoRoot "src\Glasswork.Mcp\CHANGELOG.md"
    if (-not (Test-Path $changelogPath)) {
        throw "Glasswork MCP changelog not found: $changelogPath"
    }

    $changelog = Get-Content $changelogPath -Raw
    $topRelease = [regex]::Match(
        $changelog,
        '(?m)^## \[(?<version>[^\]]+)\] — (?<date>[^\r\n]+)\s*$')
    if (-not $topRelease.Success) {
        throw "Glasswork MCP changelog must include a release heading."
    }
    if ($topRelease.Groups["version"].Value -ne $Version -or
        $topRelease.Groups["date"].Value -notmatch '^\d{4}-\d{2}-\d{2}$') {
        throw "Glasswork MCP top release heading must be a dated entry for '$Version'."
    }
}

function Resolve-McpPublicationState {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$ReleaseExists,

        [Parameter(Mandatory = $true)]
        [bool]$ReleaseIsDraft,

        [Parameter(Mandatory = $true)]
        [bool]$TagExists
    )

    if (-not $ReleaseExists -and -not $TagExists) {
        return "New"
    }
    if ($ReleaseExists -and $ReleaseIsDraft) {
        return "ResumeDraft"
    }
    if (-not $ReleaseExists -and $TagExists) {
        throw "MCP integrity tag exists without its draft release."
    }

    throw "MCP GitHub Release is already published for this version."
}

function ConvertFrom-McpTagMessage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message,

        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $versionMatch = [regex]::Match($Message, '(?m)^version: (?<value>0\.\d+\.\d+)\s*$')
    $revisionMatch = [regex]::Match($Message, '(?m)^commit: (?<value>[0-9a-f]{40})\s*$')
    $shaMatch = [regex]::Match($Message, '(?m)^sha256: (?<value>[0-9a-f]{64})\s*$')
    if (-not $versionMatch.Success -or $versionMatch.Groups["value"].Value -ne $Version) {
        throw "MCP tag metadata does not match requested version '$Version'."
    }
    if (-not $revisionMatch.Success) {
        throw "MCP tag metadata is missing a valid source revision."
    }
    if (-not $shaMatch.Success) {
        throw "MCP tag metadata is missing a valid SHA-256 checksum."
    }

    [pscustomobject]@{
        Version        = $Version
        SourceRevision = $revisionMatch.Groups["value"].Value
        Sha256         = $shaMatch.Groups["value"].Value
    }
}

function Get-McpPackageMetadata {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagePath
    )

    $ErrorActionPreference = "Stop"

    if (-not (Test-Path $PackagePath -PathType Leaf)) {
        throw "MCP package not found: $PackagePath"
    }

    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $nuspecEntries = @($archive.Entries | Where-Object { $_.FullName -like "*.nuspec" })
        if ($nuspecEntries.Count -ne 1) {
            throw "MCP package must contain exactly one .nuspec manifest."
        }

        $reader = [System.IO.StreamReader]::new($nuspecEntries[0].Open())
        try {
            [xml]$nuspec = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        $metadata = $nuspec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
        if ($null -eq $metadata) {
            throw "MCP package manifest is missing metadata."
        }

        $packageId = $metadata.SelectSingleNode("*[local-name()='id']").InnerText
        $packageVersion = $metadata.SelectSingleNode("*[local-name()='version']").InnerText
        $repository = $metadata.SelectSingleNode("*[local-name()='repository']")
        if ($null -eq $repository) {
            throw "MCP package manifest is missing repository metadata."
        }
        $packageRevision = $repository.GetAttribute("commit")
        $repositoryUrl = $repository.GetAttribute("url")

        if ($packageId -ne "glasswork-mcp") {
            throw "MCP package ID '$packageId' does not match 'glasswork-mcp'."
        }
        if ($packageVersion -notmatch '^0\.\d+\.\d+$') {
            throw "MCP package version is not a stable 0.x semantic version: $packageVersion"
        }
        if ($packageRevision -notmatch '^[0-9a-f]{40}$') {
            throw "MCP package source revision must be a 40-character lowercase Git commit: $packageRevision"
        }
        if ($repositoryUrl -ne "https://github.com/tjegbejimba/Glasswork") {
            throw "MCP package repository URL '$repositoryUrl' is not the canonical repository."
        }

        return [pscustomobject]@{
            PackageId      = $packageId
            Version        = $packageVersion
            SourceRevision = $packageRevision
            RepositoryUrl  = $repositoryUrl
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Test-McpPackageArtifact {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagePath,

        [Parameter(Mandatory = $true)]
        [string]$Version,

        [Parameter(Mandatory = $true)]
        [string]$SourceRevision
    )

    $ErrorActionPreference = "Stop"
    $metadata = Get-McpPackageMetadata -PackagePath $PackagePath

    if ($metadata.Version -ne $Version) {
        throw "MCP package version '$($metadata.Version)' does not match requested version '$Version'."
    }

    if ($metadata.SourceRevision -ne $SourceRevision) {
        throw "MCP package source revision '$($metadata.SourceRevision)' does not match '$SourceRevision'."
    }

    $sha256 = (Get-FileHash -Algorithm SHA256 -Path $PackagePath).Hash.ToLowerInvariant()
    $checksumPath = "$PackagePath.sha256"
    "$sha256  $(Split-Path $PackagePath -Leaf)" | Set-Content $checksumPath

    [pscustomobject]@{
        PackagePath   = $PackagePath
        ChecksumPath  = $checksumPath
        Sha256        = $sha256
        SourceRevision = $SourceRevision
    }
}

function Test-McpPackageIntegrity {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagePath,

        [Parameter(Mandatory = $true)]
        [string]$ChecksumPath,

        [Parameter(Mandatory = $true)]
        [string]$Version,

        [Parameter(Mandatory = $true)]
        [string]$SourceRevision,

        [string]$ExpectedSha256
    )

    $metadata = Get-McpPackageMetadata -PackagePath $PackagePath
    if ($metadata.Version -ne $Version) {
        throw "MCP package version '$($metadata.Version)' does not match requested version '$Version'."
    }
    if ($metadata.SourceRevision -ne $SourceRevision) {
        throw "MCP package source revision '$($metadata.SourceRevision)' does not match '$SourceRevision'."
    }
    if (-not (Test-Path $ChecksumPath -PathType Leaf)) {
        throw "MCP checksum not found: $ChecksumPath"
    }

    $sha256 = (Get-FileHash -Algorithm SHA256 -Path $PackagePath).Hash.ToLowerInvariant()
    $checksum = (Get-Content $ChecksumPath -Raw).Trim()
    $packageName = Split-Path $PackagePath -Leaf
    if ($checksum -ne "$sha256  $packageName") {
        throw "MCP package checksum does not match the downloaded package."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256) -and
        $sha256 -ne $ExpectedSha256) {
        throw "MCP package checksum does not match the independent integrity anchor."
    }

    [pscustomobject]@{
        PackagePath    = $PackagePath
        ChecksumPath   = $ChecksumPath
        Version        = $metadata.Version
        SourceRevision = $metadata.SourceRevision
        Sha256         = $sha256
    }
}
