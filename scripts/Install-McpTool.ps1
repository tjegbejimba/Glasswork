#Requires -Version 7.0

<#
.SYNOPSIS
    Installs an exact, verified glasswork-mcp build.
#>

. (Join-Path $PSScriptRoot "Validate-McpReleasePublication.ps1")

function Resolve-McpGitHubCliPath {
    $command = Get-Command gh -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -ne $command) {
        return $command.Source
    }

    if (-not $IsWindows) {
        return $null
    }

    $candidates = @(
        [Environment]::ExpandEnvironmentVariables("%ProgramFiles%\GitHub CLI\gh.exe"),
        [Environment]::ExpandEnvironmentVariables("%ProgramFiles(x86)%\GitHub CLI\gh.exe"),
        [Environment]::ExpandEnvironmentVariables("%LOCALAPPDATA%\Microsoft\WinGet\Links\gh.exe"),
        [Environment]::ExpandEnvironmentVariables("%LOCALAPPDATA%\Programs\GitHub CLI\gh.exe")
    )
    $candidates | Where-Object { Test-Path $_ -PathType Leaf } | Select-Object -First 1
}

function Invoke-McpGitHubApi {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Uri,

        [Parameter(Mandatory = $true)]
        [string]$ApiPath,

        [Parameter(Mandatory = $true)]
        [hashtable]$Headers
    )

    try {
        return Invoke-RestMethod -Uri $Uri -Headers $Headers
    }
    catch {
        $statusCode = if ($null -ne $_.Exception.Response) {
            [int]$_.Exception.Response.StatusCode
        }
        elseif ($null -ne $_.Exception.StatusCode) {
            [int]$_.Exception.StatusCode
        }
        if ($statusCode -ne [int][System.Net.HttpStatusCode]::Forbidden) {
            throw
        }

        $ghPath = Resolve-McpGitHubCliPath
        if ([string]::IsNullOrWhiteSpace($ghPath)) {
            throw
        }

        $stderrPath = Join-Path ([IO.Path]::GetTempPath()) "glasswork-gh-$([Guid]::NewGuid().ToString('N')).log"
        try {
            $output = @(& $ghPath api --hostname github.com $ApiPath 2> $stderrPath)
            if ($LASTEXITCODE -ne 0) {
                $details = if (Test-Path $stderrPath -PathType Leaf) {
                    (Get-Content $stderrPath -Raw).Trim()
                }
                else {
                    ""
                }
                throw "GitHub API rate limit exceeded and authenticated GitHub CLI fallback failed. $details"
            }

            try {
                return ($output -join "`n") | ConvertFrom-Json
            }
            catch {
                throw "Authenticated GitHub CLI returned invalid release metadata. $($_.Exception.Message)"
            }
        }
        finally {
            Remove-Item $stderrPath -Force -ErrorAction SilentlyContinue
        }
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
    if (-not [string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
        $headers.Authorization = "Bearer $env:GH_TOKEN"
    }
    $tagName = "mcp-v$Version"
    $releaseUrl = "https://api.github.com/repos/tjegbejimba/Glasswork/releases/tags/$tagName"
    $release = Invoke-McpGitHubApi `
        -Uri $releaseUrl `
        -ApiPath "repos/tjegbejimba/Glasswork/releases/tags/$tagName" `
        -Headers $headers
    if ($release.tag_name -ne $tagName -or $release.draft -or $release.prerelease) {
        throw "MCP GitHub Release '$tagName' is not a published stable release."
    }

    $packageName = "glasswork-mcp.$Version.nupkg"
    $checksumName = "$packageName.sha256"
    $packageAssets = @($release.assets | Where-Object { $_.name -eq $packageName })
    $checksumAssets = @($release.assets | Where-Object { $_.name -eq $checksumName })
    if ($packageAssets.Count -ne 1 -or $checksumAssets.Count -ne 1) {
        throw "MCP GitHub Release '$tagName' must contain exactly one package and checksum asset."
    }

    $refUrl = "https://api.github.com/repos/tjegbejimba/Glasswork/git/ref/tags/mcp-v$Version"
    $ref = Invoke-McpGitHubApi `
        -Uri $refUrl `
        -ApiPath "repos/tjegbejimba/Glasswork/git/ref/tags/mcp-v$Version" `
        -Headers $headers
    if ($ref.object.type -ne "tag") {
        throw "MCP publication tag '$tagName' must be annotated."
    }

    $tagUrl = "https://api.github.com/repos/tjegbejimba/Glasswork/git/tags/$($ref.object.sha)"
    $tag = Invoke-McpGitHubApi `
        -Uri $tagUrl `
        -ApiPath "repos/tjegbejimba/Glasswork/git/tags/$($ref.object.sha)" `
        -Headers $headers
    if ($tag.object.type -ne "commit") {
        throw "MCP publication tag '$tagName' does not target a commit."
    }
    $tagMetadata = ConvertFrom-McpTagMessage -Message $tag.message -Version $Version
    if ($tagMetadata.SourceRevision -ne $tag.object.sha) {
        throw "MCP integrity tag source revision does not match its target commit."
    }
    $sourceRevision = $tag.object.sha

    [pscustomobject]@{
        Version             = $Version
        SourceRevision      = $sourceRevision
        Sha256              = $tagMetadata.Sha256
        PackageDownloadUrl  = $packageAssets[0].browser_download_url
        ChecksumDownloadUrl = $checksumAssets[0].browser_download_url
    }
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
        $checksumPath = "$verifiedPackagePath.sha256"
        Invoke-WebRequest `
            -Uri $publishedMetadata.PackageDownloadUrl `
            -OutFile $verifiedPackagePath
        Invoke-WebRequest `
            -Uri $publishedMetadata.ChecksumDownloadUrl `
            -OutFile $checksumPath
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
        $checksum = (Get-Content $checksumPath -Raw).Trim()
        $expectedChecksumPattern = "^([0-9a-f]{64})  $([regex]::Escape((Split-Path $verifiedPackagePath -Leaf)))$"
        if ($checksum -notmatch $expectedChecksumPattern) {
            throw "MCP GitHub Release checksum is malformed or names the wrong package."
        }
        if ($metadata.SourceRevision -ne $publishedMetadata.SourceRevision) {
            throw "MCP package source revision does not match the publication tag."
        }
        if ($Matches[1] -ne $publishedMetadata.Sha256) {
            throw "MCP GitHub Release checksum does not match the annotated integrity tag."
        }
        if ($sha256 -ne $publishedMetadata.Sha256) {
            throw "MCP package SHA-256 does not match the GitHub Release checksum."
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

function Get-DefaultMcpInstallRoot {
    Join-Path `
        ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) `
        "Glasswork\Mcp"
}

function Get-DefaultCopilotMcpConfigPath {
    Join-Path `
        ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) `
        ".copilot\mcp-config.json"
}

function Write-McpAtomicJson {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [object]$Value,

        [string]$ExpectedContent
    )

    $directory = Split-Path $Path -Parent
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $temporaryPath = Join-Path $directory ".$(Split-Path $Path -Leaf).$([guid]::NewGuid().ToString('N')).tmp"
    try {
        $json = $Value | ConvertTo-Json -Depth 100
        [System.IO.File]::WriteAllText($temporaryPath, $json)
        if ($PSBoundParameters.ContainsKey("ExpectedContent")) {
            $currentContent = Get-Content $Path -Raw
            if ($currentContent -cne $ExpectedContent) {
                throw "MCP configuration changed while the update was being prepared. Retry the MCP update."
            }
        }
        [System.IO.File]::Move($temporaryPath, $Path, $true)
    }
    finally {
        if (Test-Path $temporaryPath) {
            Remove-Item -Force $temporaryPath
        }
    }
}

function Set-CopilotGlassworkMcpCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ConfigPath,

        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath
    )

    $configExists = Test-Path $ConfigPath -PathType Leaf
    $originalContent = if ($configExists) {
        Get-Content $ConfigPath -Raw
    }
    else {
        $null
    }
    $config = if ($configExists) {
        $originalContent | ConvertFrom-Json -AsHashtable
    }
    else {
        [ordered]@{}
    }
    if (-not $config.Contains("mcpServers")) {
        $config["mcpServers"] = [ordered]@{}
    }
    $glassworkExists = $config["mcpServers"].Contains("glasswork")
    if (-not $glassworkExists) {
        $config["mcpServers"]["glasswork"] = [ordered]@{
            tools = @("*")
            type = "stdio"
            command = $ExecutablePath
            args = @()
        }
    }

    $glasswork = $config["mcpServers"]["glasswork"]
    $previousCommand = if ($glassworkExists) {
        [string]$glasswork["command"]
    }
    else {
        $null
    }
    $glasswork["command"] = $ExecutablePath
    if ($configExists) {
        Write-McpAtomicJson `
            -Path $ConfigPath `
            -Value $config `
            -ExpectedContent $originalContent
    }
    else {
        Write-McpAtomicJson -Path $ConfigPath -Value $config
    }
    $previousCommand
}

function Get-McpSideBySideState {
    param(
        [Parameter(Mandatory = $true)]
        [string]$InstallRoot
    )

    $statePath = Join-Path $InstallRoot "current.json"
    if (-not (Test-Path $statePath -PathType Leaf)) {
        return $null
    }

    Get-Content $statePath -Raw | ConvertFrom-Json
}

function Install-McpSideBySide {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Package,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedIdentity,

        [Parameter(Mandatory = $true)]
        [string]$StagingPath,

        [Parameter(Mandatory = $true)]
        [string]$InstallRoot,

        [Parameter(Mandatory = $true)]
        [string]$McpConfigPath
    )

    $versionsRoot = Join-Path $InstallRoot "versions"
    $versionDirectory = Join-Path $versionsRoot $ExpectedIdentity
    $executablePath = Get-McpToolExecutablePath -ToolPath $versionDirectory
    $currentState = Get-McpSideBySideState -InstallRoot $InstallRoot

    New-Item -ItemType Directory -Force -Path $versionsRoot | Out-Null
    $materializeVersion = $false
    if (Test-Path $versionDirectory) {
        $existingIdentity = Get-McpExecutableIdentity -ExecutablePath $executablePath
        if ($existingIdentity -ne $ExpectedIdentity) {
            try {
                Remove-Item -Recurse -Force -ErrorAction Stop $versionDirectory
            }
            catch {
                throw "MCP version directory is damaged and could not be replaced: $versionDirectory"
            }
            $materializeVersion = $true
        }
    }
    else {
        $materializeVersion = $true
    }
    if ($materializeVersion) {
        $pendingDirectory = Join-Path $versionsRoot ".pending-$([guid]::NewGuid().ToString('N'))"
        try {
            Copy-Item $StagingPath $pendingDirectory -Recurse
            $pendingExecutable = Get-McpToolExecutablePath -ToolPath $pendingDirectory
            $pendingIdentity = Get-McpExecutableIdentity -ExecutablePath $pendingExecutable
            if ($pendingIdentity -ne $ExpectedIdentity) {
                throw "Pending MCP identity '$pendingIdentity' does not match '$ExpectedIdentity'."
            }
            Move-Item $pendingDirectory $versionDirectory
        }
        finally {
            if (Test-Path $pendingDirectory) {
                Remove-Item -Recurse -Force $pendingDirectory
            }
        }
    }

    $previousCommand = Set-CopilotGlassworkMcpCommand `
        -ConfigPath $McpConfigPath `
        -ExecutablePath $executablePath
    Write-McpAtomicJson `
        -Path (Join-Path $InstallRoot "current.json") `
        -Value ([ordered]@{
            version = $Package.Version
            identity = $ExpectedIdentity
            sourceRevision = $Package.SourceRevision
            sha256 = $Package.Sha256
            executablePath = $executablePath
        })
    $currentIdentity = if ($null -ne $currentState) {
        [string]$currentState.identity
    }
    else {
        $null
    }

    [pscustomobject]@{
        Status = if ($currentIdentity -eq $ExpectedIdentity) {
            "Current"
        }
        elseif ([string]::IsNullOrWhiteSpace($previousCommand)) {
            "Installed"
        }
        else {
            "Updated"
        }
        Version = $Package.Version
        Identity = $ExpectedIdentity
        Sha256 = $Package.Sha256
        ExecutablePath = $executablePath
    }
}

function Install-GlassworkMcp {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version,

        [string]$PackagePath,

        [string]$ToolPath,

        [string]$InstallRoot,

        [string]$McpConfigPath
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

        if ([string]::IsNullOrWhiteSpace($ToolPath)) {
            $resolvedInstallRoot = if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
                Get-DefaultMcpInstallRoot
            }
            else {
                $InstallRoot
            }
            $resolvedConfigPath = if ([string]::IsNullOrWhiteSpace($McpConfigPath)) {
                Get-DefaultCopilotMcpConfigPath
            }
            else {
                $McpConfigPath
            }

            return Install-McpSideBySide `
                -Package $package `
                -ExpectedIdentity $expectedIdentity `
                -StagingPath $stagingPath `
                -InstallRoot $resolvedInstallRoot `
                -McpConfigPath $resolvedConfigPath
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
            try {
                Remove-McpInstalledTool -ToolPath $ToolPath
            }
            catch {
                $message = $_.Exception.Message
                if ($message -match '(?is)(access to the path.+glasswork-mcp.+denied|being used by another process|sharing violation)') {
                    throw "glasswork-mcp is currently in use. Close active Copilot or agent sessions, then retry the MCP update."
                }
                throw
            }
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
