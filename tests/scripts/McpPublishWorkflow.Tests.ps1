<#
.SYNOPSIS
    Tests for the glasswork-mcp publication workflow contract.
#>

Describe "Publish MCP workflow" {
    BeforeAll {
        $script:RepoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $script:WorkflowPath = Join-Path $script:RepoRoot ".github\workflows\publish-mcp.yml"
    }

    It "publishes only an exact committed version from current main" {
        $workflow = Get-Content $script:WorkflowPath -Raw

        $workflow | Should -Match "workflow_dispatch:"
        $workflow | Should -Match "(?m)^\s+version:"
        $workflow | Should -Match "ref: main"
        $workflow | Should -Match "origin/main"
        $workflow | Should -Match 'current_main_revision=\$head'
        $workflow | Should -Match 'steps\.preflight\.outputs\.current_main_revision'
        $workflow | Should -Not -Match 'RepositoryCommit=\$env:GITHUB_SHA'
        $workflow | Should -Match "Validate-McpReleasePublication\.ps1"
        $workflow | Should -Match "Test-McpReleasePublicationInputs"
        $workflow | Should -Match "Resolve-McpPublicationState"
        $workflow | Should -Match "mcp-v"
    }

    It "runs serial tests, Release build, clean pack, and package identity validation" {
        $workflow = Get-Content $script:WorkflowPath -Raw

        $workflow | Should -Match "Invoke-Pester -Path tests\\scripts -Output Detailed -CI"
        $workflow | Should -Match "dotnet test tests\\Glasswork\.Mcp\.Tests\\Glasswork\.Mcp\.Tests\.csproj"
        $workflow | Should -Match "MSTest\.Parallelize\.Workers=1"
        $workflow | Should -Match "dotnet build src\\Glasswork\.Mcp\\Glasswork\.Mcp\.csproj"
        $workflow | Should -Match "dotnet pack src\\Glasswork\.Mcp\\Glasswork\.Mcp\.csproj"
        $workflow | Should -Match "RepositoryCommit"
        $workflow | Should -Match "Test-McpPackageArtifact"
        $workflow | Should -Match "checksum_path"
        $workflow | Should -Match '-OutFile \$packagePath'
        $workflow | Should -Match "-PassThru"
    }

    It "recovers a missing tag from the published package after main advances" {
        $workflow = Get-Content $script:WorkflowPath -Raw

        $workflow | Should -Match "PUBLICATION_STATE"
        $workflow | Should -Match "Get-McpPackageMetadata"
        $workflow | Should -Match "merge-base --is-ancestor"
        $workflow | Should -Match 'source_revision=\$\(\$metadata\.SourceRevision\)'
        $workflow | Should -Match 'SOURCE_REVISION: \$\{\{ steps\.published\.outputs\.source_revision \}\}'
        $workflow | Should -Match 'attempt -le 120'
    }

    It "uses NuGet trusted publishing and never creates an app-visible GitHub Release" {
        $workflow = Get-Content $script:WorkflowPath -Raw

        $workflow | Should -Match "id-token: write"
        $workflow | Should -Match "NuGet/login@v1"
        $workflow | Should -Match "NUGET_USER"
        $workflow | Should -Match "dotnet nuget push"
        $workflow | Should -Match "api\.nuget\.org"
        $workflow | Should -Match "git tag -a"
        $workflow | Should -Not -Match "gh release create"
        $workflow | Should -Not -Match "releases/latest"
    }
}
