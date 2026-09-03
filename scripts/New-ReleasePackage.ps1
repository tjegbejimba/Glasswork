function New-ReleasePackage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PublishDirectory,

        [Parameter(Mandatory = $true)]
        [string]$OutputDirectory
    )

    $ErrorActionPreference = "Stop"

    $requiredFiles = @(
        "Glasswork.exe",
        "Updater\release-update.ps1",
        "Updater\Invoke-ReleaseUpdate.ps1",
        "Updater\Install-CanvasExtension.ps1",
        "McpUpdater\install-mcp.ps1",
        "McpUpdater\Install-McpTool.ps1",
        "McpUpdater\Validate-McpReleasePublication.ps1"
    )
    foreach ($requiredFile in $requiredFiles) {
        if (!(Test-Path (Join-Path $PublishDirectory $requiredFile))) {
            throw "Publish directory does not contain required file '$requiredFile': $PublishDirectory"
        }
    }

    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

    $archivePath = Join-Path $OutputDirectory "Glasswork-win-x64.zip"
    $checksumPath = "$archivePath.sha256"
    Remove-Item $archivePath, $checksumPath -Force -ErrorAction SilentlyContinue

    Compress-Archive -Path (Join-Path $PublishDirectory "*") -DestinationPath $archivePath
    $hash = (Get-FileHash -Path $archivePath -Algorithm SHA256).Hash
    Set-Content -Path $checksumPath -Value "$hash  Glasswork-win-x64.zip" -Encoding ascii

    [pscustomobject]@{
        ArchivePath = $archivePath
        ChecksumPath = $checksumPath
    }
}
