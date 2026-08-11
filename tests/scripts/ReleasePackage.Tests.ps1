BeforeAll {
    $scriptRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    . (Join-Path $scriptRoot "scripts\New-ReleasePackage.ps1")
}

Describe "New-ReleasePackage" {
    It "Creates the stable-named Windows archive and matching SHA-256 sidecar" {
        $publishDirectory = Join-Path $TestDrive "publish"
        $outputDirectory = Join-Path $TestDrive "release"
        $updaterDirectory = Join-Path $publishDirectory "Updater"
        New-Item -ItemType Directory -Path $publishDirectory, $updaterDirectory | Out-Null
        Set-Content -Path (Join-Path $publishDirectory "Glasswork.exe") -Value "release binary"
        Set-Content -Path (Join-Path $updaterDirectory "release-update.ps1") -Value "wrapper"
        Set-Content -Path (Join-Path $updaterDirectory "Invoke-ReleaseUpdate.ps1") -Value "updater"

        $result = New-ReleasePackage `
            -PublishDirectory $publishDirectory `
            -OutputDirectory $outputDirectory

        $result.ArchivePath | Should -Be (Join-Path $outputDirectory "Glasswork-win-x64.zip")
        $result.ChecksumPath | Should -Be (Join-Path $outputDirectory "Glasswork-win-x64.zip.sha256")
        Test-Path $result.ArchivePath | Should -BeTrue
        (Get-Content $result.ChecksumPath -Raw).Trim() |
            Should -Match "^[A-F0-9]{64}  Glasswork-win-x64\.zip$"

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [System.IO.Compression.ZipFile]::OpenRead($result.ArchivePath)
        try {
            $entries = $archive.Entries.FullName
            $entries | Should -Contain "Glasswork.exe"
            $entries | Should -Contain "Updater/release-update.ps1"
            $entries | Should -Contain "Updater/Invoke-ReleaseUpdate.ps1"
        }
        finally {
            $archive.Dispose()
        }
    }

    It "Rejects a publish output without the bundled updater" {
        $publishDirectory = Join-Path $TestDrive "publish-missing-updater"
        New-Item -ItemType Directory -Path $publishDirectory | Out-Null
        Set-Content -Path (Join-Path $publishDirectory "Glasswork.exe") -Value "release binary"

        {
            New-ReleasePackage `
                -PublishDirectory $publishDirectory `
                -OutputDirectory (Join-Path $TestDrive "release-missing-updater")
        } | Should -Throw "*Updater*"
    }
}
