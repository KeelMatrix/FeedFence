param(
    [switch]$SelfTest,
    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'

$allowedAuthorIdentities = @(
    'KeelMatrix <keelmatrix@users.noreply.github.com>',
    'KeelMatrix <keelmatrix@gmail.com>'
)
$allowedCommitterIdentities = @(
    'KeelMatrix <keelmatrix@users.noreply.github.com>',
    'GitHub <noreply@github.com>'
)

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
        if ($author -cnotin $allowedAuthorIdentities) {
            $findings.Add("commit $commit has unexpected author identity '$author'")
        }
        if ($committer -cnotin $allowedCommitterIdentities) {
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

    $workflowPaths = @(git -C $Root ls-files -- '.github/workflows/*.yml' '.github/workflows/*.yaml')
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to enumerate workflow files.'
    }

    foreach ($relativePath in $workflowPaths) {
        $path = Join-Path $Root $relativePath
        foreach ($line in Get-Content -LiteralPath $path) {
            if ($line -match '^\s*(?:-\s*)?uses:\s*(?<reference>[^\s#]+)') {
                $reference = $Matches.reference
                if ($reference -notmatch '@[0-9a-fA-F]{40}$') {
                    $findings.Add("workflow '$relativePath' contains a mutable GitHub Actions reference")
                }
            }
        }
    }

    if ($findings.Count -gt 0) {
        $findings | ForEach-Object { Write-Error $_ }
        throw "Repository hygiene failed with $($findings.Count) finding(s)."
    }

    Write-Output "Repository hygiene: inspected $($commitRecords.Count) reachable commit record(s) and $($trackedFiles.Count) tracked scoped file(s)."
    Write-Output 'PASS: approved KeelMatrix authors and KeelMatrix/GitHub web-flow committers passed with no prohibited authorship trailer or internal/orchestration wording.'
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

function Assert-HygieneAccepts {
    param([string]$Root)

    & $PSCommandPath -RepositoryRoot $Root *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "Self-test expected the hygiene guard to accept '$Root'."
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

        $webFlowRoot = Join-Path $selfTestRoot 'github-web-flow'
        New-SyntheticRepository $webFlowRoot
        Set-Content -LiteralPath (Join-Path $webFlowRoot 'web-flow.txt') -Encoding utf8 -Value 'GitHub web flow'
        Invoke-GitChecked $webFlowRoot @('add', 'web-flow.txt')
        & git -C $webFlowRoot -c user.name='GitHub' -c user.email='noreply@github.com' commit --quiet --author='KeelMatrix <keelmatrix@users.noreply.github.com>' -m 'synthetic GitHub web flow' *> $null
        if ($LASTEXITCODE -ne 0) { throw 'Unable to create the GitHub web-flow synthetic commit.' }
        Assert-HygieneAccepts $webFlowRoot
        Write-Output 'Repository hygiene self-test: approved GitHub web-flow committer accepted.'

        $trailerRoot = Join-Path $selfTestRoot 'prohibited-trailer'
        New-SyntheticRepository $trailerRoot
        Set-Content -LiteralPath (Join-Path $trailerRoot 'trailer.txt') -Encoding utf8 -Value 'prohibited trailer'
        Invoke-GitChecked $trailerRoot @('add', 'trailer.txt')
        $trailerMessage = "synthetic prohibited trailer`n`nCo-Authored-By: Someone <someone@example.invalid>"
        Invoke-GitChecked $trailerRoot @('commit', '--quiet', '-m', $trailerMessage)
        Assert-HygieneRejects $trailerRoot 'contains prohibited authorship'
        Write-Output 'Repository hygiene self-test: prohibited-trailer commit rejected.'

        $mutableWorkflowRoot = Join-Path $selfTestRoot 'mutable-workflow'
        New-SyntheticRepository $mutableWorkflowRoot
        $workflowDirectory = Join-Path $mutableWorkflowRoot '.github\workflows'
        New-Item -ItemType Directory -Force -Path $workflowDirectory | Out-Null
        Set-Content -LiteralPath (Join-Path $workflowDirectory 'ci.yml') -Encoding utf8 -Value "name: CI`nsteps:`n  - uses: actions/checkout@v4"
        Invoke-GitChecked $mutableWorkflowRoot @('add', '.github/workflows/ci.yml')
        Invoke-GitChecked $mutableWorkflowRoot @('commit', '--quiet', '-m', 'synthetic mutable workflow')
        Assert-HygieneRejects $mutableWorkflowRoot 'mutable GitHub Actions reference'
        Write-Output 'Repository hygiene self-test: mutable workflow reference rejected.'

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
