<#
.SYNOPSIS
    Parses every PowerShell run block in release automation workflows.
#>

Describe "Release automation workflow syntax" {
    BeforeAll {
        $script:RepoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $script:WorkflowPaths = @(
            ".github\workflows\evaluate-releases.yml",
            ".github\workflows\release-publication.yml",
            ".github\workflows\release.yml",
            ".github\workflows\publish-mcp.yml"
        ) | ForEach-Object { Join-Path $script:RepoRoot $_ }
    }

    It "contains syntactically valid PowerShell run blocks" {
        foreach ($workflowPath in $script:WorkflowPaths) {
            $lines = Get-Content $workflowPath
            for ($index = 0; $index -lt $lines.Count; $index++) {
                if ($lines[$index] -notmatch '^(\s+)run: \|$') {
                    continue
                }

                $runIndent = $Matches[1].Length
                $contentIndent = $runIndent + 2
                $scriptLines = [System.Collections.Generic.List[string]]::new()
                for ($cursor = $index + 1; $cursor -lt $lines.Count; $cursor++) {
                    $line = $lines[$cursor]
                    if (-not [string]::IsNullOrWhiteSpace($line) -and
                        ($line.Length - $line.TrimStart().Length) -le $runIndent) {
                        break
                    }
                    $scriptLine = if ($line.Length -ge $contentIndent) {
                        $line.Substring($contentIndent)
                    }
                    else {
                        ""
                    }
                    $scriptLines.Add($scriptLine)
                }

                $tokens = $null
                $parseErrors = $null
                [System.Management.Automation.Language.Parser]::ParseInput(
                    ($scriptLines -join "`n"),
                    [ref]$tokens,
                    [ref]$parseErrors) | Out-Null
                $parseErrors | Should -BeNullOrEmpty -Because $workflowPath
            }
        }
    }
}
