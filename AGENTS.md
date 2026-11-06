# FeedFence Development Guide

## Navigation

- `src/KeelMatrix.FeedFence` is the single packable `net8.0` .NET tool project.
- `tests/Phase0Probe` is the mandatory offline NuGet feasibility probe and its
  synthetic fixture corpus.
- `tests/FeedFenceCliTests` covers shipping and installed CLI contracts, including
  real SDK restore evidence and bounded malformed-input families.
- `scripts` contains local validation helpers.
- `README.md`, `SECURITY.md`, and `PRIVACY.md` are developer-facing contracts.

## Commands

Build the shipping project:

```text
dotnet build src/KeelMatrix.FeedFence/KeelMatrix.FeedFence.csproj -c Release
```

Build and run only the Phase 0 probe:

```text
dotnet build tests/Phase0Probe/KeelMatrix.FeedFence.Phase0Probe.csproj -c Release
dotnet run --project tests/Phase0Probe/KeelMatrix.FeedFence.Phase0Probe.csproj -c Release --no-build
```

Run the CLI contract tests, the host platform fixture, and the package gate:

```text
dotnet run --project tests/FeedFenceCliTests/KeelMatrix.FeedFence.CliTests.csproj -c Release --no-build
pwsh ./scripts/Invoke-PlatformFixtures.ps1 -Configuration Release
pwsh ./scripts/Invoke-ReleaseWarningsAsErrors.ps1 -Configuration Release
pwsh ./scripts/Invoke-PackGate.ps1 -Configuration Release
pwsh ./scripts/Invoke-DependencyAudit.ps1
pwsh ./scripts/Invoke-RepoHygiene.ps1
pwsh ./scripts/Invoke-RepoHygiene.ps1 -SelfTest
pwsh ./scripts/Invoke-ReleaseContract.ps1 -SelfTest
pwsh ./scripts/Test-ReleaseOrchestration.ps1
```

## Invariants

- The Phase 0 probe uses official `NuGet.Configuration` APIs for effective
  values and compares them with actual `dotnet restore` behavior.
- Probe restore sources are synthetic local file feeds. Do not restore probe
  fixtures from a real package feed.
- The probe must not implement a second NuGet configuration or mapping engine.
- Analysis is read-only with respect to fixture inputs and must not invoke
  network sources or credential providers.
- Use evaluated `.nuget.dgspec.json` restore inputs rather than interpreting
  project XML. Validate dependency closure and required framework/runtime targets
  in assets and lock graphs; NuGet lock runtime entries are base-framework deltas.
- Every declared solution member must be accounted for or fail explicitly.
- The shipping tool has no supported in-process library API in this milestone.
- Do not add a public API analyzer baseline to the tool project.

## Validation boundaries

Use the Phase 0 probe as the focused falsification gate. Record the exact
commands, output, and any unverified platform or environment evidence before
handing the repository to independent review.
