. (Join-Path $PSScriptRoot "Install-CanvasExtension.ps1")

function Invoke-ReleaseUpdate {
    param(
        [Parameter(Mandatory = $true)]
        [int]$AppProcessId,

        [Parameter(Mandatory = $true)]
        [string]$InstallExePath,

        [Parameter(Mandatory = $true)]
        [string]$Version,

        [string]$MutexName = "Local\Glasswork.SelfUpdate",

        [string]$WorkDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) "Glasswork\update-$([guid]::NewGuid())"),

        [scriptblock]$Downloader = {
            param($uri, $destination)
            Invoke-WebRequest -Uri $uri -OutFile $destination -UseBasicParsing
        },

        [scriptblock]$ProcessWaiter = {
            param($processId, $timeoutSec)
            try {
                $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
                if ($process) {
                    $process | Wait-Process -Timeout $timeoutSec -ErrorAction Stop
                }
                return $true
            }
            catch {
                return $false
            }
        },

        [scriptblock]$Relauncher = {
            param($exe)
            if (Test-Path $exe) {
                Start-Process -FilePath $exe
            }
        },

        [scriptblock]$ReleasePageOpener = {
            param($uri)
            Start-Process $uri
        },

        [bool]$ShowProgress = $true
    )

    $ErrorActionPreference = "Stop"
    [Net.ServicePointManager]::SecurityProtocol =
        [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    if ($Version -notmatch '^\d+\.\d+\.\d+$') {
        throw "Version must be in X.Y.Z form."
    }

    $installDirectory = Split-Path $InstallExePath -Parent
    $backupDirectory = "$installDirectory.update-backup"
    $archiveUri = "https://github.com/tjegbejimba/Glasswork/releases/download/v$Version/Glasswork-win-x64.zip"
    $checksumUri = "$archiveUri.sha256"
    $releasePageUri = "https://github.com/tjegbejimba/Glasswork/releases/tag/v$Version"
    $mutex = $null
    $mutexAcquired = $false
    $progressForm = $null
    $installMoved = $false
    $appExited = $false
    $stagingDirectory = "$installDirectory.update-staging"

    try {
        try {
            $mutex = New-Object System.Threading.Mutex($false, $MutexName)
            $mutexAcquired = $mutex.WaitOne(0)
        }
        catch [System.Threading.AbandonedMutexException] {
            $mutexAcquired = $true
        }

        if (!$mutexAcquired) {
            Write-Verbose "Another Glasswork update is already running."
            return
        }

        if (!(Test-Path $InstallExePath)) {
            throw "Installed executable was not found: $InstallExePath"
        }

        try {
            & $ReleasePageOpener $releasePageUri
        }
        catch {
            Write-Verbose "Could not open release notes: $_"
        }

        if (!(& $ProcessWaiter $AppProcessId 60)) {
            throw "Glasswork did not exit within 60 seconds."
        }
        $appExited = $true

        if ($ShowProgress -and [System.Environment]::OSVersion.Platform -eq 'Win32NT') {
            try {
                Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
                $progressForm = New-Object System.Windows.Forms.Form
                $progressForm.Text = "Updating Glasswork..."
                $progressForm.Width = 320
                $progressForm.Height = 110
                $progressForm.StartPosition = 'CenterScreen'
                $progressForm.FormBorderStyle = 'FixedDialog'
                $progressForm.MaximizeBox = $false
                $progressForm.MinimizeBox = $false

                $label = New-Object System.Windows.Forms.Label
                $label.Text = "Downloading and installing Glasswork $Version..."
                $label.AutoSize = $true
                $label.Left = 12
                $label.Top = 32
                $progressForm.Controls.Add($label)
                $progressForm.Show()
                [System.Windows.Forms.Application]::DoEvents()
            }
            catch {
                $progressForm = $null
            }
        }

        New-Item -ItemType Directory -Force -Path $WorkDirectory | Out-Null
        $archivePath = Join-Path $WorkDirectory "Glasswork-win-x64.zip"
        $checksumPath = "$archivePath.sha256"

        & $Downloader $archiveUri $archivePath
        & $Downloader $checksumUri $checksumPath

        $expectedHash = ((Get-Content $checksumPath -Raw).Trim() -split '\s+')[0]
        if ($expectedHash -notmatch '^[A-Fa-f0-9]{64}$') {
            throw "The release checksum file is invalid."
        }
        $actualHash = (Get-FileHash -Path $archivePath -Algorithm SHA256).Hash
        if ($actualHash -ne $expectedHash) {
            throw "The downloaded release failed SHA-256 verification."
        }

        if (Test-Path $stagingDirectory) {
            Remove-Item -Recurse -Force $stagingDirectory
        }
        Expand-Archive -Path $archivePath -DestinationPath $stagingDirectory
        if (!(Test-Path (Join-Path $stagingDirectory "Glasswork.exe"))) {
            throw "The release archive does not contain Glasswork.exe."
        }

        if (Test-Path $backupDirectory) {
            Remove-Item -Recurse -Force $backupDirectory
        }

        Move-Item -Path $installDirectory -Destination $backupDirectory
        $installMoved = $true
        Move-Item -Path $stagingDirectory -Destination $installDirectory
        $extensionBundle = Join-Path $installDirectory "CopilotExtensions\glasswork-task-viewer"
        if (Test-Path $extensionBundle) {
            # Canvas extension activation must never fail the app update: a
            # broken/incomplete bundle here must not roll back an otherwise
            # successful app install, so failures are only warned about.
            try {
                $canvasResult = Install-GlassworkCanvasExtension -SourcePath $extensionBundle
                if ($canvasResult.Status -eq "Failed") {
                    Write-Warning "Glasswork canvas extension activation failed: $($canvasResult.Message)"
                }
            }
            catch {
                Write-Warning "Glasswork canvas extension activation failed: $_"
            }
        }

        & $Relauncher $InstallExePath
        $installMoved = $false
        Remove-Item -Recurse -Force $backupDirectory -ErrorAction SilentlyContinue
    }
    catch {
        Write-Warning "Glasswork update failed: $_"

        if ($installMoved -and (Test-Path $backupDirectory)) {
            if (Test-Path $installDirectory) {
                Remove-Item -Recurse -Force $installDirectory
            }
            Move-Item -Path $backupDirectory -Destination $installDirectory
            $installMoved = $false
        }

        if ($appExited -and (Test-Path $InstallExePath)) {
            & $Relauncher $InstallExePath
        }
    }
    finally {
        if ($progressForm) {
            try { $progressForm.Close() } catch { }
            try { $progressForm.Dispose() } catch { }
        }
        if ($mutexAcquired -and $mutex) {
            try { $mutex.ReleaseMutex() } catch { }
        }
        if ($mutex) {
            try { $mutex.Dispose() } catch { }
        }
        if (Test-Path $WorkDirectory) {
            Remove-Item -Recurse -Force $WorkDirectory -ErrorAction SilentlyContinue
        }
        if (Test-Path $stagingDirectory) {
            Remove-Item -Recurse -Force $stagingDirectory -ErrorAction SilentlyContinue
        }
    }
}
