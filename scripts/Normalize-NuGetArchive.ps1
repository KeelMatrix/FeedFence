param(
    [Parameter(Mandatory)]
    [string]$Path
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-Bytes([System.IO.Compression.ZipArchiveEntry]$Entry) {
    $memory = [System.IO.MemoryStream]::new()
    try {
        $stream = $Entry.Open()
        try { $stream.CopyTo($memory) } finally { $stream.Dispose() }
        return $memory.ToArray()
    }
    finally { $memory.Dispose() }
}

function Get-DeterministicCorePropertiesName([string]$ArchivePath) {
    $kind = if ([IO.Path]::GetExtension($ArchivePath) -ieq '.snupkg') { 'snupkg' } else { 'nupkg' }
    $seed = "KeelMatrix.FeedFence|$kind|$([IO.Path]::GetFileNameWithoutExtension($ArchivePath))"
    $hash = [Convert]::ToHexString([Security.Cryptography.MD5]::HashData([Text.Encoding]::UTF8.GetBytes($seed))).ToLowerInvariant()
    return "package/services/metadata/core-properties/$hash.psmdcp"
}

$fullPath = [IO.Path]::GetFullPath($Path)
if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
    throw "Archive does not exist: $Path"
}

$temporaryPath = "$fullPath.normalized-$([guid]::NewGuid().ToString('N'))"
$source = [IO.Compression.ZipFile]::OpenRead($fullPath)
try {
    $entries = @($source.Entries | Sort-Object FullName)
    $coreEntries = @($entries | Where-Object FullName -match '^package/services/metadata/core-properties/[0-9a-f]{32}\.psmdcp$')
    if ($coreEntries.Count -ne 1) {
        throw "Archive must contain exactly one NuGet core-properties entry; found $($coreEntries.Count)."
    }

    $oldCoreName = $coreEntries[0].FullName
    $newCoreName = Get-DeterministicCorePropertiesName $fullPath
    $destination = [IO.Compression.ZipFile]::Open($temporaryPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $entries) {
            $entryName = if ($entry.FullName -eq $oldCoreName) { $newCoreName } else { $entry.FullName }
            $bytes = Get-Bytes $entry
            if ($entryName -eq '_rels/.rels') {
                $text = [Text.Encoding]::UTF8.GetString($bytes).Replace($oldCoreName, $newCoreName, [StringComparison]::Ordinal)
                $relationshipIndex = 0
                $text = [regex]::Replace($text, 'Id="R[0-9A-F]+"', {
                    param($match)
                    $relationshipIndex++
                    return "Id=`"R$relationshipIndex`""
                })
                $bytes = [Text.Encoding]::UTF8.GetBytes($text)
            }
            elseif ($entryName -eq '[Content_Types].xml') {
                $text = [Text.Encoding]::UTF8.GetString($bytes).Replace($oldCoreName, $newCoreName, [StringComparison]::Ordinal)
                $bytes = [Text.Encoding]::UTF8.GetBytes($text)
            }

            $newEntry = $destination.CreateEntry($entryName, [IO.Compression.CompressionLevel]::Optimal)
            $newEntry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $stream = $newEntry.Open()
            try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
        }
    }
    finally { $destination.Dispose() }
}
finally { $source.Dispose() }

Move-Item -LiteralPath $temporaryPath -Destination $fullPath -Force
Write-Output "Normalized $fullPath with core-properties entry $newCoreName and fixed ZIP timestamps."
