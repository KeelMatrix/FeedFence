# KeelMatrix FeedFence

FeedFence checks whether your restored NuGet graph can come only from the sources you intended. It evaluates effective `nuget.config` settings and Package Source Mapping without restoring packages or contacting feeds.

## Quick start

```text
dotnet tool install --global KeelMatrix.FeedFence
dotnet restore
feedfence check
```

FeedFence reads existing `project.assets.json` and/or `packages.lock.json`. Missing or incomplete restore artifacts fail with exit code `2` and an instruction to restore first.

## Command contract

```text
feedfence check [path] [options]
feedfence --help
feedfence --version
```

Use `--config <path>` to replace NuGet hierarchy discovery, `--policy <path>` for an explicit `feedfence.json`, and `--strict` to promote inherited-source warning `FF005` to a violation. Text output is deterministic; machine formats are a separate milestone.

Exit codes: `0` means analysis and policy passed, `1` means one or more policy violations, and `2` means invocation, environment, or analysis failure.

Diagnostics are stable from `FF001` through `FF008`. Exact package IDs, prefix patterns, wildcard patterns, and equal-specificity outcomes follow NuGet Package Source Mapping semantics. Reports use source keys and normalized provenance labels; credentials, authenticated URLs, query strings, usernames, and full local paths are never printed. `FF008` is informational only.

The optional repository-root `feedfence.json` supports source trust labels, protected/private package patterns, and narrow exceptions. Every exception must include a target and a non-empty reason. See the repository README for the complete policy schema and limitations.

The tool targets `net8.0`, has no supported in-process API in v1, and makes no analysis-time network or credential-provider calls.
