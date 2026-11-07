# KeelMatrix FeedFence

FeedFence checks whether your restored NuGet graph can come only from the sources you intended. It evaluates effective `nuget.config` settings and Package Source Mapping without restoring packages or contacting feeds.

## Install

```text
dotnet tool install --global KeelMatrix.FeedFence
dotnet restore
feedfence check
```

Use a repository-local tool manifest with `dotnet new tool-manifest` and
`dotnet tool install --local KeelMatrix.FeedFence --version 0.1.0`. Update with
`dotnet tool update --global KeelMatrix.FeedFence` or `dotnet tool update --local KeelMatrix.FeedFence`; uninstall with the corresponding `dotnet tool uninstall`
command.

FeedFence reads existing `project.assets.json` and/or `packages.lock.json`, together with evaluated `obj/<project-file-name>.nuget.dgspec.json` restore inputs. It checks framework coverage, direct dependencies, and dependency edges from package and project nodes. Incomplete or inconsistent evidence fails with exit `2` without a partial report. Genuine SDK `ProjectReference` and zero-package restores remain valid. Checks follow the selected root/framework: its direct dependencies remain required, while fully private package or project-reference edges in referenced projects do not flow into that root. Partial privacy and asset filtering do not excuse missing nodes; an independently included dependency remains required. Restore again after changing project inputs or build properties; FeedFence analyzes the last restore and never evaluates project XML or runs MSBuild. Every declared solution member must be supported and readable; malformed members fail the whole analysis, while solution folders remain valid.

Package IDs use NuGet's normal package-ID validation, including Unicode and long valid IDs. Unsafe or malformed identities such as credential-shaped URLs, control-character payloads, quotes, backslashes, and path-like values fail closed before they can reach reports.

## Command contract

```text
feedfence check [path] [options]
feedfence --help
feedfence --version
```

Use `--config <path>` to replace NuGet hierarchy discovery (implicit machine defaults still apply), `--policy <path>` for an explicit `feedfence.json`, `--strict` to promote inherited-source warning `FF005` to a violation, and `--format text|json|sarif` for deterministic text, JSON schema `1`, or SARIF `2.1.0`. Reports go only to stdout; invocation and analysis errors go only to stderr.

Exit codes: `0` means analysis and policy passed, `1` means one or more policy violations, and `2` means invocation, environment, or analysis failure.

Diagnostics are stable from `FF001` through `FF008`. Exact package IDs, prefix patterns, wildcard patterns, equal-specificity outcomes, and case-insensitive source-key identity follow NuGet Package Source Mapping semantics. Case-only mapping/source-key differences are accepted; `FF004` is reserved for a mapping key with no configured source identity. Reports use safe source labels and normalized provenance labels; sensitive source keys are represented by stable opaque labels. Credentials, authenticated URLs, query strings, usernames, and full local paths are never printed. `FF008` is informational only.

The optional `feedfence.json` supports source trust labels, protected/private package patterns, and narrow exceptions. A directory input first resolves one project or solution; discovery then searches that file's logical and fully physical directory ancestors, including file aliases, through each view's nearest `.git` directory/file or the filesystem root without Git. Intermediate project/solution directories do not stop it, and the same entry through aliases counts once. Distinct policy entries, conflicting ownership boundaries or a selected target redirected outside its implicit policy repository fail with exit `2`. For one consistent policy its search's physical Git root defines ownership, or its physical policy directory without Git. With no policy, provenance falls back to the physical view's Git root, then the logical view's Git root, then the selected physical directory. NuGet's restore context stays independent. Implicit policies must resolve to readable regular files inside that physical repository boundary; complete links and ancestors resolve before containment is checked. Unreadable discovery, malformed/non-file policies, dangling/cyclic links and outside final targets fail with exit `2` and empty stdout in every format. `--policy` replaces implicit policy selection and may intentionally select an external file or override an invalid/ambiguous default; explicit files must still be regular, valid and readable. Policy size is limited to 256 KiB and JSON depth 32. Inputs must remain stable during analysis; concurrent filesystem changes are not isolated atomically. Every exception must include a target and a non-empty reason. The [policy and report contract](https://github.com/KeelMatrix/FeedFence/blob/main/docs/report-contract.md) is the complete source of truth for these rules and limitations.

The tool targets `net8.0`, has no supported in-process API in v1, and makes no analysis-time network or credential-provider calls. Package Source Mapping limits package downloads, not every NuGet metadata query; `FF008` is informational only.

Configuration and restore inputs are bounded. Assets schemas `3`/`4`, lock schemas `1`/`2`, and evaluated input schema `1` are supported. Assets and evaluated inputs are limited to 32 MiB each; locks to 8 MiB. Graph limits are 50,000 libraries/resolved packages, 256 frameworks and 100,000 target-library/dependency-edge references. Evaluated inputs permit 4,096 projects and 128 nested project references. Required framework containers remain nonempty for zero-package restores. Dependency targets use supported NuGet names/flag combinations and compatible record types. All NuGet configuration inputs, including `NuGetDefaults.config`, are checked before eager parsing against 2 MiB, XML depth 64 and 100,000 elements; DTD/external entity processing is disabled.

NuGet configuration is hierarchical and `<clear />` resets a collection. Exact
mapping IDs beat the longest prefix, which beats `*`; equal winners produce
`FF002`. Inherited active sources produce `FF005`. A successful restore does not
prove repository-owned deterministic policy.

Unknown or disabled winning mapping keys cannot promote less-specific patterns: unknown keys produce `FF004`, and no eligible source produces `FF003`, including when mapping is active with zero enabled/configured sources. `FF006` reports enabled HTTP sources and HTTPS sources that disable TLS certificate validation; normal HTTPS/local feeds pass, and narrow exceptions require a target and reason. Repository provenance resolves complete path ancestry, including links and junctions; unresolved paths are uncontrolled.

Analysis never creates a missing user configuration. If no repository/path `packageSources` clear removes possible user defaults, an absent user file produces exit `2`. Initialize it with `dotnet nuget list source` or use `--config`. Machine `NuGetDefaults.config` still applies even with an explicit config or clear. Additional user `.config`/`.Config` files share NuGet's combined ordering.

Policies must be readable regular files. FIFOs, sockets and devices are rejected
before opening, including aliases and explicit external overrides. Only directory
or regular-file `.git` marker types may bound discovery; invalid object types
fail with exit `2` and empty stdout. Git marker file contents remain opaque.

The optional policy schema supports `version: 1`, `sourceTrust`,
`protectedPackages`, `privatePackages`, and `exceptions`. Every exception needs
a target and non-empty `reason`. Schema-v1 policy objects reject unknown or
duplicate members, conflicting selector aliases, and duplicate array selectors.
The root repository README has the complete
two-feed example, all `FF001`–`FF008` trigger/remediation/severity/limitation
details, the JSON/SARIF field contracts, troubleshooting, and explicit
non-goals.

Activation uses shared `KeelMatrix.Telemetry` only after a completed analysis
with a resolved package and effective source-policy evaluation. FeedFence calls
the shared parameterless activation API and does not attach analysis counts,
results, diagnostics, package/source identities, URLs, policy contents, paths,
or credentials. The shared client owns the activation event fields and delivery
contract. Telemetry errors cannot alter a run. Opt out with
`KEELMATRIX_NO_TELEMETRY=1`.

FeedFence supports Windows, Linux, and macOS on .NET 8. CI separately validates
Windows, Linux, Intel macOS and arm64 macOS, including the actual .NET process
architecture, platform-specific fixtures and exact installed package.
Native Linux/macOS suites also cover policy and Git-marker special objects,
prompt failures, all report formats and unchanged inputs against both commands.

For the complete policy schema, diagnostic reference, and consumer-facing
limitations, see the [FeedFence repository README](https://github.com/KeelMatrix/FeedFence#readme).
The versioned JSON and SARIF compatibility rules are in the
[report contract](https://github.com/KeelMatrix/FeedFence/blob/main/docs/report-contract.md).
The shared activation fields, local state, delivery behavior, and opt-out
precedence are described in the
[FeedFence privacy contract](https://github.com/KeelMatrix/FeedFence/blob/main/PRIVACY.md).

## License

FeedFence is distributed under the [MIT License](https://github.com/KeelMatrix/FeedFence/blob/main/LICENSE).
