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
dotnet restore KeelMatrix.FeedFence.sln --configfile NuGet.config --no-cache --force
dotnet build KeelMatrix.FeedFence.sln -c Release --no-restore
pwsh ./scripts/Invoke-ReleaseWarningsAsErrors.ps1 -Configuration Release
dotnet run --project tests/FeedFenceCliTests/KeelMatrix.FeedFence.CliTests.csproj -c Release --no-build
dotnet run --project tests/Phase0Probe/KeelMatrix.FeedFence.Phase0Probe.csproj -c Release --no-build
```

Run the repository gates:

```powershell
pwsh ./scripts/Invoke-PlatformFixtures.ps1 -Configuration Release
pwsh ./scripts/Invoke-PackGate.ps1 -Configuration Release
pwsh ./scripts/Invoke-DependencyAudit.ps1
pwsh ./scripts/Invoke-RepoHygiene.ps1
pwsh ./scripts/Invoke-RepoHygiene.ps1 -SelfTest
pwsh ./scripts/Invoke-ReleaseContract.ps1 -SelfTest
dotnet format KeelMatrix.FeedFence.sln --verify-no-changes --no-restore
```

The platform fixture runs the same artifact against the current OS. The Phase 0
probe and package gate use synthetic/offline fixtures where specified; neither
should be changed to contact a real package feed during analysis.

## Release preparation

The shipping project is the only packable project. The package gate validates
the complete `.nupkg` and `.snupkg` archive allowlists, nuspec/tool/runtime and
SourceLink metadata, sensitive-input rejection, and icon byte identity. It
installs the exact inspected `.nupkg` from a single-source local feed with
isolated NuGet caches. The release workflow gives the gate its upload directory,
so the inspected artifacts are the artifacts uploaded to the publish job.

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
