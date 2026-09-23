using System.Text.Json;

namespace KeelMatrix.FeedFence;

internal static class RestoreGraphReader
{
    private static readonly HashSet<string> ValidLibraryTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "package",
        "project",
        "externalProject",
        "assembly",
        "reference",
        "winmd"
    };

    public static IReadOnlyList<string> ReadFromProjects(IReadOnlyList<string> projectPaths)
    {
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectPath in projectPaths.Order(StringComparer.OrdinalIgnoreCase))
        {
            var projectDirectory = Path.GetDirectoryName(projectPath)!;
            var assetsPath = Path.Combine(projectDirectory, "obj", "project.assets.json");
            var lockPath = Path.Combine(projectDirectory, "packages.lock.json");
            var hasAssets = File.Exists(assetsPath);
            var hasLock = File.Exists(lockPath);
            if (!hasAssets && !hasLock)
            {
                throw new AnalysisException("restore artifacts are missing; run restore first, then run FeedFence again.");
            }

            var assetsPackages = hasAssets ? ReadAssets(assetsPath) : null;
            var lockPackages = hasLock ? ReadLockFile(lockPath) : null;
            if (assetsPackages is not null && lockPackages is not null &&
                !assetsPackages.SetEquals(lockPackages))
            {
                throw new AnalysisException("project.assets.json and packages.lock.json disagree; restore again before analysis.");
            }

            foreach (var packageId in (assetsPackages ?? lockPackages)!)
            {
                if (!packages.TryGetValue(packageId, out var existing) || string.CompareOrdinal(packageId, existing) < 0)
                {
                    packages[packageId] = packageId;
                }
            }
        }

        return packages.Values.Order(StringComparer.OrdinalIgnoreCase).ThenBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static HashSet<string> ReadAssets(string path)
    {
        using var document = ParseJson(path, MaxBytes: 32 * 1024 * 1024, "project.assets.json");
        var root = document.RootElement;
        if (!root.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Object)
        {
            throw new AnalysisException("project.assets.json is incomplete; run restore first, then run FeedFence again.");
        }

        EnsureCount(targets, InputLimits.MaxAssetTargetCount, "project.assets.json contains too many target frameworks.");
        var targetLibraryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targetFrameworkCount = 0;
        var targetLibraryCount = 0;
        foreach (var target in targets.EnumerateObject())
        {
            targetFrameworkCount++;
            if (targetFrameworkCount > InputLimits.MaxAssetFrameworkCount || target.Value.ValueKind != JsonValueKind.Object)
            {
                throw new AnalysisException("project.assets.json contains too many or invalid target frameworks.");
            }

            foreach (var targetLibrary in target.Value.EnumerateObject())
            {
                targetLibraryCount++;
                if (targetLibraryCount > InputLimits.MaxAssetTargetLibraryCount)
                {
                    throw new AnalysisException("project.assets.json contains too many target library references.");
                }

                if (!targetLibrary.Name.Contains('/'))
                {
                    throw new AnalysisException("project.assets.json contains an invalid target library identity.");
                }

                if (targetLibrary.Value.ValueKind != JsonValueKind.Object)
                {
                    throw new AnalysisException($"project.assets.json target library '{targetLibrary.Name}' is malformed.");
                }

                targetLibraryNames.Add(targetLibrary.Name);
                if (!libraries.TryGetProperty(targetLibrary.Name, out var libraryRecord))
                {
                    throw new AnalysisException($"project.assets.json is incomplete; target library '{targetLibrary.Name}' has no library record.");
                }

                ValidateLibraryRecord(targetLibrary.Name, libraryRecord);
            }
        }

        EnsureCount(libraries, InputLimits.MaxAssetLibraryCount, "project.assets.json contains too many libraries.");
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries.EnumerateObject())
        {
            ValidateLibraryRecord(library.Name, library.Value);
            var type = library.Value.GetProperty("type").GetString()!;
            if (!string.Equals(type, "package", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var separator = library.Name.LastIndexOf('/');
            if (separator <= 0 || separator == library.Name.Length - 1)
            {
                throw new AnalysisException("project.assets.json contains an invalid package identity.");
            }

            if (!targetLibraryNames.Contains(library.Name))
            {
                throw new AnalysisException("project.assets.json is incomplete; a package library is not present in any target framework.");
            }

            result.Add(library.Name[..separator]);
            if (result.Count > InputLimits.MaxResolvedPackageCount)
            {
                throw new AnalysisException("project.assets.json contains too many resolved packages.");
            }
        }

        return result;
    }

    private static void ValidateLibraryRecord(string identity, JsonElement library)
    {
        if (library.ValueKind != JsonValueKind.Object ||
            !library.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String ||
            type.GetString() is not { } typeValue ||
            !ValidLibraryTypes.Contains(typeValue))
        {
            throw new AnalysisException($"project.assets.json library record '{identity}' has invalid type data.");
        }
    }

    private static HashSet<string> ReadLockFile(string path)
    {
        using var document = ParseJson(path, MaxBytes: 8 * 1024 * 1024, "packages.lock.json");
        var root = document.RootElement;
        if (!root.TryGetProperty("dependencies", out var dependencies) || dependencies.ValueKind != JsonValueKind.Object)
        {
            throw new AnalysisException("packages.lock.json is incomplete; run restore first, then run FeedFence again.");
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        EnsureCount(dependencies, InputLimits.MaxLockFrameworkCount, "packages.lock.json contains too many target frameworks.");
        foreach (var framework in dependencies.EnumerateObject())
        {
            if (framework.Value.ValueKind != JsonValueKind.Object)
            {
                throw new AnalysisException("packages.lock.json contains an invalid framework entry.");
            }

            foreach (var package in framework.Value.EnumerateObject())
            {
                if (result.Count >= InputLimits.MaxResolvedPackageCount)
                {
                    throw new AnalysisException("packages.lock.json contains too many resolved packages.");
                }

                if (package.Value.ValueKind == JsonValueKind.Object &&
                    package.Value.TryGetProperty("type", out var packageType) &&
                    string.Equals(packageType.GetString(), "Project", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (package.Value.ValueKind != JsonValueKind.Object ||
                    !package.Value.TryGetProperty("resolved", out var resolved) ||
                    resolved.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(resolved.GetString()))
                {
                    throw new AnalysisException("packages.lock.json contains an unresolved package; run restore first, then run FeedFence again.");
                }

                result.Add(package.Name);
            }
        }

        return result;
    }

    private static void EnsureCount(JsonElement element, int maximum, string message)
    {
        var count = 0;
        foreach (var _ in element.EnumerateObject())
        {
            if (++count > maximum)
            {
                throw new AnalysisException(message);
            }
        }
    }

    private static JsonDocument ParseJson(string path, int MaxBytes, string kind)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxBytes)
        {
            throw new AnalysisException($"{kind} exceeds the supported input size.");
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024, FileOptions.SequentialScan);
            return JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (AnalysisException)
        {
            throw;
        }
        catch
        {
            throw new AnalysisException($"{kind} is malformed; run restore first, then run FeedFence again.");
        }
    }
}
