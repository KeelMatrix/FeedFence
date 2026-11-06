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

The probe also invokes the built shipping CLI for each restore-backed mapping
fixture and compares its exit code and diagnostics with the actual restore
outcome. The pack gate repeats the casing and disabled-exact ordering cases
through the installed tool from the generated package.

Windows and Linux redirect NuGet's machine configuration into the disposable
fixture. macOS NuGet fixes that directory under `/Library/Application Support`
and ignores `NUGET_COMMON_APPLICATION_DATA`. On macOS the machine-named source
fixtures are also installed in additional user configuration, so the real SDK
and shipping CLI see the same inputs without changing host machine files. Their
provenance is asserted as user scope. A separate official-API control checks the
isolated injected machine layer, including disabled sources and `ConfigPath`,
on every OS. No restore, ambiguity or source-origin assertion is skipped.

Exercise that fixture placement on another host with
`dotnet run --project tests/Phase0Probe/KeelMatrix.FeedFence.Phase0Probe.csproj -c Release --no-build -- --machine-in-user-layer`.

Each synthetic package contains a distinct `feedfence-origin.txt` marker. The
probe reads that marker from the isolated global-packages folder after restore,
then repeats restore with each active source isolated and with each expected
source disabled. This is independent evidence of the actual eligible set; it
does not call NuGet's mapping API to assert the restore result.
The SDK-recorded source inputs in each successful assets file must also equal
the modeled active fixture sources before any isolation control rewrites sources.
Probe projects disable the SDK's implicit library-packs source so this comparison
measures configuration inputs rather than an unrelated SDK-local package cache.
Restores use `--disable-build-servers` so persistent build processes cannot reuse
configuration paths from an earlier disposable environment.

The probe's recommendation is `PASS (go)` when all effective values, marker-
based restore outcomes, provenance cases, and parse-safety fixtures agree. A
`NARROW` or `STOP` result is reserved for a future API/client contradiction and
must include its bounded evidence in the run output.

NuGet 7.9.0 can materialize a missing user `NuGet.Config` while loading default
settings. The probe records this behavior and performs it only in its
disposable run directory; the fixture corpus and active repository are not
modified. FeedFence analysis must account for this side effect to remain
read-only with respect to the user's active worktree.
