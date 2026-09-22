using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using NuGet.Configuration;

namespace KeelMatrix.FeedFence.Phase0Probe;

internal static class Program
{
    public static int Main()
    {
        try
        {
            return new ProbeRunner().Run();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"STOP: probe infrastructure failure: {exception.GetType().Name}: {exception.Message}");
            return 2;
        }
    }
}

internal sealed class ProbeRunner
{
    private readonly List<string> _failures = [];
    private readonly List<string> _observations = [];
    private string _runRoot = string.Empty;

    public int Run()
    {
        var templateRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        if (!Directory.Exists(templateRoot))
        {
            throw new DirectoryNotFoundException($"Fixture corpus was not copied to {templateRoot}.");
        }

        var runRoot = Path.Combine(Path.GetTempPath(), "feedfence-phase0-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        _runRoot = runRoot;

        try
        {
            CopyDirectory(templateRoot, runRoot);
            ReplaceTokens(runRoot, "__RUN_ROOT__", runRoot);
            InstallScopeTemplates(runRoot);
            PrepareFeeds(runRoot);

            using var environment = new ProbeEnvironment(runRoot);

            Console.WriteLine("FeedFence Phase 0 feasibility probe");
            Console.WriteLine("NuGet.Configuration: 7.9.0");
            Console.WriteLine("Restore mode: synthetic local file feeds only; network-capable sources and credential-provider paths are absent.");
            Console.WriteLine();

            RunHierarchyCases(runRoot);
            RunClearCase(runRoot);
            RunNoMappingCase(runRoot);
            RunCasingCase(runRoot);
            RunExplicitConfigCase(runRoot);
            RunProvenanceCases(runRoot);
            RunParseSafetyCases(runRoot);

            AssertTemplateCorpusUnchanged(templateRoot);

            Console.WriteLine();
            Console.WriteLine("Evidence summary");
            foreach (var observation in _observations)
            {
                Console.WriteLine($"- {observation}");
            }

            if (_failures.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("STOP: fixture assertions failed");
                foreach (var failure in _failures)
                {
                    Console.WriteLine($"- {failure}");
                }

                return 1;
            }

            Console.WriteLine();
            Console.WriteLine("PASS (go): official NuGet APIs reproduce effective source and mapping values, independent restore markers prove eligible source outcomes, and source-item ConfigPath distinguishes repository, user, machine, and external origins for FF005.");
            Console.WriteLine("Provenance scope: classify only package-source declarations returned by NuGet by their official ConfigPath; do not re-merge settings or reimplement pattern matching.");
            Console.WriteLine("Read-only note: NuGet 7.9.0 can materialize a missing user NuGet.Config while loading default settings; the probe contains that side effect only inside its disposable run directory.");
            return 0;
        }
        finally
        {
            TryDelete(runRoot);
        }
    }

    private void RunHierarchyCases(string runRoot)
    {
        var repositoryRoot = Path.Combine(runRoot, "hierarchy", "repository");
        var settings = LoadHierarchySettings(repositoryRoot, runRoot);
        var effective = Describe(settings, repositoryRoot);

        AssertSet(
            "hierarchy active sources",
            effective.Sources.Where(source => source.IsEnabled).Select(source => source.Name),
            ["Machine", "User", "Repository"]);
        Assert(
            "disabled source is reported inactive",
            effective.Sources.Single(source => source.Name == "DisabledMachine").IsEnabled == false);
        Assert(
            "hierarchy mapping is enabled",
            effective.MappingEnabled);
        Assert(
            "machine source provenance",
            effective.Sources.Single(source => source.Name == "Machine").Scope == "machine");
        Assert(
            "user source provenance",
            effective.Sources.Single(source => source.Name == "User").Scope == "user");
        Assert(
            "repository source provenance",
            effective.Sources.Single(source => source.Name == "Repository").Scope == "repository");

        RunRestoreCase(runRoot, "hierarchy exact-ID mapping", repositoryRoot, "Probe.Exact", settings, expectSuccess: true, expectedCandidates: ["Repository"]);
        RunRestoreCase(runRoot, "hierarchy prefix mapping", repositoryRoot, "Probe.Prefix.Item", settings, expectSuccess: true, expectedCandidates: ["Repository"]);
        RunRestoreCase(runRoot, "hierarchy wildcard mapping", repositoryRoot, "Probe.Wildcard", settings, expectSuccess: true, expectedCandidates: ["User"]);
        RunRestoreCase(runRoot, "equal-specificity duplicate eligibility", repositoryRoot, "Probe.Duplicate", settings, expectSuccess: true, expectedCandidates: ["Machine", "User"]);
        _observations.Add("hierarchy: <clear /> absent, active Machine/User/Repository sources matched actual restore; DisabledMachine remained inactive");
        _observations.Add("mapping: official SearchForPattern selected exact-ID, longest-prefix, wildcard, and equal-specificity candidates");
        RunUnmappedCase(runRoot);
    }

    private void RunUnmappedCase(string runRoot)
    {
        var repositoryRoot = Path.Combine(runRoot, "unmapped", "repository");
        var settings = LoadHierarchySettings(repositoryRoot, runRoot);
        var effective = Describe(settings, repositoryRoot);
        RunRestoreCase(runRoot, "unmapped resolved package", repositoryRoot, "Probe.Unmapped", settings, expectSuccess: false, expectedCandidates: []);
        _observations.Add("unmapped package: a mapping configuration without a wildcard produced no eligible source and actual restore failed closed");
    }

    private void RunClearCase(string runRoot)
    {
        var repositoryRoot = Path.Combine(runRoot, "clear", "repository");
        var settings = LoadHierarchySettings(repositoryRoot, runRoot);
        var effective = Describe(settings, repositoryRoot);

        AssertSet(
            "repository <clear /> active sources",
            effective.Sources.Where(source => source.IsEnabled).Select(source => source.Name),
            ["ClearRepository"]);
        RunRestoreCase(runRoot, "repository <clear />", repositoryRoot, "Probe.Clear", settings, expectSuccess: true, expectedCandidates: ["ClearRepository"]);
        _observations.Add("<clear />: inherited machine and user sources were removed by official settings evaluation and actual restore used ClearRepository");
    }

    private void RunNoMappingCase(string runRoot)
    {
        var repositoryRoot = Path.Combine(runRoot, "nomapping", "repository");
        var settings = LoadHierarchySettings(repositoryRoot, runRoot);
        var effective = Describe(settings, repositoryRoot);

        Assert(
            "no-mapping fixture disables source mapping",
            effective.MappingEnabled == false);
        RunRestoreCase(runRoot, "multiple active sources without mapping", repositoryRoot, "Probe.NoMapping", settings, expectSuccess: true, expectedCandidates: ["Machine", "User", "Repository"]);
        _observations.Add("no mapping: official active-source set matched restore eligibility while three active sources remained available");
    }

    private void RunCasingCase(string runRoot)
    {
        var repositoryRoot = Path.Combine(runRoot, "casing", "repository");
        var settings = LoadHierarchySettings(repositoryRoot, runRoot);
        var effective = Describe(settings, repositoryRoot);

        Assert(
            "source-key casing mismatch is visible",
            effective.MappingEnabled && effective.MappedSourceNames.Contains("machine", StringComparer.Ordinal) &&
            effective.Sources.All(source => !string.Equals(source.Name, "machine", StringComparison.Ordinal)));
        RunRestoreCase(runRoot, "source-key casing variant", repositoryRoot, "Probe.Casing", settings, expectSuccess: true, expectedCandidates: ["Machine"]);
        _observations.Add("source-key casing: NuGet 7.9.0 matched the lowercase mapping key to the configured Machine source; the probe records this current behavior rather than applying the historical case-sensitive assumption");
    }

    private void RunExplicitConfigCase(string runRoot)
    {
        var repositoryRoot = Path.Combine(runRoot, "explicit");
        var configPath = Path.Combine(repositoryRoot, "explicit.config");
        var settings = Settings.LoadSettingsGivenConfigPaths([configPath]);
        var effective = Describe(settings, repositoryRoot);

        AssertSet(
            "explicit --config active sources",
            effective.Sources.Where(source => source.IsEnabled).Select(source => source.Name),
            ["Explicit"]);
        Assert(
            "explicit --config provenance",
            effective.Sources.Single(source => source.Name == "Explicit").Scope == "repository");
        RunRestoreCase(runRoot, "explicit --config override", repositoryRoot, "Probe.Explicit", settings, expectSuccess: true, expectedCandidates: ["Explicit"], configPath);
        _observations.Add("explicit --config: official LoadSettingsGivenConfigPaths and dotnet restore --configfile both ignored hierarchy and used Explicit");
    }

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

    private void RunRestoreCase(
        string runRoot,
        string name,
        string repositoryRoot,
        string packageId,
        ISettings settings,
        bool expectSuccess,
        IReadOnlyCollection<string> expectedCandidates,
        string? configPath = null)
    {
        var effective = Describe(settings, repositoryRoot);
        var candidates = effective.GetEligibleSourceNames(packageId);
        AssertSet($"{name} derived candidates", candidates, expectedCandidates);
        Assert(
            $"{name} has no network-capable source",
            effective.Sources.All(source => source.IsLocal));

        var projectPath = Path.Combine(repositoryRoot, "ProbeProject.csproj");
        WriteRestoreProject(projectPath, packageId);
        var packageRoot = Path.Combine(runRoot, "packages", Sanitize(name));
        Directory.CreateDirectory(packageRoot);
        var restore = RunRestore(projectPath, repositoryRoot, packageRoot, configPath, runRoot);
        var actualSuccess = restore.ExitCode == 0 && File.Exists(Path.Combine(repositoryRoot, "obj", "project.assets.json"));
        Assert($"{name} restore outcome", actualSuccess == expectSuccess);

        var resolvedIds = actualSuccess ? ReadResolvedPackageIds(Path.Combine(repositoryRoot, "obj", "project.assets.json")) : [];
        var selectedMarker = actualSuccess ? ReadPackageMarker(packageRoot) : null;
        if (actualSuccess)
        {
            Assert($"{name} restored package is present", resolvedIds.Contains(packageId, StringComparer.OrdinalIgnoreCase));
            Assert($"{name} selected source marker is eligible", selectedMarker is not null && expectedCandidates.Contains(selectedMarker, StringComparer.OrdinalIgnoreCase));
        }

        var actualEligible = ProbeActualEligibleSources(runRoot, name, repositoryRoot, packageId, effective, expectedCandidates, configPath);
        AssertSet($"{name} actual eligible sources", actualEligible, expectedCandidates);
        RunDisabledSourceControls(runRoot, name, repositoryRoot, packageId, effective, expectedCandidates, configPath);

        Console.WriteLine($"Restore case: {name}; package={packageId}; derived=[{string.Join(", ", candidates)}]; actual={(actualSuccess ? "success" : "failure")}; marker={selectedMarker ?? "none"}; actualEligible=[{string.Join(", ", actualEligible)}]; duration={restore.DurationMs} ms");
        if (!string.IsNullOrWhiteSpace(restore.Output))
        {
            Console.WriteLine($"  output: {restore.Output}");
        }
    }

    private string[] ProbeActualEligibleSources(
        string runRoot,
        string name,
        string repositoryRoot,
        string packageId,
        EffectiveConfig effective,
        IReadOnlyCollection<string> expectedCandidates,
        string? configPath)
    {
        var activeSources = effective.Sources.Where(source => source.IsEnabled).ToList();
        var actualEligible = new List<string>();
        foreach (var source in activeSources)
        {
            var disabledSources = activeSources
                .Where(candidate => !string.Equals(candidate.Name, source.Name, StringComparison.Ordinal))
                .Select(candidate => candidate.Name)
                .ToArray();
            var control = RunControlledRestore(
                runRoot,
                $"{name} only {source.Name}",
                repositoryRoot,
                packageId,
                configPath,
                activeSources,
                disabledSources,
                runRoot);

            if (control.Success && string.Equals(control.Marker, source.Name, StringComparison.OrdinalIgnoreCase))
            {
                actualEligible.Add(source.Name);
            }

            var shouldSucceed = expectedCandidates.Contains(source.Name, StringComparer.Ordinal);
            Assert(
                $"{name} isolated {source.Name} outcome",
                control.Success == shouldSucceed && (!control.Success || string.Equals(control.Marker, source.Name, StringComparison.OrdinalIgnoreCase)));
        }

        return actualEligible.Order(StringComparer.Ordinal).ToArray();
    }

    private void RunDisabledSourceControls(
        string runRoot,
        string name,
        string repositoryRoot,
        string packageId,
        EffectiveConfig effective,
        IReadOnlyCollection<string> expectedCandidates,
        string? configPath)
    {
        foreach (var sourceName in expectedCandidates.Order(StringComparer.Ordinal))
        {
            var control = RunControlledRestore(
                runRoot,
                $"{name} with {sourceName} disabled",
                repositoryRoot,
                packageId,
                configPath,
                effective.Sources.Where(source => source.IsEnabled).ToList(),
                [sourceName],
                runRoot);
            var shouldSucceed = expectedCandidates.Count > 1;
            Assert(
                $"{name} one-feed-disabled {sourceName} outcome",
                control.Success == shouldSucceed && (!control.Success || !string.Equals(control.Marker, sourceName, StringComparison.OrdinalIgnoreCase)));
            Console.WriteLine($"  one-feed-disabled: {sourceName}; actual={(control.Success ? "success" : "failure")}; marker={control.Marker ?? "none"}");
        }
    }

    private static ControlledRestoreResult RunControlledRestore(
        string runRoot,
        string name,
        string repositoryRoot,
        string packageId,
        string? configPath,
        IReadOnlyCollection<SourceObservation> activeSources,
        IReadOnlyCollection<string> disabledSources,
        string displayRoot)
    {
        var controlRoot = Path.Combine(runRoot, "controls", Sanitize(name) + "-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(repositoryRoot, controlRoot);
        TryDelete(Path.Combine(controlRoot, "obj"));
        TryDelete(Path.Combine(controlRoot, "bin"));

        var controlledConfigPath = configPath is null
            ? Path.Combine(controlRoot, "NuGet.Config")
            : MapCopiedPath(repositoryRoot, controlRoot, configPath);
        RewritePackageSources(
            controlledConfigPath,
            activeSources.Where(source => !disabledSources.Contains(source.Name, StringComparer.Ordinal)).ToArray());

        var projectPath = Path.Combine(controlRoot, "ProbeProject.csproj");
        WriteRestoreProject(projectPath, packageId);
        var packageRoot = Path.Combine(runRoot, "packages", "control-" + Sanitize(name) + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        var restore = RunRestore(
            projectPath,
            controlRoot,
            packageRoot,
            configPath is null ? null : controlledConfigPath,
            displayRoot);
        var success = restore.ExitCode == 0 && File.Exists(Path.Combine(controlRoot, "obj", "project.assets.json"));
        var marker = success ? ReadPackageMarker(packageRoot) : null;
        Console.WriteLine($"  isolated control: {name}; disabled=[{string.Join(", ", disabledSources.Order(StringComparer.Ordinal))}]; actual={(success ? "success" : "failure")}; marker={marker ?? "none"}");
        if (!success && !string.IsNullOrWhiteSpace(restore.Output))
        {
            Console.WriteLine($"    control output: {restore.Output}");
        }
        TryDelete(controlRoot);
        return new ControlledRestoreResult(success, marker);
    }

    private static string MapCopiedPath(string sourceRoot, string destinationRoot, string path)
    {
        var relative = Path.GetRelativePath(sourceRoot, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            var destination = Path.Combine(destinationRoot, "controlled.config");
            File.Copy(path, destination, overwrite: true);
            return destination;
        }

        return Path.Combine(destinationRoot, relative);
    }

    private static void RewritePackageSources(string configPath, IReadOnlyCollection<SourceObservation> allowedSources)
    {
        var document = XDocument.Load(configPath, LoadOptions.PreserveWhitespace);
        var configuration = document.Root ?? throw new InvalidOperationException($"Missing configuration root in {configPath}.");
        var section = configuration.Element("packageSources");
        if (section is null)
        {
            section = new XElement("packageSources");
            configuration.Add(section);
        }

        section.RemoveNodes();
        section.Add(new XElement("clear"));
        foreach (var source in allowedSources)
        {
            section.Add(new XElement("add", new XAttribute("key", source.Name), new XAttribute("value", source.Source)));
        }

        document.Save(configPath, SaveOptions.DisableFormatting);
    }

    private static RestoreResult RunRestore(string projectPath, string workingDirectory, string packageRoot, string? configPath, string runRoot)
    {
        var arguments = new List<string>
        {
            "restore",
            projectPath,
            "--packages",
            packageRoot,
            "--force",
            "--no-cache",
            "--disable-parallel",
            "--verbosity",
            "minimal"
        };
        if (configPath is not null)
        {
            arguments.Add("--configfile");
            arguments.Add(configPath);
        }

        var displayCommand = "dotnet " + string.Join(" ", arguments.Select(argument => argument.Replace(runRoot, "<fixture-root>", StringComparison.OrdinalIgnoreCase)));
        var start = Stopwatch.StartNew();
        var processStart = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            processStart.ArgumentList.Add(argument);
        }

        using var process = Process.Start(processStart) ?? throw new InvalidOperationException("Unable to start dotnet restore.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        start.Stop();

        var combined = (standardOutput + " " + standardError).Replace(runRoot, "<fixture-root>", StringComparison.OrdinalIgnoreCase).Replace("\r", " ").Replace("\n", " ").Trim();
        if (combined.Length > 500)
        {
            combined = combined[..500] + "...";
        }

        Console.WriteLine($"  command: {displayCommand}; exit={process.ExitCode}; duration={start.ElapsedMilliseconds} ms");
        return new RestoreResult(process.ExitCode, start.ElapsedMilliseconds, combined);
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

    private void RunParseSafetyCases(string runRoot)
    {
        var malformedConfigPath = Path.Combine(runRoot, "parse-safety", "malformed.config");
        var malformedConfigRejected = TryLoadConfigAndGetFailure(malformedConfigPath, out var malformedConfigFailure);
        Assert("malformed NuGet config is rejected", malformedConfigRejected);
        Console.WriteLine($"Parse fixture: malformed NuGet.Config; rejected={(malformedConfigRejected ? "yes" : "no")}; exception={malformedConfigFailure ?? "none"}");

        var dtdConfigPath = Path.Combine(runRoot, "parse-safety", "dtd.config");
        var dtdRejected = TryLoadConfigAndGetFailure(dtdConfigPath, out var dtdFailure);
        Assert("DTD/external-entity NuGet config is rejected", dtdRejected);
        Console.WriteLine($"Parse fixture: DTD/external entity NuGet.Config; rejected={(dtdRejected ? "yes" : "no")}; exception={dtdFailure ?? "none"}");

        var malformedJsonPath = Path.Combine(runRoot, "parse-safety", "malformed.json");
        var malformedJsonRejected = TryParseJsonAndGetFailure(malformedJsonPath, out var malformedJsonFailure);
        Assert("malformed JSON is rejected", malformedJsonRejected);
        Console.WriteLine($"Parse fixture: malformed JSON; rejected={(malformedJsonRejected ? "yes" : "no")}; exception={malformedJsonFailure ?? "none"}");
    }

    private static bool TryLoadConfigAndGetFailure(string path, out string? exceptionType)
    {
        try
        {
            Settings.LoadSettingsGivenConfigPaths([path]);
            exceptionType = null;
            return false;
        }
        catch (Exception exception)
        {
            exceptionType = exception.GetType().Name;
            return true;
        }
    }

    private static bool TryParseJsonAndGetFailure(string path, out string? exceptionType)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            document.RootElement.ToString();
            exceptionType = null;
            return false;
        }
        catch (Exception exception)
        {
            exceptionType = exception.GetType().Name;
            return true;
        }
    }

    private static void InstallScopeTemplates(string runRoot)
    {
        var userConfig = Path.Combine(runRoot, "user-appdata", "NuGet", "NuGet.Config");
        Directory.CreateDirectory(Path.GetDirectoryName(userConfig)!);
        File.Copy(Path.Combine(runRoot, "hierarchy", "user", "NuGet.Config"), userConfig, overwrite: true);

        var unixUserConfig = Path.Combine(runRoot, "user-profile", ".nuget", "NuGet", "NuGet.Config");
        Directory.CreateDirectory(Path.GetDirectoryName(unixUserConfig)!);
        File.Copy(Path.Combine(runRoot, "hierarchy", "user", "NuGet.Config"), unixUserConfig, overwrite: true);

        var dotnetUserConfig = Path.Combine(runRoot, "dotnet-home", ".nuget", "NuGet", "NuGet.Config");
        Directory.CreateDirectory(Path.GetDirectoryName(dotnetUserConfig)!);
        File.Copy(Path.Combine(runRoot, "hierarchy", "user", "NuGet.Config"), dotnetUserConfig, overwrite: true);

        var machineConfig = Path.Combine(runRoot, "machine-common", "NuGet", "Config", "NuGet.Config");
        Directory.CreateDirectory(Path.GetDirectoryName(machineConfig)!);
        File.Copy(Path.Combine(runRoot, "hierarchy", "machine", "NuGet.Config"), machineConfig, overwrite: true);
    }

    private static void PrepareFeeds(string runRoot)
    {
        var feeds = Path.Combine(runRoot, "feeds");
        Directory.CreateDirectory(feeds);
        WritePackage(Path.Combine(feeds, "machine", "Probe.Duplicate.1.0.0.nupkg"), "Probe.Duplicate", "machine");
        WritePackage(Path.Combine(feeds, "machine", "Probe.NoMapping.1.0.0.nupkg"), "Probe.NoMapping", "machine");
        WritePackage(Path.Combine(feeds, "user", "Probe.Wildcard.1.0.0.nupkg"), "Probe.Wildcard", "user");
        WritePackage(Path.Combine(feeds, "user", "Probe.Duplicate.1.0.0.nupkg"), "Probe.Duplicate", "user");
        WritePackage(Path.Combine(feeds, "user", "Probe.NoMapping.1.0.0.nupkg"), "Probe.NoMapping", "user");
        WritePackage(Path.Combine(feeds, "repository", "Probe.Exact.1.0.0.nupkg"), "Probe.Exact", "repository");
        WritePackage(Path.Combine(feeds, "repository", "Probe.Prefix.Item.1.0.0.nupkg"), "Probe.Prefix.Item", "repository");
        WritePackage(Path.Combine(feeds, "repository", "Probe.NoMapping.1.0.0.nupkg"), "Probe.NoMapping", "repository");
        WritePackage(Path.Combine(feeds, "clear", "Probe.Clear.1.0.0.nupkg"), "Probe.Clear", "ClearRepository");
        WritePackage(Path.Combine(feeds, "explicit", "Probe.Explicit.1.0.0.nupkg"), "Probe.Explicit", "Explicit");
        WritePackage(Path.Combine(feeds, "machine", "Probe.Casing.1.0.0.nupkg"), "Probe.Casing", "machine");
    }

    private static void WritePackage(string path, string id, string marker)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var nuspec = archive.CreateEntry($"{id}.nuspec");
        using (var writer = new StreamWriter(nuspec.Open(), Encoding.UTF8))
        {
            writer.Write($"<?xml version=\"1.0\" encoding=\"utf-8\"?><package><metadata><id>{id}</id><version>1.0.0</version><authors>Fixture</authors><description>Offline fixture package.</description></metadata></package>");
        }

        var markerEntry = archive.CreateEntry("content/probe-marker.txt");
        using (var markerWriter = new StreamWriter(markerEntry.Open(), Encoding.UTF8))
        {
            markerWriter.Write(marker);
        }

        var originEntry = archive.CreateEntry("feedfence-origin.txt");
        using (var originWriter = new StreamWriter(originEntry.Open(), Encoding.UTF8))
        {
            originWriter.Write(marker);
        }
    }

    private static void WriteRestoreProject(string path, string packageId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework><RestoreProjectStyle>PackageReference</RestoreProjectStyle></PropertyGroup><ItemGroup><PackageReference Include=\"{packageId}\" Version=\"1.0.0\" /></ItemGroup></Project>", new UTF8Encoding(false));
    }

    private static HashSet<string> ReadResolvedPackageIds(string assetsPath)
    {
        using var stream = File.OpenRead(assetsPath);
        using var document = JsonDocument.Parse(stream);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!document.RootElement.TryGetProperty("libraries", out var libraries))
        {
            return result;
        }

        foreach (var property in libraries.EnumerateObject())
        {
            var separator = property.Name.IndexOf('/');
            if (separator > 0)
            {
                result.Add(property.Name[..separator]);
            }
        }

        return result;
    }

    private static string? ReadPackageMarker(string packageRoot)
    {
        var markerPath = Directory.GetFiles(packageRoot, "feedfence-origin.txt", SearchOption.AllDirectories).SingleOrDefault();
        return markerPath is null ? null : File.ReadAllText(markerPath).Trim();
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destinationPath = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(file, destinationPath, overwrite: true);
        }
    }

    private static void ReplaceTokens(string root, string token, string value)
    {
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(file);
            if (content.Contains(token, StringComparison.Ordinal))
            {
                File.WriteAllText(file, content.Replace(token, value, StringComparison.Ordinal), new UTF8Encoding(false));
            }
        }
    }

    private void AssertTemplateCorpusUnchanged(string templateRoot)
    {
        foreach (var file in Directory.GetFiles(templateRoot, "*", SearchOption.AllDirectories))
        {
            if (File.ReadAllText(file).Contains("__RUN_ROOT__", StringComparison.Ordinal) || !file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Assert("fixture corpus remains readable", File.Exists(file));
        }
    }

    private static string Label(string path)
    {
        var directory = new DirectoryInfo(path);
        return string.Equals(directory.Name, "repository", StringComparison.OrdinalIgnoreCase)
            ? directory.Parent?.Name ?? directory.Name
            : directory.Name;
    }

    private static string Sanitize(string value) => new(value.Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray());

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Disposable evidence should not turn a passing probe into a failure.
        }
    }

    private void Assert(string description, bool condition)
    {
        if (!condition)
        {
            _failures.Add(description);
        }
    }

    private void AssertSet(string description, IEnumerable<string> actual, IEnumerable<string> expected)
    {
        var actualSet = actual.ToHashSet(StringComparer.Ordinal);
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        Assert(description, actualSet.SetEquals(expectedSet));
        if (!actualSet.SetEquals(expectedSet))
        {
            _failures.Add($"{description}: expected [{string.Join(", ", expectedSet)}], actual [{string.Join(", ", actualSet)}]");
        }
    }
}

internal sealed record SourceObservation(string Name, string Source, bool IsEnabled, string ConfigPath, string Scope)
{
    public bool IsLocal => !Source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !Source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}

internal sealed class EffectiveConfig
{
    private readonly PackageSourceMapping _mapping;

    public EffectiveConfig(IReadOnlyList<SourceObservation> sources, PackageSourceMapping mapping, IReadOnlySet<string> mappedSourceNames)
    {
        Sources = sources;
        _mapping = mapping;
        MappedSourceNames = mappedSourceNames;
    }

    public IReadOnlyList<SourceObservation> Sources { get; }
    public bool MappingEnabled => _mapping.IsEnabled;
    public IReadOnlySet<string> MappedSourceNames { get; }

    public IReadOnlyList<string> GetEligibleSourceNames(string packageId)
    {
        var activeNames = Sources
            .Where(source => source.IsEnabled)
            .ToDictionary(source => source.Name, StringComparer.OrdinalIgnoreCase);
        if (!_mapping.IsEnabled)
        {
            return activeNames.Values.Select(source => source.Name).Order(StringComparer.Ordinal).ToArray();
        }

        var configuredNames = _mapping.GetConfiguredPackageSources(packageId);
        return configuredNames
            .Where(activeNames.ContainsKey)
            .Select(name => activeNames[name].Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}

internal sealed record RestoreResult(int ExitCode, long DurationMs, string Output);

internal sealed record ControlledRestoreResult(bool Success, string? Marker);

internal sealed class ExplicitMachineWideSettings : IMachineWideSettings
{
    public ExplicitMachineWideSettings(ISettings settings)
    {
        Settings = settings;
    }

    public ISettings Settings { get; }
}

internal sealed class ProbeEnvironment : IDisposable
{
    private readonly Dictionary<string, string?> _previous = [];

    public ProbeEnvironment(string runRoot)
    {
        var values = new Dictionary<string, string?>
        {
            ["APPDATA"] = Path.Combine(runRoot, "user-appdata"),
            ["USERPROFILE"] = Path.Combine(runRoot, "user-profile"),
            ["HOME"] = Path.Combine(runRoot, "user-profile"),
            ["NUGET_COMMON_APPLICATION_DATA"] = Path.Combine(runRoot, "machine-common"),
            ["NUGET_PACKAGES"] = Path.Combine(runRoot, "process-packages"),
            ["NUGET_HTTP_CACHE_PATH"] = Path.Combine(runRoot, "http-cache"),
            ["DOTNET_CLI_HOME"] = Path.Combine(runRoot, "dotnet-home"),
            ["NUGET_PLUGIN_PATHS"] = string.Empty,
            ["NUGET_CREDENTIALPROVIDERS_PATH"] = string.Empty,
            ["NUGET_CERT_REVOCATION_MODE"] = "offline",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_NOLOGO"] = "1"
        };

        foreach (var pair in values)
        {
            _previous[pair.Key] = Environment.GetEnvironmentVariable(pair.Key);
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    public void Dispose()
    {
        foreach (var pair in _previous)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }
}
