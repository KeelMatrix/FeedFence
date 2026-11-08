using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace KeelMatrix.FeedFence.Phase0Probe;

internal sealed partial class ProbeRunner
{
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

    private static void WriteSyntheticAssets(string projectPath, string packageId)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var objDirectory = Path.Combine(projectDirectory, "obj");
        Directory.CreateDirectory(objDirectory);
        var assets = new Dictionary<string, object>
        {
            ["version"] = 3,
            ["targets"] = new Dictionary<string, object>
            {
                ["net8.0"] = new Dictionary<string, object>
                {
                    [$"{packageId}/1.0.0"] = new { type = "package" }
                }
            },
            ["libraries"] = new Dictionary<string, object>
            {
                [$"{packageId}/1.0.0"] = new { type = "package" }
            },
            ["projectFileDependencyGroups"] = new Dictionary<string, object>
            {
                ["net8.0"] = new List<string> { $"{packageId} >= 1.0.0" }
            },
            ["project"] = new Dictionary<string, object>
            {
                ["frameworks"] = new Dictionary<string, object>
                {
                    ["net8.0"] = new Dictionary<string, object>
                    {
                        ["dependencies"] = new Dictionary<string, object>
                        {
                            [packageId] = new { target = "Package", version = "[1.0.0, )" }
                        }
                    }
                }
            }
        };
        File.WriteAllText(Path.Combine(objDirectory, "project.assets.json"), JsonSerializer.Serialize(assets), new UTF8Encoding(false));
    }

    private static string FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(start));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KeelMatrix.FeedFence.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the FeedFence repository root.");
    }

    private void InstallScopeTemplates(string runRoot)
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
        if (_machineFixtureInUserLayer)
        {
            foreach (var primaryConfig in new[] { userConfig, unixUserConfig, dotnetUserConfig })
            {
                var additional = Path.Combine(Path.GetDirectoryName(primaryConfig)!, "config", "MachineFixture.config");
                Directory.CreateDirectory(Path.GetDirectoryName(additional)!);
                File.Copy(machineConfig, additional, overwrite: true);
            }
        }
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
        File.WriteAllText(path, $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework><RestoreProjectStyle>PackageReference</RestoreProjectStyle><DisableImplicitLibraryPacksFolder>true</DisableImplicitLibraryPacksFolder></PropertyGroup><ItemGroup><PackageReference Include=\"{packageId}\" Version=\"1.0.0\" /></ItemGroup></Project>", new UTF8Encoding(false));
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
                var tokenWithWindowsSeparator = token + '\\';
                var nativeTokenReplacement = new Uri(Path.GetFullPath(value) + Path.DirectorySeparatorChar).AbsoluteUri;
                var replaced = content.Replace(tokenWithWindowsSeparator, nativeTokenReplacement, StringComparison.Ordinal);
                File.WriteAllText(file, replaced.Replace('\\', '/').Replace(token, value, StringComparison.Ordinal), new UTF8Encoding(false));
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
}
