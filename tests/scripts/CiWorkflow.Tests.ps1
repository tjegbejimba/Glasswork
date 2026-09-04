<#
.SYNOPSIS
    Locks the CI workflow's failure propagation and required-check contracts.
#>

Describe "CI workflow" {
    BeforeAll {
        $script:RepoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $script:WorkflowPath = Join-Path $script:RepoRoot ".github\workflows\ci.yml"
        $script:Workflow = Get-Content $script:WorkflowPath -Raw
        $script:WorkflowLines = Get-Content $script:WorkflowPath

        function Get-CiJobBlock {
            param([Parameter(Mandatory)][string] $Name)

            $start = -1
            for ($index = 0; $index -lt $script:WorkflowLines.Count; $index++) {
                if ($script:WorkflowLines[$index] -eq "  ${Name}:") {
                    $start = $index
                    break
                }
            }

            if ($start -lt 0) {
                throw "Job '$Name' was not found."
            }

            $end = $script:WorkflowLines.Count
            for ($index = $start + 1; $index -lt $script:WorkflowLines.Count; $index++) {
                if ($script:WorkflowLines[$index] -match '^  [a-zA-Z0-9_-]+:$') {
                    $end = $index
                    break
                }
            }

            return @($script:WorkflowLines[$start..($end - 1)])
        }

        function Get-CiSteps {
            param([AllowEmptyString()][string[]] $JobBlock)

            $steps = [System.Collections.Generic.List[object]]::new()
            $currentName = $null
            $currentLines = [System.Collections.Generic.List[string]]::new()

            foreach ($line in $JobBlock) {
                if ($line -match '^      - name: (.+)$') {
                    if ($null -ne $currentName) {
                        $steps.Add([pscustomobject]@{
                            Name  = $currentName
                            Lines = @($currentLines)
                        })
                    }

                    $currentName = $Matches[1]
                    $currentLines = [System.Collections.Generic.List[string]]::new()
                }
                elseif ($null -ne $currentName) {
                    $currentLines.Add($line)
                }
            }

            if ($null -ne $currentName) {
                $steps.Add([pscustomobject]@{
                    Name  = $currentName
                    Lines = @($currentLines)
                })
            }

            return @($steps)
        }

        function Get-CiRunBody {
            param([Parameter(Mandatory)] $Step)

            for ($index = 0; $index -lt $Step.Lines.Count; $index++) {
                $line = $Step.Lines[$index]
                if ($line -match '^        run: \|$') {
                    $body = [System.Collections.Generic.List[string]]::new()
                    for ($cursor = $index + 1; $cursor -lt $Step.Lines.Count; $cursor++) {
                        $bodyLine = $Step.Lines[$cursor]
                        if (-not [string]::IsNullOrWhiteSpace($bodyLine) -and
                            ($bodyLine.Length - $bodyLine.TrimStart().Length) -lt 10) {
                            break
                        }

                        $body.Add($(if ($bodyLine.Length -ge 10) {
                            $bodyLine.Substring(10)
                        }
                        else {
                            ""
                        }))
                    }

                    return $body -join "`n"
                }

                if ($line -match '^        run: (.+)$') {
                    return $Matches[1]
                }
            }

            return $null
        }

        function Get-CiStep {
            param(
                [Parameter(Mandatory)][object[]] $Steps,
                [Parameter(Mandatory)][string] $Name
            )

            $matches = @($Steps | Where-Object { $_.Name -eq $Name })
            $matches.Count | Should -Be 1 -Because "step '$Name' must be unique"
            return $matches[0]
        }

        function Invoke-PowerShellRunBody {
            param(
                [Parameter(Mandatory)][string] $Body,
                [Parameter(Mandatory)][string] $Directory
            )

            $scriptPath = Join-Path $Directory "$([guid]::NewGuid()).ps1"
            Set-Content -Path $scriptPath -Value $Body
            & pwsh -NoProfile -File $scriptPath
            return $LASTEXITCODE
        }

        function Invoke-BashRunBody {
            param(
                [Parameter(Mandatory)][string] $Body,
                [Parameter(Mandatory)][string] $Directory
            )

            $scriptPath = Join-Path $Directory "$([guid]::NewGuid()).sh"
            Set-Content -Path $scriptPath -Value "dotnet() { return 23; }`n$Body"
            & bash --noprofile --norc -eo pipefail $scriptPath
            return $LASTEXITCODE
        }

        $script:WindowsJob = Get-CiJobBlock -Name "ci"
        $script:LinuxJob = Get-CiJobBlock -Name "linux-tests"
        $script:WindowsSteps = Get-CiSteps -JobBlock $script:WindowsJob
        $script:LinuxSteps = Get-CiSteps -JobBlock $script:LinuxJob
    }

    It "preserves the protected Windows check and bounds both jobs" {
        $script:Workflow | Should -Match '(?m)^name: CI\r?$'
        ($script:WindowsJob -join "`n") | Should -Match '(?m)^  ci:$'
        ($script:WindowsJob -join "`n") | Should -Match '(?m)^    runs-on: windows-latest$'
        ($script:WindowsJob -join "`n") | Should -Match '(?m)^    timeout-minutes: 30$'
        ($script:WindowsJob -join "`n") | Should -Match '(?ms)^    permissions:\r?\n      contents: read$'

        ($script:LinuxJob -join "`n") | Should -Match '(?m)^    name: Linux tests$'
        ($script:LinuxJob -join "`n") | Should -Match '(?m)^    runs-on: ubuntu-latest$'
        ($script:LinuxJob -join "`n") | Should -Match '(?m)^    timeout-minutes: 15$'
        ($script:LinuxJob -join "`n") | Should -Match '(?ms)^    permissions:\r?\n      contents: read$'
        ($script:LinuxJob -join "`n") | Should -Not -Match '(?m)^    needs:'
    }

    It "cancels only superseded pull request runs" {
        $script:Workflow | Should -Match ([regex]::Escape(
            'group: ci-${{ github.event_name == ''pull_request'' && github.event.pull_request.number || github.run_id }}'))
        $script:Workflow | Should -Match ([regex]::Escape(
            'cancel-in-progress: ${{ github.event_name == ''pull_request'' }}'))
        $script:Workflow | Should -Not -Match 'group: .*\bgithub\.ref\b'
    }

    It "restores every Windows project graph before no-restore linting" {
        $expectedRestores = @(
            'dotnet restore src\Glasswork.App\Glasswork.csproj',
            'dotnet restore tests\Glasswork.Tests\Glasswork.Tests.csproj',
            'dotnet restore tests\Glasswork.Mcp.Tests\Glasswork.Mcp.Tests.csproj',
            'dotnet restore tests\Glasswork.CanvasHost.Tests\Glasswork.CanvasHost.Tests.csproj'
        )

        foreach ($command in $expectedRestores) {
            $script:Workflow | Should -Match ([regex]::Escape($command))
        }

        $lintSteps = @($script:WindowsSteps | Where-Object { $_.Name -like "Lint *" })
        $lintSteps.Count | Should -Be 7
        foreach ($step in $lintSteps) {
            (Get-CiRunBody -Step $step) | Should -Match '\bdotnet format\b.*\s--no-restore(?:\s|$)'
        }
    }

    It "uses one explicitly checked native producer per Windows validation step" {
        $producerSteps = @($script:WindowsSteps | Where-Object {
            (Get-CiRunBody -Step $_) -match '(?m)^\s*(dotnet|pwsh)\s'
        })

        $producerSteps.Count | Should -BeGreaterThan 0
        foreach ($step in $producerSteps) {
            $body = Get-CiRunBody -Step $step
            @([regex]::Matches($body, '(?m)^\s*(dotnet|pwsh)\s')).Count | Should -Be 1 -Because $step.Name
            $body | Should -Match '(?m)^if \(\$LASTEXITCODE -ne 0\) \{ exit \$LASTEXITCODE \}$'
            $body | Should -Not -Match '(?m)^\s*exit 0\s*$'
            $body | Should -Not -Match '\|\|'
            $body | Should -Not -Match '(?m)^\s*(try|catch)\b'
            ($step.Lines -join "`n") | Should -Not -Match '(?m)^        continue-on-error:'
            ($step.Lines -join "`n") | Should -Not -Match '(?m)^        if: always\(\)$'
        }
    }

    It "propagates a real failing dotnet from every Windows dotnet step" {
        $temp = Join-Path $TestDrive "fake-dotnet"
        New-Item -ItemType Directory -Path $temp | Out-Null
        Set-Content -Path (Join-Path $temp "dotnet.cmd") -Value "@exit /b 23"
        $originalPath = $env:PATH
        $env:PATH = "$temp;$originalPath"

        try {
            $dotnetSteps = @($script:WindowsSteps | Where-Object {
                (Get-CiRunBody -Step $_) -match '(?m)^\s*dotnet\s'
            })

            foreach ($step in $dotnetSteps) {
                $exitCode = Invoke-PowerShellRunBody -Body (Get-CiRunBody -Step $step) -Directory $TestDrive
                $exitCode | Should -Be 23 -Because $step.Name
            }
        }
        finally {
            $env:PATH = $originalPath
        }
    }

    It "demonstrates that later success constructs would mask an early failure" {
        $temp = Join-Path $TestDrive "masking-dotnet"
        New-Item -ItemType Directory -Path $temp | Out-Null
        Set-Content -Path (Join-Path $temp "dotnet.cmd") -Value "@exit /b 23"
        $originalPath = $env:PATH
        $env:PATH = "$temp;$originalPath"

        try {
            (Invoke-PowerShellRunBody -Body "dotnet test fake`ncmd /c exit 0" -Directory $TestDrive) |
                Should -Be 0
            (Invoke-PowerShellRunBody -Body "dotnet test fake`nexit 0" -Directory $TestDrive) |
                Should -Be 0
            (Invoke-PowerShellRunBody -Body "dotnet test fake | cmd /c exit 0" -Directory $TestDrive) |
                Should -Be 0
        }
        finally {
            $env:PATH = $originalPath
        }
    }

    It "runs the complete script suite once through the confirmed wrapper" {
        $step = Get-CiStep -Steps $script:WindowsSteps -Name "Run script tests"
        $body = Get-CiRunBody -Step $step
        $body | Should -Match ('(?m)^' + [regex]::Escape(
            'pwsh -NoProfile -File scripts\Invoke-ScriptTests.ps1 -TestPath tests\scripts -ResultPath TestResults\pester\script-tests.xml') + '$')
        $script:Workflow | Should -Not -Match 'Install-Module Pester'
        $script:Workflow | Should -Not -Match 'Invoke-Pester'
        $script:Workflow | Should -Not -Match '(?i)\bretry\b'
    }

    It "uploads Pester and Canvas Host evidence after producer failure" {
        $pesterUpload = Get-CiStep -Steps $script:WindowsSteps -Name "Upload script test results"
        $pesterBlock = $pesterUpload.Lines -join "`n"
        $pesterBlock | Should -Match '(?m)^        if: always\(\)$'
        $pesterBlock | Should -Match '(?m)^          path: TestResults\\pester\\script-tests\.xml$'
        $pesterBlock | Should -Match '(?m)^          if-no-files-found: warn$'
        $pesterBlock | Should -Not -Match 'continue-on-error'

        $canvasUpload = Get-CiStep -Steps $script:WindowsSteps -Name "Upload Canvas Host test diagnostics"
        $canvasBlock = $canvasUpload.Lines -join "`n"
        $canvasBlock | Should -Match '(?m)^        if: always\(\)$'
        $canvasBlock | Should -Match '(?m)^          path: TestResults\\canvas-host$'
        $canvasBlock | Should -Match '(?m)^          if-no-files-found: ignore$'
        $canvasBlock | Should -Not -Match 'continue-on-error'

        $canvasTest = Get-CiRunBody -Step (
            Get-CiStep -Steps $script:WindowsSteps -Name "Run Canvas Host tests")
        $canvasTest | Should -Match ([regex]::Escape(
            '--logger "trx;LogFileName=canvas-host.trx" --results-directory TestResults\canvas-host'))
    }

    It "keeps portable validation independent and excludes Windows-only projects" {
        $linuxText = $script:LinuxJob -join "`n"
        $linuxText | Should -Match 'dotnet msbuild tests/Glasswork\.Tests/Glasswork\.Tests\.csproj -getProperty:TargetFramework -nologo'
        $linuxText | Should -Match '\[\[ "\$target_framework" != "net10\.0" \]\]'
        $linuxText | Should -Match 'dotnet restore tests/Glasswork\.Tests/Glasswork\.Tests\.csproj'
        $linuxText | Should -Match 'dotnet restore tests/Glasswork\.Mcp\.Tests/Glasswork\.Mcp\.Tests\.csproj'
        $linuxText | Should -Match 'dotnet build src/Glasswork\.Core/Glasswork\.Core\.csproj .*--no-restore'
        $linuxText | Should -Match 'dotnet build src/Glasswork\.Mcp/Glasswork\.Mcp\.csproj .*--no-restore'
        $linuxText | Should -Match 'dotnet test tests/Glasswork\.Tests/Glasswork\.Tests\.csproj .*MSTest\.Parallelize\.Workers=1'
        $linuxText | Should -Match 'dotnet test tests/Glasswork\.Mcp\.Tests/Glasswork\.Mcp\.Tests\.csproj .*--no-restore'
        $linuxText | Should -Not -Match 'Glasswork\.App|CanvasHost|VisualVerification'
        $linuxText | Should -Not -Match 'continue-on-error|(?i)\bretry\b'

        $nativeSteps = @($script:LinuxSteps | Where-Object {
            (Get-CiRunBody -Step $_) -match '(?m)^\s*(?:target_framework=.*)?dotnet\s|\$\(dotnet\s'
        })
        foreach ($step in $nativeSteps) {
            $body = Get-CiRunBody -Step $step
            @([regex]::Matches($body, '\bdotnet\s')).Count | Should -Be 1 -Because $step.Name
            ($step.Lines -join "`n") | Should -Match '(?m)^        shell: bash$'
            (Invoke-BashRunBody -Body $body -Directory $TestDrive) |
                Should -Be 23 -Because $step.Name
        }
    }

    It "keeps test suites separately visible and ordered" {
        $names = @($script:WindowsSteps.Name)
        foreach ($name in "Run Core tests", "Run MCP tests", "Run Canvas Host tests") {
            $names | Should -Contain $name
        }

        [array]::IndexOf($names, "Build Canvas Host (debug)") |
            Should -BeLessThan ([array]::IndexOf($names, "Run Canvas Host tests"))
        [array]::IndexOf($names, "Run script tests") |
            Should -BeLessThan ([array]::IndexOf($names, "Upload script test results"))
        [array]::IndexOf($names, "Run Canvas Host tests") |
            Should -BeLessThan ([array]::IndexOf($names, "Upload Canvas Host test diagnostics"))
    }
}
