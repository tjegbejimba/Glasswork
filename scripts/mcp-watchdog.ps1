#requires -Version 7
<#
.SYNOPSIS
    Auto-merges Copilot PRs and chains MCP server milestones (#87, #89, #88, #90, #91).

.DESCRIPTION
    Polls every $PollIntervalSec. For each open Copilot PR matching a tracked
    milestone, fetches it locally, runs `dotnet test` on the relevant test
    projects, and if everything passes, marks ready and squash-merges. When all
    of a milestone's dependencies close, assigns Copilot to it with the
    standard steering comment.

    Stops automatically when all 5 issues are closed, or when 10 consecutive
    iterations error out.

.NOTES
    Run via:
      pwsh -NoProfile -File scripts\mcp-watchdog.ps1

    Log file: .mcp-watchdog.log (gitignored).
    Lock file: .mcp-watchdog.lock — prevents two instances.

    Keep your machine awake. `powercfg /requests` won't help here; use
    `presentationsettings /start` or just don't let it sleep.
#>

$ErrorActionPreference = 'Continue'

# ---- Config ----------------------------------------------------------------

$Repo            = 'tjegbejimba/Glasswork'
$RepoPath        = 'C:\Users\toegbeji\Repos\Glasswork'
$LogPath         = Join-Path $RepoPath '.mcp-watchdog.log'
$LockPath        = Join-Path $RepoPath '.mcp-watchdog.lock'
$PollIntervalSec = 180
$MaxConsecErrors = 10

# Known-flaky tests to skip during automated runs. These pass locally on a
# clean machine but timing-sensitive assertions fail under the watchdog's
# back-to-back test runs. Tracked separately; not a license to ignore real bugs.
$FlakyTests = @(
    'DebouncesBurstsIntoOneEvent'
    'Trigger_FiresAgainAfterQuietPeriodElapses'
)

# Milestone chain. DependsOn = list of issue numbers that must be CLOSED before
# this milestone can be assigned. TestProjects = projects to run dotnet test
# against before merging the PR; missing projects are skipped (not failed).
$Milestones = @(
    @{ Issue = 87; DependsOn = @();      TestProjects = @('tests\Glasswork.Tests\Glasswork.Tests.csproj') }
    @{ Issue = 89; DependsOn = @();      TestProjects = @('tests\Glasswork.Tests\Glasswork.Tests.csproj', 'tests\Glasswork.Mcp.Tests\Glasswork.Mcp.Tests.csproj') }
    @{ Issue = 88; DependsOn = @(87,89); TestProjects = @('tests\Glasswork.Tests\Glasswork.Tests.csproj', 'tests\Glasswork.Mcp.Tests\Glasswork.Mcp.Tests.csproj') }
    @{ Issue = 90; DependsOn = @(88);    TestProjects = @('tests\Glasswork.Tests\Glasswork.Tests.csproj', 'tests\Glasswork.Mcp.Tests\Glasswork.Mcp.Tests.csproj') }
    @{ Issue = 91; DependsOn = @(88);    TestProjects = @('tests\Glasswork.Tests\Glasswork.Tests.csproj', 'tests\Glasswork.Mcp.Tests\Glasswork.Mcp.Tests.csproj') }
    @{ Issue = 96; DependsOn = @();      TestProjects = @('tests\Glasswork.Tests\Glasswork.Tests.csproj') }
)

$SteeringBody = @'
@copilot Implement this issue end-to-end and open a PR.

**Process:**
- Follow `.github/copilot-instructions.md` (hard rules, build constraints).
- Follow `.github/skills/tdd.md` — vertical-slice red-green-refactor, **not** all tests first then all code.
- Use MSTest. Do not introduce other test frameworks.
- Pure `Glasswork.Core` / `Glasswork.Mcp` work — builds and tests on Linux. Do **not** touch `Glasswork.App`.
- Do not modify ADRs. If the design needs to change, post a comment and stop.

**Definition of done:**
- All Acceptance Criteria in the issue body are checked off.
- `dotnet build` succeeds for `src/Glasswork.Core/Glasswork.Core.csproj` (and `src/Glasswork.Mcp/Glasswork.Mcp.csproj` if it exists).
- `dotnet test` passes for `tests/Glasswork.Tests/` (and `tests/Glasswork.Mcp.Tests/` if it exists).
- **PR body MUST include `Closes #<issue>` so the watchdog can match the PR to the issue.**
- **When all work is committed, mark the PR ready for review (it should not stay as draft).** Use `gh pr ready` or the GitHub UI. The watchdog will not test or merge draft PRs.
- PR description summarizes the change in 2-3 sentences and lists the AC items satisfied.
'@

# ---- Helpers ---------------------------------------------------------------

function Write-Log {
    param([string]$Message, [string]$Level = 'INFO')
    $ts = Get-Date -Format 'yyyy-MM-ddTHH:mm:sszzz'
    $line = "$ts [$Level] $Message"
    Add-Content -Path $LogPath -Value $line
    Write-Host $line
}

function Invoke-Gh {
    param([string[]]$GhArgs)
    # gh writes a TTY spinner to stderr that corrupts JSON if merged into stdout, but on
    # error we DO want the stderr message. Redirect stderr to a temp file and read it back
    # only when the command failed.
    $errFile = [System.IO.Path]::GetTempFileName()
    try {
        $output = & gh @GhArgs 2>$errFile
        $code = $LASTEXITCODE
        if ($code -ne 0) {
            $stderr = (Get-Content $errFile -Raw -ErrorAction SilentlyContinue) ?? ''
            $combined = (@($output) + @($stderr) | Where-Object { $_ }) -join "`n"
            return [pscustomobject]@{ Output = $combined; ExitCode = $code }
        }
        return [pscustomobject]@{ Output = $output; ExitCode = $code }
    } finally {
        Remove-Item $errFile -ErrorAction SilentlyContinue
    }
}

function Get-IssueState {
    param([int]$Number)
    $r = Invoke-Gh -GhArgs @('issue', 'view', "$Number", '--repo', $Repo, '--json', 'state', '-q', '.state')
    if ($r.ExitCode -ne 0) { return $null }
    return ($r.Output | Out-String).Trim()
}

function Get-IssueAssignees {
    param([int]$Number)
    $r = Invoke-Gh -GhArgs @('issue', 'view', "$Number", '--repo', $Repo, '--json', 'assignees', '-q', '[.assignees[].login] | join(",")')
    if ($r.ExitCode -ne 0) { return '' }
    return ($r.Output | Out-String).Trim()
}

function Get-CopilotPrForIssue {
    param([int]$Number)

    # Primary: ask the issue which PRs are linked to close it. GitHub tracks
    # this even when the PR body forgets `Closes #N` (the dev panel link, the
    # auto-link from the assignment workflow, etc.).
    $r0 = Invoke-Gh -GhArgs @('issue', 'view', "$Number", '--repo', $Repo, '--json', 'closedByPullRequestsReferences', '-q', '[.closedByPullRequestsReferences[].number] | join(",")')
    $linkedNumbers = @()
    if ($r0.ExitCode -eq 0) {
        $raw = ($r0.Output | Out-String).Trim()
        if ($raw) { $linkedNumbers = $raw.Split(',') | ForEach-Object { [int]$_ } }
    }

    $r = Invoke-Gh -GhArgs @('pr', 'list', '--repo', $Repo, '--author', 'app/copilot-swe-agent', '--state', 'open', '--json', 'number,title,body,isDraft,headRefName')
    if ($r.ExitCode -ne 0) { return $null }
    $prs = ($r.Output | Out-String) | ConvertFrom-Json
    foreach ($pr in $prs) {
        if ($linkedNumbers -contains $pr.number) { return $pr }
        if ($pr.body -match "(?im)\b(close[sd]?|fix(?:es|ed)?|resolve[sd]?)\s+#$Number\b") { return $pr }
        if ($pr.title -match "(?<![\d])$Number\b" -and $pr.title -match '#') { return $pr }
        if ($pr.headRefName -match "(?<![\d])$Number(?![\d])") { return $pr }
    }
    return $null
}

function Get-PrChecksOk {
    param([int]$Number)
    $r = Invoke-Gh -GhArgs @('pr', 'checks', "$Number", '--repo', $Repo, '--json', 'name,state,conclusion')
    if ($r.ExitCode -ne 0) {
        # No checks present is OK
        return $true
    }
    $checks = ($r.Output | Out-String) | ConvertFrom-Json
    foreach ($c in $checks) {
        if ($c.state -eq 'IN_PROGRESS' -or $c.state -eq 'PENDING' -or $c.state -eq 'QUEUED') {
            Write-Log "  check '$($c.name)' still $($c.state) — skip merge for now"
            return $false
        }
        if ($c.conclusion -eq 'FAILURE' -or $c.conclusion -eq 'TIMED_OUT' -or $c.conclusion -eq 'CANCELLED') {
            Write-Log "  check '$($c.name)' = $($c.conclusion) — block merge" 'WARN'
            return $false
        }
    }
    return $true
}

function Test-PrLocally {
    param($Pr, [string[]]$TestProjects)

    Set-Location $RepoPath

    # Ensure clean state on main first
    & git checkout main 2>&1 | Out-Null
    & git branch -D "pr-$($Pr.number)" 2>&1 | Out-Null

    Write-Log "  fetching pr-$($Pr.number)"
    & git fetch origin "pull/$($Pr.number)/head:pr-$($Pr.number)" --force 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Log "  git fetch failed for PR #$($Pr.number)" 'ERROR'
        return $false
    }
    & git checkout "pr-$($Pr.number)" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Log "  git checkout failed for pr-$($Pr.number)" 'ERROR'
        return $false
    }

    $allPassed = $true
    foreach ($proj in $TestProjects) {
        $fullPath = Join-Path $RepoPath $proj
        if (-not (Test-Path $fullPath)) {
            Write-Log "  skipping missing test project: $proj"
            continue
        }
        Write-Log "  dotnet test $proj"
        $filter = ($FlakyTests | ForEach-Object { "FullyQualifiedName!~$_" }) -join '&'
        $output = & dotnet test $fullPath --nologo --filter $filter 2>&1
        $code = $LASTEXITCODE
        if ($code -ne 0) {
            Write-Log "  TESTS FAILED for $proj" 'ERROR'
            $output | Select-Object -Last 30 | ForEach-Object { Write-Log "    $_" 'ERROR' }
            $allPassed = $false
            break
        }
        Write-Log "  $proj — passed"
    }

    & git checkout main 2>&1 | Out-Null
    & git branch -D "pr-$($Pr.number)" 2>&1 | Out-Null

    return $allPassed
}

function Merge-Pr {
    param($Pr, [int]$IssueNumber)
    if ($Pr.isDraft) {
        Write-Log "  PR #$($Pr.number) is draft — marking ready"
        $r = Invoke-Gh -GhArgs @('pr', 'ready', "$($Pr.number)", '--repo', $Repo)
        if ($r.ExitCode -ne 0) {
            Write-Log "  failed to mark ready: $($r.Output)" 'ERROR'
            return $false
        }
    }
    Write-Log "  squash-merging PR #$($Pr.number) (closes #$IssueNumber)"
    $r = Invoke-Gh -GhArgs @('pr', 'merge', "$($Pr.number)", '--repo', $Repo, '--squash', '--delete-branch')
    if ($r.ExitCode -ne 0) {
        Write-Log "  merge failed: $($r.Output)" 'ERROR'
        return $false
    }
    Set-Location $RepoPath
    & git checkout main 2>&1 | Out-Null
    & git pull --ff-only origin main 2>&1 | Out-Null
    Write-Log "  MERGED PR #$($Pr.number); local main pulled"
    return $true
}

function Add-CopilotAssignee {
    param([int]$Number)
    Write-Log "assigning Copilot to #$Number"
    $r = Invoke-Gh -GhArgs @('issue', 'edit', "$Number", '--repo', $Repo, '--add-assignee', 'copilot-swe-agent')
    if ($r.ExitCode -ne 0) {
        Write-Log "  assign failed: $($r.Output)" 'ERROR'
        return
    }
    $r2 = Invoke-Gh -GhArgs @('issue', 'comment', "$Number", '--repo', $Repo, '--body', $SteeringBody)
    if ($r2.ExitCode -ne 0) {
        Write-Log "  steering comment failed: $($r2.Output)" 'WARN'
    }
}

# ---- Lock guard ------------------------------------------------------------

if (Test-Path $LockPath) {
    $existingPid = Get-Content $LockPath -ErrorAction SilentlyContinue
    if ($existingPid -and (Get-Process -Id $existingPid -ErrorAction SilentlyContinue)) {
        Write-Host "Watchdog already running (PID $existingPid). Exiting."
        exit 1
    }
    Write-Host "Stale lock file — clearing"
    Remove-Item $LockPath -Force
}
$PID | Out-File -FilePath $LockPath -NoNewline

try {
    Write-Log "watchdog START (PID=$PID, poll=${PollIntervalSec}s, milestones=$(($Milestones | ForEach-Object { '#' + $_.Issue }) -join ','))"

    $consecErrors = 0
    while ($true) {
        try {
            # Phase 1: process open Copilot PRs for tracked milestones
            foreach ($m in $Milestones) {
                $issueState = Get-IssueState -Number $m.Issue
                if ($issueState -eq 'CLOSED') { continue }

                $pr = Get-CopilotPrForIssue -Number $m.Issue
                if (-not $pr) { continue }

                Write-Log "PR #$($pr.number) found for issue #$($m.Issue) (draft=$($pr.isDraft))"

                if ($pr.isDraft) {
                    # Strongest signal: Copilot has requested review from the repo
                    # owner. That's the agent's explicit "I'm done" — promote
                    # to ready immediately, no waiting.
                    $reviewers = (Invoke-Gh -GhArgs @('pr', 'view', "$($pr.number)", '--repo', $Repo, '--json', 'reviewRequests', '-q', '[.reviewRequests[].login] | join(",")')).Output | Out-String
                    $reviewers = $reviewers.Trim()
                    if ($reviewers -match '(?i)\btjegbejimba\b') {
                        Write-Log "  draft has review requested from tjegbejimba — marking ready"
                        $r = Invoke-Gh -GhArgs @('pr', 'ready', "$($pr.number)", '--repo', $Repo)
                        if ($r.ExitCode -eq 0) {
                            $pr.isDraft = $false
                            Write-Log "  marked ready — will test on this iteration"
                        } else {
                            Write-Log "  failed to mark ready: $($r.Output)" 'WARN'
                            continue
                        }
                    }
                }

                if ($pr.isDraft) {
                    # Fallback: auto-mark ready if no commits in the last 30 min
                    # (agent finished but forgot both to mark ready and to request review).
                    $lastCommitIso = (Invoke-Gh -GhArgs @('pr', 'view', "$($pr.number)", '--repo', $Repo, '--json', 'commits', '-q', '[.commits[].committedDate, .commits[].authoredDate] | map(select(. != null)) | sort | last')).Output | Out-String
                    $lastCommitIso = $lastCommitIso.Trim()
                    if ($lastCommitIso) {
                        try {
                            $lastCommit = [datetimeoffset]::Parse($lastCommitIso)
                            $ageMin = ([datetimeoffset]::UtcNow - $lastCommit).TotalMinutes
                            Write-Log "  draft last commit $([math]::Round($ageMin)) min ago"
                            if ($ageMin -gt 30) {
                                Write-Log "  draft is stale (>30 min since last commit) — marking ready"
                                $r = Invoke-Gh -GhArgs @('pr', 'ready', "$($pr.number)", '--repo', $Repo)
                                if ($r.ExitCode -eq 0) {
                                    $pr.isDraft = $false
                                    Write-Log "  marked ready — will test on this iteration"
                                } else {
                                    Write-Log "  failed to mark ready: $($r.Output)" 'WARN'
                                    continue
                                }
                            } else {
                                Write-Log "  draft is fresh — agent likely still working, skipping"
                                continue
                            }
                        } catch {
                            Write-Log "  could not parse last commit date '$lastCommitIso' — skipping" 'WARN'
                            continue
                        }
                    } else {
                        Write-Log "  draft has no commits yet — skipping"
                        continue
                    }
                }

                if (-not (Get-PrChecksOk -Number $pr.number)) { continue }

                # If the PR conflicts with main, no point in running tests or attempting merge.
                # Cloud agent will see review comments and rebase; we'll pick it back up next iteration.
                $stateRes = Invoke-Gh -GhArgs @('pr', 'view', "$($pr.number)", '--repo', $Repo, '--json', 'mergeable,mergeStateStatus')
                if ($stateRes.ExitCode -eq 0) {
                    try {
                        $state = $stateRes.Output | ConvertFrom-Json
                        if ($state.mergeable -eq 'CONFLICTING' -or $state.mergeStateStatus -eq 'DIRTY') {
                            Write-Log "  PR #$($pr.number) has merge conflicts (mergeable=$($state.mergeable), state=$($state.mergeStateStatus)) — skipping until rebased"
                            continue
                        }
                    } catch {
                        Write-Log "  could not parse merge state — proceeding anyway" 'WARN'
                    }
                }

                $passed = Test-PrLocally -Pr $pr -TestProjects $m.TestProjects
                if ($passed) {
                    Merge-Pr -Pr $pr -IssueNumber $m.Issue | Out-Null
                } else {
                    Write-Log "leaving PR #$($pr.number) for human review (tests failed)" 'WARN'
                    $note = "Watchdog ran ``dotnet test`` locally and saw failures. Leaving for human review. See ``.mcp-watchdog.log`` on tjegbejimba's machine."
                    Invoke-Gh -GhArgs @('pr', 'comment', "$($pr.number)", '--repo', $Repo, '--body', $note) | Out-Null
                    # Skip subsequent iterations for this issue by treating it as needing humans
                    # (we re-check assignees, so removing Copilot would stop further auto-action;
                    #  for safety we just continue and let the human decide).
                }
            }

            # Phase 2: assign Copilot to any milestone whose deps are now closed
            foreach ($m in $Milestones) {
                $issueState = Get-IssueState -Number $m.Issue
                if ($issueState -eq 'CLOSED') { continue }
                if ($m.DependsOn.Count -eq 0) { continue }

                $assignees = Get-IssueAssignees -Number $m.Issue
                if ($assignees -match 'Copilot') { continue }

                $allClosed = $true
                foreach ($dep in $m.DependsOn) {
                    if ((Get-IssueState -Number $dep) -ne 'CLOSED') { $allClosed = $false; break }
                }
                if ($allClosed) {
                    Write-Log "deps satisfied for #$($m.Issue) (depends on $($m.DependsOn -join ','))"
                    Add-CopilotAssignee -Number $m.Issue
                }
            }

            # Phase 3: stop if all milestones are closed
            $allClosed = $true
            foreach ($m in $Milestones) {
                if ((Get-IssueState -Number $m.Issue) -ne 'CLOSED') { $allClosed = $false; break }
            }
            if ($allClosed) {
                Write-Log 'ALL MILESTONES CLOSED — watchdog exiting cleanly'
                break
            }

            $consecErrors = 0
        } catch {
            $consecErrors++
            Write-Log "iteration error ($consecErrors/$MaxConsecErrors): $_" 'ERROR'
            if ($consecErrors -ge $MaxConsecErrors) {
                Write-Log 'too many consecutive errors — exiting' 'ERROR'
                break
            }
        }

        Start-Sleep -Seconds $PollIntervalSec
    }
} finally {
    if (Test-Path $LockPath) { Remove-Item $LockPath -Force -ErrorAction SilentlyContinue }
    Write-Log 'watchdog STOP'
}
