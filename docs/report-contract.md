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
- `summary` — `resolvedPackageCount`, `activeSourceCount`,
  `packageSourceMappingEnabled`, `deterministicMappingCount`, and
  `diagnosticCount`.
- `sources` — sorted safe source labels and normalized provenance labels; feed
  URLs and source values are omitted.
- `diagnostics` — ordered diagnostic objects. Each object contains `code`,
  `severity`, and `message`; a package diagnostic may additionally contain
  `packageId`, and a source-related diagnostic may additionally contain the
  redacted `sourceKeys` array. The v1 JSON contract does not contain
  diagnostic `id` or `title` fields.

For an empty report, `diagnosticCount` is `0` and `diagnostics` is an empty
array. An informational `FF008` report uses `severity: "information"`; policy
violations such as `FF003` and `FF004` use `severity: "violation"`. All three
forms use the same required diagnostic fields and stable property order.

JSON is intended for automation. Successful reports are written to stdout;
invocation and analysis failures are written to stderr and do not mix progress
text into the JSON document.

## SARIF version 2.1.0

Run `feedfence check --format sarif`. The result uses SARIF `2.1.0`, declares
rules `FF001` through `FF008` in code order, and maps FeedFence violations to
SARIF `error`, warnings to `warning`, and informational diagnostics to `note`.
It uses the same diagnostic identities and redaction rules as JSON. SARIF
uses `ruleId` for the diagnostic code and `message.text` for the safe message;
the JSON-only `packageId` and `sourceKeys` fields are not emitted as raw SARIF
properties.

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
