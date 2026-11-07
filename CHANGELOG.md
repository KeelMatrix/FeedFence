# Changelog

Consumer-facing capabilities and release history for FeedFence.

## [Unreleased]

### Fixed

- Inspect policy and Git marker types correctly on Intel macOS by pairing the native metadata entry point with its inode64 layout; validate native Intel and arm64 source and installed commands separately.

- Reject non-regular policy files before opening them, including FIFOs without producers, sockets, devices and resolved aliases; invalid `.git` object types fail closed without hiding ancestor policy.

- Preserve ancestor repository policy in Git-free source trees and reject ambiguous, unreadable, non-file or physically external implicit policies before emitting a report; explicit external `--policy` overrides remain supported.
- Resolve complete policy link ancestry before checking repository ownership, so internal targets reached through filesystem aliases remain valid.
- Check logical and physical target ancestry together so source-directory links cannot hide policy; conflicting boundaries fail closed and explicit overrides remain available.
- Discover policy from the resolved project/solution for directory inputs and retain both ancestry views for selected file aliases.

### Added

- Offline NuGet source-policy checks for already-restored SDK projects and whole
  solutions, using evaluated restore inputs and complete package/project
  dependency graphs, respecting private dependency flow for the selected project
  and framework. Incomplete evidence fails without a partial success report.
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
