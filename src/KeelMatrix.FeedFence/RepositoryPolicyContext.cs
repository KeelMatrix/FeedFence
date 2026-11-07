namespace KeelMatrix.FeedFence;

// Policy ownership is independent of the directory NuGet used for restore settings.
internal sealed record RepositoryPolicyContext(string RepositoryRoot, string? PolicyPath, bool IsExplicit)
{
    public static RepositoryPolicyContext Discover(string start, string? explicitPolicy)
    {
        try
        {
            var physicalStart = Provenance.ResolvePhysicalPath(start)
                ?? throw new AnalysisException("the policy discovery directory could not be resolved.");
            var policies = new List<string>();
            string? gitRoot = null;
            for (var directory = new DirectoryInfo(physicalStart); directory is not null; directory = directory.Parent)
            {
                if (explicitPolicy is null && HasEntry(directory.FullName, "feedfence.json"))
                {
                    policies.Add(Path.Combine(directory.FullName, "feedfence.json"));
                }
                if (HasEntry(directory.FullName, ".git"))
                {
                    var marker = Path.Combine(directory.FullName, ".git");
                    if (Provenance.ResolvePhysicalPath(marker) is null)
                    {
                        throw new AnalysisException("the repository boundary marker could not be resolved.");
                    }
                    gitRoot = directory.FullName;
                    break;
                }
            }
            if (explicitPolicy is not null)
            {
                return new(gitRoot ?? physicalStart, Path.GetFullPath(explicitPolicy), true);
            }
            if (policies.Count > 1)
            {
                throw new AnalysisException("multiple ancestor feedfence.json entries make policy discovery ambiguous; supply an intentional --policy override.");
            }
            var policy = policies.SingleOrDefault();
            return new(gitRoot ?? (policy is null ? physicalStart : Path.GetDirectoryName(policy)!), policy, false);
        }
        catch (AnalysisException) { throw; }
        catch
        {
            throw new AnalysisException("repository policy discovery could not inspect the ancestor directories or supplied policy path; check access and path validity.");
        }
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
