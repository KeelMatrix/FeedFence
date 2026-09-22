param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,

    [string]$ArtifactDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path (Join-Path $repositoryRoot 'src') (Join-Path 'KeelMatrix.FeedFence' 'KeelMatrix.FeedFence.csproj')
$changelogPath = Join-Path $repositoryRoot 'CHANGELOG.md'

if ($Tag -notmatch '^v(?<version>\d+\.\d+\.\d+)$') {
    throw "Malformed release tag '$Tag'. Expected vX.Y.Z."
}

$version = $Matches.version
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$versionNode = $project.Project.PropertyGroup.Version | Select-Object -First 1
if ($null -eq $versionNode -or $versionNode.ToString().Trim() -ne $version) {
    $declaredVersion = if ($null -eq $versionNode) { '<missing>' } else { $versionNode.ToString().Trim() }
    throw "Package version '$declaredVersion' does not match release version '$version'."
}

$changelogLines = @(Get-Content -LiteralPath $changelogPath)
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

Write-Output "Release contract: tag $Tag, package version $version, changelog date $releaseDate."

if (-not [string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $artifactPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $ArtifactDirectory))
    if (-not (Test-Path -LiteralPath $artifactPath -PathType Container)) {
        throw "Artifact directory does not exist: $ArtifactDirectory"
    }

    $expected = @(
        "KeelMatrix.FeedFence.$version.nupkg",
        "KeelMatrix.FeedFence.$version.snupkg"
    )
    $actual = @(Get-ChildItem -LiteralPath $artifactPath -File | Where-Object { $_.Extension -in '.nupkg', '.snupkg' } | Select-Object -ExpandProperty Name)
    $unexpected = @($actual | Where-Object { $_ -notin $expected })
    $missing = @($expected | Where-Object { $_ -notin $actual })
    if ($unexpected.Count -ne 0 -or $missing.Count -ne 0) {
        throw "Release artifact allowlist failed. Expected: $($expected -join ', '); Actual: $($actual -join ', ')"
    }

    Write-Output "Release artifact allowlist: $($actual -join ', ')."
}

Write-Output 'PASS: release tag, package version, changelog, and artifact contract.'
