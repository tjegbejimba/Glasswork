<#
.SYNOPSIS
    Tests for Repo Path stamping in publish.ps1
#>

BeforeAll {
    # Import the function we'll extract from publish.ps1
    $scriptRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    . (Join-Path $scriptRoot "scripts\Stamp-RepoPathToUiState.ps1")
}

Describe "Stamp-RepoPathToUiState" {
    BeforeEach {
        $TestPath = Join-Path $TestDrive "ui-state.json"
    }

    Context "When ui-state.json does not exist" {
        It "Creates file with repo path key" {
            $repoPath = "C:\test\repo"
            Stamp-RepoPathToUiState -UiStateFilePath $TestPath -RepoPath $repoPath

            $TestPath | Should -Exist
            $content = Get-Content $TestPath -Raw | ConvertFrom-Json
            $content."app.repoPath" | Should -Be $repoPath
        }

        It "Creates valid JSON structure" {
            $repoPath = "C:\test\repo"
            Stamp-RepoPathToUiState -UiStateFilePath $TestPath -RepoPath $repoPath

            { Get-Content $TestPath -Raw | ConvertFrom-Json } | Should -Not -Throw
        }
    }

    Context "When ui-state.json exists with other keys" {
        It "Preserves existing keys" {
            $existingContent = @{
                "vault.path" = "C:\vault"
                "app.theme" = "dark"
                "backlog.viewMode" = "list"
            }
            $existingContent | ConvertTo-Json | Set-Content $TestPath

            $repoPath = "C:\new\repo"
            Stamp-RepoPathToUiState -UiStateFilePath $TestPath -RepoPath $repoPath

            $content = Get-Content $TestPath -Raw | ConvertFrom-Json
            $content."vault.path" | Should -Be "C:\vault"
            $content."app.theme" | Should -Be "dark"
            $content."backlog.viewMode" | Should -Be "list"
            $content."app.repoPath" | Should -Be $repoPath
        }

        It "Updates existing repo path key" {
            $existingContent = @{
                "app.repoPath" = "C:\old\repo"
                "vault.path" = "C:\vault"
            }
            $existingContent | ConvertTo-Json | Set-Content $TestPath

            $newRepoPath = "C:\new\repo"
            Stamp-RepoPathToUiState -UiStateFilePath $TestPath -RepoPath $newRepoPath

            $content = Get-Content $TestPath -Raw | ConvertFrom-Json
            $content."app.repoPath" | Should -Be $newRepoPath
            $content."vault.path" | Should -Be "C:\vault"
        }
    }

    Context "When ui-state.json exists but is empty" {
        It "Creates valid JSON with repo path" {
            "" | Set-Content $TestPath

            $repoPath = "C:\test\repo"
            Stamp-RepoPathToUiState -UiStateFilePath $TestPath -RepoPath $repoPath

            $content = Get-Content $TestPath -Raw | ConvertFrom-Json
            $content."app.repoPath" | Should -Be $repoPath
        }
    }

    Context "JSON formatting" {
        It "Writes indented JSON matching JsonFileUiStateService format" {
            $repoPath = "C:\test\repo"
            Stamp-RepoPathToUiState -UiStateFilePath $TestPath -RepoPath $repoPath

            $content = Get-Content $TestPath -Raw
            $content | Should -Match '{\s+'
            $content | Should -Match '\s+}'
        }
    }
}
