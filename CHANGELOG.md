# Changelog

Consumer-facing capabilities and release history for FeedFence.

## [Unreleased]

### Added

- Offline NuGet source-policy checks for already-restored SDK projects and whole
  solutions, using evaluated restore inputs and complete package/project
  dependency graphs. Incomplete evidence fails without a partial success report.
- NuGet configuration hierarchy and Package Source Mapping analysis with
  exact/prefix/wildcard specificity, source identity checks, physical repository
  provenance, protected-package policy and narrow reasoned exceptions.
- Stable `FF001`–`FF008` diagnostics, including inherited active sources, HTTP
  sources and disabled TLS certificate validation, with deterministic text,
  JSON and SARIF output and redacted sensitive values.
- Read-only configuration analysis with pre-parse input bounds, explicit
  handling of absent user defaults and implicit machine configuration.
- A .NET 8 tool for Windows, Linux and macOS, with opt-out-aware shared
  activation measurement and isolated installed-package validation.
