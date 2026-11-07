# Security Policy

## Reporting a Vulnerability

Please report suspected vulnerabilities privately to **keelmatrix@gmail.com**,
the private reporting route for this repository. Include a concise description,
affected version, reproduction steps, expected and observed behavior, and impact.
Do not include credentials, tokens, private package URLs, or customer data in a
public issue or in an unredacted report.

Routine bugs and usage questions belong in the normal repository issue tracker;
use the private route when the report may expose a vulnerability.

## Supported Versions

The latest published version is the supported security baseline. During
pre-release development, include the exact repository commit or package version
in a private report. Older versions and unsupported runtimes may receive fixes
on a case-by-case basis.

## Product Security Boundary

FeedFence is intended to analyze local NuGet configuration and restore artifacts.
It must not contact package sources or invoke credential providers during
analysis. Reports involving unexpected network access, credential handling, or
diagnostic disclosure should be treated as security-sensitive.

Graph checks use the SDK's evaluated restore input companion and validate direct
and transitive package/project dependency closure for the selected root and
framework. Fully private child edges do not flow into an ancestor graph; direct
root dependencies and independently included paths still require resolved nodes.
Incomplete evidence fails
without a partial success report. Project XML, imports and build targets are
never executed during analysis. Configuration bounds apply before NuGet parses
hierarchy files or implicit machine defaults. Missing user defaults are never
silently modeled as fewer sources, and analysis does not initialize user files.

Repository control requires resolved physical ancestry, including links and
junctions; resolution failure cannot assert repository ownership. `FF006`
reports enabled HTTP sources and disabled TLS certificate validation. Exceptions
must be narrow and reasoned. These checks do not verify a server's certificate,
reachability or credentials and do not contact the feed.

Repository policy does not require Git metadata. An implicit `feedfence.json`
must be uniquely discovered and resolve to a readable file inside the physical
repository boundary. An unusable entry, unreadable discovery directory,
ambiguous policy or external target fails with exit `2` and no partial report.
Explicit `--policy` overrides may intentionally select external files, with the
same parsing and read requirements. Inputs must stay stable during analysis;
the filesystem checks do not provide atomic isolation from concurrent changes.
The [README policy and CLI contract](README.md#repository-policy) defines
discovery boundaries, absence, overrides and limits.

FeedFence telemetry is best-effort and activates only after a completed analysis
with a resolved package and an effective source-policy evaluation. FeedFence
calls the shared client's parameterless activation API and attaches no analysis
summary, package/source identities, URLs, policy contents, paths, credentials,
or raw diagnostics. See [PRIVACY.md](PRIVACY.md) for the shared event contract
and disable it with `KEELMATRIX_NO_TELEMETRY=1`.
