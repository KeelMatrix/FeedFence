using System.Text;

namespace KeelMatrix.FeedFence.CliTests;

internal sealed partial class Fixture : IDisposable
{
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

    private void RunSourceSecurityCases()
    {
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
        foreach (var format in ReportFormats)
        {
            var sensitiveResult = Run("check", sensitiveSource.Project, "--config", sensitiveSource.Config!, "--format", format);
            AssertContains(sensitiveResult.Output, "source-");
            AssertNotContains(sensitiveResult.Output, "https://user:password@example.invalid/nuget/index.json?token=topsecret");
            AssertNotContains(sensitiveResult.Output, "user");
            AssertNotContains(sensitiveResult.Output, "password");
            AssertNotContains(sensitiveResult.Output, "example.invalid");
            AssertNotContains(sensitiveResult.Output, "topsecret");
        }
    }
}
