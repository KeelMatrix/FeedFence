# KeelMatrix FeedFence

**FeedFence checks whether your restored NuGet graph can come only from the sources you intended.** It evaluates effective `nuget.config` settings and Package Source Mapping, then fails on ambiguous, incomplete, or machine-dependent restore policy.

## Install and quick start

```text
dotnet tool install --global KeelMatrix.FeedFence
dotnet restore
feedfence check
```

FeedFence reads existing `project.assets.json` and/or `packages.lock.json`. It never runs restore, contacts a package feed, invokes a credential provider, or connects to a database. Restore the repository first.

## Install, update, and uninstall

Global tool:

```text
dotnet tool install --global KeelMatrix.FeedFence --version 0.1.0
dotnet tool update --global KeelMatrix.FeedFence
dotnet tool uninstall --global KeelMatrix.FeedFence
```

Repository-local tool:

```text
dotnet new tool-manifest
dotnet tool install --local KeelMatrix.FeedFence --version 0.1.0
dotnet tool update --local KeelMatrix.FeedFence
dotnet tool uninstall --local KeelMatrix.FeedFence
```

## Five-minute quick start

```text
dotnet restore
feedfence check
```

Name a target explicitly with `feedfence check ./src/App/App.csproj`. Restore must already have produced `project.assets.json` or `packages.lock.json`; missing, malformed, or disagreeing artifacts fail with exit code `2`.

## Command contract

```text
feedfence check [path] [options]
feedfence --help
feedfence --version
```

`path` may be one solution, one project, or an omitted path (the current directory). `--config <path>` replaces NuGet hierarchy discovery with the specified configuration. `--policy <path>` selects an explicit policy file; otherwise `feedfence.json` is read from the repository root when present. `--strict` promotes inherited active-source warning `FF005` to a violation. `--format` defaults to deterministic text and also supports deterministic JSON and SARIF.

Successful reports are written only to stdout. Invocation and analysis errors are written only to stderr; machine output is never mixed with progress or noise.

Exit codes are stable:

* `0` — analysis completed and policy passed; warnings and informational diagnostics may still be shown.
* `1` — one or more policy violations were found.
* `2` — invocation, configuration, restore-artifact, or analysis failure.

## Diagnostics

* `FF001` — multiple active sources are available without mapping (violation). Add mapping or reduce active sources; this does not prove feed trust by itself.
* `FF002` — multiple sources remain at the same winning specificity (violation). Make the exact/prefix/wildcard winner unique; textual overlap at lower specificity is not enough.
* `FF003` — a direct or transitive resolved package has no eligible mapped source (violation). Correct mapping and restore; FeedFence does not restore for you.
* `FF004` — a mapping source key does not exactly match a configured source key (violation). Match spelling and casing; URLs do not define source identity.
* `FF005` — an active source comes from inherited or external configuration (warning; violation with `--strict`). Move required policy into the repository or document intentional inheritance.
* `FF006` — a source uses plain HTTP (violation). Use HTTPS or a narrow documented exception; FeedFence does not validate reachability or credentials.
* `FF007` — a protected/private package can resolve outside its declared trust set (violation). Map it to the trusted source or add a reasoned narrow exception.
* `FF008` — informational reminder that mapping limits package downloads, not every NuGet metadata query. It never changes the exit code.

Mapping selection follows NuGet specificity: exact package ID, then the longest matching prefix, then `*`; equal winning specificity remains ambiguous. Diagnostics name package IDs, safe source labels, winning mapping patterns, and normalized configuration provenance. Sensitive source keys are represented by stable opaque labels; URLs, credentials, query strings, usernames, and full local paths are not printed.

## Effective configuration, `<clear />`, and two feeds

NuGet combines repository, user, and computer-wide configuration. `<clear />` resets the relevant collection before repository entries are added. A public/private configuration keeps source and mapping keys exactly aligned:

```xml
<packageSources>
  <clear />
  <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  <add key="company" value="https://nuget.example.invalid/company/index.json" />
</packageSources>
<packageSourceMapping>
  <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  <packageSource key="company"><package pattern="Company.*" /></packageSource>
</packageSourceMapping>
```

Exact IDs beat the longest prefix, and the longest prefix beats `*`. An equal winner is `FF002`. A successful restore only proves that NuGet found packages; FeedFence checks the deterministic repository-owned policy. Inherited active sources produce `FF005` (warning, or violation with `--strict`). Package Source Mapping does not constrain every NuGet metadata query, and FeedFence never contacts feeds during analysis.

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

## Machine output contracts

JSON has schema version `1`, fixed property order, and sorted sources/diagnostics. Its top-level fields are `schemaVersion`, `format`, `tool`, `exitCode`, `summary`, `sources`, and `diagnostics`; source values/URLs are intentionally omitted. SARIF is `2.1.0`, declares rules `FF001`–`FF008` in code order, and uses the same identities and severity meanings. Neither format includes timestamps, paths, or run-specific fields.

## Telemetry and privacy

FeedFence requests one best-effort shared `KeelMatrix.Telemetry` activation only after a completed analysis has at least one resolved package and one effective source-policy evaluation. Installation, assembly loading, parse failures, and no-package runs do not activate telemetry, and telemetry failure cannot change output or exit code.

The bounded FeedFence summary is limited to FeedFence version, .NET major version, broad OS family, resolved-package-count bucket, active-source-count bucket, mapping-enabled state, coarse result class, and diagnostic-count bucket. It never adds package/source identities, URLs, policy contents, paths, credentials, or raw diagnostics. Opt out with `KEELMATRIX_NO_TELEMETRY=1`; local validation sets this variable.

## Limitations and privacy

FeedFence verifies resolved local restore artifacts and effective NuGet configuration. It does not scan vulnerabilities or licenses, query package availability, validate credentials, rewrite configuration, invoke restore, or claim to be a network-isolation sandbox. Configuration, XML, JSON, and restore inputs are size/depth/count bounded; assets graphs accept at most 50,000 libraries/resolved packages, 256 target frameworks, and 100,000 target-library references. Malformed, incomplete, unsupported, or impossible nonempty graphs fail closed. XML DTD and external entity processing is disabled.

The tool has no supported in-process library API in v1. It targets `net8.0` and is intended for Windows, Linux, and macOS.

## Compatibility evidence

Run `pwsh ./scripts/Invoke-PlatformFixtures.ps1 -Configuration Release`. The disposable offline fixture checks platform configuration locations, path separators, an absolute `file://` local feed, casing, and JSON output. Windows passed locally on the current host. Linux and macOS fixture runs remain unverified here; no remote CI evidence is claimed for this private repository.

## Troubleshooting and non-goals

- Missing restore artifacts: run `dotnet restore` for the same target; FeedFence never restores for you.
- Malformed config/policy or assets disagreement: fix the input and rerun; FeedFence fails closed with exit `2`.
- `FF004`: mapping identity is the configured source key, including casing; match `<packageSource key>` to `<add key>` exactly.
- `FF005`: inspect user/machine configuration and move required policy into the repository, or use strict mode.

FeedFence is not a restore engine, feed client, network-isolation sandbox, vulnerability/license scanner, credential validator, package-content scanner, configuration rewriter, automatic mapping generator, remote policy service, or supported in-process API. It does not prove feed reachability or credential validity.

## Development validation

```powershell
dotnet build KeelMatrix.FeedFence.sln -c Release
dotnet run --project tests/FeedFenceCliTests/KeelMatrix.FeedFence.CliTests.csproj -c Release --no-build
dotnet run --project tests/Phase0Probe/KeelMatrix.FeedFence.Phase0Probe.csproj -c Release --no-build
pwsh ./scripts/Invoke-PackGate.ps1 -Configuration Release
pwsh ./scripts/Invoke-DependencyAudit.ps1
```

The Phase 0 probe remains the NuGet-equivalence guard. It uses synthetic local file feeds only and does not contact real package sources.
