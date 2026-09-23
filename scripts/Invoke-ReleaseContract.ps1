param(
    [string]$Tag,

    [string]$ArtifactDirectory,

    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path (Join-Path $repositoryRoot 'src') (Join-Path 'KeelMatrix.FeedFence' 'KeelMatrix.FeedFence.csproj')
$changelogPath = Join-Path $repositoryRoot 'CHANGELOG.md'
$firstReleaseRemediationPattern = '(?im)\b(?:now|no\s+longer|previously|formerly|used\s+to|fixed|fixes|corrected|resolved|addressed|this\s+removes|this\s+fixes|changed\s+from)\b'

function Invoke-ReleaseContractValidation(
    [string]$ValidationTag,
    [string]$ValidationProjectPath,
    [string]$ValidationChangelogPath,
    [string]$ValidationArtifactDirectory
) {
    if ($ValidationTag -notmatch '^v(?<version>\d+\.\d+\.\d+)$') {
        throw "Malformed release tag '$ValidationTag'. Expected vX.Y.Z."
    }

    $version = $Matches.version
    [xml]$project = Get-Content -LiteralPath $ValidationProjectPath -Raw
    $versionNode = $project.Project.PropertyGroup.Version | Select-Object -First 1
    if ($null -eq $versionNode -or $versionNode.ToString().Trim() -ne $version) {
        $declaredVersion = if ($null -eq $versionNode) { '<missing>' } else { $versionNode.ToString().Trim() }
        throw "Package version '$declaredVersion' does not match release version '$version'."
    }

    $changelogLines = @(Get-Content -LiteralPath $ValidationChangelogPath)
    $releaseHeader = "## [$version] - "
    $releaseIndex = -1
    for ($index = 0; $index -lt $changelogLines.Count; $index++) {
        if ($changelogLines[$index].StartsWith($releaseHeader, [System.StringComparison]::Ordinal)) {
            $releaseIndex = $index
            break
        }
    }

    if ($releaseIndex -lt 0 -or $changelogLines[$releaseIndex] -notmatch "^## \[$([regex]::Escape($version))\] - (?<date>\d{4}-\d{2}-\d{2})$") {
        throw "CHANGELOG.md has no dated release section for [$version]. Finalize it before tagging."
    }

    $releaseDate = $Matches.date
    $parsedDate = [datetime]::MinValue
    if (-not [datetime]::TryParseExact($releaseDate, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$parsedDate)) {
        throw "CHANGELOG.md release date '$releaseDate' is invalid."
    }

    $nextHeader = $changelogLines.Count
    for ($index = $releaseIndex + 1; $index -lt $changelogLines.Count; $index++) {
        if ($changelogLines[$index] -match '^##\s+') {
            $nextHeader = $index
            break
        }
    }

    $releaseSection = ($changelogLines[$releaseIndex..($nextHeader - 1)] -join [Environment]::NewLine)
    if ($releaseSection -match '(?im)\b(unreleased|planned|not yet published|tbd)\b') {
        throw "CHANGELOG.md release section [$version] still contains pre-release wording."
    }

    $versionedReleaseHeaders = @($changelogLines | Where-Object { $_ -match '^## \[\d+\.\d+\.\d+\] - \d{4}-\d{2}-\d{2}$' })
    if ($versionedReleaseHeaders.Count -eq 1) {
        $categories = @([regex]::Matches($releaseSection, '(?m)^###\s+(?<category>[^\r\n]+?)\s*$') | ForEach-Object { $_.Groups['category'].Value })
        if ($categories.Count -ne 1 -or $categories[0] -cne 'Added') {
            throw "The first public release section [$version] must contain only an Added category."
        }

        if ($releaseSection -match $firstReleaseRemediationPattern) {
            throw "The first public release section [$version] contains pre-release remediation wording ('$($Matches[0])')."
        }
    }

    Write-Output "Release contract: tag $ValidationTag, package version $version, changelog date $releaseDate."

    if (-not [string]::IsNullOrWhiteSpace($ValidationArtifactDirectory)) {
        if (-not (Test-Path -LiteralPath $ValidationArtifactDirectory -PathType Container)) {
            throw "Artifact directory does not exist: $ValidationArtifactDirectory"
        }

        $expected = @(
            "KeelMatrix.FeedFence.$version.nupkg",
            "KeelMatrix.FeedFence.$version.snupkg"
        )
        $actual = @(Get-ChildItem -LiteralPath $ValidationArtifactDirectory -File | Where-Object { $_.Extension -in '.nupkg', '.snupkg' } | Select-Object -ExpandProperty Name)
        $unexpected = @($actual | Where-Object { $_ -notin $expected })
        $missing = @($expected | Where-Object { $_ -notin $actual })
        if ($unexpected.Count -ne 0 -or $missing.Count -ne 0) {
            throw "Release artifact allowlist failed. Expected: $($expected -join ', '); Actual: $($actual -join ', ')"
        }

        Write-Output "Release artifact allowlist: $($actual -join ', ')."
    }

    Write-Output 'PASS: release tag, package version, changelog, and artifact contract.'
}

function Assert-ContractRejected([string]$Label, [scriptblock]$Action) {
    $rejected = $false
    try {
        & $Action | Out-Null
    }
    catch {
        $rejected = $true
    }

    if (-not $rejected) {
        throw "Release contract self-test failed: $Label was accepted."
    }
}

function Invoke-ReleaseContractSelfTest {
    $systemTempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $selfTestRoot = Join-Path $systemTempRoot "feedfence-release-contract-$([guid]::NewGuid().ToString('N'))"
    $selfTestProject = Join-Path $selfTestRoot 'KeelMatrix.FeedFence.csproj'
    $selfTestChangelog = Join-Path $selfTestRoot 'CHANGELOG.md'

    try {
        New-Item -ItemType Directory -Force -Path $selfTestRoot | Out-Null
        Set-Content -LiteralPath $selfTestProject -Encoding utf8 -Value '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><Version>0.1.0</Version></PropertyGroup></Project>'

        Set-Content -LiteralPath $selfTestChangelog -Encoding utf8 -Value @'
# Changelog

## [Unreleased]

### Added

- Provides the initial package contract.
'@
        Assert-ContractRejected 'an unreleased target version' {
            Invoke-ReleaseContractValidation 'v0.1.0' $selfTestProject $selfTestChangelog $null
        }

        Set-Content -LiteralPath $selfTestChangelog -Encoding utf8 -Value @'
# Changelog

## [Unreleased]

## [0.1.0] - 2026-09-23

### Added

- Provides the initial package contract.
'@
        Invoke-ReleaseContractValidation 'v0.1.0' $selfTestProject $selfTestChangelog $null | Out-Null

        Set-Content -LiteralPath $selfTestProject -Encoding utf8 -Value '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><Version>0.2.0</Version></PropertyGroup></Project>'
        Assert-ContractRejected 'a package/tag version mismatch' {
            Invoke-ReleaseContractValidation 'v0.1.0' $selfTestProject $selfTestChangelog $null
        }

        Set-Content -LiteralPath $selfTestProject -Encoding utf8 -Value '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><Version>0.1.0</Version></PropertyGroup></Project>'
        Set-Content -LiteralPath $selfTestChangelog -Encoding utf8 -Value @'
# Changelog

## [Unreleased]

## [0.1.0] - 2026-09-23

### Added

- Now provides the initial package contract.
'@
        Assert-ContractRejected 'first-release remediation wording' {
            Invoke-ReleaseContractValidation 'v0.1.0' $selfTestProject $selfTestChangelog $null
        }

        Set-Content -LiteralPath $selfTestChangelog -Encoding utf8 -Value @'
# Changelog

## [Unreleased]

## [0.1.0] - 2026-09-23

### Fixed

- Provides the initial package contract.
'@
        Assert-ContractRejected 'a first-release category other than Added' {
            Invoke-ReleaseContractValidation 'v0.1.0' $selfTestProject $selfTestChangelog $null
        }

        Write-Output 'PASS: release contract self-test rejected unreleased, version-mismatched, remediation-worded, and non-Added first-release fixtures and accepted the finalized fixture.'
    }
    finally {
        $resolvedSelfTestRoot = [System.IO.Path]::GetFullPath($selfTestRoot)
        if ($resolvedSelfTestRoot.StartsWith($systemTempRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
            $resolvedSelfTestRoot -ne $systemTempRoot -and
            (Test-Path -LiteralPath $resolvedSelfTestRoot)) {
            Remove-Item -LiteralPath $resolvedSelfTestRoot -Recurse -Force
        }
    }
}

if ($SelfTest) {
    Invoke-ReleaseContractSelfTest
    return
}

if ([string]::IsNullOrWhiteSpace($Tag)) {
    throw 'The -Tag parameter is required unless -SelfTest is used.'
}

$artifactPath = if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $null
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $ArtifactDirectory))
}

Invoke-ReleaseContractValidation $Tag $projectPath $changelogPath $artifactPath
