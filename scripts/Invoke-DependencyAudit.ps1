param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repositoryRoot 'KeelMatrix.FeedFence.sln'

$arguments = @('list', $solution, 'package', '--vulnerable', '--include-transitive', '--format', 'json')
$outputLines = & dotnet @arguments 2>&1
$exitCode = $LASTEXITCODE
$output = $outputLines -join [Environment]::NewLine
if ($output.Length -gt 0) {
    Write-Output $output
}

if ($exitCode -ne 0) {
    throw "Dependency vulnerability audit command failed with exit code $exitCode."
}

try {
    $report = $output | ConvertFrom-Json
}
catch {
    throw 'Dependency vulnerability audit did not return valid JSON.'
}

$vulnerabilities = @(
    foreach ($project in @($report.projects)) {
        foreach ($framework in @($project.frameworks)) {
            if ($null -ne $framework) {
                foreach ($package in @($framework.topLevelPackages)) {
                    if ($null -ne $package) {
                        foreach ($vulnerability in @($package.vulnerabilities)) {
                            if ($null -ne $vulnerability) {
                                [pscustomobject]@{ Project = $project.path; Package = $package.id; Version = $package.resolvedVersion; Severity = $vulnerability.severity; Advisory = $vulnerability.advisoryurl }
                            }
                        }
                    }
                }
                foreach ($package in @($framework.transitivePackages)) {
                    if ($null -ne $package) {
                        foreach ($vulnerability in @($package.vulnerabilities)) {
                            if ($null -ne $vulnerability) {
                                [pscustomobject]@{ Project = $project.path; Package = $package.id; Version = $package.resolvedVersion; Severity = $vulnerability.severity; Advisory = $vulnerability.advisoryurl }
                            }
                        }
                    }
                }
            }
        }
    }
)

if ($vulnerabilities.Count -ne 0) {
    $summary = $vulnerabilities | ForEach-Object { "$($_.Package) $($_.Version) [$($_.Severity)] $($_.Advisory)" }
    throw "Dependency vulnerability audit found $($vulnerabilities.Count) advisory result(s): $($summary -join '; ')"
}

Write-Output 'PASS: no direct or transitive dependency vulnerabilities were reported.'
