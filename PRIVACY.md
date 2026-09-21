# Privacy

The Phase 0 feasibility probe is local-only and does not send telemetry or
contact package feeds. Its synthetic fixtures contain no customer data,
credentials, or private package URLs.

The FeedFence analysis contract is intentionally offline. Diagnostics use source
keys and bounded configuration labels rather than credentials, authenticated
URLs, full local paths, or package contents.

After a completed analysis with at least one resolved package and one effective
source-policy evaluation, the tool may request one activation through the
shared `KeelMatrix.Telemetry` package. The FeedFence summary is limited to the
tool version, .NET major version, broad OS family, coarse package/source and
diagnostic counts, mapping-enabled state, and coarse result class. It never
sends package IDs or versions, source names/URLs, protected patterns,
exception reasons, repository/project/solution names, paths, credentials,
configuration contents, or raw diagnostics. Telemetry failures do not affect
analysis or exit codes. Set `KEELMATRIX_NO_TELEMETRY=1` to opt out.
