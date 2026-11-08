using System.Text;
using System.Text.Json;

namespace KeelMatrix.FeedFence.CliTests;

internal sealed partial class Fixture : IDisposable
{
    private void RunIdentityOutputCases()
    {
        var valid = CreateCase("identity-controls", ["Feed.Valid"], [], ["public"]);
        string[] unsafeArguments = [
            "https://user:password@example.invalid/index.json?token=topsecret",
            "Company.Secret\r\nFF003: forged diagnostic",
            "Company.Secret\tquoted",
            "C:\\Users\\secret\\feed.config",
            "Company.Secret\u2028separator",
            "Company.Secret\u202Fformat",
            "\"quoted identity\""
        ];
        foreach (var unsafeArgument in unsafeArguments)
        {
            var invalidOption = Run("check", valid.Project, "--unknown=" + unsafeArgument);
            AssertEqual(2, invalidOption.ExitCode, "invalid CLI option exit code");
            AssertNotContains(invalidOption.Output, unsafeArgument);
        }

        var rawArgument = unsafeArguments[0];
        var invalidOptionOutput = Run("check", valid.Project, "--unknown=" + rawArgument).Output;
        AssertNotContains(invalidOptionOutput, "password");
        AssertNotContains(invalidOptionOutput, "topsecret");

        var invalidIdentity = CreateCase("credential-shaped-package-id", [],
            ["<packageSourceMapping><packageSource key=\"public\"><package pattern=\"Other.*\" /></packageSource></packageSourceMapping>"],
            ["public"]);
        WritePackageProject(invalidIdentity.Project, [rawArgument]);
        var identity = rawArgument + "/1.0.0";
        var identityAssets = new Dictionary<string, object>
        {
            ["version"] = 3,
            ["targets"] = new Dictionary<string, object> { ["net8.0"] = new Dictionary<string, object> { [identity] = new { type = "package" } } },
            ["libraries"] = new Dictionary<string, object> { [identity] = new { type = "package" } },
            ["projectFileDependencyGroups"] = new Dictionary<string, object> { ["net8.0"] = new[] { rawArgument + " >= 1.0.0" } },
            ["project"] = new Dictionary<string, object> { ["frameworks"] = new Dictionary<string, object> { ["net8.0"] = new Dictionary<string, object> { ["dependencies"] = new Dictionary<string, object> { [rawArgument] = new { target = "Package", version = "[1.0.0, )" } } } } }
        };
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(invalidIdentity.Project)!, "obj", "project.assets.json"), JsonSerializer.Serialize(identityAssets), Encoding.UTF8);
        foreach (var format in ReportFormats)
        {
            var result = Run("check", invalidIdentity.Project, "--config", invalidIdentity.Config!, "--format", format);
            AssertEqual(2, result.ExitCode, $"credential-shaped package identity {format} exit code");
            AssertNotContains(result.Output, rawArgument);
            AssertNotContains(result.Output, "password");
            AssertNotContains(result.Output, "topsecret");
        }

        var longPackageId = new string('a', 300);
        var combiningMarkPackageId = "A\u0301.B";
        var validPackageIds = new[] { "日本語パッケージ", "A.B-C_D", longPackageId, combiningMarkPackageId };
        var validIdentities = CreateCase(
            "valid-package-identities",
            validPackageIds,
            ["<packageSourceMapping><packageSource key=\"public\"><package pattern=\"*\" /></packageSource></packageSourceMapping>"],
            ["public"]);
        WritePackageProject(validIdentities.Project, validPackageIds);
        var validIdentityPolicy = Path.Combine(validIdentities.Root, "valid-identities-policy.json");
        File.WriteAllText(
            validIdentityPolicy,
            JsonSerializer.Serialize(new
            {
                version = 1,
                sourceTrust = new Dictionary<string, string> { ["public"] = "public" },
                protectedPackages = ValidIdentityPolicyPatterns
            }),
            Encoding.UTF8);

        foreach (var format in ReportFormats)
        {
            var result = Run("check", validIdentities.Project, "--config", validIdentities.Config!, "--policy", validIdentityPolicy, "--format", format);
            AssertEqual(1, result.ExitCode, $"valid package identity {format} exit code");
            var reportText = result.StandardOutput;
            if (format == "json")
            {
                using var report = JsonDocument.Parse(result.StandardOutput);
                reportText = string.Join(
                    "\n",
                    report.RootElement.GetProperty("diagnostics").EnumerateArray().Select(diagnostic =>
                        diagnostic.GetProperty("message").GetString() + " " +
                        (diagnostic.TryGetProperty("packageId", out var packageId) ? packageId.GetString() : string.Empty)));
            }
            else if (format == "sarif")
            {
                using var report = JsonDocument.Parse(result.StandardOutput);
                reportText = string.Join(
                    "\n",
                    report.RootElement.GetProperty("runs")[0].GetProperty("results").EnumerateArray()
                        .Select(diagnostic => diagnostic.GetProperty("message").GetProperty("text").GetString()));
            }

            foreach (var packageId in validPackageIds)
            {
                AssertContains(reportText, packageId);
            }
        }
    }
}
