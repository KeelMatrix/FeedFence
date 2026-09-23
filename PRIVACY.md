# Privacy

The shared telemetry client contract is maintained in the [KeelMatrix.Telemetry privacy policy](https://github.com/KeelMatrix/Telemetry/blob/main/PRIVACY.md), which is the source of truth for shared delivery, retention, identifiers, and opt-out behavior.

The Phase 0 feasibility probe is local-only and does not send telemetry or
contact package feeds. Its synthetic fixtures contain no customer data,
credentials, or private package URLs.

The FeedFence analysis contract is intentionally offline. Diagnostics use safe
source labels and bounded configuration labels; sensitive source keys become
stable opaque labels rather than exposing credentials, authenticated URLs, full
local paths, or package contents.

After a completed analysis with at least one resolved package and one effective
source-policy evaluation, the tool may request one activation through the
shared `KeelMatrix.Telemetry` package. FeedFence calls
`Client.TrackActivation()` with the tool name `feedfence` and the FeedFence
assembly type. It does not pass a custom payload and does not request shared
heartbeats.

The shared client owns the event contract. When it can derive a stable anonymous
project identity and activation has not already been recorded, its activation
event contains the tool and assembly versions, telemetry/schema versions,
anonymous project and installation hashes, runtime, operating system, CI state,
and timestamp. Its queue, marker, salt, HTTPS delivery, retention, and opt-out
precedence are documented in the shared privacy policy linked above.

FeedFence therefore does not add analysis counts or results, package IDs or
versions, source names/URLs, protected patterns, exception reasons,
repository/project/solution names, paths, credentials, configuration contents,
or raw diagnostics to telemetry. Telemetry failures do not affect analysis or
exit codes. Set `KEELMATRIX_NO_TELEMETRY=1` to opt out.
