# Phase 0 fixture corpus

The probe copies these templates into a disposable run directory and replaces
`__RUN_ROOT__` with that directory. The corpus intentionally contains only
synthetic local file feeds and configuration values.

- `hierarchy/` covers machine, user, and repository configuration, active and
  disabled sources, exact/prefix/wildcard mapping, and equal-specificity
  duplicate eligibility.
- `clear/` proves repository `<clear />` behavior.
- `nomapping/` proves multiple active sources without mapping.
- `casing/` proves a source-key casing mismatch.
- `explicit/` proves an explicit config override.
- `provenance/` proves nested, missing-user, outside-repository, and
  external-explicit-config provenance cases.
- `parse-safety/` contains malformed NuGet XML, DTD/external-entity XML, and
  malformed JSON inputs that must be rejected.

Every generated package also contains `feedfence-origin.txt`, whose content is
the feed marker. The probe reads it after restore to independently identify the
source that supplied the package.
