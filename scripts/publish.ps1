<#
.SYNOPSIS
    Publishes Glasswork and installs shortcuts with proper icons + AUMID.
.DESCRIPTION
    Builds a self-contained Release publish, installs to a stable location,
    and creates Start Menu + Desktop shortcuts with correct identity.
#>
param(
    [string]$InstallDir = "$env:LOCALAPPDATA\Programs\Glasswork"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path $PSScriptRoot -Parent
$AppProject = Join-Path $RepoRoot "src\Glasswork.App"

Write-Host "=== Glasswork Publish ===" -ForegroundColor Cyan

# 1. Kill any running instance
$procs = Get-Process -Name Glasswork -ErrorAction SilentlyContinue
foreach ($p in $procs) {
    Write-Host "Stopping running instance (PID $($p.Id))..."
    Stop-Process -Id $p.Id -Force
}
if ($procs) { Start-Sleep 2 }

# 2. Publish
Write-Host "Publishing Release build..."
dotnet publish $AppProject -c Release -p:Platform=x64 --self-contained -r win-x64 -o $InstallDir
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

# 3. Verify critical files
$exe = Join-Path $InstallDir "Glasswork.exe"
$ico = Join-Path $InstallDir "Assets\AppIcon.ico"
$png = Join-Path $InstallDir "Assets\AppIcon.png"

$checks = @(
    @{ Path = $exe; Label = "Executable" }
    @{ Path = $ico; Label = "ICO icon" }
    @{ Path = $png; Label = "PNG icon" }
)

$allGood = $true
foreach ($check in $checks) {
    if (Test-Path $check.Path) {
        Write-Host "  [OK] $($check.Label)" -ForegroundColor Green
    } else {
        Write-Host "  [MISSING] $($check.Label): $($check.Path)" -ForegroundColor Red
        $allGood = $false
    }
}
if (-not $allGood) { throw "Critical files missing from publish output" }

# 4. Create shortcuts
$shell = New-Object -ComObject WScript.Shell

function New-GlassworkShortcut($Path) {
    $sc = $shell.CreateShortcut($Path)
    $sc.TargetPath = $exe
    $sc.WorkingDirectory = $InstallDir
    $sc.IconLocation = "$ico,0"
    $sc.Description = "Glasswork - Task Manager backed by Obsidian"
    $sc.Save()
    Write-Host "  Created: $Path" -ForegroundColor Green
}

$startMenu = [System.Environment]::GetFolderPath('StartMenu')
$desktop = [System.Environment]::GetFolderPath('Desktop')

Write-Host "Creating shortcuts..."
New-GlassworkShortcut (Join-Path $startMenu "Programs\Glasswork.lnk")
New-GlassworkShortcut (Join-Path $desktop "Glasswork.lnk")

# 5. Summary
Write-Host ""
Write-Host "=== Publish Complete ===" -ForegroundColor Cyan
Write-Host "Install dir: $InstallDir"
Write-Host "Launch from: Start Menu, Desktop, or $exe"

# 6. Install Copilot CLI skills (skills/ -> ~/.copilot/skills/)
$skillsSrc = Join-Path $RepoRoot "skills"
if (Test-Path $skillsSrc) {
    $skillsDest = Join-Path $env:USERPROFILE ".copilot\skills"
    Write-Host ""
    Write-Host "Installing Copilot CLI skills to $skillsDest ..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $skillsDest | Out-Null

    $skillDirs = Get-ChildItem -Path $skillsSrc -Directory
    foreach ($skill in $skillDirs) {
        $destSkillDir = Join-Path $skillsDest $skill.Name
        if (Test-Path $destSkillDir) {
            Remove-Item -Recurse -Force $destSkillDir
        }
        Copy-Item -Recurse -Force -Path $skill.FullName -Destination $destSkillDir
        Write-Host "  [OK] $($skill.Name)" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "To update after code changes, run this script again." -ForegroundColor Yellow
