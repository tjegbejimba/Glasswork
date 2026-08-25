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

Describe "Resolve-AppPublicationState" {
    BeforeAll {
        $script:AppSourceRevision = "0123456789abcdef0123456789abcdef01234567"
    }

    It "starts a new publication when no release or tag exists" {
        Resolve-AppPublicationState `
            -ReleaseExists $false `
            -ReleaseIsDraft $false `
            -TagExists $false `
            -RequestedSourceRevision $script:AppSourceRevision |
            Should -Be "New"
    }

    It "resumes a matching draft publication" {
        Resolve-AppPublicationState `
            -ReleaseExists $true `
            -ReleaseIsDraft $true `
            -TagExists $false `
            -ReleaseTargetRevision $script:AppSourceRevision `
            -RequestedSourceRevision $script:AppSourceRevision |
            Should -Be "ResumeDraft"
    }

    It "rejects a draft that targets a different source revision" {
        {
            Resolve-AppPublicationState `
                -ReleaseExists $true `
                -ReleaseIsDraft $true `
                -TagExists $false `
                -ReleaseTargetRevision ("f" * 40) `
                -RequestedSourceRevision $script:AppSourceRevision
        } | Should -Throw "*different source revision*"
    }

    It "rejects an orphaned app tag" {
        {
            Resolve-AppPublicationState `
                -ReleaseExists $false `
                -ReleaseIsDraft $false `
                -TagExists $true `
                -RequestedSourceRevision $script:AppSourceRevision
        } | Should -Throw "*tag exists without*"
    }

    It "rejects an already published app release" {
        {
            Resolve-AppPublicationState `
                -ReleaseExists $true `
                -ReleaseIsDraft $false `
                -TagExists $true `
                -ReleaseTargetRevision $script:AppSourceRevision `
                -RequestedSourceRevision $script:AppSourceRevision
        } | Should -Throw "*already published*"
    }
}

Describe "App release asset integrity" {
    It "validates an app package and matching checksum" {
        $packagePath = Join-Path $TestDrive "Glasswork-win-x64.zip"
        $checksumPath = "$packagePath.sha256"
        Set-Content $packagePath "package bytes"
        $sha256 = (Get-FileHash -Algorithm SHA256 -Path $packagePath).Hash.ToLowerInvariant()
        "$sha256  Glasswork-win-x64.zip" | Set-Content $checksumPath

        $result = Test-AppReleaseAssetIntegrity `
            -PackagePath $packagePath `
            -ChecksumPath $checksumPath

        $result.Sha256 | Should -Be $sha256
    }

    It "rejects a checksum that does not match the app package" {
        $packagePath = Join-Path $TestDrive "Glasswork-win-x64.zip"
        $checksumPath = "$packagePath.sha256"
        Set-Content $packagePath "package bytes"
        (("0" * 64) + "  Glasswork-win-x64.zip") | Set-Content $checksumPath

        {
            Test-AppReleaseAssetIntegrity `
                -PackagePath $packagePath `
                -ChecksumPath $checksumPath
        } | Should -Throw "*checksum does not match*"
    }

    It "reads version source revision and checksum from the app tag" {
        $source = "0123456789abcdef0123456789abcdef01234567"
        $sha256 = "a" * 64

        $metadata = ConvertFrom-AppTagMessage -Message @"
Glasswork app publication
version: 1.5.0
commit: $source
sha256: $sha256
"@ -Version "1.5.0"

        $metadata.SourceRevision | Should -Be $source
        $metadata.Sha256 | Should -Be $sha256
    }
}
