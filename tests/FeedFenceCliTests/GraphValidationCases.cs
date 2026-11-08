using System.Text;
using System.Text.Json;

namespace KeelMatrix.FeedFence.CliTests;

internal sealed partial class Fixture : IDisposable
{
    private void RunRestoreGraphCompletenessCases()
    {
        var emptyGraph = CreateCase("declared-package-empty-graph", [], [], ["public"]);
        WritePackageProject(emptyGraph.Project, ["Company.Secret"]);
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(emptyGraph.Project)!, "obj", "project.assets.json"),
            "{\"version\":3,\"targets\":{\"net8.0\":{}},\"libraries\":{},\"projectFileDependencyGroups\":{\"net8.0\":[]},\"project\":{\"frameworks\":{\"net8.0\":{}}}}",
            Encoding.UTF8);
        AssertAnalysisFailure(emptyGraph.Project, emptyGraph.Config!, "empty graph with declared package");

        var missingVersion = CreateCase("missing-assets-schema-version", ["Feed.Schema"], [], ["public"]);
        var missingVersionAssets = File.ReadAllText(Path.Combine(Path.GetDirectoryName(missingVersion.Project)!, "obj", "project.assets.json"));
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(missingVersion.Project)!, "obj", "project.assets.json"),
            missingVersionAssets.Replace("{\"version\":3,", "{", StringComparison.Ordinal),
            Encoding.UTF8);
        AssertAnalysisFailure(missingVersion.Project, missingVersion.Config!, "missing assets schema version");

        var lockProject = CreateCase("lock-project-type-laundering", [], [], ["public"]);
        WritePackageProject(lockProject.Project, ["Company.Secret"]);
        File.Delete(Path.Combine(Path.GetDirectoryName(lockProject.Project)!, "obj", "project.assets.json"));
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(lockProject.Project)!, "packages.lock.json"),
            "{\"version\":2,\"dependencies\":{\"net8.0\":{\"Company.Secret\":{\"type\":\"Project\"}}}}",
            Encoding.UTF8);
        AssertAnalysisFailure(lockProject.Project, lockProject.Config!, "lock project type laundering");

        var unknownLock = CreateCase("lock-unknown-type", [], [], ["public"]);
        File.Delete(Path.Combine(Path.GetDirectoryName(unknownLock.Project)!, "obj", "project.assets.json"));
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(unknownLock.Project)!, "packages.lock.json"),
            "{\"version\":2,\"dependencies\":{\"net8.0\":{\"Mystery.Package\":{\"type\":\"Mystery\",\"resolved\":\"1.0.0\"}}}}",
            Encoding.UTF8);
        AssertAnalysisFailure(unknownLock.Project, unknownLock.Config!, "unknown lock type");

        var lockControls = CreateCase("lock-type-controls", [], [], ["public"]);
        WritePackageProject(lockControls.Project, ["Direct.Package"]);
        File.Delete(Path.Combine(Path.GetDirectoryName(lockControls.Project)!, "obj", "project.assets.json"));
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(lockControls.Project)!, "packages.lock.json"),
            "{\"version\":2,\"dependencies\":{\"net8.0\":{\"Direct.Package\":{\"type\":\"Direct\",\"resolved\":\"1.0.0\"},\"Transitive.Package\":{\"type\":\"Transitive\",\"resolved\":\"1.0.0\"},\"Central.Package\":{\"type\":\"CentralTransitive\",\"resolved\":\"1.0.0\"},\"Child.Project\":{\"type\":\"Project\"}}}}",
            Encoding.UTF8);
        var lockControlResult = Run("check", lockControls.Project, "--config", lockControls.Config!);
        AssertEqual(0, lockControlResult.ExitCode, "recognized lock entry types control");
        AssertContains(lockControlResult.Output, "3 resolved packages");
    }

    private void AssertAnalysisFailure(string project, string config, string label)
    {
        var result = Run("check", project, "--config", config);
        AssertEqual(2, result.ExitCode, label + " exit code");
        AssertEqual(string.Empty, result.StandardOutput, label + " stdout");
    }

    private static void WriteDependencyTargetAssets(string projectPath, string dependencyTarget, string targetType, string libraryType)
    {
        var declaresPackage = dependencyTarget.Split(',', StringSplitOptions.TrimEntries)
            .Any(value => value is "Package" or "PackageProjectExternal" or "All");
        File.WriteAllText(
            projectPath,
            declaresPackage
                ? "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Company.Secret\" Version=\"1.0.0\" /></ItemGroup></Project>"
                : "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>",
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

    private void RunInputFailureCases(TestCase specificity)
    {
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
        var disagreeingPackages = new[] { "Feed.Exact", "Feed.Prefix.Item", "Feed.Wildcard", "Different.Package" };
        File.WriteAllText(disagreement, JsonSerializer.Serialize(new { version = 2, dependencies = new Dictionary<string, object> { ["net8.0"] = disagreeingPackages.ToDictionary(id => id, id => new { type = "Direct", resolved = "1.0.0" }) } }), Encoding.UTF8);
        var disagreementResult = Run("check", specificity.Project, "--config", specificity.Config!);
        AssertEqual(2, disagreementResult.ExitCode, "assets/lock disagreement exit code");
        AssertContains(disagreementResult.Output, "project.assets.json and packages.lock.json disagree");

        var dtdConfig = Path.Combine(_root, "dtd.config");
        File.WriteAllText(dtdConfig, "<?xml version=\"1.0\"?><!DOCTYPE configuration [<!ENTITY secret SYSTEM \"file:///not-used\">]><configuration><packageSources><add key=\"one\" value=\"&secret;\" /></packageSources></configuration>", Encoding.UTF8);
        var dtdResult = Run("check", specificity.Project, "--config", dtdConfig);
        AssertEqual(2, dtdResult.ExitCode, "DTD configuration exit code");
        AssertNotContains(dtdResult.Output, "not-used");
    }

    private void RunBasicGraphFailureCases()
    {
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
    }

    private void RunDependencyTargetCases()
    {
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
        AssertContains(missingLibraryTypeResult.Output, "library record 'Company.Secret' has invalid type data");

        var zeroPackage = CreateCase("zero-package", [], [], ["public"]);
        var zeroPackageResult = Run("check", zeroPackage.Project, "--config", zeroPackage.Config!);
        AssertEqual(0, zeroPackageResult.ExitCode, "legitimate zero-package graph exit code");
    }
}
