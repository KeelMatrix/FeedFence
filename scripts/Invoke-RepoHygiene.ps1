param(
    [switch]$SelfTest,
    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'

$expectedIdentity = 'KeelMatrix <keelmatrix@users.noreply.github.com>'

function Invoke-HygieneCheck {
    param([string]$Root)

    $forbiddenPattern = '(?im)(?:Co-Authored-By\s*:|\b(?:Paperclip|Codex|agent(?:s)?|orchestrat(?:e|ed|ion|or)|Task\s+Delegator|staff[- ]owned|founder[- ](?:approval|approved|gated|controlled)|frontier[- ](?:review|approval)|internal\s+process)\b)'
    $findings = [System.Collections.Generic.List[string]]::new()

    $commitOutput = @(git -C $Root log --all --format='%H%x1f%an%x1f%ae%x1f%cn%x1f%ce%x1f%s%x1f%b%x1e')
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to inspect reachable commit messages.'
    }

    $commitRecords = @(($commitOutput -join [Environment]::NewLine) -split [char]0x1e | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    foreach ($record in $commitRecords) {
        $fields = $record -split ([char]0x1f), 6
        $commit = $fields[0]
        $author = if ($fields.Count -gt 2) { "$($fields[1]) <$($fields[2])>" } else { '' }
        $committer = if ($fields.Count -gt 4) { "$($fields[3]) <$($fields[4])>" } else { '' }
        if ($author -cne $expectedIdentity) {
            $findings.Add("commit $commit has unexpected author identity '$author'")
        }
        if ($committer -cne $expectedIdentity) {
            $findings.Add("commit $commit has unexpected committer identity '$committer'")
        }

        $message = if ($fields.Count -gt 5) { $fields[5] } else { '' }
        if ($message -match $forbiddenPattern) {
            $findings.Add("commit $commit contains prohibited authorship or internal wording")
        }
    }

    $scopedPaths = @(
        'CONTRIBUTING.md',
        'CODE_OF_CONDUCT.md',
        'docs/DEV.md',
        'docs/report-contract.md',
        '.github/**'
    )
    $trackedFiles = @(git -C $Root ls-files -- $scopedPaths)
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to enumerate tracked developer-document and GitHub files.'
    }

    foreach ($relativePath in $trackedFiles) {
        $path = Join-Path $Root $relativePath
        $content = Get-Content -LiteralPath $path -Raw
        if ($content -match $forbiddenPattern) {
            $findings.Add("tracked file '$relativePath' contains prohibited authorship or internal wording")
        }
    }

    if ($findings.Count -gt 0) {
        $findings | ForEach-Object { Write-Error $_ }
        throw "Repository hygiene failed with $($findings.Count) finding(s)."
    }

    Write-Output "Repository hygiene: inspected $($commitRecords.Count) reachable commit record(s) and $($trackedFiles.Count) tracked scoped file(s)."
    Write-Output 'PASS: expected KeelMatrix author/committer identities and no prohibited authorship trailer or internal/orchestration wording found.'
}

function Invoke-GitChecked {
    param(
        [string]$Root,
        [string[]]$Arguments
    )

    & git -C $Root @Arguments *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "Git command failed: git -C $Root $($Arguments -join ' ')"
    }
}

function New-SyntheticRepository {
    param([string]$Root)

    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    Invoke-GitChecked $Root @('init', '--quiet')
    Invoke-GitChecked $Root @('config', 'user.name', 'KeelMatrix')
    Invoke-GitChecked $Root @('config', 'user.email', 'keelmatrix@users.noreply.github.com')
    Set-Content -LiteralPath (Join-Path $Root 'fixture.txt') -Encoding utf8 -Value 'fixture'
    Invoke-GitChecked $Root @('add', 'fixture.txt')
    Invoke-GitChecked $Root @('commit', '--quiet', '-m', 'synthetic fixture')
}

function Assert-HygieneRejects {
    param(
        [string]$Root,
        [string]$ExpectedFinding
    )

    $rejected = $false
    try {
        & $PSCommandPath -RepositoryRoot $Root *> $null
    }
    catch {
        $rejected = $true
        if ($ExpectedFinding -and $_.Exception.Message -notmatch [regex]::Escape($ExpectedFinding)) {
            throw "Self-test failed with an unexpected hygiene error: $($_.Exception.Message)"
        }
    }

    if (-not $rejected) {
        throw "Self-test expected the hygiene guard to reject '$ExpectedFinding'."
    }
}

if ($SelfTest) {
    $selfTestRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('feedfence-hygiene-' + [guid]::NewGuid().ToString('N'))
    try {
        $wrongAuthorRoot = Join-Path $selfTestRoot 'wrong-author'
        New-SyntheticRepository $wrongAuthorRoot
        Set-Content -LiteralPath (Join-Path $wrongAuthorRoot 'author.txt') -Encoding utf8 -Value 'wrong author'
        Invoke-GitChecked $wrongAuthorRoot @('add', 'author.txt')
        & git -C $wrongAuthorRoot -c user.name='Not KeelMatrix' -c user.email='not-keelmatrix@example.invalid' commit --quiet --author='Not KeelMatrix <not-keelmatrix@example.invalid>' -m 'synthetic wrong author' *> $null
        if ($LASTEXITCODE -ne 0) { throw 'Unable to create the wrong-author synthetic commit.' }
        Assert-HygieneRejects $wrongAuthorRoot 'unexpected author identity'
        Write-Output 'Repository hygiene self-test: wrong-author commit rejected.'

        $trailerRoot = Join-Path $selfTestRoot 'prohibited-trailer'
        New-SyntheticRepository $trailerRoot
        Set-Content -LiteralPath (Join-Path $trailerRoot 'trailer.txt') -Encoding utf8 -Value 'prohibited trailer'
        Invoke-GitChecked $trailerRoot @('add', 'trailer.txt')
        $trailerMessage = "synthetic prohibited trailer`n`nCo-Authored-By: Someone <someone@example.invalid>"
        Invoke-GitChecked $trailerRoot @('commit', '--quiet', '-m', $trailerMessage)
        Assert-HygieneRejects $trailerRoot 'contains prohibited authorship'
        Write-Output 'Repository hygiene self-test: prohibited-trailer commit rejected.'
        Write-Output 'PASS: repository hygiene self-test completed.'
    }
    finally {
        if (Test-Path -LiteralPath $selfTestRoot) {
            Remove-Item -LiteralPath $selfTestRoot -Recurse -Force
        }
    }
    exit 0
}

$repositoryRoot = if ($RepositoryRoot) { (Resolve-Path -LiteralPath $RepositoryRoot).Path } else { (git -C $PSScriptRoot rev-parse --show-toplevel).Trim() }
if ([string]::IsNullOrWhiteSpace($repositoryRoot)) {
    throw 'Unable to determine the repository root.'
}
Invoke-HygieneCheck $repositoryRoot
