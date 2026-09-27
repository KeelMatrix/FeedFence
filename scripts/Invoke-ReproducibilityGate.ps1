param(
    [string]$Configuration = 'Release',
    [string]$PackageVersion = '0.1.0'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$shippingProject = Join-Path $repositoryRoot 'src/KeelMatrix.FeedFence/KeelMatrix.FeedFence.csproj'
$normalizer = Join-Path $repositoryRoot 'scripts/Normalize-NuGetArchive.ps1'
$scratchParent = if (-not [string]::IsNullOrWhiteSpace($env:PAPERCLIP_RUN_SCRATCH_DIR)) {
    $env:PAPERCLIP_RUN_SCRATCH_DIR
}
else {
    [IO.Path]::GetTempPath()
}
$scratchRoot = Join-Path $scratchParent ('feedfence-repro-' + [guid]::NewGuid().ToString('N'))

function Invoke-Checked([string]$FilePath, [string[]]$Arguments, [string]$WorkingDirectory) {
    Push-Location -LiteralPath $WorkingDirectory
    try {
        & $FilePath @Arguments
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    if ($exitCode -ne 0) {
        throw "Command failed with exit code ${exitCode}: $FilePath $($Arguments -join ' ')"
    }
}

function Get-FileDigest([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-ZipManifest([string]$ArchivePath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        foreach ($entry in @($archive.Entries | Sort-Object FullName)) {
            $memory = [IO.MemoryStream]::new()
            try {
                $stream = $entry.Open()
                try { $stream.CopyTo($memory) } finally { $stream.Dispose() }
                $bytes = $memory.ToArray()
                [pscustomobject]@{
                    Name = $entry.FullName
                    Length = $bytes.Length
                    Hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
                    LastWriteTime = $entry.LastWriteTime.DateTime.ToString('O')
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

function Assert-Same([string]$Label, [string[]]$Actual, [string[]]$Expected) {
    if (($Actual -join "`n") -cne ($Expected -join "`n")) {
        throw "$Label differs.`nExpected:`n$($Expected -join "`n")`nActual:`n$($Actual -join "`n")"
    }
    Write-Output "${Label}: PASS"
}

function Get-ArchiveManifestLines([string]$ArchivePath) {
    return @(Get-ZipManifest $ArchivePath | ForEach-Object { "$($_.Name)|$($_.Length)|$($_.Hash)" })
}

function Assert-NormalizedArchive([string]$ArchivePath) {
    $manifest = @(Get-ZipManifest $ArchivePath)
    if ($manifest.Count -eq 0 -or @($manifest | Where-Object { $_.LastWriteTime -ne '1980-01-01T00:00:00.0000000' }).Count -ne 0) {
        throw "Archive '$ArchivePath' does not have the required normalized ZIP timestamps."
    }
    $core = @($manifest | Where-Object { $_.Name -cmatch '^package/services/metadata/core-properties/[0-9a-f]{32}\.psmdcp$' })
    if ($core.Count -ne 1) {
        throw "Archive '$ArchivePath' does not have exactly one deterministic core-properties entry."
    }
}

function Pack-CleanClone([string]$ClonePath, [string]$Label) {
    Write-Output "[$Label] locked restore"
    Invoke-Checked 'dotnet' @('restore', (Join-Path $ClonePath 'KeelMatrix.FeedFence.sln'), '--configfile', (Join-Path $ClonePath 'NuGet.config'), '--no-cache', '--locked-mode') $ClonePath

    $output = Join-Path $ClonePath 'repro-artifacts'
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    Write-Output "[$Label] deterministic pack"
    Invoke-Checked 'dotnet' @('pack', (Join-Path $ClonePath 'src/KeelMatrix.FeedFence/KeelMatrix.FeedFence.csproj'), '-c', $Configuration, '--no-restore', "-p:PackageVersion=$PackageVersion", '--output', $output) $ClonePath

    $nupkg = Join-Path $output "KeelMatrix.FeedFence.$PackageVersion.nupkg"
    $snupkg = Join-Path $output "KeelMatrix.FeedFence.$PackageVersion.snupkg"
    Invoke-Checked 'pwsh' @('-NoProfile', '-File', (Join-Path $ClonePath 'scripts/Normalize-NuGetArchive.ps1'), '-Path', $nupkg) $ClonePath
    Invoke-Checked 'pwsh' @('-NoProfile', '-File', (Join-Path $ClonePath 'scripts/Normalize-NuGetArchive.ps1'), '-Path', $snupkg) $ClonePath
    Assert-NormalizedArchive $nupkg
    Assert-NormalizedArchive $snupkg

    [pscustomobject]@{
        Label = $Label
        Root = $ClonePath
        Nupkg = $nupkg
        Snupkg = $snupkg
        Dll = Join-Path $ClonePath "src/KeelMatrix.FeedFence/bin/$Configuration/net8.0/KeelMatrix.FeedFence.dll"
        Pdb = Join-Path $ClonePath "src/KeelMatrix.FeedFence/bin/$Configuration/net8.0/KeelMatrix.FeedFence.pdb"
    }
}

& git -C $repositoryRoot diff --quiet
if ($LASTEXITCODE -ne 0) {
    throw 'Reproducibility gate requires a clean worktree so clean clones contain the exact candidate SHA.'
}
$candidateSha = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($candidateSha -notmatch '^[0-9a-f]{40}$') {
    throw 'Could not resolve the candidate commit for reproducibility validation.'
}

try {
    New-Item -ItemType Directory -Force -Path $scratchRoot | Out-Null
    $shortRoot = Join-Path $scratchRoot 'a'
    $longRoot = Join-Path $scratchRoot 'b-long'
    Invoke-Checked 'git' @('clone', '--quiet', '--no-local', $repositoryRoot, $shortRoot) $scratchRoot
    Invoke-Checked 'git' @('clone', '--quiet', '--no-local', $repositoryRoot, $longRoot) $scratchRoot

    $first = Pack-CleanClone $shortRoot 'short-root first run'
    $second = Pack-CleanClone $longRoot 'long-root first run'

    Assert-Same 'Candidate SHA' @((& git -C $shortRoot rev-parse HEAD).Trim(), (& git -C $longRoot rev-parse HEAD).Trim()) @($candidateSha, $candidateSha)
    Assert-Same 'Shipping DLL bytes across checkout roots' @((Get-FileDigest $first.Dll), (Get-FileDigest $second.Dll)) @((Get-FileDigest $first.Dll), (Get-FileDigest $first.Dll))
    Assert-Same 'Shipping PDB bytes across checkout roots' @((Get-FileDigest $first.Pdb), (Get-FileDigest $second.Pdb)) @((Get-FileDigest $first.Pdb), (Get-FileDigest $first.Pdb))
    Assert-Same 'Normalized nupkg entry contents across checkout roots' (Get-ArchiveManifestLines $second.Nupkg) (Get-ArchiveManifestLines $first.Nupkg)
    Assert-Same 'Normalized snupkg entry contents across checkout roots' (Get-ArchiveManifestLines $second.Snupkg) (Get-ArchiveManifestLines $first.Snupkg)

    $repeat = Pack-CleanClone $shortRoot 'short-root repeated run'
    Assert-Same 'Shipping DLL bytes across repeated pack' @((Get-FileDigest $repeat.Dll), (Get-FileDigest $first.Dll)) @((Get-FileDigest $first.Dll), (Get-FileDigest $first.Dll))
    Assert-Same 'Shipping PDB bytes across repeated pack' @((Get-FileDigest $repeat.Pdb), (Get-FileDigest $first.Pdb)) @((Get-FileDigest $first.Pdb), (Get-FileDigest $first.Pdb))
    Assert-Same 'Normalized nupkg entry contents across repeated pack' (Get-ArchiveManifestLines $repeat.Nupkg) (Get-ArchiveManifestLines $first.Nupkg)
    Assert-Same 'Normalized snupkg entry contents across repeated pack' (Get-ArchiveManifestLines $repeat.Snupkg) (Get-ArchiveManifestLines $first.Snupkg)
    Write-Output "Reproducibility claim: PASS for candidate $candidateSha (DLL/PDB bytes and normalized package entry names/bytes are invariant across two clean roots and repeated pack; raw ZIP container byte identity is not claimed)."
}
finally {
    if (Test-Path -LiteralPath $scratchRoot) {
        Remove-Item -LiteralPath $scratchRoot -Recurse -Force
    }
}
