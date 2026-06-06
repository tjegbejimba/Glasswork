<#
.SYNOPSIS
    Tests for Release publication input validation.
#>

BeforeAll {
    $scriptRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    . (Join-Path $scriptRoot "scripts\Validate-ReleasePublication.ps1")
}

Describe "Test-ReleasePublicationInputs" {
    BeforeEach {
        $RepoRoot = Join-Path $TestDrive "repo"
        New-Item -ItemType Directory -Force -Path (Join-Path $RepoRoot "src\Glasswork.App") | Out-Null
        New-Item -ItemType Directory -Force -Path (Join-Path $RepoRoot "docs\releases") | Out-Null
    }

    It "accepts a release whose version, app metadata, and notes match" {
        @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <Version>1.4.2</Version>
    <AssemblyVersion>1.4.2.0</AssemblyVersion>
    <FileVersion>1.4.2.0</FileVersion>
    <InformationalVersion>1.4.2</InformationalVersion>
  </PropertyGroup>
</Project>
"@ | Set-Content (Join-Path $RepoRoot "src\Glasswork.App\Glasswork.csproj")

        "# Glasswork v1.4.2`n`nTest release summary.`n`n## Changes`n`n- Test release.`n`n## Validation`n`n- Release workflow gates pass.`n" |
            Set-Content (Join-Path $RepoRoot "docs\releases\v1.4.2.md")

        { Test-ReleasePublicationInputs -RepoRoot $RepoRoot -Version "1.4.2" } |
            Should -Not -Throw
    }

    It "rejects a release when app metadata does not match the requested version" {
        @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <Version>1.4.1</Version>
    <AssemblyVersion>1.4.1.0</AssemblyVersion>
    <FileVersion>1.4.1.0</FileVersion>
    <InformationalVersion>1.4.1</InformationalVersion>
  </PropertyGroup>
</Project>
"@ | Set-Content (Join-Path $RepoRoot "src\Glasswork.App\Glasswork.csproj")

        "# Glasswork v1.4.2`n`n## Changes`n`n- Test release.`n" |
            Set-Content (Join-Path $RepoRoot "docs\releases\v1.4.2.md")

        { Test-ReleasePublicationInputs -RepoRoot $RepoRoot -Version "1.4.2" } |
            Should -Throw "*does not match requested version '1.4.2'*"
    }

    It "rejects release notes that do not follow the required template" {
        @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <Version>1.4.2</Version>
    <AssemblyVersion>1.4.2.0</AssemblyVersion>
    <FileVersion>1.4.2.0</FileVersion>
    <InformationalVersion>1.4.2</InformationalVersion>
  </PropertyGroup>
</Project>
"@ | Set-Content (Join-Path $RepoRoot "src\Glasswork.App\Glasswork.csproj")

        "# Glasswork v1.4.2`n`n## Changes`n`n- Missing validation section.`n" |
            Set-Content (Join-Path $RepoRoot "docs\releases\v1.4.2.md")

        { Test-ReleasePublicationInputs -RepoRoot $RepoRoot -Version "1.4.2" } |
            Should -Throw "*Release notes must include*"
    }
}
