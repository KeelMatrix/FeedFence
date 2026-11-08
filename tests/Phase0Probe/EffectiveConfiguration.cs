using NuGet.Configuration;

namespace KeelMatrix.FeedFence.Phase0Probe;

internal sealed partial class ProbeRunner
{
    private EffectiveConfig Describe(ISettings settings, string repositoryRoot)
    {
        if (settings is not Settings concreteSettings)
        {
            throw new InvalidOperationException("NuGet did not return concrete Settings for the fixture.");
        }

        var sourceItems = settings.GetSection("packageSources")?.Items.OfType<SourceItem>().ToList() ?? [];
        var sources = new PackageSourceProvider(settings)
            .LoadPackageSources()
            .Select(source =>
            {
                var configPath = sourceItems
                    .FirstOrDefault(item => string.Equals(item.Key, source.Name, StringComparison.OrdinalIgnoreCase))?.ConfigPath ?? string.Empty;
                return new SourceObservation(
                    source.Name,
                    source.Source,
                    source.IsEnabled,
                    configPath,
                    ClassifyScope(configPath, repositoryRoot));
            })
            .ToList();
        var mapping = PackageSourceMapping.GetPackageSourceMapping(settings);
        var mappingItems = new PackageSourceMappingProvider(settings).GetPackageSourceMappingItems();
        var mappedSourceNames = mappingItems.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);

        Console.WriteLine($"Effective config: {Label(repositoryRoot)}");
        Console.WriteLine($"  config files: {string.Join(", ", concreteSettings.GetConfigFilePaths().Select(Path.GetFileName))}");
        Console.WriteLine($"  sources: {string.Join(", ", sources.Select(source => $"{source.Name}={(source.IsEnabled ? "active" : "disabled")}/{source.Scope}"))}");
        Console.WriteLine($"  mapping enabled: {mapping.IsEnabled}; mapping keys: {string.Join(", ", mappedSourceNames)}");

        return new EffectiveConfig(sources, mapping, mappedSourceNames);
    }

    private static ISettings LoadHierarchySettings(string repositoryRoot, string runRoot)
    {
        var machineRoot = Path.Combine(runRoot, "machine-common");
        var machineSettings = Settings.LoadMachineWideSettings(machineRoot, "NuGet", "Config");
        return Settings.LoadDefaultSettings(repositoryRoot, null, new ExplicitMachineWideSettings(machineSettings));
    }

    private string ClassifyScope(string configPath, string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(configPath))
        {
            return "unknown";
        }

        var fullPath = Path.GetFullPath(configPath);
        var machinePath = Path.GetFullPath(Path.Combine(_runRoot, "machine-common", "NuGet", "Config"));
        var userPaths = new[]
        {
            Path.GetFullPath(Path.Combine(_runRoot, "user-appdata", "NuGet")),
            Path.GetFullPath(Path.Combine(_runRoot, "user-profile", ".nuget", "NuGet")),
            Path.GetFullPath(Path.Combine(_runRoot, "dotnet-home", ".nuget", "NuGet"))
        };
        if (IsWithinDirectory(fullPath, repositoryRoot))
        {
            return "repository";
        }

        if (IsWithinDirectory(fullPath, machinePath))
        {
            return "machine";
        }

        if (userPaths.Any(userPath => IsWithinDirectory(fullPath, userPath)))
        {
            return "user";
        }

        return "external";
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
        return relative == "." ||
            (!Path.IsPathRooted(relative) &&
             !string.Equals(relative, "..", StringComparison.Ordinal) &&
             !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
             !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal));
    }
}
