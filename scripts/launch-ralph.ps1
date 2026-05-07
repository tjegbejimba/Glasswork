<#
.SYNOPSIS
    Launch / monitor / stop the Ralph TDD loop on Windows from PowerShell.

.DESCRIPTION
    Wraps Ralph's Bash launcher (.ralph\launch.sh) so an agent — including
    Copilot CLI agents running in a PowerShell session — can drive the loop
    without hitting the Git-Bash-on-Windows background-mode fork crash.

    The loop's own background mode (`launch.sh` without `--foreground`) uses
    `nohup ... &`, which crashes Cygwin's fork emulation when Git Bash is
    spawned from a non-Bash parent (PowerShell, conhost, Start-Process):

        bash 1026 dofork: child 1027 - died waiting for dll loading, errno 11

    The workaround: spawn `bash --foreground` via Start-Process with a hidden
    window. Bash itself runs the loop attached to its own (hidden) console;
    no fork is needed; the Windows process is detached and survives this
    session ending.

    See ADR considerations and PR history for context. This script supersedes
    the documented `.ralph/launch.sh` invocation on Windows.

.PARAMETER Action
    One of:
      Launch  - Pre-flight, then start the loop detached. Writes
                .ralph\launcher.pid for later -Status / -Stop calls.
      Status  - Show launcher PID + alive state + recent iteration logs.
                This is the default (safe / read-only).
      Stop    - SIGTERM the launcher process and any spawned copilot worker.

.PARAMETER Parallelism
    Workers to start (Launch only). Defaults to 1. Foreground mode only
    runs ONE worker; if you pass >1 the script falls back to 1 with a
    warning. (Native parallelism requires either WSL or fixing the fork
    crash upstream.)

.EXAMPLE
    pwsh -File scripts\launch-ralph.ps1
    # Default: shows status. Safe to run anytime.

.EXAMPLE
    pwsh -File scripts\launch-ralph.ps1 -Action Launch
    # Pre-flights and starts the loop in the background. Returns immediately.

.EXAMPLE
    pwsh -File scripts\launch-ralph.ps1 -Action Stop
    # Stops the launcher + any active worker.
#>
param(
    [ValidateSet("Launch", "Status", "Stop")]
    [string]$Action = "Status",

    [int]$Parallelism = 1
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path $PSScriptRoot -Parent
$RalphDir = Join-Path $RepoRoot ".ralph"
$LogDir = Join-Path $RalphDir "logs"
$PidFile = Join-Path $RalphDir "launcher.pid"
$BashExe = "C:\Program Files\Git\usr\bin\bash.exe"

function Get-LauncherProcess {
    if (-not (Test-Path $PidFile)) { return $null }
    $launcherPid = [int]((Get-Content $PidFile -Raw).Trim())
    return Get-Process -Id $launcherPid -ErrorAction SilentlyContinue
}

function Show-Status {
    Write-Host "=== Ralph launcher status ===" -ForegroundColor Cyan
    $proc = Get-LauncherProcess
    if ($proc) {
        $age = [int]((Get-Date) - $proc.StartTime).TotalMinutes
        Write-Host ("  Launcher PID {0} alive, {1} min old" -f $proc.Id, $age) -ForegroundColor Green
    } elseif (Test-Path $PidFile) {
        $stalePid = (Get-Content $PidFile -Raw).Trim()
        Write-Host ("  Launcher PID {0}: dead (stale pidfile, will be cleared on next Launch)" -f $stalePid) -ForegroundColor Yellow
    } else {
        Write-Host "  No launcher running" -ForegroundColor Gray
    }

    Write-Host ""
    Write-Host "=== Active issue claims (.ralph\state.json) ===" -ForegroundColor Cyan
    $stateFile = Join-Path $RalphDir "state.json"
    if (Test-Path $stateFile) {
        Get-Content $stateFile -Raw
    } else {
        Write-Host "  (no state.json)"
    }

    Write-Host ""
    Write-Host "=== 5 most recent iteration logs ===" -ForegroundColor Cyan
    Get-ChildItem $LogDir -Filter "iter-*.log" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 5 |
        Select-Object @{n='Modified'; e={$_.LastWriteTime.ToString('HH:mm:ss')}}, @{n='Bytes'; e={$_.Length}}, Name |
        Format-Table -AutoSize
}

function Stop-Loop {
    Write-Host "=== Stopping Ralph loop ===" -ForegroundColor Cyan
    $proc = Get-LauncherProcess
    if ($proc) {
        Write-Host ("  Stopping launcher PID {0}" -f $proc.Id)
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }

    # Kill any copilot workers spawned in the last 4 hours that are still alive.
    # Conservative window so we don't murder the user's interactive Copilot CLI.
    $cutoff = (Get-Date).AddHours(-4)
    $copilots = Get-Process -Name copilot -ErrorAction SilentlyContinue |
        Where-Object { $_.StartTime -gt $cutoff -and $_.CPU -gt 60 }
    foreach ($c in $copilots) {
        Write-Host ("  Stopping copilot worker PID {0} (CPU {1:N0}s, age {2} min)" -f `
            $c.Id, $c.CPU, [int]((Get-Date) - $c.StartTime).TotalMinutes)
        Stop-Process -Id $c.Id -Force -ErrorAction SilentlyContinue
    }

    if (Test-Path $PidFile) { Remove-Item $PidFile -Force }
    Write-Host "  Done." -ForegroundColor Green
}

function Test-PreFlight {
    Push-Location $RepoRoot
    try {
        if (-not (Test-Path $BashExe)) {
            throw "Git Bash not found at $BashExe. Install Git for Windows."
        }
        if (-not (Test-Path (Join-Path $RalphDir "launch.sh"))) {
            throw ".ralph\launch.sh not found. Re-run ralph-loop-dashboard\install.sh."
        }
        $branch = (git branch --show-current).Trim()
        if ($branch -ne "main") {
            throw "Must be on main branch (currently on '$branch'). Switch branches before launching."
        }
        $dirty = git status --porcelain
        if ($dirty) {
            throw "Working tree is not clean. Commit or stash changes first.`n$dirty"
        }
        $existing = Get-LauncherProcess
        if ($existing) {
            throw ("Launcher PID {0} is already running. Run -Action Stop first." -f $existing.Id)
        }
    } finally {
        Pop-Location
    }
}

function Start-Loop {
    Test-PreFlight

    if ($Parallelism -gt 1) {
        Write-Host "WARNING: --foreground only runs 1 worker. Falling back to Parallelism=1." -ForegroundColor Yellow
        Write-Host "         To run multiple workers on Windows, install WSL2 and launch from there." -ForegroundColor Yellow
        $Parallelism = 1
    }

    if (-not (Test-Path $LogDir)) {
        New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
    }

    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $stdoutLog = Join-Path $LogDir "launcher-$timestamp.out"
    $stderrLog = Join-Path $LogDir "launcher-$timestamp.err"

    # Convert Windows repo path to a /c/... POSIX path Git Bash understands.
    # Example: "C:\Users\toegbeji\Repos\Glasswork" -> "/c/Users/toegbeji/Repos/Glasswork"
    $drive = $RepoRoot.Substring(0, 1).ToLower()
    $rest = $RepoRoot.Substring(2) -replace '\\', '/'
    $posixRepo = "/$drive$rest"

    # Output is captured via shell redirection inside bash (relative paths, since
    # we cd into the repo first). This avoids the .NET RedirectStandardOutput/Error
    # properties, which (on this machine, in this combo) caused the spawned bash to
    # exit immediately with no error output. The 'exec' replaces the bash login
    # shell with launch.sh so killing the launcher kills the loop directly.
    $bashCmd = "cd '$posixRepo' && exec ./.ralph/launch.sh --foreground > .ralph/logs/launcher-$timestamp.out 2> .ralph/logs/launcher-$timestamp.err"

    Write-Host "=== Launching Ralph (foreground, detached) ===" -ForegroundColor Cyan
    Write-Host "  Repo:    $RepoRoot"
    Write-Host "  POSIX:   $posixRepo"
    Write-Host "  stdout:  $stdoutLog"
    Write-Host "  stderr:  $stderrLog"

    # Use System.Diagnostics.Process directly. Start-Process with -WindowStyle Hidden
    # + -RedirectStandardOutput/-Error reliably FAILED on this machine: the spawned
    # bash exited immediately, redirect files stayed 0 bytes, no error surfaced.
    # The .NET ProcessStartInfo path with UseShellExecute=false + CreateNoWindow=true
    # launches detached, survives the spawning PowerShell session ending, and is the
    # canonical Windows pattern for "spawn console process with no window".
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $BashExe
    $psi.Arguments = "-lc `"$bashCmd`""
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.WorkingDirectory = $RepoRoot

    $proc = [System.Diagnostics.Process]::Start($psi)
    $proc.Id | Set-Content $PidFile

    Write-Host ""
    Write-Host ("  Launcher PID: {0}" -f $proc.Id) -ForegroundColor Green
    Write-Host ""
    Write-Host "Monitor with:" -ForegroundColor Cyan
    Write-Host "  pwsh -File $PSCommandPath"
    Write-Host ""
    Write-Host "Stop with:" -ForegroundColor Cyan
    Write-Host "  pwsh -File $PSCommandPath -Action Stop"
}

switch ($Action) {
    "Launch" { Start-Loop }
    "Status" { Show-Status }
    "Stop"   { Stop-Loop }
}
