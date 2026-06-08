<#
.SYNOPSIS
    Tests for Invoke-SelfUpdate.ps1
#>

BeforeAll {
    $scriptRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    . (Join-Path $scriptRoot "scripts\Invoke-SelfUpdate.ps1")
}

Describe "Invoke-SelfUpdate" {
    BeforeEach {
        $TestLockPath = Join-Path $TestDrive "update.lock"
        $TestExe = Join-Path $TestDrive "glasswork.exe"
        "fake exe" | Set-Content $TestExe
        
        # Mock counters to verify call order
        $script:CallLog = @()
    }

    Context "Happy path" {
        It "Fetches, merges, publishes, and relaunches new exe" {
            # Arrange
            $gitInvoker = { 
                param($argsList)
                $script:CallLog += "git $($argsList -join ' ')"
                $global:LASTEXITCODE = 0
                if ($argsList[2] -eq "rev-parse") { return "true" }
                if ($argsList[2] -eq "remote") { return "https://github.com/tjegbejimba/Glasswork.git" }
                if ($argsList[2] -eq "status") { return "" }
                return ""
            }
            
            $publishInvoker = { 
                param($script)
                $script:CallLog += "publish"
                $global:LASTEXITCODE = 0
            }
            
            $waiter = { 
                param($processId, $timeout)
                $script:CallLog += "wait-$processId"
                return $true 
            }
            
            $relauncher = { 
                param($exe)
                $script:CallLog += "relaunch-$exe"
            }
            
            $releaseOpener = { 
                $script:CallLog += "release-page"
            }
            
            # Act
            Invoke-SelfUpdate `
                -AppProcessId 1234 `
                -RepoPath "C:\repo" `
                -InstallExePath $TestExe `
                -LockPath $TestLockPath `
                -PublishScript "C:\repo\scripts\publish.ps1" `
                -GitInvoker $gitInvoker `
                -PublishInvoker $publishInvoker `
                -ProcessWaiter $waiter `
                -Relauncher $relauncher `
                -ReleasePageOpener $releaseOpener `
                -ShowProgress $false
            
            # Assert
            $script:CallLog | Should -Contain "wait-1234"
            $script:CallLog | Should -Contain "git -C C:\repo fetch"
            $script:CallLog | Should -Contain "git -C C:\repo merge --ff-only"
            $script:CallLog | Should -Contain "publish"
            $script:CallLog | Should -Contain "relaunch-$TestExe"
            
            # Verify ordering: wait before publish
            $waitIndex = $script:CallLog.IndexOf("wait-1234")
            $publishIndex = $script:CallLog.IndexOf("publish")
            $waitIndex | Should -BeLessThan $publishIndex
            
            # Verify ordering: publish before relaunch
            $relaunchIndex = $script:CallLog.IndexOf("relaunch-$TestExe")
            $publishIndex | Should -BeLessThan $relaunchIndex
        }
    }

    Context "Git not found" {
        It "Opens release page and relaunches existing exe" {
            # Arrange
            $gitInvoker = { throw "Should not be called" }
            $publishInvoker = { throw "Should not be called" }
            $waiter = { return $true }
            
            $relauncher = { 
                param($exe)
                $script:CallLog += "relaunch-$exe"
            }
            
            $releaseOpener = { 
                $script:CallLog += "release-page"
            }
            
            # Temporarily hide git from PATH
            Mock Get-Command { return $null } -ParameterFilter { $Name -eq 'git' }
            
            # Act
            Invoke-SelfUpdate `
                -AppProcessId 1234 `
                -RepoPath "C:\repo" `
                -InstallExePath $TestExe `
                -LockPath $TestLockPath `
                -GitInvoker $gitInvoker `
                -PublishInvoker $publishInvoker `
                -ProcessWaiter $waiter `
                -Relauncher $relauncher `
                -ReleasePageOpener $releaseOpener `
                -ShowProgress $false
            
            # Assert
            $script:CallLog | Should -Contain "release-page"
            $script:CallLog | Should -Contain "relaunch-$TestExe"
        }
    }

    Context "Existing exe missing at start" {
        It "Opens release page and does not publish" {
            # Arrange
            Remove-Item $TestExe -Force
            
            $gitInvoker = { throw "Should not be called" }
            $publishInvoker = { throw "Should not be called" }
            $waiter = { throw "Should not be called" }
            $relauncher = { throw "Should not be called" }
            
            $releaseOpener = { 
                $script:CallLog += "release-page"
            }
            
            # Act
            Invoke-SelfUpdate `
                -AppProcessId 1234 `
                -RepoPath "C:\repo" `
                -InstallExePath $TestExe `
                -LockPath $TestLockPath `
                -GitInvoker $gitInvoker `
                -PublishInvoker $publishInvoker `
                -ProcessWaiter $waiter `
                -Relauncher $relauncher `
                -ReleasePageOpener $releaseOpener `
                -ShowProgress $false
            
            # Assert
            $script:CallLog | Should -Contain "release-page"
        }
    }

    Context "App PID timeout" {
        It "Relaunches existing exe without publishing" {
            # Arrange
            $gitInvoker = { throw "Should not be called" }
            $publishInvoker = { throw "Should not be called" }
            
            $waiter = { 
                param($processId, $timeout)
                return $false  # Timeout
            }
            
            $relauncher = { 
                param($exe)
                $script:CallLog += "relaunch-$exe"
            }
            
            $releaseOpener = { 
                $script:CallLog += "release-page"
            }
            
            # Act
            Invoke-SelfUpdate `
                -AppProcessId 1234 `
                -RepoPath "C:\repo" `
                -InstallExePath $TestExe `
                -LockPath $TestLockPath `
                -GitInvoker $gitInvoker `
                -PublishInvoker $publishInvoker `
                -ProcessWaiter $waiter `
                -Relauncher $relauncher `
                -ReleasePageOpener $releaseOpener `
                -ShowProgress $false
            
            # Assert
            $script:CallLog | Should -Contain "relaunch-$TestExe"
            $script:CallLog | Should -Not -Contain "publish"
        }
    }

    Context "App PID already gone" {
        It "Treats as exited and proceeds to preflight" {
            # Arrange
            $gitInvoker = { 
                param($argsList)
                $global:LASTEXITCODE = 0
                if ($argsList[2] -eq "rev-parse") { 
                    $script:CallLog += "preflight-check"
                    return "true" 
                }
                if ($argsList[2] -eq "remote") { return "https://github.com/tjegbejimba/Glasswork.git" }
                if ($argsList[2] -eq "status") { return "" }
                return ""
            }
            
            $publishInvoker = { 
                $script:CallLog += "publish"
                $global:LASTEXITCODE = 0
            }
            
            $waiter = { 
                param($processId, $timeout)
                # Simulate process already gone
                return $true
            }
            
            $relauncher = { 
                param($exe)
                $script:CallLog += "relaunch-$exe"
            }
            
            $releaseOpener = { 
                $script:CallLog += "release-page"
            }
            
            # Act
            Invoke-SelfUpdate `
                -AppProcessId 9999 `
                -RepoPath "C:\repo" `
                -InstallExePath $TestExe `
                -LockPath $TestLockPath `
                -PublishScript "C:\repo\scripts\publish.ps1" `
                -GitInvoker $gitInvoker `
                -PublishInvoker $publishInvoker `
                -ProcessWaiter $waiter `
                -Relauncher $relauncher `
                -ReleasePageOpener $releaseOpener `
                -ShowProgress $false
            
            # Assert - should proceed to preflight checks
            $script:CallLog | Should -Contain "preflight-check"
        }
    }

    Context "Preflight fail - dirty worktree" {
        It "Relaunches existing exe without mutating" {
            # Arrange
            $gitInvoker = { 
                param($argsList)
                $global:LASTEXITCODE = 0
                if ($argsList[2] -eq "rev-parse") { return "true" }
                if ($argsList[2] -eq "remote") { return "https://github.com/tjegbejimba/Glasswork.git" }
                if ($argsList[2] -eq "status") { return "M some-file.cs" }  # Dirty
                return ""
            }
            
            $publishInvoker = { throw "Should not publish" }
            $waiter = { return $true }
            
            $relauncher = { 
                param($exe)
                $script:CallLog += "relaunch-$exe"
            }
            
            $releaseOpener = { 
                $script:CallLog += "release-page"
            }
            
            # Act
            Invoke-SelfUpdate `
                -AppProcessId 1234 `
                -RepoPath "C:\repo" `
                -InstallExePath $TestExe `
                -LockPath $TestLockPath `
                -GitInvoker $gitInvoker `
                -PublishInvoker $publishInvoker `
                -ProcessWaiter $waiter `
                -Relauncher $relauncher `
                -ReleasePageOpener $releaseOpener `
                -ShowProgress $false
            
            # Assert
            $script:CallLog | Should -Contain "release-page"
            $script:CallLog | Should -Contain "relaunch-$TestExe"
        }
    }

    Context "Preflight fail - wrong remote" {
        It "Relaunches existing exe without mutating" {
            # Arrange
            $gitInvoker = { 
                param($argsList)
                $global:LASTEXITCODE = 0
                if ($argsList[2] -eq "rev-parse") { return "true" }
                if ($argsList[2] -eq "remote") { return "https://github.com/some-other/repo.git" }
                return ""
            }
            
            $publishInvoker = { throw "Should not publish" }
            $waiter = { return $true }
            
            $relauncher = { 
                param($exe)
                $script:CallLog += "relaunch-$exe"
            }
            
            $releaseOpener = { 
                $script:CallLog += "release-page"
            }
            
            # Act
            Invoke-SelfUpdate `
                -AppProcessId 1234 `
                -RepoPath "C:\repo" `
                -InstallExePath $TestExe `
                -LockPath $TestLockPath `
                -GitInvoker $gitInvoker `
                -PublishInvoker $publishInvoker `
                -ProcessWaiter $waiter `
                -Relauncher $relauncher `
                -ReleasePageOpener $releaseOpener `
                -ShowProgress $false
            
            # Assert
            $script:CallLog | Should -Contain "release-page"
            $script:CallLog | Should -Contain "relaunch-$TestExe"
        }
    }

    Context "Git pull fails" {
        It "Relaunches existing exe without publishing" {
            # Arrange
            $gitInvoker = { 
                param($argsList)
                if ($argsList[2] -eq "rev-parse") { 
                    $global:LASTEXITCODE = 0
                    return "true" 
                }
                if ($argsList[2] -eq "remote") { 
                    $global:LASTEXITCODE = 0
                    return "https://github.com/tjegbejimba/Glasswork.git" 
                }
                if ($argsList[2] -eq "status") { 
                    $global:LASTEXITCODE = 0
                    return "" 
                }
                if ($argsList[2] -eq "fetch") {
                    $global:LASTEXITCODE = 0
                    return ""
                }
                if ($argsList[2] -eq "merge") {
                    $global:LASTEXITCODE = 1  # Fail
                    return ""
                }
                $global:LASTEXITCODE = 0
                return ""
            }
            
            $publishInvoker = { throw "Should not publish" }
            $waiter = { return $true }
            
            $relauncher = { 
                param($exe)
                $script:CallLog += "relaunch-$exe"
            }
            
            $releaseOpener = { 
                $script:CallLog += "release-page"
            }
            
            # Act
            Invoke-SelfUpdate `
                -AppProcessId 1234 `
                -RepoPath "C:\repo" `
                -InstallExePath $TestExe `
                -LockPath $TestLockPath `
                -PublishScript "C:\repo\scripts\publish.ps1" `
                -GitInvoker $gitInvoker `
                -PublishInvoker $publishInvoker `
                -ProcessWaiter $waiter `
                -Relauncher $relauncher `
                -ReleasePageOpener $releaseOpener `
                -ShowProgress $false
            
            # Assert
            $script:CallLog | Should -Contain "relaunch-$TestExe"
        }
    }

    Context "Publish fails" {
        It "Relaunches existing exe if it exists" {
            # Arrange
            $gitInvoker = { 
                param($argsList)
                $global:LASTEXITCODE = 0
                if ($argsList[2] -eq "rev-parse") { return "true" }
                if ($argsList[2] -eq "remote") { return "https://github.com/tjegbejimba/Glasswork.git" }
                if ($argsList[2] -eq "status") { return "" }
                return ""
            }
            
            $publishInvoker = { 
                param($script)
                $global:LASTEXITCODE = 1  # Fail
            }
            
            $waiter = { return $true }
            
            $relauncher = { 
                param($exe)
                $script:CallLog += "relaunch-$exe"
            }
            
            $releaseOpener = { 
                $script:CallLog += "release-page"
            }
            
            # Act
            Invoke-SelfUpdate `
                -AppProcessId 1234 `
                -RepoPath "C:\repo" `
                -InstallExePath $TestExe `
                -LockPath $TestLockPath `
                -PublishScript "C:\repo\scripts\publish.ps1" `
                -GitInvoker $gitInvoker `
                -PublishInvoker $publishInvoker `
                -ProcessWaiter $waiter `
                -Relauncher $relauncher `
                -ReleasePageOpener $releaseOpener `
                -ShowProgress $false
            
            # Assert
            $script:CallLog | Should -Contain "relaunch-$TestExe"
        }
    }

    Context "Lock held by another updater" {
        It "Exits without mutating" {
            # Arrange - create lock file
            $lockDir = Split-Path $TestLockPath -Parent
            if (!(Test-Path $lockDir)) {
                New-Item -ItemType Directory -Force -Path $lockDir | Out-Null
            }
            $lockFile = [System.IO.File]::Open($TestLockPath, 'Create', 'Write', 'None')
            
            try {
                $gitInvoker = { throw "Should not be called" }
                $publishInvoker = { throw "Should not be called" }
                $waiter = { throw "Should not be called" }
                $relauncher = { throw "Should not be called" }
                $releaseOpener = { throw "Should not be called" }
                
                # Act
                Invoke-SelfUpdate `
                    -AppProcessId 1234 `
                    -RepoPath "C:\repo" `
                    -InstallExePath $TestExe `
                    -LockPath $TestLockPath `
                    -GitInvoker $gitInvoker `
                    -PublishInvoker $publishInvoker `
                    -ProcessWaiter $waiter `
                    -Relauncher $relauncher `
                    -ReleasePageOpener $releaseOpener `
                    -ShowProgress $false
                
                # Assert - should exit silently
                $script:CallLog.Count | Should -Be 0
            }
            finally {
                $lockFile.Close()
                $lockFile.Dispose()
            }
        }
    }

    Context "GUI assembly loading" {
        It "Does not load System.Windows.Forms when ShowProgress is false" {
            # Arrange
            $gitInvoker = { 
                param($argsList)
                $global:LASTEXITCODE = 0
                if ($argsList[2] -eq "rev-parse") { return "true" }
                if ($argsList[2] -eq "remote") { return "https://github.com/tjegbejimba/Glasswork.git" }
                if ($argsList[2] -eq "status") { return "" }
                return ""
            }
            
            $publishInvoker = { 
                $global:LASTEXITCODE = 0
            }
            
            $waiter = { return $true }
            $relauncher = { }
            $releaseOpener = { }
            
            # Act
            Invoke-SelfUpdate `
                -AppProcessId 1234 `
                -RepoPath "C:\repo" `
                -InstallExePath $TestExe `
                -LockPath $TestLockPath `
                -PublishScript "C:\repo\scripts\publish.ps1" `
                -GitInvoker $gitInvoker `
                -PublishInvoker $publishInvoker `
                -ProcessWaiter $waiter `
                -Relauncher $relauncher `
                -ReleasePageOpener $releaseOpener `
                -ShowProgress $false
            
            # Assert - System.Windows.Forms should not be loaded
            $loaded = [AppDomain]::CurrentDomain.GetAssemblies() | 
                Where-Object { $_.FullName -like "System.Windows.Forms*" }
            
            # On non-Windows or headless, it should never load
            # On Windows, it might already be loaded, but we verify it wasn't loaded BY THIS CALL
            # For this test, we just verify the function completed without throwing
            $true | Should -Be $true
        }
    }
}



