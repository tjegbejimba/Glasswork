<#
.SYNOPSIS
    Stages, verifies, and atomically activates the Glasswork canvas extension
    bundle (Node adapter + self-contained canvas host) into the user-scoped
    Copilot extensions directory, side by side with any prior version.
.DESCRIPTION
    See issue #561. The canvas extension is not a NuGet package like
    glasswork-mcp — it ships as a plain directory bundle
    (extension.mjs + host\<version>\* + host\active.txt + manifest.json)
    inside the App release. This module never throws from
    Install-GlassworkCanvasExtension: activation failures must never fail the
    overall app install/update (criterion: "App installation succeeds even if
    extension activation fails"). Failures are returned as a bounded result and
    recorded in current.json for a later health/retry surface (issue #562).
#>

$ErrorActionPreference = "Stop"

function Get-DefaultCanvasExtensionsRoot {
    $copilotHome = $env:COPILOT_HOME
    if (-not [string]::IsNullOrWhiteSpace($copilotHome)) {
        return Join-Path $copilotHome "extensions"
    }

    Join-Path `
        ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) `
        ".copilot\extensions"
}

function Get-CanvasExtensionHostExecutablePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$VersionDirectory
    )

    $exe = Join-Path $VersionDirectory "Glasswork.CanvasHost.exe"
    if (Test-Path $exe -PathType Leaf) {
        return $exe
    }

    Join-Path $VersionDirectory "Glasswork.CanvasHost.dll"
}

function Convert-CanvasHostProcessArgument {
    # ProcessStartInfo.ArgumentList is unavailable under Windows PowerShell 5.1
    # (.NET Framework), so arguments are built as a single quoted string here
    # instead — Invoke-ReleaseUpdate.ps1 dot-sources this file and must keep
    # working when invoked via classic powershell.exe, not just pwsh.
    param(
        [Parameter(Mandatory = $true)]
        [string]$Argument
    )

    '"{0}"' -f ($Argument -replace '"', '""')
}

function Get-CanvasHostIdentity {
    param(
        [Parameter(Mandatory = $true)]
        [string]$VersionDirectory
    )

    $hostPath = Get-CanvasExtensionHostExecutablePath -VersionDirectory $VersionDirectory
    if (-not (Test-Path $hostPath -PathType Leaf)) {
        return $null
    }

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    if ([System.IO.Path]::GetExtension($hostPath) -eq ".dll") {
        $startInfo.FileName = "dotnet"
        $startInfo.Arguments = "$(Convert-CanvasHostProcessArgument $hostPath) --version"
    }
    else {
        $startInfo.FileName = $hostPath
        $startInfo.Arguments = "--version"
    }
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.UseShellExecute = $false

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            return $null
        }
        if (-not $process.WaitForExit(15000)) {
            $process.Kill($true)
            return $null
        }
        if ($process.ExitCode -ne 0) {
            return $null
        }

        $identity = $process.StandardOutput.ReadToEnd().Trim()
        if ($identity -notmatch '^\d+\.\d+\.\d+\+(?:local|[0-9a-f]{40})$') {
            return $null
        }

        $identity
    }
    finally {
        $process.Dispose()
    }
}

function Get-CanvasExtensionBundleManifest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourcePath
    )

    $extensionFile = Join-Path $SourcePath "extension.mjs"
    if (-not (Test-Path $extensionFile -PathType Leaf)) {
        throw "Canvas extension bundle is missing extension.mjs: $extensionFile"
    }

    $manifestPath = Join-Path $SourcePath "manifest.json"
    if (-not (Test-Path $manifestPath -PathType Leaf)) {
        throw "Canvas extension bundle is missing manifest.json: $manifestPath"
    }
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    foreach ($field in @("version", "sourceRevision", "sha256")) {
        if ([string]::IsNullOrWhiteSpace($manifest.$field)) {
            throw "Canvas extension manifest.json is missing required field '$field'."
        }
    }

    $activeTxt = Join-Path $SourcePath "host\active.txt"
    if (-not (Test-Path $activeTxt -PathType Leaf)) {
        throw "Canvas extension bundle is missing host\active.txt: $activeTxt"
    }
    $bundledVersion = (Get-Content $activeTxt -Raw).Trim()
    if ($bundledVersion -ne $manifest.version) {
        throw "host\active.txt version '$bundledVersion' does not match manifest.json version '$($manifest.version)'."
    }

    $versionDirectory = Join-Path $SourcePath "host\$bundledVersion"
    if (-not (Test-Path $versionDirectory -PathType Container)) {
        throw "Canvas extension bundle is missing host\$bundledVersion."
    }
    $hostExecutable = Get-CanvasExtensionHostExecutablePath -VersionDirectory $versionDirectory
    if (-not (Test-Path $hostExecutable -PathType Leaf)) {
        throw "Canvas extension bundle is missing a canvas host executable under host\$bundledVersion."
    }

    [pscustomobject]@{
        Version           = [string]$manifest.version
        SourceRevision    = [string]$manifest.sourceRevision
        Sha256            = [string]$manifest.sha256
        VersionDirectory  = $versionDirectory
        ExtensionFilePath = $extensionFile
    }
}

function Write-CanvasExtensionAtomicFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Content
    )

    $directory = Split-Path $Path -Parent
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $temporaryPath = Join-Path $directory ".$(Split-Path $Path -Leaf).$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [System.IO.File]::WriteAllText($temporaryPath, $Content)
        [System.IO.File]::Move($temporaryPath, $Path, $true)
    }
    finally {
        if (Test-Path $temporaryPath) {
            Remove-Item -Force $temporaryPath
        }
    }
}

function Get-CanvasExtensionCurrentState {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExtensionDirectory
    )

    $statePath = Join-Path $ExtensionDirectory "current.json"
    if (-not (Test-Path $statePath -PathType Leaf)) {
        return $null
    }

    Get-Content $statePath -Raw | ConvertFrom-Json
}

function Install-GlassworkCanvasExtension {
    <#
    .SYNOPSIS
        Stages, verifies, and atomically activates one version of the
        Glasswork canvas extension bundle. Never throws — returns a bounded
        Status/Message result instead, so a caller (self-update, dev publish)
        can never have this step fail the overall app install.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourcePath,

        [string]$ExtensionsRoot,

        [string]$ExtensionName = "glasswork-task-viewer"
    )

    $resolvedRoot = if ([string]::IsNullOrWhiteSpace($ExtensionsRoot)) {
        Get-DefaultCanvasExtensionsRoot
    }
    else {
        $ExtensionsRoot
    }
    $extensionDirectory = Join-Path $resolvedRoot $ExtensionName
    $nowUtc = [DateTime]::UtcNow.ToString("o")

    try {
        $manifest = Get-CanvasExtensionBundleManifest -SourcePath $SourcePath
    }
    catch {
        return [pscustomobject]@{
            Status  = "Failed"
            Version = $null
            Message = $_.Exception.Message
        }
    }
    $expectedIdentity = "$($manifest.Version)+$($manifest.SourceRevision)"

    try {
        New-Item -ItemType Directory -Force -Path $resolvedRoot | Out-Null
    }
    catch {
        return [pscustomobject]@{
            Status  = "Failed"
            Version = $manifest.Version
            Message = "Could not create the Copilot extensions directory '$resolvedRoot': $($_.Exception.Message)"
        }
    }

    $previousState = Get-CanvasExtensionCurrentState -ExtensionDirectory $extensionDirectory
    $hostVersionsRoot = Join-Path $extensionDirectory "host"
    $targetVersionDirectory = Join-Path $hostVersionsRoot $manifest.Version
    $existingIdentity = Get-CanvasHostIdentity -VersionDirectory $targetVersionDirectory
    if ($existingIdentity -eq $expectedIdentity -and
        $null -ne $previousState -and
        [string]$previousState.identity -eq $expectedIdentity) {
        return [pscustomobject]@{
            Status   = "Current"
            Version  = $manifest.Version
            Identity = $expectedIdentity
            Message  = $null
        }
    }

    $wasInstalled = $null -ne $previousState
    $pendingVersionDirectory = Join-Path $hostVersionsRoot ".pending-$([guid]::NewGuid().ToString('N'))"
    try {
        New-Item -ItemType Directory -Force -Path $hostVersionsRoot | Out-Null
        Copy-Item -Path $manifest.VersionDirectory -Destination $pendingVersionDirectory -Recurse
        $stagedIdentity = Get-CanvasHostIdentity -VersionDirectory $pendingVersionDirectory
        if ($stagedIdentity -ne $expectedIdentity) {
            throw "Staged canvas host identity '$stagedIdentity' does not match expected '$expectedIdentity'."
        }

        if (Test-Path $targetVersionDirectory) {
            Remove-Item -Recurse -Force $targetVersionDirectory
        }
        Move-Item $pendingVersionDirectory $targetVersionDirectory

        Copy-Item -Path $manifest.ExtensionFilePath -Destination (Join-Path $extensionDirectory "extension.mjs.new") -Force
        Move-Item -Path (Join-Path $extensionDirectory "extension.mjs.new") -Destination (Join-Path $extensionDirectory "extension.mjs") -Force
        Write-CanvasExtensionAtomicFile -Path (Join-Path $hostVersionsRoot "active.txt") -Content $manifest.Version

        Write-CanvasExtensionAtomicFile `
            -Path (Join-Path $extensionDirectory "current.json") `
            -Content ([ordered]@{
                version            = $manifest.Version
                identity           = $expectedIdentity
                sourceRevision     = $manifest.SourceRevision
                sha256             = $manifest.Sha256
                hostExecutablePath = (Get-CanvasExtensionHostExecutablePath -VersionDirectory $targetVersionDirectory)
                lastAttempt        = [ordered]@{
                    utc     = $nowUtc
                    version = $manifest.Version
                    status  = "ok"
                    message = $null
                }
            } | ConvertTo-Json -Depth 10)

        [pscustomobject]@{
            Status   = if ($wasInstalled) { "Updated" } else { "Installed" }
            Version  = $manifest.Version
            Identity = $expectedIdentity
            Message  = $null
        }
    }
    catch {
        $failureMessage = $_.Exception.Message
        try {
            $fallback = if ($null -ne $previousState) {
                [ordered]@{
                    version            = [string]$previousState.version
                    identity           = [string]$previousState.identity
                    sourceRevision     = [string]$previousState.sourceRevision
                    sha256             = [string]$previousState.sha256
                    hostExecutablePath = [string]$previousState.hostExecutablePath
                }
            }
            else {
                [ordered]@{
                    version            = $null
                    identity           = $null
                    sourceRevision     = $null
                    sha256             = $null
                    hostExecutablePath = $null
                }
            }
            $fallback["lastAttempt"] = [ordered]@{
                utc     = $nowUtc
                version = $manifest.Version
                status  = "failed"
                message = $failureMessage
            }
            Write-CanvasExtensionAtomicFile `
                -Path (Join-Path $extensionDirectory "current.json") `
                -Content ($fallback | ConvertTo-Json -Depth 10)
        }
        catch {
            # Recording the failure is best-effort; the bounded result below is
            # the primary contract callers rely on.
        }

        [pscustomobject]@{
            Status   = "Failed"
            Version  = $manifest.Version
            Identity = $expectedIdentity
            Message  = $failureMessage
        }
    }
    finally {
        if (Test-Path $pendingVersionDirectory) {
            Remove-Item -Recurse -Force $pendingVersionDirectory
        }
    }
}
