$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$workflowPath = Join-Path $repositoryRoot '.github/workflows/release.yml'
$workflow = Get-Content -LiteralPath $workflowPath -Raw
$step = [regex]::Match($workflow, '(?ms)^      - name: Pack, inspect, and consume exact release artifacts\r?\n        shell: pwsh\r?\n        run: \|\r?\n(?<body>.*?)(?=\r?\n      - name:)')
if (-not $step.Success) {
    throw 'Could not locate the release artifact validation step.'
}

# Execute the shipping workflow body, substituting only GitHub expressions.
$body = [regex]::Replace($step.Groups['body'].Value, '(?m)^          ', '')
$body = $body.Replace('${{ steps.release-version.outputs.version }}', '0.1.0').Replace('${{ github.ref_name }}', 'v0.1.0')
$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = Join-Path $tempParent ('feedfence-release-workflow-' + [guid]::NewGuid().ToString('N'))

function Invoke-TestProcess([string]$ScriptPath, [string]$WorkingDirectory, [string[]]$Arguments = @()) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new('pwsh')
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.WorkingDirectory = $WorkingDirectory
    foreach ($argument in @('-NoProfile', '-NonInteractive', '-File', $ScriptPath) + $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            throw 'Release workflow test process timed out.'
        }
        [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $stdout.Result + $stderr.Result }
    }
    finally {
        $process.Dispose()
    }
}

function Get-CheckedFunction([string]$ScriptName) {
    $tokens = $null
    $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $ScriptName), [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) { throw "Cannot parse $ScriptName." }
    $functions = @($ast.FindAll({
                param($node)
                $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-Checked'
            }, $true))
    if ($functions.Count -ne 1) { throw "Cannot locate the shipping Invoke-Checked in $ScriptName." }
    $functions[0].Extent.Text
}

$cases = @(
    @{ Name = 'early failure'; Failure = 'exit 1'; Files = $false; Finalized = $true; Pass = $false },
    @{ Name = 'late failure with both artifacts'; Failure = 'exit 1'; Files = $true; Finalized = $true; Pass = $false },
    @{ Name = 'first gate throws'; Failure = "throw 'Synthetic validator rejection.'"; Files = $true; Finalized = $true; Pass = $false },
    @{ Name = 'first passes and final fails'; Failure = ''; Files = $true; Finalized = $false; Pass = $false },
    @{ Name = 'nested pack failure'; Failure = 'nested'; Helper = 'Invoke-PackGate.ps1'; Files = $true; Finalized = $true; Pass = $false },
    @{ Name = 'website metadata rejection'; Failure = 'metadata'; Files = $true; Finalized = $true; Pass = $false },
    @{ Name = 'nested reproducibility failure'; Failure = 'nested'; Helper = 'Invoke-ReproducibilityGate.ps1'; Files = $true; Finalized = $true; Pass = $false },
    @{ Name = 'both pass'; Failure = ''; Files = $true; Finalized = $true; Pass = $true }
)
$failures = [Collections.Generic.List[string]]::new()
try {
    for ($index = 0; $index -lt $cases.Count; $index++) {
        $case = $cases[$index]
        $fixture = Join-Path $testRoot $index
        foreach ($directory in @('scripts', 'build', 'src/KeelMatrix.FeedFence', '.github/workflows')) {
            New-Item -ItemType Directory -Force -Path (Join-Path $fixture $directory) | Out-Null
        }
        Copy-Item -LiteralPath $workflowPath -Destination (Join-Path $fixture '.github/workflows/release.yml')
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Invoke-ReleaseContract.ps1') -Destination (Join-Path $fixture 'scripts')
        Copy-Item -LiteralPath (Join-Path $repositoryRoot 'build/Invoke-NestedPwsh.ps1') -Destination (Join-Path $fixture 'build')
        Set-Content -LiteralPath (Join-Path $fixture 'src/KeelMatrix.FeedFence/KeelMatrix.FeedFence.csproj') -Value '<Project><PropertyGroup><Version>0.1.0</Version></PropertyGroup></Project>'
        $changelog = "# Changelog`n`n## [Unreleased]`n"
        if ($case.Finalized) {
            $changelog += "`n## [0.1.0] - 2026-09-23`n`n### Added`n`n- Provides initial offline source-policy analysis.`n"
        }
        Set-Content -LiteralPath (Join-Path $fixture 'CHANGELOG.md') -Value $changelog

        $packFixture = @'
param([string]$Configuration, [string]$PackageVersion, [string]$ArtifactDirectory)
$ErrorActionPreference = 'Stop'
'@
        if ($case.Files) {
            $packFixture += @'

foreach ($extension in @('nupkg', 'snupkg')) {
    Set-Content -LiteralPath (Join-Path $ArtifactDirectory "KeelMatrix.FeedFence.$PackageVersion.$extension") -Value 'Synthetic control-flow fixture; never publish.'
}
'@
        }
        if ($case.Failure -eq 'nested') {
            # Use the actual shipping checked-call function and launch helper.
            $packFixture += "`n. (Join-Path `$PSScriptRoot '../build/Invoke-NestedPwsh.ps1')`n"
            $packFixture += (Get-CheckedFunction $case.Helper) + "`n"
            $packFixture += "Invoke-Checked 'pwsh' @('-NoProfile', '-NonInteractive', '-Command', 'exit 17')"
            if ($case.Helper -eq 'Invoke-ReproducibilityGate.ps1') { $packFixture += ' $PSScriptRoot' }
            $packFixture += "`nSet-Content -LiteralPath 'nested-continued' -Value 'unreachable'`n"
        }
        elseif ($case.Failure -eq 'metadata') {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Invoke-WebsiteMetadata.ps1') -Destination (Join-Path $fixture 'scripts')
            Copy-Item -LiteralPath (Join-Path $repositoryRoot 'build/WebsiteMetadata.ps1') -Destination (Join-Path $fixture 'build')
            Set-Content -LiteralPath (Join-Path $fixture 'src/KeelMatrix.FeedFence/KeelMatrix.FeedFence.csproj') -Value '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><Version>0.1.0</Version><IsPackable>true</IsPackable><PackageId>KeelMatrix.FeedFence</PackageId><PackageTags>keelmatrix-public-product;keelmatrix-primary</PackageTags><PackageProjectUrl>https://github.com/KeelMatrix/FeedFence</PackageProjectUrl><RepositoryUrl>https://github.com/KeelMatrix/FeedFence</RepositoryUrl><RepositoryType>git</RepositoryType></PropertyGroup></Project>'
            Set-Content -LiteralPath (Join-Path $fixture 'keelmatrix.website.json') -Value '{"schemaVersion":1,"packages":{"KeelMatrix.FeedFence":{"visibility":"public-product","role":"component"}}}'
            $packFixture += "`n. (Join-Path `$PSScriptRoot '../build/Invoke-NestedPwsh.ps1')`n"
            $packFixture += (Get-CheckedFunction 'Invoke-PackGate.ps1') + "`n"
            $packFixture += "Invoke-Checked 'pwsh' @('-NoProfile', '-File', (Join-Path `$PSScriptRoot 'Invoke-WebsiteMetadata.ps1'))`n"
            $packFixture += "Set-Content -LiteralPath 'metadata-continued' -Value 'unreachable'`n"
        }
        else {
            $packFixture += "`n" + $case.Failure + "`n"
        }
        Set-Content -LiteralPath (Join-Path $fixture 'scripts/Invoke-PackGate.ps1') -Value $packFixture

        # Match the runner's pwsh wrapper. Sentinels stand for downstream work;
        # no upload, authentication, or publication command is executed here.
        $wrapper = '$ErrorActionPreference = ''Stop''' + "`n" + $body + @'

Set-Content -LiteralPath 'upload-reached' -Value 'validated'
Set-Content -LiteralPath 'publication-reached' -Value 'validated'
if (Test-Path -LiteralPath variable:\LASTEXITCODE) { exit $LASTEXITCODE }
'@
        $wrapperPath = Join-Path $fixture 'workflow-step.ps1'
        Set-Content -LiteralPath $wrapperPath -Value $wrapper
        $result = Invoke-TestProcess $wrapperPath $fixture
        $uploadReached = Test-Path -LiteralPath (Join-Path $fixture 'upload-reached')
        $publicationReached = Test-Path -LiteralPath (Join-Path $fixture 'publication-reached')
        $finalPassed = $result.Output -match 'PASS: release tag, package version, changelog, and artifact contract\.'
        Write-Output "$($case.Name): exit=$($result.ExitCode); finalPassed=$finalPassed; upload=$uploadReached; publication=$publicationReached"
        if (($result.ExitCode -eq 0) -ne $case.Pass -or $uploadReached -ne $case.Pass -or $publicationReached -ne $case.Pass) {
            $failures.Add("$($case.Name): unexpected workflow exit or reachable downstream work. $($result.Output)")
        }
        if ($case.Failure -ne '' -and $finalPassed) {
            $failures.Add("$($case.Name): first-gate failure did not stop the final gate.")
        }
        if ($case.Failure -eq 'nested' -and ($result.Output -notmatch 'exit code 17' -or (Test-Path -LiteralPath (Join-Path $fixture 'nested-continued')))) {
            $failures.Add("$($case.Name): the shipping nested check did not reject exit 17.")
        }
        if ($case.Failure -eq 'metadata' -and ($result.Output -notmatch 'classification disagrees' -or (Test-Path -LiteralPath (Join-Path $fixture 'metadata-continued')))) {
            $failures.Add('Website metadata rejection did not stop the checked package call and downstream release work.')
        }
        if ($case.Pass -and -not $finalPassed) { $failures.Add('Positive control did not execute the real final validator.') }
        if ($case.Name -eq 'first passes and final fails' -and $result.Output -notmatch 'no dated release section') {
            $failures.Add('Final failure did not come from the real changelog validator.')
        }
        if ($case.Name -eq 'late failure with both artifacts') {
            $artifacts = @(Get-ChildItem -LiteralPath (Join-Path $fixture 'artifacts/packages') -File)
            $finalControl = Invoke-TestProcess (Join-Path $fixture 'scripts/Invoke-ReleaseContract.ps1') $fixture @('-Tag', 'v0.1.0', '-ArtifactDirectory', 'artifacts/packages')
            if ($artifacts.Count -ne 2 -or $finalControl.ExitCode -ne 0) {
                $failures.Add('Late-failure fixture must retain both artifacts and independently pass the unchanged real final validator.')
            }
            Write-Output "Late-failure control: artifacts=$($artifacts.Count); standalone real final validator exit=$($finalControl.ExitCode)"
        }
    }
    # Run the actual publish commands with a native substitute. It records the
    # implicit adjacent-symbol behavior too, so duplicate submissions cannot hide.
    $publishSteps = [regex]::Matches($workflow, '(?ms)^      - name: Push (?:symbols )?package to NuGet.org\r?\n        shell: pwsh\r?\n        run: >-\r?\n(?<body>.*?)(?=^      - name:|\z)')
    if ($publishSteps.Count -ne 2) { throw 'Expected exact primary and symbol publication steps.' }
    foreach ($failureAt in @('none', 'primary', 'symbols')) {
        $fixture = Join-Path $testRoot "publish-$failureAt"
        New-Item -ItemType Directory -Force -Path $fixture | Out-Null
        Copy-Item -LiteralPath (Join-Path $repositoryRoot 'build/Invoke-NestedPwsh.ps1') -Destination $fixture
        $logPath = Join-Path $fixture 'submissions.txt'
        $stubPath = Join-Path $fixture 'dotnet-substitute.ps1'
        $stub = @'
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$PushArguments)
$ErrorActionPreference = 'Stop'
if ($PushArguments[0] -ne 'nuget' -or $PushArguments[1] -ne 'push') { throw 'Unexpected substitute command.' }
if ($PushArguments -contains '--skip-duplicate') { throw 'Duplicate masking is prohibited.' }
$artifacts = @($PushArguments | Where-Object { $_ -match '\.(?:nupkg|snupkg)$' })
if ($artifacts.Count -ne 1) { throw 'Expected exactly one immutable artifact per command.' }
$artifact = [IO.Path]::GetFileName($artifacts[0])
Add-Content -LiteralPath (Join-Path $PSScriptRoot 'submissions.txt') -Value $artifact
if ($artifact.EndsWith('.nupkg') -and $PushArguments -notcontains '--no-symbols') {
    Add-Content -LiteralPath (Join-Path $PSScriptRoot 'submissions.txt') -Value $artifact.Replace('.nupkg', '.snupkg')
}
'@
        foreach ($stepIndex in 0..1) {
            $command = ($publishSteps[$stepIndex].Groups['body'].Value -split '\r?\n' | Where-Object { $_ -match '\S' } | ForEach-Object { $_.Trim() }) -join ' '
            $command = $command.Replace('${{ needs.verify.outputs.version }}', '0.1.0').Replace('${{ steps.nuget-login.outputs.NUGET_API_KEY }}', 'substitute-only')
            $exitCode = if (($failureAt -eq 'primary' -and $stepIndex -eq 0) -or ($failureAt -eq 'symbols' -and $stepIndex -eq 1)) { 17 } else { 0 }
            Set-Content -LiteralPath $stubPath -Value ($stub + "`nexit $exitCode`n")
            $wrapper = @'
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Invoke-NestedPwsh.ps1')
function dotnet {
    $forwardedArguments = @('-NoProfile', '-NonInteractive', '-File', (Join-Path $PSScriptRoot 'dotnet-substitute.ps1')) + $args
    Invoke-NestedPwsh -ArgumentList $forwardedArguments
}
'@
            $wrapper += "`n$command`n" + 'if (Test-Path -LiteralPath variable:\LASTEXITCODE) { exit $LASTEXITCODE }' + "`n"
            $wrapperPath = Join-Path $fixture "publish-step-$stepIndex.ps1"
            Set-Content -LiteralPath $wrapperPath -Value $wrapper
            $result = Invoke-TestProcess $wrapperPath $fixture
            if ($result.ExitCode -ne $exitCode) { $failures.Add("$failureAt step $stepIndex did not propagate exit $exitCode. $($result.Output)") }
            # Match ordinary GitHub step success gating. A failed primary command
            # must not reach the second command; a failed symbol command fails the job.
            if ($result.ExitCode -ne 0) { break }
        }
        $submissions = @(Get-Content -LiteralPath $logPath)
        $expected = @('KeelMatrix.FeedFence.0.1.0.nupkg')
        if ($failureAt -ne 'primary') { $expected += 'KeelMatrix.FeedFence.0.1.0.snupkg' }
        if (($submissions -join ',') -ne ($expected -join ',')) { $failures.Add("$failureAt submitted an unexpected or duplicate artifact: $($submissions -join ',')") }
        if (($result.ExitCode -eq 0) -ne ($failureAt -eq 'none')) { $failures.Add("$failureAt publication result was incorrectly successful.") }
        Write-Output "Publication substitute: failure=$failureAt; finalExit=$($result.ExitCode); submissions=$($submissions -join ',')"
    }
    if ($failures.Count -ne 0) { throw ($failures -join "`n") }
    Write-Output 'PASS: actual release workflow stops after each rejected gate; nested shipping checks propagate failure and the positive control reaches downstream sentinels.'
}
finally {
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    if ($resolvedRoot.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase) -and $resolvedRoot -ne $tempParent -and (Test-Path -LiteralPath $resolvedRoot)) {
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}
