#Requires -Version 7.0

<#
.SYNOPSIS
    Installs an exact, verified glasswork-mcp version.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$PackagePath,

    [string]$ToolPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Install-McpTool.ps1")

$result = Install-GlassworkMcp `
    -Version $Version `
    -PackagePath $PackagePath `
    -ToolPath $ToolPath

Write-Host "glasswork-mcp $($result.Status.ToLowerInvariant()): $($result.Identity)"
Write-Host "SHA-256: $($result.Sha256)"
