#Requires -Version 7.0

<#
.SYNOPSIS
    Installs an exact, verified glasswork-mcp build.
#>

. (Join-Path $PSScriptRoot "Validate-McpReleasePublication.ps1")

function ConvertFrom-McpTagMessage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message,

        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $versionMatch = [regex]::Match($Message, '(?m)^version: (?<value>0\.\d+\.\d+)\s*$')
    $revisionMatch = [regex]::Match($Message, '(?m)^commit: (?<value>[0-9a-f]{40})\s*$')
    $shaMatch = [regex]::Match($Message, '(?m)^sha256: (?<value>[0-9a-f]{64})\s*$')
    if (-not $versionMatch.Success -or $versionMatch.Groups["value"].Value -ne $Version) {
        throw "MCP tag metadata does not match requested version '$Version'."
    }
    if (-not $revisionMatch.Success) {
        throw "MCP tag metadata is missing a valid source revision."
    }
    if (-not $shaMatch.Success) {
        throw "MCP tag metadata is missing a valid SHA-256 checksum."
    }

    [pscustomobject]@{
        Version        = $Version
        SourceRevision = $revisionMatch.Groups["value"].Value
        Sha256         = $shaMatch.Groups["value"].Value
    }
}

function Get-McpPublishedMetadata {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $headers = @{
        Accept                 = "application/vnd.github+json"
        "X-GitHub-Api-Version" = "2022-11-28"
        "User-Agent"           = "Glasswork-Mcp-Installer"
    }
    $refUrl = "https://api.github.com/repos/tjegbejimba/Glasswork/git/ref/tags/mcp-v$Version"
    $ref = Invoke-RestMethod -Uri $refUrl -Headers $headers
    if ($ref.object.type -ne "tag") {
        throw "MCP publication tag 'mcp-v$Version' must be annotated."
    }

    $tag = Invoke-RestMethod `
        -Uri "https://api.github.com/repos/tjegbejimba/Glasswork/git/tags/$($ref.object.sha)" `
        -Headers $headers
    if ($tag.object.type -ne "commit") {
        throw "MCP publication tag 'mcp-v$Version' does not target a commit."
    }

    $metadata = ConvertFrom-McpTagMessage -Message $tag.message -Version $Version
    if ($metadata.SourceRevision -ne $tag.object.sha) {
        throw "MCP tag source revision does not match its target commit."
    }

    $metadata
}

function Get-McpInstallPackage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version,

        [string]$PackagePath,

        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory
    )

    $ErrorActionPreference = "Stop"

    $feedPath = Join-Path $WorkingDirectory "feed"
    New-Item -ItemType Directory -Force -Path $feedPath | Out-Null
    $verifiedPackagePath = Join-Path $feedPath "glasswork-mcp.$Version.nupkg"

    $publishedMetadata = $null
    if ([string]::IsNullOrWhiteSpace($PackagePath)) {
        $publishedMetadata = Get-McpPublishedMetadata -Version $Version
        $packageUrl = "https://api.nuget.org/v3-flatcontainer/glasswork-mcp/$Version/glasswork-mcp.$Version.nupkg"
        Invoke-WebRequest -Uri $packageUrl -OutFile $verifiedPackagePath
    }
    else {
        if (-not (Test-Path $PackagePath -PathType Leaf)) {
            throw "MCP package not found: $PackagePath"
        }
        Copy-Item $PackagePath $verifiedPackagePath
    }

    $metadata = Get-McpPackageMetadata -PackagePath $verifiedPackagePath
    if ($metadata.Version -ne $Version) {
        throw "MCP package version '$($metadata.Version)' does not match requested version '$Version'."
    }

    $sha256 = (Get-FileHash -Algorithm SHA256 -Path $verifiedPackagePath).Hash.ToLowerInvariant()
    if ($null -ne $publishedMetadata) {
        if ($metadata.SourceRevision -ne $publishedMetadata.SourceRevision) {
            throw "MCP package source revision does not match the publication tag."
        }
        if ($sha256 -ne $publishedMetadata.Sha256) {
            throw "MCP package SHA-256 does not match the publication tag."
        }
    }

    [pscustomobject]@{
        PackagePath    = $verifiedPackagePath
        FeedPath       = $feedPath
        Version        = $metadata.Version
        SourceRevision = $metadata.SourceRevision
        Sha256         = $sha256
    }
}

function Invoke-McpDotnet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$NuGetPackagesPath
    )

    $previousNuGetPackages = $env:NUGET_PACKAGES
    $env:NUGET_PACKAGES = $NuGetPackagesPath
    try {
        $output = @(& dotnet @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0) {
            $details = ($output | Out-String).Trim()
            throw "dotnet $($Arguments -join ' ') failed with exit code $exitCode. $details"
        }
        $output | ForEach-Object { Write-Verbose $_ }
    }
    finally {
        $env:NUGET_PACKAGES = $previousNuGetPackages
    }
}

function Get-McpToolExecutablePath {
    param(
        [string]$ToolPath
    )

    $root = if ([string]::IsNullOrWhiteSpace($ToolPath)) {
        Join-Path `
            (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) ".dotnet") `
            "tools"
    }
    else {
        $ToolPath
    }
    $executableName = if ($IsWindows) { "glasswork-mcp.exe" } else { "glasswork-mcp" }
    Join-Path $root $executableName
}

function Test-McpToolInstalled {
    param(
        [string]$ToolPath
    )

    Test-Path (Get-McpToolExecutablePath -ToolPath $ToolPath) -PathType Leaf
}

function Get-McpExecutableIdentity {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath
    )

    if (-not (Test-Path $ExecutablePath -PathType Leaf)) {
        return $null
    }

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $ExecutablePath
    $startInfo.ArgumentList.Add("--version")
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.UseShellExecute = $false

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Unable to start MCP executable: $ExecutablePath"
        }
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(10000)) {
            $process.Kill($true)
            throw "Timed out reading MCP build identity from '$ExecutablePath'."
        }
        if ($process.ExitCode -ne 0) {
            return $null
        }

        $identity = $process.StandardOutput.ReadToEnd().Trim()
        if ($identity -notmatch '^0\.\d+\.\d+\+(?:local|[0-9a-f]{40})$') {
            return $null
        }

        $identity
    }
    finally {
        $process.Dispose()
    }
}

function Install-McpToolToStaging {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Package,

        [Parameter(Mandatory = $true)]
        [string]$StagingPath
    )

    $nuGetPackagesPath = Join-Path (Split-Path $StagingPath -Parent) "nuget-packages"
    Invoke-McpDotnet `
        -Arguments @(
            "tool", "install", "glasswork-mcp",
            "--tool-path", $StagingPath,
            "--version", $Package.Version,
            "--source", $Package.FeedPath,
            "--no-cache",
            "--disable-parallel") `
        -NuGetPackagesPath $nuGetPackagesPath

    Get-McpExecutableIdentity -ExecutablePath (Get-McpToolExecutablePath -ToolPath $StagingPath)
}

function Get-McpInstalledIdentity {
    param(
        [string]$ToolPath
    )

    Get-McpExecutableIdentity -ExecutablePath (Get-McpToolExecutablePath -ToolPath $ToolPath)
}

function Remove-McpInstalledTool {
    param(
        [string]$ToolPath
    )

    $arguments = @("tool", "uninstall", "glasswork-mcp")
    if ([string]::IsNullOrWhiteSpace($ToolPath)) {
        $arguments += "--global"
    }
    else {
        $arguments += @("--tool-path", $ToolPath)
    }

    Invoke-McpDotnet `
        -Arguments $arguments `
        -NuGetPackagesPath (Join-Path ([System.IO.Path]::GetTempPath()) "glasswork-mcp-uninstall")
}

function Install-McpTargetTool {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Package,

        [string]$ToolPath
    )

    $arguments = @(
        "tool", "install", "glasswork-mcp",
        "--version", $Package.Version,
        "--source", $Package.FeedPath,
        "--no-cache",
        "--disable-parallel")
    if ([string]::IsNullOrWhiteSpace($ToolPath)) {
        $arguments += "--global"
    }
    else {
        New-Item -ItemType Directory -Force -Path $ToolPath | Out-Null
        $arguments += @("--tool-path", $ToolPath)
    }

    Invoke-McpDotnet `
        -Arguments $arguments `
        -NuGetPackagesPath (Join-Path (Split-Path $Package.FeedPath -Parent) "target-nuget-packages")
}

function Install-GlassworkMcp {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version,

        [string]$PackagePath,

        [string]$ToolPath
    )

    $ErrorActionPreference = "Stop"

    if ($Version -notmatch '^0\.\d+\.\d+$') {
        throw "MCP version must be a stable 0.x semantic version in X.Y.Z form: $Version"
    }

    $workingDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "glasswork-mcp-install-$([guid]::NewGuid().ToString('N'))"
    $stagingPath = Join-Path $workingDirectory "stage"
    New-Item -ItemType Directory -Force -Path $workingDirectory | Out-Null

    try {
        $package = Get-McpInstallPackage `
            -Version $Version `
            -PackagePath $PackagePath `
            -WorkingDirectory $workingDirectory

        $expectedIdentity = "$Version+$($package.SourceRevision)"
        $stagedIdentity = Install-McpToolToStaging -Package $package -StagingPath $stagingPath
        if ($stagedIdentity -ne $expectedIdentity) {
            throw "Staged MCP identity '$stagedIdentity' does not match expected '$expectedIdentity'."
        }

        $toolInstalled = Test-McpToolInstalled -ToolPath $ToolPath
        $installedIdentity = if ($toolInstalled) {
            Get-McpInstalledIdentity -ToolPath $ToolPath
        }
        else {
            $null
        }
        if ($installedIdentity -eq $expectedIdentity) {
            return [pscustomobject]@{
                Status   = "Current"
                Version  = $Version
                Identity = $expectedIdentity
                Sha256   = $package.Sha256
            }
        }

        if ($toolInstalled) {
            Remove-McpInstalledTool -ToolPath $ToolPath
        }
        Install-McpTargetTool -Package $package -ToolPath $ToolPath

        $verifiedIdentity = Get-McpInstalledIdentity -ToolPath $ToolPath
        if ($verifiedIdentity -ne $expectedIdentity) {
            throw "Installed MCP identity '$verifiedIdentity' does not match expected '$expectedIdentity'."
        }

        [pscustomobject]@{
            Status   = if ($toolInstalled) { "Updated" } else { "Installed" }
            Version  = $Version
            Identity = $expectedIdentity
            Sha256   = $package.Sha256
        }
    }
    finally {
        if (Test-Path $workingDirectory) {
            Remove-Item -Recurse -Force $workingDirectory
        }
    }
}
