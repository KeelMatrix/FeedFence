# Security

Please do not report suspected vulnerabilities in a public issue.

Send a private report to the repository maintainers with a concise description,
reproduction steps, affected version, and impact. Do not include credentials,
tokens, private package URLs, or customer data in the report.

FeedFence is intended to analyze local NuGet configuration and restore artifacts.
It must not contact package sources or invoke credential providers during
analysis. Reports involving unexpected network access, credential handling, or
diagnostic disclosure should be treated as security-sensitive.
