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
Both logical and physical policy ancestry are covered for selected directory,
project/solution file aliases and inward/outward source-directory link chains.
On Linux/macOS the CLI suite also runs `PolicySpecialFiles.py`: real restored
Company.Secret, no-producer FIFOs, sockets, existing character devices and
resolved aliases for policies and Git markers, all formats, nested project and
solution, empty error stdout and unchanged inputs. Required Python 3 or object
capability failures fail the gate; Windows reports these Unix-only cases as N/A.
The same native suite runs against the isolated installed exact package.
CI runs separate native Intel and arm64 macOS jobs and checks the .NET process
architecture. The native cases also cover special policy and marker objects in
both ancestry views of directory aliases. A supplementary buffer replay varies
mode, permissions and inode bits independently; it is not native platform proof.
