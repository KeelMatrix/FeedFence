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
dotnet run --project tests/FeedFenceCliTests/KeelMatrix.FeedFence.CliTests.csproj -c Release --no-build
dotnet run --project tests/Phase0Probe/KeelMatrix.FeedFence.Phase0Probe.csproj -c Release --no-build
```

Run the repository gates:

```powershell
pwsh ./scripts/Invoke-PlatformFixtures.ps1 -Configuration Release
pwsh ./scripts/Invoke-PackGate.ps1 -Configuration Release
pwsh ./scripts/Invoke-DependencyAudit.ps1
dotnet format KeelMatrix.FeedFence.sln --verify-no-changes --no-restore
```

The platform fixture runs the same artifact against the current OS. The Phase 0
probe and package gate use synthetic/offline fixtures where specified; neither
should be changed to contact a real package feed during analysis.

## Release preparation

The shipping project is the only packable project. The package gate validates
the exact `.nupkg` and `.snupkg` allowlist and installs the tool from an
isolated local package source. It also checks the installed command, JSON
determinism, missing-artifact failures, dependency metadata, and package-content
exclusions.

The release contract check is intentionally expected to fail while the
changelog remains under `[Unreleased]`:

```powershell
pwsh ./scripts/Invoke-ReleaseContract.ps1 -Tag v0.1.0
```

After frontier approval, the release owner finalizes and verifies the changelog
on the exact commit to be tagged. The tag-triggered workflow reruns the same
check before obtaining a short-lived NuGet credential. Do not create a tag or
publish a package as part of ordinary local validation.

## Telemetry and fixtures

Keep `KEELMATRIX_NO_TELEMETRY=1` set for local repository work. Fixtures must
use synthetic package IDs, feeds, and configuration and must not contain
credentials, authenticated URLs, or customer data.
credentials, authenticated URLs, or customer data.
