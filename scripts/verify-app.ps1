<#
.SYNOPSIS
  Visual smoke-test of Glasswork: build, launch, screenshot, kill, return PNG path.

.DESCRIPTION
  Agents must run this after implementing changes that could affect the running app,
  then view the resulting PNG to confirm the change visually before declaring the
  task done. This catches XAML parse errors, layout regressions, blank screens, and
  the silent STOWED_EXCEPTION crashes (per architectural hard rule 6 in
  .github/copilot-instructions.md) that the test suite cannot.

  The script only touches the Debug-build exe under src\Glasswork.App\bin\. Any
  user-launched Glasswork (e.g. from the published install at
  %LOCALAPPDATA%\Programs\Glasswork) is left alone — only the spawned dev-build
  PID is screenshotted and killed.

.PARAMETER NoBuild
  Skip dotnet build. Use when you've just built and want a pure screenshot pass.

.PARAMETER OutPath
  Destination PNG. Defaults to $env:TEMP\Glasswork-verify-<timestamp>.png.

.PARAMETER LaunchTimeoutSec
  Seconds to wait for the dev-build window to appear (default 20).

.EXAMPLE
  pwsh -File scripts\verify-app.ps1
#>
[CmdletBinding()]
param(
    [switch]$NoBuild,
    [string]$OutPath,
    [int]$LaunchTimeoutSec = 20
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo 'src\Glasswork.App\Glasswork.csproj'
$binDir = Join-Path $repo 'src\Glasswork.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64'
$exe = Join-Path $binDir 'Glasswork.exe'

# 1. Kill any prior dev-build instance (match by exe path, not name — leaves the
#    user's installed Glasswork running).
function Stop-DevBuild {
    Get-Process -Name Glasswork -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and ($_.Path -ieq $exe) } catch { $false }
    } | ForEach-Object {
        Write-Host "Stopping prior dev-build (PID $($_.Id))..."
        Stop-Process -Id $_.Id -Force
    }
}
Stop-DevBuild
Start-Sleep 1

# 2. Build (unless caller already did).
if (-not $NoBuild) {
    Write-Host "Building Glasswork.App (Debug|x64)..." -ForegroundColor Cyan
    & dotnet build $proj -c Debug -p:Platform=x64 --nologo -v quiet -tl:off
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE). Fix compile errors before re-running." }
}
if (-not (Test-Path $exe)) { throw "Build succeeded but exe not found at $exe" }

# 3. Launch.
Write-Host "Launching $exe"
$spawned = Start-Process -FilePath $exe -PassThru
$pid_ = $spawned.Id

# 4. Wait for MainWindowHandle. WinUI 3 self-contained init is ~1-3s on warm cache,
#    longer on cold start. Poll every 250ms up to LaunchTimeoutSec.
$deadline = (Get-Date).AddSeconds($LaunchTimeoutSec)
$hwnd = 0
while ((Get-Date) -lt $deadline) {
    $p = Get-Process -Id $pid_ -ErrorAction SilentlyContinue
    if (-not $p) {
        throw "Glasswork (PID $pid_) exited before showing a window — likely a startup crash. Check Event Viewer > Application for STOWED_EXCEPTION or XamlParseException (architectural hard rule 6)."
    }
    if ($p.MainWindowHandle -ne 0) { $hwnd = $p.MainWindowHandle; break }
    Start-Sleep -Milliseconds 250
}
if ($hwnd -eq 0) {
    Stop-Process -Id $pid_ -Force -ErrorAction SilentlyContinue
    throw "Glasswork did not show a window within ${LaunchTimeoutSec}s. Either increase -LaunchTimeoutSec or investigate why startup is slow."
}
# Give the first frame a moment to compose so the screenshot isn't a blank/loading state.
Start-Sleep -Milliseconds 800

# 5. Screenshot via the existing primitive (targets the specific PID, so the user's
#    own Glasswork is never touched).
if (-not $OutPath) {
    $ts = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutPath = Join-Path $env:TEMP "Glasswork-verify-$ts.png"
}
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'screenshot-app.ps1') -ProcessId $pid_ -OutPath $OutPath | Out-Null
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $OutPath)) {
    Stop-Process -Id $pid_ -Force -ErrorAction SilentlyContinue
    throw "Screenshot capture failed."
}

# 6. Kill the spawned instance — never leave dev builds running after verification.
Stop-Process -Id $pid_ -Force -ErrorAction SilentlyContinue

# 7. Print path on last line so callers can pipe / capture it.
$resolved = (Resolve-Path -LiteralPath $OutPath).Path
Write-Host "PNG saved to: $resolved" -ForegroundColor Green
$resolved
