param(
    [string]$Configuration = 'Release',
    [string]$PackageVersion = '0.1.0',
    [string]$ArtifactDirectory = ''
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$shippingProject = Join-Path (Join-Path $repositoryRoot 'src') (Join-Path 'KeelMatrix.FeedFence' 'KeelMatrix.FeedFence.csproj')
$probeProject = Join-Path (Join-Path $repositoryRoot 'tests') (Join-Path 'Phase0Probe' 'KeelMatrix.FeedFence.Phase0Probe.csproj')
$outputRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('feedfence-pack-gate-' + [guid]::NewGuid().ToString('N'))
$shippingOutput = if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    Join-Path $outputRoot 'shipping'
}
else {
    [System.IO.Path]::GetFullPath($ArtifactDirectory)
}

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
        throw "Consumer $Label failed with exit code $($Result.ExitCode). stdout: $($Result.StandardOutput.Trim()) stderr: $($Result.StandardError.Trim())"
    }
}

function Assert-NoMachineLocalPdbPaths([string[]]$ArchivePaths) {
    $machineAbsolutePathPattern = '(?i)(?<![A-Za-z0-9])(?:[A-Z]:[\\/]|\\\\[^\\/\r\n]+[\\/]|/(?:Users|home|private|tmp)/)'
    $pdbCount = 0
    foreach ($archivePath in $ArchivePaths) {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            foreach ($entry in @($archive.Entries | Where-Object { $_.FullName -match '(?i)\.pdb$' })) {
                $pdbCount++
                $memory = [System.IO.MemoryStream]::new()
                try {
                    $stream = $entry.Open()
                    try { $stream.CopyTo($memory) } finally { $stream.Dispose() }
                    $bytes = $memory.ToArray()
                    $texts = @(
                        [System.Text.Encoding]::UTF8.GetString($bytes),
                        [System.Text.Encoding]::Unicode.GetString($bytes)
                    )
                    if ($texts | Where-Object { $_ -match $machineAbsolutePathPattern }) {
                        throw "Symbol/PDB privacy check failed for $($entry.FullName) in $(Split-Path -Leaf $archivePath): machine-local absolute path detected."
                    }
                }
                finally {
                    $memory.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }

    if ($pdbCount -eq 0) {
        throw 'Symbol/PDB privacy check found no PDB entries to inspect.'
    }

    Write-Output "Symbol/PDB path privacy: PASS (inspected $pdbCount PDB entries across package and symbols artifacts; no machine-local absolute paths found)."
}

function Assert-Equal([object]$Actual, [object]$Expected, [string]$Label) {
    if ($Actual -cne $Expected) {
        throw "$Label mismatch. Expected '$Expected'; actual '$Actual'."
    }
}

function Get-ArchiveEntryBytes([string]$ArchivePath, [string]$EntryName) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entry = $archive.GetEntry($EntryName)
        if ($null -eq $entry) {
            throw "Archive $(Split-Path -Leaf $ArchivePath) is missing $EntryName."
        }

        $memory = [System.IO.MemoryStream]::new()
        try {
            $stream = $entry.Open()
            try { $stream.CopyTo($memory) } finally { $stream.Dispose() }
            return $memory.ToArray()
        }
        finally {
            $memory.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-ArchiveEntryText([string]$ArchivePath, [string]$EntryName) {
    $text = [System.Text.Encoding]::UTF8.GetString((Get-ArchiveEntryBytes $ArchivePath $EntryName))
    return $text.TrimStart([char[]]@(0xFEFF))
}

function Get-BytesSha256([byte[]]$Bytes) {
    return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Assert-ArchiveEntryMatchesFile([string]$ArchivePath, [string]$EntryName, [string]$FilePath, [string]$Label) {
    $archiveHash = Get-BytesSha256 (Get-ArchiveEntryBytes $ArchivePath $EntryName)
    $fileHash = (Get-FileHash -LiteralPath $FilePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-Equal $archiveHash $fileHash "$Label SHA-256"
    Write-Output "$Label byte identity: PASS (SHA-256 $fileHash)."
}

function Assert-ArchiveAllowlist([string]$ArchivePath, [string[]]$StaticEntries, [string]$Label) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entryNames = @($archive.Entries | ForEach-Object { $_.FullName })
    }
    finally {
        $archive.Dispose()
    }

    $distinctNames = @($entryNames | Sort-Object -Unique)
    if ($distinctNames.Count -ne $entryNames.Count) {
        throw "$Label contains duplicate archive entry names."
    }

    $coreProperties = @($entryNames | Where-Object { $_ -cmatch '^package/services/metadata/core-properties/[0-9a-f]{32}\.psmdcp$' })
    if ($coreProperties.Count -ne 1) {
        throw "$Label must contain exactly one NuGet core-properties entry; found $($coreProperties.Count)."
    }

    $unexpected = @($entryNames | Where-Object { $_ -notin $StaticEntries -and $_ -notin $coreProperties })
    $missing = @($StaticEntries | Where-Object { $_ -notin $entryNames })
    if ($unexpected.Count -ne 0 -or $missing.Count -ne 0 -or $entryNames.Count -ne ($StaticEntries.Count + 1)) {
        throw "$Label archive allowlist failed. Missing: $($missing -join ', '); unexpected: $($unexpected -join ', ')."
    }

    Write-Output "$Label complete archive allowlist: PASS ($($entryNames.Count) entries): $(@($entryNames | Sort-Object) -join ', ')"
}

function Assert-NuspecMetadata([string]$ArchivePath, [string]$NuspecEntry, [string]$ExpectedPackageType, [bool]$IsPrimaryPackage, [string]$ExpectedCommit) {
    [xml]$document = Get-ArchiveEntryText $ArchivePath $NuspecEntry
    $metadata = $document.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
    if ($null -eq $metadata) { throw "$(Split-Path -Leaf $ArchivePath) has no nuspec metadata element." }

    Assert-Equal $metadata.SelectSingleNode("*[local-name()='id']").InnerText 'KeelMatrix.FeedFence' 'Package ID'
    Assert-Equal $metadata.SelectSingleNode("*[local-name()='version']").InnerText $PackageVersion 'Package version'
    Assert-Equal $metadata.SelectSingleNode("*[local-name()='projectUrl']").InnerText 'https://github.com/KeelMatrix/FeedFence' 'Package project URL'
    Assert-Equal $metadata.SelectSingleNode("*[local-name()='description']").InnerText 'Audits effective NuGet package sources and Package Source Mapping against the resolved dependency graph. Detects ambiguous, unmapped, inherited, and unsafe restore-source policy.' 'Package description'

    $packageType = $metadata.SelectSingleNode("*[local-name()='packageTypes']/*[local-name()='packageType']")
    Assert-Equal $packageType.GetAttribute('name') $ExpectedPackageType 'Package type'
    $repository = $metadata.SelectSingleNode("*[local-name()='repository']")
    Assert-Equal $repository.GetAttribute('type') 'git' 'Repository type'
    Assert-Equal $repository.GetAttribute('url') 'https://github.com/KeelMatrix/FeedFence' 'Repository URL'
    Assert-Equal $repository.GetAttribute('commit') $ExpectedCommit 'Repository commit'

    if ($IsPrimaryPackage) {
        Assert-Equal $metadata.SelectSingleNode("*[local-name()='authors']").InnerText 'KeelMatrix' 'Package authors'
        $license = $metadata.SelectSingleNode("*[local-name()='license']")
        Assert-Equal $license.GetAttribute('type') 'expression' 'License metadata type'
        Assert-Equal $license.InnerText 'MIT' 'License expression'
        Assert-Equal $metadata.SelectSingleNode("*[local-name()='readme']").InnerText 'README.md' 'Package README metadata'
        Assert-Equal $metadata.SelectSingleNode("*[local-name()='icon']").InnerText 'icon.png' 'Package icon metadata'
        if ($null -ne $metadata.SelectSingleNode("*[local-name()='dependencies']")) {
            throw 'The self-contained tool package must not declare nuspec dependencies.'
        }
    }
}

function Get-PngDimensions([string]$Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $signature = [byte[]](137, 80, 78, 71, 13, 10, 26, 10)
    $validSignature = $bytes.Length -ge 24
    for ($index = 0; $validSignature -and $index -lt $signature.Length; $index++) {
        $validSignature = $bytes[$index] -eq $signature[$index]
    }
    if (-not $validSignature) {
        throw "Package icon is not a valid PNG: $Path"
    }

    $widthBytes = [byte[]]$bytes[16..19]
    $heightBytes = [byte[]]$bytes[20..23]
    if ([BitConverter]::IsLittleEndian) {
        [Array]::Reverse($widthBytes)
        [Array]::Reverse($heightBytes)
    }

    return [pscustomobject]@{
        Width = [BitConverter]::ToUInt32($widthBytes, 0)
        Height = [BitConverter]::ToUInt32($heightBytes, 0)
        Size = $bytes.Length
    }
}

function New-ConsumerFixture([string]$Root, [string]$Name, [string[]]$SourceKeys) {
    $fixtureRoot = Join-Path $Root $Name
    $projectDirectory = Join-Path $fixtureRoot 'project'
    New-Item -ItemType Directory -Force -Path (Join-Path $projectDirectory 'obj') | Out-Null
    $projectPath = Join-Path $projectDirectory 'Fixture.csproj'
    Set-Content -LiteralPath $projectPath -Encoding utf8 -Value '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>'
    $assets = @{
        version = 3
        targets = @{ 'net8.0' = @{ 'Fixture.Package/1.0.0' = @{} } }
        libraries = @{ 'Fixture.Package/1.0.0' = @{ type = 'package' } }
    } | ConvertTo-Json -Depth 8 -Compress
    Set-Content -LiteralPath (Join-Path (Join-Path $projectDirectory 'obj') 'project.assets.json') -Encoding utf8 -Value $assets

    $sources = foreach ($sourceKey in $SourceKeys) {
        $sourceDirectory = Join-Path $fixtureRoot (Join-Path 'feeds' $sourceKey)
        New-Item -ItemType Directory -Force -Path $sourceDirectory | Out-Null
        "<add key='$sourceKey' value='$([System.Security.SecurityElement]::Escape($sourceDirectory))' />"
    }

    $configPath = Join-Path $fixtureRoot 'NuGet.Config'
    Set-Content -LiteralPath $configPath -Encoding utf8 -Value "<configuration><packageSources><clear />$($sources -join '')</packageSources></configuration>"
    [pscustomobject]@{ Root = $fixtureRoot; Project = $projectPath; Config = $configPath }
}

function Write-EquivalencePackage([string]$Path, [string]$Id) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    $archive = [System.IO.Compression.ZipFile]::Open($Path, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $nuspec = $archive.CreateEntry("$Id.nuspec")
        $writer = [System.IO.StreamWriter]::new($nuspec.Open(), [System.Text.Encoding]::UTF8)
        try { $writer.Write("<?xml version=`"1.0`" encoding=`"utf-8`"?><package><metadata><id>$Id</id><version>1.0.0</version><authors>Fixture</authors><description>Offline equivalence fixture.</description></metadata></package>") }
        finally { $writer.Dispose() }
    }
    finally { $archive.Dispose() }
}

function Write-EquivalenceProject([string]$Path, [string]$PackageId) {
    New-Item -ItemType Directory -Force -Path (Join-Path (Split-Path -Parent $Path) 'obj') | Out-Null
    Set-Content -LiteralPath $Path -Encoding utf8 -Value "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net8.0</TargetFramework><RestoreProjectStyle>PackageReference</RestoreProjectStyle></PropertyGroup><ItemGroup><PackageReference Include=`"$PackageId`" Version=`"1.0.0`" /></ItemGroup></Project>"
}

function Write-EquivalenceAssets([string]$ProjectPath, [string]$PackageId) {
    $assets = @{
        version = 3
        targets = @{ 'net8.0' = @{ "$PackageId/1.0.0" = @{} } }
        libraries = @{ "$PackageId/1.0.0" = @{ type = 'package' } }
    } | ConvertTo-Json -Depth 8 -Compress
    Set-Content -LiteralPath (Join-Path (Join-Path (Split-Path -Parent $ProjectPath) 'obj') 'project.assets.json') -Encoding utf8 -Value $assets
}

function Invoke-InstalledEquivalenceCases($ToolCommand, [string]$Root, [hashtable]$Environment) {
    $equivalenceRoot = Join-Path $Root 'installed-equivalence'
    $casingRoot = Join-Path $equivalenceRoot 'casing'
    $casingRepository = Join-Path $casingRoot 'repository-feed'
    $casingProject = Join-Path $casingRoot 'Fixture.csproj'
    $casingConfig = Join-Path $casingRoot 'NuGet.Config'
    $casingPackageId = 'Fixture.Casing'
    Write-EquivalencePackage (Join-Path $casingRepository "$casingPackageId.1.0.0.nupkg") $casingPackageId
    Write-EquivalenceProject $casingProject $casingPackageId
    $escapedCasingRepository = [System.Security.SecurityElement]::Escape($casingRepository)
    Set-Content -LiteralPath $casingConfig -Encoding utf8 -Value "<configuration><packageSources><clear /><add key='Repository' value='$escapedCasingRepository' /></packageSources><packageSourceMapping><packageSource key='repository'><package pattern='$casingPackageId' /></packageSource></packageSourceMapping></configuration>"
    $casingPackages = Join-Path $equivalenceRoot 'casing-packages'
    $casingRestore = Invoke-Captured 'dotnet' @('restore', $casingProject, '--configfile', $casingConfig, '--packages', $casingPackages, '--force', '--no-cache', '--disable-parallel', '--verbosity', 'minimal') $casingRoot $Environment
    Assert-ConsumerResult $casingRestore 0 'installed casing equivalence restore'
    $casingCheck = Invoke-Captured $ToolCommand.FullName @('check', $casingProject, '--config', $casingConfig, '--format', 'text') $Root $Environment
    Assert-ConsumerResult $casingCheck 0 'installed casing equivalence analyzer'
    if ($casingCheck.StandardOutput -match 'FF004') { throw 'The installed casing equivalence fixture reported contradictory FF004.' }
    Write-Output 'Installed CLI equivalence: source-key casing; restore-exit=0; analyzer-exit=0; diagnostics=none (FF004 absent).'

    $disabledRoot = Join-Path $equivalenceRoot 'disabled-exact'
    $disabledActive = Join-Path $disabledRoot 'active-feed'
    $disabledExact = Join-Path $disabledRoot 'exact-feed'
    $disabledProject = Join-Path $disabledRoot 'Fixture.csproj'
    $disabledConfig = Join-Path $disabledRoot 'NuGet.Config'
    $disabledPackageId = 'Fixture.DisabledExact'
    Write-EquivalencePackage (Join-Path $disabledActive "$disabledPackageId.1.0.0.nupkg") $disabledPackageId
    New-Item -ItemType Directory -Force -Path $disabledExact | Out-Null
    Write-EquivalenceProject $disabledProject $disabledPackageId
    $escapedDisabledExact = [System.Security.SecurityElement]::Escape($disabledExact)
    $escapedDisabledActive = [System.Security.SecurityElement]::Escape($disabledActive)
    Set-Content -LiteralPath $disabledConfig -Encoding utf8 -Value "<configuration><packageSources><clear /><add key='DisabledExact' value='$escapedDisabledExact' /><add key='ActiveWildcard' value='$escapedDisabledActive' /></packageSources><disabledPackageSources><add key='DisabledExact' value='true' /></disabledPackageSources><packageSourceMapping><packageSource key='DisabledExact'><package pattern='$disabledPackageId' /></packageSource><packageSource key='ActiveWildcard'><package pattern='*' /></packageSource></packageSourceMapping></configuration>"
    $disabledPackages = Join-Path $equivalenceRoot 'disabled-packages'
    $disabledRestore = Invoke-Captured 'dotnet' @('restore', $disabledProject, '--configfile', $disabledConfig, '--packages', $disabledPackages, '--force', '--no-cache', '--disable-parallel', '--verbosity', 'minimal') $disabledRoot $Environment
    Assert-ConsumerResult $disabledRestore 1 'installed disabled exact equivalence restore'
    Write-EquivalenceAssets $disabledProject $disabledPackageId
    $disabledCheck = Invoke-Captured $ToolCommand.FullName @('check', $disabledProject, '--config', $disabledConfig, '--format', 'text') $Root $Environment
    Assert-ConsumerResult $disabledCheck 1 'installed disabled exact equivalence analyzer'
    if ($disabledCheck.StandardOutput -notmatch 'FF003') { throw 'The installed disabled exact equivalence fixture did not report FF003.' }
    if ($disabledCheck.StandardOutput -match 'could not be reconciled') { throw 'The installed disabled exact equivalence fixture reported a reconciliation exception.' }
    Write-Output 'Installed CLI equivalence: disabled exact + active wildcard; restore-exit=1; analyzer-exit=1; diagnostics=FF003 (no reconciliation exception).'
}

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection.Metadata;
using System.Text;

public static class FeedFencePdbInspection
{
    private static readonly Guid SourceLinkKind = new("CC110556-A091-4D38-9FEC-25AB9A351A6A");

    public static string ReadSourceLink(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        var reader = provider.GetMetadataReader();
        foreach (var handle in reader.GetCustomDebugInformation(EntityHandle.ModuleDefinition))
        {
            var information = reader.GetCustomDebugInformation(handle);
            if (reader.GetGuid(information.Kind) == SourceLinkKind)
            {
                return Encoding.UTF8.GetString(reader.GetBlobBytes(information.Value));
            }
        }

        return null;
    }
}
'@

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

    New-Item -ItemType Directory -Force -Path $shippingOutput | Out-Null
    $existingArtifacts = @(Get-ChildItem -LiteralPath $shippingOutput -File | Where-Object { $_.Extension -in '.nupkg', '.snupkg' })
    if ($existingArtifacts.Count -ne 0) {
        throw "Artifact directory must not contain stale package artifacts: $($existingArtifacts.Name -join ', ')"
    }

    $guardProbe = Invoke-Captured 'dotnet' @(
        'msbuild',
        $shippingProject,
        '-t:ValidateSensitivePackageInputs',
        '-p:FeedFenceSensitivePackGuardProbe=.env.feedfence-pack-guard-probe',
        '-nologo'
    ) $repositoryRoot @{}
    $guardProbeOutput = $guardProbe.StandardOutput + $guardProbe.StandardError
    if ($guardProbe.ExitCode -eq 0 -or $guardProbeOutput -notmatch 'Sensitive pack input rejected') {
        throw 'The MSBuild sensitive-pack guard did not reject the synthetic sensitive input.'
    }
    Write-Output 'MSBuild sensitive-pack guard rejection: PASS (.env probe rejected before nuspec generation).'

    Invoke-Checked 'dotnet' @('pack', $shippingProject, '-c', $Configuration, '--no-restore', "-p:PackageVersion=$PackageVersion", '--output', $shippingOutput)
    $artifacts = @(Get-ChildItem -LiteralPath $shippingOutput -File | Where-Object { $_.Extension -in '.nupkg', '.snupkg' } | Select-Object -ExpandProperty Name)
    $expected = @("KeelMatrix.FeedFence.$PackageVersion.nupkg", "KeelMatrix.FeedFence.$PackageVersion.snupkg")
    $unexpected = @($artifacts | Where-Object { $_ -notin $expected })
    $missing = @($expected | Where-Object { $_ -notin $artifacts })
    if ($unexpected.Count -ne 0 -or $missing.Count -ne 0) {
        throw "Shipping artifact allowlist failed. Expected: $($expected -join ', '); Actual: $($artifacts -join ', ')"
    }

    Write-Output "Shipping artifact allowlist: $($artifacts -join ', ')"

    $nupkgPath = Join-Path $shippingOutput "KeelMatrix.FeedFence.$PackageVersion.nupkg"
    $snupkgPath = Join-Path $shippingOutput "KeelMatrix.FeedFence.$PackageVersion.snupkg"
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $nupkgStaticEntries = @(
        '_rels/.rels',
        '[Content_Types].xml',
        'icon.png',
        'KeelMatrix.FeedFence.nuspec',
        'LICENSE',
        'README.md',
        'tools/net8.0/any/DotnetToolSettings.xml',
        'tools/net8.0/any/KeelMatrix.FeedFence.deps.json',
        'tools/net8.0/any/KeelMatrix.FeedFence.dll',
        'tools/net8.0/any/KeelMatrix.FeedFence.pdb',
        'tools/net8.0/any/KeelMatrix.FeedFence.runtimeconfig.json',
        'tools/net8.0/any/KeelMatrix.Telemetry.dll',
        'tools/net8.0/any/NuGet.Common.dll',
        'tools/net8.0/any/NuGet.Configuration.dll',
        'tools/net8.0/any/NuGet.Frameworks.dll',
        'tools/net8.0/any/System.Security.Cryptography.ProtectedData.dll'
    )
    $snupkgStaticEntries = @(
        '_rels/.rels',
        '[Content_Types].xml',
        'KeelMatrix.FeedFence.nuspec',
        'tools/net8.0/any/KeelMatrix.FeedFence.pdb'
    )
    Assert-ArchiveAllowlist $nupkgPath $nupkgStaticEntries '.nupkg'
    Assert-ArchiveAllowlist $snupkgPath $snupkgStaticEntries '.snupkg'

    $expectedCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $expectedCommit -notmatch '^[0-9a-f]{40}$') {
        throw 'Could not resolve the repository commit for package metadata validation.'
    }
    Assert-NuspecMetadata $nupkgPath 'KeelMatrix.FeedFence.nuspec' 'DotnetTool' $true $expectedCommit
    Assert-NuspecMetadata $snupkgPath 'KeelMatrix.FeedFence.nuspec' 'SymbolsPackage' $false $expectedCommit

    [xml]$toolSettings = Get-ArchiveEntryText $nupkgPath 'tools/net8.0/any/DotnetToolSettings.xml'
    $toolCommandMetadata = $toolSettings.SelectSingleNode("/*[local-name()='DotNetCliTool']/*[local-name()='Commands']/*[local-name()='Command']")
    Assert-Equal $toolCommandMetadata.GetAttribute('Name') 'feedfence' 'Tool command name'
    Assert-Equal $toolCommandMetadata.GetAttribute('EntryPoint') 'KeelMatrix.FeedFence.dll' 'Tool entry point'
    Assert-Equal $toolCommandMetadata.GetAttribute('Runner') 'dotnet' 'Tool runner'

    $runtimeConfig = Get-ArchiveEntryText $nupkgPath 'tools/net8.0/any/KeelMatrix.FeedFence.runtimeconfig.json' | ConvertFrom-Json -AsHashtable
    Assert-Equal $runtimeConfig.runtimeOptions.tfm 'net8.0' 'Runtime target framework'
    Assert-Equal $runtimeConfig.runtimeOptions.framework.name 'Microsoft.NETCore.App' 'Runtime framework'
    Assert-Equal $runtimeConfig.runtimeOptions.framework.version '8.0.0' 'Runtime framework version'

    $deps = Get-ArchiveEntryText $nupkgPath 'tools/net8.0/any/KeelMatrix.FeedFence.deps.json' | ConvertFrom-Json -AsHashtable
    Assert-Equal $deps.runtimeTarget.name '.NETCoreApp,Version=v8.0' 'Dependency runtime target'
    $expectedRuntimeLibraries = @(
        "KeelMatrix.FeedFence/$PackageVersion",
        'KeelMatrix.Telemetry/0.1.1',
        'NuGet.Common/7.9.0',
        'NuGet.Configuration/7.9.0',
        'NuGet.Frameworks/7.9.0',
        'System.Security.Cryptography.ProtectedData/8.0.0'
    ) | Sort-Object
    $actualRuntimeLibraries = @($deps.libraries.Keys | Sort-Object)
    Assert-Equal ($actualRuntimeLibraries -join "`n") ($expectedRuntimeLibraries -join "`n") 'Runtime dependency asset set'

    $projectDirectory = Split-Path -Parent $shippingProject
    $projectReadme = Join-Path $projectDirectory 'README.md'
    $licensePath = Join-Path $repositoryRoot 'LICENSE'
    $iconPath = Join-Path $repositoryRoot 'icon.png'
    [xml]$projectDocument = Get-Content -Raw -LiteralPath $shippingProject
    $packageIcon = $projectDocument.SelectSingleNode("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='PackageIcon']")
    Assert-Equal $packageIcon.InnerText 'icon.png' 'Project package icon metadata'
    $iconPackItem = $projectDocument.SelectSingleNode("/*[local-name()='Project']/*[local-name()='ItemGroup']/*[local-name()='None'][@Include='..\..\icon.png']")
    if ($null -eq $iconPackItem) { throw 'The shipping project does not resolve its package icon from the repository root.' }
    Assert-Equal $iconPackItem.GetAttribute('Pack') 'true' 'Icon pack item'
    Assert-Equal $iconPackItem.GetAttribute('PackagePath') 'icon.png' 'Icon package path'
    $resolvedIconPath = [System.IO.Path]::GetFullPath((Join-Path $projectDirectory $iconPackItem.GetAttribute('Include')))
    Assert-Equal $resolvedIconPath ([System.IO.Path]::GetFullPath($iconPath)) 'Resolved icon path'
    & git -C $repositoryRoot ls-files --error-unmatch -- icon.png *> $null
    if ($LASTEXITCODE -ne 0) { throw 'The repository-root icon.png is not tracked.' }
    $iconDimensions = Get-PngDimensions $iconPath
    if ($iconDimensions.Width -ne 512 -or $iconDimensions.Height -ne 512 -or $iconDimensions.Size -gt (200 * 1024)) {
        throw "Package icon contract failed: $($iconDimensions.Width)x$($iconDimensions.Height), $($iconDimensions.Size) bytes."
    }
    Write-Output "Icon path set: PASS ($iconPath; repository-root file resolved by src/KeelMatrix.FeedFence/KeelMatrix.FeedFence.csproj; 512x512; $($iconDimensions.Size) bytes)."
    Assert-ArchiveEntryMatchesFile $nupkgPath 'README.md' $projectReadme 'Project-local README'
    Assert-ArchiveEntryMatchesFile $nupkgPath 'LICENSE' $licensePath 'Repository license'
    Assert-ArchiveEntryMatchesFile $nupkgPath 'icon.png' $iconPath 'Repository-root icon'

    $nupkgPdbHash = Get-BytesSha256 (Get-ArchiveEntryBytes $nupkgPath 'tools/net8.0/any/KeelMatrix.FeedFence.pdb')
    $snupkgPdbBytes = Get-ArchiveEntryBytes $snupkgPath 'tools/net8.0/any/KeelMatrix.FeedFence.pdb'
    Assert-Equal (Get-BytesSha256 $snupkgPdbBytes) $nupkgPdbHash 'Package/symbol PDB byte identity'
    $sourceLinkText = [FeedFencePdbInspection]::ReadSourceLink($snupkgPdbBytes)
    if ([string]::IsNullOrWhiteSpace($sourceLinkText)) { throw 'The symbol package PDB has no SourceLink data.' }
    $sourceLink = $sourceLinkText | ConvertFrom-Json -AsHashtable
    Assert-Equal $sourceLink.documents.Count 1 'SourceLink document mapping count'
    Assert-Equal $sourceLink.documents['/_/*'] "https://raw.githubusercontent.com/KeelMatrix/FeedFence/$expectedCommit/*" 'SourceLink repository mapping'
    Write-Output "Package metadata contract: PASS (ID, version, tool command, net8.0 runtime assets, project README, MIT license, repository commit, and SourceLink mapping $expectedCommit)."

    $nuspec = Get-ArchiveEntryText $nupkgPath 'KeelMatrix.FeedFence.nuspec'
    Assert-NoMachineLocalPdbPaths @($nupkgPath, $snupkgPath)

    $consumerRoot = Join-Path $outputRoot 'consumer'
    $localSource = $shippingOutput
    $toolPath = Join-Path $consumerRoot 'tool'
    $isolatedPackages = Join-Path $consumerRoot 'packages'
    $isolatedHome = Join-Path $consumerRoot 'dotnet-home'
    $isolatedHttpCache = Join-Path $consumerRoot 'http-cache'
    $isolatedPluginsCache = Join-Path $consumerRoot 'plugins-cache'
    $isolatedScratch = Join-Path $consumerRoot 'nuget-scratch'
    New-Item -ItemType Directory -Force -Path $toolPath, $isolatedPackages, $isolatedHome, $isolatedHttpCache, $isolatedPluginsCache, $isolatedScratch | Out-Null

    $consumerConfig = Join-Path $consumerRoot 'NuGet.Config'
    Set-Content -LiteralPath $consumerConfig -Encoding utf8 -Value "<configuration><packageSources><clear /><add key='artifact-under-test' value='$([System.Security.SecurityElement]::Escape($localSource))' /></packageSources><packageSourceMapping><packageSource key='artifact-under-test'><package pattern='KeelMatrix.FeedFence' /></packageSource></packageSourceMapping></configuration>"
    $consumerEnvironment = @{
        NUGET_PACKAGES = $isolatedPackages
        NUGET_HTTP_CACHE_PATH = $isolatedHttpCache
        NUGET_PLUGINS_CACHE_PATH = $isolatedPluginsCache
        NUGET_SCRATCH = $isolatedScratch
        DOTNET_CLI_HOME = $isolatedHome
        DOTNET_NOLOGO = '1'
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        KEELMATRIX_NO_TELEMETRY = '1'
    }

    $install = Invoke-Captured 'dotnet' @('tool', 'install', '--tool-path', $toolPath, '--configfile', $consumerConfig, '--version', $PackageVersion, '--no-cache', 'KeelMatrix.FeedFence') $consumerRoot $consumerEnvironment
    Assert-ConsumerResult $install 0 'tool install'
    if ($install.StandardError.Length -ne 0) { Write-Output "Consumer tool install stderr: $($install.StandardError.Trim())" }
    Write-Output 'Consumer source isolation: PASS (exact .nupkg artifact only; package-source mapping restricts KeelMatrix.FeedFence to that artifact directory; isolated package/HTTP/plugin/scratch caches; user global-package cache not referenced).'

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

    Invoke-InstalledEquivalenceCases $toolCommand $consumerRoot $consumerEnvironment

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
    Write-Output "PASS: exact artifacts inspected and consumed: $($expected -join ', ')."
}
finally {
    if (Test-Path -LiteralPath $outputRoot) {
        Remove-Item -LiteralPath $outputRoot -Recurse -Force
    }
}
