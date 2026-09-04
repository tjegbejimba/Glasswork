#Requires -Version 7.0

BeforeAll {
    $script:RepoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    . (Join-Path $script:RepoRoot "scripts\ReleaseAutomation.ps1")
}

Describe "Test-ReleaseScheduleGate" {
    It "opens for the active summer cron even when GitHub starts the run late" {
        Test-ReleaseScheduleGate `
            -ScheduledCron "0 16 * * 1-5" `
            -UtcNow ([datetime]"2026-09-03T19:39:00Z") |
            Should -BeTrue
    }

    It "opens for the active winter cron even when GitHub starts the run late" {
        Test-ReleaseScheduleGate `
            -ScheduledCron "0 17 * * 1-5" `
            -UtcNow ([datetime]"2026-01-05T20:12:00Z") |
            Should -BeTrue
    }

    It "rejects the inactive DST cron in summer" {
        Test-ReleaseScheduleGate `
            -ScheduledCron "0 17 * * 1-5" `
            -UtcNow ([datetime]"2026-09-03T19:39:00Z") |
            Should -BeFalse
    }

    It "rejects the inactive DST cron in winter" {
        Test-ReleaseScheduleGate `
            -ScheduledCron "0 16 * * 1-5" `
            -UtcNow ([datetime]"2026-01-05T20:12:00Z") |
            Should -BeFalse
    }
}

Describe "Get-RequiredNativeOutputLine" {
    It "preserves a complete single line from a real native command" {
        Push-Location $script:RepoRoot
        try {
            $nativeOutput = & git rev-parse HEAD
            $nativeOutput.GetType().FullName | Should -Be "System.String"

            Get-RequiredNativeOutputLine `
                -Output $nativeOutput `
                -Description "current commit" |
                Should -Match '^[0-9a-f]{40}$'
        }
        finally {
            Pop-Location
        }
    }
}

Describe "Get-LatestPublishedReleaseTag" {
    It "selects the highest published stable tag for each stream" {
        $releases = @(
            [pscustomobject]@{ tag_name = "v1.4.9"; draft = $false; prerelease = $false },
            [pscustomobject]@{ tag_name = "v1.5.0"; draft = $false; prerelease = $false },
            [pscustomobject]@{ tag_name = "mcp-v0.12.0"; draft = $false; prerelease = $false }
        )

        (Get-LatestPublishedReleaseTag -Releases $releases -Stream App).Tag |
            Should -Be "v1.5.0"
        (Get-LatestPublishedReleaseTag -Releases $releases -Stream Mcp).Tag |
            Should -Be "mcp-v0.12.0"
    }

    It "ignores newer draft prerelease and other-stream tags" {
        $releases = @(
            [pscustomobject]@{ tag_name = "v1.5.0"; draft = $false; prerelease = $false },
            [pscustomobject]@{ tag_name = "v1.6.0"; draft = $true; prerelease = $false },
            [pscustomobject]@{ tag_name = "v2.0.0"; draft = $false; prerelease = $true },
            [pscustomobject]@{ tag_name = "mcp-v0.99.0"; draft = $false; prerelease = $false }
        )

        (Get-LatestPublishedReleaseTag -Releases $releases -Stream App).Tag |
            Should -Be "v1.5.0"
    }
}

Describe "Get-ReleasePathStreams" {
    It "classifies product paths and exact shipped scripts case-insensitively" {
        $result = Get-ReleasePathStreams -Paths @(
            "SRC\GLASSWORK.CORE\Models\Task.cs",
            "src/Glasswork.App/Glasswork.csproj",
            "src/Glasswork.Mcp/README.md",
            "scripts/release-update.ps1",
            "scripts/Install-McpTool.ps1"
        )

        $result.App | Should -BeTrue
        $result.Mcp | Should -BeTrue
        ($result.IncludedPaths.App -join "|") | Should -Be (
            "scripts/Install-McpTool.ps1|scripts/release-update.ps1|" +
            "src/Glasswork.App/Glasswork.csproj|SRC/GLASSWORK.CORE/Models/Task.cs"
        )
        ($result.IncludedPaths.Mcp -join "|") | Should -Be (
            "scripts/Install-McpTool.ps1|SRC/GLASSWORK.CORE/Models/Task.cs|" +
            "src/Glasswork.Mcp/README.md"
        )
    }

    It "uses the exact release-script impact map" {
        $app = Get-ReleasePathStreams -Paths @(
            "scripts/release-update.ps1",
            "scripts/Invoke-ReleaseUpdate.ps1",
            "scripts/New-ReleasePackage.ps1"
        )
        $both = Get-ReleasePathStreams -Paths @(
            "scripts/install-mcp.ps1",
            "scripts/Install-McpTool.ps1",
            "scripts/Validate-McpReleasePublication.ps1"
        )

        $app.App | Should -BeTrue
        $app.Mcp | Should -BeFalse
        $both.App | Should -BeTrue
        $both.Mcp | Should -BeTrue
    }

    It "classifies the canvas extension and host as App-only, never MCP" {
        $result = Get-ReleasePathStreams -Paths @(
            "tools/Glasswork.CanvasHost/Program.cs",
            ".github/extensions/glasswork-task-viewer/extension.mjs",
            "scripts/Install-CanvasExtension.ps1",
            "scripts/retry-canvas-extension.ps1"
        )

        $result.App | Should -BeTrue
        $result.Mcp | Should -BeFalse
        ($result.IncludedPaths.App -join "|") | Should -Be (
            ".github/extensions/glasswork-task-viewer/extension.mjs|" +
            "scripts/Install-CanvasExtension.ps1|" +
            "scripts/retry-canvas-extension.ps1|" +
            "tools/Glasswork.CanvasHost/Program.cs"
        )
        $result.ExcludedPaths.Count | Should -Be 0
    }

    It "excludes tests docs workflows generated metadata and validation-only tooling" {
        $paths = @(
            "tests/scripts/ReleaseAutomation.Tests.ps1",
            "docs/adr/0023-independent-github-release-streams.md",
            ".github/workflows/release.yml",
            "docs/releases/v1.2.3.md",
            "scripts/Validate-ReleasePublication.ps1"
        )

        $result = Get-ReleasePathStreams -Paths $paths

        $result.App | Should -BeFalse
        $result.Mcp | Should -BeFalse
        ($result.ExcludedPaths -join "|") | Should -Be (
            ".github/workflows/release.yml|docs/adr/0023-independent-github-release-streams.md|" +
            "docs/releases/v1.2.3.md|scripts/Validate-ReleasePublication.ps1|" +
            "tests/scripts/ReleaseAutomation.Tests.ps1"
        )
    }
}

Describe "Resolve-ReleaseLabelDirective" {
    It "recognizes directives case-insensitively and defaults the bump to patch" {
        $result = Resolve-ReleaseLabelDirective -Labels @("RELEASE:MCP")

        $result.ReleaseDirective | Should -Be "Mcp"
        $result.SemVerBump | Should -Be "patch"
    }

    It "leaves an unspecified release directive empty" {
        $result = Resolve-ReleaseLabelDirective -Labels @("bug")

        $result.ReleaseDirective | Should -BeNullOrEmpty
        $result.SemVerBump | Should -Be "patch"
    }

    It "rejects conflicting release directives" {
        {
            Resolve-ReleaseLabelDirective -Labels @("release:app", "release:none")
        } | Should -Throw "*Conflicting release labels*"
    }

    It "rejects conflicting semantic-version directives" {
        {
            Resolve-ReleaseLabelDirective -Labels @("semver:minor", "SEMVER:MAJOR")
        } | Should -Throw "*Conflicting semver labels*"
    }
}

Describe "Semantic version calculations" {
    It "takes the maximum App bump across every level" {
        Get-MaximumSemVerBump -Bumps @("patch", "minor") -Stream App |
            Should -Be "minor"
        Get-MaximumSemVerBump -Bumps @("patch", "major", "minor") -Stream App |
            Should -Be "major"
    }

    It "calculates App patch minor and major versions" {
        Get-NextReleaseVersion -CurrentVersion "2.3.4" -Bump patch -Stream App |
            Should -Be "2.3.5"
        Get-NextReleaseVersion -CurrentVersion "2.3.4" -Bump minor -Stream App |
            Should -Be "2.4.0"
        Get-NextReleaseVersion -CurrentVersion "2.3.4" -Bump major -Stream App |
            Should -Be "3.0.0"
    }

    It "maps MCP major and breaking bumps to a 0.x minor" {
        Get-MaximumSemVerBump -Bumps @("patch", "major") -Stream Mcp |
            Should -Be "minor"
        Get-NextReleaseVersion -CurrentVersion "0.11.7" -Bump major -Stream Mcp |
            Should -Be "0.12.0"
        Get-NextReleaseVersion -CurrentVersion "0.11.7" -Bump breaking -Stream Mcp |
            Should -Be "0.12.0"
    }

    It "rejects unstable versions and MCP versions outside 0.x" {
        { Get-NextReleaseVersion -CurrentVersion "1.2.3-beta" -Bump patch -Stream App } |
            Should -Throw "*stable numeric semantic version*"
        { Get-NextReleaseVersion -CurrentVersion "1.2.3" -Bump patch -Stream Mcp } |
            Should -Throw "*must remain in 0.x*"
    }
}

Describe "Test-ReleaseNetDiff" {
    It "treats a reverted empty range as net zero" {
        Test-ReleaseNetDiff -NameStatusLines @() | Should -BeFalse
        Test-ReleaseNetDiff -NameStatusLines @("") | Should -BeFalse
        Test-ReleaseNetDiff -NameStatusLines $null | Should -BeFalse
    }

    It "accepts ordinary changes and renames" {
        Test-ReleaseNetDiff -NameStatusLines @("M`tREADME.md") | Should -BeTrue
        Test-ReleaseNetDiff -NameStatusLines @(
            "R100`tsrc/Glasswork.App/Old.cs`tsrc/Glasswork.App/New.cs"
        ) | Should -BeTrue
    }

    It "rejects malformed name-status entries" {
        { Test-ReleaseNetDiff -NameStatusLines @("M README.md") } |
            Should -Throw "*Malformed git name-status entry*"
        { Test-ReleaseNetDiff -NameStatusLines @("R100`told-only") } |
            Should -Throw "*Malformed git name-status entry*"
    }
}

Describe "Test-ReleaseAutomationActor" {
    It "accepts the GitHub REST and GraphQL identities for the configured App" {
        Test-ReleaseAutomationActor `
            -Login "glasswork-release-automation[bot]" `
            -AppSlug "glasswork-release-automation" |
            Should -BeTrue
        Test-ReleaseAutomationActor `
            -Login "app/glasswork-release-automation" `
            -AppSlug "glasswork-release-automation" |
            Should -BeTrue
    }

    It "rejects other bot and user identities" {
        Test-ReleaseAutomationActor `
            -Login "other-release-automation[bot]" `
            -AppSlug "glasswork-release-automation" |
            Should -BeFalse
        Test-ReleaseAutomationActor `
            -Login "glasswork-release-automation" `
            -AppSlug "glasswork-release-automation" |
            Should -BeFalse
    }
}

Describe "New-ReleasePlan" {
    BeforeAll {
        $script:PlanPullRequests = @(
            [pscustomobject]@{
                Number = 40
                Title = "Change the public contract"
                Url = "https://github.test/pull/40"
                Author = "ada"
                Labels = @("breaking", "semver:major")
            },
            [pscustomobject]@{
                Number = 12
                Title = "Add saved filters"
                Url = "https://github.test/pull/12"
                Author = "grace"
                Labels = @("enhancement")
            },
            [pscustomobject]@{
                Number = 19
                Title = "Fix task refresh"
                Url = "https://github.test/pull/19"
                Author = "linus"
                Labels = @("bug")
            },
            [pscustomobject]@{
                Number = 23
                Title = "Refresh dependencies"
                Url = "https://github.test/pull/23"
                Author = "margaret"
                Labels = @()
            },
            [pscustomobject]@{
                Number = 99
                Title = "Exclude this pull request"
                Url = "https://github.test/pull/99"
                Author = "ignored"
                Labels = @("release:none", "semver:major")
            }
        )
    }

    It "builds an eligible categorized deterministic App plan" {
        $plan = New-ReleasePlan `
            -Stream App `
            -BaseTag "v1.8.2" `
            -BaseVersion "1.8.2" `
            -CandidateSha ("a" * 40) `
            -NameStatusLines @("M`tsrc/Glasswork.App/App.xaml.cs") `
            -LabelsByPullRequest $script:PlanPullRequests

        $plan.Eligible | Should -BeTrue
        $plan.Bump | Should -Be "major"
        $plan.NextVersion | Should -Be "2.0.0"
        $plan.Notes.Breaking.Count | Should -Be 1
        $plan.Notes.Features.Count | Should -Be 1
        $plan.Notes.Fixes.Count | Should -Be 1
        $plan.Notes.Maintenance.Count | Should -Be 1
        $plan.Notes.Features[0].Text | Should -Be (
            "Add saved filters ([#12](https://github.test/pull/12)) — @grace"
        )
        ($plan.Notes.Breaking.Number -join ",") | Should -Not -Match "99"
    }

    It "keeps an empty net diff ineligible even when forced" {
        $plan = New-ReleasePlan `
            -Stream App `
            -BaseTag "v1.8.2" `
            -BaseVersion "1.8.2" `
            -CandidateSha ("b" * 40) `
            -NameStatusLines @() `
            -LabelsByPullRequest @() `
            -Force

        $plan.Eligible | Should -BeFalse
        $plan.Reason | Should -Be "NoNetChanges"
        $plan.NextVersion | Should -BeNullOrEmpty
    }

    It "keeps a native empty diff represented as null ineligible" {
        $plan = New-ReleasePlan `
            -Stream App `
            -BaseTag "v1.8.2" `
            -BaseVersion "1.8.2" `
            -CandidateSha ("b" * 40) `
            -NameStatusLines $null `
            -LabelsByPullRequest @() `
            -Force

        $plan.Eligible | Should -BeFalse
        $plan.Reason | Should -Be "NoNetChanges"
        $plan.NextVersion | Should -BeNullOrEmpty
    }

    It "allows Force to release an otherwise excluded non-empty range" {
        $plan = New-ReleasePlan `
            -Stream App `
            -BaseTag "v1.8.2" `
            -BaseVersion "1.8.2" `
            -CandidateSha ("c" * 40) `
            -NameStatusLines @("M`tdocs/design.md") `
            -LabelsByPullRequest @() `
            -Force

        $plan.Eligible | Should -BeTrue
        $plan.Reason | Should -Be "Forced"
        $plan.NextVersion | Should -Be "1.8.3"
    }

    It "allows a matching release label to force its stream" {
        $pullRequest = [pscustomobject]@{
            Number = 51
            Title = "Document MCP behavior"
            Url = "https://github.test/pull/51"
            Author = "katherine"
            Labels = @("release:mcp", "semver:minor")
        }

        $plan = New-ReleasePlan `
            -Stream Mcp `
            -BaseTag "mcp-v0.11.4" `
            -BaseVersion "0.11.4" `
            -CandidateSha ("d" * 40) `
            -NameStatusLines @("M`tdocs/mcp.md") `
            -LabelsByPullRequest @($pullRequest)

        $plan.Eligible | Should -BeTrue
        $plan.Reason | Should -Be "ReleaseDirective"
        $plan.NextVersion | Should -Be "0.12.0"
    }

    It "does not let release none force an excluded range or add notes" {
        $pullRequest = [pscustomobject]@{
            Number = 52
            Title = "Internal documentation"
            Url = "https://github.test/pull/52"
            Author = "donald"
            Labels = @("release:none", "semver:major")
        }

        $plan = New-ReleasePlan `
            -Stream App `
            -BaseTag "v1.8.2" `
            -BaseVersion "1.8.2" `
            -CandidateSha ("e" * 40) `
            -NameStatusLines @("M`tdocs/internal.md") `
            -LabelsByPullRequest @($pullRequest)

        $plan.Eligible | Should -BeFalse
        $plan.Notes.Maintenance.Count | Should -Be 0
    }

    It "rejects conflicts carried by pull-request metadata" {
        $pullRequest = [pscustomobject]@{
            Number = 53
            Title = "Ambiguous release"
            Url = "https://github.test/pull/53"
            Author = "barbara"
            Labels = @("release:app", "release:mcp")
        }

        {
            New-ReleasePlan `
                -Stream App `
                -BaseTag "v1.8.2" `
                -BaseVersion "1.8.2" `
                -CandidateSha ("f" * 40) `
                -NameStatusLines @("M`tsrc/Glasswork.App/App.xaml") `
                -LabelsByPullRequest @($pullRequest)
        } | Should -Throw "*Conflicting release labels*"
    }

    It "does not let an App-only semver conflict block the MCP stream" {
        $appOnly = [pscustomobject]@{
            Number = 64
            Title = "Change App shell"
            Url = "https://github.test/pull/64"
            Author = "ada"
            Labels = @("release:app", "semver:minor", "semver:patch")
        }

        {
            New-ReleasePlan `
                -Stream App `
                -BaseTag "v1.4.0" `
                -BaseVersion "1.4.0" `
                -CandidateSha ("6" * 40) `
                -NameStatusLines @("M`tsrc/Glasswork.App/App.xaml.cs") `
                -LabelsByPullRequest @($appOnly)
        } | Should -Throw "*Conflicting semver labels*"

        $mcpPlan = New-ReleasePlan `
            -Stream Mcp `
            -BaseTag "mcp-v0.11.4" `
            -BaseVersion "0.11.4" `
            -CandidateSha ("6" * 40) `
            -NameStatusLines @("M`tsrc/Glasswork.App/App.xaml.cs") `
            -LabelsByPullRequest @($appOnly)

        $mcpPlan.Eligible | Should -BeFalse
        $mcpPlan.Reason | Should -Be "NoIncludedPaths"
    }
}

Describe "Test-ReleasePrChangedFiles" {
    It "accepts only the complete App release-PR allowlist" {
        Test-ReleasePrChangedFiles -Stream App -Version "2.1.0" -Paths @(
            "src/Glasswork.App/Glasswork.csproj",
            "docs/releases/v2.1.0.md",
            "CHANGELOG.md"
        ) | Should -BeTrue

        Test-ReleasePrChangedFiles -Stream App -Version "2.1.0" -Paths @(
            "src/Glasswork.App/Glasswork.csproj",
            "docs/releases/v2.1.0.md"
        ) | Should -BeFalse

        Test-ReleasePrChangedFiles -Stream App -Version "2.1.0" -Paths @(
            "src/Glasswork.App/Glasswork.csproj",
            "docs/releases/v2.1.0.md",
            "CHANGELOG.md",
            "README.md"
        ) | Should -BeFalse
    }

    It "accepts only the complete MCP release-PR allowlist" {
        Test-ReleasePrChangedFiles -Stream Mcp -Version "0.12.0" -Paths @(
            "src/Glasswork.Mcp/Glasswork.Mcp.csproj",
            "src/Glasswork.Mcp/CHANGELOG.md",
            "CHANGELOG.md"
        ) | Should -BeTrue

        Test-ReleasePrChangedFiles -Stream Mcp -Version "0.12.0" -Paths @(
            "src/Glasswork.Mcp/Glasswork.Mcp.csproj",
            "CHANGELOG.md"
        ) | Should -BeFalse
    }
}

Describe "Merge-ReleasePrChangedPaths" {
    It "includes new untracked release notes with tracked release edits" {
        $paths = Merge-ReleasePrChangedPaths `
            -TrackedPaths @(
                "CHANGELOG.md",
                "src/Glasswork.App/Glasswork.csproj"
            ) `
            -UntrackedPaths @(
                "docs/releases/v1.4.12.md",
                "CHANGELOG.md"
            )

        $paths | Should -Be @(
            "CHANGELOG.md",
            "docs/releases/v1.4.12.md",
            "src/Glasswork.App/Glasswork.csproj"
        )
    }
}

Describe "Test-ReleaseProjectVersionChange" {
    It "accepts only the expected App version element replacements" {
        $base = @"
<Project>
  <Version>1.4.11</Version>
  <AssemblyVersion>1.4.11.0</AssemblyVersion>
  <FileVersion>1.4.11.0</FileVersion>
  <InformationalVersion>1.4.11</InformationalVersion>
</Project>
"@
        $expected = $base.Replace("1.4.11", "1.5.0")
        Test-ReleaseProjectVersionChange `
            -Stream App `
            -Version "1.5.0" `
            -BaseContent $base `
            -ReleaseContent $expected |
            Should -BeTrue

        Test-ReleaseProjectVersionChange `
            -Stream App `
            -Version "1.5.0" `
            -BaseContent $base `
            -ReleaseContent ($expected.Replace("</Project>", "<Target Name=`"Run`" /></Project>")) |
            Should -BeFalse
    }

    It "accepts only an MCP 0.x Version replacement" {
        $base = "<Project><Version>0.11.0</Version><Other>kept</Other></Project>"
        Test-ReleaseProjectVersionChange `
            -Stream Mcp `
            -Version "0.12.0" `
            -BaseContent $base `
            -ReleaseContent "<Project><Version>0.12.0</Version><Other>kept</Other></Project>" |
            Should -BeTrue
        Test-ReleaseProjectVersionChange `
            -Stream Mcp `
            -Version "1.0.0" `
            -BaseContent $base `
            -ReleaseContent $base |
            Should -BeFalse
    }
}

Describe "Resolve-ReleaseBlockerAction" {
    BeforeAll {
        $script:AppBlocker = [pscustomobject]@{
            Title = "[Release automation][App] Blocked"
            State = "OPEN"
            Labels = @(
                [pscustomobject]@{ Name = "release-automation-blocker" },
                [pscustomobject]@{ Name = "release:app" }
            )
        }
    }

    Describe "Test-AiReleaseNotes" {
        It "normalizes an exact safe response from text or a file" {
            $response = (
                '{"Breaking":[],"Features":[{"id":"pr:7","text":"Clearer saved-filter wording"}],' +
                '"Fixes":[{"id":"pr:8","text":"Reliable refresh behavior"}],"Maintenance":[]}'
            )
            $path = Join-Path $TestDrive "ai-notes.json"
            Set-Content -Path $path -Value $response

            $fromText = Test-AiReleaseNotes -Response $response
            $fromFile = Test-AiReleaseNotes -ResponsePath $path

            $fromText.Features[0].Text | Should -Be "Clearer saved-filter wording"
            $fromFile.Fixes[0].Id | Should -Be "pr:8"
        }

        It "rejects missing extra duplicate or non-array keys" {
            {
                Test-AiReleaseNotes -Response (
                    '{"Breaking":[],"Features":[],"Fixes":[],"Extra":[]}'
                )
            } | Should -Throw "*exactly the keys*"
            {
                Test-AiReleaseNotes -Response (
                    '{"Breaking":[],"Breaking":[],"Features":[],"Fixes":[],"Maintenance":[]}'
                )
            } | Should -Throw "*duplicate key*"
            {
                Test-AiReleaseNotes -Response (
                    '{"Breaking":[],"Features":"text","Fixes":[],"Maintenance":[]}'
                )
            } | Should -Throw "*id/text objects*"
        }

        It "rejects empty oversized HTML and control-character prose" {
            $longText = "x" * 501
            {
                Test-AiReleaseNotes -Response (
                    '{"Breaking":[],"Features":[{"id":"pr:1","text":" "}],"Fixes":[],"Maintenance":[]}'
                )
            } | Should -Throw "*empty strings*"
            {
                Test-AiReleaseNotes -Response (
                    '{"Breaking":[],"Features":[{"id":"pr:1","text":"<b>unsafe</b>"}],"Fixes":[],"Maintenance":[]}'
                )
            } | Should -Throw "*HTML*"
            {
                Test-AiReleaseNotes -Response (
                    '{"Breaking":[],"Features":[],"Fixes":[],"Maintenance":[{"id":"pr:1","text":"' +
                    $longText + '"}]}'
                )
            } | Should -Throw "*500 characters*"
            {
                Test-AiReleaseNotes -Response (
                    '{"Breaking":[],"Features":[],"Fixes":[{"id":"pr:1","text":"line\nbreak"}],"Maintenance":[]}'
                )
            } | Should -Throw "*control characters*"
        }

        It "rejects links author handles and code from AI prose" {
            {
                Test-AiReleaseNotes -Response (
                    '{"Breaking":[],"Features":[{"id":"pr:1","text":"See [PR](https://example.test)"}],' +
                    '"Fixes":[],"Maintenance":[]}'
                )
            } | Should -Throw "*prose only*"
            {
                Test-AiReleaseNotes -Response (
                    '{"Breaking":[],"Features":[],"Fixes":[{"id":"pr:1","text":"Thanks @author"}],' +
                    '"Maintenance":[]}'
                )
            } | Should -Throw "*prose only*"
            {
                Test-AiReleaseNotes -Response (
                    '{"Breaking":[],"Features":[],"Fixes":[],' +
                    '"Maintenance":[{"id":"pr:1","text":"Run `command`"}]}'
                )
            } | Should -Throw "*prose only*"
        }
    }

    Describe "ConvertTo-ReleaseNotesMarkdown" {
        BeforeAll {
            $script:NotesPullRequests = @(
                [pscustomobject]@{
                    Number = 7
                    Title = "Add quick capture"
                    Url = "https://github.test/pull/7"
                    Author = "ada"
                    Labels = @("feature", "semver:minor")
                },
                [pscustomobject]@{
                    Number = 8
                    Title = "Fix capture focus"
                    Url = "https://github.test/pull/8"
                    Author = "grace"
                    Labels = @("bug")
                }
            )
            $script:AppNotesPlan = New-ReleasePlan `
                -Stream App `
                -BaseTag "v1.4.0" `
                -BaseVersion "1.4.0" `
                -CandidateSha ("1" * 40) `
                -NameStatusLines @("M`tsrc/Glasswork.App/MainWindow.xaml") `
                -LabelsByPullRequest $script:NotesPullRequests
            $script:McpNotesPlan = New-ReleasePlan `
                -Stream Mcp `
                -BaseTag "mcp-v0.11.0" `
                -BaseVersion "0.11.0" `
                -CandidateSha ("2" * 40) `
                -NameStatusLines @("M`tsrc/Glasswork.Mcp/Program.cs") `
                -LabelsByPullRequest $script:NotesPullRequests
        }

        It "renders categorized App release notes and a root changelog fragment" {
            $result = ConvertTo-ReleaseNotesMarkdown -Plan $script:AppNotesPlan

            $result.AppReleaseNotes | Should -Match '^# Glasswork v1\.5\.0'
            $result.AppReleaseNotes | Should -Match '### Features'
            $result.AppReleaseNotes | Should -Match '### Fixes'
            $result.AppReleaseNotes | Should -Match '\[#7\]\(https://github\.test/pull/7\)'
            $result.AppReleaseNotes | Should -Match '@ada'
            $result.RootChangelogFragment | Should -Match '^## App v1\.5\.0'
        }

        It "uses valid count-matched AI prose while retaining PR identity" {
            $aiNotes = Test-AiReleaseNotes -Response (
                '{"Breaking":[],' +
                '"Features":[{"id":"pr:7","text":"Capture work without leaving the current page"}],' +
                '"Fixes":[{"id":"pr:8","text":"Keep keyboard focus stable after capture"}],' +
                '"Maintenance":[]}'
            )

            $result = ConvertTo-ReleaseNotesMarkdown `
                -Plan $script:AppNotesPlan `
                -AiNotes $aiNotes

            $result.AppReleaseNotes |
                Should -Match 'Capture work without leaving the current page'
            $result.AppReleaseNotes |
                Should -Match '\[#7\]\(https://github\.test/pull/7\).*@ada'
            $result.AppReleaseNotes | Should -Not -Match 'Add quick capture'
        }

        It "uses AI prose for direct commits while retaining commit identity" {
            $commitSha = "a" * 40
            $plan = $script:AppNotesPlan.PSObject.Copy()
            $plan.Notes = [pscustomobject]@{
                Breaking = @()
                Features = @()
                Fixes = @()
                Maintenance = @(
                    [pscustomobject]@{
                        Id = "commit:$commitSha"
                        Category = "Maintenance"
                        Number = $null
                        Title = "Refresh release metadata"
                        Url = "https://github.test/commit/$commitSha"
                        Author = "ada"
                        Text = "Refresh release metadata ([commit aaaaaaa](https://github.test/commit/$commitSha)) — ada"
                    }
                )
            }
            $aiNotes = Test-AiReleaseNotes -Response (
                '{"Breaking":[],"Features":[],"Fixes":[],' +
                '"Maintenance":[{"id":"commit:' + $commitSha +
                '","text":"Refresh generated release metadata"}]}'
            )

            $result = ConvertTo-ReleaseNotesMarkdown -Plan $plan -AiNotes $aiNotes

            $result.AppReleaseNotes | Should -Match 'Refresh generated release metadata'
            $result.AppReleaseNotes |
                Should -Match '\[commit aaaaaaa\]\(https://github\.test/commit/'
            $result.AppReleaseNotes | Should -Match '— ada'
            $result.AppReleaseNotes | Should -Not -Match '\[#\]'
        }

        It "falls back deterministically when AI content is invalid or counts differ" {
            $invalid = [pscustomobject]@{
                Breaking = @()
                Features = @([pscustomobject]@{ Id = "pr:7"; Text = "<script>unsafe</script>" })
                Fixes = @([pscustomobject]@{ Id = "pr:8"; Text = "Reworded fix" })
                Maintenance = @()
            }
            $wrongCounts = [pscustomobject]@{
                Breaking = @()
                Features = @()
                Fixes = @([pscustomobject]@{ Id = "pr:8"; Text = "Reworded fix" })
                Maintenance = @()
            }
            $wrongMapping = [pscustomobject]@{
                Breaking = @()
                Features = @([pscustomobject]@{ Id = "pr:8"; Text = "Wrong PR" })
                Fixes = @([pscustomobject]@{ Id = "pr:7"; Text = "Wrong PR" })
                Maintenance = @()
            }

            $invalidResult = ConvertTo-ReleaseNotesMarkdown `
                -Plan $script:AppNotesPlan `
                -AiNotes $invalid
            $wrongCountResult = ConvertTo-ReleaseNotesMarkdown `
                -Plan $script:AppNotesPlan `
                -AiNotes $wrongCounts
            $wrongMappingResult = ConvertTo-ReleaseNotesMarkdown `
                -Plan $script:AppNotesPlan `
                -AiNotes $wrongMapping

            $invalidResult.AppReleaseNotes | Should -Match 'Add quick capture'
            $wrongCountResult.AppReleaseNotes | Should -Match 'Add quick capture'
            $wrongMappingResult.AppReleaseNotes | Should -Match 'Add quick capture'
        }

        It "renders MCP and root changelog fragments with deterministic categories" {
            $result = ConvertTo-ReleaseNotesMarkdown -Plan $script:McpNotesPlan

            $result.McpChangelogFragment | Should -Match '^## \[0\.12\.0\] — \d{4}-\d{2}-\d{2}'
            $result.McpChangelogFragment | Should -Match '### Features'
            $result.RootChangelogFragment | Should -Match '^## MCP v0\.12\.0'
            $result.Markdown | Should -Be $result.McpChangelogFragment
        }
    }

    It "creates or updates one App blocker on failure" {
        Resolve-ReleaseBlockerAction `
            -ExistingIssues @() `
            -Stream App `
            -HasFailure $true |
            Should -Be "Create"

        Resolve-ReleaseBlockerAction `
            -ExistingIssues @($script:AppBlocker) `
            -Stream App `
            -HasFailure $true |
            Should -Be "Update"
    }

    It "creates the first App blocker when issue lookup returns no output" {
        Resolve-ReleaseBlockerAction `
            -ExistingIssues $null `
            -Stream App `
            -HasFailure $true |
            Should -Be "Create"
    }

    It "closes one blocker after recovery and otherwise does nothing" {
        Resolve-ReleaseBlockerAction `
            -ExistingIssues @($script:AppBlocker) `
            -Stream App `
            -HasFailure $false |
            Should -Be "Close"

        Resolve-ReleaseBlockerAction `
            -ExistingIssues @() `
            -Stream App `
            -HasFailure $false |
            Should -Be "None"
    }

    It "closes a blocker only when the recovering workflow stage matches" {
        $publicationBlocker = $script:AppBlocker.PSObject.Copy()
        $publicationBlocker | Add-Member -NotePropertyName Body -NotePropertyValue (
            "<!-- release-automation-blocker-stage:Publication -->")

        Resolve-ReleaseBlockerAction `
            -ExistingIssues @($publicationBlocker) `
            -Stream App `
            -Stage Evaluation `
            -HasFailure $false |
            Should -Be "None"

        Resolve-ReleaseBlockerAction `
            -ExistingIssues @($publicationBlocker) `
            -Stream App `
            -Stage Publication `
            -HasFailure $false |
            Should -Be "Close"
    }

    It "preserves a publication blocker from evaluator failures and closes it on publication recovery" {
        $publicationBlocker = $script:AppBlocker.PSObject.Copy()
        $publicationBlocker | Add-Member -NotePropertyName Body -NotePropertyValue (
            "<!-- release-automation-blocker-stage:Publication -->")

        Resolve-ReleaseBlockerAction `
            -ExistingIssues @($publicationBlocker) `
            -Stream App `
            -Stage Evaluation `
            -HasFailure $true |
            Should -Be "None"

        Resolve-ReleaseBlockerAction `
            -ExistingIssues @($publicationBlocker) `
            -Stream App `
            -Stage Publication `
            -HasFailure $false |
            Should -Be "Close"
    }

    It "ignores closed or incorrectly labeled issues" {
        $closed = $script:AppBlocker.PSObject.Copy()
        $closed.State = "CLOSED"
        $wrongLabels = $script:AppBlocker.PSObject.Copy()
        $wrongLabels.Labels = @("release-automation-blocker", "release:mcp")

        Resolve-ReleaseBlockerAction `
            -ExistingIssues @($closed, $wrongLabels) `
            -Stream App `
            -HasFailure $true |
            Should -Be "Create"
    }

    It "fails closed when duplicate open blockers exist" {
        {
            Resolve-ReleaseBlockerAction `
                -ExistingIssues @($script:AppBlocker, $script:AppBlocker.PSObject.Copy()) `
                -Stream App `
                -HasFailure $true
        } | Should -Throw "*More than one matching open release blocker*"
    }
}
