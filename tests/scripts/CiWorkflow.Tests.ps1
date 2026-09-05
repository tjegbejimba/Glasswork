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
        $uploadReferenceCount = 0
        foreach ($line in $script:WindowsJob) {
            if ($line.TrimStart().StartsWith("#", [System.StringComparison]::Ordinal)) {
                continue
            }

            $uploadReferenceCount += [regex]::Matches(
                $line,
                'actions/upload-artifact@').Count
        }
        $uploadReferenceCount | Should -Be 2
        $artifactUploads = @($script:WindowsSteps | Where-Object {
            ($_.Lines -join "`n") -match '(?m)^\s{8}[''"]?uses[''"]?\s*:\s*[''"]?actions/upload-artifact@'
        })
        $artifactUploads.Count | Should -Be 2
        $artifactUploads[0].Name | Should -Be "Upload script test results"
        $artifactUploads[1].Name | Should -Be "Upload Canvas Host test diagnostics"

        $pesterUpload = Get-CiStep -Steps $script:WindowsSteps -Name "Upload script test results"
        $pesterBlock = $pesterUpload.Lines -join "`n"
        $pesterLines = @($pesterUpload.Lines | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_)
        })
        $pesterLines.Count | Should -Be 7
        $pesterLines[0] | Should -Be "        if: always()"
        $pesterLines[1] | Should -Be "        uses: actions/upload-artifact@v4"
        $pesterLines[2] | Should -Be "        with:"
        $pesterLines[3] | Should -Be '          name: ci-pester-results-${{ github.run_attempt }}'
        $pesterLines[4] | Should -Be '          path: TestResults\pester\script-tests.xml'
        $pesterLines[5] | Should -Be "          if-no-files-found: warn"
        $pesterLines[6] | Should -Be "          retention-days: 14"
        $pesterBlock | Should -Not -Match 'continue-on-error'

        $canvasUploads = @($script:WindowsSteps | Where-Object {
            $block = $_.Lines -join "`n"
            $block -match '(?m)^        uses: actions/upload-artifact@v4$' -and
                $block -match '(?i)canvas-host'
        })
        $canvasUploads.Count | Should -Be 1
        $canvasUpload = $canvasUploads[0]
        $canvasUpload.Name | Should -Be "Upload Canvas Host test diagnostics"
        $canvasBlock = $canvasUpload.Lines -join "`n"
        @($canvasUpload.Lines | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_)
        }).Count | Should -Be 9
        $stepKeys = @($canvasUpload.Lines | ForEach-Object {
            if ($_ -match '^\s{8}[''"]?([a-zA-Z][a-zA-Z0-9-]*)[''"]?\s*:') {
                $Matches[1]
            }
        })
        $stepKeys.Count | Should -Be 3
        $stepKeys[0] | Should -Be "if"
        $stepKeys[1] | Should -Be "uses"
        $stepKeys[2] | Should -Be "with"

        $withKeys = @($canvasUpload.Lines | ForEach-Object {
            if ($_ -match '^\s{10}[''"]?([a-zA-Z][a-zA-Z0-9-]*)[''"]?\s*:') {
                $Matches[1]
            }
        })
        $withKeys.Count | Should -Be 4
        $withKeys[0] | Should -Be "name"
        $withKeys[1] | Should -Be "path"
        $withKeys[2] | Should -Be "if-no-files-found"
        $withKeys[3] | Should -Be "retention-days"

        $canvasUpload.Lines[0] | Should -Be "        if: always()"
        $canvasUpload.Lines[1] | Should -Be "        uses: actions/upload-artifact@v4"
        $canvasUpload.Lines[2] | Should -Be "        with:"
        $canvasUpload.Lines[3] | Should -Be '          name: ci-canvas-host-results-${{ github.run_attempt }}'
        $canvasUpload.Lines[4] | Should -Be "          path: |"
        $pathIndex = [array]::IndexOf($canvasUpload.Lines, "          path: |")
        $canvasPaths = [System.Collections.Generic.List[string]]::new()
        for ($index = $pathIndex + 1; $index -lt $canvasUpload.Lines.Count; $index++) {
            $line = $canvasUpload.Lines[$index]
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }

            $indent = $line.Length - $line.TrimStart().Length
            if ($indent -le 10) {
                break
            }

            $canvasPaths.Add($line.Substring(12))
        }
        $canvasPaths.Count | Should -Be 2
        $canvasPaths[0] | Should -Be 'TestResults\canvas-host\canvas-host.trx'
        $canvasPaths[1] | Should -Be 'TestResults\canvas-host\diagnostics\${{ github.run_id }}-${{ github.run_attempt }}\*.json'
        $canvasUpload.Lines[$pathIndex + 3] | Should -Be "          if-no-files-found: ignore"
        $canvasUpload.Lines[$pathIndex + 4] | Should -Be "          retention-days: 14"
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
