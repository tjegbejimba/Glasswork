<#
.SYNOPSIS
    Tests for the Windows Ralph launcher preflight boundary.
#>

BeforeAll {
    $script:RepoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $script:LauncherSource = Join-Path $script:RepoRoot "scripts\launch-ralph.ps1"

    function Invoke-TestGit {
        param(
            [Parameter(Mandatory = $true)]
            [string]$WorkingDirectory,

            [Parameter(ValueFromRemainingArguments = $true)]
            [string[]]$Arguments
        )

        $output = & git -C $WorkingDirectory @Arguments 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "git $($Arguments -join ' ') failed:`n$($output -join "`n")"
        }

        return $output
    }

    function New-RalphLauncherFixture {
        param(
            [string]$Name
        )

        $fixtureRoot = Join-Path $TestDrive $Name
        $remotePath = Join-Path $fixtureRoot "origin.git"
        $seedPath = Join-Path $fixtureRoot "seed"
        $coordinatorPath = Join-Path $fixtureRoot "coordinator"

        New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
        & git init --bare --quiet $remotePath
        if ($LASTEXITCODE -ne 0) { throw "Failed to initialize test remote." }

        & git init --quiet --initial-branch=main $seedPath
        if ($LASTEXITCODE -ne 0) { throw "Failed to initialize test seed repository." }

        Invoke-TestGit $seedPath config user.email "ralph-launcher-tests@example.invalid"
        Invoke-TestGit $seedPath config user.name "Ralph Launcher Tests"
        New-Item -ItemType Directory -Path (Join-Path $seedPath "scripts") | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $seedPath ".ralph") | Out-Null
        Copy-Item $script:LauncherSource (Join-Path $seedPath "scripts\launch-ralph.ps1")
        $launcherFixture = @'
#!/usr/bin/env bash
if [[ -n "${RALPH_TEST_CAPTURE:-}" ]]; then
  printf '%s\n' \
    "baseRemote=${RALPH_BASE_REMOTE:-}" \
    "baseBranch=${RALPH_BASE_BRANCH:-}" \
    "baseCommit=${RALPH_BASE_COMMIT:-}" \
    "mainRepo=${RALPH_MAIN_REPO:-}" \
    "loopRepo=${RALPH_LOOP_REPO-unset}" \
    "copilotBin=${RALPH_COPILOT_BIN-unset}" \
    > "$RALPH_TEST_CAPTURE"
fi
'@
        [System.IO.File]::WriteAllText(
            (Join-Path $seedPath ".ralph\launch.sh"),
            (($launcherFixture -replace "`r`n", "`n") + "`n"),
            [System.Text.UTF8Encoding]::new($false)
        )
        Set-Content (Join-Path $seedPath ".ralph\config.json") '{"allowAgentLaunch":true}'
        Set-Content (Join-Path $seedPath ".gitignore") @"
.ralph/launcher.pid
.ralph/state.json
"@
        Invoke-TestGit $seedPath add scripts/launch-ralph.ps1 .ralph/launch.sh .ralph/config.json .gitignore
        Invoke-TestGit $seedPath update-index --chmod=+x .ralph/launch.sh
        Invoke-TestGit $seedPath commit --quiet -m "Initialize launcher fixture"
        Invoke-TestGit $seedPath remote add origin $remotePath
        Invoke-TestGit $seedPath push --quiet --set-upstream origin main

        & git clone --quiet --branch main $remotePath $coordinatorPath
        if ($LASTEXITCODE -ne 0) { throw "Failed to clone test coordinator." }

        return [pscustomobject]@{
            RemotePath = $remotePath
            SeedPath = $seedPath
            CoordinatorPath = $coordinatorPath
            LauncherPath = Join-Path $coordinatorPath "scripts\launch-ralph.ps1"
        }
    }
}

Describe "Test-PreFlight" {
    It "accepts clean main at the fetched remote base revision" {
        $fixture = New-RalphLauncherFixture "main"
        . $fixture.LauncherPath

        $result = Test-PreFlight

        $result.BaseRef | Should -Be "refs/remotes/origin/main"
    }

    It "accepts a clean detached worktree at the fetched remote base revision" {
        $fixture = New-RalphLauncherFixture "detached"
        Invoke-TestGit $fixture.CoordinatorPath switch --detach
        . $fixture.LauncherPath

        { Test-PreFlight } | Should -Not -Throw
    }

    It "accepts a clean temporary branch at the fetched remote base revision" {
        $fixture = New-RalphLauncherFixture "temporary-branch"
        Invoke-TestGit $fixture.CoordinatorPath switch --create coordinator
        . $fixture.LauncherPath

        $result = Test-PreFlight

        $result.BaseRef | Should -Be "refs/remotes/origin/main"
        $result.BaseCommit | Should -Be (
            Invoke-TestGit $fixture.CoordinatorPath rev-parse "refs/remotes/origin/main"
        )
    }

    It "rejects a stale coordinator behind the fetched remote base revision" {
        $fixture = New-RalphLauncherFixture "stale"
        Set-Content (Join-Path $fixture.SeedPath "remote-change.txt") "new remote revision"
        Invoke-TestGit $fixture.SeedPath add remote-change.txt
        Invoke-TestGit $fixture.SeedPath commit --quiet -m "Advance remote base"
        Invoke-TestGit $fixture.SeedPath push --quiet origin main
        . $fixture.LauncherPath

        { Test-PreFlight } |
            Should -Throw "*does not equal fetched base refs/remotes/origin/main*"
    }

    It "rejects a coordinator with a local commit ahead of the fetched remote base" {
        $fixture = New-RalphLauncherFixture "ahead"
        Invoke-TestGit $fixture.CoordinatorPath config user.email "ralph-launcher-tests@example.invalid"
        Invoke-TestGit $fixture.CoordinatorPath config user.name "Ralph Launcher Tests"
        Set-Content (Join-Path $fixture.CoordinatorPath "local-change.txt") "local revision"
        Invoke-TestGit $fixture.CoordinatorPath add local-change.txt
        Invoke-TestGit $fixture.CoordinatorPath commit --quiet -m "Add local-only commit"
        . $fixture.LauncherPath

        { Test-PreFlight } |
            Should -Throw "*does not equal fetched base refs/remotes/origin/main*"
    }

    It "rejects a dirty coordinator worktree" {
        $fixture = New-RalphLauncherFixture "dirty"
        Set-Content (Join-Path $fixture.CoordinatorPath "dirty.txt") "uncommitted"
        . $fixture.LauncherPath

        { Test-PreFlight } | Should -Throw "*Working tree is not clean*dirty.txt*"
    }

    It "rejects an active launcher process" {
        $fixture = New-RalphLauncherFixture "active-launcher"
        Set-Content (Join-Path $fixture.CoordinatorPath ".ralph\launcher.pid") $PID
        . $fixture.LauncherPath

        { Test-PreFlight } | Should -Throw "*Launcher PID $PID is already running*"
    }

    It "rejects an active state claim whose worker uses an MSYS PID" {
        $fixture = New-RalphLauncherFixture "active-msys-claim"
        $claimPidFile = Join-Path $TestDrive "active-msys-claim.pid"
        $claimPidPosix = "/" +
            $claimPidFile.Substring(0, 1).ToLowerInvariant() +
            $claimPidFile.Substring(2).Replace("\", "/")
        $processInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $processInfo.FileName = "C:\Program Files\Git\usr\bin\bash.exe"
        $processInfo.ArgumentList.Add("-lc")
        $processInfo.ArgumentList.Add(
            "echo `$`$ > '$claimPidPosix'; exec -a ralph.sh sleep 60"
        )
        $processInfo.UseShellExecute = $false
        $processInfo.CreateNoWindow = $true
        $worker = [System.Diagnostics.Process]::Start($processInfo)

        try {
            for ($attempt = 0; $attempt -lt 50 -and -not (Test-Path $claimPidFile); $attempt++) {
                Start-Sleep -Milliseconds 100
            }
            if (-not (Test-Path $claimPidFile)) {
                throw "MSYS test worker did not report its PID."
            }

            $claimPid = [int]((Get-Content $claimPidFile -Raw).Trim())
            $worker.Id | Should -Not -Be $claimPid
            Set-Content (Join-Path $fixture.CoordinatorPath ".ralph\state.json") (
                '{"claims":{"506":{"pid":' + $claimPid + '}}}'
            )
            . $fixture.LauncherPath

            { Test-PreFlight } | Should -Throw "*Active Ralph state claim exists for issue 506*"
        }
        finally {
            if (-not $worker.HasExited) {
                Stop-Process -Id $worker.Id -Force
            }
        }
    }

    It "compares against an explicitly configured non-default release base" {
        $fixture = New-RalphLauncherFixture "release-base"
        Invoke-TestGit $fixture.SeedPath switch --create release/v2
        Set-Content (Join-Path $fixture.SeedPath "release.txt") "release revision"
        Invoke-TestGit $fixture.SeedPath add release.txt
        Invoke-TestGit $fixture.SeedPath commit --quiet -m "Create release base"
        Invoke-TestGit $fixture.SeedPath push --quiet --set-upstream origin release/v2
        Invoke-TestGit $fixture.CoordinatorPath fetch --quiet origin release/v2
        Invoke-TestGit $fixture.CoordinatorPath switch --detach origin/release/v2
        . $fixture.LauncherPath

        $previousReleaseBranch = $env:RALPH_RELEASE_BRANCH
        try {
            $env:RALPH_RELEASE_BRANCH = "main"
            $result = Test-PreFlight -BaseBranch "release/v2"
        }
        finally {
            $env:RALPH_RELEASE_BRANCH = $previousReleaseBranch
        }

        $result.BaseRef | Should -Be "refs/remotes/origin/release/v2"
    }

    It "uses RALPH_RELEASE_BRANCH when no base branch is explicitly configured" {
        $fixture = New-RalphLauncherFixture "release-environment"
        Invoke-TestGit $fixture.SeedPath switch --create next
        Set-Content (Join-Path $fixture.SeedPath "next.txt") "next revision"
        Invoke-TestGit $fixture.SeedPath add next.txt
        Invoke-TestGit $fixture.SeedPath commit --quiet -m "Create next base"
        Invoke-TestGit $fixture.SeedPath push --quiet --set-upstream origin next
        Invoke-TestGit $fixture.CoordinatorPath fetch --quiet origin next
        Invoke-TestGit $fixture.CoordinatorPath switch --detach origin/next
        . $fixture.LauncherPath

        $previousReleaseBranch = $env:RALPH_RELEASE_BRANCH
        try {
            $env:RALPH_RELEASE_BRANCH = "next"
            $result = Test-PreFlight
        }
        finally {
            $env:RALPH_RELEASE_BRANCH = $previousReleaseBranch
        }

        $result.BaseRef | Should -Be "refs/remotes/origin/next"
    }

    It "fetches and compares an explicitly configured base remote" {
        $fixture = New-RalphLauncherFixture "base-remote"
        Invoke-TestGit $fixture.CoordinatorPath remote rename origin upstream
        . $fixture.LauncherPath

        $result = Test-PreFlight -BaseRemote "upstream"

        $result.BaseRef | Should -Be "refs/remotes/upstream/main"
    }

    It "rejects a coordinator checked out at a different base revision" {
        $fixture = New-RalphLauncherFixture "different-base"
        Invoke-TestGit $fixture.SeedPath switch --create feature-base
        Set-Content (Join-Path $fixture.SeedPath "feature.txt") "different base"
        Invoke-TestGit $fixture.SeedPath add feature.txt
        Invoke-TestGit $fixture.SeedPath commit --quiet -m "Create different base"
        Invoke-TestGit $fixture.SeedPath push --quiet --set-upstream origin feature-base
        Invoke-TestGit $fixture.CoordinatorPath fetch --quiet origin feature-base
        Invoke-TestGit $fixture.CoordinatorPath switch --detach origin/feature-base
        . $fixture.LauncherPath

        { Test-PreFlight } |
            Should -Throw "*does not equal fetched base refs/remotes/origin/main*"
    }

    It "fetches the configured base before evaluating readiness gates" {
        $fixture = New-RalphLauncherFixture "fetch-first"
        Set-Content (Join-Path $fixture.SeedPath "remote-change.txt") "new remote revision"
        Invoke-TestGit $fixture.SeedPath add remote-change.txt
        Invoke-TestGit $fixture.SeedPath commit --quiet -m "Advance remote before preflight"
        Invoke-TestGit $fixture.SeedPath push --quiet origin main
        $expectedCommit = Invoke-TestGit $fixture.SeedPath rev-parse HEAD
        Remove-Item (Join-Path $fixture.CoordinatorPath ".ralph\launch.sh")
        . $fixture.LauncherPath

        { Test-PreFlight } | Should -Throw "*.ralph\launch.sh not found*"
        Invoke-TestGit $fixture.CoordinatorPath rev-parse refs/remotes/origin/main |
            Should -Be $expectedCommit
    }

    It "rejects a configured remote whose name can be parsed as a git option" {
        $fixture = New-RalphLauncherFixture "unsafe-remote"
        Invoke-TestGit $fixture.CoordinatorPath config "remote.--upload-pack=payload.url" $fixture.RemotePath
        . $fixture.LauncherPath

        { Test-PreFlight -BaseRemote "--upload-pack=payload" } |
            Should -Throw "*Configured base remote must not begin with '-'*"
    }
}

Describe "Start-Loop" {
    It "pins the verified base and sanitizes path and executable overrides" {
        $fixture = New-RalphLauncherFixture "launch-environment"
        $captureFile = Join-Path $TestDrive "launch-environment.txt"
        $captureFilePosix = "/" +
            $captureFile.Substring(0, 1).ToLowerInvariant() +
            $captureFile.Substring(2).Replace("\", "/")
        . $fixture.LauncherPath

        $previousCapture = $env:RALPH_TEST_CAPTURE
        $previousLoopRepo = $env:RALPH_LOOP_REPO
        $previousCopilotBin = $env:RALPH_COPILOT_BIN
        try {
            $env:RALPH_TEST_CAPTURE = $captureFilePosix
            $env:RALPH_LOOP_REPO = "/tmp/redirected-loop"
            $env:RALPH_COPILOT_BIN = "/tmp/payload"
            Start-Loop

            for ($attempt = 0; $attempt -lt 50 -and -not (Test-Path $captureFile); $attempt++) {
                Start-Sleep -Milliseconds 100
            }
            if (-not (Test-Path $captureFile)) {
                throw "Test launcher did not capture its child environment."
            }

            $captured = Get-Content $captureFile
            $expectedCommit = Invoke-TestGit $fixture.CoordinatorPath rev-parse HEAD
            $captured | Should -Contain "baseRemote=origin"
            $captured | Should -Contain "baseBranch=main"
            $captured | Should -Contain "baseCommit=$expectedCommit"
            $captured | Should -Contain "loopRepo=unset"
            $captured | Should -Contain "copilotBin=unset"
        }
        finally {
            $env:RALPH_TEST_CAPTURE = $previousCapture
            $env:RALPH_LOOP_REPO = $previousLoopRepo
            $env:RALPH_COPILOT_BIN = $previousCopilotBin
            $launcherPidFile = Join-Path $fixture.CoordinatorPath ".ralph\launcher.pid"
            if (Test-Path $launcherPidFile) {
                $launcherPid = [int]((Get-Content $launcherPidFile -Raw).Trim())
                Stop-Process -Id $launcherPid -Force -ErrorAction SilentlyContinue
            }
        }
    }
}
