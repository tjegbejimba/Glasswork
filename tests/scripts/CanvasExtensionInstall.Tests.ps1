<#
.SYNOPSIS
    Tests for staged/verified/atomic Glasswork canvas extension installation
    (issue #561).
#>

BeforeAll {
    $script:RepoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    . (Join-Path $script:RepoRoot "scripts\Install-CanvasExtension.ps1")

    function New-TestCanvasBundle {
        param(
            [Parameter(Mandatory = $true)]
            [string]$Path,

            [string]$Version = "1.4.11",

            [string]$SourceRevision = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",

            [string]$Sha256 = ("b" * 64),

            [switch]$OmitManifest,

            [switch]$OmitExtensionFile,

            [switch]$OmitHostExecutable,

            [switch]$MismatchedActiveVersion
        )

        New-Item -ItemType Directory -Force -Path $Path | Out-Null
        if (-not $OmitExtensionFile) {
            "// test extension adapter" | Set-Content (Join-Path $Path "extension.mjs")
        }
        if (-not $OmitManifest) {
            [ordered]@{
                version        = $Version
                sourceRevision = $SourceRevision
                sha256         = $Sha256
            } | ConvertTo-Json | Set-Content (Join-Path $Path "manifest.json")
        }

        $hostRoot = Join-Path $Path "host"
        New-Item -ItemType Directory -Force -Path $hostRoot | Out-Null
        $activeVersion = if ($MismatchedActiveVersion) { "9.9.9" } else { $Version }
        Set-Content -Path (Join-Path $hostRoot "active.txt") -Value $activeVersion -Encoding ascii

        $versionDirectory = Join-Path $hostRoot $Version
        New-Item -ItemType Directory -Force -Path $versionDirectory | Out-Null
        if (-not $OmitHostExecutable) {
            "fake host binary" | Set-Content (Join-Path $versionDirectory "Glasswork.CanvasHost.dll")
        }

        $versionDirectory
    }
}

Describe "Get-CanvasExtensionBundleManifest" {
    It "reads a complete bundle's version, source identity, and checksum" {
        $bundle = Join-Path $TestDrive "complete-bundle"
        New-TestCanvasBundle -Path $bundle -Version "1.4.11" -SourceRevision "cccccccccccccccccccccccccccccccccccccccc" | Out-Null

        $manifest = Get-CanvasExtensionBundleManifest -SourcePath $bundle

        $manifest.Version | Should -Be "1.4.11"
        $manifest.SourceRevision | Should -Be "cccccccccccccccccccccccccccccccccccccccc"
        $manifest.Sha256 | Should -Be ("b" * 64)
        Test-Path $manifest.VersionDirectory -PathType Container | Should -BeTrue
    }

    It "rejects a bundle missing extension.mjs" {
        $bundle = Join-Path $TestDrive "no-extension-file"
        New-TestCanvasBundle -Path $bundle -OmitExtensionFile | Out-Null

        { Get-CanvasExtensionBundleManifest -SourcePath $bundle } | Should -Throw "*extension.mjs*"
    }

    It "rejects a bundle missing manifest.json" {
        $bundle = Join-Path $TestDrive "no-manifest"
        New-TestCanvasBundle -Path $bundle -OmitManifest | Out-Null

        { Get-CanvasExtensionBundleManifest -SourcePath $bundle } | Should -Throw "*manifest.json*"
    }

    It "rejects a bundle whose active.txt version does not match manifest.json" {
        $bundle = Join-Path $TestDrive "mismatched-version"
        New-TestCanvasBundle -Path $bundle -MismatchedActiveVersion | Out-Null

        { Get-CanvasExtensionBundleManifest -SourcePath $bundle } | Should -Throw "*does not match*"
    }

    It "rejects a bundle missing the canvas host executable" {
        $bundle = Join-Path $TestDrive "no-host-exe"
        New-TestCanvasBundle -Path $bundle -OmitHostExecutable | Out-Null

        { Get-CanvasExtensionBundleManifest -SourcePath $bundle } | Should -Throw "*canvas host executable*"
    }
}

Describe "Install-GlassworkCanvasExtension" {
    BeforeEach {
        $script:StagedIdentity = "1.4.11+bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        Mock Get-CanvasHostIdentity {
            if ($VersionDirectory -like "*.pending-*") {
                return $script:StagedIdentity
            }
            return $null
        }
    }

    It "returns a bounded Failed result (never throws) for an incomplete bundle" {
        $bundle = Join-Path $TestDrive "incomplete"
        New-TestCanvasBundle -Path $bundle -OmitManifest | Out-Null
        $extensionsRoot = Join-Path $TestDrive "extensions-incomplete"

        $result = Install-GlassworkCanvasExtension -SourcePath $bundle -ExtensionsRoot $extensionsRoot

        $result.Status | Should -Be "Failed"
        $result.Message | Should -Match "manifest.json"
        Test-Path (Join-Path $extensionsRoot "glasswork-task-viewer") | Should -BeFalse
    }

    It "performs a fresh install and activates the version atomically" {
        $bundle = Join-Path $TestDrive "fresh-bundle"
        New-TestCanvasBundle -Path $bundle -Version "1.4.11" -SourceRevision "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" | Out-Null
        $extensionsRoot = Join-Path $TestDrive "fresh-extensions"

        $result = Install-GlassworkCanvasExtension -SourcePath $bundle -ExtensionsRoot $extensionsRoot

        $result.Status | Should -Be "Installed"
        $result.Identity | Should -Be $script:StagedIdentity
        $extensionDirectory = Join-Path $extensionsRoot "glasswork-task-viewer"
        Test-Path (Join-Path $extensionDirectory "extension.mjs") -PathType Leaf | Should -BeTrue
        (Get-Content (Join-Path $extensionDirectory "host\active.txt") -Raw).Trim() | Should -Be "1.4.11"
        Test-Path (Join-Path $extensionDirectory "host\1.4.11\Glasswork.CanvasHost.dll") -PathType Leaf | Should -BeTrue
        $state = Get-Content (Join-Path $extensionDirectory "current.json") -Raw | ConvertFrom-Json
        $state.version | Should -Be "1.4.11"
        $state.identity | Should -Be $script:StagedIdentity
        $state.lastAttempt.status | Should -Be "ok"
        (Get-ChildItem $extensionDirectory\host -Directory -Filter ".pending-*").Count | Should -Be 0
    }

    It "updates from a prior version and reports Updated" {
        $bundle = Join-Path $TestDrive "update-bundle"
        New-TestCanvasBundle -Path $bundle -Version "1.5.0" -SourceRevision "dddddddddddddddddddddddddddddddddddddddd" | Out-Null
        $script:StagedIdentity = "1.5.0+dddddddddddddddddddddddddddddddddddddddd"
        $extensionsRoot = Join-Path $TestDrive "update-extensions"
        $extensionDirectory = Join-Path $extensionsRoot "glasswork-task-viewer"
        New-Item -ItemType Directory -Force -Path (Join-Path $extensionDirectory "host\1.4.11") | Out-Null
        "old host binary" | Set-Content (Join-Path $extensionDirectory "host\1.4.11\Glasswork.CanvasHost.dll")
        Set-Content -Path (Join-Path $extensionDirectory "host\active.txt") -Value "1.4.11" -Encoding ascii
        [ordered]@{
            version = "1.4.11"; identity = "1.4.11+aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            sourceRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"; sha256 = ("a" * 64)
            hostExecutablePath = (Join-Path $extensionDirectory "host\1.4.11\Glasswork.CanvasHost.dll")
        } | ConvertTo-Json | Set-Content (Join-Path $extensionDirectory "current.json")
        # Simulate an old session's still-running host process holding this
        # file open — the exact case eventual cleanup must not disturb.
        $lockedFile = [System.IO.File]::Open(
            (Join-Path $extensionDirectory "host\1.4.11\Glasswork.CanvasHost.dll"),
            [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)

        try {
            $result = Install-GlassworkCanvasExtension -SourcePath $bundle -ExtensionsRoot $extensionsRoot

            $result.Status | Should -Be "Updated"
            (Get-Content (Join-Path $extensionDirectory "host\active.txt") -Raw).Trim() | Should -Be "1.5.0"
            # Old version stays side by side — a running session's already-spawned
            # host process still holds its file open, so cleanup must skip it
            # rather than fail the activation (issue #562).
            Test-Path (Join-Path $extensionDirectory "host\1.4.11\Glasswork.CanvasHost.dll") -PathType Leaf | Should -BeTrue
        }
        finally {
            $lockedFile.Dispose()
        }
    }

    It "garbage-collects an unlocked prior version once no process is using it" {
        $bundle = Join-Path $TestDrive "gc-bundle"
        New-TestCanvasBundle -Path $bundle -Version "1.5.0" -SourceRevision "dddddddddddddddddddddddddddddddddddddddd" | Out-Null
        $script:StagedIdentity = "1.5.0+dddddddddddddddddddddddddddddddddddddddd"
        $extensionsRoot = Join-Path $TestDrive "gc-extensions"
        $extensionDirectory = Join-Path $extensionsRoot "glasswork-task-viewer"
        New-Item -ItemType Directory -Force -Path (Join-Path $extensionDirectory "host\1.4.11") | Out-Null
        "old host binary" | Set-Content (Join-Path $extensionDirectory "host\1.4.11\Glasswork.CanvasHost.dll")
        Set-Content -Path (Join-Path $extensionDirectory "host\active.txt") -Value "1.4.11" -Encoding ascii
        [ordered]@{
            version = "1.4.11"; identity = "1.4.11+aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            sourceRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"; sha256 = ("a" * 64)
            hostExecutablePath = (Join-Path $extensionDirectory "host\1.4.11\Glasswork.CanvasHost.dll")
        } | ConvertTo-Json | Set-Content (Join-Path $extensionDirectory "current.json")

        $result = Install-GlassworkCanvasExtension -SourcePath $bundle -ExtensionsRoot $extensionsRoot

        $result.Status | Should -Be "Updated"
        # Nothing holds the old version's files open, so it is safe to remove
        # once a newer version has been verified and activated.
        Test-Path (Join-Path $extensionDirectory "host\1.4.11") | Should -BeFalse
        Test-Path (Join-Path $extensionDirectory "host\1.5.0\Glasswork.CanvasHost.dll") -PathType Leaf | Should -BeTrue
    }

    It "sweeps a version left over from an earlier activation during a no-op reinstall" {
        $bundle = Join-Path $TestDrive "retry-gc-bundle"
        New-TestCanvasBundle -Path $bundle -Version "1.4.11" -SourceRevision "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" | Out-Null
        $extensionsRoot = Join-Path $TestDrive "retry-gc-extensions"
        $extensionDirectory = Join-Path $extensionsRoot "glasswork-task-viewer"
        New-Item -ItemType Directory -Force -Path (Join-Path $extensionDirectory "host\1.4.11") | Out-Null
        "active host binary" | Set-Content (Join-Path $extensionDirectory "host\1.4.11\Glasswork.CanvasHost.dll")
        New-Item -ItemType Directory -Force -Path (Join-Path $extensionDirectory "host\1.3.0") | Out-Null
        "stale host binary" | Set-Content (Join-Path $extensionDirectory "host\1.3.0\Glasswork.CanvasHost.dll")
        Set-Content -Path (Join-Path $extensionDirectory "host\active.txt") -Value "1.4.11" -Encoding ascii
        [ordered]@{
            version = "1.4.11"; identity = $script:StagedIdentity
            sourceRevision = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"; sha256 = ("b" * 64)
            hostExecutablePath = (Join-Path $extensionDirectory "host\1.4.11\Glasswork.CanvasHost.dll")
        } | ConvertTo-Json | Set-Content (Join-Path $extensionDirectory "current.json")
        Mock Get-CanvasHostIdentity { $script:StagedIdentity }

        # No-op reinstall (Status "Current") must still sweep for stale
        # versions left over from an earlier activation whose cleanup was
        # blocked at the time.
        $result = Install-GlassworkCanvasExtension -SourcePath $bundle -ExtensionsRoot $extensionsRoot

        $result.Status | Should -Be "Current"
        Test-Path (Join-Path $extensionDirectory "host\1.3.0") | Should -BeFalse
        Test-Path (Join-Path $extensionDirectory "host\1.4.11\Glasswork.CanvasHost.dll") -PathType Leaf | Should -BeTrue
    }

    It "is idempotent: reports Current and makes no changes when already active" {
        $bundle = Join-Path $TestDrive "current-bundle"
        New-TestCanvasBundle -Path $bundle -Version "1.4.11" -SourceRevision "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" | Out-Null
        $extensionsRoot = Join-Path $TestDrive "current-extensions"
        $extensionDirectory = Join-Path $extensionsRoot "glasswork-task-viewer"
        New-Item -ItemType Directory -Force -Path (Join-Path $extensionDirectory "host\1.4.11") | Out-Null
        "already active host binary" | Set-Content (Join-Path $extensionDirectory "host\1.4.11\Glasswork.CanvasHost.dll")
        Set-Content -Path (Join-Path $extensionDirectory "host\active.txt") -Value "1.4.11" -Encoding ascii
        [ordered]@{
            version = "1.4.11"; identity = $script:StagedIdentity
            sourceRevision = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"; sha256 = ("b" * 64)
            hostExecutablePath = (Join-Path $extensionDirectory "host\1.4.11\Glasswork.CanvasHost.dll")
        } | ConvertTo-Json | Set-Content (Join-Path $extensionDirectory "current.json")
        $beforeContent = Get-Content (Join-Path $extensionDirectory "current.json") -Raw
        Mock Get-CanvasHostIdentity { $script:StagedIdentity }

        $result = Install-GlassworkCanvasExtension -SourcePath $bundle -ExtensionsRoot $extensionsRoot

        $result.Status | Should -Be "Current"
        (Get-Content (Join-Path $extensionDirectory "current.json") -Raw) | Should -Be $beforeContent
        (Get-Content (Join-Path $extensionDirectory "host\1.4.11\Glasswork.CanvasHost.dll") -Raw) |
            Should -Match "already active host binary"
    }

    It "preserves the previously active version and records a failed attempt when staging verification fails" {
        $bundle = Join-Path $TestDrive "corrupt-bundle"
        New-TestCanvasBundle -Path $bundle -Version "1.5.0" -SourceRevision "dddddddddddddddddddddddddddddddddddddddd" | Out-Null
        $extensionsRoot = Join-Path $TestDrive "corrupt-extensions"
        $extensionDirectory = Join-Path $extensionsRoot "glasswork-task-viewer"
        New-Item -ItemType Directory -Force -Path (Join-Path $extensionDirectory "host\1.4.11") | Out-Null
        "good host binary" | Set-Content (Join-Path $extensionDirectory "host\1.4.11\Glasswork.CanvasHost.dll")
        Set-Content -Path (Join-Path $extensionDirectory "host\active.txt") -Value "1.4.11" -Encoding ascii
        [ordered]@{
            version = "1.4.11"; identity = "1.4.11+aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            sourceRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"; sha256 = ("a" * 64)
            hostExecutablePath = (Join-Path $extensionDirectory "host\1.4.11\Glasswork.CanvasHost.dll")
        } | ConvertTo-Json | Set-Content (Join-Path $extensionDirectory "current.json")
        # Simulate corruption in transit: staged copy never reports the expected identity.
        Mock Get-CanvasHostIdentity { $null }

        $result = Install-GlassworkCanvasExtension -SourcePath $bundle -ExtensionsRoot $extensionsRoot

        $result.Status | Should -Be "Failed"
        $result.Message | Should -Match "does not match expected"
        (Get-Content (Join-Path $extensionDirectory "host\active.txt") -Raw).Trim() | Should -Be "1.4.11"
        (Get-Content (Join-Path $extensionDirectory "host\1.4.11\Glasswork.CanvasHost.dll") -Raw) |
            Should -Match "good host binary"
        $state = Get-Content (Join-Path $extensionDirectory "current.json") -Raw | ConvertFrom-Json
        $state.version | Should -Be "1.4.11"
        $state.identity | Should -Be "1.4.11+aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        $state.lastAttempt.status | Should -Be "failed"
        $state.lastAttempt.version | Should -Be "1.5.0"
        (Get-ChildItem (Join-Path $extensionDirectory "host") -Directory -Filter ".pending-*").Count | Should -Be 0
    }

    It "returns a bounded Failed result when the extensions location cannot be created" {
        $bundle = Join-Path $TestDrive "unreachable-root-bundle"
        New-TestCanvasBundle -Path $bundle | Out-Null
        # A path nested under a file (not a directory) can never be created.
        $blocker = Join-Path $TestDrive "blocker-file"
        "not a directory" | Set-Content $blocker
        $unreachableRoot = Join-Path $blocker "extensions"

        $result = Install-GlassworkCanvasExtension -SourcePath $bundle -ExtensionsRoot $unreachableRoot

        $result.Status | Should -Be "Failed"
        $result.Message | Should -Match "blocker-file"
    }
}

Describe "retry-canvas-extension.ps1" {
    BeforeAll {
        # A real published host is required here: the CLI wrapper runs as its
        # own external process, so Pester's `Mock Get-CanvasHostIdentity` (used
        # elsewhere in this file) cannot reach across process boundaries. Built
        # once and reused by every test in this block to bound total runtime.
        $script:RealBundleVersion = "1.9.10"
        $script:RealBundleSourceRevision = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"
        $script:RealBundle = Join-Path $TestDrive "retry-cli-real-bundle"
        $realVersionDirectory = Join-Path $script:RealBundle "host\$script:RealBundleVersion"
        New-Item -ItemType Directory -Force -Path $script:RealBundle | Out-Null
        "// real extension adapter (not exercised by this test)" |
            Set-Content (Join-Path $script:RealBundle "extension.mjs")

        & dotnet publish (Join-Path $script:RepoRoot "tools\Glasswork.CanvasHost\Glasswork.CanvasHost.csproj") `
            --configuration Release `
            --self-contained `
            --runtime win-x64 `
            --output $realVersionDirectory `
            -p:Version=$script:RealBundleVersion `
            -p:RepositoryCommit=$script:RealBundleSourceRevision `
            --nologo `
            --verbosity quiet
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed while preparing the real canvas host bundle for retry-canvas-extension.ps1 tests."
        }

        Set-Content -Path (Join-Path $script:RealBundle "host\active.txt") -Value $script:RealBundleVersion -Encoding ascii
        $realHostExe = Join-Path $realVersionDirectory "Glasswork.CanvasHost.exe"
        $realSha256 = (Get-FileHash -Algorithm SHA256 -Path $realHostExe).Hash.ToLowerInvariant()
        [ordered]@{
            version        = $script:RealBundleVersion
            sourceRevision = $script:RealBundleSourceRevision
            sha256         = $realSha256
        } | ConvertTo-Json | Set-Content (Join-Path $script:RealBundle "manifest.json")
    }

    It "retries activation and prints a JSON result on success" {
        $extensionsRoot = Join-Path $TestDrive "retry-cli-extensions"

        $output = & (Join-Path $script:RepoRoot "scripts\retry-canvas-extension.ps1") `
            -SourcePath $script:RealBundle -ExtensionsRoot $extensionsRoot
        $exitCode = $LASTEXITCODE

        $exitCode | Should -Be 0
        $parsed = $output | ConvertFrom-Json
        $parsed.status | Should -Be "Installed"
        Test-Path (Join-Path $extensionsRoot "glasswork-task-viewer\current.json") -PathType Leaf | Should -BeTrue
    }

    It "exits non-zero and reports the failure message for an incomplete bundle" {
        $bundle = Join-Path $TestDrive "retry-cli-bad-bundle"
        New-Item -ItemType Directory -Force -Path $bundle | Out-Null
        $extensionsRoot = Join-Path $TestDrive "retry-cli-bad-extensions"

        $output = & (Join-Path $script:RepoRoot "scripts\retry-canvas-extension.ps1") `
            -SourcePath $bundle -ExtensionsRoot $extensionsRoot
        $exitCode = $LASTEXITCODE

        $exitCode | Should -Be 1
        $parsed = $output | ConvertFrom-Json
        $parsed.status | Should -Be "Failed"
        $parsed.message | Should -Match "extension.mjs"
    }

    It "is idempotent when retried against an already-active bundle" {
        $extensionsRoot = Join-Path $TestDrive "retry-cli-idempotent-extensions"

        & (Join-Path $script:RepoRoot "scripts\retry-canvas-extension.ps1") -SourcePath $script:RealBundle -ExtensionsRoot $extensionsRoot | Out-Null
        $LASTEXITCODE | Should -Be 0
        $secondOutput = & (Join-Path $script:RepoRoot "scripts\retry-canvas-extension.ps1") -SourcePath $script:RealBundle -ExtensionsRoot $extensionsRoot
        $secondExitCode = $LASTEXITCODE

        $secondExitCode | Should -Be 0
        ($secondOutput | ConvertFrom-Json).status | Should -Be "Current"
    }
}

Describe "Install-GlassworkCanvasExtension duplicate registration" {
    It "installs under the same extension name the repo already ships at project scope, so the Copilot CLI's by-name discovery shadowing prevents a duplicate registration" {
        # The Copilot CLI discovers extensions by directory name and shadows
        # duplicates at discovery time: if .github/extensions/<name>/ exists,
        # a user-scope extension with the same <name> is dropped before
        # loading (see the create-canvas skill's "ID model" section). The
        # installer must therefore activate under the exact same <name> the
        # repo already ships in .github/extensions/, or a real machine with
        # both scopes populated would end up with two independent
        # "glasswork-task-viewer" canvas providers registered instead of one.
        $projectScopeExtension = Join-Path $script:RepoRoot ".github\extensions\glasswork-task-viewer"
        Test-Path $projectScopeExtension -PathType Container | Should -BeTrue

        $bundle = Join-Path $TestDrive "bundle"
        New-TestCanvasBundle -Path $bundle
        Mock Get-CanvasHostIdentity { "1.4.11+bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }

        $extensionsRoot = Join-Path $TestDrive "extensions"
        $result = Install-GlassworkCanvasExtension -SourcePath $bundle -ExtensionsRoot $extensionsRoot

        $result.Status | Should -Be "Installed"
        $installedName = Split-Path (Join-Path $extensionsRoot "glasswork-task-viewer") -Leaf
        $installedName | Should -Be (Split-Path $projectScopeExtension -Leaf)
    }
}

Describe "Install-GlassworkCanvasExtension integration" {
    It "installs a real self-contained canvas host build and serves a real Task with no native app running" {
        $version = "1.9.9"
        $sourceRevision = "cccccccccccccccccccccccccccccccccccccccc"
        $bundle = Join-Path $TestDrive "real-bundle"
        $versionDirectory = Join-Path $bundle "host\$version"
        New-Item -ItemType Directory -Force -Path $bundle | Out-Null
        "// real extension adapter (not exercised by this test)" |
            Set-Content (Join-Path $bundle "extension.mjs")

        & dotnet publish (Join-Path $script:RepoRoot "tools\Glasswork.CanvasHost\Glasswork.CanvasHost.csproj") `
            --configuration Release `
            --self-contained `
            --runtime win-x64 `
            --output $versionDirectory `
            -p:Version=$version `
            -p:RepositoryCommit=$sourceRevision `
            --nologo `
            --verbosity quiet
        $LASTEXITCODE | Should -Be 0

        Set-Content -Path (Join-Path $bundle "host\active.txt") -Value $version -Encoding ascii
        $hostExe = Join-Path $versionDirectory "Glasswork.CanvasHost.exe"
        Test-Path $hostExe -PathType Leaf | Should -BeTrue
        $sha256 = (Get-FileHash -Algorithm SHA256 -Path $hostExe).Hash.ToLowerInvariant()
        [ordered]@{
            version        = $version
            sourceRevision = $sourceRevision
            sha256         = $sha256
        } | ConvertTo-Json | Set-Content (Join-Path $bundle "manifest.json")

        $extensionsRoot = Join-Path $TestDrive "real-extensions"
        $result = Install-GlassworkCanvasExtension -SourcePath $bundle -ExtensionsRoot $extensionsRoot

        $result.Status | Should -Be "Installed"
        $result.Identity | Should -Be "$version+$sourceRevision"
        $extensionDirectory = Join-Path $extensionsRoot "glasswork-task-viewer"
        $installedExe = Join-Path $extensionDirectory "host\$version\Glasswork.CanvasHost.exe"
        Test-Path $installedExe -PathType Leaf | Should -BeTrue

        # Confirm the *installed* copy (not the publish output) reports the
        # expected identity, proving the staged copy is what got activated.
        Get-CanvasHostIdentity -VersionDirectory (Join-Path $extensionDirectory "host\$version") |
            Should -Be "$version+$sourceRevision"

        # Clean-machine scenario (issue #561 acceptance criterion): no native
        # Glasswork.exe process is running; spawn the installed host directly,
        # from an unrelated working directory, and confirm a real Task loads.
        $vaultRoot = Join-Path $TestDrive "vault"
        $todoDirectory = Join-Path $vaultRoot "wiki\todo"
        New-Item -ItemType Directory -Force -Path $todoDirectory | Out-Null
        @"
---
id: demo
title: Demo task
status: todo
priority: medium
type: task
created: 2026-09-02
---

Demo description.
"@ | Set-Content (Join-Path $todoDirectory "demo.md")

        $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $installedExe
        $startInfo.Arguments = "--session-id clean-machine --token clean-machine-token"
        $startInfo.WorkingDirectory = [System.IO.Path]::GetTempPath()
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.UseShellExecute = $false
        $startInfo.EnvironmentVariables["GLASSWORK_VAULT"] = $vaultRoot

        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        $readyUrl = $null
        try {
            $process.Start() | Out-Null
            $deadline = [DateTime]::UtcNow.AddSeconds(15)
            while ([DateTime]::UtcNow -lt $deadline -and $null -eq $readyUrl) {
                $line = $process.StandardOutput.ReadLine()
                if ($null -eq $line) { break }
                try {
                    $ready = $line | ConvertFrom-Json
                    if ($ready.ready -eq $true) { $readyUrl = $ready.url }
                }
                catch { }
            }
            $readyUrl | Should -Not -BeNullOrEmpty

            $response = Invoke-WebRequest `
                -Uri "$readyUrl/api/task?task_id=demo" `
                -Headers @{ "X-Glasswork-Canvas-Token" = "clean-machine-token" } `
                -UseBasicParsing
            $response.StatusCode | Should -Be 200
            $projection = ($response.Content | ConvertFrom-Json)
            $projection.kind | Should -Be "task"
            $projection.projection.description | Should -Be "Demo description."
        }
        finally {
            if (-not $process.HasExited) {
                $process.Kill($true)
            }
            $process.Dispose()
        }
    }
}
