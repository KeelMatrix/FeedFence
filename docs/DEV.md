# FeedFence Developer Guide

This guide covers repository development and release-equivalent validation. It
is not the installation guide for FeedFence users; use the [root README](../README.md)
for consumer setup.

## Prerequisites

- .NET SDK `8.0.425`, selected by `global.json`.
- PowerShell 7 (`pwsh`).
- Git.
- Python 3 on Linux/macOS for native filesystem object regression tests.

Check the SDK before starting:

```powershell
dotnet --info
```

## Build and test

Restore with the repository-controlled `NuGet.config`:

Ordinary CLI and Visual Studio restores use the ignored `.nuget/packages`
folder configured there. This keeps packages built locally for other repositories
out of this repository's dependency cache, and keeps restored dependencies outside
the `artifacts` directory cleared before release packaging. Committed lock hashes are still
required. An explicit `NUGET_PACKAGES`, `--packages`, or `RestorePackagesPath`
override selects a different cache; conflicting package bytes must fail with
`NU1403`. Do not update locks to accept an unexplained cached package or clear
the entire shared NuGet cache to work around it.

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
The [probe guide](../tests/Phase0Probe/README.md) distinguishes native machine
directory redirection from the macOS additional-user fixture placement and its
separate injected-machine API control. Both retain the same behavioral assertions.

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

Both test projects keep argument dispatch and failure exits in small entry
points. `tests/Phase0Probe/ProbeRunner.cs` lists the ordered probe suites;
source/mapping, provenance and parse-safety cases have separate files, with
fixture construction and process execution in `ProbeFixtures.cs` and
`ProbeProcesses.cs`. `tests/FeedFenceCliTests/CliTestRunner.cs` selects the CLI
suites; `CliFixture.cs` lists the ordered synthetic cases. Report, telemetry,
mapping, source security, graph, policy, identity and solution cases are grouped
by responsibility, with `CliFixtures.cs` and `CliProcesses.cs` providing their
shared fixture and process support. The real-restore families retain their
existing files and run after the synthetic cases.

The CLI suite also starts with real local-feed SDK restores and mutates their
assets/lock graphs. It covers package/project dependency closure, zero-package
and multi-framework containers, evaluated conditions/imports/item operations,
complete solution membership, NuGet configuration differentials and pre-parse
bounds, physical provenance, mapping winners and effective source security.
The package gate repeats these cases against its isolated installed command.
For focused development, run the built test executable with `--families-only`;
add `--tool <installed-feedfence-path>` to exercise an installed artifact.
Use `--private-flow-only` for the real local-feed package/project privacy cases,
including nested and independently included paths, asset flags, selected
frameworks, all three artifact modes and applicable-node omission negatives.
These cases run in both the shipping suite and the isolated installed-package gate.
Use `--policy-only` for real-restored repository policy discovery/loading cases:
Git-free nested projects/root solutions, ordinary Git and worktree markers,
multiple ancestor entries, optional absence, unusable paths, complete link ancestry,
portable absolute ancestor aliases and alias chains,
inward/outward source-directory links with logical/physical policy conflicts,
resolved parent-directory selection and real SDK-restored project/solution file aliases,
actual permission denials and explicit external overrides. Every case runs in
text, JSON and SARIF and checks read-only behavior; failed analysis must emit no
stdout report. Windows uses ACL denial and junctions; Unix uses mode denial and
symbolic links. File-link tests require link privilege on Windows and report a
skip if it is unavailable. The [policy and report contract](report-contract.md)
owns the discovery/loading semantics.
CI requires separate Intel (`macos-15-intel`) and arm64 (`macos-latest`) macOS
jobs, in addition to Linux and Windows. `FEEDFENCE_TEST_ARCHITECTURE` checks the
actual .NET test process architecture. `--metadata-only` runs a supplementary
mode/permission/inode buffer replay and native-symbol/layout selection checks;
it does not replace native execution. On Unix, `--policy-runner-failure-control`
must exit `1` with the intentional Python failure: CI verifies that the actual
native regression failure reaches the CLI runner before accepting the job.
`--policy-only` also runs `PolicySpecialFiles.py` on native Linux/macOS; use
`--policy-objects-only` to run that bounded family alone. Python 3 and working
FIFO/socket capabilities are required there; unavailable capabilities fail the
gate. Cases use a real local-feed Company.Secret restore, nested project and
solution, every report format, direct/linked/chained/explicit policies, invalid
Git marker types, existing character devices, valid marker/removal and clean
absence controls. The tests inspect metadata without reading special objects,
require prompt product exit `2`, safe stderr, empty stdout and unchanged inputs,
and kill timed-out process groups as failures. Relative socket binding supports
long macOS temporary paths. The same suite runs through the exact installed
package in the pack gate. Windows reports these Unix-only classes as N/A and
retains its own policy/link/ACL cases. No privileged device creation is needed.
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
Its offline [cache regression](../scripts/Test-RestoreCache.ps1) also exercises
ordinary build/restore with a conflicting inherited cache, explicit and
environment cache overrides, and separate shipping/transitive test lock
mutations. It requires real `NU1403` failures to reach the build process exit.
It also builds a real dependency and synthetic tool, clears release artifacts,
then packs with `--no-restore` to prove that cleanup preserves restored inputs.
Run `pwsh ./scripts/Invoke-LockContract.ps1 -SelfTest` for that focused regression.

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
