using System.Text.Json;

namespace KeelMatrix.FeedFence.CliTests;

internal sealed partial class Fixture : IDisposable
{
    private void RunReportContractCases()
    {
        var empty = CreateCase("report-empty", [], [], ["public"]);
        var emptyJson = Run("check", empty.Project, "--config", empty.Config!, "--format", "json");
        AssertEqual(0, emptyJson.ExitCode, "empty JSON report exit code");
        AssertJsonReportContract(emptyJson.StandardOutput, null);

        var violation = CreateCase("report-violation", ["Feed.ReportViolation"],
            ["<packageSourceMapping><packageSource key=\"public\"><package pattern=\"Other.*\" /></packageSource></packageSourceMapping>"],
            ["public"]);
        var violationJson = Run("check", violation.Project, "--config", violation.Config!, "--format", "json");
        AssertEqual(1, violationJson.ExitCode, "violation JSON report exit code");
        AssertJsonReportContract(violationJson.StandardOutput, "FF003");

        var mismatch = CreateCase("report-mismatch", ["Feed.ReportMismatch"],
            ["<packageSourceMapping><packageSource key=\"missing\"><package pattern=\"Feed.ReportMismatch\" /></packageSource></packageSourceMapping>"],
            ["public"]);
        var mismatchJson = Run("check", mismatch.Project, "--config", mismatch.Config!, "--format", "json");
        AssertEqual(1, mismatchJson.ExitCode, "mismatch JSON report exit code");
        AssertJsonReportContract(mismatchJson.StandardOutput, "FF004");

        var informational = CreateCase("report-informational", ["Feed.ReportInfo"],
            ["<packageSourceMapping><packageSource key=\"public\"><package pattern=\"*\" /></packageSource></packageSourceMapping>"],
            ["public"]);
        var informationalJson = Run("check", informational.Project, "--config", informational.Config!, "--format", "json");
        AssertEqual(0, informationalJson.ExitCode, "informational JSON report exit code");
        AssertJsonReportContract(informationalJson.StandardOutput, "FF008");
    }

    private static void AssertJsonReportContract(string json, string? expectedDiagnosticCode)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        foreach (var property in new[] { "schemaVersion", "format", "tool", "exitCode", "summary", "sources", "diagnostics" })
        {
            if (!root.TryGetProperty(property, out _)) throw new InvalidOperationException($"JSON report is missing '{property}'.");
        }

        var summary = root.GetProperty("summary");
        foreach (var property in new[] { "resolvedPackageCount", "activeSourceCount", "packageSourceMappingEnabled", "deterministicMappingCount", "diagnosticCount" })
        {
            if (!summary.TryGetProperty(property, out _)) throw new InvalidOperationException($"JSON summary is missing '{property}'.");
        }

        foreach (var diagnostic in root.GetProperty("diagnostics").EnumerateArray())
        {
            foreach (var property in new[] { "code", "severity", "message" })
            {
                if (!diagnostic.TryGetProperty(property, out _)) throw new InvalidOperationException($"JSON diagnostic is missing '{property}'.");
            }

            if (diagnostic.TryGetProperty("id", out _) || diagnostic.TryGetProperty("title", out _))
            {
                throw new InvalidOperationException("JSON diagnostic contains forbidden legacy fields.");
            }

            if (expectedDiagnosticCode is not null && diagnostic.GetProperty("code").GetString() == expectedDiagnosticCode)
            {
                var expectedSeverity = expectedDiagnosticCode == "FF008" ? "information" : "violation";
                AssertEqual(expectedSeverity, diagnostic.GetProperty("severity").GetString(), $"JSON {expectedDiagnosticCode} severity");
            }
        }

        var diagnostics = root.GetProperty("diagnostics").EnumerateArray().ToArray();
        AssertEqual(diagnostics.Length, summary.GetProperty("diagnosticCount").GetInt32(), "JSON diagnostic count");
        if (expectedDiagnosticCode is null && diagnostics.Length != 0)
        {
            throw new InvalidOperationException("Empty JSON report contains unexpected diagnostics.");
        }

        if (expectedDiagnosticCode is not null && !diagnostics.Any(item => item.GetProperty("code").GetString() == expectedDiagnosticCode))
        {
            throw new InvalidOperationException($"JSON report is missing diagnostic '{expectedDiagnosticCode}'.");
        }
    }

    private TestCase RunHelpAndReports()
    {
        AssertProcess(0, "--help");
        var help = Run("--help");
        AssertContains(help.Output, "feedfence check [path] [options]");
        AssertContains(help.Output, "NuGet's normal package-ID validation");
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
        return specificity;
    }
}
