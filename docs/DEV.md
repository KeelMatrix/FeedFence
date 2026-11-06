# FeedFence Developer Guide

This guide covers repository development and release-equivalent validation. It
is not the installation guide for FeedFence users; use the [root README](../README.md)
for consumer setup.

## Prerequisites

- .NET SDK `8.0.425`, selected by `global.json`.
- PowerShell 7 (`pwsh`).
- Git.

Check the SDK before starting:

```powershell
dotnet --info
```

## Build and test

Restore with the repository-controlled `NuGet.config`:

```powershell
dotnet restore KeelMatrix.FeedFence.sln --configfile NuGet.config --no-cache --locked-mode
dotnet build KeelMatrix.FeedFence.sln -c Release --no-restore
pwsh ./scripts/Invoke-ReleaseWarningsAsErrors.ps1 -Configuration Release
dotnet run --project tests/FeedFenceCliTests/KeelMatrix.FeedFence.CliTests.csproj -c Release --no-build
dotnet run --project tests/Phase0Probe/KeelMatrix.FeedFence.Phase0Probe.csproj -c Release --no-build
```

Run the repository gates:

```powershell
pwsh ./scripts/Invoke-PlatformFixtures.ps1 -Configuration Release
pwsh ./scripts/Invoke-PackGate.ps1 -Configuration Release
pwsh ./scripts/Invoke-ReproducibilityGate.ps1 -Configuration Release
pwsh ./scripts/Invoke-LockContract.ps1
pwsh ./scripts/Invoke-DependencyAudit.ps1
pwsh ./scripts/Invoke-RepoHygiene.ps1
pwsh ./scripts/Invoke-RepoHygiene.ps1 -SelfTest
pwsh ./scripts/Invoke-ReleaseContract.ps1 -SelfTest
pwsh ./scripts/Test-ReleaseOrchestration.ps1
dotnet format KeelMatrix.FeedFence.sln --verify-no-changes --no-restore
```

The platform fixture runs the same artifact against the current OS. The Phase 0
probe and package gate use synthetic/offline fixtures where specified; neither
should be changed to contact a real package feed during analysis.

The [repository hygiene check](../scripts/Invoke-RepoHygiene.ps1) owns the exact
commit identity allowlists, including the KeelMatrix account's GitHub ID-based
noreply address. It checks every reachable commit, including other branches and
tags, for prohibited attribution and internal wording. Its self-test covers
identity lookalikes, message subjects and bodies, and a failing process exit.

The CLI contracts and installed-package gate include an unknown dependency
target and a declared-dependency omission that must fail with exit code `2`,
all official package-bearing target flag values, a supported non-package
target, a separate true zero-package graph that must pass, and a solution
containing a dotted solution folder plus a real project. The package gate also
proves that safe inputs below parent directories
named `Temp`, `tmp`, and `artifacts` are accepted while secret-bearing and
repository-internal inputs remain rejected.

The CLI suite also starts with real local-feed SDK restores and mutates their
assets/lock graphs. It covers package/project dependency closure, zero-package
and multi-framework containers, evaluated conditions/imports/item operations,
complete solution membership, NuGet configuration differentials and pre-parse
bounds, physical provenance, mapping winners and effective source security.
The package gate repeats these cases against its isolated installed command.
For focused development, run the built test executable with `--families-only`;
add `--tool <installed-feedfence-path>` to exercise an installed artifact.
`tests/Write-RestoreFixture.ps1` supplies evaluated-input companions only for
synthetic parser fixtures; the restore-equivalence tests use real SDK output.

## Release preparation

The shipping project is the only packable project. The package gate validates
the complete `.nupkg` and `.snupkg` archive allowlists, nuspec/tool/runtime and
SourceLink metadata, sensitive-input rejection, and icon byte identity. It
installs the exact inspected `.nupkg` from a single-source local feed with
isolated NuGet caches. The release workflow gives the gate its upload directory,
so the inspected artifacts are the artifacts uploaded to the publish job.

The [release workflow](../.github/workflows/release.yml) checks each native
validator's exit immediately. A rejected package gate stops the release before
the final contract check; rejection by either gate prevents artifact upload and
the dependent publish job. The [workflow test](../scripts/Test-ReleaseOrchestration.ps1)
executes that workflow body locally with synthetic first-gate results and the
real final validator. It covers early and late failure, both expected files
already present, final-gate rejection, nested shipping-helper failure, and a
passing control. It also executes the actual primary/symbol publish command
bodies through a native recording substitute. The primary command uses
`--no-symbols`; the exact symbol artifact has its own command. The controls
assert one submission per artifact and failure propagation for both commands,
without duplicate masking. They never upload or publish packages.

The package gate normalizes NuGet archive metadata before checking the exact
archive allowlist. Release builds also normalize the generated managed PE
`TimeDateStamp` field to zero before packaging. Reproducibility is claimed for
the normalized shipping DLL/PDB bytes and normalized `.nupkg`/`.snupkg` entry
names and bytes across clean checkout roots and repeated pack; raw ZIP
container byte identity is not promised. Clean reproducibility checkouts omit
the physical icon and pack the unchanged canonical root asset by an explicit
input path; no icon copy is created in a validation checkout. The lock contract proves that the
committed graph restores in locked mode and that a mutated lock fails closed.

The release contract check is intentionally expected to fail while the
changelog remains under `[Unreleased]`:

```powershell
pwsh ./scripts/Invoke-ReleaseContract.ps1 -Tag v0.1.0
```

Its self-test proves that an unreleased target, a version mismatch,
first-release remediation wording, and a first-release category other than
`Added` fail closed, while a finalized first-release fixture passes.

Finalize the changelog for the release version and verify it on the exact commit
before it is tagged. Run the repository hygiene check before pushing and in the
tag-triggered workflow before obtaining a short-lived NuGet credential. Do not
create a tag or publish a package as part of ordinary local validation.

## Telemetry and fixtures

Keep `KEELMATRIX_NO_TELEMETRY=1` set for local repository work. Fixtures must
use synthetic package IDs, feeds, and configuration and must not contain
credentials, authenticated URLs, or customer data.
