param(
    [Parameter(Mandatory = $true)]
    [int]$AppProcessId,

    [Parameter(Mandatory = $true)]
    [string]$InstallExePath,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$CleanupDirectory
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-ReleaseUpdate.ps1")
$invokeParameters = @{
    AppProcessId = $AppProcessId
    InstallExePath = $InstallExePath
    Version = $Version
}
try {
    Invoke-ReleaseUpdate @invokeParameters
}
finally {
    Remove-Item -Recurse -Force $CleanupDirectory -ErrorAction SilentlyContinue
}
