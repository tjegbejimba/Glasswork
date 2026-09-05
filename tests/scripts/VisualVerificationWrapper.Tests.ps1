BeforeAll {
    $repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $wrapper = Join-Path $repoRoot "scripts\invoke-visual-verification.ps1"
    $scenario = Join-Path $repoRoot "scripts\visual-verification\backlog-smoke.json"
}

Describe "invoke-visual-verification merge evidence failures" {
    It "Removes stale success and writes failure evidence when the scenario is missing" {
        $outDir = Join-Path $TestDrive "missing-scenario"
        New-Item -ItemType Directory -Path $outDir | Out-Null
        Set-Content -LiteralPath (Join-Path $outDir "result.json") -Value '{"Success":true}'

        & pwsh -NoProfile -File $wrapper `
            -Scenario (Join-Path $TestDrive "missing.json") `
            -OutDir $outDir `
            -MergeEvidence 2>&1 | Out-Null
        $exitCode = $LASTEXITCODE

        $exitCode | Should -Not -Be 0
        Test-Path -LiteralPath (Join-Path $outDir "result.json") | Should -BeFalse
        $failure = Get-Content -LiteralPath (Join-Path $outDir "failure.json") -Raw |
            ConvertFrom-Json
        $failure.Success | Should -BeFalse
        $failure.Stage | Should -Be "wrapper"
        $failure.Message | Should -Match "missing.json"
    }

    It "Preserves a dotnet pre-run exit code without leaving stale success" {
        $outDir = Join-Path $TestDrive "dotnet-failure"
        $fakeBin = Join-Path $TestDrive "fake-bin"
        New-Item -ItemType Directory -Path $outDir, $fakeBin | Out-Null
        Set-Content -LiteralPath (Join-Path $outDir "result.json") -Value '{"Success":true}'
        Set-Content -LiteralPath (Join-Path $fakeBin "dotnet.cmd") -Value @"
@echo off
echo forced dotnet failure 1>&2
exit /b 37
"@

        $originalPath = $env:PATH
        try {
            $env:PATH = "$fakeBin;$originalPath"
            & pwsh -NoProfile -File $wrapper `
                -Scenario $scenario `
                -OutDir $outDir `
                -MergeEvidence 2>&1 | Out-Null
            $exitCode = $LASTEXITCODE
        }
        finally {
            $env:PATH = $originalPath
        }

        $exitCode | Should -Be 37
        Test-Path -LiteralPath (Join-Path $outDir "result.json") | Should -BeFalse
        $failure = Get-Content -LiteralPath (Join-Path $outDir "failure.json") -Raw |
            ConvertFrom-Json
        $failure.Success | Should -BeFalse
        $failure.Stage | Should -Be "wrapper"
        $failure.Message | Should -Match "37"
    }

    It "Fails closed before dotnet when stale <lockedName> cannot be removed" -ForEach @(
        @{ lockedName = "result.json" },
        @{ lockedName = "failure.json" }
    ) {
        param($lockedName)

        $outDir = Join-Path $TestDrive "locked-$($lockedName.Replace('.', '-'))"
        $fakeBin = Join-Path $TestDrive "locked-fake-bin-$($lockedName.Replace('.', '-'))"
        $dotnetMarker = Join-Path $TestDrive "dotnet-ran-$($lockedName.Replace('.', '-'))"
        New-Item -ItemType Directory -Path $outDir, $fakeBin | Out-Null
        Set-Content -LiteralPath (Join-Path $outDir $lockedName) -Value '{"stale":true}'
        Set-Content -LiteralPath (Join-Path $fakeBin "dotnet.cmd") -Value @"
@echo off
echo ran> "$dotnetMarker"
exit /b 0
"@

        $locked = [IO.File]::Open(
            (Join-Path $outDir $lockedName),
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::None)
        $originalPath = $env:PATH
        try {
            $env:PATH = "$fakeBin;$originalPath"
            & pwsh -NoProfile -File $wrapper `
                -Scenario $scenario `
                -OutDir $outDir `
                -MergeEvidence 2>&1 | Out-Null
            $exitCode = $LASTEXITCODE
        }
        finally {
            $env:PATH = $originalPath
            $locked.Dispose()
        }

        $exitCode | Should -Not -Be 0
        Test-Path -LiteralPath $dotnetMarker | Should -BeFalse
    }

    It "Fails closed before dotnet when generated evidence path is a directory named <directoryName>" -ForEach @(
        @{ directoryName = "result.json" },
        @{ directoryName = "failure.json" }
    ) {
        param($directoryName)

        $outDir = Join-Path $TestDrive "directory-$($directoryName.Replace('.', '-'))"
        $fakeBin = Join-Path $TestDrive "directory-fake-bin-$($directoryName.Replace('.', '-'))"
        $dotnetMarker = Join-Path $TestDrive "directory-dotnet-ran-$($directoryName.Replace('.', '-'))"
        New-Item -ItemType Directory -Path $outDir, $fakeBin | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $outDir $directoryName) | Out-Null
        Set-Content -LiteralPath (Join-Path $fakeBin "dotnet.cmd") -Value @"
@echo off
echo ran> "$dotnetMarker"
exit /b 0
"@

        $originalPath = $env:PATH
        try {
            $env:PATH = "$fakeBin;$originalPath"
            & pwsh -NoProfile -File $wrapper `
                -Scenario $scenario `
                -OutDir $outDir `
                -MergeEvidence 2>&1 | Out-Null
            $exitCode = $LASTEXITCODE
        }
        finally {
            $env:PATH = $originalPath
        }

        $exitCode | Should -Not -Be 0
        Test-Path -LiteralPath $dotnetMarker | Should -BeFalse
        Test-Path -LiteralPath (Join-Path $outDir $directoryName) -PathType Container |
            Should -BeTrue
    }

    It "Removes a stale read-only <readOnlyName> before invoking dotnet" -ForEach @(
        @{ readOnlyName = "result.json" },
        @{ readOnlyName = "failure.json" }
    ) {
        param($readOnlyName)

        $outDir = Join-Path $TestDrive "read-only-$($readOnlyName.Replace('.', '-'))"
        $fakeBin = Join-Path $TestDrive "read-only-fake-bin-$($readOnlyName.Replace('.', '-'))"
        $dotnetMarker = Join-Path $TestDrive "read-only-dotnet-ran-$($readOnlyName.Replace('.', '-'))"
        New-Item -ItemType Directory -Path $outDir, $fakeBin | Out-Null
        $stalePath = Join-Path $outDir $readOnlyName
        Set-Content -LiteralPath $stalePath -Value '{"stale":true}'
        Set-ItemProperty -LiteralPath $stalePath -Name IsReadOnly -Value $true
        Set-Content -LiteralPath (Join-Path $fakeBin "dotnet.cmd") -Value @"
@echo off
echo ran> "$dotnetMarker"
exit /b 0
"@

        $originalPath = $env:PATH
        try {
            $env:PATH = "$fakeBin;$originalPath"
            & pwsh -NoProfile -File $wrapper `
                -Scenario $scenario `
                -OutDir $outDir `
                -MergeEvidence 2>&1 | Out-Null
            $exitCode = $LASTEXITCODE
        }
        finally {
            $env:PATH = $originalPath
        }

        $exitCode | Should -Be 0
        Test-Path -LiteralPath $dotnetMarker | Should -BeTrue
        Test-Path -LiteralPath $stalePath | Should -BeFalse
    }

    It "Preserves the dotnet exit code when failure evidence cannot be written" {
        $outDir = Join-Path $TestDrive "failure-write-error"
        $fakeBin = Join-Path $TestDrive "failure-write-fake-bin"
        New-Item -ItemType Directory -Path $outDir, $fakeBin | Out-Null
        Set-Content -LiteralPath (Join-Path $outDir "result.json") -Value '{"Success":true}'
        $env:WRAPPER_TEST_OUT = $outDir
        Set-Content -LiteralPath (Join-Path $fakeBin "dotnet.cmd") -Value @"
@echo off
rmdir /s /q "%WRAPPER_TEST_OUT%"
echo blocked> "%WRAPPER_TEST_OUT%"
exit /b 37
"@

        $originalPath = $env:PATH
        try {
            $env:PATH = "$fakeBin;$originalPath"
            & pwsh -NoProfile -File $wrapper `
                -Scenario $scenario `
                -OutDir $outDir `
                -MergeEvidence 2>&1 | Out-Null
            $exitCode = $LASTEXITCODE
        }
        finally {
            $env:PATH = $originalPath
            Remove-Item Env:\WRAPPER_TEST_OUT
        }

        $exitCode | Should -Be 37
        [IO.File]::Exists($outDir) | Should -BeTrue
    }
}
