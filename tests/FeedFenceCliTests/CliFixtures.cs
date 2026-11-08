using System.Security;
using System.Text;
using System.Text.Json;

namespace KeelMatrix.FeedFence.CliTests;

internal sealed partial class Fixture : IDisposable
{
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
        WriteEvaluatedEvidence(project, packages);
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

    private static void WritePackageProject(string project, IReadOnlyList<string> packages)
    {
        WriteEvaluatedEvidence(project, packages);
        var references = string.Join(
            string.Empty,
            packages.Select(package => $"<PackageReference Include=\"{SecurityElement.Escape(package)}\" Version=\"1.0.0\" />"));
        File.WriteAllText(
            project,
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup>{references}</ItemGroup></Project>",
            Encoding.UTF8);
    }

    private TestCase CreateSlnxCase()
    {
        var root = Path.Combine(_root, "slnx-case");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var project = Path.Combine(root, "Project.csproj");
        WriteProjectAndAssets(project, []);
        var solution = Path.Combine(root, "Solution.slnx");
        File.WriteAllText(solution, "<?xml version=\"1.0\" encoding=\"utf-8\"?><Solution><Project Path=\"Project.csproj\" /></Solution>", Encoding.UTF8);
        var config = Path.Combine(root, "NuGet.config");
        WriteConfig(config, ["public"]);
        return new(root, project, solution, config);
    }

    private TestCase CreateCaseOnlySolution()
    {
        var root = Path.Combine(_root, "case-only-solution");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var emptyProject = Path.Combine(root, "member", "Project.csproj");
        var violatingProject = Path.Combine(root, "MEMBER", "Project.csproj");
        WriteProjectAndAssets(emptyProject, []);
        WriteProjectAndAssets(violatingProject, ["Feed.CaseOnly"]);
        var solution = Path.Combine(root, "CaseOnly.sln");
        WriteSolution(solution, [("Empty", "member/Project.csproj", "csproj"), ("Violating", "MEMBER/Project.csproj", "csproj")]);
        var config = Path.Combine(root, "NuGet.config");
        WriteConfig(config, ["one", "two"]);
        return new(root, emptyProject, solution, config);
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
        WriteEvaluatedEvidence(project, coherentTargets ? packages : []);

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

    private static void WriteEvaluatedEvidence(string project, IReadOnlyList<string> packages)
    {
        var frameworks = CreateProjectFrameworks(packages);
        var metadata = new
        {
            frameworks,
            restore = new
            {
                projectStyle = "PackageReference",
                projectName = Path.GetFileNameWithoutExtension(project),
                originalTargetFrameworks = new[] { "net8.0" },
                frameworks = new Dictionary<string, object> { ["net8.0"] = new { projectReferences = new Dictionary<string, object>() } }
            }
        };
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(project)!, "obj", Path.GetFileName(project) + ".nuget.dgspec.json"),
            JsonSerializer.Serialize(new { format = 1, restore = new Dictionary<string, object> { [project] = new { } }, projects = new Dictionary<string, object> { [project] = metadata } }), Encoding.UTF8);
    }
}
