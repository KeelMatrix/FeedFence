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

function Invoke-Captured([string]$FilePath, [string[]]$Arguments, [string]$WorkingDirectory, [hashtable]$Environment) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($FilePath)
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    foreach ($entry in $Environment.GetEnumerator()) {
        $startInfo.Environment[$entry.Key] = $entry.Value
    }

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $process = [System.Diagnostics.Process]::Start($startInfo)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stopwatch.Stop()
    [pscustomobject]@{
        ExitCode = $process.ExitCode
        StandardOutput = $stdout.Result
        StandardError = $stderr.Result
        DurationMs = $stopwatch.ElapsedMilliseconds
    }
}

function Assert-ConsumerResult($Result, [int]$ExpectedExitCode, [string]$Label) {
    Write-Output "Consumer ${Label}: exit=$($Result.ExitCode); duration=$($Result.DurationMs) ms"
    if ($Result.ExitCode -ne $ExpectedExitCode) {
        throw "Consumer $Label failed with exit code $($Result.ExitCode). stderr: $($Result.StandardError.Trim())"
    }
}

function New-ConsumerFixture([string]$Root, [string]$Name, [string[]]$SourceKeys) {
    $fixtureRoot = Join-Path $Root $Name
    $projectDirectory = Join-Path $fixtureRoot 'project'
    New-Item -ItemType Directory -Force -Path (Join-Path $projectDirectory 'obj') | Out-Null
    $projectPath = Join-Path $projectDirectory 'Fixture.csproj'
    Set-Content -LiteralPath $projectPath -Encoding utf8 -Value '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>'
    Set-Content -LiteralPath (Join-Path $projectDirectory 'obj\project.assets.json') -Encoding utf8 -Value '{"version":3,"targets":{"net8.0":{}},"libraries":{"Fixture.Package/1.0.0":{"type":"package"}}}'

    $sources = foreach ($sourceKey in $SourceKeys) {
        $sourceDirectory = Join-Path $fixtureRoot (Join-Path 'feeds' $sourceKey)
        New-Item -ItemType Directory -Force -Path $sourceDirectory | Out-Null
        "<add key='$sourceKey' value='$([System.Security.SecurityElement]::Escape($sourceDirectory))' />"
    }

    $configPath = Join-Path $fixtureRoot 'NuGet.Config'
    Set-Content -LiteralPath $configPath -Encoding utf8 -Value "<configuration><packageSources><clear />$($sources -join '')</packageSources></configuration>"
    [pscustomobject]@{ Root = $fixtureRoot; Project = $projectPath; Config = $configPath }
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

    $nupkgPath = Join-Path $shippingOutput 'KeelMatrix.FeedFence.0.1.0.nupkg'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($nupkgPath)
    try {
        $entryNames = @($archive.Entries | Select-Object -ExpandProperty FullName)
        foreach ($requiredEntry in @('README.md', 'LICENSE', 'icon.png', 'tools/net8.0/any/KeelMatrix.Telemetry.dll', 'tools/net8.0/any/NuGet.Configuration.dll')) {
            if ($requiredEntry -notin $entryNames) {
                throw "Shipping package is missing required entry: $requiredEntry"
            }
        }

        $forbidden = @($entryNames | Where-Object { $_ -match '(^|/)(AGENTS\.md|\.env[^/]*|tests?/|bin/|obj/|paperclip-guide/)' })
        if ($forbidden.Count -ne 0) {
            throw "Shipping package contains forbidden entries: $($forbidden -join ', ')"
        }

        $nuspecEntry = $archive.Entries | Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1
        $reader = [System.IO.StreamReader]::new($nuspecEntry.Open())
        try { $nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($nuspec -match '<dependencies') {
            throw 'Shipping tool must remain self-contained and must not declare an unallowlisted nuspec dependency.'
        }
        Write-Output 'Package content contract: README, LICENSE, founder-provided icon, embedded runtime set, and exclusion checks passed.'
    }
    finally {
        $archive.Dispose()
    }

    $consumerRoot = Join-Path $outputRoot 'consumer'
    $localSource = Join-Path $consumerRoot 'local-source'
    $toolPath = Join-Path $consumerRoot 'tool'
    $isolatedPackages = Join-Path $consumerRoot 'packages'
    $isolatedHome = Join-Path $consumerRoot 'dotnet-home'
    New-Item -ItemType Directory -Force -Path $localSource, $toolPath, $isolatedPackages, $isolatedHome | Out-Null
    Copy-Item -LiteralPath $nupkgPath -Destination (Join-Path $localSource (Split-Path $nupkgPath -Leaf))

    $telemetryNupkg = Join-Path $env:USERPROFILE '.nuget\packages\keelmatrix.telemetry\0.1.0\keelmatrix.telemetry.0.1.0.nupkg'
    if (-not (Test-Path -LiteralPath $telemetryNupkg)) {
        throw "The resolved KeelMatrix.Telemetry 0.1.0 package is not available for the isolated local source: $telemetryNupkg"
    }
    Copy-Item -LiteralPath $telemetryNupkg -Destination (Join-Path $localSource (Split-Path $telemetryNupkg -Leaf))

    $consumerConfig = Join-Path $consumerRoot 'NuGet.Config'
    Set-Content -LiteralPath $consumerConfig -Encoding utf8 -Value "<configuration><packageSources><clear /><add key='local' value='$([System.Security.SecurityElement]::Escape($localSource))' /><add key='nuget.org' value='https://api.nuget.org/v3/index.json' /></packageSources></configuration>"
    $consumerEnvironment = @{
        NUGET_PACKAGES = $isolatedPackages
        DOTNET_CLI_HOME = $isolatedHome
        DOTNET_NOLOGO = '1'
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        KEELMATRIX_NO_TELEMETRY = '1'
    }

    $install = Invoke-Captured 'dotnet' @('tool', 'install', '--tool-path', $toolPath, '--configfile', $consumerConfig, '--add-source', $localSource, '--version', '0.1.0', '--no-cache', 'KeelMatrix.FeedFence') $consumerRoot $consumerEnvironment
    Assert-ConsumerResult $install 0 'tool install'
    if ($install.StandardError.Length -ne 0) { Write-Output "Consumer tool install stderr: $($install.StandardError.Trim())" }

    $toolCommand = Get-ChildItem -LiteralPath $toolPath -File | Where-Object { $_.Name -like 'feedfence*' } | Select-Object -First 1
    if ($null -eq $toolCommand) { throw 'The isolated tool path does not contain the feedfence command.' }

    $help = Invoke-Captured $toolCommand.FullName @('--help') $consumerRoot $consumerEnvironment
    Assert-ConsumerResult $help 0 'feedfence --help'
    if ($help.StandardOutput -notmatch 'feedfence check \[path\]') { throw 'The installed feedfence help contract is missing.' }

    $pass = New-ConsumerFixture $consumerRoot 'pass' @('local')
    $passText = Invoke-Captured $toolCommand.FullName @('check', $pass.Project, '--config', $pass.Config, '--format', 'text') $consumerRoot $consumerEnvironment
    Assert-ConsumerResult $passText 0 'passing restored fixture (text)'
    $passJson = Invoke-Captured $toolCommand.FullName @('check', $pass.Project, '--config', $pass.Config, '--format', 'json') $consumerRoot $consumerEnvironment
    Assert-ConsumerResult $passJson 0 'passing restored fixture (JSON)'
    if ($passJson.StandardError.Length -ne 0) { throw 'JSON consumer output polluted stderr.' }
    $passJsonRepeat = Invoke-Captured $toolCommand.FullName @('check', $pass.Project, '--config', $pass.Config, '--format', 'json') $consumerRoot $consumerEnvironment
    Assert-ConsumerResult $passJsonRepeat 0 'passing restored fixture (JSON determinism repeat)'
    if ($passJson.StandardOutput -cne $passJsonRepeat.StandardOutput) { throw 'Installed JSON output is not byte-deterministic.' }
    if (($passJson.StandardOutput | ConvertFrom-Json).schemaVersion -ne 1) { throw 'Installed JSON schema version is not 1.' }
    Write-Output 'Consumer JSON determinism: PASS (two identical installed-tool runs matched byte-for-byte).'

    $violation = New-ConsumerFixture $consumerRoot 'violation' @('public', 'private')
    $violationResult = Invoke-Captured $toolCommand.FullName @('check', $violation.Project, '--config', $violation.Config, '--format', 'text') $consumerRoot $consumerEnvironment
    Assert-ConsumerResult $violationResult 1 'policy-violation fixture'
    if ($violationResult.StandardOutput -notmatch 'FF001') { throw 'The installed violation fixture did not report FF001.' }

    $missingRoot = Join-Path $consumerRoot 'missing'
    New-Item -ItemType Directory -Force -Path $missingRoot | Out-Null
    $missingProject = Join-Path $missingRoot 'Fixture.csproj'
    Set-Content -LiteralPath $missingProject -Encoding utf8 -Value '<Project />'
    $missingResult = Invoke-Captured $toolCommand.FullName @('check', $missingProject, '--config', $pass.Config) $consumerRoot $consumerEnvironment
    Assert-ConsumerResult $missingResult 2 'missing-artifact fixture'
    if ($missingResult.StandardError -notmatch 'restore artifacts are missing') { throw 'The installed missing-artifact fixture did not explain the environment failure.' }

    $depsFiles = @(Get-ChildItem -LiteralPath $toolPath -Recurse -File -Filter '*.deps.json')
    foreach ($depsFile in $depsFiles) {
        $depsText = Get-Content -Raw -LiteralPath $depsFile.FullName
        if ($depsText.Contains($repositoryRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Installed tool dependency metadata references the source repository: $($depsFile.FullName)"
        }
    }
    $dependencyIds = [regex]::Matches($nuspec, '<dependency\s+id="([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
    if ($dependencyIds.Count -ne 0) { throw "Installed package has unexpected nuspec dependencies: $($dependencyIds -join ', ')" }
    Write-Output 'Consumer independence: PASS (isolated package cache/tool path, no source-repository path, no unpublished package dependency).'
    Write-Output 'PASS: pack gate, package content contract, and full isolated package-consumer smoke passed.'
}
finally {
    if (Test-Path -LiteralPath $outputRoot) {
        Remove-Item -LiteralPath $outputRoot -Recurse -Force
    }
}
