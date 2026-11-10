$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $repositoryRoot 'build/WebsiteMetadata.ps1')
$scratchParent = [IO.Path]::GetTempPath()
$testRoot = Join-Path $scratchParent ('feedfence-website-' + [guid]::NewGuid().ToString('N'))
$manifestPath = Join-Path $testRoot 'keelmatrix.website.json'
$manifest = '{"schemaVersion":1,"packages":{"KeelMatrix.FeedFence":{"visibility":"public-product","role":"primary"}}}'
$package = @{
    PackageId = 'KeelMatrix.FeedFence'; PackageTags = 'nuget;keelmatrix-public-product;keelmatrix-primary'
    PackageProjectUrl = 'https://github.com/KeelMatrix/FeedFence'
    RepositoryUrl = 'https://github.com/KeelMatrix/FeedFence'; RepositoryType = 'git'
}
$count = 0
function Assert-Rejected([string]$Label, [scriptblock]$Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw "Website metadata accepted $Label." }
    $script:count++
}
function Write-TestPackage([string]$Nuspec, [switch]$Duplicate) {
    $path = Join-Path $testRoot 'fixture.nupkg'
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $(if ($Duplicate) { @('fixture.nuspec', 'other.nuspec') } else { @('fixture.nuspec') })) {
            $writer = [IO.StreamWriter]::new($zip.CreateEntry($name).Open())
            try { $writer.Write($Nuspec) } finally { $writer.Dispose() }
        }
    }
    finally { $zip.Dispose() }
    return $path
}
function Invoke-MetadataProcess {
    $start = [Diagnostics.ProcessStartInfo]::new('pwsh')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-File', (Join-Path $testRoot 'scripts/Invoke-WebsiteMetadata.ps1'))) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) { $process.Kill($true); throw 'Metadata process timed out.' }
        return @{ ExitCode = $process.ExitCode; Output = $stdout.Result + $stderr.Result }
    }
    finally { $process.Dispose() }
}
try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    Set-Content -LiteralPath $manifestPath -Value $manifest
    Assert-WebsiteMetadata $manifestPath @($package)
    $spaced = $package.Clone(); $spaced.PackageTags = "nuget keelmatrix-public-product`tkeelmatrix-primary"
    Assert-WebsiteMetadata $manifestPath @($spaced)
    Assert-Rejected 'missing manifest' { Assert-WebsiteMetadata (Join-Path $testRoot 'missing.json') @($package) }
    foreach ($bad in @(
            '{', $manifest.Replace('1,', '1,/*comment*/'), $manifest.Replace('1,', '1,\n'),
            $manifest.Replace('1,', '1,"schemaVersion":1,'), $manifest.Replace('schemaVersion', 'SchemaVersion'),
            $manifest.Replace('1,', '2,'), $manifest.Replace('1,', '"1",'), $manifest.Replace('1,', '1.0,'),
            $manifest.Replace('1,', 'null,'), $manifest.Replace('1,', 'true,'), $manifest.Replace('1,', '1,"extra":0,'),
            '{"schemaVersion":1}', '{"schemaVersion":1,"packages":[]}', '{"schemaVersion":1,"packages":{}}',
            $manifest.Replace('KeelMatrix.FeedFence', 'keelmatrix.feedfence'), $manifest.Replace('KeelMatrix.FeedFence', 'KeelMatrix.Other'),
            $manifest.Replace('public-product', 'PUBLIC-PRODUCT'), $manifest.Replace('public-product', 'unknown'),
            $manifest.Replace('"visibility":"public-product"', '"visibility":"public-product","visibility":"public-product"'),
            $manifest.Replace('"visibility":"public-product"', '"Visibility":"public-product"'),
            $manifest.Replace('"primary"', 'null'), $manifest.Replace('"primary"', 'false'), $manifest.Replace('primary', 'Primary'),
            $manifest.Replace('primary', 'unknown'), $manifest.Replace('"role":"primary"', '"role":"primary","role":"primary"'),
            $manifest.Replace('"role":"primary"', '"extra":true'),
            '{"schemaVersion":1,"packages":{"KeelMatrix.FeedFence":{"visibility":"public-product","role":"primary"},"KeelMatrix.FeedFence":{"visibility":"public-product","role":"primary"}}}',
            '{"schemaVersion":1,"packages":{"KeelMatrix.FeedFence":{"visibility":"public-product","role":"primary"},"keelmatrix.feedfence":{"visibility":"public-product","role":"primary"}}}'
        )) {
        Set-Content -LiteralPath $manifestPath -Value $bad
        Assert-Rejected 'malformed manifest' { Assert-WebsiteMetadata $manifestPath @($package) }
    }
    Set-Content -LiteralPath $manifestPath -Value $manifest
    Assert-Rejected 'no projects' { Assert-WebsiteMetadata $manifestPath @() }
    Assert-Rejected 'duplicate projects' { Assert-WebsiteMetadata $manifestPath @($package, $package) }
    foreach ($tags in @('', 'nuget', 'keelmatrix-primary', 'keelmatrix-public-product',
            'keelmatrix-internal-package;keelmatrix-primary', 'keelmatrix-public-product;keelmatrix-component')) {
        $changed = $package.Clone(); $changed.PackageTags = $tags
        Assert-Rejected 'missing or disagreeing source classification' { Assert-WebsiteMetadata $manifestPath @($changed) }
    }
    foreach ($sentinel in @('keelmatrix-public-product', 'keelmatrix-primary')) {
        foreach ($replacement in @($sentinel.ToUpperInvariant(), "$sentinel;$sentinel", "$sentinel $sentinel", "$sentinel;$($sentinel.ToUpperInvariant())",
                $(if ($sentinel.EndsWith('primary')) { "$sentinel;keelmatrix-component" } else { "$sentinel;keelmatrix-internal-package" }))) {
            $changed = $package.Clone(); $changed.PackageTags = $package.PackageTags.Replace($sentinel, $replacement)
            Assert-Rejected 'duplicate, conflicting or wrong-case source sentinel' { Assert-WebsiteMetadata $manifestPath @($changed) }
        }
    }
    foreach ($field in @('PackageProjectUrl', 'RepositoryUrl', 'RepositoryType')) {
        foreach ($value in @('', 'https://github.com/Other/FeedFence', 'https://github.com/KeelMatrix/Other',
                'https://github.com.evil/KeelMatrix/FeedFence', 'http://github.com/KeelMatrix/FeedFence',
                'https://github.com/KeelMatrix/FeedFence?query', 'https://github.com/KeelMatrix/FeedFence#fragment',
                'https://github.com/keelmatrix/feedfence', 'https://keelmatrix.com', 'https://github.com/KeelMatrix/FeedFence/')) {
            $changed = $package.Clone(); $changed[$field] = $value
            Assert-Rejected 'noncanonical source URL/type' { Assert-WebsiteMetadata $manifestPath @($changed) }
        }
    }
    $component = $package.Clone(); $component.PackageId = 'KeelMatrix.FeedFence.Component'
    $component.PackageTags = $component.PackageTags.Replace('keelmatrix-primary', 'keelmatrix-component')
    $family = '{"schemaVersion":1,"packages":{"KeelMatrix.FeedFence":{"visibility":"public-product","role":"primary"},"KeelMatrix.FeedFence.Component":{"visibility":"public-product","role":"component"}}}'
    Set-Content -LiteralPath $manifestPath -Value $family
    Assert-WebsiteMetadata $manifestPath @($package, $component)
    foreach ($role in @('primary', 'component')) {
        $changed = $package.Clone(); $changed.PackageTags = $changed.PackageTags.Replace('keelmatrix-primary', "keelmatrix-$role")
        $sibling = $component.Clone(); $sibling.PackageTags = $sibling.PackageTags.Replace('keelmatrix-component', "keelmatrix-$role")
        Set-Content -LiteralPath $manifestPath -Value ($family.Replace('"primary"', "`"$role`"").Replace('"component"', "`"$role`""))
        Assert-Rejected 'zero or multiple primaries with matching source tags' { Assert-WebsiteMetadata $manifestPath @($changed, $sibling) }
    }
    $internal = $package.Clone(); $internal.PackageTags = $internal.PackageTags.Replace('public-product', 'internal-package')
    Set-Content -LiteralPath $manifestPath -Value ($family.Replace('"KeelMatrix.FeedFence":{"visibility":"public-product"', '"KeelMatrix.FeedFence":{"visibility":"internal-package"'))
    Assert-Rejected 'public family with only an internal primary' { Assert-WebsiteMetadata $manifestPath @($internal, $component) }
    Set-Content -LiteralPath $manifestPath -Value ($manifest.Replace('public-product', 'internal-package'))
    Assert-WebsiteMetadata $manifestPath @($internal)
    $nuspec = '<package><metadata><id>KeelMatrix.FeedFence</id><tags>nuget keelmatrix-public-product keelmatrix-primary</tags><projectUrl>https://github.com/KeelMatrix/FeedFence</projectUrl><repository type="git" url="https://github.com/KeelMatrix/FeedFence" /></metadata></package>'
    Assert-WebsiteBuiltPackage (Write-TestPackage $nuspec) @($package)
    foreach ($bad in @($nuspec.Replace('KeelMatrix.FeedFence</id>', 'keelmatrix.feedfence</id>'),
            $nuspec.Replace('<tags>', '<tags>keelmatrix-primary '), $nuspec.Replace('keelmatrix-primary', 'KeelMatrix-Primary'),
            $nuspec.Replace('keelmatrix-primary', 'keelmatrix-component'), $nuspec.Replace('nuget ', ''),
            $nuspec.Replace('keelmatrix-public-product', 'keelmatrix-public-product keelmatrix-internal-package'),
            $nuspec.Replace('keelmatrix-primary', ''), $nuspec.Replace('<tags>', '<tags/><tags>'),
            $nuspec.Replace('<projectUrl>', '<projectUrl/><projectUrl>'),
            $nuspec.Replace('https://github.com/KeelMatrix/FeedFence', 'https://github.com/Other/FeedFence'),
            $nuspec.Replace('type="git"', 'type="svn"'), '<package/>', '<!DOCTYPE package SYSTEM "file:///invalid"><package/>')) {
        Assert-Rejected 'built nuspec drift' { Assert-WebsiteBuiltPackage (Write-TestPackage $bad) @($package) }
    }
    Assert-Rejected 'multiple nuspecs' { Assert-WebsiteBuiltPackage (Write-TestPackage $nuspec -Duplicate) @($package) }

    # Exercise evaluated inherited properties, excluded tests and the actual native entry point.
    foreach ($directory in @('src', 'tests', 'scripts', 'build')) { New-Item -ItemType Directory -Path (Join-Path $testRoot $directory) | Out-Null }
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'build/WebsiteMetadata.ps1') -Destination (Join-Path $testRoot 'build')
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'scripts/Invoke-WebsiteMetadata.ps1') -Destination (Join-Path $testRoot 'scripts')
    Set-Content -LiteralPath (Join-Path $testRoot 'Directory.Build.props') -Value '<Project><PropertyGroup><IsPackable>true</IsPackable><PackageId>KeelMatrix.FeedFence</PackageId><PackageTags>nuget;keelmatrix-public-product;keelmatrix-primary</PackageTags><PackageProjectUrl>https://github.com/KeelMatrix/FeedFence</PackageProjectUrl><RepositoryUrl>https://github.com/KeelMatrix/FeedFence</RepositoryUrl><RepositoryType>git</RepositoryType></PropertyGroup></Project>'
    Set-Content -LiteralPath (Join-Path $testRoot 'src/Fixture.csproj') -Value '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>'
    Set-Content -LiteralPath (Join-Path $testRoot 'tests/Fixture.csproj') -Value '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup></Project>'
    Set-Content -LiteralPath $manifestPath -Value $manifest
    $result = Invoke-MetadataProcess
    if ($result.ExitCode -ne 0 -or $result.Output -notmatch '1 packable project') { throw "Inherited metadata control failed: $($result.Output)" }
    Set-Content -LiteralPath $manifestPath -Value ($manifest.Replace('primary', 'component'))
    $result = Invoke-MetadataProcess
    if ($result.ExitCode -eq 0 -or $result.Output -notmatch 'disagrees') { throw 'Metadata rejection did not reach native process exit.' }
    Write-Output "Website metadata self-test: PASS ($count rejection controls; inherited source, family, built package and native exit controls)."
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath($scratchParent).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe metadata fixture cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
