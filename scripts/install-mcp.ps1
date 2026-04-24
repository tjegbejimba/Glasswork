<#
.SYNOPSIS
    Packs the glasswork-mcp .NET global tool and installs it globally.
.DESCRIPTION
    Runs `dotnet pack` on the Glasswork.Mcp project, then installs (or updates)
    the resulting package as a global .NET tool named `glasswork-mcp`.
#>
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path $PSScriptRoot -Parent
$McpProject = Join-Path $RepoRoot "src\Glasswork.Mcp"
$NupkgDir = Join-Path $RepoRoot "nupkg"

Write-Host "=== glasswork-mcp install ===" -ForegroundColor Cyan

# 1. Clean previous nupkg
if (Test-Path $NupkgDir) {
    Remove-Item -Recurse -Force $NupkgDir
}
New-Item -ItemType Directory -Force -Path $NupkgDir | Out-Null

# 2. Pack
Write-Host "Packing $McpProject ($Configuration)..."
dotnet pack $McpProject -c $Configuration -o $NupkgDir
if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed" }

# 3. Install (or update) as a global tool
$toolName = "glasswork-mcp"
$installed = dotnet tool list -g 2>$null | Select-String $toolName

if ($installed) {
    Write-Host "Updating existing installation of $toolName..."
    dotnet tool update -g $toolName --add-source $NupkgDir
} else {
    Write-Host "Installing $toolName globally..."
    dotnet tool install -g $toolName --add-source $NupkgDir
}

if ($LASTEXITCODE -ne 0) { throw "dotnet tool install/update failed" }

Write-Host ""
Write-Host "=== Done ===" -ForegroundColor Cyan
Write-Host "Run 'glasswork-mcp' to verify the installation."
Write-Host "Set GLASSWORK_VAULT to your vault path, or open the Glasswork app to configure it."
