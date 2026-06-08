<#
.SYNOPSIS
    Dot-sourceable self-update function for Glasswork.
.DESCRIPTION
    Implements the core self-update logic: wait for app, pull repo, rebuild, relaunch.
    Fully testable via Pester by mocking the injected command runners.
.PARAMETER AppPid
    Process ID of the running Glasswork app to wait for.
.PARAMETER RepoPath
    Absolute path to the Glasswork source repository.
.PARAMETER InstallExePath
    Path to the installed Glasswork.exe to relaunch after update.
.PARAMETER LockPath
    Path to the update lock file (default: %LOCALAPPDATA%\Glasswork\update.lock).
.PARAMETER PublishScript
    Path to publish.ps1 (default: <RepoPath>\scripts\publish.ps1).
.PARAMETER GitInvoker
    Function to invoke git commands (default: real git via & git).
.PARAMETER PublishInvoker
    Function to invoke publish.ps1 (default: real invoke via & $PublishScript).
.PARAMETER ProcessWaiter
    Function to wait for a process to exit (default: Wait-Process with timeout).
.PARAMETER Relauncher
    Function to relaunch the exe (default: Start-Process -FilePath $exe).
.PARAMETER ReleasePageOpener
    Function to open the GitHub release page (default: Start-Process https://...).
.PARAMETER ShowProgress
    Whether to show a progress window during the update (default: $true).
#>

function Invoke-SelfUpdate {
    param(
        [Parameter(Mandatory = $true)]
        [int]$AppProcessId,

        [Parameter(Mandatory = $true)]
        [string]$RepoPath,

        [Parameter(Mandatory = $true)]
        [string]$InstallExePath,

        [string]$LockPath = (Join-Path $env:LOCALAPPDATA "Glasswork\update.lock"),

        [string]$PublishScript = (Join-Path $RepoPath "scripts\publish.ps1"),

        [scriptblock]$GitInvoker = { param($argsList) & git @argsList },

        [scriptblock]$PublishInvoker = { param($script) & $script },

        [scriptblock]$ProcessWaiter = { 
            param($processId, $timeoutSec)
            try {
                $p = Get-Process -Id $processId -ErrorAction SilentlyContinue
                if ($p) {
                    $p | Wait-Process -Timeout $timeoutSec -ErrorAction SilentlyContinue
                    return $true
                }
                return $true  # Already gone = treated as exited
            }
            catch {
                return $false  # Timeout
            }
        },

        [scriptblock]$Relauncher = {
            param($exe)
            if (Test-Path $exe) {
                Start-Process -FilePath $exe
            }
        },

        [scriptblock]$ReleasePageOpener = {
            Start-Process "https://github.com/tjegbejimba/Glasswork/releases"
        },

        [bool]$ShowProgress = $true
    )

    $ErrorActionPreference = "Stop"

    # Step 1: Acquire update lock
    try {
        $lockDir = Split-Path $LockPath -Parent
        if ($lockDir -and !(Test-Path $lockDir)) {
            New-Item -ItemType Directory -Force -Path $lockDir | Out-Null
        }

        # Try to create lock file exclusively
        try {
            $lockFile = [System.IO.File]::Open($LockPath, 'CreateNew', 'Write', 'None')
        }
        catch [System.IO.IOException] {
            # Lock held by another updater
            Write-Verbose "Update lock held by another process. Exiting."
            return
        }
    }
    catch {
        Write-Error "Failed to acquire update lock: $_"
        return
    }

    try {
        # Step 2: Capture existing exe before mutation
        if (!(Test-Path $InstallExePath)) {
            Write-Verbose "Install exe not found. Opening release page."
            & $ReleasePageOpener
            return
        }
        $existingExe = $InstallExePath

        # Step 3: Resolve git
        $gitPath = Get-Command git -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
        if (!$gitPath) {
            Write-Verbose "git not found. Opening release page and relaunching existing exe."
            & $ReleasePageOpener
            & $Relauncher $existingExe
            return
        }

        # Step 4: Wait for app PID to exit
        $waitedSuccessfully = & $ProcessWaiter $AppProcessId 60
        if (!$waitedSuccessfully) {
            Write-Verbose "App process did not exit in time. Relaunching existing exe."
            & $Relauncher $existingExe
            return
        }

        # Step 5: Git preflight checks
        # Check if it's a git worktree
        $isGitRepo = & $GitInvoker @("-C", $RepoPath, "rev-parse", "--is-inside-work-tree") 2>$null
        if ($LASTEXITCODE -ne 0 -or $isGitRepo -ne "true") {
            Write-Verbose "Not a git repository. Relaunching existing exe."
            & $ReleasePageOpener
            & $Relauncher $existingExe
            return
        }

        # Check remote points to expected repo
        $remoteUrl = & $GitInvoker @("-C", $RepoPath, "remote", "get-url", "origin") 2>$null
        if ($LASTEXITCODE -ne 0 -or $remoteUrl -notmatch "tjegbejimba/Glasswork") {
            Write-Verbose "Wrong or missing origin remote. Relaunching existing exe."
            & $ReleasePageOpener
            & $Relauncher $existingExe
            return
        }

        # Check worktree is clean
        $status = & $GitInvoker @("-C", $RepoPath, "status", "--porcelain") 2>$null
        if ($LASTEXITCODE -ne 0 -or ![string]::IsNullOrWhiteSpace($status)) {
            Write-Verbose "Dirty worktree. Relaunching existing exe."
            & $ReleasePageOpener
            & $Relauncher $existingExe
            return
        }

        # Show progress window if requested (GUI code only executed when ShowProgress is true)
        $progressForm = $null
        if ($ShowProgress -and [System.Environment]::OSVersion.Platform -eq 'Win32NT') {
            try {
                Add-Type -AssemblyName System.Windows.Forms -ErrorAction SilentlyContinue
                $progressForm = New-Object System.Windows.Forms.Form
                $progressForm.Text = "Updating Glasswork..."
                $progressForm.Width = 300
                $progressForm.Height = 100
                $progressForm.StartPosition = 'CenterScreen'
                $progressForm.FormBorderStyle = 'FixedDialog'
                $progressForm.MaximizeBox = $false
                $progressForm.MinimizeBox = $false
                
                $label = New-Object System.Windows.Forms.Label
                $label.Text = "Updating Glasswork..."
                $label.AutoSize = $true
                $label.Left = 10
                $label.Top = 30
                $progressForm.Controls.Add($label)
                
                $progressForm.Show()
                [System.Windows.Forms.Application]::DoEvents()
            }
            catch {
                # Progress window optional - continue without it
            }
        }

        try {
            # Step 6: Git pull (fetch + ff-only merge)
            & $GitInvoker @("-C", $RepoPath, "fetch") 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) {
                Write-Verbose "git fetch failed. Relaunching existing exe."
                & $Relauncher $existingExe
                return
            }

            & $GitInvoker @("-C", $RepoPath, "merge", "--ff-only") 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) {
                Write-Verbose "git merge --ff-only failed. Relaunching existing exe."
                & $Relauncher $existingExe
                return
            }

            # Step 7: Run publish.ps1
            & $PublishInvoker $PublishScript 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) {
                Write-Verbose "publish.ps1 failed. Relaunching existing exe if it exists."
                if (Test-Path $existingExe) {
                    & $Relauncher $existingExe
                }
                else {
                    & $ReleasePageOpener
                }
                return
            }

            # Step 8: Relaunch the newly built exe
            & $Relauncher $InstallExePath
        }
        finally {
            if ($progressForm) {
                try { $progressForm.Close() } catch { }
                try { $progressForm.Dispose() } catch { }
            }
        }
    }
    finally {
        # Release lock
        if ($lockFile) {
            try {
                $lockFile.Close()
                $lockFile.Dispose()
                Remove-Item $LockPath -Force -ErrorAction SilentlyContinue
            }
            catch { }
        }
    }
}
