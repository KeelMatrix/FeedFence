# Phase 0 fixture corpus

The probe copies these templates into a disposable run directory and replaces
`__RUN_ROOT__` with that directory. The corpus intentionally contains only
synthetic local file feeds and configuration values.

- `hierarchy/` covers machine, user, and repository configuration, active and
  disabled sources, exact/prefix/wildcard mapping, and equal-specificity
  duplicate eligibility.
- `clear/` proves repository `<clear />` behavior.
- `nomapping/` proves multiple active sources without mapping.
- `casing/` proves that a case-variant mapping source key follows current NuGet matching behavior.
- `explicit/` proves an explicit config override.
- `provenance/` proves nested, missing-user, outside-repository, and
  external-explicit-config provenance cases.
- `parse-safety/` contains malformed NuGet XML, DTD/external-entity XML, and
  malformed JSON inputs that must be rejected.

Every generated package also contains `feedfence-origin.txt`, whose content is
the feed marker. The probe reads it after restore to independently identify the
source that supplied the package.

Machine settings have an isolated official-API control on every OS. For actual
SDK restores, macOS places the machine-named fixtures in additional user config
because NuGet's machine directory cannot be redirected there. The probe asserts
the resulting user provenance and retains all mapping/restore controls; it does
not write host machine settings. Windows/Linux retain native redirected-machine
coverage. See the [probe guide](../README.md) for the explicit placement switch.
