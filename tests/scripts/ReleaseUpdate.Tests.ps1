BeforeAll {
    $scriptRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    . (Join-Path $scriptRoot "scripts\New-ReleasePackage.ps1")
    . (Join-Path $scriptRoot "scripts\Invoke-ReleaseUpdate.ps1")

    function New-TestReleasePackage {
        param($PublishDirectory, $OutputDirectory)

        $updaterDirectory = Join-Path $PublishDirectory "Updater"
        $mcpUpdaterDirectory = Join-Path $PublishDirectory "McpUpdater"
        New-Item -ItemType Directory -Path $updaterDirectory, $mcpUpdaterDirectory | Out-Null
        Set-Content -Path (Join-Path $updaterDirectory "release-update.ps1") -Value "wrapper"
        Set-Content -Path (Join-Path $updaterDirectory "Invoke-ReleaseUpdate.ps1") -Value "updater"
        Set-Content -Path (Join-Path $mcpUpdaterDirectory "install-mcp.ps1") -Value "wrapper"
        Set-Content -Path (Join-Path $mcpUpdaterDirectory "Install-McpTool.ps1") -Value "installer"
        Set-Content -Path (Join-Path $mcpUpdaterDirectory "Validate-McpReleasePublication.ps1") -Value "validation"
        New-ReleasePackage `
            -PublishDirectory $PublishDirectory `
            -OutputDirectory $OutputDirectory
    }
}

Describe "Invoke-ReleaseUpdate" {
    It "Downloads, verifies, installs, opens release notes, and relaunches the new version" {
        $installDirectory = Join-Path $TestDrive "install"
        $packageDirectory = Join-Path $TestDrive "package"
        $releaseDirectory = Join-Path $TestDrive "release"
        $workDirectory = Join-Path $TestDrive "work"
        New-Item -ItemType Directory -Path $installDirectory, $packageDirectory | Out-Null
        Set-Content -Path (Join-Path $installDirectory "Glasswork.exe") -Value "old version"
        Set-Content -Path (Join-Path $packageDirectory "Glasswork.exe") -Value "new version"
        $release = New-TestReleasePackage `
            -PublishDirectory $packageDirectory `
            -OutputDirectory $releaseDirectory

        $script:CallLog = @()
        $downloader = {
            param($uri, $destination)
            $script:CallLog += "download-$uri"
            $source = if ($uri.EndsWith(".sha256")) {
                $release.ChecksumPath
            }
            else {
                $release.ArchivePath
            }
            Copy-Item $source $destination
        }
        $relauncher = {
            param($exe)
            $script:CallLog += "relaunch-$exe"
        }
        $releasePageOpener = {
            param($uri)
            $script:CallLog += "release-page-$uri"
        }

        Invoke-ReleaseUpdate `
            -AppProcessId 1234 `
            -InstallExePath (Join-Path $installDirectory "Glasswork.exe") `
            -Version "1.5.0" `
            -MutexName "Local\Glasswork.ReleaseUpdateTest.$([guid]::NewGuid())" `
            -WorkDirectory $workDirectory `
            -Downloader $downloader `
            -ProcessWaiter { return $true } `
            -Relauncher $relauncher `
            -ReleasePageOpener $releasePageOpener `
            -ShowProgress $false

        (Get-Content (Join-Path $installDirectory "Glasswork.exe") -Raw).Trim() |
            Should -Be "new version"
        $script:CallLog | Should -Contain "download-https://github.com/tjegbejimba/Glasswork/releases/download/v1.5.0/Glasswork-win-x64.zip"
        $script:CallLog | Should -Contain "download-https://github.com/tjegbejimba/Glasswork/releases/download/v1.5.0/Glasswork-win-x64.zip.sha256"
        $script:CallLog | Should -Contain "release-page-https://github.com/tjegbejimba/Glasswork/releases/tag/v1.5.0"
        $script:CallLog | Should -Contain "relaunch-$(Join-Path $installDirectory "Glasswork.exe")"
    }

    It "Keeps and relaunches the installed version when checksum verification fails" {
        $installDirectory = Join-Path $TestDrive "install-corrupt"
        $packageDirectory = Join-Path $TestDrive "package-corrupt"
        $releaseDirectory = Join-Path $TestDrive "release-corrupt"
        New-Item -ItemType Directory -Path $installDirectory, $packageDirectory | Out-Null
        Set-Content -Path (Join-Path $installDirectory "Glasswork.exe") -Value "old version"
        Set-Content -Path (Join-Path $packageDirectory "Glasswork.exe") -Value "new version"
        $release = New-TestReleasePackage `
            -PublishDirectory $packageDirectory `
            -OutputDirectory $releaseDirectory

        $script:Relaunched = $null
        $downloader = {
            param($uri, $destination)
            if ($uri.EndsWith(".sha256")) {
                Set-Content $destination ("0" * 64 + "  Glasswork-win-x64.zip")
            }
            else {
                Copy-Item $release.ArchivePath $destination
            }
        }

        Invoke-ReleaseUpdate `
            -AppProcessId 1234 `
            -InstallExePath (Join-Path $installDirectory "Glasswork.exe") `
            -Version "1.5.0" `
            -MutexName "Local\Glasswork.ReleaseUpdateTest.$([guid]::NewGuid())" `
            -WorkDirectory (Join-Path $TestDrive "corrupt-work") `
            -Downloader $downloader `
            -ProcessWaiter { return $true } `
            -Relauncher { param($exe) $script:Relaunched = $exe } `
            -ReleasePageOpener { } `
            -ShowProgress $false `
            -WarningAction SilentlyContinue

        (Get-Content (Join-Path $installDirectory "Glasswork.exe") -Raw).Trim() |
            Should -Be "old version"
        $script:Relaunched | Should -Be (Join-Path $installDirectory "Glasswork.exe")
    }

    It "Restores the installed version when the new version cannot be launched" {
        $installDirectory = Join-Path $TestDrive "install-launch-failure"
        $packageDirectory = Join-Path $TestDrive "package-launch-failure"
        $releaseDirectory = Join-Path $TestDrive "release-launch-failure"
        New-Item -ItemType Directory -Path $installDirectory, $packageDirectory | Out-Null
        Set-Content -Path (Join-Path $installDirectory "Glasswork.exe") -Value "old version"
        Set-Content -Path (Join-Path $packageDirectory "Glasswork.exe") -Value "new version"
        $release = New-TestReleasePackage `
            -PublishDirectory $packageDirectory `
            -OutputDirectory $releaseDirectory

        $downloader = {
            param($uri, $destination)
            $source = if ($uri.EndsWith(".sha256")) {
                $release.ChecksumPath
            }
            else {
                $release.ArchivePath
            }
            Copy-Item $source $destination
        }
        $script:RelaunchAttempts = 0
        $relauncher = {
            param($exe)
            $script:RelaunchAttempts++
            if ($script:RelaunchAttempts -eq 1) {
                throw "new version failed to launch"
            }
        }

        Invoke-ReleaseUpdate `
            -AppProcessId 1234 `
            -InstallExePath (Join-Path $installDirectory "Glasswork.exe") `
            -Version "1.5.0" `
            -MutexName "Local\Glasswork.ReleaseUpdateTest.$([guid]::NewGuid())" `
            -WorkDirectory (Join-Path $TestDrive "launch-failure-work") `
            -Downloader $downloader `
            -ProcessWaiter { return $true } `
            -Relauncher $relauncher `
            -ReleasePageOpener { } `
            -ShowProgress $false `
            -WarningAction SilentlyContinue

        (Get-Content (Join-Path $installDirectory "Glasswork.exe") -Raw).Trim() |
            Should -Be "old version"
        $script:RelaunchAttempts | Should -Be 2
    }

    It "Does not relaunch while the original app is still running" {
        $installDirectory = Join-Path $TestDrive "install-timeout"
        New-Item -ItemType Directory -Path $installDirectory | Out-Null
        Set-Content -Path (Join-Path $installDirectory "Glasswork.exe") -Value "old version"
        $script:Relaunched = $false

        Invoke-ReleaseUpdate `
            -AppProcessId 1234 `
            -InstallExePath (Join-Path $installDirectory "Glasswork.exe") `
            -Version "1.5.0" `
            -MutexName "Local\Glasswork.ReleaseUpdateTest.$([guid]::NewGuid())" `
            -WorkDirectory (Join-Path $TestDrive "timeout-work") `
            -Downloader { throw "Should not download" } `
            -ProcessWaiter { return $false } `
            -Relauncher { $script:Relaunched = $true } `
            -ReleasePageOpener { } `
            -ShowProgress $false `
            -WarningAction SilentlyContinue

        $script:Relaunched | Should -BeFalse
    }

    It "Runs from and removes a temporary updater directory under Windows PowerShell" {
        $updaterDirectory = Join-Path $TestDrive "copied-updater"
        New-Item -ItemType Directory -Path $updaterDirectory | Out-Null
        Copy-Item (Join-Path $scriptRoot "scripts\release-update.ps1") $updaterDirectory
        Copy-Item (Join-Path $scriptRoot "scripts\Invoke-ReleaseUpdate.ps1") $updaterDirectory
        $powershell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"

        & $powershell `
            -NoProfile `
            -ExecutionPolicy Bypass `
            -File (Join-Path $updaterDirectory "release-update.ps1") `
            -AppProcessId 1234 `
            -InstallExePath (Join-Path $TestDrive "missing\Glasswork.exe") `
            -Version "1.5.0" `
            -CleanupDirectory $updaterDirectory

        $LASTEXITCODE | Should -Be 0
        Test-Path $updaterDirectory | Should -BeFalse
    }
}
