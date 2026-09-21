# KeelMatrix FeedFence

**FeedFence checks whether your restored NuGet graph can come only from the sources you intended.** It evaluates effective `nuget.config` settings and Package Source Mapping, then fails on ambiguous, incomplete, or machine-dependent restore policy.

## Install and quick start

```text
dotnet tool install --global KeelMatrix.FeedFence
dotnet restore
feedfence check
```

FeedFence reads existing `project.assets.json` and/or `packages.lock.json`. It never runs restore, contacts a package feed, invokes a credential provider, or connects to a database. Restore the repository first.

## Command contract

```text
feedfence check [path] [options]
feedfence --help
feedfence --version
```

`path` may be one solution, one project, or an omitted path (the current directory). `--config <path>` replaces NuGet hierarchy discovery with the specified configuration. `--policy <path>` selects an explicit policy file; otherwise `feedfence.json` is read from the repository root when present. `--strict` promotes inherited active-source warning `FF005` to a violation. This milestone provides deterministic text output through `--format text`; machine-readable formats are planned separately.

Exit codes are stable:

* `0` — analysis completed and policy passed; warnings and informational diagnostics may still be shown.
* `1` — one or more policy violations were found.
* `2` — invocation, configuration, restore-artifact, or analysis failure.

## Diagnostics

* `FF001` — multiple active sources are available without Package Source Mapping (violation).
* `FF002` — multiple sources remain at the same winning mapping specificity (violation).
* `FF003` — a resolved package has no eligible active mapped source (violation).
* `FF004` — a mapping source key does not exactly match a configured source key (violation).
* `FF005` — an active source comes from inherited or external configuration (warning; violation with `--strict`).
* `FF006` — a source uses plain HTTP (violation).
* `FF007` — a protected/private package can resolve outside its declared trust set (violation).
* `FF008` — informational reminder that Package Source Mapping limits package downloads, not every NuGet metadata query. It never changes the exit code.

Mapping selection follows NuGet specificity: exact package ID, then the longest matching prefix, then `*`; equal winning specificity remains ambiguous. Diagnostics name package IDs, source keys, winning patterns, and normalized configuration provenance. URLs, credentials, query strings, usernames, and full local paths are not printed.

## Repository policy

An optional `feedfence.json` keeps policy deliberately small. The schema is version `1`:

```json
{
  "version": 1,
  "sourceTrust": {
    "nuget.org": "public",
    "company": "private"
  },
  "protectedPackages": [
    { "pattern": "Company.*", "allowedSources": ["company"] }
  ],
  "privatePackages": ["Company.Internal.*"],
  "exceptions": [
    {
      "code": "FF006",
      "sourceKey": "legacy",
      "reason": "Temporary local exception approved for the migration window."
    }
  ]
}
```

Source labels `private`, `trusted`, `internal`, and `repository` satisfy protected/private package rules. Every exception must name a diagnostic, a package pattern or source key, and a non-empty reason. Malformed or unreadable policy is an analysis failure (`2`), never a clean pass.

## Limitations and privacy

FeedFence verifies resolved local restore artifacts and effective NuGet configuration. It does not scan vulnerabilities or licenses, query package availability, validate credentials, rewrite configuration, invoke restore, or claim to be a network-isolation sandbox. Configuration, XML, JSON, and restore inputs are size/depth/count bounded; malformed or unsupported input fails closed. XML DTD and external entity processing is disabled.

The tool has no supported in-process library API in v1. It targets `net8.0` and is intended for Windows, Linux, and macOS. The package contains no telemetry integration in this milestone.

## Development validation

```powershell
dotnet build KeelMatrix.FeedFence.sln -c Release
dotnet run --project tests/FeedFenceCliTests/KeelMatrix.FeedFence.CliTests.csproj -c Release --no-build
dotnet run --project tests/Phase0Probe/KeelMatrix.FeedFence.Phase0Probe.csproj -c Release --no-build
pwsh ./scripts/Invoke-PackGate.ps1 -Configuration Release
```

The Phase 0 probe remains the NuGet-equivalence guard. It uses synthetic local file feeds only and does not contact real package sources.
