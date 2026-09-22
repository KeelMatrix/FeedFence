param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$toolDll = Join-Path (Join-Path (Join-Path (Join-Path (Join-Path $repositoryRoot 'src') 'KeelMatrix.FeedFence') 'bin') $Configuration) (Join-Path 'net8.0' 'KeelMatrix.FeedFence.dll')
$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('feedfence-platform-' + [guid]::NewGuid().ToString('N'))

if ($IsWindows) {
    $platform = 'Windows'
    $configRelativePath = Join-Path (Join-Path 'AppData' 'Roaming') (Join-Path 'NuGet' 'NuGet.Config')
}
elseif ($IsLinux) {
    $platform = 'Linux'
    $configRelativePath = Join-Path '.nuget' (Join-Path 'NuGet' 'NuGet.Config')
}
elseif ($IsMacOS) {
    $platform = 'macOS'
    $configRelativePath = Join-Path '.nuget' (Join-Path 'NuGet' 'NuGet.Config')
}
else {
    throw 'This fixture runner supports Windows, Linux, and macOS only.'
}

function Invoke-Tool([string[]]$Arguments, [string]$WorkingDirectory) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::Start($startInfo)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    [pscustomobject]@{ ExitCode = $process.ExitCode; StandardOutput = $stdout.Result; StandardError = $stderr.Result }
}

try {
    if (-not (Test-Path -LiteralPath $toolDll)) {
        throw "Build the Release shipping project before running platform fixtures: $toolDll"
    }

    $projectDirectory = Join-Path $fixtureRoot 'project'
    $objDirectory = Join-Path $projectDirectory 'obj'
    $feedDirectory = Join-Path (Join-Path $fixtureRoot 'feeds') 'local'
    $configPath = Join-Path $fixtureRoot $configRelativePath
    New-Item -ItemType Directory -Force -Path $projectDirectory, $objDirectory, $feedDirectory, (Split-Path -Parent $configPath) | Out-Null

    $projectPath = Join-Path $projectDirectory 'Fixture.csproj'
    Set-Content -LiteralPath $projectPath -Encoding utf8 -Value '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>'
    $assets = @{
        version = 3
        targets = @{ 'net8.0' = @{ 'Fixture.Package/1.0.0' = @{} } }
        libraries = @{ 'Fixture.Package/1.0.0' = @{ type = 'package' } }
    } | ConvertTo-Json -Depth 8 -Compress
    Set-Content -LiteralPath (Join-Path (Join-Path $projectDirectory 'obj') 'project.assets.json') -Encoding utf8 -Value $assets
    $fileUri = [Uri]::new($feedDirectory).AbsoluteUri
    Set-Content -LiteralPath $configPath -Encoding utf8 -Value "<configuration><packageSources><clear /><add key='LocalFeed' value='$fileUri' /></packageSources><packageSourceMapping><packageSource key='LocalFeed'><package pattern='Fixture.*' /></packageSource></packageSourceMapping></configuration>"

    $previousOptOut = $env:KEELMATRIX_NO_TELEMETRY
    $env:KEELMATRIX_NO_TELEMETRY = '1'
    $result = Invoke-Tool @($toolDll, 'check', $projectPath, '--config', $configPath, '--format', 'json') $fixtureRoot
    if ($result.ExitCode -ne 0 -or $result.StandardError.Length -ne 0) {
        throw "Platform fixture failed: exit=$($result.ExitCode); stderr=$($result.StandardError.Trim())"
    }

    $report = $result.StandardOutput | ConvertFrom-Json
    if ($report.schemaVersion -ne 1 -or $report.format -ne 'json' -or $report.exitCode -ne 0) {
        throw 'Platform fixture produced an unexpected JSON contract.'
    }

    Write-Output "Platform fixture: $platform"
    Write-Output "Config location: $configRelativePath"
    Write-Output "File feed URI: $fileUri"
    Write-Output "Path separator: $([System.IO.Path]::DirectorySeparatorChar)"
    Write-Output 'PASS: platform-specific configuration, file-feed URL, path, casing, and JSON fixture.'
}
finally {
    if ($null -ne $previousOptOut) { $env:KEELMATRIX_NO_TELEMETRY = $previousOptOut } else { Remove-Item Env:KEELMATRIX_NO_TELEMETRY -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
}
