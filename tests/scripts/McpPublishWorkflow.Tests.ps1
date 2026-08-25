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
        $workflow | Should -Match "(?m)^\s+source_ref:"
        $workflow | Should -Match "ref: main"
        $workflow | Should -Match "origin/main"
        $workflow | Should -Match 'source_revision=\$sourceRevision'
        $workflow | Should -Match 'steps\.preflight\.outputs\.source_revision'
        $workflow | Should -Not -Match 'RepositoryCommit=\$env:GITHUB_SHA'
        $workflow | Should -Match "Validate-McpReleasePublication\.ps1"
        $workflow | Should -Match "Test-McpReleasePublicationInputs"
        $workflow | Should -Match "Resolve-McpPublicationState"
        $workflow | Should -Match "mcp-v"
        $workflow | Should -Match "RecordFailure"
        $workflow | Should -Match "CloseBlocker"
    }

    It "serializes publication and accepts only a source revision in main history" {
        $workflow = Get-Content $script:WorkflowPath -Raw

        $workflow | Should -Match "(?m)^concurrency:"
        $workflow | Should -Match "publish-mcp"
        $workflow | Should -Match "cancel-in-progress: false"
        $workflow | Should -Match "inputs\.source_ref"
        $workflow | Should -Match "merge-base --is-ancestor"
        $workflow | Should -Match "persist-credentials: false"
        $workflow | Should -Match 'APP_TOKEN: \$\{\{ steps\.app-token\.outputs\.token \}\}'
        $workflow | Should -Match "AUTHORIZATION: basic"
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
    }

    It "creates an MCP-prefixed GitHub Release with the package and checksum" {
        $workflow = Get-Content $script:WorkflowPath -Raw

        $workflow | Should -Match '"release", "create", \$tag'
        $workflow | Should -Match '"--target", \$env:SOURCE_REVISION'
        $workflow | Should -Match '\$artifact\.PackagePath'
        $workflow | Should -Match '\$artifact\.ChecksumPath'
        $workflow | Should -Match 'mcp-v\$env:MCP_VERSION'
    }

    It "uses a recoverable draft and an independently anchored annotated tag" {
        $workflow = Get-Content $script:WorkflowPath -Raw

        $workflow | Should -Match "ResumeDraft"
        $workflow | Should -Match '"release", "create", \$tag'
        $workflow | Should -Match "--draft"
        $workflow | Should -Match "gh release upload"
        $workflow | Should -Match "--clobber"
        $workflow | Should -Match "git tag -a"
        $workflow | Should -Match 'sha256: \$env:PACKAGE_SHA256'
        $workflow | Should -Match "Test-McpPackageIntegrity"
        $workflow | Should -Match "steps\.assets\.outputs\.sha256"
        $workflow | Should -Match 'gh release edit \$tag --draft=false --verify-tag --latest=false'
    }

    It "does not depend on NuGet or create an app-tagged release" {
        $workflow = Get-Content $script:WorkflowPath -Raw

        $workflow | Should -Not -Match "id-token: write"
        $workflow | Should -Not -Match "NuGet/login"
        $workflow | Should -Not -Match "NUGET_USER"
        $workflow | Should -Not -Match "dotnet nuget push"
        $workflow | Should -Not -Match "api\.nuget\.org"
        $workflow | Should -Not -Match '(?m)^\s+\$tag = "v\$env:MCP_VERSION"'
        $workflow | Should -Not -Match 'Authorization = "\*{6}"'
    }

    It "contains syntactically valid PowerShell run blocks" {
        $lines = Get-Content $script:WorkflowPath
        for ($index = 0; $index -lt $lines.Count; $index++) {
            if ($lines[$index] -notmatch '^(\s+)run: \|$') {
                continue
            }

            $runIndent = $Matches[1].Length
            $contentIndent = $runIndent + 2
            $scriptLines = [System.Collections.Generic.List[string]]::new()
            for ($cursor = $index + 1; $cursor -lt $lines.Count; $cursor++) {
                $line = $lines[$cursor]
                if (-not [string]::IsNullOrWhiteSpace($line) -and
                    ($line.Length - $line.TrimStart().Length) -le $runIndent) {
                    break
                }
                $scriptLine = if ($line.Length -ge $contentIndent) {
                    $line.Substring($contentIndent)
                }
                else {
                    ""
                }
                $scriptLines.Add($scriptLine)
            }

            $tokens = $null
            $parseErrors = $null
            [System.Management.Automation.Language.Parser]::ParseInput(
                ($scriptLines -join "`n"),
                [ref]$tokens,
                [ref]$parseErrors) | Out-Null
            $parseErrors | Should -BeNullOrEmpty
        }
    }
}
