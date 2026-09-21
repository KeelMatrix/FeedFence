# Security

Please do not report suspected vulnerabilities in a public issue.

Send a private report to the repository maintainers with a concise description,
reproduction steps, affected version, and impact. Do not include credentials,
tokens, private package URLs, or customer data in the report.

FeedFence is intended to analyze local NuGet configuration and restore artifacts.
It must not contact package sources or invoke credential providers during
analysis. Reports involving unexpected network access, credential handling, or
diagnostic disclosure should be treated as security-sensitive.

FeedFence telemetry is best-effort and activates only after a completed analysis
with a resolved package and an effective source-policy evaluation. Its bounded
summary contains no package/source identities, URLs, policy contents, paths,
credentials, or raw diagnostics. Disable it with `KEELMATRIX_NO_TELEMETRY=1`.
