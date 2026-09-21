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
