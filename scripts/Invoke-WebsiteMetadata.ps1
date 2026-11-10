param(
    [string]$Configuration = 'Release',
    [string[]]$PackagePath = @(),
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $repositoryRoot 'build/WebsiteMetadata.ps1')
if ($SelfTest) {
    & (Join-Path $repositoryRoot 'tests/Test-WebsiteMetadata.ps1')
}
else {
    $packages = @(Get-WebsiteSourcePackages $repositoryRoot $Configuration)
    Assert-WebsiteMetadata (Join-Path $repositoryRoot 'keelmatrix.website.json') $packages
    foreach ($path in $PackagePath) { Assert-WebsiteBuiltPackage $path $packages }
    Write-Output "Website metadata: PASS ($($packages.Count) packable project(s), $($PackagePath.Count) built package(s))."
}
