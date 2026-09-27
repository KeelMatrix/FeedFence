using System.Text.Json;

namespace KeelMatrix.FeedFence;

internal static class RestoreGraphReader
{
    private static readonly string[] DeclaredDependencySeparators = [" >= ", " > ", " <= ", " < ", " (>= ", " (> ", " (<= ", " (< "];
    private static readonly Dictionary<string, DeclaredDependencyTarget> DeclaredDependencyTargets =
        new Dictionary<string, DeclaredDependencyTarget>(StringComparer.OrdinalIgnoreCase)
        {
            ["Package"] = DeclaredDependencyTarget.Package,
            ["Project"] = DeclaredDependencyTarget.Project,
            ["ExternalProject"] = DeclaredDependencyTarget.ExternalProject,
            ["Assembly"] = DeclaredDependencyTarget.Assembly,
            ["Reference"] = DeclaredDependencyTarget.Reference,
            ["WinMD"] = DeclaredDependencyTarget.WinMD,
            ["All"] = DeclaredDependencyTarget.All,
            ["PackageProjectExternal"] = DeclaredDependencyTarget.PackageProjectExternal
        };
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

            var declaredPackageIds = ProjectPackageReferenceReader.Read(projectPath);
            var assetsPackages = hasAssets ? ReadAssets(assetsPath, declaredPackageIds) : null;
            var lockPackages = hasLock ? ReadLockFile(lockPath, declaredPackageIds) : null;
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

    private static HashSet<string> ReadAssets(string path, IReadOnlySet<string> declaredPackageIds)
    {
        using var document = ParseJson(path, MaxBytes: 32 * 1024 * 1024, "project.assets.json");
        var root = document.RootElement;
        if (!root.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var schemaVersion) ||
            schemaVersion is not (3 or 4) ||
            !root.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("projectFileDependencyGroups", out var dependencyGroups) || dependencyGroups.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("project", out var project) || project.ValueKind != JsonValueKind.Object ||
            !project.TryGetProperty("frameworks", out var projectFrameworks) || projectFrameworks.ValueKind != JsonValueKind.Object)
        {
            throw new AnalysisException("project.assets.json is incomplete; run restore first, then run FeedFence again.");
        }

        EnsureCount(targets, InputLimits.MaxAssetTargetCount, "project.assets.json contains too many target frameworks.");
        EnsureCount(dependencyGroups, InputLimits.MaxAssetFrameworkCount, "project.assets.json contains too many dependency groups.");
        EnsureCount(projectFrameworks, InputLimits.MaxAssetFrameworkCount, "project.assets.json contains too many project frameworks.");
        EnsureCount(libraries, InputLimits.MaxAssetLibraryCount, "project.assets.json contains too many libraries.");
        if (declaredPackageIds.Count > 0)
        {
            EnsureNonEmpty(targets, "project.assets.json contains no target frameworks.");
            EnsureNonEmpty(dependencyGroups, "project.assets.json contains no dependency groups.");
            EnsureNonEmpty(projectFrameworks, "project.assets.json contains no project frameworks.");
        }

        var libraryTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var library in libraries.EnumerateObject())
        {
            var packageName = GetLibraryName(library.Name);
            if (!libraryTypes.TryAdd(library.Name, GetLibraryType("library record", packageName, library.Value)))
            {
                throw new AnalysisException("project.assets.json contains duplicate library identities.");
            }
        }

        var targetLibraryNames = new HashSet<string>(StringComparer.Ordinal);
        var targetLibrariesByFramework = new Dictionary<string, Dictionary<string, TargetLibrary>>(StringComparer.OrdinalIgnoreCase);
        var targetFrameworkCount = 0;
        var targetLibraryCount = 0;
        foreach (var target in targets.EnumerateObject())
        {
            targetFrameworkCount++;
            if (targetFrameworkCount > InputLimits.MaxAssetFrameworkCount || target.Value.ValueKind != JsonValueKind.Object)
            {
                throw new AnalysisException("project.assets.json contains too many or invalid target frameworks.");
            }

            var frameworkDependencyNames = new Dictionary<string, TargetLibrary>(StringComparer.OrdinalIgnoreCase);
            foreach (var targetLibrary in target.Value.EnumerateObject())
            {
                targetLibraryCount++;
                if (targetLibraryCount > InputLimits.MaxAssetTargetLibraryCount)
                {
                    throw new AnalysisException("project.assets.json contains too many target library references.");
                }

                if (targetLibrary.Value.ValueKind != JsonValueKind.Object)
                {
                    throw new AnalysisException("project.assets.json contains a malformed target library record.");
                }

                var libraryName = GetLibraryName(targetLibrary.Name);
                if (!libraryTypes.TryGetValue(targetLibrary.Name, out var libraryType))
                {
                    throw new AnalysisException($"project.assets.json is incomplete; target library '{libraryName}' has no library record.");
                }

                var targetType = GetLibraryType("target library", libraryName, targetLibrary.Value);
                if (!string.Equals(targetType, libraryType, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AnalysisException($"project.assets.json is inconsistent; target and library types disagree for '{libraryName}'.");
                }

                if (!frameworkDependencyNames.TryAdd(libraryName, new TargetLibrary(targetType)))
                {
                    throw new AnalysisException("project.assets.json is inconsistent; a target framework contains multiple identities for one library.");
                }

                targetLibraryNames.Add(targetLibrary.Name);
            }

            if (!targetLibrariesByFramework.TryAdd(target.Name, frameworkDependencyNames))
            {
                throw new AnalysisException("project.assets.json contains duplicate target framework identities.");
            }
        }

        ValidateDeclaredDependencies(dependencyGroups, targetLibrariesByFramework);
        ValidateOfficialPackageDependencies(projectFrameworks, targetLibrariesByFramework);

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraryTypes)
        {
            if (!string.Equals(library.Value, "package", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var packageId = GetLibraryName(library.Key);

            if (!targetLibraryNames.Contains(library.Key))
            {
                throw new AnalysisException("project.assets.json is incomplete; a package library is not present in any target framework.");
            }

            result.Add(packageId);
            if (result.Count > InputLimits.MaxResolvedPackageCount)
            {
                throw new AnalysisException("project.assets.json contains too many resolved packages.");
            }
        }

        if (declaredPackageIds.Any(packageId => !result.Contains(packageId)))
        {
            throw new AnalysisException("project.assets.json is incomplete; a package declared by the project is not represented in the resolved package graph.");
        }

        return result;
    }

    private static void ValidateDeclaredDependencies(
        JsonElement dependencyGroups,
        IReadOnlyDictionary<string, Dictionary<string, TargetLibrary>> targetLibrariesByFramework)
    {
        var declaredDependencyCount = 0;
        foreach (var dependencyGroup in dependencyGroups.EnumerateObject())
        {
            if (dependencyGroup.Value.ValueKind != JsonValueKind.Array)
            {
                throw new AnalysisException("project.assets.json contains an invalid dependency group.");
            }

            if (dependencyGroup.Value.GetArrayLength() == 0)
            {
                continue;
            }

            var matchingTargets = targetLibrariesByFramework
                .Where(target =>
                    string.Equals(target.Key, dependencyGroup.Name, StringComparison.OrdinalIgnoreCase) ||
                    target.Key.StartsWith(dependencyGroup.Name + "/", StringComparison.OrdinalIgnoreCase))
                .Select(target => target.Value)
                .ToArray();
            if (matchingTargets.Length == 0)
            {
                throw new AnalysisException("project.assets.json is incomplete; a dependency group has no matching target framework.");
            }

            foreach (var dependency in dependencyGroup.Value.EnumerateArray())
            {
                if (++declaredDependencyCount > InputLimits.MaxAssetTargetLibraryCount ||
                    dependency.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(dependency.GetString()))
                {
                    throw new AnalysisException("project.assets.json contains too many or invalid declared dependencies.");
                }

                var dependencyName = GetDeclaredDependencyName(dependency.GetString()!);
                if (matchingTargets.Any(target => !target.ContainsKey(dependencyName)))
                {
                    throw new AnalysisException("project.assets.json is incomplete; a declared dependency is not represented in its target framework.");
                }
            }
        }
    }

    private static void ValidateOfficialPackageDependencies(
        JsonElement projectFrameworks,
        IReadOnlyDictionary<string, Dictionary<string, TargetLibrary>> targetLibrariesByFramework)
    {
        var declaredDependencyCount = 0;
        foreach (var projectFramework in projectFrameworks.EnumerateObject())
        {
            if (projectFramework.Value.ValueKind != JsonValueKind.Object)
            {
                throw new AnalysisException("project.assets.json contains an invalid project framework.");
            }

            var matchingTargets = targetLibrariesByFramework
                .Where(target =>
                    string.Equals(target.Key, projectFramework.Name, StringComparison.OrdinalIgnoreCase) ||
                    target.Key.StartsWith(projectFramework.Name + "/", StringComparison.OrdinalIgnoreCase))
                .Select(target => target.Value)
                .ToArray();
            if (matchingTargets.Length == 0)
            {
                throw new AnalysisException("project.assets.json is incomplete; a project framework has no matching target framework.");
            }

            if (!projectFramework.Value.TryGetProperty("dependencies", out var dependencies))
            {
                continue;
            }

            if (dependencies.ValueKind != JsonValueKind.Object)
            {
                throw new AnalysisException("project.assets.json contains invalid project dependency metadata.");
            }

            foreach (var dependency in dependencies.EnumerateObject())
            {
                var dependencyName = InputIdentity.RequirePackageId(dependency.Name, "project.assets.json contains an invalid project dependency identity.");
                if (++declaredDependencyCount > InputLimits.MaxAssetTargetLibraryCount ||
                    dependency.Value.ValueKind != JsonValueKind.Object ||
                    !dependency.Value.TryGetProperty("target", out var targetKind) ||
                    targetKind.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(targetKind.GetString()))
                {
                    throw new AnalysisException("project.assets.json contains too many or invalid project dependencies.");
                }

                var dependencyTarget = ParseDeclaredDependencyTarget(targetKind.GetString()!);
                var isPackageDependency = dependencyTarget.HasFlag(DeclaredDependencyTarget.Package);

                foreach (var matchingTarget in matchingTargets)
                {
                    if (!matchingTarget.TryGetValue(dependencyName, out var targetLibrary))
                    {
                        var dependencyKind = isPackageDependency ? "package dependency" : "dependency";
                        throw new AnalysisException($"project.assets.json is incomplete; declared {dependencyKind} '{dependencyName}' is not represented in its target framework.");
                    }

                    if (isPackageDependency && !string.Equals(targetLibrary.Type, "package", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new AnalysisException($"project.assets.json is inconsistent; declared package dependency '{dependencyName}' resolves to non-package target and library records.");
                    }

                    if (!isPackageDependency && !TargetAllowsLibraryType(dependencyTarget, targetLibrary.Type))
                    {
                        throw new AnalysisException($"project.assets.json is inconsistent; declared dependency '{dependencyName}' has a target that does not allow '{targetLibrary.Type}' target and library records.");
                    }
                }
            }
        }
    }

    private static DeclaredDependencyTarget ParseDeclaredDependencyTarget(string value)
    {
        var result = DeclaredDependencyTarget.None;
        foreach (var segment in value.Split(','))
        {
            var name = segment.Trim();
            if (name.Length == 0 || !DeclaredDependencyTargets.TryGetValue(name, out var target))
            {
                throw new AnalysisException("project.assets.json contains an invalid project dependency target.");
            }

            result |= target;
        }

        if (result == DeclaredDependencyTarget.None)
        {
            throw new AnalysisException("project.assets.json contains an invalid project dependency target.");
        }

        return result;
    }

    private static bool TargetAllowsLibraryType(DeclaredDependencyTarget target, string libraryType)
    {
        var libraryTarget = libraryType.ToLowerInvariant() switch
        {
            "package" => DeclaredDependencyTarget.Package,
            "project" => DeclaredDependencyTarget.Project,
            "externalproject" => DeclaredDependencyTarget.ExternalProject,
            "assembly" => DeclaredDependencyTarget.Assembly,
            "reference" => DeclaredDependencyTarget.Reference,
            "winmd" => DeclaredDependencyTarget.WinMD,
            _ => DeclaredDependencyTarget.None
        };
        return (target & libraryTarget) != 0;
    }

    private static string GetDeclaredDependencyName(string declaration)
    {
        var end = DeclaredDependencySeparators
            .Select(separator => declaration.IndexOf(separator, StringComparison.Ordinal))
            .Where(index => index >= 0)
            .DefaultIfEmpty(declaration.Length)
            .Min();
        return InputIdentity.RequirePackageId(declaration[..end].Trim(), "project.assets.json contains an invalid declared dependency identity.");
    }

    private static string GetLibraryName(string identity)
    {
        var separator = identity.LastIndexOf('/');
        if (separator <= 0 || separator == identity.Length - 1)
        {
            throw new AnalysisException("project.assets.json contains an invalid library identity.");
        }

        return InputIdentity.RequirePackageId(identity[..separator], "project.assets.json contains an invalid library identity.");
    }

    private static string GetLibraryType(string recordKind, string identity, JsonElement library)
    {
        if (library.ValueKind != JsonValueKind.Object ||
            !library.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String ||
            type.GetString() is not { } typeValue ||
            !ValidLibraryTypes.Contains(typeValue))
        {
            throw new AnalysisException($"project.assets.json {recordKind} '{identity}' has invalid type data.");
        }

        return typeValue;
    }

    private sealed record TargetLibrary(string Type);

    [Flags]
    private enum DeclaredDependencyTarget : ushort
    {
        None = 0,
        Package = 1 << 0,
        Project = 1 << 1,
        ExternalProject = 1 << 2,
        Assembly = 1 << 3,
        Reference = 1 << 4,
        WinMD = 1 << 5,
        All = Package | Project | ExternalProject | Assembly | Reference | WinMD,
        PackageProjectExternal = Package | Project | ExternalProject
    }

    private static HashSet<string> ReadLockFile(string path, IReadOnlySet<string> declaredPackageIds)
    {
        using var document = ParseJson(path, MaxBytes: 8 * 1024 * 1024, "packages.lock.json");
        var root = document.RootElement;
        if (!root.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var schemaVersion) ||
            schemaVersion != 2 ||
            !root.TryGetProperty("dependencies", out var dependencies) || dependencies.ValueKind != JsonValueKind.Object)
        {
            throw new AnalysisException("packages.lock.json is incomplete; run restore first, then run FeedFence again.");
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        EnsureCount(dependencies, InputLimits.MaxLockFrameworkCount, "packages.lock.json contains too many target frameworks.");
        EnsureNonEmpty(dependencies, "packages.lock.json contains no target frameworks.");
        foreach (var framework in dependencies.EnumerateObject())
        {
            if (framework.Value.ValueKind != JsonValueKind.Object)
            {
                throw new AnalysisException("packages.lock.json contains an invalid framework entry.");
            }

            foreach (var package in framework.Value.EnumerateObject())
            {
                var packageId = InputIdentity.RequirePackageId(package.Name, "packages.lock.json contains an invalid package identity.");
                if (result.Count >= InputLimits.MaxResolvedPackageCount)
                {
                    throw new AnalysisException("packages.lock.json contains too many resolved packages.");
                }

                if (package.Value.ValueKind != JsonValueKind.Object ||
                    !package.Value.TryGetProperty("type", out var packageType) ||
                    packageType.ValueKind != JsonValueKind.String ||
                    packageType.GetString() is not { } packageTypeValue ||
                    packageTypeValue is not ("Direct" or "Transitive" or "Project" or "CentralTransitive"))
                {
                    throw new AnalysisException("packages.lock.json contains an unknown or invalid package entry type.");
                }

                if (string.Equals(packageTypeValue, "Project", StringComparison.OrdinalIgnoreCase))
                {
                    if (declaredPackageIds.Contains(packageId))
                    {
                        throw new AnalysisException("packages.lock.json marks a project dependency as a package declared by the project.");
                    }

                    continue;
                }

                if (!package.Value.TryGetProperty("resolved", out var resolved) ||
                    resolved.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(resolved.GetString()))
                {
                    throw new AnalysisException("packages.lock.json contains an unresolved package; run restore first, then run FeedFence again.");
                }

                result.Add(packageId);
            }
        }

        if (declaredPackageIds.Any(packageId => !result.Contains(packageId)))
        {
            throw new AnalysisException("packages.lock.json is incomplete; a package declared by the project is not represented in the resolved package graph.");
        }

        return result;
    }

    private static void EnsureNonEmpty(JsonElement element, string message)
    {
        if (!element.EnumerateObject().Any())
        {
            throw new AnalysisException(message);
        }
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
