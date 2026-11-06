using System.Text.Json;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace KeelMatrix.FeedFence;

internal static class RestoreGraphReader
{
    private static readonly string[] DeclaredDependencySeparators = [" >= ", " > ", " <= ", " < ", " (>= ", " (> ", " (<= ", " (< "];
    private static readonly string[] AllAssetFlags = ["compile", "runtime", "native", "contentfiles", "analyzers", "build", "buildtransitive"];
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

            var evaluated = EvaluatedRestoreReader.Read(projectPath);
            var assetsPackages = hasAssets ? ReadAssets(assetsPath, evaluated) : null;
            var lockPackages = hasLock ? ReadLockFile(lockPath, evaluated) : null;
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

    private static HashSet<string> ReadAssets(string path, EvaluatedRestoreEvidence evaluated)
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
        EnsureNonEmpty(targets, "project.assets.json contains no target frameworks.");
        EnsureNonEmpty(dependencyGroups, "project.assets.json contains no dependency groups.");
        EnsureNonEmpty(projectFrameworks, "project.assets.json contains no project frameworks.");

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

                if (!frameworkDependencyNames.TryAdd(libraryName, new TargetLibrary(targetType, targetLibrary.Value, targetLibrary.Name[(targetLibrary.Name.LastIndexOf('/') + 1)..])))
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
        ValidateDependencyClosure(targetLibrariesByFramework, "project.assets.json");
        ValidateEvaluatedDependencies(evaluated, targetLibrariesByFramework, "project.assets.json", projectFrameworks, dependencyGroups);

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
        IReadOnlyDictionary<string, Dictionary<string, TargetLibrary>> targetLibrariesByFramework,
        string? targetFrameworkOverride = null,
        string? selectedEvaluatedFramework = null,
        bool isReferencedProject = false)
    {
        var declaredDependencyCount = 0;
        foreach (var projectFramework in projectFrameworks.EnumerateObject()
            .Where(item => selectedEvaluatedFramework is null || FrameworkName(item.Name) == selectedEvaluatedFramework))
        {
            if (projectFramework.Value.ValueKind != JsonValueKind.Object)
            {
                throw new AnalysisException("project.assets.json contains an invalid project framework.");
            }

            var matchingTargets = targetLibrariesByFramework
                .Where(target =>
                    FrameworkName(target.Key) == FrameworkName(targetFrameworkOverride ?? projectFramework.Name))
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
                var privateDependency = IsFullyPrivate(dependency.Value, "suppressParent");
                if (isReferencedProject && privateDependency)
                {
                    continue;
                }

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

    private sealed record TargetLibrary(string Type, JsonElement Record, string? Version);

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

    private static HashSet<string> ReadLockFile(string path, EvaluatedRestoreEvidence evaluated)
    {
        using var document = ParseJson(path, MaxBytes: 8 * 1024 * 1024, "packages.lock.json");
        var root = document.RootElement;
        if (!root.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var schemaVersion) ||
            schemaVersion is not (1 or 2) ||
            !root.TryGetProperty("dependencies", out var dependencies) || dependencies.ValueKind != JsonValueKind.Object)
        {
            throw new AnalysisException("packages.lock.json is incomplete; run restore first, then run FeedFence again.");
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        EnsureCount(dependencies, InputLimits.MaxLockFrameworkCount, "packages.lock.json contains too many target frameworks.");
        EnsureNonEmpty(dependencies, "packages.lock.json contains no target frameworks.");
        var targets = new Dictionary<string, Dictionary<string, TargetLibrary>>(StringComparer.OrdinalIgnoreCase);
        var nodeCount = 0;
        foreach (var framework in dependencies.EnumerateObject())
        {
            if (framework.Value.ValueKind != JsonValueKind.Object)
            {
                throw new AnalysisException("packages.lock.json contains an invalid framework entry.");
            }

            var nodes = new Dictionary<string, TargetLibrary>(StringComparer.OrdinalIgnoreCase);
            if (!targets.TryAdd(framework.Name, nodes))
            {
                throw new AnalysisException("packages.lock.json contains duplicate target frameworks.");
            }
            foreach (var package in framework.Value.EnumerateObject())
            {
                if (++nodeCount > InputLimits.MaxAssetTargetLibraryCount)
                {
                    throw new AnalysisException("packages.lock.json contains too many target library references.");
                }
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
                    if (!nodes.TryAdd(packageId, new TargetLibrary("project", package.Value, null)))
                    {
                        throw new AnalysisException("packages.lock.json contains duplicate library identities.");
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
                if (!nodes.TryAdd(packageId, new TargetLibrary("package", package.Value, resolved.GetString())))
                {
                    throw new AnalysisException("packages.lock.json contains duplicate library identities.");
                }
            }
        }

        // NuGet writes RID lock targets as differences from the base TFM graph.
        // Compose the effective target before checking its edges and evaluated roots.
        var effectiveLibraryCount = targets.Where(item => !item.Key.Contains('/')).Sum(item => item.Value.Count);
        foreach (var target in targets.Where(item => item.Key.Contains('/')).ToArray())
        {
            var baseTarget = targets.FirstOrDefault(item => !item.Key.Contains('/') && FrameworkName(item.Key) == FrameworkName(target.Key));
            if (baseTarget.Value is null)
            {
                throw new AnalysisException("packages.lock.json is incomplete; a runtime target has no base framework graph.");
            }
            effectiveLibraryCount += baseTarget.Value.Count + target.Value.Keys.Count(key => !baseTarget.Value.ContainsKey(key));
            if (effectiveLibraryCount > InputLimits.MaxAssetTargetLibraryCount)
            {
                throw new AnalysisException("packages.lock.json contains too many effective target library references.");
            }
            var effective = new Dictionary<string, TargetLibrary>(baseTarget.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var node in target.Value) effective[node.Key] = node.Value;
            targets[target.Key] = effective;
        }
        ValidateDependencyClosure(targets, "packages.lock.json");
        ValidateEvaluatedDependencies(evaluated, targets, "packages.lock.json");

        return result;
    }

    private static void ValidateDependencyClosure(
        IReadOnlyDictionary<string, Dictionary<string, TargetLibrary>> targets, string artifact)
    {
        var edgeCount = 0;
        foreach (var nodes in targets.Values)
        {
            foreach (var node in nodes.Values)
            {
                if (!node.Record.TryGetProperty("dependencies", out var dependencies))
                {
                    continue;
                }
                if (dependencies.ValueKind != JsonValueKind.Object)
                {
                    throw new AnalysisException($"{artifact} contains invalid dependency edges; restore again before analysis.");
                }
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var dependency in dependencies.EnumerateObject())
                {
                    InputIdentity.RequirePackageId(dependency.Name, $"{artifact} contains an invalid dependency identity.");
                    if (++edgeCount > InputLimits.MaxAssetTargetLibraryCount || !names.Add(dependency.Name) ||
                        dependency.Value.ValueKind != JsonValueKind.String ||
                        !VersionRange.TryParse(dependency.Value.GetString()!, out var range))
                    {
                        throw new AnalysisException($"{artifact} contains too many or invalid dependency edges.");
                    }
                    if (!nodes.TryGetValue(dependency.Name, out var resolved))
                    {
                        throw new AnalysisException($"{artifact} is incomplete; a package or project dependency has no resolved node in its target framework. Restore again before analysis.");
                    }
                    if (resolved.Version is not null &&
                        (!NuGetVersion.TryParse(resolved.Version, out var version) || !range.Satisfies(version)))
                    {
                        throw new AnalysisException($"{artifact} contains an inconsistent resolved dependency version; restore again before analysis.");
                    }
                }
            }
        }
    }

    private static string FrameworkName(string target)
    {
        var framework = NuGetFramework.Parse(target.Split('/')[0]);
        if (framework.IsUnsupported)
        {
            throw new AnalysisException("restore evidence contains an unsupported target framework.");
        }
        return framework.GetShortFolderName();
    }

    private static string TargetName(string target)
    {
        var separator = target.IndexOf('/');
        return FrameworkName(target) + (separator < 0 ? "" : target[separator..]);
    }

    private static Dictionary<string, JsonElement> EvaluatedFrameworks(JsonElement project)
    {
        if (project.ValueKind != JsonValueKind.Object ||
            !project.TryGetProperty("frameworks", out var frameworks) || frameworks.ValueKind != JsonValueKind.Object ||
            !project.TryGetProperty("restore", out var restore) || restore.ValueKind != JsonValueKind.Object ||
            !restore.TryGetProperty("projectStyle", out var style) || style.GetString() != "PackageReference" ||
            !restore.TryGetProperty("frameworks", out var restoreFrameworks) || restoreFrameworks.ValueKind != JsonValueKind.Object ||
            !restore.TryGetProperty("originalTargetFrameworks", out var originals) || originals.ValueKind != JsonValueKind.Array)
        {
            throw new AnalysisException("evaluated restore input evidence is incomplete; restore again before analysis.");
        }
        EnsureCount(frameworks, InputLimits.MaxAssetFrameworkCount, "evaluated restore input evidence contains too many frameworks.");
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var framework in frameworks.EnumerateObject())
        {
            if (framework.Value.ValueKind != JsonValueKind.Object || !result.TryAdd(FrameworkName(framework.Name), framework.Value))
            {
                throw new AnalysisException("evaluated restore input evidence contains invalid or duplicate frameworks.");
            }
        }
        var names = result.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var aliases = result.Select(item => item.Value.TryGetProperty("targetAlias", out var alias) ? alias.GetString()! : item.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0 || originals.GetArrayLength() == 0 ||
            !names.SetEquals(restoreFrameworks.EnumerateObject().Select(item => FrameworkName(item.Name))) ||
            !aliases.SetEquals(originals.EnumerateArray().Select(item => item.GetString()!)))
        {
            throw new AnalysisException("evaluated restore input evidence has incomplete framework containers; restore again before analysis.");
        }
        return result;
    }

    private static void ValidateEvaluatedDependencies(
        EvaluatedRestoreEvidence evidence,
        IReadOnlyDictionary<string, Dictionary<string, TargetLibrary>> targets,
        string artifact,
        JsonElement? assetFrameworks = null,
        JsonElement? dependencyGroups = null)
    {
        var root = evidence.Projects[evidence.ProjectPath];
        var rootFrameworks = EvaluatedFrameworks(root);
        var expected = rootFrameworks.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedTargets = new HashSet<string>(expected, StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("runtimes", out var runtimes))
        {
            if (runtimes.ValueKind != JsonValueKind.Object)
            {
                throw new AnalysisException("evaluated restore input evidence contains invalid runtime identifiers.");
            }
            EnsureCount(runtimes, InputLimits.MaxAssetTargetCount, "evaluated restore input evidence contains too many runtime identifiers.");
            foreach (var runtime in runtimes.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(runtime.Name) || runtime.Name.Contains('/') || runtime.Name.Contains('\\'))
                {
                    throw new AnalysisException("evaluated restore input evidence contains an invalid runtime identifier.");
                }
                foreach (var framework in expected) expectedTargets.Add(framework + "/" + runtime.Name);
            }
        }
        if (expectedTargets.Count > InputLimits.MaxAssetTargetCount || targets.Count != expectedTargets.Count ||
            !expectedTargets.SetEquals(targets.Keys.Select(TargetName)) ||
            (assetFrameworks is { } af && !expected.SetEquals(af.EnumerateObject().Select(item => FrameworkName(item.Name)))) ||
            (dependencyGroups is { } dg && !expected.SetEquals(dg.EnumerateObject().Select(item => FrameworkName(item.Name)))))
        {
            throw new AnalysisException($"{artifact} has incomplete target/framework containers compared with evaluated restore inputs; restore again before analysis.");
        }
        foreach (var framework in rootFrameworks.Keys)
        {
            var visited = new HashSet<(string, string)>();
            Visit(evidence.ProjectPath, framework, framework, 0);

            void Visit(string projectPath, string selectedFramework, string rootFramework, int depth)
            {
                if (!visited.Add((projectPath, selectedFramework)))
                {
                    return;
                }
                if (depth > 128 || visited.Count > 4096 || !evidence.Projects.TryGetValue(projectPath, out var project))
                {
                    throw new AnalysisException("evaluated restore input evidence is missing a referenced project or exceeds the project limit.");
                }
                EvaluatedFrameworks(project);
                // Reuse the declared-target/type validator for evaluated inputs, including child projects.
                ValidateOfficialPackageDependencies(project.GetProperty("frameworks"), targets, rootFramework, selectedFramework, depth != 0);
                var restoreFramework = project.GetProperty("restore").GetProperty("frameworks").EnumerateObject()
                    .Single(item => FrameworkName(item.Name) == selectedFramework).Value;
                if (!restoreFramework.TryGetProperty("projectReferences", out var references) || references.ValueKind != JsonValueKind.Object)
                {
                    throw new AnalysisException("evaluated restore input evidence has missing or invalid project-reference metadata.");
                }
                EnsureCount(references, 4096, "evaluated restore input evidence contains too many project references.");
                foreach (var reference in references.EnumerateObject())
                {
                    // NuGet keeps a root's direct edges even when private. Only
                    // fully private edges of referenced projects leave this graph.
                    // Asset inclusion/exclusion does not remove dependency nodes.
                    var privateReference = IsFullyPrivate(reference.Value, "privateAssets");
                    if (depth != 0 && privateReference)
                    {
                        continue;
                    }
                    var referencePath = Path.GetFullPath(reference.Name);
                    if (!evidence.Projects.TryGetValue(referencePath, out var child))
                    {
                        throw new AnalysisException("evaluated restore input evidence is missing a referenced project; restore again before analysis.");
                    }
                    var projectId = InputIdentity.RequirePackageId(child.GetProperty("restore").GetProperty("projectName").GetString()!, "evaluated restore input evidence contains an invalid project identity.");
                    foreach (var target in targets.Where(item => FrameworkName(item.Key) == rootFramework))
                    {
                        if (!target.Value.TryGetValue(projectId, out var node) || !string.Equals(node.Type, "project", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new AnalysisException($"{artifact} is incomplete; an evaluated project reference has no project node in its target framework.");
                        }
                    }
                    var childFrameworks = EvaluatedFrameworks(child);
                    var nearest = new FrameworkReducer().GetNearest(NuGetFramework.Parse(rootFramework), childFrameworks.Keys.Select(NuGetFramework.Parse));
                    if (nearest is null)
                    {
                        throw new AnalysisException("evaluated restore input evidence has incompatible project-reference frameworks.");
                    }
                    Visit(referencePath, nearest.GetShortFolderName(), rootFramework, depth + 1);
                }
            }
        }
    }

    private static bool IsFullyPrivate(JsonElement dependency, string property)
    {
        if (dependency.ValueKind != JsonValueKind.Object)
        {
            throw new AnalysisException("evaluated restore input evidence contains invalid dependency flow metadata.");
        }
        if (!dependency.TryGetProperty(property, out var value)) return false;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new AnalysisException("evaluated restore input evidence contains invalid dependency flow flags.");
        }
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in value.GetString()!.Split([',', ';']))
        {
            var flag = segment.Trim().ToLowerInvariant();
            if (flag is not ("all" or "none" or "compile" or "runtime" or "native" or "contentfiles" or "analyzers" or "build" or "buildtransitive"))
            {
                throw new AnalysisException("evaluated restore input evidence contains unsupported dependency flow flags.");
            }
            flags.Add(flag);
            // NuGet's BuildTransitive flag also includes Build.
            if (flag == "buildtransitive") flags.Add("build");
        }
        return flags.Contains("all") || AllAssetFlags.All(flags.Contains);
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
