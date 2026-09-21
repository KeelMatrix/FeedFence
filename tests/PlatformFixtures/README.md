# FeedFence platform fixtures

`Invoke-PlatformFixtures.ps1` creates a disposable, offline fixture using the
user-level NuGet configuration location for the host OS:

- Windows: `%APPDATA%\NuGet\NuGet.Config`
- Linux: `~/.nuget/NuGet/NuGet.Config`
- macOS: `~/.nuget/NuGet/NuGet.Config`

The fixture also exercises an absolute `file://` local-feed URL, the host path
separator, a case-sensitive mapping/source-key spelling, and deterministic JSON
output. The script refuses to emulate another OS; run it on each target OS to
produce platform evidence. It never restores the synthetic project or contacts
a package feed.
