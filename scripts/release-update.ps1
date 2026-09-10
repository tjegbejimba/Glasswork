param(
    [Parameter(Mandatory = $true)]
    [int]$AppProcessId,

    [Parameter(Mandatory = $true)]
    [string]$InstallExePath,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$CleanupDirectory,

    [switch]$NoProgress
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-ReleaseUpdate.ps1")
$statusPath = Join-Path $CleanupDirectory "update-status.txt"
$statusEncoding = [System.Text.UTF8Encoding]::new($false)
$progressProcess = $null
$progressReporter = {
    param($message)
    [System.IO.File]::WriteAllText($statusPath, [string]$message, $statusEncoding)
}
$invokeParameters = @{
    AppProcessId = $AppProcessId
    InstallExePath = $InstallExePath
    Version = $Version
    ProgressReporter = $progressReporter
    ShowProgress = $false
}
try {
    $showProgress = -not $NoProgress
    if ($showProgress -and [System.Environment]::OSVersion.Platform -eq 'Win32NT') {
        $progressScript = Join-Path $PSScriptRoot "Show-UpdateProgress.ps1"
        if (Test-Path $progressScript) {
            try {
                & $progressReporter "Preparing Glasswork $Version..."
                $hostPath = (Get-Process -Id $PID).Path
                $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
                $startInfo.FileName = $hostPath
                $startInfo.UseShellExecute = $false
                $startInfo.CreateNoWindow = $true
                $startInfo.Arguments = @(
                    "-NoProfile",
                    "-STA",
                    "-File",
                    "`"$progressScript`"",
                    "-StatusPath",
                    "`"$statusPath`"",
                    "-ParentProcessId",
                    $PID.ToString(),
                    "-Version",
                    $Version
                ) -join " "
                $progressProcess = [System.Diagnostics.Process]::Start($startInfo)
            }
            catch {
                Write-Warning "Could not start the update progress window: $_"
            }
        }
    }
    $invokeParameters.ShowProgress = $showProgress -and $null -eq $progressProcess
    Invoke-ReleaseUpdate @invokeParameters
}
finally {
    if ($progressProcess) {
        [System.IO.File]::WriteAllText($statusPath, "__complete__", $statusEncoding)
        [void]$progressProcess.WaitForExit(5000)
        $progressProcess.Dispose()
    }
    Remove-Item -Recurse -Force $CleanupDirectory -ErrorAction SilentlyContinue
}
