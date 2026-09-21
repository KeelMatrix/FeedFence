$ErrorActionPreference = 'Stop'

$repositoryRoot = (git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
if ([string]::IsNullOrWhiteSpace($repositoryRoot)) {
    throw 'Unable to determine the repository root.'
}

$forbiddenPattern = '(?im)(?:Co-Authored-By\s*:|\b(?:Paperclip|Codex|agent(?:s)?|orchestrat(?:e|ed|ion|or)|Task\s+Delegator|staff[- ]owned|founder[- ](?:approval|approved|gated|controlled)|frontier[- ](?:review|approval)|internal\s+process)\b)'
$findings = [System.Collections.Generic.List[string]]::new()

$commitOutput = @(git -C $repositoryRoot log --all --format='%H%x1f%s%x1f%b%x1e')
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to inspect reachable commit messages.'
}

$commitRecords = @(($commitOutput -join [Environment]::NewLine) -split [char]0x1e | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
foreach ($record in $commitRecords) {
    $fields = $record -split [char]0x1f, 3
    $commit = $fields[0]
    $message = if ($fields.Count -gt 1) { $fields[1..($fields.Count - 1)] -join "`n" } else { '' }
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
$trackedFiles = @(git -C $repositoryRoot ls-files -- $scopedPaths)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to enumerate tracked developer-document and GitHub files.'
}

foreach ($relativePath in $trackedFiles) {
    $path = Join-Path $repositoryRoot $relativePath
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
Write-Output 'PASS: no prohibited authorship trailer or internal/orchestration wording found.'
