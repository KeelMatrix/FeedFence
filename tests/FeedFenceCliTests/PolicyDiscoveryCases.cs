using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace KeelMatrix.FeedFence.CliTests;

internal sealed partial class RestoreInputCases
{
    private const string ProtectedPolicy = """{"version":1,"protectedPackages":[{"pattern":"Company.*","allowedSources":["company"]}]}""";

    public void PolicyDiscovery()
    {
        var start = _assertions;
        var help = _tool.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? External("dotnet", [_tool, "--help"], _root)
            : External(_tool, ["--help"], _root);
        Expect(help, 0, "policy help");
        Contains(help.StandardOutput, "without Git", "help documents Git-free discovery");
        Contains(help.StandardOutput, "external file", "help documents explicit external policy");
        var fixture = Project("policy-boundary/repository/src/components/App", """<ItemGroup><PackageReference Include="Company.Secret" Version="1.0.0"/></ItemGroup>""", restore: false);
        Directory.Delete(Path.Combine(fixture.Root, ".git")); // Empty fixture marker only.
        var repository = Path.GetFullPath(Path.Combine(fixture.Root, "../../.."));
        var config = Path.Combine(repository, "NuGet.Config");
        File.Move(fixture.Config, config);
        fixture = fixture with { Config = config };
        Restore(fixture);
        var policy = Path.Combine(repository, "feedfence.json");
        var rootSolution = PolicySolution(repository, fixture.Path);
        var nestedSolution = PolicySolution(Path.Combine(repository, "src"), fixture.Path);
        // These markers must not stop an ancestor policy search.
        File.WriteAllText(Path.Combine(repository, "src", "Other.csproj"), "<Project/>");
        File.WriteAllText(Path.Combine(repository, "src", "components", "Other.csproj"), "<Project/>");

        PolicyResult(fixture, 0, "git-free no policy", rootSolution);
        PolicyResult(fixture, 0, "git-free nested project without policy");
        File.WriteAllText(policy, ProtectedPolicy);
        PolicyResult(fixture, 1, "git-free nested project");
        PolicyResult(fixture, 1, "git-free root solution", rootSolution);
        PolicyResult(fixture, 1, "git-free nested solution", nestedSolution);
        PolicyResult(fixture, 1, "git-free selected directory", fixture.Root);
        PolicyResult(fixture, 1, "NuGet hierarchy remains independent", explicitConfig: false);

        var git = Path.Combine(repository, ".git");
        Directory.CreateDirectory(git);
        PolicyResult(fixture, 1, "ordinary Git nested project");
        PolicyResult(fixture, 1, "ordinary Git root solution", rootSolution);
        var casedPolicy = Path.Combine(repository, "FeedFence.JSON");
        File.Move(policy, casedPolicy);
        PolicyResult(fixture, File.Exists(policy) ? 1 : 0, "policy name follows filesystem case semantics");
        File.Move(casedPolicy, policy);
        Directory.Delete(git);
        File.WriteAllText(git, "gitdir: ../../worktree-metadata\n");
        PolicyResult(fixture, 1, "Git worktree file project");
        PolicyResult(fixture, 1, "Git worktree file solution", rootSolution);

        var ancestorPolicy = Path.Combine(Path.GetDirectoryName(repository)!, "feedfence.json");
        File.WriteAllText(ancestorPolicy, "not JSON");
        PolicyResult(fixture, 1, "Git boundary excludes parent policy");
        File.Delete(git);
        PolicyResult(fixture, 2, "multiple Git-free ancestor policies");
        File.Delete(ancestorPolicy);
        var nestedPolicy = Path.Combine(fixture.Root, "feedfence.json");
        File.WriteAllText(nestedPolicy, """{"version":1}""");
        PolicyResult(fixture, 2, "multiple policies without Git");
        Directory.CreateDirectory(git);
        PolicyResult(fixture, 2, "multiple policies within Git");
        var external = Path.Combine(Path.GetDirectoryName(repository)!, "explicit.json");
        File.WriteAllText(external, ProtectedPolicy);
        PolicyResult(fixture, 1, "explicit external overrides ambiguity", explicitPolicy: external);
        File.Delete(nestedPolicy);
        File.Delete(policy);
        PolicyResult(fixture, 0, "Git no policy");
        File.WriteAllText(nestedPolicy, ProtectedPolicy);
        PolicyResult(fixture, 1, "unique nested Git policy");
        File.Delete(nestedPolicy);

        foreach (var invalid in new[] { "{", "[]", "{\"version\":2}", new string(' ', 256 * 1024 + 1) })
        {
            File.WriteAllText(policy, invalid);
            PolicyResult(fixture, 2, "present malformed or oversized implicit policy");
            PolicyResult(fixture, 1, "explicit external overrides invalid default", explicitPolicy: external);
        }
        File.Delete(policy);
        File.WriteAllText(policy, ProtectedPolicy.PadRight(256 * 1024));
        PolicyResult(fixture, 1, "policy at supported byte boundary");
        File.Delete(policy);
        Directory.CreateDirectory(policy);
        PolicyResult(fixture, 2, "policy directory");
        PolicyResult(fixture, 1, "explicit external overrides non-file default", explicitPolicy: external);
        Directory.Delete(policy);
        Directory.Delete(git);
        Directory.CreateDirectory(policy);
        PolicyResult(fixture, 2, "Git-free non-file policy");
        Directory.Delete(policy);
        Directory.CreateDirectory(git);
        PolicyResult(fixture, 2, "missing explicit policy", explicitPolicy: policy);
        File.WriteAllText(external, "{");
        PolicyResult(fixture, 2, "malformed explicit policy", explicitPolicy: external);
        File.WriteAllText(external, ProtectedPolicy);

        PolicyLinks(fixture, repository, policy, external);
        File.WriteAllText(policy, ProtectedPolicy);
        PolicyReadDenial(fixture, policy, isDirectory: false);
        PolicyReadDenial(fixture, repository, isDirectory: true);
        File.Delete(policy);
        PolicyReadDenial(fixture, repository, isDirectory: true);
        PolicyResult(fixture, 0, "absence after all failure controls");
        Console.WriteLine($"PASS: repository policy discovery/loading ({_assertions - start} assertions): real restore, Git-free/Git/worktree, ambiguity, links, permissions, overrides, all formats, read-only; command={Path.GetFileName(_tool)}");
    }

    private static string PolicySolution(string directory, string project)
    {
        var path = Path.Combine(directory, "Policy.sln");
        var relative = Path.GetRelativePath(directory, project);
        File.WriteAllText(path, $"Microsoft Visual Studio Solution File, Format Version 12.00\nProject(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"App\", \"{relative}\", \"{{00000000-0000-0000-0000-000000000001}}\"\nEndProject\nGlobal\nEndGlobal\n");
        return path;
    }

    private void PolicyLinks(RestoredProject fixture, string repository, string policy, string external)
    {
        var internalFile = Path.Combine(repository, "owned.json");
        File.WriteAllText(internalFile, ProtectedPolicy);
        try
        {
            File.CreateSymbolicLink(policy, "owned.json");
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP: Windows file symbolic links require developer mode or link privilege; junction and permission controls still run.");
            PolicyDirectoryLinks(fixture, repository, policy, external);
            return;
        }
        PolicyResult(fixture, 1, "internal relative policy link");
        File.Delete(policy);
        File.CreateSymbolicLink(policy, external);
        PolicyResult(fixture, 2, "external policy link");
        PolicyResult(fixture, 1, "explicit external link permitted", explicitPolicy: policy);
        File.Delete(policy);
        File.WriteAllText(external, """{"version":1}""");
        File.CreateSymbolicLink(policy, external);
        PolicyResult(fixture, 2, "external empty policy cannot erase FF007");
        PolicyResult(fixture, 0, "explicit empty external policy is intentional", explicitPolicy: policy);
        File.Delete(policy);
        File.WriteAllText(external, ProtectedPolicy);

        var middle = Path.Combine(repository, "middle.json");
        File.CreateSymbolicLink(middle, "owned.json");
        File.CreateSymbolicLink(policy, "middle.json");
        PolicyResult(fixture, 1, "internal policy link chain");
        File.Delete(middle);
        File.CreateSymbolicLink(middle, external);
        PolicyResult(fixture, 2, "external policy link chain");
        File.Delete(external);
        File.CreateSymbolicLink(external, internalFile);
        PolicyResult(fixture, 2, "outside link hop cannot regain implicit ownership by returning inside");
        PolicyResult(fixture, 1, "explicit outside-then-inside link chain", explicitPolicy: policy);
        File.Delete(external);
        File.WriteAllText(external, ProtectedPolicy);
        File.Delete(middle);
        PolicyResult(fixture, 2, "dangling chain");
        File.CreateSymbolicLink(middle, "feedfence.json");
        PolicyResult(fixture, 2, "cyclic policy link chain");
        File.Delete(policy);
        File.Delete(middle);
        File.CreateSymbolicLink(policy, "missing.json");
        PolicyResult(fixture, 2, "dangling policy link");
        PolicyResult(fixture, 2, "dangling explicit policy", explicitPolicy: policy);
        var git = Path.Combine(repository, ".git");
        Directory.Delete(git);
        PolicyResult(fixture, 2, "Git-free dangling policy");
        Directory.CreateDirectory(git);
        File.Delete(policy);
        File.CreateSymbolicLink(policy, "owned");
        Directory.CreateDirectory(Path.Combine(repository, "owned"));
        PolicyResult(fixture, 2, "policy link to directory");
        File.Delete(policy);
        PolicyDirectoryLinks(fixture, repository, policy, external);
    }

    private void PolicyDirectoryLinks(RestoredProject fixture, string repository, string policy, string external)
    {
        var owned = Path.Combine(repository, "owned");
        Directory.CreateDirectory(owned);
        File.WriteAllText(Path.Combine(owned, "policy.json"), ProtectedPolicy);
        var outside = Path.Combine(Path.GetDirectoryName(repository)!, "repository-sibling");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "policy.json"), ProtectedPolicy);
        var parentLink = Path.Combine(repository, "policy-parent");
        LinkDirectory(parentLink, owned);
        var chain = Path.Combine(repository, "policy-parent-chain");
        LinkDirectory(chain, parentLink);
        try
        {
            File.CreateSymbolicLink(policy, Path.GetRelativePath(repository, Path.Combine(chain, "policy.json")));
            PolicyResult(fixture, 1, "internal linked policy ancestors and chain");
            File.Delete(policy);
            var outsideLink = Path.Combine(repository, "outside-parent");
            LinkDirectory(outsideLink, outside);
            File.CreateSymbolicLink(policy, Path.Combine(outsideLink, "policy.json"));
            PolicyResult(fixture, 2, "linked outside ancestor with sibling prefix");
            File.Delete(policy);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP: file-link policy ancestor controls require Windows link privilege.");
        }
        File.WriteAllText(policy, ProtectedPolicy);
        var alias = Path.Combine(Path.GetDirectoryName(repository)!, "repository-alias");
        LinkDirectory(alias, repository);
        PolicyResult(fixture, 1, "repository alias with physical ancestry", Path.Combine(alias, Path.GetRelativePath(repository, fixture.Path)));
        var projectAlias = Path.Combine(Path.GetDirectoryName(repository)!, "project-alias");
        LinkDirectory(projectAlias, fixture.Root);
        PolicyResult(fixture, 1, "linked target ancestor retains actual repository policy", Path.Combine(projectAlias, "Project.csproj"));
        File.Delete(policy);
        PolicyResult(fixture, 1, "explicit linked external directory", explicitPolicy: Path.Combine(outside, "policy.json"));
        PolicyResult(fixture, 1, "explicit ordinary external file", explicitPolicy: external);
    }

    private void PolicyReadDenial(RestoredProject fixture, string path, bool isDirectory)
    {
        var before = PolicySnapshot(Path.GetDirectoryName(Path.GetFullPath(Path.Combine(fixture.Root, "../../..")))!);
        if (OperatingSystem.IsWindows())
        {
            FileSystemInfo entry = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
            var original = isDirectory ? ((DirectoryInfo)entry).GetAccessControl() : (FileSystemSecurity)((FileInfo)entry).GetAccessControl();
            var descriptor = original.GetSecurityDescriptorBinaryForm();
            var changed = isDirectory ? new DirectorySecurity() : (FileSystemSecurity)new FileSecurity();
            changed.SetSecurityDescriptorBinaryForm(descriptor, AccessControlSections.Access);
            changed.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, isDirectory ? FileSystemRights.ListDirectory : FileSystemRights.ReadData, AccessControlType.Deny));
            try
            {
                if (isDirectory) ((DirectoryInfo)entry).SetAccessControl((DirectorySecurity)changed);
                else ((FileInfo)entry).SetAccessControl((FileSecurity)changed);
                Denied();
            }
            finally
            {
                original.SetSecurityDescriptorBinaryForm(descriptor, AccessControlSections.Access);
                if (isDirectory) ((DirectoryInfo)entry).SetAccessControl((DirectorySecurity)original);
                else ((FileInfo)entry).SetAccessControl((FileSecurity)original);
            }
        }
        else
        {
            var original = File.GetUnixFileMode(path);
            try
            {
                File.SetUnixFileMode(path, original & ~(UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead));
                Denied();
            }
            finally { File.SetUnixFileMode(path, original); }
        }
        Equal(before, PolicySnapshot(Path.GetDirectoryName(Path.GetFullPath(Path.Combine(fixture.Root, "../../..")))!), "permission-denied analysis preserves inputs");

        void Denied()
        {
            var denied = false;
            try
            {
                if (isDirectory) _ = Directory.EnumerateFileSystemEntries(path).ToArray();
                else { using var stream = File.OpenRead(path); }
            }
            catch (UnauthorizedAccessException) { denied = true; }
            Equal(true, denied, "permission fixture must establish actual access denial");
            PolicyResult(fixture, 2, isDirectory ? "unreadable discovery directory" : "unreadable implicit policy", snapshot: false);
            if (!isDirectory) PolicyResult(fixture, 2, "unreadable explicit policy", explicitPolicy: path, snapshot: false);
        }
    }

    private void PolicyResult(RestoredProject fixture, int expected, string name, string? target = null, string? explicitPolicy = null, bool explicitConfig = true, bool snapshot = true)
    {
        var before = snapshot ? PolicySnapshot(Path.GetDirectoryName(Path.GetFullPath(Path.Combine(fixture.Root, "../../..")))!) : "";
        foreach (var format in new[] { "text", "json", "sarif" })
        {
            var arguments = new List<string> { "check", target ?? fixture.Path, "--format", format };
            if (explicitConfig) arguments.AddRange(["--config", fixture.Config]);
            if (explicitPolicy is not null) arguments.AddRange(["--policy", explicitPolicy]);
            var result = _tool.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? External("dotnet", [_tool, .. arguments], fixture.Root)
                : External(_tool, arguments, fixture.Root);
            Expect(result, expected, name + "/" + format);
            if (expected == 2)
            {
                Equal("", result.StandardOutput, name + " has no partial report");
                Contains(result.StandardError, "Analysis error:", name + " reports analysis failure");
                Equal(false, result.StandardError.Contains(_root, StringComparison.OrdinalIgnoreCase), "failure does not expose local path");
            }
            else
            {
                Equal("", result.StandardError, name + " has clean stderr");
                var hasProtectedFinding = result.StandardOutput.Contains("FF007", StringComparison.Ordinal);
                if (format != "text")
                {
                    using var report = JsonDocument.Parse(result.StandardOutput);
                    hasProtectedFinding = format == "json"
                        ? report.RootElement.GetProperty("diagnostics").EnumerateArray().Any(item => item.GetProperty("code").GetString() == "FF007")
                        : report.RootElement.GetProperty("runs")[0].GetProperty("results").EnumerateArray().Any(item => item.GetProperty("ruleId").GetString() == "FF007");
                }
                Equal(expected == 1, hasProtectedFinding, name + " retains protected-package enforcement");
            }
        }
        if (snapshot) Equal(before, PolicySnapshot(Path.GetDirectoryName(Path.GetFullPath(Path.Combine(fixture.Root, "../../..")))!), name + " is read-only");
    }

    private static string PolicySnapshot(string root)
    {
        var entries = new List<string>();
        Visit(root);
        return string.Join("\n", entries.Order(StringComparer.Ordinal));
        void Visit(string directory)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var info = new FileInfo(path);
                if (info.LinkTarget is { } link) entries.Add(path + ":link:" + link);
                else if (Directory.Exists(path)) { entries.Add(path + ":directory"); Visit(path); }
                else entries.Add(path + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
            }
        }
    }
}
