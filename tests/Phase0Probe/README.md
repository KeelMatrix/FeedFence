# Phase 0 feasibility probe

This probe is the repository's source of truth for the foundation milestone.
It tests official `NuGet.Configuration` 7.9.0 effective values against actual
`dotnet restore` behavior using only synthetic local file feeds.

The fixture corpus covers:

- computer-, user-, and repository-scope settings;
- active and disabled sources;
- repository `<clear />` behavior;
- exact-ID, longest-prefix, wildcard, and equal-specificity duplicate mappings;
- an unmapped package that fails restore;
- a source-key casing variant using the current NuGet behavior;
- an explicit `--configfile` override;
- provenance from official source-item `ConfigPath` values;
- absence of network-capable sources and credential-provider paths during restore.

The probe's recommendation is `PASS (go)` when all effective values and restore
outcomes agree and provenance classifies source origin. A `NARROW` or `STOP`
result is reserved for a future API/client contradiction and must include its
bounded evidence in the run output.
