<#
.SYNOPSIS
    Tests the automation Release PR merge-to-publication contract.
#>

Describe "Release publication dispatch workflow" {
    BeforeAll {
        $script:RepoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $script:WorkflowPath = Join-Path $script:RepoRoot ".github\workflows\release-publication.yml"
    }

    It "runs only for merged automation-created release pull requests" {
        $workflow = Get-Content $script:WorkflowPath -Raw

        $workflow | Should -Match "pull_request:"
        $workflow | Should -Match "types: \[closed\]"
        $workflow | Should -Match "github\.event\.pull_request\.merged"
        $workflow | Should -Match "release-automation"
        $workflow | Should -Match "release-automation:"
        $workflow | Should -Match "app-slug"
    }

    It "uses the dedicated GitHub App token and honors kill switches" {
        $workflow = Get-Content $script:WorkflowPath -Raw

        $workflow | Should -Match "actions/create-github-app-token@v2"
        $workflow | Should -Match "RELEASE_AUTOMATION_APP_ID"
        $workflow | Should -Match "RELEASE_AUTOMATION_PRIVATE_KEY"
        $workflow | Should -Match "RELEASE_AUTOMATION_ENABLED"
        $workflow | Should -Match "RELEASE_AUTOMATION_APP_ENABLED"
        $workflow | Should -Match "RELEASE_AUTOMATION_MCP_ENABLED"
        $workflow | Should -Not -Match "admin"
        $workflow | Should -Not -Match "bypass"
    }

    It "dispatches the matching publication workflow at the exact merge commit" {
        $workflow = Get-Content $script:WorkflowPath -Raw

        $workflow | Should -Match "merge_commit_sha"
        $workflow | Should -Match "pull_request\.head\.sha"
        $workflow | Should -Match 'git/commits/\$env:PR_HEAD_SHA'
        $workflow | Should -Match "workflow run release\.yml"
        $workflow | Should -Match "workflow run publish-mcp\.yml"
        $workflow | Should -Match "source_ref="
        $workflow | Should -Match "version="
        $workflow | Should -Match "release:app"
        $workflow | Should -Match "release:mcp"
        $workflow | Should -Match "exactly its controlled labels"
        $workflow | Should -Match "Test-ReleasePrChangedFiles"
        $workflow | Should -Match "Test-ReleaseProjectVersionChange"
        $workflow | Should -Match "signed marker"
        $workflow | Should -Match "VerifyData"
        $workflow | Should -Match "commit and body markers disagree"
        $workflow | Should -Match "body contains human or conflicting edits"
        $workflow | Should -Match "title contains human or conflicting edits"
        $workflow | Should -Match 'pulls/\$env:PR_NUMBER/files'
        $workflow | Should -Match "BlockerStage Publication"
    }

    It "serializes dispatch handling" {
        $workflow = Get-Content $script:WorkflowPath -Raw

        $workflow | Should -Match "(?m)^concurrency:"
        $workflow | Should -Match "release-publication-dispatch"
        $workflow | Should -Match "cancel-in-progress: false"
    }
}
