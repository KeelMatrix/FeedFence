using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using NuGet.Configuration;

namespace KeelMatrix.FeedFence.Phase0Probe;

internal sealed partial class ProbeRunner
{
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
            "machine fixture source provenance matches its SDK-visible layer",
            effective.Sources.Single(source => source.Name == "Machine").Scope == (_machineFixtureInUserLayer ? "user" : "machine"));
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

    private void RunDisabledExactCase(string runRoot)
    {
        var repositoryRoot = Path.Combine(runRoot, "disabled-exact", "repository");
        var activeFeed = Path.Combine(runRoot, "feeds", "disabled-exact-active");
        WritePackage(Path.Combine(activeFeed, "Probe.DisabledExact.1.0.0.nupkg"), "Probe.DisabledExact", "active-wildcard");
        Directory.CreateDirectory(repositoryRoot);
        var configPath = Path.Combine(repositoryRoot, "NuGet.Config");
        File.WriteAllText(
            configPath,
            $"<configuration><packageSources><clear /><add key=\"DisabledExact\" value=\"{SecurityElement.Escape(Path.Combine(runRoot, "feeds", "disabled-exact-exact"))}\" /><add key=\"ActiveWildcard\" value=\"{SecurityElement.Escape(activeFeed)}\" /></packageSources><disabledPackageSources><add key=\"DisabledExact\" value=\"true\" /></disabledPackageSources><packageSourceMapping><packageSource key=\"DisabledExact\"><package pattern=\"Probe.DisabledExact\" /></packageSource><packageSource key=\"ActiveWildcard\"><package pattern=\"*\" /></packageSource></packageSourceMapping></configuration>",
            new UTF8Encoding(false));

        var projectPath = Path.Combine(repositoryRoot, "ProbeProject.csproj");
        WriteRestoreProject(projectPath, "Probe.DisabledExact");
        var restore = RunRestore(projectPath, repositoryRoot, Path.Combine(runRoot, "packages", "disabled-exact"), null, runRoot);
        Assert("disabled exact source is rejected by actual restore", restore.ExitCode != 0);

        WriteSyntheticAssets(projectPath, "Probe.DisabledExact");
        var shipping = RunShippingCheck(projectPath, null);
        Assert("disabled exact shipping CLI reports no eligible source", shipping.ExitCode == 1 && shipping.Output.Contains("FF003", StringComparison.Ordinal));
        Assert("disabled exact shipping CLI avoids reconciliation failure", !shipping.Output.Contains("could not be reconciled", StringComparison.Ordinal));
        Console.WriteLine($"Shipping CLI equivalence: disabled exact + active wildcard; restore-exit={restore.ExitCode}; analyzer-exit={shipping.ExitCode}; diagnostics=[{string.Join(", ", Regex.Matches(shipping.Output, "FF\\d{3}").Select(match => match.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))}]");
        _observations.Add("disabled exact mapping: actual NuGet restore rejected the package and the shipping CLI reported FF003 after the same resolved-package fixture was supplied, without a reconciliation exception");
    }
}
