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
- Added fail-closed restore-graph completeness validation, including official
  package-target metadata and target/library type agreement, alongside
  solution-folder-aware target discovery and repository-relative sensitive
  package-input checks.
