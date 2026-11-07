# FeedFence Report Contract

This document is the source of truth for FeedFence's v1 policy discovery,
filesystem input requirements, exit behavior and machine reports. The README,
built help and package documentation summarize this contract.

## Policy discovery and filesystem inputs

A directory input first selects one project or solution. From that file's
logical directory and fully resolved physical directory, discovery searches
both ancestor chains for `feedfence.json`, including file aliases. Each search
includes the nearest `.git` directory or regular file and stops there; without
Git it continues to the filesystem root. Intermediate project/solution
directories do not stop discovery. The same policy entry reached through aliases
counts once. Optional policy is absent only after both searches complete.

Distinct policy entries, conflicting ownership boundaries for one entry, or a
selected target redirected outside its implicit policy boundary fail with exit
`2`. A unique implicit policy uses its ancestry's physical Git root as the
repository boundary, or its physical directory without Git. With no policy,
provenance uses the physical view's Git root, then the logical view's Git root,
then the selected physical directory. NuGet settings retain the selected
target's original restore context; `--config` does not disable policy discovery.

An implicit policy must resolve to a readable regular file physically inside
that boundary. Complete path ancestry, symbolic links, junctions and link chains
resolve before containment checks. Internal regular-file aliases are accepted.
Unreadable discovery, malformed/non-file policies, unresolved/dangling/cyclic
links and external implicit targets fail closed. FIFOs, sockets and devices
are rejected by filesystem type before opening, including aliases and explicit
overrides; a FIFO needs no producer to fail promptly. Only directory or
regular-file `.git` marker types may stop discovery. Other present marker types
and unresolved markers fail instead of hiding ancestor policy. Regular Git
marker files are opaque; Git internals are not validated.

`--policy` intentionally replaces implicit policy selection and may select an
external file or override an invalid/ambiguous default. It still requires a
regular, readable, valid file, at most 256 KiB and JSON depth 32. The
[README policy schema](../README.md#repository-policy) describes its fields.
Filesystem type decisions use mode bits, independent of inode values. Native
metadata entry points and layouts must agree on supported OS/CPU paths,
including Intel and arm64 macOS. Inputs must stay stable during analysis;
these separate metadata/read operations do not provide atomic isolation from
concurrent filesystem changes.

Completed analysis writes only a deterministic text/JSON/SARIF report to stdout:
exit `0` means policy passed and `1` means a policy violation, including `FF007`
for protected-package escapes. Invocation, environment and analysis failures
exit `2`, write a safe error only to stderr, and leave stdout empty in every
format. Analysis does not mutate inputs. Neither a timeout nor a harness-killed
process is a successful fail-closed result.

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
  URLs and source values are omitted. Each source includes `key`, `enabled`,
  `provenance` and `repositoryControlled`. Repository control uses complete
  physical ancestry for the config and repository boundary. Unresolved paths
  have unknown inherited provenance and `repositoryControlled: false`.
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

Policy discovery and loading follow the filesystem contract above.
Ambiguous, unreadable, non-file or physically external implicit policy inputs
produce exit `2`, a stderr error and empty stdout in every format. Non-regular
policy objects and invalid Git marker types follow the same error contract,
including through aliases; FIFO rejection needs no producer. Optional policy
absence requires completed logical and physical discovery. Conflicting
entries or ownership boundaries, and a selected target outside its implicit
policy repository, are analysis failures. Protected-package findings from a
loaded policy remain `FF007` in a completed exit-`1` report; this repair does not
change the machine-report schema.

Package coverage follows the selected root/framework, including NuGet private
child dependency flow. Direct-root and independently included dependencies remain
required. This does not change the version-1 report shape or exit-code meanings.

Incomplete restore graphs/evaluated inputs and malformed solution membership
produce exit `2` with no JSON/SARIF success document. Unknown winning mapping
keys produce `FF004` and, when no configured enabled winner remains, `FF003`
within a completed policy report (exit `1`), including configurations with zero
enabled sources. `FF006` covers enabled HTTP sources
and disabled TLS certificate validation. These triggers use the version-1
report shape and existing diagnostic identities.

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
