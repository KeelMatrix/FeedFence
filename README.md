# KeelMatrix FeedFence

**FeedFence checks whether your restored NuGet graph can come only from the sources you intended.** It evaluates effective `nuget.config` settings and Package Source Mapping, then fails on ambiguous, incomplete, or machine-dependent restore policy.

## Install

```text
dotnet tool install --global KeelMatrix.FeedFence
dotnet restore
feedfence check
```

FeedFence reads existing `project.assets.json` and/or `packages.lock.json`. For assets files, every declared dependency in `projectFileDependencyGroups` must be represented in the corresponding framework target and library graph. Every dependency identified as a package by `project.frameworks.<tfm>.dependencies` must resolve to identity-matched, package-typed target and library records. An unexplained omission or type conflict fails with exit code `2`; a genuine SDK `ProjectReference` or empty declared-and-resolved package graph remains valid. FeedFence never runs restore, contacts a package feed, invokes a credential provider, or connects to a database. Restore the repository first.

## Tool lifecycle

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

## Quick Start

```text
dotnet restore
feedfence check
```

Name a target explicitly with `feedfence check ./src/App/App.csproj`. Restore must already have produced `project.assets.json` or `packages.lock.json`; missing, malformed, or disagreeing artifacts fail with exit code `2`.

## Documentation

- [Developer guide](docs/DEV.md) — repository prerequisites and validation gates.
- [Report contract](docs/report-contract.md) — JSON and SARIF compatibility rules.
- [Security policy](SECURITY.md) — private vulnerability reporting and product boundaries.
- [Privacy](PRIVACY.md) — telemetry and data-handling contract.
- [Contributing](CONTRIBUTING.md) and [Code of Conduct](CODE_OF_CONDUCT.md) — community guidance.

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
* `FF004` — a mapping source key does not correspond to any configured source key (violation). NuGet accepts case-only differences; genuinely invalid source identities still fail. URLs do not define source identity.
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

FeedFence calls the shared client's parameterless activation API with the tool name `feedfence` and the FeedFence assembly type. It does not attach a FeedFence-specific summary, analysis counts, outcomes, diagnostics, package/source identities, URLs, policy contents, paths, or credentials. The shared activation contract supplies its own tool/version, anonymous project and installation hashes, runtime, OS, CI, and timestamp fields as documented in [Privacy](PRIVACY.md). Opt out with `KEELMATRIX_NO_TELEMETRY=1`; local validation sets this variable.

## Limitations and privacy

FeedFence verifies resolved local restore artifacts and effective NuGet configuration. It does not scan vulnerabilities or licenses, query package availability, validate credentials, rewrite configuration, invoke restore, or claim to be a network-isolation sandbox. Configuration, XML, JSON, and restore inputs are size/depth/count bounded; assets graphs accept at most 50,000 libraries/resolved packages, 256 target frameworks, and 100,000 target-library or declared-dependency references. Assets files must contain object-valued `targets`, `libraries`, `projectFileDependencyGroups`, and `project.frameworks`; each declared dependency must appear in every corresponding framework target and have an identity-matched library record. Target and library types must agree, and dependencies whose official target is `Package` must use `package` records. Malformed, incomplete, unsupported, or inconsistent graphs fail closed with exit code `2`, while genuine project records and an empty declared-and-resolved package graph remain valid. XML DTD and external entity processing is disabled.

The tool has no supported in-process library API in v1. It targets `net8.0`.

## Supported Platforms

FeedFence supports Windows, Linux, and macOS. The repository's three-OS CI matrix runs the Release build, CLI contracts, NuGet-equivalence probe, platform-specific configuration/path fixtures, exact package inspection, and installed-tool consumer gate on each supported operating system.

## Troubleshooting and non-goals

- Missing restore artifacts: run `dotnet restore` for the same target; FeedFence never restores for you.
- Malformed config/policy or assets disagreement: fix the input and rerun; FeedFence fails closed with exit `2`.
- `FF004`: mapping identity is matched to the configured source key using NuGet's case-insensitive source identity; case-only differences are accepted, while an unknown source key fails.
- `FF005`: inspect user/machine configuration and move required policy into the repository, or use strict mode.

FeedFence is not a restore engine, feed client, network-isolation sandbox, vulnerability/license scanner, credential validator, package-content scanner, configuration rewriter, automatic mapping generator, remote policy service, or supported in-process API. It does not prove feed reachability or credential validity.

## License

FeedFence is distributed under the [MIT License](LICENSE).
