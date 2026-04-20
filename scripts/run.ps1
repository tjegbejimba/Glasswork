# Republish + launch Glasswork. This updates the *installed* app at
# %LOCALAPPDATA%\Programs\Glasswork so the Start menu / taskbar shortcuts
# also pick up the latest code — not just the dev exe in bin\.
#
# Why not just `dotnet build`? The Debug build in bin\ is a separate exe
# from the installed one. After running it, opening Glasswork from the
# Start menu would still launch the stale install. Publishing keeps both
# in sync so muscle memory works.

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

# 1. Run the canonical publish (kills running, publishes Release, refreshes shortcuts).
& "$repo\scripts\publish.ps1"
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

# 2. Launch the freshly-installed exe.
$exe = "$env:LOCALAPPDATA\Programs\Glasswork\Glasswork.exe"
if (-not (Test-Path $exe)) { throw "Installed exe not found at $exe" }
Write-Host "Launching $exe"
Start-Process $exe
