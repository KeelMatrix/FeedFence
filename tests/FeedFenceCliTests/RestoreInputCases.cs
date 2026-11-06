using System.Diagnostics;
using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NuGet.Configuration;

namespace KeelMatrix.FeedFence.CliTests;

// These cases start from real SDK restore output and run through either the shipping
// DLL or the installed command. Fixture restores use only synthetic local packages.
internal sealed class RestoreInputCases : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "feedfence-restore-inputs-" + Guid.NewGuid().ToString("N"));
    private readonly string _tool;
    private readonly string _feed;
    private readonly Dictionary<string, string> _environment;
    private readonly string _userConfig;
    private readonly string _machineBase;
    private readonly List<string> _directoryLinks = [];
    private int _assertions;

    public RestoreInputCases(string tool)
    {
        _tool = Path.GetFullPath(tool);
        _feed = Path.Combine(_root, "feed");
        Directory.CreateDirectory(_feed);
        File.WriteAllText(Path.Combine(_root, "global.json"), """{"sdk":{"version":"8.0.425","rollForward":"latestPatch","allowPrerelease":false}}""");
        var home = Path.Combine(_root, "home");
        var appData = Path.Combine(_root, "appdata");
        _machineBase = OperatingSystem.IsWindows() ? Path.Combine(_root, "machine", "NuGet") : Path.Combine(_root, "common", "NuGet");
        _userConfig = OperatingSystem.IsWindows() ? Path.Combine(appData, "NuGet", "NuGet.Config") : Path.Combine(home, ".nuget", "NuGet", "NuGet.Config");
        Directory.CreateDirectory(Path.GetDirectoryName(_userConfig)!);
        Directory.CreateDirectory(_machineBase);
        File.WriteAllText(_userConfig, "<configuration/>");
        _environment = new(StringComparer.OrdinalIgnoreCase)
        {
            ["DOTNET_CLI_HOME"] = home,
            ["APPDATA"] = appData,
            ["PROGRAMFILES(X86)"] = Path.Combine(_root, "machine"),
            ["PROGRAMFILES"] = Path.Combine(_root, "machine"),
            ["NUGET_COMMON_APPLICATION_DATA"] = Path.Combine(_root, "common"),
            ["NUGET_PACKAGES"] = Path.Combine(_root, "packages"),
            ["NUGET_HTTP_CACHE_PATH"] = Path.Combine(_root, "http-cache"),
            ["NUGET_PLUGINS_CACHE_PATH"] = Path.Combine(_root, "plugins-cache"),
            ["NUGET_SCRATCH"] = Path.Combine(_root, "nuget-scratch"),
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["KEELMATRIX_NO_TELEMETRY"] = "1"
        };
        Package("Company.Secret");
        Package("Public.Entry", "Company.Secret");
        Package("Other.Dependency");
    }

    public void RunAll()
    {
        GraphClosure();
        EvaluatedInputs();
        SolutionMembers();
        ConfigInputs();
        PhysicalProvenance();
        MappingWinners();
        SourceSecurity();
        Console.WriteLine($"PASS: real restore/input families ({_assertions} assertions), command={Path.GetFileName(_tool)}");
    }

    private void GraphClosure()
    {
        var fixture = Project("closure", """<ItemGroup><PackageReference Include="Public.Entry" Version="1.0.0"/></ItemGroup>""");
        Pass(fixture, 2);
        var caseVariant = Path.Combine(fixture.Root, "PROJECT.CSPROJ");
        if (File.Exists(caseVariant)) Pass(fixture with { Path = caseVariant }, 2);
        var original = SaveRestore(fixture);
        foreach (var mode in new[] { "assets", "lock", "combined" })
        {
            Reset(original);
            OmitPackage(fixture, "Company.Secret", mode);
            Failure(fixture, mode + " transitive omission");
        }
        foreach (var field in new[] { "targets", "projectFileDependencyGroups", "project.frameworks", "all" })
        {
            Reset(original);
            var assets = Read(fixture.Assets);
            if (field is "targets" or "all") assets["targets"] = new JsonObject();
            if (field is "projectFileDependencyGroups" or "all") assets["projectFileDependencyGroups"] = new JsonObject();
            if (field is "project.frameworks" or "all") assets["project"]!["frameworks"] = new JsonObject();
            if (field == "all") assets["libraries"] = new JsonObject();
            Write(fixture.Assets, assets);
            Failure(fixture, "collapsed " + field);
        }
        Reset(original);
        var truncatedLock = Read(fixture.Lock);
        truncatedLock["dependencies"] = new JsonObject();
        Write(fixture.Lock, truncatedLock);
        File.Delete(fixture.Assets);
        Failure(fixture, "collapsed lock frameworks");
        Reset(original);
        // Keep artifact declarations mutually consistent while omitting a direct root.
        var direct = Read(fixture.Assets);
        direct["targets"]!["net8.0"]!.AsObject().Clear();
        direct["libraries"]!.AsObject().Clear();
        direct["projectFileDependencyGroups"]!["net8.0"] = new JsonArray();
        direct["project"]!["frameworks"]!["net8.0"]!["dependencies"] = new JsonObject();
        Write(fixture.Assets, direct);
        File.Delete(fixture.Lock);
        Failure(fixture, "omitted direct evaluated dependency");
        Reset(original);
        File.Delete(fixture.Evidence);
        Failure(fixture, "missing evaluated input evidence");
        Reset(original);
        foreach (var version in new[] { 1, 2 })
        {
            var lockFile = Read(fixture.Lock);
            lockFile["version"] = version;
            Write(fixture.Lock, lockFile);
            Pass(fixture, 2);
        }

        var zero = Project("zero", "");
        Pass(zero, 0);
        var zeroOriginal = SaveRestore(zero);
        foreach (var mode in new[] { "assets", "lock" })
        {
            Reset(zeroOriginal);
            File.Delete(mode == "assets" ? zero.Lock : zero.Assets);
            Pass(zero, 0);
        }
        Reset(zeroOriginal);
        var empty = Read(zero.Assets);
        empty["targets"] = new JsonObject();
        empty["libraries"] = new JsonObject();
        empty["projectFileDependencyGroups"] = new JsonObject();
        empty["project"]!["frameworks"] = new JsonObject();
        Write(zero.Assets, empty);
        Failure(zero, "collapsed zero-package framework containers");

        var child = Project("reference/Child", """<PropertyGroup><PackageId>Company.Child</PackageId></PropertyGroup><ItemGroup><PackageReference Include="Company.Secret" Version="1.0.0"/></ItemGroup>""", fileName: "Child.csproj");
        var parent = Project("reference/Parent", $"<ItemGroup><ProjectReference Include=\"{Escape(child.Path)}\"/></ItemGroup>");
        Pass(parent, 1);
        var parentOriginal = SaveRestore(parent);
        foreach (var mode in new[] { "assets", "lock", "combined" })
        {
            Reset(parentOriginal);
            OmitPackage(parent, "Company.Secret", mode);
            Failure(parent, mode + " project-origin dependency omission");
        }
        Reset(parentOriginal);
        var nodes = Read(parent.Assets);
        nodes["targets"]!["net8.0"]!.AsObject().Clear();
        nodes["libraries"]!.AsObject().Clear();
        nodes["projectFileDependencyGroups"]!["net8.0"] = new JsonArray();
        Write(parent.Assets, nodes);
        File.Delete(parent.Lock);
        Failure(parent, "collapsed ProjectReference graph");

        var runtime = Project("runtime-targets", """<PropertyGroup><RuntimeIdentifiers>linux-x64;win-x64</RuntimeIdentifiers><SelfContained>false</SelfContained><EnableRuntimePackDownload>false</EnableRuntimePackDownload><EnableAppHostPackDownload>false</EnableAppHostPackDownload></PropertyGroup><ItemGroup><PackageReference Include="Public.Entry" Version="1.0.0"/></ItemGroup>""");
        Pass(runtime, 2);
        var runtimeOriginal = SaveRestore(runtime);
        foreach (var artifact in new[] { "assets", "lock" })
            foreach (var rid in new[] { "linux-x64", "win-x64" })
            {
                Reset(runtimeOriginal);
                var path = artifact == "assets" ? runtime.Assets : runtime.Lock;
                var document = Read(path);
                var container = document[artifact == "assets" ? "targets" : "dependencies"]!.AsObject();
                container.Remove(container.First(item => item.Key.EndsWith("/" + rid, StringComparison.Ordinal)).Key);
                Write(path, document);
                File.Delete(artifact == "assets" ? runtime.Lock : runtime.Assets);
                Failure(runtime, "missing runtime target " + artifact + "/" + rid);
            }
        Reset(runtimeOriginal);
        var runtimeLock = Read(runtime.Lock);
        var runtimeTarget = runtimeLock["dependencies"]!.AsObject().First(item => item.Key.EndsWith("/linux-x64", StringComparison.Ordinal)).Value!.AsObject();
        runtimeTarget["Public.Entry"] = JsonNode.Parse("""{"type":"Direct","resolved":"1.0.0","dependencies":{"Missing.RuntimeDependency":"1.0.0"}}""");
        Write(runtime.Lock, runtimeLock);
        File.Delete(runtime.Assets);
        Failure(runtime, "dangling dependency in runtime lock delta");
        Console.WriteLine("Restore closure: package/project edges, artifacts alone/together, framework containers, zero-package controls, lock 1/2.");
    }

    private void EvaluatedInputs()
    {
        const string items = """
            <PropertyGroup><DependencyId>Public.Entry</DependencyId></PropertyGroup>
            <ItemGroup Condition="'$(Configuration)' == 'Never' "><PackageReference Include="Missing.Group" Version="1.0.0"/></ItemGroup>
            <ItemGroup>
              <PackageReference Include="Missing.Optional" Version="1.0.0" Condition="false"/>
              <PackageReference Include="$(DependencyId)" Version="1.0.0"/>
              <PackageReference Update="NotIncluded" Version="1.0.0"/>
              <PackageReference Include="Removed.Dependency" Version="1.0.0"/>
              <PackageReference Remove="Removed.Dependency"/>
            </ItemGroup>
            <Choose><When Condition="'$(Configuration)' == 'Release'"><ItemGroup><PackageReference Include="Other.Dependency" Version="1.0.0"/></ItemGroup></When>
            <Otherwise><ItemGroup><PackageReference Include="Missing.Debug" Version="1.0.0" Condition="false"/></ItemGroup></Otherwise></Choose>
            <Import Project="Imported.props"/>
            """;
        var fixture = Project("evaluated", items, restore: false);
        File.WriteAllText(Path.Combine(fixture.Root, "Imported.props"), """<Project><ItemGroup Condition="'$(TargetFramework)' == 'net8.0'"><PackageReference Include="Company.Secret" Version="1.0.0"/></ItemGroup></Project>""");
        foreach (var configuration in new[] { "Debug", "Release" })
        {
            Restore(fixture, "-p:Configuration=" + configuration);
            Pass(fixture, configuration == "Release" ? 3 : 2);
        }
        var saved = SaveRestore(fixture);
        OmitPackage(fixture, "Other.Dependency", "combined");
        Failure(fixture, "omitted conditionally evaluated dependency");
        Reset(saved);

        var multi = Project("multi-framework", """
            <PropertyGroup><TargetFramework/><TargetFrameworks>net8.0;net8.0-windows</TargetFrameworks></PropertyGroup>
            <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'"><PackageReference Include="Public.Entry" Version="1.0.0"/></ItemGroup>
            <ItemGroup Condition="'$(TargetFramework)' == 'net8.0-windows'"><PackageReference Include="Other.Dependency" Version="1.0.0"/></ItemGroup>
            """);
        Pass(multi, 3);
        var multiSaved = SaveRestore(multi);
        foreach (var artifact in new[] { "assets", "lock" })
        {
            Reset(multiSaved);
            if (artifact == "assets")
            {
                var assets = Read(multi.Assets);
                var target = assets["targets"]!.AsObject().First(item => item.Key.Contains("windows", StringComparison.Ordinal)).Key;
                assets["targets"]!.AsObject().Remove(target);
                Write(multi.Assets, assets);
                File.Delete(multi.Lock);
            }
            else
            {
                var lockFile = Read(multi.Lock);
                var framework = lockFile["dependencies"]!.AsObject().First(item => item.Key.Contains("windows", StringComparison.Ordinal)).Key;
                lockFile["dependencies"]!.AsObject().Remove(framework);
                Write(multi.Lock, lockFile);
                File.Delete(multi.Assets);
            }
            Failure(multi, artifact + " missing conditional framework");
        }
        Console.WriteLine("Evaluated inputs: conditional groups/items, Choose, properties, Update, Remove, imports, configuration/TFM conditions.");
    }

    private void SolutionMembers()
    {
        var fixture = Project("solution", "");
        var path = Path.Combine(fixture.Root, "Mixed.sln");
        const string type = "FAE04EC0-301F-11D3-BF4B-00C04F79EFBC";
        var good = $"Project(\"{{{type}}}\") = \"Good\", \"Project.csproj\", \"{{00000000-0000-0000-0000-000000000001}}\"\nEndProject\n";
        foreach (var prefix in new[] { "", "  ", "\t" })
        {
            foreach (var member in new[] { "Missing.csproj", "Unsupported.vcxproj" })
            {
                var bad = $"{prefix}Project(\"{{{type}}}\") = \"Bad\", \"{member}\", \"{{00000000-0000-0000-0000-000000000002}}\"\nEndProject\n";
                File.WriteAllText(path, "Microsoft Visual Studio Solution File, Format Version 12.00\n" + good + bad + "Global\nEndGlobal\n");
                Failure(fixture with { Path = path }, "mixed " + prefix.Length + " " + member);
            }
        }
        foreach (var bad in new[] { "Project(\"broken\") = missing", "Project = invalid", "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Bad\", \"\", \"{00000000-0000-0000-0000-000000000002}\"" })
        {
            File.WriteAllText(path, "Microsoft Visual Studio Solution File, Format Version 12.00\n" + good + bad + "\nEndProject\n");
            Failure(fixture with { Path = path }, "malformed mixed sln member");
        }
        var slnx = Path.ChangeExtension(path, ".slnx");
        foreach (var bad in new[] { "<Project/>", "<Project Path=\"\"/>", "<Project Path=\" \t \"/>", "<Project Path=\"Missing.csproj\"/>", "<Project Path=\"Unsupported.vcxproj\"/>", "<project Path=\"Missing.csproj\"/>", "<Project xmlns=\"urn:unsupported\" Path=\"Missing.csproj\"/>" })
        {
            File.WriteAllText(slnx, "<Solution><Project Path=\"Project.csproj\"/>" + bad + "</Solution>");
            Failure(fixture with { Path = slnx }, "malformed mixed slnx member");
        }
        // Project languages are accepted by membership, independently of source compilation.
        var fs = Project("solution/FSharp", "");
        var vb = Project("solution/VisualBasic", "");
        foreach (var member in new[] { (fs, ".fsproj"), (vb, ".vbproj") })
        {
            var renamed = Path.ChangeExtension(member.Item1.Path, member.Item2);
            File.Move(member.Item1.Path, renamed);
            var oldEvidence = member.Item1.Evidence;
            var evidence = Read(oldEvidence);
            var project = evidence["projects"]![member.Item1.Path]!.DeepClone();
            evidence["projects"] = new JsonObject { [renamed] = project };
            evidence["restore"] = new JsonObject { [renamed] = new JsonObject() };
            File.Delete(oldEvidence);
            Write(Path.Combine(member.Item1.Root, "obj", Path.GetFileName(renamed) + ".nuget.dgspec.json"), evidence);
        }
        var fsPath = Path.ChangeExtension(fs.Path, ".fsproj");
        var vbPath = Path.ChangeExtension(vb.Path, ".vbproj");
        File.WriteAllText(slnx, $"<Solution><Folder Name=\"/docs.v2/\"><Project Path=\"Project.csproj\"/></Folder><Project Path=\"{Escape(fsPath)}\"/><Project Path=\"{Escape(vbPath)}\"/></Solution>");
        Pass(fixture with { Path = slnx }, 0);
        File.WriteAllText(path, "Microsoft Visual Studio Solution File, Format Version 12.00\n" + good.Replace("Project(", "\tProject(", StringComparison.Ordinal) + "Project(\"{2150E333-8FDC-42A3-9474-1A3956D46DE8}\") = \"docs.v2\", \"docs.v2\", \"{00000000-0000-0000-0000-000000000003}\"\nEndProject\nGlobal\nEndGlobal\n");
        Pass(fixture with { Path = path }, 0);
        Console.WriteLine("Solutions: mixed good/bad members, spaces/tabs, malformed declarations, slnx Path, project languages and folders.");
    }

    private void ConfigInputs()
    {
        var fixture = Project("config", "");
        File.Delete(_userConfig);
        var before = Snapshot(fixture.Root, Path.GetDirectoryName(_userConfig)!);
        Expect(Check(fixture, explicitConfig: false), 0, "absent user with clear");
        Equal(before, Snapshot(fixture.Root, Path.GetDirectoryName(_userConfig)!), "read-only absent user with clear");
        File.WriteAllText(fixture.Config, "<configuration><packageSources><add key=\"private\" value=\"https://private.example.invalid/v3/index.json\"/></packageSources></configuration>");
        before = Snapshot(fixture.Root, Path.GetDirectoryName(_userConfig)!);
        var missing = Check(fixture, explicitConfig: false);
        Expect(missing, 2, "absent user without clear");
        Contains(missing.StandardError, "user NuGet configuration is absent", "explicit absent-user failure");
        Equal(before, Snapshot(fixture.Root, Path.GetDirectoryName(_userConfig)!), "read-only absent user without clear");
        var actual = External("dotnet", ["nuget", "list", "source", "--format", "Detailed"], fixture.Root);
        Expect(actual, 0, "NuGet missing-user default control");
        Contains(actual.Output, "nuget.org", "NuGet initialized default source");
        Contains(actual.Output, "private", "NuGet retained repository source");
        File.WriteAllText(_userConfig, "<configuration/>");
        var additional = Path.Combine(Path.GetDirectoryName(_userConfig)!, "config");
        Directory.CreateDirectory(additional);
        File.WriteAllText(Path.Combine(additional, "A.config"), "<configuration><packageSources><add key=\"ordered\" value=\"http://ordered.example.invalid/v3/index.json\"/></packageSources></configuration>");
        File.WriteAllText(Path.Combine(additional, "Z.Config"), "<configuration><packageSources><add key=\"ordered\" value=\"https://ordered.example.invalid/v3/index.json\"/></packageSources></configuration>");
        File.WriteAllText(fixture.Config, "<configuration/>");
        actual = External("dotnet", ["nuget", "list", "source", "--format", "Detailed"], fixture.Root);
        Expect(actual, 0, "NuGet combined-extension ordering control");
        Contains(actual.Output, "http://ordered.example.invalid", "official combined-extension winner");
        before = Snapshot(fixture.Root, Path.GetDirectoryName(_userConfig)!);
        var ordered = Check(fixture, explicitConfig: false);
        Expect(ordered, 1, "combined-extension source ordering");
        Diagnostic(ordered, "FF006", true);
        Equal(before, Snapshot(fixture.Root, Path.GetDirectoryName(_userConfig)!), "read-only combined-extension analysis");
        File.Delete(Path.Combine(additional, "A.config"));
        File.Delete(Path.Combine(additional, "Z.Config"));

        if (!OperatingSystem.IsMacOS())
        {
            var defaults = Path.Combine(_machineBase, ConfigurationConstants.ConfigurationDefaultsFile);
            foreach (var budget in new[] { "bytes", "depth", "elements" })
            {
                var xml = budget switch
                {
                    "bytes" => "<configuration>" + new string(' ', 2 * 1024 * 1024) + "</configuration>",
                    "depth" => "<configuration>" + string.Concat(Enumerable.Repeat("<section>", 65)) + string.Concat(Enumerable.Repeat("</section>", 65)) + "</configuration>",
                    _ => "<configuration>" + string.Concat(Enumerable.Repeat("<section/>", 100_001)) + "</configuration>"
                };
                File.WriteAllText(defaults, xml);
                foreach (var explicitConfig in new[] { false, true })
                {
                    before = Snapshot(fixture.Root, Path.GetDirectoryName(_userConfig)!, _machineBase);
                    var result = Check(fixture, explicitConfig: explicitConfig);
                    Expect(result, 2, "implicit defaults " + budget + "/" + explicitConfig);
                    Contains(result.StandardError, "configuration file", "pre-parse defaults budget error");
                    Equal(before, Snapshot(fixture.Root, Path.GetDirectoryName(_userConfig)!, _machineBase), "read-only implicit defaults rejection");
                }
            }
            File.WriteAllText(defaults, "<configuration><packageSources><add key=\"default\" value=\"https://default.example.invalid/v3/index.json\"/></packageSources></configuration>");
            var officialDefaults = External("dotnet", ["nuget", "list", "source", "--configfile", fixture.Config, "--format", "Detailed"], fixture.Root);
            Expect(officialDefaults, 0, "official implicit defaults control");
            Contains(officialDefaults.Output, "https://default.example.invalid", "official consumed defaults input");
            var control = Check(fixture);
            Expect(control, 0, "ordinary implicit defaults control");
            using (var report = JsonDocument.Parse(control.StandardOutput))
            {
                Equal(1, report.RootElement.GetProperty("summary").GetProperty("activeSourceCount").GetInt32(), "implicit defaults consumed even with explicit config");
                Equal(false, report.RootElement.GetProperty("sources")[0].GetProperty("repositoryControlled").GetBoolean(), "implicit defaults provenance");
            }
            if (OperatingSystem.IsLinux())
            {
                var ignoredCaseVariant = Path.Combine(_machineBase, "NuGetDefaults.Config");
                File.WriteAllText(ignoredCaseVariant, "<configuration>" + new string(' ', 2 * 1024 * 1024) + "</configuration>");
                var ignored = Check(fixture);
                Expect(ignored, 0, "unconsumed differently cased defaults file");
                File.Delete(ignoredCaseVariant);
            }
            File.Delete(defaults);
        }
        else
        {
            Console.WriteLine("Implicit-default file mutation controls: NuGet has no macOS machine-directory override; Windows/Linux exercise this boundary without host changes.");
        }
        Console.WriteLine("Configuration: real NuGet differential ordering/default controls, hierarchy/explicit implicit budgets, unchanged snapshots.");
    }

    private void PhysicalProvenance()
    {
        var fixture = Project("provenance", "");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(Path.Combine(outside, "sub"));
        var externalConfig = Path.Combine(outside, "sub", "NuGet.Config");
        File.WriteAllText(externalConfig, "<configuration><packageSources><clear/><add key=\"active\" value=\"https://feed.example.invalid/v3/index.json\"/></packageSources></configuration>");
        ProvenanceResult(Check(fixture, strict: true), 0, true);
        foreach (var (name, target, suffix) in new[]
        {
            ("parent", Path.Combine(outside, "sub"), "NuGet.Config"),
            ("ancestor", outside, Path.Combine("sub", "NuGet.Config"))
        })
        {
            var link = Path.Combine(fixture.Root, name);
            LinkDirectory(link, target);
            ProvenanceResult(Check(fixture with { Config = Path.Combine(link, suffix) }, strict: true), 1, false);
        }
        var chain = Path.Combine(fixture.Root, "chain");
        LinkDirectory(chain, Path.Combine(fixture.Root, "ancestor"));
        ProvenanceResult(Check(fixture with { Config = Path.Combine(chain, "sub", "NuGet.Config") }, strict: true), 1, false);
        var internalLink = Path.Combine(fixture.Root, "internal");
        LinkDirectory(internalLink, Path.Combine(fixture.Root, "obj"));
        File.WriteAllText(Path.Combine(fixture.Root, "obj", "NuGet.Config"), File.ReadAllText(externalConfig));
        ProvenanceResult(Check(fixture with { Config = Path.Combine(internalLink, "NuGet.Config") }, strict: true), 0, true);
        var linkedRoot = Path.Combine(_root, "linked-repository");
        LinkDirectory(linkedRoot, fixture.Root);
        ProvenanceResult(Check(fixture with { Root = linkedRoot, Path = Path.Combine(linkedRoot, "Project.csproj"), Config = Path.Combine(linkedRoot, "NuGet.Config") }, strict: true), 0, true);
        var classification = Provenance.Classify(fixture.Config, linkedRoot);
        Equal(true, classification.IsRepositoryControlled, "linked repository physical boundary");
        Equal(false, Provenance.Classify(Path.Combine(fixture.Root, "missing", "NuGet.Config"), fixture.Root).IsRepositoryControlled, "unresolved file is never repository-controlled");
        Equal(false, Provenance.Classify(fixture.Config, Path.Combine(fixture.Root, "missing-root")).IsRepositoryControlled, "unresolved repository boundary is never controlled");
        if (!OperatingSystem.IsWindows())
        {
            var fileLink = Path.Combine(fixture.Root, "file.config");
            File.CreateSymbolicLink(fileLink, externalConfig);
            ProvenanceResult(Check(fixture with { Config = fileLink }, strict: true), 1, false);
            var cycle = Path.Combine(fixture.Root, "cycle");
            Directory.CreateSymbolicLink(cycle, cycle);
            _directoryLinks.Add(cycle);
            Equal(false, Provenance.Classify(Path.Combine(cycle, "NuGet.Config"), fixture.Root).IsRepositoryControlled, "cyclic link cannot assert control");
        }
        else
        {
            Console.WriteLine("File symlink controls run on Linux/macOS; Windows uses real directory junctions without elevation.");
        }
        Console.WriteLine("Physical provenance: complete ancestry, link chains, internal controls, linked root and resolution failures.");
    }

    private void MappingWinners()
    {
        var fixture = Project("mapping", """<ItemGroup><PackageReference Include="Company.Secret" Version="1.0.0"/></ItemGroup>""");
        foreach (var sourceState in new[] { "unknown", "disabled" })
            foreach (var activeEnabled in new[] { true, false })
            {
                foreach (var winning in new[] { "Company.Secret", "Company.S*", "Company.*", "*" })
                {
                    var fallback = winning is "Company.Secret" or "Company.S*" ? "Company.*" : "*";
                    var equal = winning == "*";
                    var disabledKeys = new List<string>();
                    if (sourceState == "disabled") disabledKeys.Add("missing");
                    if (!activeEnabled) disabledKeys.Add("active");
                    var disabled = "<disabledPackageSources>" + string.Concat(disabledKeys.Select(key => $"<add key=\"{key}\" value=\"true\"/>")) + "</disabledPackageSources>";
                    var extra = sourceState == "disabled" ? "<add key=\"missing\" value=\"https://disabled.example.invalid/v3/index.json\"/>" : "";
                    File.WriteAllText(fixture.Config, $"<configuration><packageSources><clear/><add key=\"active\" value=\"https://active.example.invalid/v3/index.json\"/>{extra}</packageSources>{disabled}<packageSourceMapping><packageSource key=\"missing\"><package pattern=\"{winning}\"/></packageSource><packageSource key=\"active\"><package pattern=\"{fallback}\"/></packageSource></packageSourceMapping></configuration>");
                    var result = Check(fixture);
                    Expect(result, sourceState == "unknown" || !equal || !activeEnabled ? 1 : 0, sourceState + " " + winning + "/active=" + activeEnabled);
                    Diagnostic(result, "FF004", sourceState == "unknown");
                    Diagnostic(result, "FF003", !equal || !activeEnabled);
                    Diagnostic(result, "FF002", false);
                    if (!equal || !activeEnabled) Contains(result.StandardOutput, "no eligible active source", "no eligible source identity");
                }
            }
        // Equal exact and prefix winners retain the configured active source beside unknown/disabled keys.
        foreach (var state in new[] { "unknown", "disabled" })
            foreach (var pattern in new[] { "Company.Secret", "Company.*" })
            {
                var extra = state == "disabled" ? "<add key=\"missing\" value=\"https://disabled.example.invalid/v3/index.json\"/>" : "";
                var disabled = state == "disabled" ? "<disabledPackageSources><add key=\"missing\" value=\"true\"/></disabledPackageSources>" : "";
                File.WriteAllText(fixture.Config, $"<configuration><packageSources><clear/><add key=\"active\" value=\"https://active.example.invalid/v3/index.json\"/>{extra}</packageSources>{disabled}<packageSourceMapping><packageSource key=\"missing\"><package pattern=\"{pattern}\"/></packageSource><packageSource key=\"active\"><package pattern=\"{pattern}\"/></packageSource></packageSourceMapping></configuration>");
                var result = Check(fixture);
                Expect(result, state == "unknown" ? 1 : 0, "equal " + state + " specificity");
                Diagnostic(result, "FF004", state == "unknown");
                Diagnostic(result, "FF003", false);
                Diagnostic(result, "FF002", false);
            }
        File.WriteAllText(fixture.Config, "<configuration><packageSources><clear/></packageSources><packageSourceMapping><packageSource key=\"missing\"><package pattern=\"*\"/></packageSource></packageSourceMapping></configuration>");
        var absent = Check(fixture);
        Expect(absent, 1, "mapping with no configured source");
        Diagnostic(absent, "FF004", true);
        Diagnostic(absent, "FF003", true);
        Console.WriteLine("Mapping: unknown/disabled exact, prefix and wildcard winners; zero enabled/configured sources; diagnostic identities and equal-specificity controls.");
    }

    private void SourceSecurity()
    {
        var fixture = Project("security", "");
        foreach (var enabled in new[] { true, false })
        {
            foreach (var tls in new[] { "true", "false", "TRUE" })
            {
                var extra = enabled ? "" : "<add key=\"safe\" value=\"https://safe.example.invalid/v3/index.json\"/>";
                var disabled = enabled ? "" : "<disabledPackageSources><add key=\"active\" value=\"true\"/></disabledPackageSources>";
                File.WriteAllText(fixture.Config, $"<configuration><packageSources><clear/><add key=\"active\" value=\"https://tls.example.invalid/v3/index.json\" disableTLSCertificateValidation=\"{tls}\"/>{extra}</packageSources>{disabled}</configuration>");
                var insecure = enabled && bool.Parse(tls);
                var result = Check(fixture);
                Expect(result, insecure ? 1 : 0, "TLS security " + enabled + "/" + tls);
                Diagnostic(result, "FF006", insecure);
            }
        }
        File.WriteAllText(fixture.Config, "<configuration><packageSources><clear/><add key=\"active\" value=\"https://tls.example.invalid/v3/index.json\" disableTLSCertificateValidation=\"true\"/></packageSources></configuration>");
        File.WriteAllText(Path.Combine(fixture.Root, "feedfence.json"), """{"version":1,"exceptions":[{"code":"FF006","sourceKey":"active","reason":"Isolated development feed has a separately verified private certificate."}]}""");
        Expect(Check(fixture), 0, "narrow reasoned TLS exception");
        File.Delete(Path.Combine(fixture.Root, "feedfence.json"));
        File.WriteAllText(fixture.Config, "<configuration><packageSources><clear/><add key=\"active\" value=\"https://tls.example.invalid/v3/index.json\" disableTLSCertificateValidation=\"yes\"/></packageSources></configuration>");
        Failure(fixture, "invalid effective TLS setting");
        foreach (var source in new[] { "https://safe.example.invalid/v3/index.json", _feed })
        {
            File.WriteAllText(fixture.Config, $"<configuration><packageSources><clear/><add key=\"active\" value=\"{Escape(source)}\"/></packageSources></configuration>");
            Expect(Check(fixture), 0, "secure HTTPS/local control");
        }
        var parentConfig = Path.Combine(_root, "NuGet.Config");
        File.WriteAllText(parentConfig, "<configuration><packageSources><clear/><add key=\"active\" value=\"https://tls.example.invalid/v3/index.json\" disableTLSCertificateValidation=\"true\"/></packageSources></configuration>");
        File.WriteAllText(fixture.Config, "<configuration/>");
        var inherited = Check(fixture, explicitConfig: false);
        Expect(inherited, 1, "inherited TLS setting");
        Diagnostic(inherited, "FF006", true);
        File.WriteAllText(fixture.Config, "<configuration><packageSources><add key=\"active\" value=\"https://tls.example.invalid/v3/index.json\" disableTLSCertificateValidation=\"false\"/></packageSources></configuration>");
        var overridden = Check(fixture, explicitConfig: false);
        Expect(overridden, 0, "overridden TLS setting");
        Diagnostic(overridden, "FF006", false);
        File.Delete(parentConfig);
        Console.WriteLine("FF006: effective TLS true/false, enabled/disabled, inherited/override, reasoned exceptions, HTTPS/local; no feed contact.");
    }

    private RestoredProject Project(string name, string items, bool restore = true, string fileName = "Project.csproj")
    {
        var directory = Path.GetFullPath(Path.Combine(_root, name));
        Directory.CreateDirectory(Path.Combine(directory, ".git"));
        var path = Path.Combine(directory, fileName);
        var config = Path.Combine(directory, "NuGet.Config");
        File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile><NuGetAudit>false</NuGetAudit></PropertyGroup>" + items + "</Project>");
        File.WriteAllText(config, $"<configuration><packageSources><clear/><add key=\"active\" value=\"{Escape(_feed)}\"/></packageSources></configuration>");
        var fixture = new RestoredProject(directory, path, config);
        if (restore) Restore(fixture);
        return fixture;
    }

    private void Restore(RestoredProject fixture, params string[] arguments)
        => Expect(External("dotnet", ["restore", fixture.Path, "--configfile", fixture.Config, "--force", "--verbosity", "quiet", .. arguments], fixture.Root), 0, "local-feed SDK restore");

    private ProcessResult Check(RestoredProject fixture, bool explicitConfig = true, bool strict = false)
    {
        var arguments = new List<string> { "check", fixture.Path, "--format", "json" };
        if (explicitConfig) arguments.AddRange(["--config", fixture.Config]);
        if (strict) arguments.Add("--strict");
        return _tool.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? External("dotnet", [_tool, .. arguments], fixture.Root)
            : External(_tool, arguments, fixture.Root);
    }

    private ProcessResult External(string command, IEnumerable<string> arguments, string workingDirectory)
    {
        var info = new ProcessStartInfo(command) { WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var entry in _environment) info.Environment[entry.Key] = entry.Value;
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start test process.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Restore/input test process timed out.");
        }
        return new(process.ExitCode, stdout.Result, stderr.Result);
    }

    private void Pass(RestoredProject fixture, int packageCount)
    {
        var result = Check(fixture);
        Expect(result, 0, "valid restored input " + fixture.Root);
        using var report = JsonDocument.Parse(result.StandardOutput);
        Equal(packageCount, report.RootElement.GetProperty("summary").GetProperty("resolvedPackageCount").GetInt32(), "resolved package coverage");
    }

    private void Failure(RestoredProject fixture, string name)
    {
        var before = Snapshot(fixture.Root);
        var result = Check(fixture);
        Expect(result, 2, name);
        Equal("", result.StandardOutput, name + " has no partial success report");
        Equal(before, Snapshot(fixture.Root), name + " is read-only");
    }

    private void ProvenanceResult(ProcessResult result, int exitCode, bool controlled)
    {
        Expect(result, exitCode, "physical provenance strict exit");
        using var report = JsonDocument.Parse(result.StandardOutput);
        var source = report.RootElement.GetProperty("sources")[0];
        Equal(controlled, source.GetProperty("repositoryControlled").GetBoolean(), "physical repositoryControlled");
        Contains(source.GetProperty("provenance").GetString()!, controlled ? "repository-controlled" : "inherited", "physical provenance label");
        Diagnostic(result, "FF005", !controlled);
    }

    private void Diagnostic(ProcessResult result, string code, bool present)
    {
        using var report = JsonDocument.Parse(result.StandardOutput);
        Equal(present, report.RootElement.GetProperty("diagnostics").EnumerateArray().Any(item => item.GetProperty("code").GetString() == code), code + " presence");
    }

    private void LinkDirectory(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(_root, "link-directory.ps1");
            File.WriteAllText(script, "param([string]$LinkPath,[string]$TargetPath)\n$ErrorActionPreference='Stop'\nNew-Item -ItemType Junction -Path $LinkPath -Target $TargetPath | Out-Null\n");
            Expect(External("pwsh", ["-NoProfile", "-NonInteractive", "-File", script, link, target], _root), 0, "create fixture junction");
        }
        else Directory.CreateSymbolicLink(link, target);
        _directoryLinks.Add(link);
    }

    private void Package(string id, string? dependency = null)
    {
        using var archive = ZipFile.Open(Path.Combine(_feed, id + ".1.0.0.nupkg"), ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry(id + ".nuspec").Open());
        var dependencies = dependency is null ? "" : $"<dependencies><group targetFramework=\"net8.0\"><dependency id=\"{dependency}\" version=\"1.0.0\"/></group></dependencies>";
        writer.Write($"<package><metadata><id>{id}</id><version>1.0.0</version><authors>Example</authors><description>Local restore fixture</description>{dependencies}</metadata></package>");
    }

    private static Dictionary<string, byte[]> SaveRestore(RestoredProject fixture)
        => new[] { fixture.Assets, fixture.Lock, fixture.Evidence }.ToDictionary(path => path, File.ReadAllBytes);

    private static void Reset(Dictionary<string, byte[]> originals)
    {
        foreach (var item in originals) File.WriteAllBytes(item.Key, item.Value);
    }

    private static void OmitPackage(RestoredProject fixture, string id, string mode)
    {
        if (mode is "assets" or "combined")
        {
            var assets = Read(fixture.Assets);
            foreach (var target in assets["targets"]!.AsObject()) target.Value!.AsObject().Remove(id + "/1.0.0");
            assets["libraries"]!.AsObject().Remove(id + "/1.0.0");
            Write(fixture.Assets, assets);
        }
        if (mode is "lock" or "combined")
        {
            var lockFile = Read(fixture.Lock);
            foreach (var framework in lockFile["dependencies"]!.AsObject()) framework.Value!.AsObject().Remove(id);
            Write(fixture.Lock, lockFile);
        }
        if (mode == "assets") File.Delete(fixture.Lock);
        if (mode == "lock") File.Delete(fixture.Assets);
    }

    private static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    private static void Write(string path, JsonNode node) => File.WriteAllText(path, node.ToJsonString());
    private static string Escape(string value) => SecurityElement.Escape(value)!;
    private static string Snapshot(params string[] directories) => string.Join("\n", directories.SelectMany(directory => Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)).Order(StringComparer.Ordinal)
        .Select(path => Directory.Exists(path) ? "directory:" + path : path + ":" + File.GetLastWriteTimeUtc(path).Ticks + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
    private void Expect(ProcessResult result, int code, string name) => Equal(code, result.ExitCode, name + "; " + result.Output);
    private void Contains(string value, string expected, string name) => Equal(true, value.Contains(expected, StringComparison.Ordinal), name + "; " + value);
    private void Equal<T>(T expected, T actual, string name)
    {
        _assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{name}: expected {expected}, got {actual}.");
    }
    public void Dispose()
    {
        foreach (var link in _directoryLinks.AsEnumerable().Reverse())
        {
            if (OperatingSystem.IsWindows()) Directory.Delete(link);
            else File.Delete(link); // unlink also handles dangling and self-referential directory symlinks
        }
        Directory.Delete(_root, recursive: true);
    }
    private sealed record RestoredProject(string Root, string Path, string Config)
    {
        public string Assets => System.IO.Path.Combine(Root, "obj", "project.assets.json");
        public string Lock => System.IO.Path.Combine(Root, "packages.lock.json");
        public string Evidence => System.IO.Path.Combine(Root, "obj", System.IO.Path.GetFileName(Path) + ".nuget.dgspec.json");
    }
}
