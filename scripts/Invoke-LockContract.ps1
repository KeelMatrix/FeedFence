param(
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot 'KeelMatrix.FeedFence.sln'
$expectedLockFiles = @(
    'src/KeelMatrix.FeedFence/packages.lock.json',
    'tests/FeedFenceCliTests/packages.lock.json',
    'tests/Phase0Probe/packages.lock.json'
)

function Assert-WorkflowRestoreContract {
    foreach ($workflow in @('.github/workflows/ci.yml', '.github/workflows/release.yml')) {
        $path = Join-Path $repositoryRoot $workflow
        $content = Get-Content -LiteralPath $path -Raw
        if ($content -notmatch '(?m)^\s*run:\s*dotnet restore .*--locked-mode\s*$' -and
            $content -notmatch '(?s)run:\s+dotnet restore .*?--locked-mode') {
            throw "Workflow '$workflow' does not enforce locked restore."
        }

        if ($content -match '(?m)^\s*run:\s*dotnet restore .*--force') {
            throw "Workflow '$workflow' uses --force on the repository restore path."
        }
    }
}

function Assert-TrackedLockFiles {
    foreach ($relativePath in $expectedLockFiles) {
        & git -C $repositoryRoot ls-files --error-unmatch -- $relativePath *> $null
        if ($LASTEXITCODE -ne 0) {
            throw "Required committed lock file is missing: $relativePath"
        }
    }
}

function Invoke-Restore([string]$Root, [string]$PackagesPath) {
    $output = & dotnet restore (Join-Path $Root 'KeelMatrix.FeedFence.sln') --configfile (Join-Path $Root 'NuGet.config') --packages $PackagesPath --no-cache --locked-mode 2>&1
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join [Environment]::NewLine) }
}

function Invoke-Contract {
    Assert-WorkflowRestoreContract
    Assert-TrackedLockFiles

    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('feedfence-lock-contract-' + [guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
        $current = Invoke-Restore $repositoryRoot (Join-Path $tempRoot 'current-packages')
        if ($current.ExitCode -ne 0) {
            throw "Committed lock graph did not restore in locked mode: $($current.Output)"
        }
        Write-Output 'Locked restore with committed graph: PASS.'

        $cloneRoot = Join-Path $tempRoot 'clone'
        & git clone --quiet --no-local $repositoryRoot $cloneRoot
        if ($LASTEXITCODE -ne 0) { throw 'Unable to create the clean lock-contract clone.' }

        $driftPath = Join-Path $cloneRoot 'src/KeelMatrix.FeedFence/packages.lock.json'
        $drift = Get-Content -LiteralPath $driftPath -Raw
        $drift = $drift.Replace('"resolved": "0.1.1"', '"resolved": "0.1.2"', [StringComparison]::Ordinal)
        Set-Content -LiteralPath $driftPath -Encoding utf8 -Value $drift

        $driftResult = Invoke-Restore $cloneRoot (Join-Path $cloneRoot 'packages')
        if ($driftResult.ExitCode -eq 0) {
            throw 'Locked restore unexpectedly accepted a mutated committed lock file.'
        }
        Write-Output 'Locked restore with mutated graph: PASS (failed closed).'
    }
    finally {
        if (Test-Path -LiteralPath $tempRoot) {
            Remove-Item -LiteralPath $tempRoot -Recurse -Force
        }
    }
}

if ($SelfTest) {
    Assert-WorkflowRestoreContract
    Write-Output 'PASS: lock contract self-test.'
    exit 0
}

Invoke-Contract
