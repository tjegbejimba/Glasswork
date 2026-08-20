#Requires -Version 7.0

<#
.SYNOPSIS
    Installs an exact, verified glasswork-mcp version.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$PackagePath,

    [string]$ToolPath,

    [string]$InstallRoot,

    [string]$McpConfigPath
)

$ErrorActionPreference = "Stop"
if ($null -ne $PSStyle) {
    $PSStyle.OutputRendering = "PlainText"
}
$env:NO_COLOR = "1"
. (Join-Path $PSScriptRoot "Install-McpTool.ps1")

try {
    $result = Install-GlassworkMcp `
        -Version $Version `
        -PackagePath $PackagePath `
        -ToolPath $ToolPath `
        -InstallRoot $InstallRoot `
        -McpConfigPath $McpConfigPath

    Write-Host "glasswork-mcp $($result.Status.ToLowerInvariant()): $($result.Identity)"
    Write-Host "SHA-256: $($result.Sha256)"
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
