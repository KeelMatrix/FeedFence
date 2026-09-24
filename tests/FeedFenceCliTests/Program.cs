using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.Json;

namespace KeelMatrix.FeedFence.CliTests;

internal static class Program
{
    private static int Main()
    {
        try
        {
            using var fixture = new Fixture();
            fixture.RunAll();
            Console.WriteLine("PASS: FeedFence CLI contract tests");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: {exception.Message}");
            return 1;
        }
    }
}

internal sealed class Fixture : IDisposable
{
    private static readonly string[] CompanySecretDependencies = ["Company.Secret"];
    private static readonly string[] CompanySecretDependencyGroup = ["Company.Secret >= 1.0.0"];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "feedfence-cli-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _tool;

    public Fixture()
    {
        Directory.CreateDirectory(_root);
        _tool = Path.Combine(AppContext.BaseDirectory, "KeelMatrix.FeedFence.dll");
        if (!File.Exists(_tool))
        {
            throw new InvalidOperationException("the shipping tool was not copied to the test output.");
        }
    }

    public void RunAll()
    {
        AssertProcess(0, "--help");
        var help = Run("--help");
        AssertContains(help.Output, "feedfence check [path] [options]");
        AssertContains(help.Output, "Declared dependency targets must use supported NuGet values and compatible assets records.");
        AssertProcess(0, "--version");

        var specificity = CreateCase("specificity", ["Feed.Exact", "Feed.Prefix.Item", "Feed.Wildcard"],
            [
                "<packageSourceMapping><packageSource key=\"exact\"><package pattern=\"Feed.Exact\" /></packageSource><packageSource key=\"prefix\"><package pattern=\"Feed.Prefix.*\" /></packageSource><packageSource key=\"wildcard\"><package pattern=\"*\" /></packageSource></packageSourceMapping>",
            ],
            ["exact", "prefix", "wildcard"]);
        AssertProcess(0, "check", specificity.Project, "--config", specificity.Config!);
        var specificityOutput = Run("check", specificity.Project, "--config", specificity.Config!).Output;
        AssertContains(specificityOutput, "No restore-source policy violations found.");
        AssertNotContains(specificityOutput, "FF002");
        AssertContains(specificityOutput, "FF008");

        var json = Run("check", specificity.Project, "--config", specificity.Config!, "--format", "json");
        AssertEqual(0, json.ExitCode, "JSON exit code");
        AssertNotContains(json.StandardError, "noise");
        AssertEqual(string.Empty, json.StandardError, "JSON stderr");
        using (var jsonDocument = JsonDocument.Parse(json.StandardOutput))
        {
            AssertEqual(1, jsonDocument.RootElement.GetProperty("schemaVersion").GetInt32(), "JSON schema version");
            AssertEqual("json", jsonDocument.RootElement.GetProperty("format").GetString(), "JSON format");
            AssertEqual("FF008", jsonDocument.RootElement.GetProperty("diagnostics").EnumerateArray().Single(diagnostic => diagnostic.GetProperty("code").GetString() == "FF008").GetProperty("code").GetString(), "JSON diagnostic identity");
        }

        var jsonRepeat = Run("check", specificity.Project, "--config", specificity.Config!, "--format", "json");
        AssertEqual(json.StandardOutput, jsonRepeat.StandardOutput, "JSON byte determinism");

        var sarif = Run("check", specificity.Project, "--config", specificity.Config!, "--format", "sarif");
        AssertEqual(0, sarif.ExitCode, "SARIF exit code");
        AssertEqual(string.Empty, sarif.StandardError, "SARIF stderr");
        using (var sarifDocument = JsonDocument.Parse(sarif.StandardOutput))
        {
            var driver = sarifDocument.RootElement.GetProperty("runs")[0].GetProperty("tool").GetProperty("driver");
            AssertEqual("FF008", driver.GetProperty("rules").EnumerateArray().Single(rule => rule.GetProperty("id").GetString() == "FF008").GetProperty("id").GetString(), "SARIF diagnostic rule");
            AssertEqual("FF008", sarifDocument.RootElement.GetProperty("runs")[0].GetProperty("results").EnumerateArray().Single(result => result.GetProperty("ruleId").GetString() == "FF008").GetProperty("ruleId").GetString(), "SARIF result identity");
        }

        var sarifRepeat = Run("check", specificity.Project, "--config", specificity.Config!, "--format", "sarif");
        AssertEqual(sarif.StandardOutput, sarifRepeat.StandardOutput, "SARIF byte determinism");

        var telemetryResult = new AnalysisResult(
            0,
            6,
            3,
            true,
            2,
            true,
            [new SourceInfo("public", "file:///local", true, "repository-controlled configuration", true)],
            [new Diagnostic("FF005", DiagnosticSeverity.Warning, "synthetic warning")]);
        var telemetryActivationRequests = 0;
        string? telemetryToolName = null;
        Type? telemetryToolType = null;
        FeedFenceTelemetry.TrackActivation(
            telemetryResult,
            (toolName, toolType) =>
            {
                telemetryToolName = toolName;
                telemetryToolType = toolType;
                return () => telemetryActivationRequests++;
            });
        AssertEqual(1, telemetryActivationRequests, "eligible shared telemetry activation request");
        AssertEqual("feedfence", telemetryToolName, "shared telemetry tool name");
        AssertEqual(typeof(VersionInfo), telemetryToolType, "shared telemetry version type");
        var sharedTrackActivation = typeof(KeelMatrix.Telemetry.Client).GetMethod("TrackActivation", Type.EmptyTypes)
            ?? throw new InvalidOperationException("shared telemetry has no parameterless activation API");
        AssertEqual(0, sharedTrackActivation.GetParameters().Length, "shared telemetry activation payload parameter count");

        FeedFenceTelemetry.TrackActivation(
            new AnalysisResult(0, 0, 1, false, 0, false, [], []),
            (_, _) => throw new InvalidOperationException("ineligible analysis requested telemetry"));
        AssertEqual(false, FeedFenceTelemetry.IsActivationEligible(new AnalysisResult(0, 0, 1, false, 0, false, [], [])), "no-package telemetry activation eligibility");

        FeedFenceTelemetry.TrackActivation(telemetryResult, (_, _) => () => throw new InvalidOperationException("synthetic telemetry failure"));

        var equal = CreateCase("equal", ["Feed.Equal"],
            [
                "<packageSourceMapping><packageSource key=\"left\"><package pattern=\"Feed.Equal\" /></packageSource><packageSource key=\"right\"><package pattern=\"Feed.Equal\" /></packageSource></packageSourceMapping>",
            ],
            ["left", "right"]);
        AssertProcess(1, "check", equal.Project, "--config", equal.Config!);
        AssertContains(Run("check", equal.Project, "--config", equal.Config!).Output, "FF002");

        var noMapping = CreateCase("nomapping", ["Feed.One"], [], ["one", "two"]);
        AssertProcess(1, "check", noMapping.Project, "--config", noMapping.Config!);
        AssertContains(Run("check", noMapping.Project, "--config", noMapping.Config!).Output, "FF001");

        var unmapped = CreateCase("unmapped", ["Feed.Unmapped"],
            ["<packageSourceMapping><packageSource key=\"one\"><package pattern=\"Other.*\" /></packageSource></packageSourceMapping>"], ["one"]);
        AssertProcess(1, "check", unmapped.Project, "--config", unmapped.Config!);
        AssertContains(Run("check", unmapped.Project, "--config", unmapped.Config!).Output, "FF003");

        var casing = CreateCase("casing", ["Feed.Casing"],
            ["<packageSourceMapping><packageSource key=\"One\"><package pattern=\"Feed.Casing\" /></packageSource></packageSourceMapping>"], ["one"]);
        AssertProcess(0, "check", casing.Project, "--config", casing.Config!);
        AssertNotContains(Run("check", casing.Project, "--config", casing.Config!).Output, "FF004");

        var invalidMapping = CreateCase("invalid-mapping", ["Feed.Invalid"],
            ["<packageSourceMapping><packageSource key=\"missing\"><package pattern=\"Feed.Invalid\" /></packageSource></packageSourceMapping>"], ["one"]);
        var invalidMappingResult = Run("check", invalidMapping.Project, "--config", invalidMapping.Config!);
        AssertEqual(1, invalidMappingResult.ExitCode, "invalid mapping source identity exit code");
        AssertContains(invalidMappingResult.Output, "FF004");

        var wildcardAmbiguity = CreateCase("wildcard-ambiguity", ["Company.Internal"],
            ["<packageSourceMapping><packageSource key=\"left\"><package pattern=\"Company.*\" /></packageSource><packageSource key=\"right\"><package pattern=\"Company.*\" /></packageSource></packageSourceMapping>"], ["left", "right"]);
        var wildcardAmbiguityResult = Run("check", wildcardAmbiguity.Project, "--config", wildcardAmbiguity.Config!);
        AssertEqual(1, wildcardAmbiguityResult.ExitCode, "wildcard mapping ambiguity exit code");
        AssertContains(wildcardAmbiguityResult.Output, "Company.*");

        var disabledExact = CreateCase("disabled-exact", ["Feed.Disabled"], [], ["exact", "active"]);
        File.WriteAllText(
            disabledExact.Config!,
            "<configuration><packageSources><clear /><add key=\"exact\" value=\"" + SecurityElement.Escape(Path.Combine(disabledExact.Root, "feeds", "exact")) + "\" /><add key=\"active\" value=\"" + SecurityElement.Escape(Path.Combine(disabledExact.Root, "feeds", "active")) + "\" /></packageSources><disabledPackageSources><add key=\"exact\" value=\"true\" /></disabledPackageSources><packageSourceMapping><packageSource key=\"exact\"><package pattern=\"Feed.Disabled\" /></packageSource><packageSource key=\"active\"><package pattern=\"*\" /></packageSource></packageSourceMapping></configuration>",
            Encoding.UTF8);
        var disabledExactResult = Run("check", disabledExact.Project, "--config", disabledExact.Config!);
        AssertEqual(1, disabledExactResult.ExitCode, "disabled exact mapping exit code");
        AssertContains(disabledExactResult.Output, "FF003");
        AssertNotContains(disabledExactResult.Output, "could not be reconciled");

        RunReadOnlyConfigurationCases();

        var inherited = CreateCase("inherited", ["Feed.Inherited"], [], ["one"]);
        var outsideConfig = Path.Combine(_root, "outside.config");
        File.Copy(inherited.Config!, outsideConfig);
        AssertProcess(0, "check", inherited.Project, "--config", outsideConfig);
        AssertProcess(1, "check", inherited.Project, "--config", outsideConfig, "--strict");
        AssertContains(Run("check", inherited.Project, "--config", outsideConfig).Output, "external inherited configuration");

        var insecure = CreateCase("insecure", ["Feed.Insecure"], [], ["one"], "http://user:secret@example.invalid/v3/index.json?token=private");
        var insecureOutput = Run("check", insecure.Project, "--config", insecure.Config!);
        AssertEqual(1, insecureOutput.ExitCode, "insecure source exit code");
        AssertContains(insecureOutput.Output, "FF006");
        AssertNotContains(insecureOutput.Output, "secret");
        AssertNotContains(insecureOutput.Output, "token=private");

        var paddedHttp = CreateCase("padded-http", ["Feed.Http"], [], ["http"], "  http://example.invalid/v3/index.json  ");
        var paddedHttpResult = Run("check", paddedHttp.Project, "--config", paddedHttp.Config!);
        AssertEqual(1, paddedHttpResult.ExitCode, "whitespace-padded HTTP source exit code");
        AssertContains(paddedHttpResult.Output, "FF006");

        var restoreCrossCheckRoot = Path.Combine(_root, "padded-http-restore");
        var restoreCrossCheckProjectDirectory = Path.Combine(restoreCrossCheckRoot, "project");
        Directory.CreateDirectory(restoreCrossCheckProjectDirectory);
        var restoreCrossCheckProject = Path.Combine(restoreCrossCheckProjectDirectory, "Project.csproj");
        File.WriteAllText(
            restoreCrossCheckProject,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"FeedFence.HttpCrossCheck\" Version=\"1.0.0\" /></ItemGroup></Project>",
            Encoding.UTF8);
        var restoreCrossCheckConfig = Path.Combine(restoreCrossCheckRoot, "NuGet.config");
        File.WriteAllText(
            restoreCrossCheckConfig,
            "<configuration><packageSources><clear /><add key=\"http\" value=\"  http://127.0.0.1:1/v3/index.json  \" /></packageSources></configuration>",
            Encoding.UTF8);
        var restoreCrossCheck = RunExternal(
            "dotnet",
            "restore",
            restoreCrossCheckProject,
            "--configfile",
            restoreCrossCheckConfig,
            "--no-cache",
            "--disable-parallel");
        AssertEqual(false, restoreCrossCheck.ExitCode == 0, "NuGet padded HTTP restore rejection");
        AssertContains(restoreCrossCheck.Output, "NU1302");

        var sensitiveSourceKey = "https://user:password@example.invalid/nuget/index.json?token=topsecret";
        var sensitiveSource = CreateCase("sensitive-source-key", ["Feed.Secret"], [], [sensitiveSourceKey, "safe"], "https://feed.example.invalid/index.json");
        foreach (var format in new[] { "text", "json", "sarif" })
        {
            var sensitiveResult = Run("check", sensitiveSource.Project, "--config", sensitiveSource.Config!, "--format", format);
            AssertContains(sensitiveResult.Output, "source-");
            AssertNotContains(sensitiveResult.Output, "https://user:password@example.invalid/nuget/index.json?token=topsecret");
            AssertNotContains(sensitiveResult.Output, "user");
            AssertNotContains(sensitiveResult.Output, "password");
            AssertNotContains(sensitiveResult.Output, "example.invalid");
            AssertNotContains(sensitiveResult.Output, "topsecret");
        }

        var protectedCase = CreateCase("protected", ["Company.Internal"], [], ["public"]);
        var policy = Path.Combine(protectedCase.Root, "feedfence.json");
        File.WriteAllText(policy, "{\"version\":1,\"sourceTrust\":{\"public\":\"public\"},\"privatePackages\":[\"Company.*\"]}", Encoding.UTF8);
        AssertProcess(1, "check", protectedCase.Project, "--config", protectedCase.Config!, "--policy", policy);
        var exceptionPolicy = Path.Combine(protectedCase.Root, "exception.json");
        File.WriteAllText(exceptionPolicy, "{\"version\":1,\"sourceTrust\":{\"public\":\"public\"},\"privatePackages\":[\"Company.*\"],\"exceptions\":[{\"code\":\"FF007\",\"packagePattern\":\"Company.Internal\",\"sourceKey\":\"public\",\"reason\":\"synthetic test exception\"}]}", Encoding.UTF8);
        AssertProcess(0, "check", protectedCase.Project, "--config", protectedCase.Config!, "--policy", exceptionPolicy);

        var malformedPolicy = Path.Combine(_root, "malformed-policy.json");
        File.WriteAllText(malformedPolicy, "{\"exceptions\":[{\"code\":\"FF005\"}]}", Encoding.UTF8);
        var malformed = Run("check", specificity.Project, "--config", specificity.Config!, "--policy", malformedPolicy);
        AssertEqual(2, malformed.ExitCode, "malformed policy exit code");
        AssertEqual(string.Empty, malformed.StandardOutput, "malformed policy stdout");
        AssertContains(malformed.StandardError, "Analysis error");
        AssertNotContains(malformed.Output, specificity.Root);

        var missing = Path.Combine(_root, "missing");
        Directory.CreateDirectory(missing);
        File.WriteAllText(Path.Combine(missing, "Project.csproj"), "<Project />", Encoding.UTF8);
        var missingResult = Run("check", Path.Combine(missing, "Project.csproj"), "--config", specificity.Config!);
        AssertEqual(2, missingResult.ExitCode, "missing restore artifact exit code");
        AssertEqual(string.Empty, missingResult.StandardOutput, "missing artifact stdout");
        AssertContains(missingResult.StandardError, "Analysis error");
        AssertContains(missingResult.Output, "restore artifacts are missing");

        var authConfig = Path.Combine(_root, "authenticated.config");
        File.WriteAllText(authConfig, "<configuration><packageSources><clear /><add key=\"private\" value=\"https://user:password@example.invalid/index.json?token=topsecret\" /></packageSources></configuration>", Encoding.UTF8);
        var redacted = Run("check", specificity.Project, "--config", authConfig);
        AssertNotContains(redacted.Output, "password");
        AssertNotContains(redacted.Output, "topsecret");
        AssertNotContains(redacted.Output, Environment.UserName);
        AssertNotContains(redacted.Output, _root);

        var disagreement = Path.Combine(Path.GetDirectoryName(specificity.Project)!, "packages.lock.json");
        File.WriteAllText(disagreement, "{\"version\":2,\"dependencies\":{\"net8.0\":{\"Different.Package\":{\"resolved\":\"1.0.0\"}}}}", Encoding.UTF8);
        var disagreementResult = Run("check", specificity.Project, "--config", specificity.Config!);
        AssertEqual(2, disagreementResult.ExitCode, "assets/lock disagreement exit code");
        AssertContains(disagreementResult.Output, "project.assets.json and packages.lock.json disagree");

        var dtdConfig = Path.Combine(_root, "dtd.config");
        File.WriteAllText(dtdConfig, "<?xml version=\"1.0\"?><!DOCTYPE configuration [<!ENTITY secret SYSTEM \"file:///not-used\">]><configuration><packageSources><add key=\"one\" value=\"&secret;\" /></packageSources></configuration>", Encoding.UTF8);
        var dtdResult = Run("check", specificity.Project, "--config", dtdConfig);
        AssertEqual(2, dtdResult.ExitCode, "DTD configuration exit code");
        AssertNotContains(dtdResult.Output, "not-used");

        var incompleteAssets = CreateCase("incomplete-assets", ["Feed.Incomplete"], [], ["one"], coherentTargets: false);
        var incompleteAssetsResult = Run("check", incompleteAssets.Project, "--config", incompleteAssets.Config!);
        AssertEqual(2, incompleteAssetsResult.ExitCode, "incomplete assets graph exit code");
        AssertContains(incompleteAssetsResult.Output, "package library is not present in any target framework");

        var zeroSources = CreateCase("zero-sources", ["Feed.NoSource"], [], []);
        var zeroSourcesResult = Run("check", zeroSources.Project, "--config", zeroSources.Config!);
        AssertEqual(2, zeroSourcesResult.ExitCode, "zero-source nonempty graph exit code");
        AssertContains(zeroSourcesResult.Output, "no active package sources");

        var symlinkCase = CreateCase("symlink-config", ["Feed.Symlink"], [], ["one"]);
        var symlinkTarget = Path.Combine(_root, "symlink-target.config");
        File.Copy(symlinkCase.Config!, symlinkTarget);
        var symlinkConfig = Path.Combine(Path.GetDirectoryName(symlinkCase.Project)!, "NuGet.config");
        File.CreateSymbolicLink(symlinkConfig, symlinkTarget);
        var symlinkResult = Run("check", symlinkCase.Project, "--config", symlinkConfig, "--format", "json");
        AssertEqual(0, symlinkResult.ExitCode, "symlink provenance non-strict exit code");
        AssertContains(symlinkResult.Output, "external inherited configuration");
        using (var symlinkDocument = JsonDocument.Parse(symlinkResult.StandardOutput))
        {
            AssertEqual(false, symlinkDocument.RootElement.GetProperty("sources")[0].GetProperty("repositoryControlled").GetBoolean(), "symlink provenance repository control");
        }

        var highCardinalityPackages = Enumerable.Range(0, 50_001).Select(index => $"Feed.Package{index:00000}").ToArray();
        var highCardinality = CreateCase("high-cardinality", highCardinalityPackages, [], ["one"]);
        var highCardinalityResult = Run("check", highCardinality.Project, "--config", highCardinality.Config!);
        AssertEqual(2, highCardinalityResult.ExitCode, "high-cardinality assets exit code");
        AssertContains(highCardinalityResult.Output, "too many libraries");

        var nestedProject = CreateNestedProjectConfigCase();
        var nestedProjectResult = Run("check", nestedProject.Project);
        AssertEqual(1, nestedProjectResult.ExitCode, "nested project configuration scope exit code");
        AssertContains(nestedProjectResult.Output, "FF001");
        using (var nestedProjectJson = JsonDocument.Parse(Run("check", nestedProject.Project, "--format", "json").StandardOutput))
        {
            AssertEqual(
                true,
                nestedProjectJson.RootElement.GetProperty("sources").EnumerateArray().All(source => source.GetProperty("repositoryControlled").GetBoolean()),
                "nested project configuration provenance");
        }

        var nestedSolution = CreateNestedSolutionConfigCase();
        var nestedSolutionResult = Run("check", nestedSolution.Solution!);
        AssertEqual(1, nestedSolutionResult.ExitCode, "nested solution configuration scope exit code");
        AssertContains(nestedSolutionResult.Output, "FF001");

        var worktreePolicy = CreateWorktreePolicyCase();
        var worktreePolicyResult = Run("check", worktreePolicy.Project);
        AssertEqual(1, worktreePolicyResult.ExitCode, ".git file repository policy exit code");
        AssertContains(worktreePolicyResult.Output, "FF007");

        var truncatedGraph = CreateCase("truncated-graph", ["Company.Secret"], [], ["public"]);
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(truncatedGraph.Project)!, "obj", "project.assets.json"),
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["version"] = 3,
                ["targets"] = new Dictionary<string, object>
                {
                    ["net8.0"] = new Dictionary<string, object>
                    {
                        ["Company.Secret/1.0.0"] = new { type = "package" }
                    }
                },
                ["libraries"] = new Dictionary<string, object>(),
                ["projectFileDependencyGroups"] = new Dictionary<string, object>
                {
                    ["net8.0"] = new List<string> { "Company.Secret >= 1.0.0" }
                },
                ["project"] = new Dictionary<string, object>
                {
                    ["frameworks"] = CreateProjectFrameworks(CompanySecretDependencies)
                }
            }),
            Encoding.UTF8);
        var truncatedGraphResult = Run("check", truncatedGraph.Project, "--config", truncatedGraph.Config!);
        AssertEqual(2, truncatedGraphResult.ExitCode, "truncated assets graph exit code");
        AssertContains(truncatedGraphResult.Output, "has no library record");

        var omittedDeclaredDependency = CreateCase("omitted-declared-dependency", [], [], ["public"]);
        File.WriteAllText(
            omittedDeclaredDependency.Project,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Company.Secret\" Version=\"1.0.0\" /></ItemGroup></Project>",
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(omittedDeclaredDependency.Project)!, "obj", "project.assets.json"),
            "{\"version\":3,\"targets\":{\"net8.0\":{}},\"libraries\":{},\"projectFileDependencyGroups\":{\"net8.0\":[\"Company.Secret >= 1.0.0\"]},\"project\":{\"frameworks\":{\"net8.0\":{\"dependencies\":{\"Company.Secret\":{\"target\":\"Package\",\"version\":\"[1.0.0, )\"}}}}}}",
            Encoding.UTF8);
        var omittedDeclaredDependencyResult = Run("check", omittedDeclaredDependency.Project, "--config", omittedDeclaredDependency.Config!);
        AssertEqual(2, omittedDeclaredDependencyResult.ExitCode, "omitted declared dependency exit code");
        AssertContains(omittedDeclaredDependencyResult.Output, "declared dependency");

        foreach (var invalidTarget in new[]
        {
            (Name: "unknown", Value: "Mystery"),
            (Name: "mixed-unknown", Value: "Package, Mystery"),
            (Name: "none", Value: "None"),
            (Name: "numeric-zero", Value: "0"),
            (Name: "numeric", Value: "1"),
            (Name: "empty-segment", Value: "Package,,Project")
        })
        {
            var invalidDependencyTarget = CreateCase($"invalid-dependency-target-{invalidTarget.Name}", [], [], ["public"]);
            WriteDependencyTargetAssets(invalidDependencyTarget.Project, invalidTarget.Value, "project", "project");
            var invalidDependencyTargetResult = Run("check", invalidDependencyTarget.Project, "--config", invalidDependencyTarget.Config!);
            AssertEqual(2, invalidDependencyTargetResult.ExitCode, $"{invalidTarget.Name} dependency target exit code");
            AssertContains(invalidDependencyTargetResult.Output, "invalid project dependency target");
        }

        foreach (var targetValue in Enumerable.Range(1, 63).Where(value => (value & 1) != 0))
        {
            var targetText = FormatDependencyTarget(targetValue);
            var packageTarget = CreateCase($"package-dependency-target-{targetValue}", [], [], ["public"]);
            WriteDependencyTargetAssets(packageTarget.Project, targetText, "package", "package");
            var packageTargetResult = Run("check", packageTarget.Project, "--config", packageTarget.Config!);
            AssertEqual(0, packageTargetResult.ExitCode, $"package-bearing dependency target {targetText} exit code");
            AssertContains(packageTargetResult.Output, "1 resolved packages");
        }

        foreach (var targetValue in Enumerable.Range(1, 62).Where(value => (value & 1) == 0))
        {
            var targetText = FormatDependencyTarget(targetValue);
            var recordType = GetCompatibleNonPackageLibraryType(targetValue);
            var nonPackageTarget = CreateCase($"non-package-dependency-target-{targetValue}", [], [], ["public"]);
            WriteDependencyTargetAssets(nonPackageTarget.Project, targetText, recordType, recordType);
            var nonPackageTargetResult = Run("check", nonPackageTarget.Project, "--config", nonPackageTarget.Config!);
            AssertEqual(0, nonPackageTargetResult.ExitCode, $"non-package dependency target {targetText} exit code");
            AssertContains(nonPackageTargetResult.Output, "0 resolved packages");
        }

        var incompatibleNonPackageTarget = CreateCase("incompatible-non-package-dependency-target", [], [], ["public"]);
        WriteDependencyTargetAssets(incompatibleNonPackageTarget.Project, "Project", "assembly", "assembly");
        var incompatibleNonPackageTargetResult = Run("check", incompatibleNonPackageTarget.Project, "--config", incompatibleNonPackageTarget.Config!);
        AssertEqual(2, incompatibleNonPackageTargetResult.ExitCode, "incompatible non-package dependency target exit code");
        AssertContains(incompatibleNonPackageTargetResult.Output, "does not allow 'assembly'");

        foreach (var typeCase in new[]
        {
            (Name: "type-substitution", TargetType: "project", LibraryType: "project", Expected: "declared package dependency"),
            (Name: "target-package-library-project", TargetType: "package", LibraryType: "project", Expected: "target and library types disagree"),
            (Name: "target-project-library-package", TargetType: "project", LibraryType: "package", Expected: "target and library types disagree")
        })
        {
            var graphTypeCase = CreateCase(typeCase.Name, [], [], ["public"]);
            File.WriteAllText(
                graphTypeCase.Project,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Company.Secret\" Version=\"1.0.0\" /></ItemGroup></Project>",
                Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(Path.GetDirectoryName(graphTypeCase.Project)!, "obj", "project.assets.json"),
                JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["version"] = 3,
                    ["targets"] = new Dictionary<string, object>
                    {
                        ["net8.0"] = new Dictionary<string, object>
                        {
                            ["Company.Secret/1.0.0"] = new { type = typeCase.TargetType }
                        }
                    },
                    ["libraries"] = new Dictionary<string, object>
                    {
                        ["Company.Secret/1.0.0"] = new { type = typeCase.LibraryType }
                    },
                    ["projectFileDependencyGroups"] = new Dictionary<string, object>
                    {
                        ["net8.0"] = CompanySecretDependencyGroup
                    },
                    ["project"] = new Dictionary<string, object>
                    {
                        ["frameworks"] = CreateProjectFrameworks(CompanySecretDependencies)
                    }
                }),
                Encoding.UTF8);
            var graphTypeResult = Run("check", graphTypeCase.Project, "--config", graphTypeCase.Config!);
            AssertEqual(2, graphTypeResult.ExitCode, $"{typeCase.Name} graph exit code");
            AssertContains(graphTypeResult.Output, typeCase.Expected);
        }

        var projectReferenceControl = CreateCase("project-reference-control", [], [], ["public"]);
        File.WriteAllText(
            projectReferenceControl.Project,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"..\\Child\\Child.csproj\" /></ItemGroup></Project>",
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(projectReferenceControl.Project)!, "obj", "project.assets.json"),
            "{\"version\":3,\"targets\":{\"net8.0\":{\"Child/1.0.0\":{\"type\":\"project\"}}},\"libraries\":{\"Child/1.0.0\":{\"type\":\"project\",\"path\":\"../Child/Child.csproj\",\"msbuildProject\":\"../Child/Child.csproj\"}},\"projectFileDependencyGroups\":{\"net8.0\":[\"Child >= 1.0.0\"]},\"project\":{\"frameworks\":{\"net8.0\":{}}}}",
            Encoding.UTF8);
        var projectReferenceControlResult = Run("check", projectReferenceControl.Project, "--config", projectReferenceControl.Config!);
        AssertEqual(0, projectReferenceControlResult.ExitCode, "project-reference control exit code");
        AssertContains(projectReferenceControlResult.Output, "0 resolved packages");

        var missingDependencyGroups = CreateCase("missing-dependency-groups", ["Company.Secret"], [], ["public"]);
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(missingDependencyGroups.Project)!, "obj", "project.assets.json"),
            "{\"version\":3,\"targets\":{\"net8.0\":{\"Company.Secret/1.0.0\":{}}},\"libraries\":{\"Company.Secret/1.0.0\":{\"type\":\"package\"}}}",
            Encoding.UTF8);
        var missingDependencyGroupsResult = Run("check", missingDependencyGroups.Project, "--config", missingDependencyGroups.Config!);
        AssertEqual(2, missingDependencyGroupsResult.ExitCode, "missing dependency groups exit code");
        AssertContains(missingDependencyGroupsResult.Output, "project.assets.json is incomplete");

        var missingLibraryType = CreateCase("missing-library-type", ["Company.Secret"], [], ["public"]);
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(missingLibraryType.Project)!, "obj", "project.assets.json"),
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["version"] = 3,
                ["targets"] = new Dictionary<string, object>
                {
                    ["net8.0"] = new Dictionary<string, object>
                    {
                        ["Company.Secret/1.0.0"] = new { type = "package" }
                    }
                },
                ["libraries"] = new Dictionary<string, object>
                {
                    ["Company.Secret/1.0.0"] = new { sha512 = "synthetic" }
                },
                ["projectFileDependencyGroups"] = new Dictionary<string, object>
                {
                    ["net8.0"] = new List<string> { "Company.Secret >= 1.0.0" }
                },
                ["project"] = new Dictionary<string, object>
                {
                    ["frameworks"] = CreateProjectFrameworks(CompanySecretDependencies)
                }
            }),
            Encoding.UTF8);
        var missingLibraryTypeResult = Run("check", missingLibraryType.Project, "--config", missingLibraryType.Config!);
        AssertEqual(2, missingLibraryTypeResult.ExitCode, "missing library type exit code");
        AssertContains(missingLibraryTypeResult.Output, "library record 'Company.Secret/1.0.0' has invalid type data");

        var zeroPackage = CreateCase("zero-package", [], [], ["public"]);
        var zeroPackageResult = Run("check", zeroPackage.Project, "--config", zeroPackage.Config!);
        AssertEqual(0, zeroPackageResult.ExitCode, "legitimate zero-package graph exit code");

        var dottedSolutionFolder = CreateDottedSolutionFolderCase();
        var dottedSolutionFolderResult = Run("check", dottedSolutionFolder.Solution!);
        AssertEqual(0, dottedSolutionFolderResult.ExitCode, "dotted solution folder exit code");

        var unsupportedSolutionMember = CreateUnsupportedSolutionMemberCase();
        var unsupportedSolutionMemberResult = Run("check", unsupportedSolutionMember.Solution!);
        AssertEqual(2, unsupportedSolutionMemberResult.ExitCode, "unsupported real solution member exit code");
        AssertContains(unsupportedSolutionMemberResult.Output, "unsupported project member");

        var partialSolution = CreatePartialSolutionCase();
        var partialSolutionResult = Run("check", partialSolution.Solution!);
        AssertEqual(2, partialSolutionResult.ExitCode, "partial solution exit code");
        AssertContains(partialSolutionResult.Output, "declares a project member that could not be read");

        var mixedSolution = CreateMixedSolutionCase();
        var mixedSolutionResult = Run("check", mixedSolution.Solution!);
        AssertEqual(1, mixedSolutionResult.ExitCode, "mixed solution member exit code");
        AssertContains(mixedSolutionResult.Output, "FF002");

        var wrongTypedSelector = CreateCase("wrong-typed-selector", ["Company.Internal"],
            ["<packageSourceMapping><packageSource key=\"public\"><package pattern=\"Company.*\" /></packageSource></packageSourceMapping>"],
            ["public"]);
        var wrongTypedPolicy = Path.Combine(wrongTypedSelector.Root, "wrong-typed.json");
        File.WriteAllText(
            wrongTypedPolicy,
            "{\"version\":1,\"sourceTrust\":{\"public\":\"public\",\"private\":\"private\"},\"privatePackages\":[\"Company.*\"],\"exceptions\":[{\"code\":\"FF007\",\"packagePattern\":\"Company.*\",\"sourceKey\":17,\"reason\":\"invalid selector\"}]}",
            Encoding.UTF8);
        var wrongTypedSelectorResult = Run("check", wrongTypedSelector.Project, "--config", wrongTypedSelector.Config!, "--policy", wrongTypedPolicy);
        AssertEqual(2, wrongTypedSelectorResult.ExitCode, "wrong-typed selector exit code");
        AssertContains(wrongTypedSelectorResult.Output, "sourceKey");

        var conflictingSelectorPolicy = Path.Combine(wrongTypedSelector.Root, "conflicting-selector.json");
        File.WriteAllText(
            conflictingSelectorPolicy,
            "{\"version\":1,\"exceptions\":[{\"code\":\"FF007\",\"packagePattern\":\"Company.*\",\"pattern\":\"Company.*\",\"reason\":\"conflicting selectors\"}]}",
            Encoding.UTF8);
        var conflictingSelectorResult = Run("check", wrongTypedSelector.Project, "--config", wrongTypedSelector.Config!, "--policy", conflictingSelectorPolicy);
        AssertEqual(2, conflictingSelectorResult.ExitCode, "conflicting selector exit code");
        AssertContains(conflictingSelectorResult.Output, "conflicting package pattern selectors");

        var partiallyExcepted = CreateCase("partially-excepted", ["Company.Internal"],
            ["<packageSourceMapping><packageSource key=\"public\"><package pattern=\"Company.*\" /></packageSource><packageSource key=\"other\"><package pattern=\"Company.*\" /></packageSource></packageSourceMapping>"],
            ["public", "other"]);
        var partiallyExceptedPolicy = Path.Combine(partiallyExcepted.Root, "partial-exception.json");
        File.WriteAllText(
            partiallyExceptedPolicy,
            "{\"version\":1,\"sourceTrust\":{\"public\":\"public\",\"other\":\"public\"},\"privatePackages\":[\"Company.*\"],\"exceptions\":[{\"code\":\"FF002\",\"packagePattern\":\"Company.*\",\"reason\":\"accepted ambiguity\"},{\"code\":\"FF007\",\"packagePattern\":\"Company.*\",\"sourceKey\":\"public\",\"reason\":\"approved source\"}]}",
            Encoding.UTF8);
        var partiallyExceptedResult = Run("check", partiallyExcepted.Project, "--config", partiallyExcepted.Config!, "--policy", partiallyExceptedPolicy);
        AssertEqual(1, partiallyExceptedResult.ExitCode, "partially excepted multi-source exit code");
        AssertContains(partiallyExceptedResult.Output, "\"other\"");
        AssertNotContains(partiallyExceptedResult.Output, "source keys \"public\", \"other\"");
    }

    private TestCase CreateNestedProjectConfigCase()
    {
        var root = Path.Combine(_root, "nested-project-config");
        var projectDirectory = Path.Combine(root, "src", "nested");
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var project = Path.Combine(projectDirectory, "Project.csproj");
        WriteProjectAndAssets(project, ["Feed.Nested"]);
        WriteConfig(Path.Combine(root, "NuGet.config"), ["safe"]);
        WriteConfig(Path.Combine(projectDirectory, "NuGet.config"), ["one", "two"]);
        return new(root, project, null, null);
    }

    private TestCase CreateNestedSolutionConfigCase()
    {
        var root = Path.Combine(_root, "nested-solution-config");
        var solutionDirectory = Path.Combine(root, "solutions", "nested");
        Directory.CreateDirectory(solutionDirectory);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var project = Path.Combine(solutionDirectory, "Project.csproj");
        WriteProjectAndAssets(project, ["Feed.NestedSolution"]);
        var solution = Path.Combine(solutionDirectory, "Nested.sln");
        WriteSolution(solution, [("Project", "Project.csproj", "csproj")]);
        WriteConfig(Path.Combine(root, "NuGet.config"), ["safe"]);
        WriteConfig(Path.Combine(solutionDirectory, "NuGet.config"), ["one", "two"]);
        return new(root, project, solution, null);
    }

    private TestCase CreateWorktreePolicyCase()
    {
        var root = Path.Combine(_root, "worktree-policy");
        var projectDirectory = Path.Combine(root, "src", "nested");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: ../.git/worktrees/feedfence", Encoding.UTF8);
        var project = Path.Combine(projectDirectory, "Project.csproj");
        WriteProjectAndAssets(project, ["Company.Secret"]);
        WriteConfig(Path.Combine(root, "NuGet.config"), ["public"]);
        File.WriteAllText(
            Path.Combine(root, "feedfence.json"),
            "{\"version\":1,\"sourceTrust\":{\"public\":\"public\"},\"privatePackages\":[\"Company.*\"]}",
            Encoding.UTF8);
        return new(root, project, null, null);
    }

    private TestCase CreatePartialSolutionCase()
    {
        var root = Path.Combine(_root, "partial-solution");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var existing = Path.Combine(root, "Existing.csproj");
        WriteProjectAndAssets(existing, []);
        var solution = Path.Combine(root, "Partial.sln");
        WriteSolution(solution, [("Existing", "Existing.csproj", "csproj"), ("Missing", "Missing.csproj", "csproj")]);
        WriteConfig(Path.Combine(root, "NuGet.config"), ["one"]);
        return new(root, existing, solution, null);
    }

    private TestCase CreateMixedSolutionCase()
    {
        var root = Path.Combine(_root, "mixed-solution");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var csProject = Path.Combine(root, "Empty.csproj");
        var fsProject = Path.Combine(root, "Packages.fsproj");
        WriteProjectAndAssets(csProject, []);
        WriteProjectAndAssets(fsProject, ["Feed.Mixed"]);
        var solution = Path.Combine(root, "Mixed.sln");
        WriteSolution(solution, [("Empty", "Empty.csproj", "csproj"), ("Packages", "Packages.fsproj", "fsproj")]);
        WriteConfig(
            Path.Combine(root, "NuGet.config"),
            ["one", "two"],
            "<packageSourceMapping><packageSource key=\"one\"><package pattern=\"Feed.Mixed\" /></packageSource><packageSource key=\"two\"><package pattern=\"Feed.Mixed\" /></packageSource></packageSourceMapping>");
        return new(root, csProject, solution, null);
    }

    private TestCase CreateDottedSolutionFolderCase()
    {
        var root = Path.Combine(_root, "dotted-solution-folder");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var project = Path.Combine(root, "Project.csproj");
        WriteProjectAndAssets(project, ["Feed.DottedFolder"]);
        var solution = Path.Combine(root, "DottedFolder.sln");
        WriteSolution(solution, [("docs.v2", "docs.v2", "solutionfolder"), ("Project", "Project.csproj", "csproj")]);
        WriteConfig(Path.Combine(root, "NuGet.config"), ["one"]);
        return new(root, project, solution, null);
    }

    private TestCase CreateUnsupportedSolutionMemberCase()
    {
        var root = Path.Combine(_root, "unsupported-solution-member");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var project = Path.Combine(root, "Native.vcxproj");
        File.WriteAllText(project, "<Project />", Encoding.UTF8);
        var solution = Path.Combine(root, "Unsupported.sln");
        WriteSolution(solution, [("Native", "Native.vcxproj", "vcxproj")]);
        WriteConfig(Path.Combine(root, "NuGet.config"), ["one"]);
        return new(root, project, solution, null);
    }

    private static void WriteProjectAndAssets(string project, IReadOnlyList<string> packages)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(project)!, "obj"));
        File.WriteAllText(project, "<Project />", Encoding.UTF8);
        var libraries = packages.ToDictionary(package => package + "/1.0.0", package => (object)new { type = "package" });
        var targetLibraries = packages.ToDictionary(package => package + "/1.0.0", package => (object)new { type = "package" });
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(project)!, "obj", "project.assets.json"),
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["version"] = 3,
                ["targets"] = new Dictionary<string, object> { ["net8.0"] = targetLibraries },
                ["libraries"] = libraries,
                ["projectFileDependencyGroups"] = new Dictionary<string, object>
                {
                    ["net8.0"] = packages.Select(package => $"{package} >= 1.0.0").ToArray()
                },
                ["project"] = new Dictionary<string, object>
                {
                    ["frameworks"] = CreateProjectFrameworks(packages)
                }
            }),
            Encoding.UTF8);
    }

    private static void WriteConfig(string path, IReadOnlyList<string> sourceKeys, string mapping = "")
    {
        var root = Path.GetDirectoryName(path)!;
        var sources = sourceKeys.Select(key => $"<add key=\"{SecurityElement.Escape(key)}\" value=\"{SecurityElement.Escape(Path.Combine(root, "feeds", key))}\" />");
        File.WriteAllText(path, $"<configuration><packageSources><clear />{string.Join(string.Empty, sources)}</packageSources>{mapping}</configuration>", Encoding.UTF8);
    }

    private static void WriteSolution(string path, IReadOnlyList<(string Name, string RelativePath, string Extension)> projects)
    {
        var lines = new List<string> { "Microsoft Visual Studio Solution File, Format Version 12.00" };
        var index = 0;
        foreach (var project in projects)
        {
            index++;
            var projectType = project.Extension switch
            {
                "fsproj" => "F2A71F9B-5D33-465A-A702-920D77279786",
                "solutionfolder" => "2150E333-8FDC-42A3-9474-1A3956D46DE8",
                "vcxproj" => "BC8A1FFA-BEE3-4634-8014-F334798102B3",
                _ => "FAE04EC0-301F-11D3-BF4B-00C04F79EFBC"
            };
            var relativePath = project.RelativePath.Replace('/', '\\');
            lines.Add($"Project(\"{{{projectType}}}\") = \"{project.Name}\", \"{relativePath}\", \"{{00000000-0000-0000-0000-{index:000000000000}}}\"");
            lines.Add("EndProject");
        }

        File.WriteAllLines(path, lines, Encoding.UTF8);
    }

    private TestCase CreateCase(
        string name,
        IReadOnlyList<string> packages,
        IReadOnlyList<string> mappings,
        IReadOnlyList<string> sourceKeys,
        string? firstSource = null,
        bool coherentTargets = true)
    {
        var root = Path.Combine(_root, name);
        var projectDirectory = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "obj"));
        var project = Path.Combine(projectDirectory, "Project.csproj");
        File.WriteAllText(project, "<Project />", Encoding.UTF8);
        var libraries = packages.ToDictionary(package => package + "/1.0.0", package => (object)new { type = "package" });
        var targetLibraries = packages.ToDictionary(package => package + "/1.0.0", package => (object)new { type = "package" });
        var assets = new Dictionary<string, object>
        {
            ["version"] = 3,
            ["targets"] = coherentTargets
                ? new Dictionary<string, object> { ["net8.0"] = targetLibraries }
                : new Dictionary<string, object> { ["net8.0"] = new Dictionary<string, object>() },
            ["libraries"] = libraries,
            ["projectFileDependencyGroups"] = new Dictionary<string, object>
            {
                ["net8.0"] = coherentTargets
                    ? packages.Select(package => $"{package} >= 1.0.0").ToArray()
                    : Array.Empty<string>()
            },
            ["project"] = new Dictionary<string, object>
            {
                ["frameworks"] = CreateProjectFrameworks(coherentTargets ? packages : [])
            }
        };
        File.WriteAllText(Path.Combine(projectDirectory, "obj", "project.assets.json"), JsonSerializer.Serialize(assets), Encoding.UTF8);

        var sourceElements = sourceKeys.Select((key, index) =>
        {
            var value = index == 0 && firstSource is not null ? firstSource : Path.Combine(root, "feeds", key);
            return $"<add key=\"{SecurityElement.Escape(key)}\" value=\"{SecurityElement.Escape(value)}\" />";
        });
        var mappingText = string.Join(string.Empty, mappings);
        var config = Path.Combine(root, "NuGet.config");
        File.WriteAllText(config, $"<configuration><packageSources><clear />{string.Join(string.Empty, sourceElements)}</packageSources>{mappingText}</configuration>", Encoding.UTF8);
        return new(root, project, null, config);
    }

    private static Dictionary<string, object> CreateProjectFrameworks(IReadOnlyList<string> packageDependencies)
    {
        if (packageDependencies.Count == 0)
        {
            return new Dictionary<string, object> { ["net8.0"] = new Dictionary<string, object>() };
        }

        return new Dictionary<string, object>
        {
            ["net8.0"] = new Dictionary<string, object>
            {
                ["dependencies"] = packageDependencies.ToDictionary(
                    package => package,
                    package => (object)new { target = "Package", version = "[1.0.0, )" })
            }
        };
    }

    private static void WriteDependencyTargetAssets(string projectPath, string dependencyTarget, string targetType, string libraryType)
    {
        File.WriteAllText(
            projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Company.Secret\" Version=\"1.0.0\" /></ItemGroup></Project>",
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(projectPath)!, "obj", "project.assets.json"),
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["version"] = 3,
                ["targets"] = new Dictionary<string, object>
                {
                    ["net8.0"] = new Dictionary<string, object>
                    {
                        ["Company.Secret/1.0.0"] = new { type = targetType }
                    }
                },
                ["libraries"] = new Dictionary<string, object>
                {
                    ["Company.Secret/1.0.0"] = new { type = libraryType }
                },
                ["projectFileDependencyGroups"] = new Dictionary<string, object>
                {
                    ["net8.0"] = CompanySecretDependencyGroup
                },
                ["project"] = new Dictionary<string, object>
                {
                    ["frameworks"] = new Dictionary<string, object>
                    {
                        ["net8.0"] = new Dictionary<string, object>
                        {
                            ["dependencies"] = new Dictionary<string, object>
                            {
                                ["Company.Secret"] = new { target = dependencyTarget, version = "[1.0.0, )" }
                            }
                        }
                    }
                }
            }),
            Encoding.UTF8);
    }

    private static string FormatDependencyTarget(int value)
    {
        if (value == 63)
        {
            return "All";
        }

        var names = new List<string>();
        if ((value & 7) == 7)
        {
            names.Add("PackageProjectExternal");
        }
        else
        {
            if ((value & 1) != 0) names.Add("Package");
            if ((value & 2) != 0) names.Add("Project");
            if ((value & 4) != 0) names.Add("ExternalProject");
        }

        if ((value & 8) != 0) names.Add("Assembly");
        if ((value & 16) != 0) names.Add("Reference");
        if ((value & 32) != 0) names.Add("WinMD");
        return string.Join(", ", names);
    }

    private static string GetCompatibleNonPackageLibraryType(int value)
    {
        if ((value & 2) != 0) return "project";
        if ((value & 4) != 0) return "externalProject";
        if ((value & 8) != 0) return "assembly";
        if ((value & 16) != 0) return "reference";
        if ((value & 32) != 0) return "winmd";
        throw new ArgumentOutOfRangeException(nameof(value));
    }

    private void RunReadOnlyConfigurationCases()
    {
        var isolatedEnvironment = CreateIsolatedNuGetEnvironment();
        var userConfigDirectory = Path.Combine(isolatedEnvironment["APPDATA"]!, "NuGet");
        var beforeHierarchy = Snapshot(userConfigDirectory);

        var hierarchy = CreateCase("read-only-hierarchy", ["Feed.ReadOnly"], [], ["one"]);
        var hierarchyResult = Run(isolatedEnvironment, "check", hierarchy.Project);
        AssertEqual(0, hierarchyResult.ExitCode, "hierarchy read-only exit code");
        AssertEqual(beforeHierarchy, Snapshot(userConfigDirectory), "hierarchy user config filesystem snapshot");
        Console.WriteLine($"Read-only config case: hierarchy; userConfigCreated={(File.Exists(Path.Combine(userConfigDirectory, "NuGet.Config")) ? "yes" : "no")}; snapshot=unchanged");

        var explicitCase = CreateCase("read-only-explicit", ["Feed.ReadOnlyExplicit"], [], ["one"]);
        var beforeExplicit = Snapshot(userConfigDirectory);
        var explicitResult = Run(isolatedEnvironment, "check", explicitCase.Project, "--config", explicitCase.Config!);
        AssertEqual(0, explicitResult.ExitCode, "explicit read-only exit code");
        AssertEqual(beforeExplicit, Snapshot(userConfigDirectory), "explicit user config filesystem snapshot");
        Console.WriteLine($"Read-only config case: explicit --config; userConfigCreated={(File.Exists(Path.Combine(userConfigDirectory, "NuGet.Config")) ? "yes" : "no")}; snapshot=unchanged");

        foreach (var budget in new[] { "size", "depth", "elements" })
        {
            var hierarchyBudget = CreateCase($"hierarchy-budget-{budget}", [], [], []);
            WriteBudgetConfig(hierarchyBudget.Config!, budget);
            var beforeBudget = Snapshot(userConfigDirectory);
            var hierarchyBudgetResult = Run(isolatedEnvironment, "check", hierarchyBudget.Project);
            AssertEqual(2, hierarchyBudgetResult.ExitCode, $"hierarchy {budget} budget exit code");
            AssertContains(hierarchyBudgetResult.Output, "configuration file");
            AssertEqual(beforeBudget, Snapshot(userConfigDirectory), $"hierarchy {budget} user config filesystem snapshot");

            var explicitBudget = CreateCase($"explicit-budget-{budget}", [], [], []);
            WriteBudgetConfig(explicitBudget.Config!, budget);
            var explicitBudgetResult = Run(isolatedEnvironment, "check", explicitBudget.Project, "--config", explicitBudget.Config!);
            AssertEqual(2, explicitBudgetResult.ExitCode, $"explicit {budget} budget exit code");
            AssertContains(explicitBudgetResult.Output, "configuration file");
            AssertEqual(beforeBudget, Snapshot(userConfigDirectory), $"explicit {budget} user config filesystem snapshot");
            Console.WriteLine($"Config budget case: {budget}; hierarchy=exit {hierarchyBudgetResult.ExitCode}; explicit=exit {explicitBudgetResult.ExitCode}; userConfigSnapshot=unchanged");
        }
    }

    private Dictionary<string, string?> CreateIsolatedNuGetEnvironment()
    {
        var appData = Path.Combine(_root, "isolated-appdata");
        var programFiles = Path.Combine(_root, "isolated-program-files");
        var dotnetHome = Path.Combine(_root, "isolated-dotnet-home");
        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(programFiles);
        Directory.CreateDirectory(dotnetHome);
        return new Dictionary<string, string?>
        {
            ["APPDATA"] = appData,
            ["PROGRAMFILES(X86)"] = programFiles,
            ["PROGRAMFILES"] = programFiles,
            ["DOTNET_CLI_HOME"] = dotnetHome,
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["KEELMATRIX_NO_TELEMETRY"] = "1"
        };
    }

    private static string Snapshot(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return "<absent>";
        }

        return string.Join(
            "\n",
            Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
                .Select(path =>
                {
                    var relative = Path.GetRelativePath(directory, path);
                    return File.Exists(path)
                        ? $"file:{relative}:{new FileInfo(path).Length}:{File.GetLastWriteTimeUtc(path).Ticks}"
                        : $"directory:{relative}";
                })
                .Order(StringComparer.Ordinal));
    }

    private static void WriteBudgetConfig(string path, string budget)
    {
        var contents = budget switch
        {
            "size" => "<configuration>" + new string('x', 2 * 1024 * 1024) + "</configuration>",
            "depth" => "<configuration>" + string.Concat(Enumerable.Repeat("<section>", 65)) + "value" + string.Concat(Enumerable.Repeat("</section>", 65)) + "</configuration>",
            "elements" => "<configuration>" + string.Concat(Enumerable.Repeat("<section />", 100_001)) + "</configuration>",
            _ => throw new ArgumentOutOfRangeException(nameof(budget))
        };
        File.WriteAllText(path, contents, Encoding.UTF8);
    }

    private ProcessResult Run(params string[] args) => Run(new Dictionary<string, string?>(), args);

    private ProcessResult Run(IReadOnlyDictionary<string, string?> environment, params string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["KEELMATRIX_NO_TELEMETRY"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        foreach (var entry in environment)
        {
            if (entry.Value is null)
            {
                startInfo.Environment.Remove(entry.Key);
            }
            else
            {
                startInfo.Environment[entry.Key] = entry.Value;
            }
        }
        startInfo.ArgumentList.Add(_tool);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("could not start the FeedFence process.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return new(process.ExitCode, output.Result, error.Result);
    }

    private ProcessResult RunExternal(string fileName, params string[] args)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["KEELMATRIX_NO_TELEMETRY"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["NUGET_PACKAGES"] = Path.Combine(_root, "restore-cross-check-packages");
        startInfo.Environment["DOTNET_CLI_HOME"] = Path.Combine(_root, "restore-cross-check-home");
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"could not start {fileName}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return new(process.ExitCode, output.Result, error.Result);
    }

    private void AssertProcess(int expectedExitCode, params string[] args)
    {
        var result = Run(args);
        if (result.ExitCode != expectedExitCode)
        {
            throw new InvalidOperationException($"{string.Join(' ', args)}: expected '{expectedExitCode}', actual '{result.ExitCode}'. stderr: {result.StandardError.Trim()} stdout: {result.StandardOutput.Trim()}");
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{name}: expected '{expected}', actual '{actual}'.");
        }
    }

    private static void AssertContains(string value, string expected)
    {
        if (!value.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"expected output to contain '{expected}'.");
        }
    }

    private static void AssertNotContains(string value, string unexpected)
    {
        if (!string.IsNullOrEmpty(unexpected) && value.Contains(unexpected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"expected output not to contain '{unexpected}'.");
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }
}

internal sealed record TestCase(string Root, string Project, string? Solution, string? Config);
internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string Output => StandardOutput + StandardError;
}
