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

FeedFence reads existing `project.assets.json` and/or `packages.lock.json`. For assets files, every declared dependency in `projectFileDependencyGroups` must be represented in the corresponding framework target and library graph. Missing, incomplete, or inconsistent restore artifacts fail with exit code `2` and an instruction to restore first; a genuinely empty declared-and-resolved graph remains valid.

## Command contract

```text
feedfence check [path] [options]
feedfence --help
feedfence --version
```

Use `--config <path>` to replace NuGet hierarchy discovery, `--policy <path>` for an explicit `feedfence.json`, `--strict` to promote inherited-source warning `FF005` to a violation, and `--format text|json|sarif` for deterministic text, JSON schema `1`, or SARIF `2.1.0`. Reports go only to stdout; invocation and analysis errors go only to stderr.

Exit codes: `0` means analysis and policy passed, `1` means one or more policy violations, and `2` means invocation, environment, or analysis failure.

Diagnostics are stable from `FF001` through `FF008`. Exact package IDs, prefix patterns, wildcard patterns, equal-specificity outcomes, and case-insensitive source-key identity follow NuGet Package Source Mapping semantics. Case-only mapping/source-key differences are accepted; `FF004` is reserved for a mapping key with no configured source identity. Reports use safe source labels and normalized provenance labels; sensitive source keys are represented by stable opaque labels. Credentials, authenticated URLs, query strings, usernames, and full local paths are never printed. `FF008` is informational only.

The optional repository-root `feedfence.json` supports source trust labels, protected/private package patterns, and narrow exceptions. Every exception must include a target and a non-empty reason. See the repository README for the complete policy schema and limitations.

The tool targets `net8.0`, has no supported in-process API in v1, and makes no analysis-time network or credential-provider calls. Package Source Mapping limits package downloads, not every NuGet metadata query; `FF008` is informational only.

Configuration and restore inputs are bounded. Assets graphs accept at most 50,000 libraries/resolved packages, 256 target frameworks, and 100,000 target-library or declared-dependency references. Assets files must contain object-valued `targets`, `libraries`, and `projectFileDependencyGroups`; each declared dependency must appear in every corresponding framework target and have a library record. Incomplete or inconsistent graphs fail closed with exit code `2`, while a genuinely empty declared-and-resolved graph passes with zero packages.

NuGet configuration is hierarchical and `<clear />` resets a collection. Exact
mapping IDs beat the longest prefix, which beats `*`; equal winners produce
`FF002`. Inherited active sources produce `FF005`. A successful restore does not
prove repository-owned deterministic policy.

The optional policy schema supports `version: 1`, `sourceTrust`,
`protectedPackages`, `privatePackages`, and `exceptions`. Every exception needs
a target and non-empty `reason`. The root repository README has the complete
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

FeedFence supports Windows, Linux, and macOS on .NET 8. The repository's CI
matrix validates its platform-specific configuration/path fixtures and the
installed package on all three operating systems.

For the complete policy schema, diagnostic reference, and consumer-facing
limitations, see the [FeedFence repository README](https://github.com/KeelMatrix/FeedFence#readme).
The versioned JSON and SARIF compatibility rules are in the
[report contract](https://github.com/KeelMatrix/FeedFence/blob/main/docs/report-contract.md).
The shared activation fields, local state, delivery behavior, and opt-out
precedence are described in the
[FeedFence privacy contract](https://github.com/KeelMatrix/FeedFence/blob/main/PRIVACY.md).

## License

FeedFence is distributed under the [MIT License](https://github.com/KeelMatrix/FeedFence/blob/main/LICENSE).
