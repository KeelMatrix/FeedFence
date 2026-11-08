# Changelog

Consumer-facing capabilities and release history for FeedFence.

## [Unreleased]

## [0.1.0] - 2026-11-08

### Added

- Provides the .NET 8 `feedfence check` tool for already-restored SDK projects
  and solutions on Windows, Linux and macOS.
- Evaluates effective NuGet configuration, Package Source Mapping specificity
  and source provenance, with repository policy for protected packages, source
  trust and narrow reasoned exceptions.
- Reports stable `FF001`–`FF008` diagnostics in deterministic text, JSON v1
  and SARIF 2.1.0, with redacted sensitive values and exit codes `0`, `1` and `2`.
- Performs bounded, read-only analysis of evaluated restore inputs and complete
  applicable package/project dependency graphs. Incomplete or unusable evidence
  fails without a partial report; analysis never runs restore, contacts package
  feeds or invokes credential providers.
- Uses best-effort shared activation telemetry only after an eligible completed
  analysis, with no FeedFence-specific payload and opt-out through
  `KEELMATRIX_NO_TELEMETRY=1`.
