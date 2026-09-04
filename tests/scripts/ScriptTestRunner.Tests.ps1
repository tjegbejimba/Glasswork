BeforeAll {
    $script:RepoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $script:RunnerPath = Join-Path $script:RepoRoot "scripts\Invoke-ScriptTests.ps1"

    function Invoke-TestScriptRunner {
        param(
            [Parameter(Mandatory = $true)]
            [string]$TestPath,

            [Parameter(Mandatory = $true)]
            [string]$ResultPath
        )

        $output = & pwsh -NoProfile -File $script:RunnerPath `
            -TestPath $TestPath `
            -ResultPath $ResultPath 2>&1
        $exitCode = $LASTEXITCODE

        [pscustomobject]@{
            ExitCode = $exitCode
            Output = @($output) -join "`n"
        }
    }

    function New-RunnerFixture {
        param(
            [Parameter(Mandatory = $true)]
            [string]$Name,

            [Parameter(Mandatory = $true)]
            [string]$Content
        )

        $fixtureRoot = Join-Path $TestDrive $Name
        New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
        Set-Content -Path (Join-Path $fixtureRoot "Fixture.Tests.ps1") -Value $Content

        @{
            TestPath = $fixtureRoot
            ResultPath = Join-Path $fixtureRoot "results\script-tests.xml"
        }
    }
}

Describe "Invoke-ScriptTests.ps1" {
    It "returns zero and writes NUnit XML for a passing fixture" {
        $fixture = New-RunnerFixture -Name "passing" -Content @'
Describe "passing fixture" {
    It "passes" {
        1 | Should -Be 1
    }
}
'@
        New-Item -ItemType Directory -Force -Path (Split-Path $fixture.ResultPath -Parent) | Out-Null
        "stale result" | Set-Content $fixture.ResultPath

        $run = Invoke-TestScriptRunner @fixture

        $run.ExitCode | Should -Be 0
        $run.Output | Should -Match "Pester version: 5\.7\.1"
        $run.Output | Should -Match "Pester summary: result=Passed, total=1, passed=1"
        $fixture.ResultPath | Should -Exist
        (Get-Content $fixture.ResultPath -Raw) | Should -Not -Be "stale result"
        { [xml](Get-Content $fixture.ResultPath -Raw) } | Should -Not -Throw
    }

    It "returns nonzero and writes NUnit XML for a failed assertion" {
        $fixture = New-RunnerFixture -Name "failed-assertion" -Content @'
Describe "failed fixture" {
    It "fails" {
        1 | Should -Be 2
    }
}
'@

        $run = Invoke-TestScriptRunner @fixture

        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match "test\(s\) failed"
        $fixture.ResultPath | Should -Exist
    }

    It "returns nonzero and writes NUnit XML for a discovery failure" {
        $fixture = New-RunnerFixture -Name "discovery-failure" -Content @'
Describe "malformed fixture" {
    It "does not parse" {
        1 | Should -Be
'@

        $run = Invoke-TestScriptRunner @fixture

        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match "test container\(s\) failed"
        $fixture.ResultPath | Should -Exist
    }

    It "returns nonzero and writes NUnit XML for a BeforeAll failure" {
        $fixture = New-RunnerFixture -Name "before-all-failure" -Content @'
Describe "container fixture" {
    BeforeAll {
        throw "before-all boom"
    }

    It "does not run" {
        1 | Should -Be 1
    }
}
'@

        $run = Invoke-TestScriptRunner @fixture

        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match "test container\(s\) failed"
        $fixture.ResultPath | Should -Exist
    }

    It "returns nonzero when no tests are discovered" {
        $fixture = New-RunnerFixture -Name "zero-tests" -Content "# no tests"

        $run = Invoke-TestScriptRunner @fixture

        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match "no tests were discovered"
        $fixture.ResultPath | Should -Exist
    }

    It "returns nonzero when a test is skipped" {
        $fixture = New-RunnerFixture -Name "skipped" -Content @'
Describe "skipped fixture" {
    It "is skipped" -Skip {
        1 | Should -Be 1
    }
}
'@

        $run = Invoke-TestScriptRunner @fixture

        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match "test\(s\) were skipped"
        $fixture.ResultPath | Should -Exist
    }

    It "returns nonzero when a test is inconclusive" {
        $fixture = New-RunnerFixture -Name "inconclusive" -Content @'
Describe "inconclusive fixture" {
    It "is inconclusive" {
        Set-ItResult -Inconclusive -Because "fixture is incomplete"
    }
}
'@

        $run = Invoke-TestScriptRunner @fixture

        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match "test\(s\) were inconclusive"
        $fixture.ResultPath | Should -Exist
    }

    It "removes stale XML and returns nonzero for a missing test path" {
        $resultPath = Join-Path $TestDrive "missing\results\script-tests.xml"
        New-Item -ItemType Directory -Force -Path (Split-Path $resultPath -Parent) | Out-Null
        "stale result" | Set-Content $resultPath

        $run = Invoke-TestScriptRunner `
            -TestPath (Join-Path $TestDrive "does-not-exist") `
            -ResultPath $resultPath

        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match "does not exist"
        $resultPath | Should -Not -Exist
    }

    It "rejects a result path equal to the test file without changing it" {
        $fixtureRoot = Join-Path $TestDrive "same-file"
        New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
        $testPath = Join-Path $fixtureRoot "Fixture.Tests.ps1"
        $original = "Describe 'preserved' { It 'passes' { 1 | Should -Be 1 } }"
        Set-Content -Path $testPath -Value $original

        $run = Invoke-TestScriptRunner -TestPath $testPath -ResultPath $testPath

        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match "overlaps"
        $run.Output | Should -Match "test input"
        (Get-Content -LiteralPath $testPath -Raw).Trim() | Should -Be $original
    }

    It "rejects an existing directory result path without changing its contents" {
        $fixture = New-RunnerFixture -Name "directory-result" -Content @'
Describe "directory result fixture" {
    It "passes" {
        1 | Should -Be 1
    }
}
'@
        $resultPath = Join-Path $TestDrive "existing-result.xml"
        New-Item -ItemType Directory -Force -Path $resultPath | Out-Null
        $markerPath = Join-Path $resultPath "marker.txt"
        "preserve directory" | Set-Content $markerPath

        $run = Invoke-TestScriptRunner -TestPath $fixture.TestPath -ResultPath $resultPath

        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match "existing"
        $run.Output | Should -Match "directory"
        (Get-Content -LiteralPath $markerPath -Raw).Trim() | Should -Be "preserve directory"
    }

    It "rejects wildcard characters in a literal result name without changing it" {
        $fixture = New-RunnerFixture -Name "wildcard-result" -Content @'
Describe "wildcard result fixture" {
    It "passes" {
        1 | Should -Be 1
    }
}
'@
        $resultPath = Join-Path $TestDrive "script-tests[1].xml"
        $original = "preserve wildcard result"
        Set-Content -LiteralPath $resultPath -Value $original

        $run = Invoke-TestScriptRunner -TestPath $fixture.TestPath -ResultPath $resultPath

        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match "must not contain wildcard characters"
        (Get-Content -LiteralPath $resultPath -Raw).Trim() | Should -Be $original
    }

    It "rejects a non-XML result path without changing it" {
        $fixture = New-RunnerFixture -Name "non-xml-result" -Content @'
Describe "non-XML result fixture" {
    It "passes" {
        1 | Should -Be 1
    }
}
'@
        $resultPath = Join-Path $TestDrive "script-tests.txt"
        $original = "preserve non-XML result"
        Set-Content -LiteralPath $resultPath -Value $original

        $run = Invoke-TestScriptRunner -TestPath $fixture.TestPath -ResultPath $resultPath

        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match "must use the \.xml extension"
        (Get-Content -LiteralPath $resultPath -Raw).Trim() | Should -Be $original
    }

    It "creates the result path parent directory" {
        $fixture = New-RunnerFixture -Name "result-parent" -Content @'
Describe "result parent fixture" {
    It "passes" {
        $true | Should -BeTrue
    }
}
'@

        $fixture.ResultPath | Should -Not -Exist
        $run = Invoke-TestScriptRunner @fixture

        $run.ExitCode | Should -Be 0
        $fixture.ResultPath | Should -Exist
        (Split-Path $fixture.ResultPath -Parent) | Should -Exist
    }

    It "pins safe Pester acquisition and exact import" {
        $runner = Get-Content $script:RunnerPath -Raw

        $runner | Should -Match 'RequiredVersion \$pesterVersion'
        $runner | Should -Match 'Repository PSGallery'
        $runner | Should -Match 'Scope CurrentUser'
        $runner | Should -Match 'Import-Module Pester -RequiredVersion \$pesterVersion'
        $runner | Should -Not -Match 'Set-PSRepository'
    }
}
