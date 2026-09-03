#Requires -Version 7.0

<#
.SYNOPSIS
    Retries Glasswork canvas extension activation (issue #562).
.DESCRIPTION
    Thin CLI wrapper the Settings "Retry" button spawns as a detached process,
    mirroring install-mcp.ps1's relationship to Install-McpTool.ps1. Dot-sources
    Install-CanvasExtension.ps1 and re-runs the same staged/verify/activate
    installer the app install/update path uses — Retry is always idempotent
    and always attempted, since (unlike App/MCP) the canvas extension has no
    separate "is an update available" check: the bundled source always matches
    the currently-installed app version.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [string]$ExtensionsRoot
)

$ErrorActionPreference = "Stop"
if ($null -ne $PSStyle) {
    $PSStyle.OutputRendering = "PlainText"
}
$env:NO_COLOR = "1"
. (Join-Path $PSScriptRoot "Install-CanvasExtension.ps1")

try {
    $result = Install-GlassworkCanvasExtension -SourcePath $SourcePath -ExtensionsRoot $ExtensionsRoot
    [ordered]@{
        status   = $result.Status
        version  = $result.Version
        identity = $result.Identity
        message  = $result.Message
    } | ConvertTo-Json -Compress
    # Explicit in both branches: this script may run in-process (via the call
    # operator, not a separate pwsh.exe) under test, where $LASTEXITCODE is
    # only set by an explicit `exit` — never leave it to fall through from a
    # previous invocation in the same session.
    if ($result.Status -eq "Failed") {
        exit 1
    }
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
