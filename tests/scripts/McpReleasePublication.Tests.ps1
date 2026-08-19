<#
.SYNOPSIS
    Tests for glasswork-mcp release publication input validation.
#>

BeforeAll {
    $script:RepoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    . (Join-Path $script:RepoRoot "scripts\Validate-McpReleasePublication.ps1")

    function New-TestMcpPackage {
        param(
            [Parameter(Mandatory = $true)]
            [string]$Path,

            [string]$Version = "0.11.0",

            [string]$SourceRevision = "0123456789abcdef0123456789abcdef01234567"
        )

        $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Create)
        try {
            $archive = [System.IO.Compression.ZipArchive]::new(
                $stream,
                [System.IO.Compression.ZipArchiveMode]::Create,
                $false)
            try {
                $entry = $archive.CreateEntry("glasswork-mcp.nuspec")
                $writer = [System.IO.StreamWriter]::new($entry.Open())
                try {
                    $writer.Write(@"
<?xml version="1.0"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>glasswork-mcp</id>
    <version>$Version</version>
    <repository type="git" url="https://github.com/tjegbejimba/Glasswork" commit="$SourceRevision" />
  </metadata>
</package>
"@)
                }
                finally {
                    $writer.Dispose()
                }
            }
            finally {
                $archive.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
    }
}

Describe "Test-McpReleasePublicationInputs" {
    BeforeEach {
        $RepoRoot = Join-Path $TestDrive "repo"
        New-Item -ItemType Directory -Force -Path (Join-Path $RepoRoot "src\Glasswork.Mcp") | Out-Null

        @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>glasswork-mcp</PackageId>
    <Version>0.11.0</Version>
  </PropertyGroup>
</Project>
"@ | Set-Content (Join-Path $RepoRoot "src\Glasswork.Mcp\Glasswork.Mcp.csproj")

        @"
# Changelog — glasswork-mcp

## [0.11.0] — 2026-08-19

### Added

- Exact build identity reporting.
"@ | Set-Content (Join-Path $RepoRoot "src\Glasswork.Mcp\CHANGELOG.md")
    }

    It "accepts a committed 0.x version with a dated matching changelog entry" {
        { Test-McpReleasePublicationInputs -RepoRoot $RepoRoot -Version "0.11.0" } |
            Should -Not -Throw
    }

    It "rejects a requested version that is not the top changelog release" {
        @"
# Changelog — glasswork-mcp

## [0.12.0] — 2026-08-20

- Newer work.

## [0.11.0] — 2026-08-19

- Older work.
"@ | Set-Content (Join-Path $RepoRoot "src\Glasswork.Mcp\CHANGELOG.md")

        { Test-McpReleasePublicationInputs -RepoRoot $RepoRoot -Version "0.11.0" } |
            Should -Throw "*top release heading*"
    }
}

Describe "Resolve-McpPublicationState" {
    It "returns New when neither the package nor MCP tag exists" {
        Resolve-McpPublicationState -PackageExists $false -TagExists $false |
            Should -Be "New"
    }

    It "returns RecoverTag when the immutable package exists without its MCP tag" {
        Resolve-McpPublicationState -PackageExists $true -TagExists $false |
            Should -Be "RecoverTag"
    }

    It "rejects an already-published version" {
        { Resolve-McpPublicationState -PackageExists $true -TagExists $true } |
            Should -Throw "*package and tag already exist*"
    }

    It "rejects a tag with no matching immutable package" {
        { Resolve-McpPublicationState -PackageExists $false -TagExists $true } |
            Should -Throw "*tag exists without its package*"
    }
}

Describe "Test-McpPackageArtifact" {
    It "validates package identity and writes durable SHA-256 metadata" {
        $packagePath = Join-Path $TestDrive "glasswork-mcp.0.11.0.nupkg"
        $revision = "0123456789abcdef0123456789abcdef01234567"
        New-TestMcpPackage -Path $packagePath -SourceRevision $revision

        $result = Test-McpPackageArtifact `
            -PackagePath $packagePath `
            -Version "0.11.0" `
            -SourceRevision $revision

        $result.SourceRevision | Should -Be $revision
        $result.Sha256 | Should -Match '^[0-9a-f]{64}$'
        $result.ChecksumPath | Should -Be "$packagePath.sha256"
        Get-Content $result.ChecksumPath -Raw |
            Should -Match "^[0-9a-f]{64}  glasswork-mcp\.0\.11\.0\.nupkg\r?\n?$"
    }
}
