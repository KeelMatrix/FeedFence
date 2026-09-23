param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$shippingProject = Join-Path (Join-Path $repositoryRoot 'src') (Join-Path 'KeelMatrix.FeedFence' 'KeelMatrix.FeedFence.csproj')
$warningSource = Join-Path (Split-Path -Parent $shippingProject) 'ReleaseWarningsAsErrorsProbe.cs'
$packOutput = Join-Path ([System.IO.Path]::GetTempPath()) ('feedfence-warning-pack-' + [guid]::NewGuid().ToString('N'))

try {
    $propertyOutput = & dotnet msbuild $shippingProject -getProperty:TreatWarningsAsErrors -p:Configuration=$Configuration
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not evaluate TreatWarningsAsErrors for the shipping project.'
    }

    $evaluatedProperty = ($propertyOutput | Select-Object -Last 1).ToString().Trim().ToLowerInvariant()
    Write-Output "Release property: TreatWarningsAsErrors=$evaluatedProperty"
    if ($evaluatedProperty -ne 'true') {
        throw 'The shipping project does not evaluate TreatWarningsAsErrors=true in Release.'
    }

    Set-Content -LiteralPath $warningSource -Encoding utf8 -Value @'
namespace KeelMatrix.FeedFence.ReleaseWarningsAsErrorsProbe;

internal static class WarningProbe
{
    [System.Obsolete("synthetic release warning", false)]
    private static void ObsoleteMember() { }

    internal static void UseObsoleteMember() => ObsoleteMember();
}
'@

    & dotnet build $shippingProject -c $Configuration --no-restore
    $buildExitCode = $LASTEXITCODE
    Write-Output "Ordinary Release validation with a shipping warning: exit=$buildExitCode (expected non-zero)"
    if ($buildExitCode -eq 0) {
        throw 'A synthetic shipping warning unexpectedly passed ordinary Release validation.'
    }

    New-Item -ItemType Directory -Force -Path $packOutput | Out-Null
    & dotnet pack $shippingProject -c $Configuration --no-restore --output $packOutput
    $packExitCode = $LASTEXITCODE
    Write-Output "Release-equivalent pack validation with a shipping warning: exit=$packExitCode (expected non-zero)"
    if ($packExitCode -eq 0) {
        throw 'A synthetic shipping warning unexpectedly passed the release-equivalent pack path.'
    }

    Write-Output 'PASS: Release warnings-as-errors negative checks rejected the warning in both paths.'
}
finally {
    if (Test-Path -LiteralPath $warningSource) {
        Remove-Item -LiteralPath $warningSource -Force
    }
    if (Test-Path -LiteralPath $packOutput) {
        Remove-Item -LiteralPath $packOutput -Recurse -Force
    }
}
