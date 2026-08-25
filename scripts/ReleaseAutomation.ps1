#Requires -Version 7.0

function Get-RequiredNativeOutputLine {
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$Output,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $lines = @($Output)
    if ($lines.Count -ne 1) {
        throw "Expected exactly one line for $Description; received $($lines.Count)."
    }

    $line = ([string]$lines[0]).Trim()
    if ([string]::IsNullOrWhiteSpace($line)) {
        throw "Expected a non-empty line for $Description."
    }
    return $line
}

function Test-ReleaseScheduleGate {
    param(
        [Parameter(Mandatory = $true)]
        [datetime]$UtcNow
    )

    $timeZone = $null
    foreach ($timeZoneId in @("America/Los_Angeles", "Pacific Standard Time")) {
        try {
            $timeZone = [System.TimeZoneInfo]::FindSystemTimeZoneById($timeZoneId)
            break
        }
        catch [System.TimeZoneNotFoundException] {
            continue
        }
        catch [System.InvalidTimeZoneException] {
            continue
        }
    }

    if ($null -eq $timeZone) {
        throw "The America/Los_Angeles time zone is not available."
    }

    $utc = if ($UtcNow.Kind -eq [System.DateTimeKind]::Local) {
        $UtcNow.ToUniversalTime()
    }
    elseif ($UtcNow.Kind -eq [System.DateTimeKind]::Unspecified) {
        [datetime]::SpecifyKind($UtcNow, [System.DateTimeKind]::Utc)
    }
    else {
        $UtcNow
    }
    $pacificNow = [System.TimeZoneInfo]::ConvertTimeFromUtc($utc, $timeZone)
    return $pacificNow.DayOfWeek -notin @(
        [System.DayOfWeek]::Saturday,
        [System.DayOfWeek]::Sunday
    ) -and $pacificNow.Hour -eq 9
}

function Get-LatestPublishedReleaseTag {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Releases,

        [Parameter(Mandatory = $true)]
        [ValidateSet("App", "Mcp")]
        [string]$Stream
    )

    $regex = if ($Stream -eq "App") {
        '^v(?<version>\d+\.\d+\.\d+)$'
    }
    else {
        '^mcp-v(?<version>0\.\d+\.\d+)$'
    }
    $stable = @(
        foreach ($release in $Releases) {
            if ($null -eq $release -or
                [bool]$release.draft -or
                [bool]$release.prerelease) {
                continue
            }
            $tag = [string]$release.tag_name
            $match = [regex]::Match($tag, $regex)
            if ($match.Success) {
                [pscustomobject]@{
                    Tag = $tag
                    Version = [version]$match.Groups["version"].Value
                    VersionText = $match.Groups["version"].Value
                }
            }
        }
    )
    if ($stable.Count -eq 0) {
        throw "No published immutable stable $Stream GitHub Release exists."
    }

    $duplicateTag = $stable |
        Group-Object Tag |
        Where-Object Count -gt 1 |
        Select-Object -First 1
    if ($null -ne $duplicateTag) {
        throw "Multiple published GitHub Releases use tag '$($duplicateTag.Name)'."
    }

    return $stable |
        Sort-Object Version -Descending |
        Select-Object -First 1
}

function Get-ReleasePathStreams {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$Paths
    )

    $pathSet = [System.Collections.Generic.Dictionary[string, string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $Paths) {
        if ([string]::IsNullOrWhiteSpace($path)) {
            throw "Release paths must not contain empty entries."
        }

        $canonicalPath = $path.Trim().Replace("\", "/")
        while ($canonicalPath.StartsWith("./", [System.StringComparison]::Ordinal)) {
            $canonicalPath = $canonicalPath.Substring(2)
        }
        if ([string]::IsNullOrWhiteSpace($canonicalPath) -or $canonicalPath.StartsWith("/")) {
            throw "Release paths must be repository-relative: '$path'."
        }

        if (-not $pathSet.ContainsKey($canonicalPath)) {
            $pathSet.Add($canonicalPath, $canonicalPath)
        }
    }

    [string[]]$canonicalPaths = @($pathSet.Values)
    [System.Array]::Sort($canonicalPaths, [System.StringComparer]::OrdinalIgnoreCase)

    $appPaths = [System.Collections.Generic.List[string]]::new()
    $mcpPaths = [System.Collections.Generic.List[string]]::new()
    $excludedPaths = [System.Collections.Generic.List[string]]::new()
    $appScripts = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    $bothScripts = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    @(
        "scripts/release-update.ps1",
        "scripts/Invoke-ReleaseUpdate.ps1",
        "scripts/New-ReleasePackage.ps1"
    ) | ForEach-Object { [void]$appScripts.Add($_) }
    @(
        "scripts/install-mcp.ps1",
        "scripts/Install-McpTool.ps1",
        "scripts/Validate-McpReleasePublication.ps1"
    ) | ForEach-Object { [void]$bothScripts.Add($_) }

    foreach ($path in $canonicalPaths) {
        $isApp = $path.StartsWith(
            "src/Glasswork.App/",
            [System.StringComparison]::OrdinalIgnoreCase) -or
            $path.StartsWith(
                "src/Glasswork.Core/",
                [System.StringComparison]::OrdinalIgnoreCase) -or
            $appScripts.Contains($path) -or
            $bothScripts.Contains($path)
        $isMcp = $path.StartsWith(
            "src/Glasswork.Mcp/",
            [System.StringComparison]::OrdinalIgnoreCase) -or
            $path.StartsWith(
                "src/Glasswork.Core/",
                [System.StringComparison]::OrdinalIgnoreCase) -or
            $bothScripts.Contains($path)

        if ($isApp) {
            $appPaths.Add($path)
        }
        if ($isMcp) {
            $mcpPaths.Add($path)
        }
        if (-not $isApp -and -not $isMcp) {
            $excludedPaths.Add($path)
        }
    }

    $includedPaths = [pscustomobject]@{
        App = @($appPaths.ToArray())
        Mcp = @($mcpPaths.ToArray())
    }
    return [pscustomobject]@{
        App           = $appPaths.Count -gt 0
        Mcp           = $mcpPaths.Count -gt 0
        IncludedPaths = $includedPaths
        AppPaths      = @($appPaths.ToArray())
        McpPaths      = @($mcpPaths.ToArray())
        ExcludedPaths = @($excludedPaths.ToArray())
    }
}

function Resolve-ReleaseLabelDirective {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$Labels,

        [ValidateSet("App", "Mcp")]
        [string]$Stream
    )

    $releaseDirectives = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    $semVerBumps = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)

    foreach ($label in $Labels) {
        if ([string]::IsNullOrWhiteSpace($label)) {
            continue
        }

        switch ($label.Trim().ToLowerInvariant()) {
            "release:app" { [void]$releaseDirectives.Add("App") }
            "release:mcp" { [void]$releaseDirectives.Add("Mcp") }
            "release:both" { [void]$releaseDirectives.Add("Both") }
            "release:none" { [void]$releaseDirectives.Add("None") }
            "semver:major" { [void]$semVerBumps.Add("major") }
            "semver:minor" { [void]$semVerBumps.Add("minor") }
            "semver:patch" { [void]$semVerBumps.Add("patch") }
        }
    }

    if ($releaseDirectives.Count -gt 1) {
        throw "Conflicting release labels were provided."
    }
    $releaseDirective = if ($releaseDirectives.Count -eq 0) {
        $null
    }
    else {
        @($releaseDirectives)[0]
    }
    $appliesToStream = [string]::IsNullOrWhiteSpace($Stream) -or
        $null -eq $releaseDirective -or
        $releaseDirective -eq "Both" -or
        $releaseDirective -eq $Stream
    if ($semVerBumps.Count -gt 1 -and $appliesToStream) {
        throw "Conflicting semver labels were provided."
    }
    $semVerBump = if ($semVerBumps.Count -eq 0) {
        "patch"
    }
    elseif ($semVerBumps.Count -gt 1) {
        "patch"
    }
    else {
        @($semVerBumps)[0].ToLowerInvariant()
    }

    return [pscustomobject]@{
        ReleaseDirective = $releaseDirective
        SemVerBump        = $semVerBump
    }
}

function Get-MaximumSemVerBump {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$Bumps,

        [Parameter(Mandatory = $true)]
        [ValidateSet("App", "Mcp")]
        [string]$Stream
    )

    $maximumRank = 0
    foreach ($bump in $Bumps) {
        $normalizedBump = if ([string]::IsNullOrWhiteSpace($bump)) {
            "patch"
        }
        else {
            $bump.Trim().ToLowerInvariant()
        }

        $rank = switch ($normalizedBump) {
            "patch" { 0 }
            "minor" { 1 }
            "major" { 2 }
            "breaking" { 2 }
            default { throw "Unsupported semantic-version bump '$bump'." }
        }
        if ($Stream -eq "Mcp" -and $rank -eq 2) {
            $rank = 1
        }
        if ($rank -gt $maximumRank) {
            $maximumRank = $rank
        }
    }

    return @("patch", "minor", "major")[$maximumRank]
}

function Get-NextReleaseVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CurrentVersion,

        [Parameter(Mandatory = $true)]
        [ValidateSet("patch", "minor", "major", "breaking")]
        [string]$Bump,

        [Parameter(Mandatory = $true)]
        [ValidateSet("App", "Mcp")]
        [string]$Stream
    )

    $versionMatch = [regex]::Match(
        $CurrentVersion,
        '^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)$')
    if (-not $versionMatch.Success) {
        throw "Current version must be a stable numeric semantic version: '$CurrentVersion'."
    }

    [uint64]$major = 0
    [uint64]$minor = 0
    [uint64]$patch = 0
    if (-not [uint64]::TryParse($versionMatch.Groups["major"].Value, [ref]$major) -or
        -not [uint64]::TryParse($versionMatch.Groups["minor"].Value, [ref]$minor) -or
        -not [uint64]::TryParse($versionMatch.Groups["patch"].Value, [ref]$patch)) {
        throw "Current version contains a numeric component that is too large."
    }

    if ($Stream -eq "Mcp" -and $major -ne 0) {
        throw "MCP versions must remain in 0.x."
    }

    $normalizedBump = $Bump.ToLowerInvariant()
    if ($Stream -eq "Mcp" -and $normalizedBump -in @("major", "breaking")) {
        $normalizedBump = "minor"
    }
    elseif ($normalizedBump -eq "breaking") {
        $normalizedBump = "major"
    }

    switch ($normalizedBump) {
        "patch" {
            if ($patch -eq [uint64]::MaxValue) {
                throw "Current version cannot be incremented because a component is too large."
            }
            $patch++
        }
        "minor" {
            if ($minor -eq [uint64]::MaxValue) {
                throw "Current version cannot be incremented because a component is too large."
            }
            $minor++
            $patch = 0
        }
        "major" {
            if ($major -eq [uint64]::MaxValue) {
                throw "Current version cannot be incremented because a component is too large."
            }
            $major++
            $minor = 0
            $patch = 0
        }
    }

    return "$major.$minor.$patch"
}

function Test-ReleaseNetDiff {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$NameStatusLines
    )

    $hasEffectiveChange = $false
    foreach ($line in $NameStatusLines) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $fields = $line -split "`t"
        $status = $fields[0]
        if ($status -match '^[RC](?<score>\d{1,3})$') {
            $score = [int]$Matches["score"]
            if ($fields.Count -ne 3 -or $score -gt 100 -or
                [string]::IsNullOrWhiteSpace($fields[1]) -or
                [string]::IsNullOrWhiteSpace($fields[2])) {
                throw "Malformed git name-status entry: '$line'."
            }

            if (-not $fields[1].Equals($fields[2], [System.StringComparison]::Ordinal)) {
                $hasEffectiveChange = $true
            }
            continue
        }

        if ($status -notmatch '^[ACDMTUXB]$' -or $fields.Count -ne 2 -or
            [string]::IsNullOrWhiteSpace($fields[1])) {
            throw "Malformed git name-status entry: '$line'."
        }
        $hasEffectiveChange = $true
    }

    return $hasEffectiveChange
}

function New-ReleasePlan {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet("App", "Mcp")]
        [string]$Stream,

        [Parameter(Mandatory = $true)]
        [string]$BaseTag,

        [Parameter(Mandatory = $true)]
        [string]$BaseVersion,

        [Parameter(Mandatory = $true)]
        [string]$CandidateSha,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$NameStatusLines,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$LabelsByPullRequest,

        [switch]$Force
    )

    $hasNetDiff = Test-ReleaseNetDiff -NameStatusLines $NameStatusLines
    $changedPaths = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $NameStatusLines) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $fields = $line -split "`t"
        for ($index = 1; $index -lt $fields.Count; $index++) {
            $changedPaths.Add($fields[$index])
        }
    }

    $pathStreams = Get-ReleasePathStreams -Paths @($changedPaths.ToArray())
    $includedByPath = if ($Stream -eq "App") {
        $pathStreams.App
    }
    else {
        $pathStreams.Mcp
    }

    $pullRequests = [System.Collections.Generic.List[object]]::new()
    foreach ($pullRequest in $LabelsByPullRequest) {
        if ($null -eq $pullRequest) {
            throw "Pull-request metadata must not contain null entries."
        }

        [long]$number = 0
        if (-not [long]::TryParse([string]$pullRequest.Number, [ref]$number) -or $number -le 0) {
            throw "Pull-request metadata contains an invalid Number."
        }
        $title = [string]$pullRequest.Title
        $url = [string]$pullRequest.Url
        $author = ([string]$pullRequest.Author).Trim().TrimStart("@")
        if ([string]::IsNullOrWhiteSpace($title) -or
            [string]::IsNullOrWhiteSpace($url) -or
            [string]::IsNullOrWhiteSpace($author)) {
            throw "Pull request #$number must include Title, Url, and Author."
        }

        $labels = [System.Collections.Generic.List[string]]::new()
        foreach ($label in @($pullRequest.Labels)) {
            if ($null -eq $label) {
                continue
            }
            if ($label -is [string]) {
                $labelName = [string]$label
            }
            elseif ($null -ne $label.PSObject.Properties["Name"]) {
                $labelName = [string]$label.Name
            }
            else {
                throw "Pull request #$number contains a label without a Name."
            }
            if (-not [string]::IsNullOrWhiteSpace($labelName)) {
                $labels.Add($labelName.Trim())
            }
        }

        $directive = Resolve-ReleaseLabelDirective `
            -Labels @($labels.ToArray()) `
            -Stream $Stream
        $pullRequests.Add([pscustomobject]@{
            Number           = $number
            Title            = $title.Trim()
            Url              = $url.Trim()
            Author           = $author
            Labels           = @($labels.ToArray())
            ReleaseDirective = $directive.ReleaseDirective
            SemVerBump       = $directive.SemVerBump
        })
    }

    $matchingDirectives = @(
        $pullRequests | Where-Object {
            $_.ReleaseDirective -eq $Stream -or $_.ReleaseDirective -eq "Both"
        }
    )
    $hasMatchingDirective = $matchingDirectives.Count -gt 0

    $eligible = $false
    $reason = "NoNetChanges"
    if ($hasNetDiff) {
        if ($includedByPath) {
            $eligible = $true
            $reason = "IncludedPaths"
        }
        elseif ($Force) {
            $eligible = $true
            $reason = "Forced"
        }
        elseif ($hasMatchingDirective) {
            $eligible = $true
            $reason = "ReleaseDirective"
        }
        else {
            $reason = "NoIncludedPaths"
        }
    }

    $breakingNotes = [System.Collections.Generic.List[object]]::new()
    $featureNotes = [System.Collections.Generic.List[object]]::new()
    $fixNotes = [System.Collections.Generic.List[object]]::new()
    $maintenanceNotes = [System.Collections.Generic.List[object]]::new()
    $bumps = [System.Collections.Generic.List[string]]::new()

    $orderedPullRequests = @(
        $pullRequests | Sort-Object `
            @{ Expression = { $_.Number }; Ascending = $true },
            @{ Expression = { $_.Title }; Ascending = $true },
            @{ Expression = { $_.Url }; Ascending = $true }
    )
    foreach ($pullRequest in $orderedPullRequests) {
        if (-not $eligible -or $pullRequest.ReleaseDirective -eq "None") {
            continue
        }
        if (($Stream -eq "App" -and $pullRequest.ReleaseDirective -eq "Mcp") -or
            ($Stream -eq "Mcp" -and $pullRequest.ReleaseDirective -eq "App")) {
            continue
        }

        $normalizedLabels = @(
            $pullRequest.Labels |
                ForEach-Object { $_.ToLowerInvariant() }
        )
        $bump = if ($normalizedLabels -contains "breaking") {
            "major"
        }
        else {
            $pullRequest.SemVerBump
        }
        $bumps.Add($bump)

        $category = if ($normalizedLabels -contains "breaking" -or
            $normalizedLabels -contains "semver:major") {
            "Breaking"
        }
        elseif ($normalizedLabels -contains "feature" -or
            $normalizedLabels -contains "enhancement") {
            "Features"
        }
        elseif ($normalizedLabels -contains "bug") {
            "Fixes"
        }
        else {
            "Maintenance"
        }

        $entry = [pscustomobject]@{
            Id       = "pr:$($pullRequest.Number)"
            Category = $category
            Number   = $pullRequest.Number
            Title    = $pullRequest.Title
            Url      = $pullRequest.Url
            Author   = $pullRequest.Author
            Text     = "$($pullRequest.Title) ([#$($pullRequest.Number)]" +
                "($($pullRequest.Url))) — @$($pullRequest.Author)"
        }
        switch ($category) {
            "Breaking" { $breakingNotes.Add($entry) }
            "Features" { $featureNotes.Add($entry) }
            "Fixes" { $fixNotes.Add($entry) }
            "Maintenance" { $maintenanceNotes.Add($entry) }
        }
    }

    $bump = Get-MaximumSemVerBump -Bumps @($bumps.ToArray()) -Stream $Stream
    $calculatedVersion = Get-NextReleaseVersion `
        -CurrentVersion $BaseVersion `
        -Bump $bump `
        -Stream $Stream
    $notes = [pscustomobject]@{
        Breaking    = @($breakingNotes.ToArray())
        Features    = @($featureNotes.ToArray())
        Fixes       = @($fixNotes.ToArray())
        Maintenance = @($maintenanceNotes.ToArray())
    }

    return [pscustomobject]@{
        Eligible       = $eligible
        Reason         = $reason
        Stream         = $Stream
        BaseTag        = $BaseTag
        BaseVersion    = $BaseVersion
        CandidateSha   = $CandidateSha
        NextVersion    = if ($eligible) { $calculatedVersion } else { $null }
        Bump           = $bump
        Notes          = $notes
        CategorizedNotes = $notes
        IncludedPaths  = $pathStreams.IncludedPaths
        ExcludedPaths  = $pathStreams.ExcludedPaths
    }
}

function Test-ReleasePrChangedFiles {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet("App", "Mcp")]
        [string]$Stream,

        [Parameter(Mandatory = $true)]
        [string]$Version,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$Paths
    )

    if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
        return $false
    }
    if ($Stream -eq "Mcp" -and $Version -notmatch '^0\.') {
        return $false
    }

    $requiredPaths = if ($Stream -eq "App") {
        @(
            "src/Glasswork.App/Glasswork.csproj",
            "docs/releases/v$Version.md",
            "CHANGELOG.md"
        )
    }
    else {
        @(
            "src/Glasswork.Mcp/Glasswork.Mcp.csproj",
            "src/Glasswork.Mcp/CHANGELOG.md",
            "CHANGELOG.md"
        )
    }

    $actualPaths = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $Paths) {
        if ([string]::IsNullOrWhiteSpace($path)) {
            return $false
        }
        $canonicalPath = $path.Trim().Replace("\", "/")
        while ($canonicalPath.StartsWith("./", [System.StringComparison]::Ordinal)) {
            $canonicalPath = $canonicalPath.Substring(2)
        }
        [void]$actualPaths.Add($canonicalPath)
    }

    if ($actualPaths.Count -ne $requiredPaths.Count) {
        return $false
    }
    foreach ($requiredPath in $requiredPaths) {
        if (-not $actualPaths.Contains($requiredPath)) {
            return $false
        }
    }

    return $true
}

function Test-ReleaseProjectVersionChange {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet("App", "Mcp")]
        [string]$Stream,

        [Parameter(Mandatory = $true)]
        [string]$Version,

        [Parameter(Mandatory = $true)]
        [string]$BaseContent,

        [Parameter(Mandatory = $true)]
        [string]$ReleaseContent
    )

    if ($Version -notmatch '^\d+\.\d+\.\d+$' -or
        ($Stream -eq "Mcp" -and $Version -notmatch '^0\.')) {
        return $false
    }

    $expected = $BaseContent
    $replacements = if ($Stream -eq "App") {
        [ordered]@{
            Version = $Version
            AssemblyVersion = "$Version.0"
            FileVersion = "$Version.0"
            InformationalVersion = $Version
        }
    }
    else {
        [ordered]@{ Version = $Version }
    }
    foreach ($entry in $replacements.GetEnumerator()) {
        $pattern = "(?m)(<$($entry.Key)>)[^<]+(</$($entry.Key)>)"
        $matches = [regex]::Matches($expected, $pattern)
        if ($matches.Count -ne 1) {
            return $false
        }
        $expected = [regex]::Replace(
            $expected,
            $pattern,
            "`${1}$($entry.Value)`${2}")
    }

    return $expected.TrimEnd() -ceq $ReleaseContent.TrimEnd()
}

function Resolve-ReleaseBlockerAction {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$ExistingIssues,

        [Parameter(Mandatory = $true)]
        [ValidateSet("App", "Mcp")]
        [string]$Stream,

        [ValidateSet("Evaluation", "Publication")]
        [string]$Stage,

        [Parameter(Mandatory = $true)]
        [bool]$HasFailure
    )

    $expectedTitle = "[Release automation][$Stream] Blocked"
    $expectedStreamLabel = "release:$($Stream.ToLowerInvariant())"
    $matchingIssues = [System.Collections.Generic.List[object]]::new()

    foreach ($issue in $ExistingIssues) {
        if ($null -eq $issue -or
            -not ([string]$issue.Title).Equals(
                $expectedTitle,
                [System.StringComparison]::Ordinal)) {
            continue
        }

        $stateProperty = $issue.PSObject.Properties["State"]
        if ($null -ne $stateProperty -and
            -not [string]::IsNullOrWhiteSpace([string]$issue.State) -and
            -not ([string]$issue.State).Equals(
                "OPEN",
                [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $labels = [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::OrdinalIgnoreCase)
        foreach ($label in @($issue.Labels)) {
            if ($label -is [string]) {
                $labelName = [string]$label
            }
            elseif ($null -ne $label -and
                $null -ne $label.PSObject.Properties["Name"]) {
                $labelName = [string]$label.Name
            }
            else {
                continue
            }
            if (-not [string]::IsNullOrWhiteSpace($labelName)) {
                [void]$labels.Add($labelName.Trim())
            }
        }

        if ($labels.Contains("release-automation-blocker") -and
            $labels.Contains($expectedStreamLabel)) {
            $matchingIssues.Add($issue)
        }
    }

    if ($matchingIssues.Count -gt 1) {
        throw "More than one matching open release blocker exists for $Stream."
    }
    if ($matchingIssues.Count -eq 0) {
        if ($HasFailure) {
            return "Create"
        }
        return "None"
    }
    $bodyProperty = $matchingIssues[0].PSObject.Properties["Body"]
    $stageMarker = if ($null -ne $bodyProperty) {
        [regex]::Match(
            [string]$matchingIssues[0].Body,
            '<!-- release-automation-blocker-stage:(?<stage>Evaluation|Publication) -->')
    }
    else {
        $null
    }
    $existingStage = if ($null -ne $stageMarker -and $stageMarker.Success) {
        $stageMarker.Groups["stage"].Value
    }
    else {
        $null
    }
    if ($HasFailure) {
        if ($Stage -eq "Evaluation" -and $existingStage -eq "Publication") {
            return "None"
        }
        return "Update"
    }
    if ($Stage -eq "Publication") {
        return "Close"
    }
    if (-not [string]::IsNullOrWhiteSpace($Stage)) {
        if ($existingStage -ne $Stage) {
            return "None"
        }
    }
    return "Close"
}

function Test-AiReleaseNotes {
    [CmdletBinding(DefaultParameterSetName = "Response")]
    param(
        [Parameter(Mandatory = $true, ParameterSetName = "Path")]
        [string]$ResponsePath,

        [Parameter(Mandatory = $true, ParameterSetName = "Response")]
        [AllowEmptyString()]
        [string]$Response
    )

    $json = if ($PSCmdlet.ParameterSetName -eq "Path") {
        if (-not (Test-Path -LiteralPath $ResponsePath -PathType Leaf)) {
            throw "AI release-notes response file was not found: '$ResponsePath'."
        }
        Get-Content -LiteralPath $ResponsePath -Raw
    }
    else {
        $Response
    }
    if ([string]::IsNullOrWhiteSpace($json)) {
        throw "AI release-notes response must not be empty."
    }

    try {
        $document = [System.Text.Json.JsonDocument]::Parse($json)
    }
    catch {
        throw "AI release-notes response is not valid JSON: $($_.Exception.Message)"
    }

    try {
        $root = $document.RootElement
        if ($root.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
            throw "AI release-notes response must be a JSON object."
        }

        $requiredCategories = @("Breaking", "Features", "Fixes", "Maintenance")
        $seenCategories = [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::Ordinal)
        $normalized = @{}
        $properties = $root.EnumerateObject()
        while ($properties.MoveNext()) {
            $property = $properties.Current
            if (-not $seenCategories.Add($property.Name)) {
                throw "AI release-notes response contains duplicate key '$($property.Name)'."
            }
            if ($property.Name -cnotin $requiredCategories) {
                throw "AI release-notes response must contain exactly the keys Breaking, Features, Fixes, and Maintenance."
            }
            if ($property.Value.ValueKind -ne [System.Text.Json.JsonValueKind]::Array) {
                throw "AI release-notes category '$($property.Name)' must be an array of id/text objects."
            }

            $items = [System.Collections.Generic.List[object]]::new()
            $ids = [System.Collections.Generic.HashSet[string]]::new(
                [System.StringComparer]::Ordinal)
            $elements = $property.Value.EnumerateArray()
            while ($elements.MoveNext()) {
                $element = $elements.Current
                if ($element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
                    throw "AI release-notes category '$($property.Name)' must be an array of id/text objects."
                }

                $itemProperties = @($element.EnumerateObject())
                $itemPropertyNames = @($itemProperties.Name | Sort-Object)
                if (($itemPropertyNames -join ",") -ne "id,text") {
                    throw "AI release-note objects must contain exactly id and text."
                }
                $id = $element.GetProperty("id").GetString()
                $item = $element.GetProperty("text").GetString()
                if ($id -notmatch '^(pr:\d+|commit:[0-9a-f]{40})$') {
                    throw "AI release-note item IDs must identify one supplied PR or commit."
                }
                if (-not $ids.Add($id)) {
                    throw "AI release-note item IDs must be unique within a category."
                }
                if ([string]::IsNullOrWhiteSpace($item)) {
                    throw "AI release-notes categories must not contain empty strings."
                }
                if ($item.Length -gt 500) {
                    throw "AI release-note items must not exceed 500 characters."
                }
                if ($item -match '[<>]') {
                    throw "AI release-note items must not contain HTML or comments."
                }
                if ($item -match '(?i)(https?://|javascript:|@\w|\[[^\]]*\]\(|`)') {
                    throw "AI release-note items must contain prose only, without links, authors, or code."
                }
                if ($item -match '\p{Cc}') {
                    throw "AI release-note items must not contain control characters."
                }

                $items.Add([pscustomobject]@{
                    id = $id
                    text = $item.Trim()
                })
            }
            $normalized[$property.Name] = @($items.ToArray())
        }

        if ($seenCategories.Count -ne $requiredCategories.Count) {
            throw "AI release-notes response must contain exactly the keys Breaking, Features, Fixes, and Maintenance."
        }
        foreach ($category in $requiredCategories) {
            if (-not $seenCategories.Contains($category)) {
                throw "AI release-notes response must contain exactly the keys Breaking, Features, Fixes, and Maintenance."
            }
        }

        return [pscustomobject]@{
            Breaking = @($normalized["Breaking"])
            Features = @($normalized["Features"])
            Fixes = @($normalized["Fixes"])
            Maintenance = @($normalized["Maintenance"])
        }
    }
    finally {
        $document.Dispose()
    }
}

function ConvertTo-ReleaseNotesMarkdown {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Plan,

        [object]$AiNotes
    )

    $stream = [string]$Plan.Stream
    if ($stream -notin @("App", "Mcp")) {
        throw "Release plan Stream must be App or Mcp."
    }
    $version = [string]$Plan.NextVersion
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "Release plan must be eligible and include NextVersion before rendering notes."
    }
    if ($null -eq $Plan.Notes) {
        throw "Release plan must include categorized Notes."
    }
    $releaseDate = if ($null -ne $Plan.PSObject.Properties["ReleaseDate"] -and
        [string]$Plan.ReleaseDate -match '^\d{4}-\d{2}-\d{2}$') {
        [string]$Plan.ReleaseDate
    }
    else {
        [datetime]::UtcNow.ToString("yyyy-MM-dd")
    }

    $categories = @("Breaking", "Features", "Fixes", "Maintenance")
    $useAiNotes = $false
    $normalizedAiNotes = $null
    if ($null -ne $AiNotes) {
        try {
            $normalizedAiNotes = if ($AiNotes -is [string]) {
                Test-AiReleaseNotes -Response ([string]$AiNotes)
            }
            else {
                $serializedAiNotes = ConvertTo-Json -InputObject $AiNotes -Depth 10 -Compress
                Test-AiReleaseNotes -Response $serializedAiNotes
            }

            $mappingMatches = $true
            foreach ($category in $categories) {
                $deterministicItems = @($Plan.Notes.$category)
                $aiItems = @($normalizedAiNotes.$category)
                if ($deterministicItems.Count -ne $aiItems.Count) {
                    $mappingMatches = $false
                    break
                }
                $aiIds = @($aiItems | ForEach-Object { $_.Id })
                if (@($aiIds | Select-Object -Unique).Count -ne $aiIds.Count) {
                    $mappingMatches = $false
                    break
                }
                foreach ($item in $deterministicItems) {
                    if ($null -eq $item.PSObject.Properties["Id"] -or
                        [string]::IsNullOrWhiteSpace([string]$item.Id) -or
                        $aiIds -notcontains [string]$item.Id -or
                        $null -eq $item.PSObject.Properties["Number"] -or
                        $null -eq $item.PSObject.Properties["Url"] -or
                        $null -eq $item.PSObject.Properties["Author"] -or
                        [string]::IsNullOrWhiteSpace([string]$item.Url) -or
                        [string]::IsNullOrWhiteSpace([string]$item.Author)) {
                        $mappingMatches = $false
                        break
                    }
                }
                if (-not $mappingMatches) {
                    break
                }
            }
            $useAiNotes = $mappingMatches
        }
        catch {
            $useAiNotes = $false
        }
    }

    $categoryLines = [System.Collections.Generic.List[string]]::new()
    foreach ($category in $categories) {
        $categoryLines.Add("### $category")
        $categoryLines.Add("")
        $deterministicItems = @($Plan.Notes.$category)
        if ($deterministicItems.Count -eq 0) {
            $categoryLines.Add("- None.")
        }
        else {
            for ($index = 0; $index -lt $deterministicItems.Count; $index++) {
                $item = $deterministicItems[$index]
                $text = if ($useAiNotes) {
                    $aiItem = @($normalizedAiNotes.$category |
                        Where-Object { $_.Id -eq $item.Id })[0]
                    $identity = if ([string]$item.Id -match '^commit:(?<sha>[0-9a-f]{40})$') {
                        "([commit $($Matches["sha"].Substring(0, 7))]($($item.Url))) — $($item.Author)"
                    }
                    else {
                        "([#$($item.Number)]($($item.Url))) — @$($item.Author)"
                    }
                    "$($aiItem.Text) $identity"
                }
                else {
                    [string]$item.Text
                }
                $categoryLines.Add("- $text")
            }
        }
        $categoryLines.Add("")
    }

    $appReleaseNotes = $null
    $mcpChangelogFragment = $null
    if ($stream -eq "App") {
        $lines = [System.Collections.Generic.List[string]]::new()
        @(
            "# Glasswork v$version",
            "",
            "Changes since $($Plan.BaseTag).",
            "",
            "## Changes",
            ""
        ) | ForEach-Object { $lines.Add($_) }
        $categoryLines | ForEach-Object { $lines.Add($_) }
        @(
            "## Validation",
            "",
            "- Release workflow gates run."
        ) | ForEach-Object { $lines.Add($_) }
        $appReleaseNotes = ($lines -join "`n").TrimEnd() + "`n"
    }
    else {
        $lines = [System.Collections.Generic.List[string]]::new()
        @(
            "## [$version] — $releaseDate",
            ""
        ) | ForEach-Object { $lines.Add($_) }
        $categoryLines | ForEach-Object { $lines.Add($_) }
        $mcpChangelogFragment = ($lines -join "`n").TrimEnd() + "`n"
    }

    $rootLines = [System.Collections.Generic.List[string]]::new()
    $rootLines.Add("## $stream v$version — $releaseDate")
    $rootLines.Add("")
    $categoryLines | ForEach-Object { $rootLines.Add($_) }
    $rootChangelogFragment = ($rootLines -join "`n").TrimEnd() + "`n"
    $markdown = if ($stream -eq "App") {
        $appReleaseNotes
    }
    else {
        $mcpChangelogFragment
    }

    return [pscustomobject]@{
        Stream                 = $stream
        Version                = $version
        Markdown               = $markdown
        AppReleaseNotes        = $appReleaseNotes
        McpChangelogFragment   = $mcpChangelogFragment
        RootChangelogFragment  = $rootChangelogFragment
        RootChangelog          = $rootChangelogFragment
    }
}
