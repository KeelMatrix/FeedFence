param(
    [Parameter(Mandatory = $true)]
    [string]$Path
)

$ErrorActionPreference = 'Stop'
$bytes = [IO.File]::ReadAllBytes($Path)
if ($bytes.Length -lt 0x40 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
    throw "Managed assembly is not a PE file: $Path"
}

$peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
if ($peOffset -lt 0 -or $peOffset + 12 -gt $bytes.Length -or
    $bytes[$peOffset] -ne 0x50 -or $bytes[$peOffset + 1] -ne 0x45 -or
    $bytes[$peOffset + 2] -ne 0 -or $bytes[$peOffset + 3] -ne 0) {
    throw "Managed assembly has an invalid PE header: $Path"
}

$timestampOffset = $peOffset + 8
$changed = $false
for ($index = 0; $index -lt 4; $index++) {
    if ($bytes[$timestampOffset + $index] -ne 0) {
        $bytes[$timestampOffset + $index] = 0
        $changed = $true
    }
}

if ($changed) {
    [IO.File]::WriteAllBytes($Path, $bytes)
}

Write-Output "Managed assembly PE timestamp normalization: PASS ($Path)"
