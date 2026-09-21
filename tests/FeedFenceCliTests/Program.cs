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
        AssertProcess(0, "--version");

        var specificity = CreateCase("specificity", ["Feed.Exact", "Feed.Prefix.Item", "Feed.Wildcard"],
            [
                "<packageSourceMapping><packageSource key=\"exact\"><package pattern=\"Feed.Exact\" /></packageSource><packageSource key=\"prefix\"><package pattern=\"Feed.Prefix.*\" /></packageSource><packageSource key=\"wildcard\"><package pattern=\"*\" /></packageSource></packageSourceMapping>",
            ],
            ["exact", "prefix", "wildcard"]);
        AssertProcess(0, "check", specificity.Project, "--config", specificity.Config);
        var specificityOutput = Run("check", specificity.Project, "--config", specificity.Config).Output;
        AssertContains(specificityOutput, "No restore-source policy violations found.");
        AssertNotContains(specificityOutput, "FF002");
        AssertContains(specificityOutput, "FF008");

        var json = Run("check", specificity.Project, "--config", specificity.Config, "--format", "json");
        AssertEqual(0, json.ExitCode, "JSON exit code");
        AssertNotContains(json.StandardError, "noise");
        AssertEqual(string.Empty, json.StandardError, "JSON stderr");
        using (var jsonDocument = JsonDocument.Parse(json.StandardOutput))
        {
            AssertEqual(1, jsonDocument.RootElement.GetProperty("schemaVersion").GetInt32(), "JSON schema version");
            AssertEqual("json", jsonDocument.RootElement.GetProperty("format").GetString(), "JSON format");
            AssertEqual("FF008", jsonDocument.RootElement.GetProperty("diagnostics").EnumerateArray().Single(diagnostic => diagnostic.GetProperty("code").GetString() == "FF008").GetProperty("code").GetString(), "JSON diagnostic identity");
        }

        var jsonRepeat = Run("check", specificity.Project, "--config", specificity.Config, "--format", "json");
        AssertEqual(json.StandardOutput, jsonRepeat.StandardOutput, "JSON byte determinism");

        var sarif = Run("check", specificity.Project, "--config", specificity.Config, "--format", "sarif");
        AssertEqual(0, sarif.ExitCode, "SARIF exit code");
        AssertEqual(string.Empty, sarif.StandardError, "SARIF stderr");
        using (var sarifDocument = JsonDocument.Parse(sarif.StandardOutput))
        {
            var driver = sarifDocument.RootElement.GetProperty("runs")[0].GetProperty("tool").GetProperty("driver");
            AssertEqual("FF008", driver.GetProperty("rules").EnumerateArray().Single(rule => rule.GetProperty("id").GetString() == "FF008").GetProperty("id").GetString(), "SARIF diagnostic rule");
            AssertEqual("FF008", sarifDocument.RootElement.GetProperty("runs")[0].GetProperty("results").EnumerateArray().Single(result => result.GetProperty("ruleId").GetString() == "FF008").GetProperty("ruleId").GetString(), "SARIF result identity");
        }

        var sarifRepeat = Run("check", specificity.Project, "--config", specificity.Config, "--format", "sarif");
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
        var telemetryJson = FeedFenceTelemetry.SerializePayload(telemetryResult);
        using (var telemetryDocument = JsonDocument.Parse(telemetryJson))
        {
            var properties = telemetryDocument.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            AssertEqual(
                "feedFenceVersion,dotnetMajorVersion,osFamily,resolvedPackageCountBucket,activeSourceCountBucket,packageSourceMappingEnabled,resultClass,diagnosticCountBucket",
                string.Join(',', properties),
                "telemetry payload shape");
        }

        AssertEqual(null, FeedFenceTelemetry.CreatePayload(new AnalysisResult(0, 0, 1, false, 0, false, [], [])), "no-package telemetry activation");

        var equal = CreateCase("equal", ["Feed.Equal"],
            [
                "<packageSourceMapping><packageSource key=\"left\"><package pattern=\"Feed.Equal\" /></packageSource><packageSource key=\"right\"><package pattern=\"Feed.Equal\" /></packageSource></packageSourceMapping>",
            ],
            ["left", "right"]);
        AssertProcess(1, "check", equal.Project, "--config", equal.Config);
        AssertContains(Run("check", equal.Project, "--config", equal.Config).Output, "FF002");

        var noMapping = CreateCase("nomapping", ["Feed.One"], [], ["one", "two"]);
        AssertProcess(1, "check", noMapping.Project, "--config", noMapping.Config);
        AssertContains(Run("check", noMapping.Project, "--config", noMapping.Config).Output, "FF001");

        var unmapped = CreateCase("unmapped", ["Feed.Unmapped"],
            ["<packageSourceMapping><packageSource key=\"one\"><package pattern=\"Other.*\" /></packageSource></packageSourceMapping>"], ["one"]);
        AssertProcess(1, "check", unmapped.Project, "--config", unmapped.Config);
        AssertContains(Run("check", unmapped.Project, "--config", unmapped.Config).Output, "FF003");

        var casing = CreateCase("casing", ["Feed.Casing"],
            ["<packageSourceMapping><packageSource key=\"One\"><package pattern=\"Feed.Casing\" /></packageSource></packageSourceMapping>"], ["one"]);
        AssertProcess(1, "check", casing.Project, "--config", casing.Config);
        AssertContains(Run("check", casing.Project, "--config", casing.Config).Output, "FF004");

        var inherited = CreateCase("inherited", ["Feed.Inherited"], [], ["one"]);
        var outsideConfig = Path.Combine(_root, "outside.config");
        File.Copy(inherited.Config, outsideConfig);
        AssertProcess(0, "check", inherited.Project, "--config", outsideConfig);
        AssertProcess(1, "check", inherited.Project, "--config", outsideConfig, "--strict");
        AssertContains(Run("check", inherited.Project, "--config", outsideConfig).Output, "external inherited configuration");

        var insecure = CreateCase("insecure", ["Feed.Insecure"], [], ["one"], "http://user:secret@example.invalid/v3/index.json?token=private");
        var insecureOutput = Run("check", insecure.Project, "--config", insecure.Config);
        AssertEqual(1, insecureOutput.ExitCode, "insecure source exit code");
        AssertContains(insecureOutput.Output, "FF006");
        AssertNotContains(insecureOutput.Output, "secret");
        AssertNotContains(insecureOutput.Output, "token=private");

        var protectedCase = CreateCase("protected", ["Company.Internal"], [], ["public"]);
        var policy = Path.Combine(protectedCase.Root, "feedfence.json");
        File.WriteAllText(policy, "{\"version\":1,\"sourceTrust\":{\"public\":\"public\"},\"privatePackages\":[\"Company.*\"]}", Encoding.UTF8);
        AssertProcess(1, "check", protectedCase.Project, "--config", protectedCase.Config, "--policy", policy);
        var exceptionPolicy = Path.Combine(protectedCase.Root, "exception.json");
        File.WriteAllText(exceptionPolicy, "{\"version\":1,\"sourceTrust\":{\"public\":\"public\"},\"privatePackages\":[\"Company.*\"],\"exceptions\":[{\"code\":\"FF007\",\"packagePattern\":\"Company.Internal\",\"sourceKey\":\"public\",\"reason\":\"synthetic test exception\"}]}", Encoding.UTF8);
        AssertProcess(0, "check", protectedCase.Project, "--config", protectedCase.Config, "--policy", exceptionPolicy);

        var malformedPolicy = Path.Combine(_root, "malformed-policy.json");
        File.WriteAllText(malformedPolicy, "{\"exceptions\":[{\"code\":\"FF005\"}]}", Encoding.UTF8);
        var malformed = Run("check", specificity.Project, "--config", specificity.Config, "--policy", malformedPolicy);
        AssertEqual(2, malformed.ExitCode, "malformed policy exit code");
        AssertEqual(string.Empty, malformed.StandardOutput, "malformed policy stdout");
        AssertContains(malformed.StandardError, "Analysis error");
        AssertNotContains(malformed.Output, specificity.Root);

        var missing = Path.Combine(_root, "missing");
        Directory.CreateDirectory(missing);
        File.WriteAllText(Path.Combine(missing, "Project.csproj"), "<Project />", Encoding.UTF8);
        var missingResult = Run("check", Path.Combine(missing, "Project.csproj"), "--config", specificity.Config);
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
        var disagreementResult = Run("check", specificity.Project, "--config", specificity.Config);
        AssertEqual(2, disagreementResult.ExitCode, "assets/lock disagreement exit code");
        AssertContains(disagreementResult.Output, "project.assets.json and packages.lock.json disagree");

        var dtdConfig = Path.Combine(_root, "dtd.config");
        File.WriteAllText(dtdConfig, "<?xml version=\"1.0\"?><!DOCTYPE configuration [<!ENTITY secret SYSTEM \"file:///not-used\">]><configuration><packageSources><add key=\"one\" value=\"&secret;\" /></packageSources></configuration>", Encoding.UTF8);
        var dtdResult = Run("check", specificity.Project, "--config", dtdConfig);
        AssertEqual(2, dtdResult.ExitCode, "DTD configuration exit code");
        AssertNotContains(dtdResult.Output, "not-used");
    }

    private TestCase CreateCase(string name, IReadOnlyList<string> packages, IReadOnlyList<string> mappings, IReadOnlyList<string> sourceKeys, string? firstSource = null)
    {
        var root = Path.Combine(_root, name);
        var projectDirectory = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(projectDirectory, "obj"));
        var project = Path.Combine(projectDirectory, "Project.csproj");
        File.WriteAllText(project, "<Project />", Encoding.UTF8);
        var assets = new
        {
            version = 3,
            targets = new { net8 = new { } },
            libraries = packages.ToDictionary(package => package + "/1.0.0", package => new { type = "package" })
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
        return new(root, project, config);
    }

    private ProcessResult Run(params string[] args)
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

    private void AssertProcess(int expectedExitCode, params string[] args)
    {
        var result = Run(args);
        AssertEqual(expectedExitCode, result.ExitCode, string.Join(' ', args));
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

internal sealed record TestCase(string Root, string Project, string Config);
internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string Output => StandardOutput + StandardError;
}
