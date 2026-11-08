using NuGet.Configuration;

namespace KeelMatrix.FeedFence.Phase0Probe;

internal sealed partial class ProbeRunner
{
    private void RunProvenanceCases(string runRoot)
    {
        var nestedRepositoryRoot = Path.Combine(runRoot, "provenance", "nested", "repository");
        var nestedProjectRoot = Path.Combine(nestedRepositoryRoot, "src", "NestedProject");
        var nestedSettings = LoadHierarchySettings(nestedProjectRoot, runRoot);
        var nestedEffective = Describe(nestedSettings, nestedRepositoryRoot);
        var nestedSource = nestedEffective.Sources.Single(source => source.Name == "NestedRepository");
        Assert("FF005 nested project repository source is classified as repository", nestedSource.Scope == "repository");
        Console.WriteLine($"FF005 provenance case: nested project; source={nestedSource.Name}; scope={nestedSource.Scope}");

        var userConfigPaths = new[]
        {
            Path.Combine(runRoot, "user-appdata", "NuGet", "NuGet.Config"),
            Path.Combine(runRoot, "user-profile", ".nuget", "NuGet", "NuGet.Config"),
            Path.Combine(runRoot, "dotnet-home", ".nuget", "NuGet", "NuGet.Config")
        };
        foreach (var userConfigPath in userConfigPaths)
        {
            if (File.Exists(userConfigPath))
            {
                File.Delete(userConfigPath);
            }
        }

        var missingUserRoot = Path.Combine(runRoot, "provenance", "missing-user", "project");
        var missingUserSettings = LoadHierarchySettings(missingUserRoot, runRoot);
        var missingUserEffective = Describe(missingUserSettings, missingUserRoot);
        var userMaterialized = userConfigPaths.Any(File.Exists);
        Assert("FF005 missing user config is materialized by NuGet settings load", userMaterialized);
        Assert("FF005 materialized user source is classified as user", missingUserEffective.Sources.Any(source => source.Scope == "user"));
        Console.WriteLine($"FF005 provenance case: missing user config; materialized={(userMaterialized ? "yes" : "no")}; userSources={string.Join(", ", missingUserEffective.Sources.Where(source => source.Scope == "user").Select(source => source.Name))}");

        foreach (var userConfigPath in userConfigPaths)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(userConfigPath)!);
            File.Copy(Path.Combine(runRoot, "hierarchy", "user", "NuGet.Config"), userConfigPath, overwrite: true);
        }

        var outsideRoot = Path.Combine(runRoot, "provenance", "outside", "project");
        var outsideSettings = LoadHierarchySettings(outsideRoot, runRoot);
        var outsideEffective = Describe(outsideSettings, outsideRoot);
        Assert("FF005 outside project has no repository-controlled source", outsideEffective.Sources.All(source => source.Scope != "repository"));
        Assert("FF005 outside project inherited source is visible", outsideEffective.Sources.Any(source => source.Scope is "machine" or "user"));
        Console.WriteLine($"FF005 provenance case: outside repository config; scopes=[{string.Join(", ", outsideEffective.Sources.Select(source => $"{source.Name}:{source.Scope}"))}]");

        var explicitRepositoryRoot = Path.Combine(runRoot, "provenance", "explicit-override", "repository");
        var explicitOverridePath = Path.Combine(runRoot, "provenance", "explicit-override", "override", "NuGet.Config");
        var explicitOverrideSettings = Settings.LoadSettingsGivenConfigPaths([explicitOverridePath]);
        var explicitOverrideEffective = Describe(explicitOverrideSettings, explicitRepositoryRoot);
        var explicitSource = explicitOverrideEffective.Sources.Single(source => source.Name == "ExternalOverride");
        Assert("FF005 explicit override outside repository is classified as external", explicitSource.Scope == "external");
        Console.WriteLine($"FF005 provenance case: explicit override outside repository; source={explicitSource.Name}; scope={explicitSource.Scope}");

        var siblingRepositoryRoot = Path.Combine(runRoot, "provenance", "boundary", "repo");
        var siblingConfigPath = Path.Combine(runRoot, "provenance", "boundary", "repo-user", "NuGet.Config");
        var siblingScope = ClassifyScope(siblingConfigPath, siblingRepositoryRoot);
        Assert("FF005 sibling repository prefix is not classified as repository", siblingScope == "external");
        Console.WriteLine($"FF005 provenance case: sibling-prefix boundary; repository={Label(siblingRepositoryRoot)}; config={Label(siblingConfigPath)}; scope={siblingScope}");
    }

    private void RunInjectedMachineControl(string runRoot)
    {
        var settings = Settings.LoadMachineWideSettings(Path.Combine(runRoot, "machine-common"), "NuGet", "Config");
        var effective = Describe(settings, Path.Combine(runRoot, "hierarchy", "repository"));
        AssertSet("injected machine API active source", effective.Sources.Where(source => source.IsEnabled).Select(source => source.Name), ["Machine"]);
        Assert("injected machine API disabled source", !effective.Sources.Single(source => source.Name == "DisabledMachine").IsEnabled);
        Assert("injected machine API provenance", effective.Sources.Single(source => source.Name == "Machine").Scope == "machine");
        _observations.Add("machine API: explicit isolated machine settings preserve active/disabled values and ConfigPath provenance on every OS");
        if (_machineFixtureInUserLayer)
        {
            _observations.Add("SDK fixture placement: machine-named sources use additional user configuration because macOS NuGet does not redirect its machine directory; all source/mapping/restore assertions remain enabled");
        }
    }
}
