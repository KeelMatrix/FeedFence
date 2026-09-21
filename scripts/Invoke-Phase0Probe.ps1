param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$emptyFeed = Join-Path ([System.IO.Path]::GetTempPath()) ('feedfence-empty-' + [guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Force -Path $emptyFeed | Out-Null
    dotnet restore (Join-Path $repositoryRoot 'KeelMatrix.FeedFence.sln') --source $emptyFeed --ignore-failed-sources --no-cache
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet build (Join-Path $repositoryRoot 'KeelMatrix.FeedFence.sln') -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet run --project (Join-Path $repositoryRoot 'tests\Phase0Probe\KeelMatrix.FeedFence.Phase0Probe.csproj') -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Phase 0 probe failed.' }
}
finally {
    if (Test-Path -LiteralPath $emptyFeed) {
        Remove-Item -LiteralPath $emptyFeed -Recurse -Force
    }
}
