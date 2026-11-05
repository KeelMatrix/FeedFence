param(
    [switch]$SelfTest,
    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'

$allowedAuthorIdentities = @(
    'KeelMatrix <keelmatrix@users.noreply.github.com>',
    'KeelMatrix <230870363+KeelMatrix@users.noreply.github.com>',
    'KeelMatrix <keelmatrix@gmail.com>'
)
$allowedCommitterIdentities = @(
    'KeelMatrix <keelmatrix@users.noreply.github.com>',
    'KeelMatrix <230870363+KeelMatrix@users.noreply.github.com>',
    'GitHub <noreply@github.com>'
)

function Invoke-HygieneCheck {
    param([string]$Root)

    $forbiddenPattern = '(?im)(?:Co-Authored-By\s*:|\b(?:PAPERCLIP(?:_[A-Z0-9_]+)*|CODEX(?:_[A-Z0-9_]+)*|KEE-\d+|Paperclip|Codex|agent(?:s)?|orchestrat(?:e|ed|ion|or)|Task\s+Delegator|model\s+rout(?:e|ing)|private\s+(?:operating\s+policy|process)|internal\s+(?:automation|company|runtime|process|routing|task|agent|model)|review[- ]process|staff[- ]owned|founder[- ](?:approval|approved|gated|controlled)|frontier[- ](?:review|approval))\b)'
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

    $guardRelativePath = 'scripts/Invoke-RepoHygiene.ps1'
    $trackedFiles = @(git -C $Root ls-files | Where-Object { $_ -cne $guardRelativePath })
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to enumerate tracked repository files.'
    }

    foreach ($relativePath in $trackedFiles) {
        $path = Join-Path $Root $relativePath
        $content = Get-Content -LiteralPath $path -Raw
        if ($content -match $forbiddenPattern) {
            $findings.Add("tracked file '$relativePath' contains prohibited authorship, internal identifier, or process wording")
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

    Write-Output "Repository hygiene: inspected $($commitRecords.Count) reachable commit record(s) and $($trackedFiles.Count) tracked file(s)."
    Write-Output 'PASS: approved KeelMatrix authors and KeelMatrix/GitHub web-flow committers passed with no prohibited authorship trailer, internal identifier, or process wording.'
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

function New-SyntheticCommit {
    param([string]$Root, [string]$Message)

    $messagePath = Join-Path $Root '.git/fixture-message.txt'
    [System.IO.File]::WriteAllText($messagePath, $Message, [System.Text.UTF8Encoding]::new($false))
    Invoke-GitChecked $Root @('commit', '--quiet', '--allow-empty', '-F', $messagePath)
    $actualMessage = @(git -C $Root log -1 --format='%B') -join "`n"
    if ($LASTEXITCODE -ne 0 -or $actualMessage.TrimEnd() -cne $Message.TrimEnd()) {
        throw 'Synthetic commit message did not preserve the intended subject and body.'
    }
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

        $wrongCommitterRoot = Join-Path $selfTestRoot 'wrong-committer'
        New-SyntheticRepository $wrongCommitterRoot
        Set-Content -LiteralPath (Join-Path $wrongCommitterRoot 'committer.txt') -Encoding utf8 -Value 'wrong committer'
        Invoke-GitChecked $wrongCommitterRoot @('add', 'committer.txt')
        & git -C $wrongCommitterRoot -c user.name='Not KeelMatrix Committer' -c user.email='not-committer@example.invalid' commit --quiet --author='KeelMatrix <keelmatrix@users.noreply.github.com>' -m 'synthetic wrong committer' *> $null
        if ($LASTEXITCODE -ne 0) { throw 'Unable to create the wrong-committer synthetic commit.' }
        Assert-HygieneRejects $wrongCommitterRoot 'unexpected committer identity'
        Write-Output 'Repository hygiene self-test: wrong-committer commit rejected.'

        $webFlowRoot = Join-Path $selfTestRoot 'github-web-flow'
        New-SyntheticRepository $webFlowRoot
        Set-Content -LiteralPath (Join-Path $webFlowRoot 'web-flow.txt') -Encoding utf8 -Value 'GitHub web flow'
        Invoke-GitChecked $webFlowRoot @('add', 'web-flow.txt')
        & git -C $webFlowRoot -c user.name='GitHub' -c user.email='noreply@github.com' commit --quiet --author='KeelMatrix <keelmatrix@users.noreply.github.com>' -m 'synthetic GitHub web flow' *> $null
        if ($LASTEXITCODE -ne 0) { throw 'Unable to create the GitHub web-flow synthetic commit.' }
        Assert-HygieneAccepts $webFlowRoot
        Write-Output 'Repository hygiene self-test: approved GitHub web-flow committer accepted.'

        $legacyEmail = 'keelmatrix@users.noreply.github.com'
        $githubEmail = '230870363+KeelMatrix@users.noreply.github.com'
        $identityCases = @(
            @{ Name = 'github-author'; AuthorEmail = $githubEmail; CommitterEmail = $legacyEmail; Finding = '' },
            @{ Name = 'github-committer'; AuthorEmail = $legacyEmail; CommitterEmail = $githubEmail; Finding = '' },
            @{ Name = 'github-both'; AuthorEmail = $githubEmail; CommitterEmail = $githubEmail; Finding = '' },
            @{ Name = 'wrong-author-id'; AuthorEmail = '230870364+KeelMatrix@users.noreply.github.com'; CommitterEmail = $legacyEmail; Finding = 'unexpected author identity' },
            @{ Name = 'wrong-committer-id'; AuthorEmail = $legacyEmail; CommitterEmail = '230870364+KeelMatrix@users.noreply.github.com'; Finding = 'unexpected committer identity' },
            @{ Name = 'wrong-author-login'; AuthorEmail = '230870363+OtherAccount@users.noreply.github.com'; CommitterEmail = $legacyEmail; Finding = 'unexpected author identity' },
            @{ Name = 'wrong-committer-login'; AuthorEmail = $legacyEmail; CommitterEmail = '230870363+OtherAccount@users.noreply.github.com'; Finding = 'unexpected committer identity' }
        )
        foreach ($case in $identityCases) {
            $identityRoot = Join-Path $selfTestRoot $case.Name
            New-SyntheticRepository $identityRoot
            Invoke-GitChecked $identityRoot @('-c', "user.email=$($case.CommitterEmail)", 'commit', '--quiet', '--allow-empty', "--author=KeelMatrix <$($case.AuthorEmail)>", '-m', 'synthetic identity')
            if ($case.Finding) {
                Assert-HygieneRejects $identityRoot $case.Finding
            }
            else {
                Assert-HygieneAccepts $identityRoot
            }
            Write-Output "Repository hygiene self-test: $($case.Name) identity checked."
        }

        $internalIdentifierRoot = Join-Path $selfTestRoot 'internal-identifier'
        New-SyntheticRepository $internalIdentifierRoot
        Set-Content -LiteralPath (Join-Path $internalIdentifierRoot 'tracked-leak.txt') -Encoding utf8 -Value 'PAPERCLIP_EXAMPLE_IDENTIFIER'
        Invoke-GitChecked $internalIdentifierRoot @('add', 'tracked-leak.txt')
        Invoke-GitChecked $internalIdentifierRoot @('commit', '--quiet', '-m', 'synthetic tracked content')
        Assert-HygieneRejects $internalIdentifierRoot 'tracked file'
        Write-Output 'Repository hygiene self-test: tracked internal identifier rejected.'

        $trailerRoot = Join-Path $selfTestRoot 'prohibited-trailer'
        New-SyntheticRepository $trailerRoot
        Set-Content -LiteralPath (Join-Path $trailerRoot 'trailer.txt') -Encoding utf8 -Value 'prohibited trailer'
        Invoke-GitChecked $trailerRoot @('add', 'trailer.txt')
        $trailerMessage = "synthetic prohibited trailer`n`nCo-Authored-By: Someone <someone@example.invalid>"
        New-SyntheticCommit $trailerRoot $trailerMessage
        Assert-HygieneRejects $trailerRoot 'contains prohibited authorship'
        Write-Output 'Repository hygiene self-test: prohibited-trailer commit rejected.'

        $internalWordingRoot = Join-Path $selfTestRoot 'internal-wording'
        New-SyntheticRepository $internalWordingRoot
        Set-Content -LiteralPath (Join-Path $internalWordingRoot 'wording.txt') -Encoding utf8 -Value 'internal wording'
        Invoke-GitChecked $internalWordingRoot @('add', 'wording.txt')
        Invoke-GitChecked $internalWordingRoot @('commit', '--quiet', '-m', 'synthetic internal process wording')
        Assert-HygieneRejects $internalWordingRoot 'contains prohibited authorship'
        Write-Output 'Repository hygiene self-test: internal wording commit rejected.'

        $messageCases = @(
            @{ Name = 'subject'; Message = 'synthetic maintenance (KEE-1234)'; Reachability = 'ancestor' },
            @{ Name = 'body'; Message = "synthetic maintenance`n`nRelated: kee-1234"; Reachability = 'ancestor' },
            @{ Name = 'branch'; Message = 'synthetic maintenance (KEE-1234)'; Reachability = 'branch' },
            @{ Name = 'tag'; Message = 'synthetic maintenance (KEE-1234)'; Reachability = 'tag' }
        )
        foreach ($case in $messageCases) {
            $messageRoot = Join-Path $selfTestRoot ('message-' + $case.Name)
            New-SyntheticRepository $messageRoot
            $defaultBranch = (git -C $messageRoot symbolic-ref --short HEAD).Trim()
            if ($LASTEXITCODE -ne 0) { throw 'Unable to read the synthetic default branch.' }
            Invoke-GitChecked $messageRoot @('checkout', '-b', 'message-fixture')
            New-SyntheticCommit $messageRoot $case.Message
            if ($case.Reachability -eq 'ancestor') {
                Invoke-GitChecked $messageRoot @('commit', '--quiet', '--allow-empty', '-m', 'synthetic clean tip')
            }
            else {
                if ($case.Reachability -eq 'tag') {
                    Invoke-GitChecked $messageRoot @('tag', 'message-fixture')
                }
                Invoke-GitChecked $messageRoot @('checkout', $defaultBranch)
                if ($case.Reachability -eq 'tag') {
                    Invoke-GitChecked $messageRoot @('branch', '-D', 'message-fixture')
                }
            }
            Assert-HygieneRejects $messageRoot 'contains prohibited authorship'
            Write-Output "Repository hygiene self-test: prohibited $($case.Name) message rejected across $($case.Reachability) history."
        }

        $publicReferenceRoot = Join-Path $selfTestRoot 'public-reference'
        New-SyntheticRepository $publicReferenceRoot
        New-SyntheticCommit $publicReferenceRoot "synthetic maintenance (#1234)`n`nSee public issue #5678."
        Assert-HygieneAccepts $publicReferenceRoot
        Write-Output 'Repository hygiene self-test: public numeric issue references accepted.'

        $startInfo = [System.Diagnostics.ProcessStartInfo]::new('pwsh')
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        foreach ($argument in @('-NoProfile', '-NonInteractive', '-File', $PSCommandPath, '-RepositoryRoot', (Join-Path $selfTestRoot 'message-subject'))) {
            $startInfo.ArgumentList.Add($argument)
        }
        $process = [System.Diagnostics.Process]::Start($startInfo)
        try {
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(30000)) {
                $process.Kill($true)
                throw 'Repository hygiene process self-test timed out.'
            }
            $output = $stdout.Result + $stderr.Result
            if ($process.ExitCode -ne 1 -or $output -notmatch 'prohibited' -or $output -notmatch 'authorship') {
                throw 'Repository hygiene process self-test did not propagate the prohibited-message failure.'
            }
            Write-Output 'Repository hygiene self-test: prohibited message exits the workflow process with failure.'
        }
        finally {
            $process.Dispose()
        }

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
