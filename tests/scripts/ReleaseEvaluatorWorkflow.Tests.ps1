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
        $workflow | Should -Match 'SCHEDULE: \$\{\{ github\.event\.schedule \}\}'
        $workflow | Should -Match '-ScheduledCron \$env:SCHEDULE'
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

    It "uses separate least-privilege tokens for repository mutation and Copilot inference" {
        $workflow = Get-Content $script:WorkflowPath -Raw

        $workflow | Should -Match "actions/create-github-app-token@v2"
        $workflow | Should -Match "RELEASE_AUTOMATION_APP_ID"
        $workflow | Should -Match "RELEASE_AUTOMATION_PRIVATE_KEY"
        $workflow | Should -Match "copilot-requests: write"
        ([regex]::Matches(
                $workflow,
                "npm install -g @github/copilot@1\.0\.80")).Count |
            Should -Be 2
        ([regex]::Matches(
                $workflow,
                "actions/ai-inference@2c43c91ae16266ca159d311430343c67a5ffa222 # v3")).Count |
            Should -Be 2
        ([regex]::Matches($workflow, "provider: copilot")).Count | Should -Be 2
        $workflow | Should -Match "model: gpt-5\.6-luna"
        $workflow | Should -Match 'GITHUB_TOKEN: \$\{\{ github\.token \}\}'
        $workflow | Should -Match (
            "(?s)name: Install Copilot CLI.*?continue-on-error: true.*?" +
            "name: Rewrite App release notes")
        $workflow | Should -Match (
            "(?s)name: Rewrite App release notes.*?continue-on-error: true.*?" +
            "name: Reconcile App Release PR")
        $workflow | Should -Match (
            "(?s)name: Install Copilot CLI.*?continue-on-error: true.*?" +
            "name: Rewrite MCP release notes")
        $workflow | Should -Match (
            "(?s)name: Rewrite MCP release notes.*?continue-on-error: true.*?" +
            "name: Reconcile MCP Release PR")
        $workflow | Should -Not -Match "actions/ai-inference@v1"
        $workflow | Should -Not -Match "COPILOT_GITHUB_TOKEN"
        $workflow | Should -Not -Match "copilot-allow-tools"
        $workflow | Should -Not -Match "models:\s*read"
        $workflow | Should -Not -Match "models\.github\.ai"
        $workflow | Should -Match "persist-credentials: false"
        $workflow | Should -Not -Match "permissions:\s*\n\s+contents: write"
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
        $workflow | Should -Match '\$env:RUNNER_TEMP[\\/]release-plan-app\.json'
        $workflow | Should -Match '\$env:RUNNER_TEMP[\\/]release-notes-prompt-app\.txt'
        $workflow | Should -Match '\$env:RUNNER_TEMP[\\/]release-error-app\.txt'
        $workflow | Should -Match '\$env:RUNNER_TEMP[\\/]release-plan-mcp\.json'
        $workflow | Should -Match '\$env:RUNNER_TEMP[\\/]release-notes-prompt-mcp\.txt'
        $workflow | Should -Match '\$env:RUNNER_TEMP[\\/]release-error-mcp\.txt'
        $workflow | Should -Not -Match '(?m)^\s+-PlanPath artifacts[\\/]'
        $workflow | Should -Not -Match '(?m)^\s+-PromptPath artifacts[\\/]'
        $workflow | Should -Not -Match '(?m)^\s+-FailurePath artifacts[\\/]'
        ([regex]::Matches(
                $workflow,
                "(?s)- name: Upload (?:App|MCP) Release plan\s+if: always\(\)")).Count |
            Should -Be 2
        $workflow | Should -Match "if-no-files-found: warn"
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
