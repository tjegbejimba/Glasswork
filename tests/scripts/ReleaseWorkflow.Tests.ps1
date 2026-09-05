<#
.SYNOPSIS
    Tests for the Release workflow contract.
#>

Describe "Release workflow" {
    BeforeAll {
        $script:RepoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $script:WorkflowPath = Join-Path $script:RepoRoot ".github\workflows\release.yml"
    }

    It "accepts a version and optional pinned source revision" {
        $workflow = Get-Content $script:WorkflowPath -Raw

        $workflow | Should -Match "workflow_dispatch:"
        $workflow | Should -Match "(?m)^\s+version:"
        $workflow | Should -Match "(?m)^\s+source_ref:"
        $workflow | Should -Not -Match "(?m)^\s+notes:"
        $workflow | Should -Not -Match "(?m)^\s+prerelease:"
    }

    It "validates inputs, runs gates, packages the app, and creates the GitHub Release" {
        $workflow = Get-Content $script:WorkflowPath -Raw

        $workflow | Should -Match "Validate-ReleasePublication\.ps1"
        $workflow | Should -Match "Test-ReleasePublicationInputs"
        $workflow | Should -Match 'RELEASE_VERSION: \$\{\{ inputs\.version \}\}'
        $workflow | Should -Match 'Version \$env:RELEASE_VERSION'
        $workflow | Should -Match "pwsh -NoProfile -File scripts\\Invoke-ScriptTests\.ps1"
        $workflow | Should -Match "-TestPath tests\\scripts"
        $workflow | Should -Match "-ResultPath TestResults\\pester\\script-tests\.xml"
        $workflow | Should -Match "(?s)Upload App Release script test diagnostics.*?if: always\(\)"
        $workflow | Should -Match "app-release-script-test-diagnostics"
        $workflow | Should -Match "if-no-files-found: warn"
        $workflow | Should -Not -Match "Install-Module Pester"
        $workflow | Should -Not -Match "Invoke-Pester"
        $workflow | Should -Not -Match "SkipPublisherCheck"
        $workflow | Should -Match "dotnet test tests\\Glasswork\.Tests\\Glasswork\.Tests\.csproj"
        $workflow | Should -Match "MSTest\.Parallelize\.Workers=1"
        $workflow | Should -Match "dotnet publish src\\Glasswork\.App\\Glasswork\.csproj"
        $workflow | Should -Match '--logger "trx;LogFileName=canvas-host\.trx"'
        $workflow | Should -Match "--results-directory TestResults\\canvas-host"
        $workflow | Should -Match "(?s)Upload canvas host test diagnostics.*?if: always\(\)"
        $workflow | Should -Match "canvas-host-test-diagnostics"
        $workflow | Should -Match "TestResults\\canvas-host\\canvas-host\.trx"
        $workflow | Should -Match '(?m)^[ \t]+TestResults\\canvas-host\\diagnostics\\\$\{\{ github\.run_id \}\}-\$\{\{ github\.run_attempt \}\}\\\*\.json[ \t]*\r?$'
        $workflow | Should -Not -Match "TestResults\\canvas-host\\diagnostics\\\*\.json"
        $workflow | Should -Not -Match "TestResults\\canvas-host\\diagnostics\\\*\*"
        $workflow | Should -Match "New-ReleasePackage"
        $workflow | Should -Match "Resolve-AppPublicationState"
        $workflow | Should -Match "ResumeDraft"
        $workflow | Should -Match "--draft"
        $workflow | Should -Match "gh release upload"
        $workflow | Should -Match "--clobber"
        $workflow | Should -Match "Test-AppReleaseAssetIntegrity"
        $workflow | Should -Match '\$expectedSha256 = \$env:LOCAL_PACKAGE_SHA256'
        $workflow | Should -Match "Tagged draft App Release is missing its anchored assets"
        $workflow | Should -Match "git tag -a"
        $workflow | Should -Match "gh release edit"
        $workflow | Should -Match '--notes-file \$notesPath'
        $workflow | Should -Match "Glasswork-win-x64\.zip"
        $workflow | Should -Match "Glasswork-win-x64\.zip\.sha256"
        $workflow | Should -Match "RecordFailure"
        $workflow | Should -Match "CloseBlocker"
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
        $workflow | Should -Match 'releaseExists = \$releases\.Count -eq 1'
        $workflow | Should -Match "Resolve-AppPublicationState"
    }

    It "pins publication to an exact commit in main history and serializes runs" {
        $workflow = Get-Content $script:WorkflowPath -Raw

        $workflow | Should -Match "source_ref"
        $workflow | Should -Match "merge-base --is-ancestor"
        $workflow | Should -Match "source_revision"
        $workflow | Should -Match "git checkout --detach"
        $workflow | Should -Match "(?m)^concurrency:"
        $workflow | Should -Match "publish-app"
        $workflow | Should -Match "cancel-in-progress: false"
        $workflow | Should -Match "persist-credentials: false"
        $workflow | Should -Match 'APP_TOKEN: \$\{\{ steps\.app-token\.outputs\.token \}\}'
        $workflow | Should -Match "AUTHORIZATION: basic"
    }
}
