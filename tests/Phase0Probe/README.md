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
- independent package-origin markers and source-isolation restore controls for
  every mapping/hierarchy fixture, including one-feed-disabled controls;
- `FF005` classification for nested-project, missing-user-config,
  outside-repository, and external explicit-override provenance cases;
- a separator-aware provenance classifier regression for sibling repository
  prefixes;
- malformed NuGet XML, DTD/external-entity XML, and malformed JSON fixtures;
- absence of network-capable sources and credential-provider paths during restore.

Each synthetic package contains a distinct `feedfence-origin.txt` marker. The
probe reads that marker from the isolated global-packages folder after restore,
then repeats restore with each active source isolated and with each expected
source disabled. This is independent evidence of the actual eligible set; it
does not call NuGet's mapping API to assert the restore result.

The probe's recommendation is `PASS (go)` when all effective values, marker-
based restore outcomes, provenance cases, and parse-safety fixtures agree. A
`NARROW` or `STOP` result is reserved for a future API/client contradiction and
must include its bounded evidence in the run output.

NuGet 7.9.0 can materialize a missing user `NuGet.Config` while loading default
settings. The probe records this behavior and performs it only in its
disposable run directory; the fixture corpus and active repository are not
modified. FeedFence analysis must account for this side effect to remain
read-only with respect to the user's active worktree.
