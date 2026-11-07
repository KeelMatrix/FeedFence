# FeedFence platform fixtures

`Invoke-PlatformFixtures.ps1` creates a disposable, offline fixture using the
user-level NuGet configuration location for the host OS:

- Windows: `%APPDATA%\NuGet\NuGet.Config`
- Linux: `~/.nuget/NuGet/NuGet.Config`
- macOS: `~/.nuget/NuGet/NuGet.Config`

The fixture also exercises an absolute `file://` local-feed URL, the host path
separator, case-only mapping/source-key differences, and deterministic JSON
output. The script refuses to emulate another OS; run it on each target OS to
produce platform evidence. It never restores the synthetic project or contacts
a package feed.

The synthetic graph includes an evaluated-input companion from
`tests/Write-RestoreFixture.ps1`. Real restored graph, configuration and link
controls run in `FeedFenceCliTests` and repeat against the installed tool in
the package gate.
Those real-restored cases include Git-free/Git/worktree policy discovery,
physical policy links and ancestors, actual platform permission denial, explicit
external overrides and empty stdout on policy-loading failures in all formats.
