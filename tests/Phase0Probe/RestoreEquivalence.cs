using System.Text.Json;
using System.Text.RegularExpressions;
using NuGet.Configuration;

namespace KeelMatrix.FeedFence.Phase0Probe;

internal sealed partial class ProbeRunner
{
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
            AssertSdkSourceInputs(name, projectPath, effective);
        }

        var actualEligible = ProbeActualEligibleSources(runRoot, name, repositoryRoot, packageId, effective, expectedCandidates, configPath);
        AssertSet($"{name} actual eligible sources", actualEligible, expectedCandidates);
        RunDisabledSourceControls(runRoot, name, repositoryRoot, packageId, effective, expectedCandidates, configPath);

        Console.WriteLine($"Restore case: {name}; package={packageId}; derived=[{string.Join(", ", candidates)}]; actual={(actualSuccess ? "success" : "failure")}; marker={selectedMarker ?? "none"}; actualEligible=[{string.Join(", ", actualEligible)}]; duration={restore.DurationMs} ms");
        if (!string.IsNullOrWhiteSpace(restore.Output))
        {
            Console.WriteLine($"  output: {restore.Output}");
        }

        if (!actualSuccess)
        {
            Console.WriteLine($"Shipping CLI equivalence: {name}; skipped because restore produced no trustworthy assets graph");
            return;
        }

        var mappingEnabled = PackageSourceMapping.GetPackageSourceMapping(settings).IsEnabled;
        var expectedShippingExit = mappingEnabled ? expectedCandidates.Count == 1 ? 0 : 1 : expectedCandidates.Count > 1 ? 1 : 0;
        var shipping = RunShippingCheck(projectPath, configPath);
        Assert($"{name} shipping CLI matches restore policy exit", shipping.ExitCode == expectedShippingExit);
        if (expectedCandidates.Count > 1)
        {
            Assert($"{name} shipping CLI reports ambiguity", shipping.Output.Contains(mappingEnabled ? "FF002" : "FF001", StringComparison.Ordinal));
        }
        if (name.Contains("casing", StringComparison.OrdinalIgnoreCase))
        {
            Assert($"{name} shipping CLI accepts case-only source identity", !shipping.Output.Contains("FF004", StringComparison.Ordinal));
        }
        var diagnosticCodes = Regex.Matches(shipping.Output, "FF\\d{3}")
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        Console.WriteLine($"Shipping CLI equivalence: {name}; expected-exit={expectedShippingExit}; actual-exit={shipping.ExitCode}; diagnostics=[{string.Join(", ", diagnosticCodes)}]");
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

    private void AssertSdkSourceInputs(string name, string projectPath, EffectiveConfig effective)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(projectPath)!, "obj", "project.assets.json")));
        var sources = document.RootElement.GetProperty("project").GetProperty("restore").GetProperty("sources");
        var actualPaths = sources.EnumerateObject().Select(source => NormalizeLocalSource(source.Name))
            .ToHashSet(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var expectedPaths = effective.Sources.Where(source => source.IsEnabled).Select(source => NormalizeLocalSource(source.Source));
        if (!actualPaths.SetEquals(expectedPaths))
        {
            Console.WriteLine($"  SDK input mismatch: actual=[{string.Join(", ", actualPaths.Select(path => path.Replace(_runRoot, "<fixture-root>", StringComparison.OrdinalIgnoreCase)))}]; modeled=[{string.Join(", ", expectedPaths.Select(path => path.Replace(_runRoot, "<fixture-root>", StringComparison.OrdinalIgnoreCase)))}]");
        }
        Assert($"{name} SDK source inputs equal the modeled active sources before isolation controls", actualPaths.SetEquals(expectedPaths));

        static string NormalizeLocalSource(string source)
        {
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
            {
                return uri.IsFile ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(uri.LocalPath)) : uri.AbsoluteUri;
            }
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        }
    }
}
