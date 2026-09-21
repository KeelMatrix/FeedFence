param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$shippingProject = Join-Path $repositoryRoot 'src\KeelMatrix.FeedFence\KeelMatrix.FeedFence.csproj'
$probeProject = Join-Path $repositoryRoot 'tests\Phase0Probe\KeelMatrix.FeedFence.Phase0Probe.csproj'
$outputRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('feedfence-pack-gate-' + [guid]::NewGuid().ToString('N'))

function Invoke-Checked([string]$FilePath, [string[]]$Arguments) {
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($Arguments -join ' ')"
    }
}

try {
    New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

    $projects = Get-ChildItem -LiteralPath $repositoryRoot -Recurse -Filter '*.csproj' -File |
        Where-Object { $_.FullName -notmatch '[\\/]bin[\\/]|[\\/]obj[\\/]' }
    $shippingFullPath = [System.IO.Path]::GetFullPath($shippingProject)
    foreach ($project in $projects) {
        $projectFullPath = [System.IO.Path]::GetFullPath($project.FullName)
        $propertyOutput = & dotnet msbuild $projectFullPath -getProperty:IsPackable
        if ($LASTEXITCODE -ne 0) {
            throw "Could not inspect packability for $($project.Name)."
        }

        $isPackable = ($propertyOutput | Select-Object -Last 1).ToString().Trim().ToLowerInvariant()
        $isShipping = $projectFullPath.Equals($shippingFullPath, [System.StringComparison]::OrdinalIgnoreCase)
        Write-Output "Packability: $($project.Name) = $isPackable"
        if ($isShipping -and $isPackable -ne 'true') {
            throw 'The shipping project is not packable.'
        }

        if (-not $isShipping -and $isPackable -ne 'false') {
            throw "Unexpected packable non-shipping project: $($project.Name)."
        }
    }

    $probeOutput = Join-Path $outputRoot 'probe'
    New-Item -ItemType Directory -Force -Path $probeOutput | Out-Null
    Invoke-Checked 'dotnet' @('pack', $probeProject, '-c', $Configuration, '--no-restore', '--output', $probeOutput)
    $probeArtifacts = @(Get-ChildItem -LiteralPath $probeOutput -File | Where-Object { $_.Extension -in '.nupkg', '.snupkg' })
    if ($probeArtifacts.Count -ne 0) {
        throw "The non-shipping probe produced package artifacts: $($probeArtifacts.Name -join ', ')"
    }
    Write-Output 'Probe direct-pack artifact check: no package artifacts.'

    $shippingOutput = Join-Path $outputRoot 'shipping'
    New-Item -ItemType Directory -Force -Path $shippingOutput | Out-Null
    Invoke-Checked 'dotnet' @('pack', $shippingProject, '-c', $Configuration, '--no-restore', '--output', $shippingOutput)
    $artifacts = @(Get-ChildItem -LiteralPath $shippingOutput -File | Where-Object { $_.Extension -in '.nupkg', '.snupkg' } | Select-Object -ExpandProperty Name)
    $expected = @('KeelMatrix.FeedFence.0.1.0.nupkg', 'KeelMatrix.FeedFence.0.1.0.snupkg')
    $unexpected = @($artifacts | Where-Object { $_ -notin $expected })
    $missing = @($expected | Where-Object { $_ -notin $artifacts })
    if ($unexpected.Count -ne 0 -or $missing.Count -ne 0) {
        throw "Shipping artifact allowlist failed. Expected: $($expected -join ', '); Actual: $($artifacts -join ', ')"
    }

    Write-Output "Shipping artifact allowlist: $($artifacts -join ', ')"
    Write-Output 'PASS: pack gate emitted only the allowlisted KeelMatrix.FeedFence artifacts.'
}
finally {
    if (Test-Path -LiteralPath $outputRoot) {
        Remove-Item -LiteralPath $outputRoot -Recurse -Force
    }
}
