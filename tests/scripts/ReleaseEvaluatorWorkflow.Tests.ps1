<#
.SYNOPSIS
    Tests the autonomous release evaluator workflow contract.
#>

Describe "Release evaluator workflow" {
    BeforeAll {
        $script:RepoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $script:WorkflowPath = Join-Path $script:RepoRoot ".github\workflows\evaluate-releases.yml"
    }

    It "uses DST-aware weekday cron entries plus a timezone guard" {
        $workflow = Get-Content $script:WorkflowPath -Raw

        $workflow | Should -Match "'0 16 \* \* 1-5'"
        $workflow | Should -Match "'0 17 \* \* 1-5'"
        $workflow | Should -Match "Test-ReleaseScheduleGate"
        $workflow | Should -Match "force_evaluate"
        $workflow | Should -Match "dry_run"
    }

    It "evaluates App first and runs MCP even if App fails" {
        $workflow = Get-Content $script:WorkflowPath -Raw

        $workflow | Should -Match "(?m)^\s+evaluate-app:"
        $workflow | Should -Match "(?m)^\s+evaluate-mcp:"
        $workflow | Should -Match "needs: \[prepare, evaluate-app\]"
        $workflow | Should -Match "always\(\)"
    }

    It "uses separate least-privilege tokens for repository mutation and inference" {
        $workflow = Get-Content $script:WorkflowPath -Raw

        $workflow | Should -Match "actions/create-github-app-token@v2"
        $workflow | Should -Match "RELEASE_AUTOMATION_APP_ID"
        $workflow | Should -Match "RELEASE_AUTOMATION_PRIVATE_KEY"
        $workflow | Should -Match "copilot-requests: write"
        $workflow | Should -Match "actions/ai-inference@v1"
        $workflow | Should -Match "model: gpt-5\.6-luna"
        $workflow | Should -Match "COPILOT_GITHUB_TOKEN"
        $workflow | Should -Match "persist-credentials: false"
        $workflow | Should -Not -Match "permissions:\s*\n\s+contents: write"
        $workflow | Should -Not -Match "npm install -g @github/copilot"
        $evaluator = Get-Content (
            Join-Path $script:RepoRoot "scripts\Invoke-ReleaseEvaluation.ps1") -Raw
        $evaluator | Should -Match "SignData"
        $evaluator | Should -Match 'commit",'
        $evaluator | Should -Match '\$commitMarker'
        $evaluator | Should -Match "Recoverable signed automation branch"
        $evaluator | Should -Match '"--head", \$branch'
        $evaluator | Should -Match 'elseif \(-not \$ciGreen\)'
    }

    It "honors global and per-stream kill switches" {
        $workflow = Get-Content $script:WorkflowPath -Raw

        $workflow | Should -Match "RELEASE_AUTOMATION_ENABLED"
        $workflow | Should -Match "RELEASE_AUTOMATION_APP_ENABLED"
        $workflow | Should -Match "RELEASE_AUTOMATION_MCP_ENABLED"
    }

    It "records plans summaries and deduplicated blockers" {
        $workflow = Get-Content $script:WorkflowPath -Raw

        $workflow | Should -Match "Invoke-ReleaseEvaluation\.ps1"
        $workflow | Should -Match "RecordFailure"
        $workflow | Should -Match "CloseBlocker"
        $workflow | Should -Match "actions/upload-artifact@v4"
        $workflow | Should -Match "retention-days: 90"
        $workflow | Should -Match "GITHUB_STEP_SUMMARY"
        $workflow | Should -Match "BlockerStage Evaluation"
        $workflow | Should -Match "steps\.plan\.outputs\.dry_run == 'false'"
    }

    It "serializes evaluator runs without cancelling recovery" {
        $workflow = Get-Content $script:WorkflowPath -Raw

        $workflow | Should -Match "(?m)^concurrency:"
        $workflow | Should -Match "release-evaluator"
        $workflow | Should -Match "cancel-in-progress: false"
    }

    It "contains a syntactically valid evaluator script" {
        $scriptPath = Join-Path $script:RepoRoot "scripts\Invoke-ReleaseEvaluation.ps1"
        $tokens = $null
        $parseErrors = $null

        [System.Management.Automation.Language.Parser]::ParseFile(
            $scriptPath,
            [ref]$tokens,
            [ref]$parseErrors) | Out-Null

        $parseErrors | Should -BeNullOrEmpty
    }
}
