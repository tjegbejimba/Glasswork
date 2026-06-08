<#
.SYNOPSIS
    Self-update script for Glasswork.
.DESCRIPTION
    Thin wrapper that dot-sources Invoke-SelfUpdate.ps1 and calls the function.
    Spawned detached by the app's "Restart to update" button.
#>

param(
    [Parameter(Mandatory = $true)]
    [int]$AppProcessId,

    [Parameter(Mandatory = $true)]
    [string]$RepoPath,

    [Parameter(Mandatory = $true)]
    [string]$InstallExePath
)

$ErrorActionPreference = "Stop"

# Dot-source the update function
. (Join-Path $PSScriptRoot "Invoke-SelfUpdate.ps1")

# Call it with all bound parameters
Invoke-SelfUpdate @PSBoundParameters
