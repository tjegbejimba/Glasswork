<#
.SYNOPSIS
    Stamps the repository path into the Glasswork UI state file.
.DESCRIPTION
    Merges the repository path into %LocalAppData%\Glasswork\ui-state.json,
    preserving all existing keys. Creates the file if it doesn't exist.
#>

function Stamp-RepoPathToUiState {
    param(
        [Parameter(Mandatory = $true)]
        [string]$UiStateFilePath,

        [Parameter(Mandatory = $true)]
        [string]$RepoPath
    )

    $ErrorActionPreference = "Stop"

    # Ensure directory exists
    $dir = Split-Path $UiStateFilePath -Parent
    if ($dir -and !(Test-Path $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }

    # Load existing state or start fresh
    $state = @{}
    if (Test-Path $UiStateFilePath) {
        try {
            $json = Get-Content $UiStateFilePath -Raw -ErrorAction SilentlyContinue
            if (![string]::IsNullOrWhiteSpace($json)) {
                $parsed = $json | ConvertFrom-Json
                # Convert PSCustomObject to hashtable to preserve all keys
                $parsed.PSObject.Properties | ForEach-Object {
                    $state[$_.Name] = $_.Value
                }
            }
        }
        catch {
            # Corrupt file - start fresh rather than fail
            $state = @{}
        }
    }

    # Set/update the repo path key
    $state["app.repoPath"] = $RepoPath

    # Write back with indentation matching JsonFileUiStateService
    $state | ConvertTo-Json -Depth 10 | Set-Content $UiStateFilePath -Encoding UTF8
}
