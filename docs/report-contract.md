# FeedFence Report Contract

This document defines the versioned machine-report contract for
`KeelMatrix.FeedFence`. It applies to the v1 CLI and is maintained with the
reporting implementation and contract tests.

## JSON version 1

Run `feedfence check --format json`. The output is UTF-8 JSON with stable
property order, no timestamps or run-specific fields, and these top-level
properties:

- `schemaVersion` — number `1`.
- `format` — `json`.
- `tool` — object containing `name` and `version`.
- `exitCode` — `0`, `1`, or `2`.
- `summary` — resolved package count, active source count, mapping-enabled
  state, deterministic mapping count, and diagnostic count.
- `sources` — sorted safe source labels and normalized provenance labels; feed
  URLs and source values are omitted.
- `diagnostics` — ordered diagnostic objects with stable `id`, `title`,
  `severity`, and safe explanatory details.

JSON is intended for automation. Successful reports are written to stdout;
invocation and analysis failures are written to stderr and do not mix progress
text into the JSON document.

## SARIF version 2.1.0

Run `feedfence check --format sarif`. The result uses SARIF `2.1.0`, declares
rules `FF001` through `FF008` in code order, and maps FeedFence violations to
SARIF `error`, warnings to `warning`, and informational diagnostics to `note`.
It uses the same diagnostic identities and redaction rules as JSON.

## Compatibility rules

The `schemaVersion` value changes only when a consumer-visible JSON shape or
meaning changes incompatibly. Additive fields remain within version `1` when
existing consumers can safely ignore them. Diagnostic IDs and exit-code
meaning are stable v1 contracts. A contract change requires:

1. updated CLI contract tests and consumer smoke coverage;
2. updated root and package documentation;
3. a `CHANGELOG.md` entry under the applicable release section; and
4. an explicit compatibility review before release.

The report contract does not promise package-feed reachability, credential
validation, or network isolation. FeedFence remains an offline analyzer during
the core analysis operation.
