<#
.SYNOPSIS
    Tests for the Release workflow contract.
#>

Describe "Release workflow" {
    BeforeAll {
        $script:RepoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $script:WorkflowPath = Join-Path $script:RepoRoot ".github\workflows\release.yml"
    }

    It "exposes only a version input for manual Release publication" {
        $workflow = Get-Content $script:WorkflowPath -Raw

        $workflow | Should -Match "workflow_dispatch:"
        $workflow | Should -Match "(?m)^\s+version:"
        $workflow | Should -Not -Match "(?m)^\s+target_ref:"
        $workflow | Should -Not -Match "(?m)^\s+notes:"
        $workflow | Should -Not -Match "(?m)^\s+prerelease:"
    }

    It "validates inputs, runs gates, and creates the GitHub Release from notes" {
        $workflow = Get-Content $script:WorkflowPath -Raw

        $workflow | Should -Match "Validate-ReleasePublication\.ps1"
        $workflow | Should -Match "Test-ReleasePublicationInputs"
        $workflow | Should -Match 'RELEASE_VERSION: \$\{\{ inputs\.version \}\}'
        $workflow | Should -Match 'Version \$env:RELEASE_VERSION'
        $workflow | Should -Match "Invoke-Pester -Path tests\\scripts -Output Detailed -CI"
        $workflow | Should -Match "Install-Module Pester -RequiredVersion"
        $workflow | Should -Not -Match "SkipPublisherCheck"
        $workflow | Should -Match "dotnet test tests\\Glasswork\.Tests\\Glasswork\.Tests\.csproj"
        $workflow | Should -Match "MSTest\.Parallelize\.Workers=1"
        $workflow | Should -Match "dotnet build src\\Glasswork\.App\\Glasswork\.csproj"
        $workflow | Should -Match "gh release create"
        $workflow | Should -Match '--notes-file \$notesPath'
    }

    It "does not interpolate the version input inside PowerShell scripts" {
        $workflow = Get-Content $script:WorkflowPath -Raw

        $workflow | Should -Not -Match 'Version "\$\{\{ inputs\.version \}\}"'
        $workflow | Should -Not -Match '\$tag = "v\$\{\{ inputs\.version \}\}"'
        $workflow | Should -Not -Match '\$version = "\$\{\{ inputs\.version \}\}"'
    }

    It "treats missing tags and releases as success paths" {
        $workflow = Get-Content $script:WorkflowPath -Raw

        $workflow | Should -Match 'PSNativeCommandUseErrorActionPreference = \$false'
        $workflow | Should -Match 'IsNullOrWhiteSpace\(\$matchingRefs\)'
        $workflow | Should -Match "SkipHttpErrorCheck"
        $workflow | Should -Match "StatusCode -eq 200"
        $workflow | Should -Match "StatusCode -ne 404"
    }
}
