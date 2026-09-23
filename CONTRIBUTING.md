# Contributing

Thanks for helping improve KeelMatrix FeedFence. Bug reports, documentation
updates, and focused pull requests are welcome.

## Before you begin

- Check existing issues before opening a new one.
- Read the [Code of Conduct](CODE_OF_CONDUCT.md).
- Report suspected vulnerabilities through [SECURITY.md](SECURITY.md), not a
  public issue.
- Do not include credentials, authenticated feed URLs, private package names,
  or full local paths in issues, fixtures, reports, or pull requests.

## Development prerequisites

- .NET SDK `8.0.425` (the repository pins this in `global.json`).
- PowerShell 7 (`pwsh`) for the repository validation scripts.
- Git.

Restore from the repository-controlled source list before building:

```powershell
dotnet restore KeelMatrix.FeedFence.sln --configfile NuGet.config --no-cache --force
```

## Validation

Run the focused checks in this order after a relevant change:

```powershell
dotnet build KeelMatrix.FeedFence.sln -c Release --no-restore
pwsh ./scripts/Invoke-ReleaseWarningsAsErrors.ps1 -Configuration Release
dotnet run --project tests/FeedFenceCliTests/KeelMatrix.FeedFence.CliTests.csproj -c Release --no-build
dotnet run --project tests/Phase0Probe/KeelMatrix.FeedFence.Phase0Probe.csproj -c Release --no-build
pwsh ./scripts/Invoke-PlatformFixtures.ps1 -Configuration Release
pwsh ./scripts/Invoke-PackGate.ps1 -Configuration Release
pwsh ./scripts/Invoke-DependencyAudit.ps1
pwsh ./scripts/Invoke-RepoHygiene.ps1
pwsh ./scripts/Invoke-RepoHygiene.ps1 -SelfTest
pwsh ./scripts/Invoke-ReleaseContract.ps1 -SelfTest
dotnet format KeelMatrix.FeedFence.sln --verify-no-changes --no-restore
```

Set `KEELMATRIX_NO_TELEMETRY=1` during local validation. The pack gate and
consumer checks already set it for their isolated runs.

The Phase 0 probe uses synthetic local feeds and must remain offline after its
fixtures are prepared. It must continue to use the official NuGet APIs rather
than a replacement configuration engine.

## Making changes

Keep changes narrow and add or update tests for user-visible behavior. For CLI
changes, update the root and package READMEs together and verify help text,
exit codes, stdout/stderr separation, and deterministic machine output.

Do not add a supported in-process API or a public API baseline to the shipping
tool without an approved product-scope change.

## Pull requests

Describe the problem, the behavior changed, the tests run, and any platform or
environment evidence that could not be verified locally. Complete the pull
request checklist. Keep commits and public documentation in normal
developer-facing language.
