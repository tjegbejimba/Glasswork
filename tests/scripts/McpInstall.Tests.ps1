<#
.SYNOPSIS
    Tests for exact-version glasswork-mcp installation.
#>

BeforeAll {
    $script:RepoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    . (Join-Path $script:RepoRoot "scripts\Install-McpTool.ps1")

    function New-TestInstallPackage {
        param(
            [Parameter(Mandatory = $true)]
            [string]$Path,

            [string]$Version = "0.11.0",

            [string]$SourceRevision = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
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

Describe "Install-GlassworkMcp" {
    BeforeEach {
        $script:IdentityReadCount = 0
        $script:ExpectedIdentity = "0.11.0+bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"

        Mock Get-McpInstallPackage {
            [pscustomobject]@{
                PackagePath   = "C:\verified\glasswork-mcp.0.11.0.nupkg"
                Version       = "0.11.0"
                SourceRevision = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                Sha256        = ("b" * 64)
            }
        }

        Mock Install-McpToolToStaging {
            $script:ExpectedIdentity
        }
        Mock Test-McpToolInstalled { $true }
        Mock Get-McpInstalledIdentity {
            $script:IdentityReadCount++
            if ($script:IdentityReadCount -eq 1) {
                return "0.11.0+aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            }

            return $script:ExpectedIdentity
        }
        Mock Remove-McpInstalledTool
        Mock Install-McpTargetTool
    }

    It "replaces stale same-version bits after staging the expected build identity" {
        $result = Install-GlassworkMcp `
            -Version "0.11.0" `
            -PackagePath "C:\incoming\glasswork-mcp.0.11.0.nupkg"

        $result.Status | Should -Be "Updated"
        $result.Identity | Should -Be $script:ExpectedIdentity
        Should -Invoke Install-McpToolToStaging -Times 1 -Exactly
        Should -Invoke Remove-McpInstalledTool -Times 1 -Exactly
        Should -Invoke Install-McpTargetTool -Times 1 -Exactly
    }

    It "replaces an installed legacy build that cannot report a build identity" {
        Mock Get-McpInstalledIdentity {
            $script:IdentityReadCount++
            if ($script:IdentityReadCount -eq 1) {
                return $null
            }

            return $script:ExpectedIdentity
        }

        $result = Install-GlassworkMcp `
            -Version "0.11.0" `
            -PackagePath "C:\incoming\glasswork-mcp.0.11.0.nupkg"

        $result.Status | Should -Be "Updated"
        Should -Invoke Remove-McpInstalledTool -Times 1 -Exactly
        Should -Invoke Install-McpTargetTool -Times 1 -Exactly
    }

    It "reports a locked active MCP process without exposing terminal output" {
        Mock Remove-McpInstalledTool {
            throw "[31;1mAccess to the path 'C:\Users\test\.dotnet\tools\.store\glasswork-mcp\0.10.0' is denied.[0m"
        }

        {
            Install-GlassworkMcp `
                -Version "0.11.0" `
                -PackagePath "C:\incoming\glasswork-mcp.0.11.0.nupkg"
        } | Should -Throw "*Close active Copilot or agent sessions, then retry the MCP update.*"
        Should -Invoke Install-McpTargetTool -Times 0 -Exactly
    }

    It "preserves unrelated uninstall failures" {
        Mock Remove-McpInstalledTool {
            throw "Failed to uninstall tool package 'glasswork-mcp': There is not enough space on the disk."
        }

        {
            Install-GlassworkMcp `
                -Version "0.11.0" `
                -PackagePath "C:\incoming\glasswork-mcp.0.11.0.nupkg"
        } | Should -Throw "*not enough space on the disk*"
        Should -Invoke Install-McpTargetTool -Times 0 -Exactly
    }
}

Describe "install-mcp.ps1 entry point" {
    It "requires PowerShell 7 so unsupported hosts fail before installation" {
        $entryPoint = Get-Content (Join-Path $script:RepoRoot "scripts\install-mcp.ps1") -Raw

        $entryPoint | Should -Match '(?m)^#Requires -Version 7\.0\r?$'
    }
}

Describe "Get-McpInstallPackage" {
    It "copies and validates an exact local package before installation" {
        $packagePath = Join-Path $TestDrive "glasswork-mcp.0.11.0.nupkg"
        $workingDirectory = Join-Path $TestDrive "work"
        New-Item -ItemType Directory -Path $workingDirectory | Out-Null
        New-TestInstallPackage -Path $packagePath

        $package = Get-McpInstallPackage `
            -Version "0.11.0" `
            -PackagePath $packagePath `
            -WorkingDirectory $workingDirectory

        $package.Version | Should -Be "0.11.0"
        $package.SourceRevision | Should -Be "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        $package.Sha256 | Should -Match '^[0-9a-f]{64}$'
        $package.PackagePath | Should -Exist
        $package.PackagePath | Should -Not -Be $packagePath
    }

    It "downloads and verifies exact GitHub Release assets" {
        $sourcePackage = Join-Path $TestDrive "source-glasswork-mcp.0.11.0.nupkg"
        $workingDirectory = Join-Path $TestDrive "github-work"
        New-Item -ItemType Directory -Path $workingDirectory | Out-Null
        New-TestInstallPackage -Path $sourcePackage
        $sha256 = (Get-FileHash -Algorithm SHA256 $sourcePackage).Hash.ToLowerInvariant()

        Mock Get-McpPublishedMetadata {
            [pscustomobject]@{
                Version             = "0.11.0"
                SourceRevision      = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                Sha256             = $sha256
                PackageDownloadUrl  = "https://github.test/package"
                ChecksumDownloadUrl = "https://github.test/checksum"
            }
        }
        Mock Invoke-WebRequest {
            param($Uri, $OutFile)
            if ($Uri -eq "https://github.test/package") {
                Copy-Item $sourcePackage $OutFile
            }
            else {
                "$sha256  glasswork-mcp.0.11.0.nupkg" | Set-Content $OutFile
            }
        }

        $package = Get-McpInstallPackage `
            -Version "0.11.0" `
            -WorkingDirectory $workingDirectory

        $package.SourceRevision | Should -Be "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        $package.Sha256 | Should -Be $sha256
        Should -Invoke Invoke-WebRequest -Times 2 -Exactly
    }
}

Describe "Get-McpPublishedMetadata" {
    It "anchors GitHub Release assets to annotated tag checksum metadata" {
        $revision = "b" * 40
        $sha256 = "c" * 64
        Mock Invoke-RestMethod {
            param($Uri)
            if ($Uri -like "*/releases/tags/*") {
                return [pscustomobject]@{
                    tag_name = "mcp-v0.11.0"
                    draft = $false
                    prerelease = $false
                    assets = @(
                        [pscustomobject]@{
                            name = "glasswork-mcp.0.11.0.nupkg"
                            browser_download_url = "https://github.test/package"
                        },
                        [pscustomobject]@{
                            name = "glasswork-mcp.0.11.0.nupkg.sha256"
                            browser_download_url = "https://github.test/checksum"
                        })
                }
            }
            if ($Uri -like "*/git/ref/tags/*") {
                return [pscustomobject]@{
                    object = [pscustomobject]@{ type = "tag"; sha = ("d" * 40) }
                }
            }
            return [pscustomobject]@{
                message = "version: 0.11.0`ncommit: $revision`nsha256: $sha256"
                object = [pscustomobject]@{ type = "commit"; sha = $revision }
            }
        }

        $metadata = Get-McpPublishedMetadata -Version "0.11.0"

        $metadata.SourceRevision | Should -Be $revision
        $metadata.Sha256 | Should -Be $sha256
        $metadata.PackageDownloadUrl | Should -Be "https://github.test/package"
        Should -Invoke Invoke-RestMethod -Times 3 -Exactly
    }
}

Describe "Install-GlassworkMcp integration" {
    It "replaces a disposable same-version install built from an older source revision" {
        $projectPath = Join-Path $script:RepoRoot "src\Glasswork.Mcp\Glasswork.Mcp.csproj"
        [xml]$project = Get-Content $projectPath -Raw
        $version = ($project.Project.PropertyGroup | Select-Object -First 1).Version
        $oldRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        $newRevision = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        $oldFeed = Join-Path $TestDrive "old-feed"
        $newFeed = Join-Path $TestDrive "new-feed"
        $toolPath = Join-Path $TestDrive "tool"
        New-Item -ItemType Directory -Force -Path $oldFeed, $newFeed | Out-Null

        & dotnet pack $projectPath `
            --configuration Release `
            --output $oldFeed `
            --nologo `
            --verbosity quiet `
            "-p:RepositoryCommit=$oldRevision"
        $LASTEXITCODE | Should -Be 0

        & dotnet pack $projectPath `
            --configuration Release `
            --output $newFeed `
            --nologo `
            --verbosity quiet `
            "-p:RepositoryCommit=$newRevision"
        $LASTEXITCODE | Should -Be 0

        $previousNuGetPackages = $env:NUGET_PACKAGES
        $env:NUGET_PACKAGES = Join-Path $TestDrive "initial-nuget-packages"
        try {
            & dotnet tool install glasswork-mcp `
                --tool-path $toolPath `
                --version $version `
                --source $oldFeed `
                --no-cache `
                --disable-parallel
            $LASTEXITCODE | Should -Be 0
        }
        finally {
            $env:NUGET_PACKAGES = $previousNuGetPackages
        }

        $packagePath = Join-Path $newFeed "glasswork-mcp.$version.nupkg"
        $result = Install-GlassworkMcp `
            -Version $version `
            -PackagePath $packagePath `
            -ToolPath $toolPath

        $result.Status | Should -Be "Updated"
        $result.Identity | Should -Be "$version+$newRevision"
        Get-McpExecutableIdentity -ExecutablePath (Get-McpToolExecutablePath -ToolPath $toolPath) |
            Should -Be "$version+$newRevision"
    }
}
