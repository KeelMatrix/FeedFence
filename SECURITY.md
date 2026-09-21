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

FeedFence telemetry is best-effort and activates only after a completed analysis
with a resolved package and an effective source-policy evaluation. Its bounded
summary contains no package/source identities, URLs, policy contents, paths,
credentials, or raw diagnostics. Disable it with `KEELMATRIX_NO_TELEMETRY=1`.
