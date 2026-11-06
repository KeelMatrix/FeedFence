using System.Text.Json.Nodes;

namespace KeelMatrix.FeedFence.CliTests;

internal sealed partial class RestoreInputCases
{
    public void PrivateDependencyFlow()
    {
        var start = _assertions;
        const string package = "<ItemGroup><PackageReference Include=\"Public.Entry\" Version=\"1.0.0\" {0}/></ItemGroup>";
        foreach (var (name, metadata, count) in new[]
        {
            ("ordinary", "", 2),
            ("all", "PrivateAssets=\"all\"", 0),
            ("none", "PrivateAssets=\"none\"", 2),
            ("partial", "PrivateAssets=\"compile;runtime\"", 2),
            ("enumerated-all", "PrivateAssets=\"compile;runtime;native;contentFiles;analyzers;build;buildTransitive\"", 0),
            ("include-none", "IncludeAssets=\"none\"", 2),
            ("exclude-all", "ExcludeAssets=\"all\"", 2)
        })
        {
            var child = Project("private-package/" + name + "/Child", string.Format(System.Globalization.CultureInfo.InvariantCulture, package, metadata), fileName: "Child.csproj");
            var middle = Project("private-package/" + name + "/Middle", Reference(child), fileName: "Middle.csproj");
            var top = Project("private-package/" + name + "/Top", Reference(middle));
            AllArtifacts(child, 2);
            AllArtifacts(middle, count);
            AllArtifacts(top, count);
            if (count == 0)
            {
                var independent = Project("private-package/" + name + "/Independent", Reference(middle) + string.Format(System.Globalization.CultureInfo.InvariantCulture, package, ""));
                AllArtifacts(independent, 2);
                OmittedEvaluatedNode(child, "Public.Entry", packageNode: true);
                OmittedEvaluatedNode(independent, "Public.Entry", packageNode: true);
            }
            else OmittedEvaluatedNode(top, "Public.Entry", packageNode: true);
        }

        foreach (var (name, metadata, count) in new[]
        {
            ("ordinary", "", 2),
            ("all", "PrivateAssets=\"all\"", 0),
            ("none", "PrivateAssets=\"none\"", 2),
            ("partial", "PrivateAssets=\"compile;runtime\"", 2),
            ("enumerated-all", "PrivateAssets=\"compile;runtime;native;contentFiles;analyzers;build;buildTransitive\"", 0),
            ("include-none", "IncludeAssets=\"none\"", 2),
            ("exclude-all", "ExcludeAssets=\"all\"", 2),
            ("output-false", "ReferenceOutputAssembly=\"false\"", 0)
        })
        {
            var leaf = Project("private-project/" + name + "/Leaf", string.Format(System.Globalization.CultureInfo.InvariantCulture, package, ""), fileName: "Leaf.csproj");
            var middle = Project("private-project/" + name + "/Middle", Reference(leaf, metadata), fileName: "Middle.csproj");
            var top = Project("private-project/" + name + "/Top", Reference(middle), fileName: "Top.csproj");
            var ancestor = Project("private-project/" + name + "/Ancestor", Reference(top));
            AllArtifacts(leaf, 2);
            AllArtifacts(middle, name == "output-false" ? 0 : 2);
            AllArtifacts(top, count);
            AllArtifacts(ancestor, count);
            if (name is "all" or "enumerated-all")
            {
                var independent = Project("private-project/" + name + "/Independent", Reference(top) + Reference(leaf));
                AllArtifacts(independent, 2);
                OmittedEvaluatedNode(middle, "Leaf", packageNode: false);
                OmittedEvaluatedNode(independent, "Leaf", packageNode: false);
            }
            else if (count != 0) OmittedEvaluatedNode(ancestor, "Leaf", packageNode: false);
        }

        // NuGet selects each child's nearest framework for the original root TFM,
        // including when an intermediate project's selected TFM is different.
        var multi = Project("private-framework/Leaf", """
            <PropertyGroup><TargetFramework/><TargetFrameworks>net8.0;net8.0-windows</TargetFrameworks></PropertyGroup>
            <ItemGroup><PackageReference Include="Public.Entry" Version="1.0.0" PrivateAssets="all" Condition="'$(TargetFramework)' == 'net8.0'"/>
              <PackageReference Include="Other.Dependency" Version="1.0.0" Condition="'$(TargetFramework)' == 'net8.0-windows'"/></ItemGroup>
            """, fileName: "Leaf.csproj");
        var bridge = Project("private-framework/Bridge", Reference(multi), fileName: "Bridge.csproj");
        var frameworkRoot = Project("private-framework/Root", "<PropertyGroup><TargetFramework/><TargetFrameworks>net8.0;net8.0-windows</TargetFrameworks></PropertyGroup>" + Reference(bridge));
        AllArtifacts(multi, 3);
        AllArtifacts(bridge, 0);
        AllArtifacts(frameworkRoot, 1);
        OmittedEvaluatedNode(frameworkRoot, "Other.Dependency", packageNode: true);

        var conditionalLeaf = Project("private-project-framework/Leaf", string.Format(System.Globalization.CultureInfo.InvariantCulture, package, ""), fileName: "Leaf.csproj");
        var conditionalMiddle = Project("private-project-framework/Middle", $"""
            <PropertyGroup><TargetFramework/><TargetFrameworks>net8.0;net8.0-windows</TargetFrameworks></PropertyGroup>
            <ItemGroup><ProjectReference Include="{Escape(conditionalLeaf.Path)}" PrivateAssets="all" Condition="'$(TargetFramework)' == 'net8.0'"/>
              <ProjectReference Include="{Escape(conditionalLeaf.Path)}" PrivateAssets="none" Condition="'$(TargetFramework)' == 'net8.0-windows'"/></ItemGroup>
            """, fileName: "Middle.csproj");
        var conditionalRoot = Project("private-project-framework/Root", "<PropertyGroup><TargetFramework/><TargetFrameworks>net8.0;net8.0-windows</TargetFrameworks></PropertyGroup>" + Reference(conditionalMiddle));
        AllArtifacts(conditionalMiddle, 2);
        AllArtifacts(conditionalRoot, 2);
        OmittedEvaluatedNode(conditionalRoot, "Leaf", packageNode: false);

        var runtimeRoot = Project("private-runtime/Root", """
            <PropertyGroup><RuntimeIdentifiers>linux-x64;win-x64</RuntimeIdentifiers><SelfContained>false</SelfContained>
            <EnableRuntimePackDownload>false</EnableRuntimePackDownload><EnableAppHostPackDownload>false</EnableAppHostPackDownload></PropertyGroup>
            """ + Reference(bridge));
        AllArtifacts(runtimeRoot, 0);

        var malformedChild = Project("private-metadata/Child", string.Format(System.Globalization.CultureInfo.InvariantCulture, package, "PrivateAssets=\"all\""), fileName: "Child.csproj");
        var malformedRoot = Project("private-metadata/Root", Reference(malformedChild));
        var saved = SaveRestore(malformedRoot);
        foreach (var invalid in new JsonNode?[] { JsonValue.Create("All, unknown"), JsonValue.Create(127), new JsonArray("All"), null })
        {
            Reset(saved);
            var evidence = Read(malformedRoot.Evidence);
            evidence["projects"]![malformedChild.Path]!["frameworks"]!["net8.0"]!["dependencies"]!["Public.Entry"]!["suppressParent"] = invalid?.DeepClone();
            Write(malformedRoot.Evidence, evidence);
            Failure(malformedRoot, "invalid suppression cannot excuse missing package");
        }
        Reset(saved);
        var privateMiddle = Project("private-project-metadata/Middle", Reference(malformedChild, "PrivateAssets=\"all\""), fileName: "Middle.csproj");
        var privateTop = Project("private-project-metadata/Top", Reference(privateMiddle));
        var topSaved = SaveRestore(privateTop);
        foreach (var invalid in new JsonNode?[] { JsonValue.Create("all, unknown"), JsonValue.Create(""), JsonValue.Create(127), new JsonArray("all"), null })
        {
            Reset(topSaved);
            var evidence = Read(privateTop.Evidence);
            evidence["projects"]![privateMiddle.Path]!["restore"]!["frameworks"]!["net8.0"]!["projectReferences"]![malformedChild.Path]!["privateAssets"] = invalid?.DeepClone();
            Write(privateTop.Evidence, evidence);
            Failure(privateTop, "invalid suppression cannot excuse missing project");
        }
        Reset(topSaved);
        Console.WriteLine($"PASS: private dependency flow ({_assertions - start} assertions): local SDK restores, package/project boundaries, direct/shared paths, flags, frameworks, omissions, read-only; command={Path.GetFileName(_tool)}");
    }

    private static string Reference(RestoredProject child, string metadata = "") => $"<ItemGroup><ProjectReference Include=\"{Escape(child.Path)}\" {metadata}/></ItemGroup>";

    private void AllArtifacts(RestoredProject fixture, int count)
    {
        var saved = SaveRestore(fixture);
        foreach (var mode in new[] { "assets", "lock", "combined" })
        {
            Reset(saved);
            if (mode != "combined") File.Delete(mode == "assets" ? fixture.Lock : fixture.Assets);
            var before = Snapshot(fixture.Root);
            Pass(fixture, count);
            Equal(before, Snapshot(fixture.Root), "private-flow success is read-only " + mode);
        }
        Reset(saved);
    }

    private void OmittedEvaluatedNode(RestoredProject fixture, string id, bool packageNode)
    {
        var saved = SaveRestore(fixture);
        foreach (var mode in new[] { "assets", "lock", "combined" })
        {
            Reset(saved);
            OmitPackage(fixture, id, mode);
            // Remove incoming graph edges too: evaluated inputs must independently
            // detect applicable omissions even when the remaining graph is closed.
            foreach (var path in new[] { fixture.Assets, fixture.Lock }.Where(File.Exists))
            {
                var graph = Read(path);
                foreach (var target in graph[path == fixture.Assets ? "targets" : "dependencies"]!.AsObject())
                    foreach (var node in target.Value!.AsObject())
                        if (node.Value?["dependencies"] is JsonObject dependencies) RemoveIdentity(dependencies, id);
                Write(path, graph);
            }
            Failure(fixture, "omitted applicable " + (packageNode ? "package" : "project") + " " + mode);
        }
        Reset(saved);
    }
}
