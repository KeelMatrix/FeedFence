# KeelMatrix FeedFence

FeedFence checks whether a restored NuGet graph can come only from the sources
you intended. This repository currently contains the Phase 0 feasibility probe
for validating that contract against official NuGet configuration APIs and
actual offline restore behavior.

The probe is not the full FeedFence CLI. It creates synthetic computer-, user-,
and repository-scope configuration fixtures, evaluates them with
`NuGet.Configuration`, and runs `dotnet restore` only against local file feeds.

## Validate the probe

From the repository root:

```text
dotnet build tests/Phase0Probe/KeelMatrix.FeedFence.Phase0Probe.csproj -c Release
dotnet run --project tests/Phase0Probe/KeelMatrix.FeedFence.Phase0Probe.csproj -c Release --no-build
```

The probe reports a `PASS`, `NARROW`, or `STOP` recommendation and exits with
code `0` only when all fixture assertions pass.

The detailed fixture contract is in
`tests/Phase0Probe/README.md`. The local validation helper is
`scripts/Invoke-Phase0Probe.ps1`.

The packable project is the future `KeelMatrix.FeedFence` .NET tool boundary.
The command surface is intentionally not implemented in this milestone.
