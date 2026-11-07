# KeelMatrix FeedFence

**FeedFence checks whether your restored NuGet graph can come only from the sources you intended.** It evaluates effective `nuget.config` settings and Package Source Mapping, then fails on ambiguous, incomplete, or machine-dependent restore policy.

## Install

```text
dotnet tool install --global KeelMatrix.FeedFence
dotnet restore
feedfence check
```

FeedFence reads existing `project.assets.json` and/or `packages.lock.json`, together with the SDK's evaluated `obj/<project-file-name>.nuget.dgspec.json` restore inputs. It checks framework coverage, direct dependencies, and dependency edges from both package and project nodes. Missing nodes, incomplete framework containers, and type conflicts fail with exit code `2`, with no partial success report. Genuine SDK `ProjectReference` and zero-package restores remain valid. Checks follow the selected root/framework: its direct dependencies remain required, while fully private package or project-reference edges in referenced projects do not flow into that root. Partial privacy and asset filtering do not excuse missing nodes; an independently included dependency remains required. FeedFence never runs restore, evaluates project XML, contacts a package feed, invokes a credential provider, or connects to a database. Restore the repository first and restore again after changing project inputs or build properties.

Package IDs use NuGet's normal package-ID validation, including Unicode and long valid IDs. Unsafe or malformed identities such as credential-shaped URLs, control-character payloads, quotes, backslashes, and path-like values fail closed before they can reach reports.

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

Name a target explicitly with `feedfence check ./src/App/App.csproj`. Restore must already have produced the graph and evaluated input evidence described above; missing, malformed, or disagreeing artifacts fail with exit code `2`. Every declared solution member must be supported and readable. Malformed or unsupported members fail the whole analysis; solution folders are preserved as folders.

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

`path` may be one solution, one project, or an omitted path (the current directory). `--config <path>` replaces NuGet hierarchy discovery with the specified configuration; NuGet's implicit machine defaults still apply. `--policy <path>` selects an explicit policy file, including an intentional external file, instead of [implicit repository policy discovery](#repository-policy). `--strict` promotes inherited active-source warning `FF005` to a violation. `--format` defaults to deterministic text and also supports deterministic JSON and SARIF.

Successful reports are written only to stdout. Invocation and analysis errors are written only to stderr; machine output is never mixed with progress or noise.

Exit codes are stable:

* `0` — analysis completed and policy passed; warnings and informational diagnostics may still be shown.
* `1` — one or more policy violations were found.
* `2` — invocation, configuration, restore-artifact, or analysis failure.

## Diagnostics

* `FF001` — multiple active sources are available without mapping (violation). Add mapping or reduce active sources; this does not prove feed trust by itself.
* `FF002` — multiple sources remain at the same winning specificity (violation). Make the exact/prefix/wildcard winner unique; textual overlap at lower specificity is not enough.
* `FF003` — mapping is active and a direct or transitive resolved package has no eligible active source, including configurations with zero enabled sources (violation). Correct mapping and source settings; FeedFence does not restore for you.
* `FF004` — a mapping source key does not correspond to any configured source key (violation). NuGet accepts case-only differences; genuinely invalid source identities still fail. URLs do not define source identity.
* `FF005` — an active source comes from inherited or external configuration (warning; violation with `--strict`). Move required policy into the repository or document intentional inheritance.
* `FF006` — an enabled source uses plain HTTP or disables TLS certificate validation (violation). Use HTTPS with certificate validation or a narrow reasoned exception. Disabled sources are inapplicable; ordinary HTTPS and local file feeds are allowed. FeedFence does not validate reachability or credentials.
* `FF007` — a protected/private package can resolve outside its declared trust set (violation). Map it to the trusted source or add a reasoned narrow exception.
* `FF008` — informational reminder that mapping limits package downloads, not every NuGet metadata query. It never changes the exit code.

Mapping selection follows NuGet specificity across the complete mapping set: exact package ID, then the longest matching prefix, then `*`. Unknown or disabled winning source keys do not promote a less-specific mapping. Unknown keys produce `FF004`; a package left without an eligible source produces `FF003`. Equally winning enabled sources produce `FF002`. Diagnostics name package IDs, safe source labels, winning mapping patterns, and normalized configuration provenance. Sensitive source keys are represented by stable opaque labels; URLs, credentials, query strings, usernames, and full local paths are not printed.

## Effective configuration, `<clear />`, and two feeds

NuGet combines repository, user, and computer-wide configuration. `<clear />` resets the relevant collection before repository entries are added.

Additional user `.config` and `.Config` files are ordered as one combined set. Analysis never creates a missing user configuration. If it is absent and repository/path configuration does not clear package sources, FeedFence exits `2` because NuGet can initialize default sources. Run `dotnet nuget list source` yourself to initialize it, or supply `--config`. A repository `<packageSources><clear /></packageSources>` permits read-only analysis without that user file. The separate `NuGetDefaults.config` input remains applicable even with `<clear />` or `--config` and is bounded before NuGet parses it.

Repository control uses the complete physical ancestry of both the source configuration and repository root, including symbolic links and junctions. An unresolved physical path is reported as uncontrolled, never accepted from its spelling alone.

A public/private configuration keeps source and mapping keys exactly aligned:

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

This section and the command contract above define policy discovery and loading. Git metadata is optional. A directory input first resolves one project or solution. From that selected file's logical directory and fully resolved physical directory, FeedFence searches both ancestor chains for `feedfence.json`, including file aliases. Each search includes the nearest `.git` directory or file and stops there; without Git it continues to the filesystem root. Intermediate project or solution directories do not stop the search. The same entry reached through aliases counts once. No policy entry in either completed search means optional policy is absent. Distinct ancestor entries, conflicting repository boundaries for one entry, or a selected target redirected outside its implicit policy boundary fail with exit `2`. A policy above either ancestry cannot silently disappear. Supply an intentional `--policy` override to select one file.

For a unique, consistent implicit policy, its ancestor search's physical Git root is the repository boundary. Without Git, the policy's physical directory supplies it. With no policy, provenance uses the physical ancestry's Git root, then the logical ancestry's Git root, or the selected physical directory when neither has Git. NuGet settings still use the selected target's original restore context; `--config` does not disable FeedFence policy discovery.

An implicit policy must resolve to a readable regular file physically inside that boundary. Complete path ancestry, symbolic links, junctions and link chains are resolved before containment is checked; links to internal files are accepted even when an ancestor is reached through a filesystem alias. Unreadable discovery directories, malformed policies, directories at the policy path, dangling/cyclic/unresolvable links and outside targets fail with exit `2` and no stdout report in text, JSON or SARIF. Policy FIFOs, sockets and devices are rejected by filesystem type before opening, including through aliases and explicit overrides, so a FIFO needs no producer to fail promptly. Only directory or regular-file `.git` marker types may bound discovery; other present types or unresolved markers fail with exit `2` rather than hide ancestor policy. Regular Git marker files remain opaque boundary markers; FeedFence does not validate Git internals. Only a completed absence check permits analysis without a policy. `--policy` replaces implicit policy selection and may select an external file or override an invalid or ambiguous default; the explicit file must still be regular, readable and valid. Policy files are limited to 256 KiB and JSON depth 32. These checks assume inputs are stable during analysis; they do not provide atomic isolation from concurrent filesystem changes.

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

Source labels `private`, `trusted`, `internal`, and `repository` satisfy protected/private package rules. Every exception must name a diagnostic, a package pattern or source key, and a non-empty reason. Schema-v1 policy objects reject unknown or duplicate members, conflicting selector aliases, and duplicate array selectors. Malformed or unreadable policy is an analysis failure (`2`), never a clean pass.

## Machine output contracts

JSON has schema version `1`, fixed property order, and sorted sources/diagnostics. Its top-level fields are `schemaVersion`, `format`, `tool`, `exitCode`, `summary`, `sources`, and `diagnostics`; `summary` contains `resolvedPackageCount`, `activeSourceCount`, `packageSourceMappingEnabled`, `deterministicMappingCount`, and `diagnosticCount`. Each JSON diagnostic contains `code`, `severity`, and `message`, with optional `packageId` and redacted `sourceKeys`; it does not contain `id` or `title`. Source values/URLs are intentionally omitted. SARIF is `2.1.0`, declares rules `FF001`–`FF008` in code order, and uses the same identities and severity meanings. Neither format includes timestamps, paths, or run-specific fields. See [the versioned report contract](docs/report-contract.md) for the exact shape.

## Telemetry and privacy

FeedFence requests one best-effort shared `KeelMatrix.Telemetry` activation only after a completed analysis has at least one resolved package and one effective source-policy evaluation. Installation, assembly loading, parse failures, and no-package runs do not activate telemetry, and telemetry failure cannot change output or exit code.

FeedFence calls the shared client's parameterless activation API with the tool name `feedfence` and the FeedFence assembly type. It does not attach a FeedFence-specific summary, analysis counts, outcomes, diagnostics, package/source identities, URLs, policy contents, paths, or credentials. The shared activation contract supplies its own tool/version, anonymous project and installation hashes, runtime, OS, CI, and timestamp fields as documented in [Privacy](PRIVACY.md). Opt out with `KEELMATRIX_NO_TELEMETRY=1`; local validation sets this variable.

## Limitations and privacy

FeedFence verifies the last restore's evaluated inputs and resolved graph at the standard output locations. It does not scan vulnerabilities or licenses, query package availability, validate credentials, rewrite configuration, invoke restore, or claim to be a network-isolation sandbox. Assets schemas `3`/`4`, lock schemas `1`/`2`, and evaluated input schema `1` are supported. Assets and evaluated inputs are bounded to 32 MiB each; lock files to 8 MiB. Each graph is limited to 50,000 libraries/resolved packages, 256 frameworks, and 100,000 target-library or dependency-edge references. Evaluated input evidence permits at most 4,096 projects and 128 nested project references. Framework containers must be nonempty even when they resolve no packages. Package/project dependency edges must close within each corresponding target. Declared dependency targets must use supported NuGet names or flag combinations; package-bearing targets require package records and non-package targets require compatible records. Malformed, incomplete, unsupported, or inconsistent evidence fails with exit `2`. NuGet configuration files, including implicit defaults, are limited to 2 MiB, XML depth 64 and 100,000 elements before eager parsing. XML DTD and external entity processing is disabled.

The tool has no supported in-process library API in v1. It targets `net8.0`.

## Supported Platforms

FeedFence supports Windows, Linux, and macOS. The repository's three-OS CI matrix runs the Release build, CLI contracts, NuGet-equivalence probe, platform-specific configuration/path fixtures, exact package inspection, and installed-tool consumer gate on each supported operating system.

Native Linux and macOS tests also exercise policy FIFOs without producers, sockets and existing character devices, and invalid Git marker types, through shipping and installed commands. Windows exercises regular-file, directory, link and access controls; Unix special objects do not apply there.

## Troubleshooting and non-goals

- Missing restore artifacts or evaluated inputs: run `dotnet restore` for the same target and intended configuration/TFMs; retain its `.nuget.dgspec.json` companion. FeedFence never restores or executes MSBuild for you.
- Absent user NuGet configuration: initialize it with `dotnet nuget list source`, use an explicit `--config`, or clear inherited package sources in repository configuration.
- Malformed config/policy or assets disagreement: fix the input and rerun; FeedFence fails closed with exit `2`.
- Ambiguous policy discovery: remove the unintended ancestor policy or select the intended file with `--policy`. For an externally linked default, use a repository-owned file or an intentional explicit override.
- `FF004`: mapping identity is matched to the configured source key using NuGet's case-insensitive source identity; case-only differences are accepted, while an unknown source key fails.
- `FF005`: inspect user/machine configuration and move required policy into the repository, or use strict mode.

FeedFence is not a restore engine, feed client, network-isolation sandbox, vulnerability/license scanner, credential validator, package-content scanner, configuration rewriter, automatic mapping generator, remote policy service, or supported in-process API. It does not prove feed reachability or credential validity.

## License

FeedFence is distributed under the [MIT License](LICENSE).
