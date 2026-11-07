namespace KeelMatrix.FeedFence;

// Policy ownership is independent of the directory NuGet used for restore settings.
internal sealed record RepositoryPolicyContext(string RepositoryRoot, string? PolicyPath, bool IsExplicit)
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static RepositoryPolicyContext Discover(string targetPath, string? explicitPolicy)
    {
        try
        {
            var logicalTarget = Path.GetFullPath(targetPath);
            var physicalTarget = Provenance.ResolvePhysicalPath(logicalTarget)
                ?? throw new AnalysisException("the policy discovery target could not be resolved.");
            var logicalStart = Path.GetDirectoryName(logicalTarget)!;
            var physicalStart = Path.GetDirectoryName(physicalTarget)!;
            var policies = new Dictionary<string, HashSet<string>>(PathComparer);
            var physicalGitRoot = CollectAncestry(physicalStart, explicitPolicy is null, policies);
            var logicalGitRoot = PathComparer.Equals(logicalStart, physicalStart)
                ? physicalGitRoot
                : CollectAncestry(logicalStart, explicitPolicy is null, policies);
            var fallbackRoot = physicalGitRoot ?? logicalGitRoot ?? physicalStart;
            if (explicitPolicy is not null)
            {
                return new(fallbackRoot, Path.GetFullPath(explicitPolicy), true);
            }
            if (policies.Count > 1)
            {
                throw new AnalysisException("multiple ancestor feedfence.json entries make policy discovery ambiguous; supply an intentional --policy override.");
            }
            if (policies.Count == 0) return new(fallbackRoot, null, false);
            var policy = policies.Single();
            if (policy.Value.Count != 1)
            {
                throw new AnalysisException("logical and physical policy ancestry identify conflicting repository boundaries; supply an intentional --policy override.");
            }
            var repositoryRoot = policy.Value.Single();
            if (!Provenance.IsWithinDirectory(physicalStart, repositoryRoot))
            {
                throw new AnalysisException("the selected target redirects outside its implicit policy repository; supply an intentional --policy override.");
            }
            return new(repositoryRoot, policy.Key, false);
        }
        catch (AnalysisException) { throw; }
        catch
        {
            throw new AnalysisException("repository policy discovery could not inspect the ancestor directories or supplied policy path; check access and path validity.");
        }
    }

    private static string? CollectAncestry(string start, bool discoverPolicy, Dictionary<string, HashSet<string>> policies)
    {
        var entries = new List<(string Path, string Owner)>();
        string? gitRoot = null;
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            var owner = Provenance.ResolvePhysicalPath(directory.FullName)
                ?? throw new AnalysisException("a policy ancestor directory could not be resolved.");
            if (discoverPolicy && HasEntry(directory.FullName, "feedfence.json"))
            {
                entries.Add((Path.Combine(owner, "feedfence.json"), owner));
            }
            if (HasEntry(directory.FullName, ".git"))
            {
                if (Provenance.ResolvePhysicalPath(Path.Combine(directory.FullName, ".git")) is null)
                {
                    throw new AnalysisException("the repository boundary marker could not be resolved.");
                }
                gitRoot = owner;
                break;
            }
        }
        foreach (var (path, owner) in entries)
        {
            if (!policies.TryGetValue(path, out var boundaries))
            {
                boundaries = new HashSet<string>(PathComparer);
                policies.Add(path, boundaries);
            }
            boundaries.Add(gitRoot ?? owner);
        }
        return gitRoot;
    }

    private static bool HasEntry(string directory, string name)
    {
        // Complete the query: Exists predicates hide access failures and non-file entries.
        var entries = Directory.EnumerateFileSystemEntries(directory, name, new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            MatchType = MatchType.Simple,
            MatchCasing = MatchCasing.PlatformDefault
        }).ToArray();
        if (entries.Length != 0) return true;
        // A volume can be case-insensitive even on a platform whose glob default is sensitive.
        try { _ = File.GetAttributes(Path.Combine(directory, name)); return true; }
        catch (FileNotFoundException) { return false; }
    }
}
