# Changelog

## [Unreleased]

### Added

- Added a hermetic Phase 0 feasibility probe for NuGet configuration hierarchy,
  Package Source Mapping precedence, restore equivalence, and source-origin
  provenance.
- Added deterministic JSON and SARIF reports with stable `FF001`–`FF008`
  identities and an explicit stdout/stderr contract.
- Added opt-out-aware shared activation measurement, three-platform fixture
  coverage, and an isolated package-consumer smoke gate.
- Added fail-closed restore-graph completeness validation, including the
  official dependency-target domain, flag combinations, and compatible
  target/library record types, alongside
  solution-folder-aware target discovery and repository-relative sensitive
  package-input checks.

### Changed

- Package identity checks now use NuGet's normal validator, preserving valid Unicode and long IDs while rejecting unsafe or malformed identities before report rendering.
- Repository hygiene now accepts the approved GitHub web-flow committer for valid KeelMatrix-authored commits while retaining prohibited-attribution and internal-wording checks.
- Schema-v1 policy parsing now rejects unknown and duplicate members, restore
  validation rejects incomplete or type-laundered graphs, and machine reports
  use one documented JSON shape with redacted untrusted identities.
- CI and release restores use committed lock graphs and immutable action pins;
  verified releases publish both the tool and symbols packages, with a
  normalized two-root reproducibility gate for compiled bytes and package
  entry contents.
- Release builds normalize the generated shipping PE timestamp so DLL and
  package contents remain reproducible across checkout roots and compiler
  hosts.
