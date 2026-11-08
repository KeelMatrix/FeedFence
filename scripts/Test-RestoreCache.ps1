param(
    [string]$ConfigPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'NuGet.config')
)

$ErrorActionPreference = 'Stop'

function Invoke-FixtureDotnet([string]$Root, [string[]]$Arguments, [string]$PackagesOverride = '') {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.WorkingDirectory = $Root
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.Environment.Remove('NUGET_PACKAGES') | Out-Null
    if ($PackagesOverride) { $start.Environment['NUGET_PACKAGES'] = $PackagesOverride }
    $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $start.Environment['KEELMATRIX_NO_TELEMETRY'] = '1'
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $timer = [Diagnostics.Stopwatch]::StartNew()
    if (!$process.Start()) { throw 'Unable to start restore-cache fixture.' }
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(60000)) {
            $process.Kill($true)
            throw 'Restore-cache fixture timed out.'
        }
        $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        Write-Host "dotnet $($Arguments -join ' '): exit=$($process.ExitCode); duration=$($timer.ElapsedMilliseconds) ms"
        [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output }
    }
    finally { $process.Dispose() }
}

function Assert-FixtureResult($Result, [bool]$Success, [string]$Label) {
    if (($Result.ExitCode -eq 0) -ne $Success -or (!$Success -and $Result.Output -notmatch 'NU1403')) {
        throw "$Label failed: $($Result.Output)"
    }
    Write-Output "PASS: $Label"
}

function Write-FixturePackage([string]$Feed, [string]$Marker, [string]$AssemblyPath) {
    New-Item -ItemType Directory -Force -Path $Feed | Out-Null
    $archive = [IO.Compression.ZipFile]::Open((Join-Path $Feed 'FeedFence.RestoreFixture.1.0.0.nupkg'), [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entries = @{
            'FeedFence.RestoreFixture.nuspec' = '<package><metadata><id>FeedFence.RestoreFixture</id><version>1.0.0</version><authors>Fixture</authors><description>Offline restore cache fixture.</description></metadata></package>'
            'content/marker.txt' = $Marker
        }
        foreach ($name in $entries.Keys) {
            $writer = [IO.StreamWriter]::new($archive.CreateEntry($name).Open())
            try { $writer.Write($entries[$name]) } finally { $writer.Dispose() }
        }
        $assembly = $archive.CreateEntry('lib/net8.0/FeedFence.RestoreFixture.dll').Open()
        $input = [IO.File]::OpenRead($AssemblyPath)
        try { $input.CopyTo($assembly) } finally { $input.Dispose(); $assembly.Dispose() }
    }
    finally { $archive.Dispose() }
}

$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
if (![OperatingSystem]::IsWindows()) {
    $physical = & realpath -- $tempParent
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($physical)) { throw 'Unable to resolve fixture temporary directory.' }
    $tempParent = $physical.Trim()
}
$tempRoot = Join-Path $tempParent ('feedfence-cache-contract-' + [guid]::NewGuid().ToString('N'))
try {
    $root = Join-Path $tempRoot 'repository'
    $goodFeed = Join-Path $tempRoot 'trusted-feed'
    $badFeed = Join-Path $tempRoot 'conflicting-feed'
    $badPackages = Join-Path $tempRoot 'inherited-packages'
    $goodPackages = Join-Path $tempRoot 'trusted-packages'
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $ConfigPath) 'global.json') -Destination $root
    # The inherited setting models a user-level cache containing a local rebuild.
    $escapedBad = [Security.SecurityElement]::Escape($badPackages)
    Set-Content -LiteralPath (Join-Path $tempRoot 'NuGet.config') -Value "<configuration><config><add key=`"globalPackagesFolder`" value=`"$escapedBad`" /></config></configuration>"
    [xml]$config = Get-Content -LiteralPath $ConfigPath -Raw
    $sources = $config.SelectSingleNode('/configuration/packageSources')
    $sources.RemoveAll()
    $sources.AppendChild($config.CreateElement('clear')) | Out-Null
    $source = $config.CreateElement('add')
    $source.SetAttribute('key', 'fixture')
    $source.SetAttribute('value', $goodFeed)
    $sources.AppendChild($source) | Out-Null
    $config.Save((Join-Path $root 'NuGet.config'))

    # A compiled dependency makes pack exercise the same cache lifetime as a
    # shipping tool; a content-only marker package would miss that seam.
    $dependencyRoot = Join-Path $root 'Dependency'
    New-Item -ItemType Directory -Force -Path $dependencyRoot | Out-Null
    $dependencyProject = Join-Path $dependencyRoot 'Dependency.csproj'
    Set-Content -LiteralPath $dependencyProject -Value '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><NuGetAudit>false</NuGetAudit><AssemblyName>FeedFence.RestoreFixture</AssemblyName></PropertyGroup></Project>'
    Set-Content -LiteralPath (Join-Path $dependencyRoot 'Dependency.cs') -Value 'namespace Fixture; public static class Dependency { public static string Message() => "fixture"; }'
    New-Item -ItemType Directory -Force -Path $goodFeed | Out-Null
    Assert-FixtureResult (Invoke-FixtureDotnet $root @('build', $dependencyProject, '-c', 'Release')) $true 'build offline fixture dependency'
    $assemblyPath = Join-Path $dependencyRoot 'bin/Release/net8.0/FeedFence.RestoreFixture.dll'
    Write-FixturePackage $goodFeed 'trusted' $assemblyPath
    Write-FixturePackage $badFeed 'conflicting same ID and version' $assemblyPath

    $shipping = Join-Path $root 'Shipping/Shipping.csproj'
    $cli = Join-Path $root 'Cli/Cli.csproj'
    New-Item -ItemType Directory -Force -Path (Split-Path $shipping), (Split-Path $cli) | Out-Null
    Set-Content -LiteralPath $shipping -Value '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><OutputType>Exe</OutputType><PackAsTool>true</PackAsTool><ToolCommandName>cache-fixture</ToolCommandName><NuGetAudit>false</NuGetAudit><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile></PropertyGroup><ItemGroup><PackageReference Include="FeedFence.RestoreFixture" Version="1.0.0" /></ItemGroup></Project>'
    Set-Content -LiteralPath (Join-Path (Split-Path $shipping) 'Program.cs') -Value 'public static class Program { public static void Main() { System.Console.WriteLine(Fixture.Dependency.Message()); } }'
    Set-Content -LiteralPath $cli -Value '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><NuGetAudit>false</NuGetAudit><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile></PropertyGroup><ItemGroup><ProjectReference Include="../Shipping/Shipping.csproj" /></ItemGroup></Project>'

    Assert-FixtureResult (Invoke-FixtureDotnet $root @('restore', $cli, '--packages', $badPackages, '--source', $badFeed)) $true 'prime conflicting inherited cache'
    # Generate only synthetic fixture locks from the trusted source, then freeze them.
    Assert-FixtureResult (Invoke-FixtureDotnet $root @('restore', $cli, '--packages', $goodPackages, '--force-evaluate')) $true 'generate trusted fixture locks'
    Set-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Value '<Project><PropertyGroup><RestoreLockedMode>true</RestoreLockedMode></PropertyGroup></Project>'

    Assert-FixtureResult (Invoke-FixtureDotnet $root @('build', $cli, '-c', 'Release')) $true 'ordinary build ignores conflicting inherited cache'
    foreach ($project in @($shipping, $cli)) {
        $assets = Get-Content -LiteralPath (Join-Path (Split-Path $project) 'obj/project.assets.json') -Raw | ConvertFrom-Json
        $actual = [IO.Path]::GetFullPath($assets.project.restore.packagesPath).TrimEnd([IO.Path]::DirectorySeparatorChar)
        $expected = [IO.Path]::GetFullPath((Join-Path $root '.nuget/packages'))
        if ($actual -ne $expected) { throw "Ordinary build selected an unexpected cache: $actual" }
    }
    $artifactRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
    if ((Split-Path $artifactRoot -Parent) -ne $root) { throw 'Artifact cleanup escaped the fixture.' }
    New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $artifactRoot 'old-output.txt') -Value 'disposable output'
    Remove-Item -LiteralPath $artifactRoot -Recurse -Force
    Assert-FixtureResult (Invoke-FixtureDotnet $root @('pack', $shipping, '-c', 'Release', '--no-restore', '--output', (Join-Path $artifactRoot 'packages'))) $true 'pack after release cleanup needs no restore'
    Assert-FixtureResult (Invoke-FixtureDotnet $root @('restore', $cli, '--packages', $badPackages)) $false 'explicit conflicting cache fails locked restore'
    Assert-FixtureResult (Invoke-FixtureDotnet $root @('build', $cli) $badPackages) $false 'environment conflicting cache fails build'
    Assert-FixtureResult (Invoke-FixtureDotnet $root @('build', $cli) $goodPackages) $true 'environment trusted cache passes build'

    foreach ($project in @($shipping, $cli)) {
        $lock = Join-Path (Split-Path $project) 'packages.lock.json'
        $original = [IO.File]::ReadAllText($lock)
        try {
            $mutated = [regex]::Replace($original, '"contentHash": "[^"]+"', '"contentHash": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=="')
            if ($mutated -eq $original) { throw 'Fixture lock mutation did not change a hash.' }
            [IO.File]::WriteAllText($lock, $mutated)
            # Invalidate only disposable fixture restore outputs so an up-to-date
            # restore cannot hide the deliberately changed lock contents.
            foreach ($member in @($shipping, $cli)) {
                [IO.File]::Delete((Join-Path (Split-Path $member) 'obj/project.nuget.cache'))
            }
            Assert-FixtureResult (Invoke-FixtureDotnet $root @('build', $cli)) $false "mutated $([IO.Path]::GetFileNameWithoutExtension($project)) lock fails build"
        }
        finally { [IO.File]::WriteAllText($lock, $original) }
    }
    Assert-FixtureResult (Invoke-FixtureDotnet $root @('build', $cli)) $true 'restored locks pass ordinary build'
}
finally {
    $resolved = [IO.Path]::GetFullPath($tempRoot)
    if (!(Split-Path $resolved -Leaf).StartsWith('feedfence-cache-contract-') -or (Split-Path $resolved -Parent) -ne $tempParent.TrimEnd([IO.Path]::DirectorySeparatorChar)) {
        throw 'Refusing to remove an unexpected restore fixture directory.'
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
